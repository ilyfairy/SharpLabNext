import { afterEach, describe, expect, it, vi } from 'vitest'
import { sha256Prefix } from './digest'

const toHex = (bytes: Uint8Array): string =>
  Array.from(bytes, (value) => value.toString(16).padStart(2, '0')).join('')

describe('share URL SHA-256', () => {
  afterEach(() => vi.unstubAllGlobals())

  it.each([
    ['', 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
    ['abc', 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
    [
      'abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq',
      '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1',
    ],
  ])('matches the standard digest for %j without Web Crypto', async (text, expected) => {
    vi.stubGlobal('crypto', {})

    const digest = await sha256Prefix(new TextEncoder().encode(text), 32)

    expect(toHex(digest)).toBe(expected)
  })

  it('uses Web Crypto when it is available', async () => {
    const expected = new Uint8Array(32).map((_, index) => index)
    const digest = vi.fn(async () => expected.buffer)
    vi.stubGlobal('crypto', { subtle: { digest } })

    await expect(sha256Prefix(new Uint8Array([1, 2, 3]), 8)).resolves.toEqual(expected.slice(0, 8))
    expect(digest).toHaveBeenCalledWith('SHA-256', expect.any(ArrayBuffer))
  })

  it('falls back when an exposed SubtleCrypto implementation rejects', async () => {
    const digest = vi.fn(async () => {
      throw new DOMException('Unavailable', 'NotSupportedError')
    })
    vi.stubGlobal('crypto', { subtle: { digest } })

    const actual = await sha256Prefix(new TextEncoder().encode('abc'), 8)

    expect(toHex(actual)).toBe('ba7816bf8f01cfea')
    expect(digest).toHaveBeenCalledOnce()
  })
})
