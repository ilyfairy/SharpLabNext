import { afterEach, describe, expect, it, vi } from 'vitest'
import { decodeBase64Url, encodeBase64Url } from './base64'
import { goldenState } from './goldenFixture'
import { decodeV3, encodeV3 } from './v3'

const deflateGolden =
  '#v3:U0xOMwEBABjmAAAAqQAAANgEwY4wiWDRVc29DoJAEATgVzFbacIRDg0mUNpamFhYIMWJC565H3N7GIjx3V2stJxvJ7MveEK5TsBACS3dVHhAArHlFDyZyQmK6mKQMRCjwygzEbBj8JzvOgpFdj5HjlcfuSG4YrQbRjEWGz7ZeQ0NKvru8EPIs7zItlKmkkV1LIfg-6Bs2hIL8Xb9S00CXKr_LIGBtOsXx4ki2ursdt6RN5iego641w6Xm3xVQdO8Pw'

const rawGolden =
  '#v3:U0xOMwEAABjmAAAA5gAAANgEwY4wiWDReyJ2IjozLCJsIjoiY3NoYXJwIiwidGMiOiJyb3NseW4tc3RhYmxlIiwicnMiOiJuZXQxMC1yZWYiLCJvIjoiaml0LWFzbSIsInJ0IjoiZG90bmV0LTEwLWxpbnV4LXg2NCIsIm0iOiJyZWxlYXNlIiwicnYiOiIyMDI2MDcxMS4xIiwiYWYiOiJQcm9ncmFtLmNzIiwic28iOlsiUHJvZ3JhbS5jcyJdLCJmIjpbWyJQcm9ncmFtLmNzIiwidXNpbmcgU3lzdGVtO1xuQ29uc29sZS5Xcml0ZUxpbmUoNDIpOyJdXX0'

describe('URL v3 without Web Crypto', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('encodes and restores the existing wire format without SubtleCrypto', async () => {
    vi.stubGlobal('crypto', {})

    const encoded = await encodeV3(goldenState, { profile: 'share' })

    expect(encoded.fragment).toBe(deflateGolden)
    await expect(decodeV3(encoded.fragment)).resolves.toEqual({
      sourceFormat: 'v3',
      state: goldenState,
      codecId: 1,
    })
  })

  it('rejects payload tampering without SubtleCrypto', async () => {
    vi.stubGlobal('crypto', {})
    const envelope = decodeBase64Url(rawGolden.slice('#v3:'.length))
    envelope[envelope.length - 1] = (envelope[envelope.length - 1] ?? 0) ^ 1

    await expect(decodeV3(`#v3:${encodeBase64Url(envelope)}`)).rejects.toMatchObject({
      code: 'digest-mismatch',
    })
  })
})
