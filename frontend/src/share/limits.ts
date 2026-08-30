import { ShareUrlError } from './errors'
import type { UrlCodecLimits, UrlLengthDisposition } from './types'

export const defaultUrlCodecLimits: Readonly<UrlCodecLimits> = Object.freeze({
  softUrlLength: 8_192,
  hardUrlLength: 32_768,
  maxUncompressedBytes: 1_048_576,
  maxFiles: 32,
  maxFileBytes: 256 * 1_024,
  maxTotalFileBytes: 1_048_576,
  maxPathLength: 240,
  maxSelectionIdLength: 256,
  maxLegacyEncodedLength: 32_768,
  maxLegacyDecodedCharacters: 1_048_576,
  workerTimeoutMs: 2_000,
})

export const resolveUrlCodecLimits = (limits?: UrlCodecLimits): UrlCodecLimits => limits ?? { ...defaultUrlCodecLimits }

export const classifyUrlLength = (urlLength: number, limits: UrlCodecLimits): UrlLengthDisposition => {
  if (!Number.isSafeInteger(urlLength) || urlLength < 0) {
    throw new ShareUrlError('url-too-long', 'The URL length is invalid.')
  }
  if (urlLength > limits.hardUrlLength) {
    throw new ShareUrlError('url-too-long', `The URL is ${urlLength} characters, exceeding the ${limits.hardUrlLength} character hard limit.`)
  }
  return urlLength <= limits.softUrlLength ? 'live' : 'explicit-warning'
}
