import { deflateSync, inflateSync } from 'fflate'
import { decodeBase64Url, encodeBase64Url } from './base64'
import { validateDeflateRaw } from './deflate'
import { bytesEqual, sha256Prefix } from './digest'
import { ShareUrlError } from './errors'
import { classifyUrlLength, resolveUrlCodecLimits } from './limits'
import type {
  DecodedV3Share,
  EncodedV3Share,
  EncodeV3Options,
  ShareWorkspaceState,
  UrlCodecId,
  UrlCodecLimits,
} from './types'
import { decodeCanonicalPayload, encodeCanonicalPayload } from './workspace'

const magic = new Uint8Array([0x53, 0x4c, 0x4e, 0x33])
const envelopeRevision = 1
const fixedHeaderLength = 24

interface Candidate {
  codecId: UrlCodecId
  compressionLevel: 6 | 9 | null
  priority: number
  envelope: Uint8Array
  fragment: string
  encodedPayloadLength: number
}

const writeEnvelope = (
  payload: Uint8Array,
  encodedPayload: Uint8Array,
  codecId: UrlCodecId,
  digest: Uint8Array,
): Uint8Array => {
  const envelope = new Uint8Array(fixedHeaderLength + encodedPayload.length)
  envelope.set(magic, 0)
  envelope[4] = envelopeRevision
  envelope[5] = codecId
  envelope[6] = 0
  envelope[7] = fixedHeaderLength
  const view = new DataView(envelope.buffer)
  view.setUint32(8, payload.length, true)
  view.setUint32(12, encodedPayload.length, true)
  envelope.set(digest, 16)
  envelope.set(encodedPayload, fixedHeaderLength)
  return envelope
}

const toCandidate = (
  payload: Uint8Array,
  encodedPayload: Uint8Array,
  codecId: UrlCodecId,
  compressionLevel: 6 | 9 | null,
  priority: number,
  digest: Uint8Array,
): Candidate => {
  const envelope = writeEnvelope(payload, encodedPayload, codecId, digest)
  return {
    codecId,
    compressionLevel,
    priority,
    envelope,
    fragment: `#v3:${encodeBase64Url(envelope)}`,
    encodedPayloadLength: encodedPayload.length,
  }
}

export const createV3Envelope = async (
  payload: Uint8Array,
  codecId: UrlCodecId,
  compressionLevel: 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 = 6,
): Promise<Uint8Array> => {
  if (payload.length > 0xffff_ffff) {
    throw new ShareUrlError('payload-too-large', 'The v3 payload cannot fit in the envelope.')
  }
  const encodedPayload = codecId === 0 ? payload : deflateSync(payload, { level: compressionLevel })
  return writeEnvelope(payload, encodedPayload, codecId, await sha256Prefix(payload))
}

export const encodeV3 = async (
  state: ShareWorkspaceState,
  options: EncodeV3Options = {},
): Promise<EncodedV3Share> => {
  const limits = resolveUrlCodecLimits(options.limits)
  const payload = encodeCanonicalPayload(state, limits)
  const digest = await sha256Prefix(payload)
  const candidates = [toCandidate(payload, payload, 0, null, 0, digest)]
  candidates.push(toCandidate(payload, deflateSync(payload, { level: 6 }), 1, 6, 1, digest))
  if ((options.profile ?? 'live') === 'share') {
    candidates.push(toCandidate(payload, deflateSync(payload, { level: 9 }), 1, 9, 2, digest))
  }

  candidates.sort(
    (left, right) => left.fragment.length - right.fragment.length || left.priority - right.priority,
  )
  const selected = candidates[0]
  if (!selected) throw new ShareUrlError('worker-failed', 'No URL codec candidate was produced.')

  const urlLength = (options.baseUrl?.length ?? 0) + selected.fragment.length
  return {
    fragment: selected.fragment,
    codecId: selected.codecId,
    compressionLevel: selected.compressionLevel,
    payloadLength: payload.length,
    encodedPayloadLength: selected.encodedPayloadLength,
    envelopeLength: selected.envelope.length,
    urlLength,
    lengthDisposition: classifyUrlLength(urlLength, limits),
  }
}

const normalizeV3Fragment = (fragment: string, limits: UrlCodecLimits): string => {
  const normalized = fragment.startsWith('#') ? fragment.slice(1) : fragment
  if (!normalized.startsWith('v3:')) {
    throw new ShareUrlError('invalid-fragment', "A v3 fragment must start with '#v3:'.")
  }
  if (fragment.length > limits.hardUrlLength) {
    throw new ShareUrlError('url-too-long', 'The v3 fragment exceeds the URL hard limit.')
  }
  return normalized.slice(3)
}

