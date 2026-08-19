export type ShareUrlErrorCode =
  | 'invalid-fragment'
  | 'url-too-long'
  | 'invalid-base64'
  | 'invalid-base64url'
  | 'envelope-truncated'
  | 'unknown-magic'
  | 'unsupported-revision'
  | 'unsupported-codec'
  | 'invalid-flags'
  | 'invalid-header'
  | 'unsupported-critical-extension'
  | 'length-mismatch'
  | 'payload-too-large'
  | 'decompression-failed'
  | 'digest-mismatch'
  | 'invalid-payload'
  | 'invalid-workspace'
  | 'legacy-invalid'
  | 'legacy-too-large'
  | 'worker-timeout'
  | 'worker-failed'

export class ShareUrlError extends Error {
  readonly code: ShareUrlErrorCode

  constructor(code: ShareUrlErrorCode, message: string, options?: ErrorOptions) {
    super(message, options)
    this.name = 'ShareUrlError'
    this.code = code
  }
}

export const asShareUrlError = (error: unknown): ShareUrlError => {
  if (error instanceof ShareUrlError) return error

  const message = error instanceof Error ? error.message : 'Unknown URL codec failure.'
  return new ShareUrlError('worker-failed', message, {
    cause: error,
  })
}
