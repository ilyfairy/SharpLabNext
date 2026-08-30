import { deflateSync } from 'fflate'
import { describe, expect, it } from 'vitest'
import { decodeBase64Url, encodeBase64Url } from './base64'
import { goldenState } from './goldenFixture'
import { defaultUrlCodecLimits } from './limits'
import { createV3Envelope, decodeV3, encodeV3 } from './v3'
import { encodeCanonicalPayload, stateToPayload } from './workspace'

const toFragment = (envelope: Uint8Array): string => `#v3:${encodeBase64Url(envelope)}`

const goldenEnvelope = async (codec: 0 | 1 = 0): Promise<Uint8Array> => createV3Envelope(encodeCanonicalPayload(goldenState, { ...defaultUrlCodecLimits }), codec)

const cloneWith = async (mutate: (envelope: Uint8Array) => void): Promise<string> => {
  const envelope = await goldenEnvelope()
  mutate(envelope)
  return toFragment(envelope)
}

describe('URL v3 validation', () => {
  it('rejects invalid base64url', async () => {
    await expect(decodeV3('#v3:not+base64')).rejects.toMatchObject({
      code: 'invalid-base64url',
    })
    await expect(decodeV3('#v3:a')).rejects.toMatchObject({
      code: 'invalid-base64url',
    })
    await expect(decodeV3('#v3:AA')).rejects.toMatchObject({
      code: 'envelope-truncated',
    })
  })

  it.each([
    ['unknown magic', (bytes: Uint8Array) => (bytes[0] = 0), 'unknown-magic'],
    ['unknown revision', (bytes: Uint8Array) => (bytes[4] = 2), 'unsupported-revision'],
    ['unknown standard codec', (bytes: Uint8Array) => (bytes[5] = 2), 'unsupported-codec'],
    ['private codec', (bytes: Uint8Array) => (bytes[5] = 128), 'unsupported-codec'],
    ['non-zero flags', (bytes: Uint8Array) => (bytes[6] = 1), 'invalid-flags'],
    ['short header', (bytes: Uint8Array) => (bytes[7] = 23), 'invalid-header'],
  ])('rejects %s', async (_name, mutate, code) => {
    await expect(decodeV3(await cloneWith(mutate))).rejects.toMatchObject({
      code,
    })
  })

  it('rejects unknown critical extensions and ignores non-critical extensions', async () => {
    const original = await goldenEnvelope()
    const extended = new Uint8Array(original.length + 3)
    extended.set(original.subarray(0, 24))
    extended[7] = 27
    extended[24] = 1
    extended[25] = 1
    extended[26] = 42
    extended.set(original.subarray(24), 27)

    await expect(decodeV3(toFragment(extended))).resolves.toMatchObject({
      state: goldenState,
    })
    extended[24] = 0x81
    await expect(decodeV3(toFragment(extended))).rejects.toMatchObject({
      code: 'unsupported-critical-extension',
    })
  })

  it('rejects declared encoded and raw lengths that do not match', async () => {
    const encodedMismatch = await cloneWith((bytes) => {
      new DataView(bytes.buffer).setUint32(12, 1, true)
    })
    await expect(decodeV3(encodedMismatch)).rejects.toMatchObject({
      code: 'length-mismatch',
    })

    const rawMismatch = await cloneWith((bytes) => {
      new DataView(bytes.buffer).setUint32(8, 1, true)
    })
    await expect(decodeV3(rawMismatch)).rejects.toMatchObject({
      code: 'length-mismatch',
    })
  })

  it('rejects digest corruption', async () => {
    const fragment = await cloneWith((bytes) => {
      bytes[16] = (bytes[16] ?? 0) ^ 0xff
    })
    await expect(decodeV3(fragment)).rejects.toMatchObject({
      code: 'digest-mismatch',
    })
  })

  it('bounds raw DEFLATE bombs using the declared output length', async () => {
    const large = new Uint8Array(900_000).fill(65)
    const envelope = await createV3Envelope(large, 1)
    expect(deflateSync(large).length).toBeLessThan(2_000)
    new DataView(envelope.buffer).setUint32(8, 100, true)
    await expect(decodeV3(toFragment(envelope))).rejects.toMatchObject({
      code: 'length-mismatch',
    })
  })

  it('rejects truncated and trailing raw DEFLATE streams', async () => {
    const envelope = await goldenEnvelope(1)
    const truncated = envelope.slice(0, -1)
    new DataView(truncated.buffer).setUint32(12, truncated.length - 24, true)
    await expect(decodeV3(toFragment(truncated))).rejects.toMatchObject({
      code: 'decompression-failed',
    })

    const trailing = new Uint8Array(envelope.length + 1)
    trailing.set(envelope)
    trailing[trailing.length - 1] = 0xff
    new DataView(trailing.buffer).setUint32(12, trailing.length - 24, true)
    await expect(decodeV3(toFragment(trailing))).rejects.toMatchObject({
      code: 'decompression-failed',
    })
  })

  it('rejects oversized declared payloads before inflation', async () => {
    const envelope = await goldenEnvelope(1)
    new DataView(envelope.buffer).setUint32(8, defaultUrlCodecLimits.maxUncompressedBytes + 1, true)
    await expect(decodeV3(toFragment(envelope))).rejects.toMatchObject({
      code: 'payload-too-large',
    })
  })

  it.each(['../Program.cs', '/Program.cs', 'C:/Program.cs', 'src\\Program.cs', 'src/\0.cs'])('rejects unsafe path %s', async (path) => {
    await expect(
      encodeV3({
        ...goldenState,
        activeFile: path,
        sourceOrder: [path],
        files: [{ path, text: 'code' }],
      }),
    ).rejects.toMatchObject({ code: 'invalid-workspace' })
  })

  it('applies the path limit to UTF-8 bytes as well as UTF-16 code units', async () => {
    const path = `${'界'.repeat(100)}.cs`
    await expect(
      encodeV3({
        ...goldenState,
        activeFile: path,
        sourceOrder: [path],
        files: [{ path, text: 'code' }],
      }),
    ).rejects.toMatchObject({ code: 'invalid-workspace' })
  })

  it('rejects duplicate paths and malformed source order', async () => {
    await expect(
      encodeV3({
        ...goldenState,
        sourceOrder: ['Program.cs', 'Program.cs'],
        files: [...goldenState.files, { path: 'Program.cs', text: 'duplicate' }],
      }),
    ).rejects.toMatchObject({ code: 'invalid-workspace' })
  })

  it('rejects too many files and files over 256 KiB after decompression', async () => {
    const payload = stateToPayload(goldenState, { ...defaultUrlCodecLimits })
    payload.so = Array.from({ length: 33 }, (_, index) => `F${index}.cs`)
    payload.f = payload.so.map((path) => [path, ''])
    payload.af = payload.so[0] ?? 'F0.cs'
    const tooMany = await createV3Envelope(new TextEncoder().encode(JSON.stringify(payload)), 1)
    await expect(decodeV3(toFragment(tooMany))).rejects.toMatchObject({
      code: 'invalid-workspace',
    })

    payload.so = ['Large.cs']
    payload.f = [['Large.cs', 'x'.repeat(defaultUrlCodecLimits.maxFileBytes + 1)]]
    payload.af = 'Large.cs'
    const tooLarge = await createV3Envelope(new TextEncoder().encode(JSON.stringify(payload)), 1)
    await expect(decodeV3(toFragment(tooLarge))).rejects.toMatchObject({
      code: 'invalid-workspace',
    })
  })

  it('rejects fragments over the hard URL limit', async () => {
    const fragment = `#v3:${'a'.repeat(defaultUrlCodecLimits.hardUrlLength)}`
    await expect(decodeV3(fragment)).rejects.toMatchObject({
      code: 'url-too-long',
    })
  })

  it('rejects standard Base64 characters in v3', async () => {
    const envelope = await goldenEnvelope()
    const standard = btoa(String.fromCharCode(...decodeBase64Url(encodeBase64Url(envelope))))
    expect(standard).toMatch(/[+/=]/u)
    await expect(decodeV3(`#v3:${standard}`)).rejects.toMatchObject({
      code: 'invalid-base64url',
    })

    const compressed = (await encodeV3(goldenState)).fragment
    expect(compressed).toContain('-')
    await expect(decodeV3(compressed.replace('-', '+'))).rejects.toMatchObject({
      code: 'invalid-base64url',
    })
  })
})