const validateExtensions = (envelope: Uint8Array, headerLength: number): void => {
  let offset = fixedHeaderLength
  while (offset < headerLength) {
    if (offset + 2 > headerLength) {
      throw new ShareUrlError('invalid-header', 'The v3 extension header is truncated.')
    }
    const type = envelope[offset] ?? 0
    const length = envelope[offset + 1] ?? 0
    offset += 2
    if (offset + length > headerLength) {
      throw new ShareUrlError('invalid-header', 'A v3 extension exceeds the declared header.')
    }
    if ((type & 0x80) !== 0) {
      throw new ShareUrlError(
        'unsupported-critical-extension',
        `The v3 envelope uses unsupported critical extension ${type}.`,
      )
    }
    offset += length
  }
}

const decodeEnvelopePayload = async (
  envelope: Uint8Array,
  limits: UrlCodecLimits,
): Promise<{ payload: Uint8Array; codecId: UrlCodecId }> => {
  if (envelope.length < fixedHeaderLength) {
    throw new ShareUrlError('envelope-truncated', 'The v3 envelope is shorter than 24 bytes.')
  }
  if (!bytesEqual(envelope.subarray(0, 4), magic)) {
    throw new ShareUrlError('unknown-magic', 'The URL does not contain an SLN3 envelope.')
  }
  if (envelope[4] !== envelopeRevision) {
    throw new ShareUrlError(
      'unsupported-revision',
      `Envelope revision ${envelope[4]} is not supported.`,
    )
  }
  const codec = envelope[5]
  if (codec !== 0 && codec !== 1) {
    throw new ShareUrlError('unsupported-codec', `URL codec ${codec} is not supported.`)
  }
  if (envelope[6] !== 0) {
    throw new ShareUrlError('invalid-flags', 'Envelope revision 1 requires flags to be zero.')
  }

  const headerLength = envelope[7] ?? 0
  if (headerLength < fixedHeaderLength || headerLength > envelope.length) {
    throw new ShareUrlError('invalid-header', 'The v3 envelope header length is invalid.')
  }
  validateExtensions(envelope, headerLength)

  const view = new DataView(envelope.buffer, envelope.byteOffset, envelope.byteLength)
  const expectedLength = view.getUint32(8, true)
  const encodedLength = view.getUint32(12, true)
  if (expectedLength > limits.maxUncompressedBytes) {
    throw new ShareUrlError(
      'payload-too-large',
      `The v3 payload declares ${expectedLength} bytes, exceeding the configured limit.`,
    )
  }
  if (encodedLength !== envelope.length - headerLength) {
    throw new ShareUrlError(
      'length-mismatch',
      'The v3 encoded payload length does not match its envelope.',
    )
  }

  const encodedPayload = envelope.subarray(headerLength)
  let payload: Uint8Array
  if (codec === 0) {
    if (encodedLength !== expectedLength) {
      throw new ShareUrlError('length-mismatch', 'A raw v3 payload must match its declared length.')
    }
    payload = encodedPayload.slice()
  } else {
    validateDeflateRaw(encodedPayload, expectedLength)
    try {
      payload = inflateSync(encodedPayload, { out: new Uint8Array(expectedLength) })
    } catch (error) {
      throw new ShareUrlError('decompression-failed', 'The raw DEFLATE payload is invalid.', {
        cause: error,
      })
    }
    if (payload.length !== expectedLength) {
      throw new ShareUrlError(
        'length-mismatch',
        'The decoded payload length does not match its envelope.',
      )
    }
  }

  const expectedDigest = envelope.subarray(16, 24)
  const actualDigest = await sha256Prefix(payload)
  if (!bytesEqual(expectedDigest, actualDigest)) {
    throw new ShareUrlError('digest-mismatch', 'The v3 payload digest does not match its envelope.')
  }
  return { payload, codecId: codec }
}

export const decodeV3 = async (
  fragment: string,
  providedLimits?: UrlCodecLimits,
): Promise<DecodedV3Share> => {
  const limits = resolveUrlCodecLimits(providedLimits)
  const base64 = normalizeV3Fragment(fragment, limits)
  const envelope = decodeBase64Url(base64)
  const { payload, codecId } = await decodeEnvelopePayload(envelope, limits)
  return {
    sourceFormat: 'v3',
    state: decodeCanonicalPayload(payload, limits),
    codecId,
  }
}
