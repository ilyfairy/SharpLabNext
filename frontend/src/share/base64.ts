import { ShareUrlError } from './errors'

const bytesToBinary = (bytes: Uint8Array): string => {
  const chunkSize = 0x8000
  let result = ''
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    const chunk = bytes.subarray(offset, Math.min(offset + chunkSize, bytes.length))
    result += String.fromCharCode(...chunk)
  }
  return result
}

const binaryToBytes = (value: string): Uint8Array => {
  const result = new Uint8Array(value.length)
  for (let index = 0; index < value.length; index += 1) result[index] = value.charCodeAt(index)
  return result
}

export const encodeBase64Url = (bytes: Uint8Array): string =>
  btoa(bytesToBinary(bytes)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/u, '')

export const decodeBase64Url = (value: string): Uint8Array => {
  if (!/^[A-Za-z0-9_-]+$/u.test(value) || value.length % 4 === 1) {
    throw new ShareUrlError('invalid-base64url', 'The v3 payload is not valid unpadded base64url.')
  }

  const standard = value.replaceAll('-', '+').replaceAll('_', '/')
  const padded = standard.padEnd(standard.length + ((4 - (standard.length % 4)) % 4), '=')
  try {
    return binaryToBytes(atob(padded))
  } catch (error) {
    throw new ShareUrlError('invalid-base64url', 'The v3 payload is not valid base64url.', {
      cause: error,
    })
  }
}

export const assertStandardBase64 = (value: string): void => {
  if (value.length === 0 || value.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,3}$/u.test(value)) {
    throw new ShareUrlError('invalid-base64', 'The legacy payload is not valid standard Base64.')
  }
}
