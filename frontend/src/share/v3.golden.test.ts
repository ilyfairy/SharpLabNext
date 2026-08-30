import { describe, expect, it } from 'vitest'
import { encodeBase64Url } from './base64'
import { goldenState } from './goldenFixture'
import { defaultUrlCodecLimits } from './limits'
import { createV3Envelope, decodeV3, encodeV3 } from './v3'
import { encodeCanonicalPayload } from './workspace'

const rawGolden =
  '#v3:U0xOMwEAABjmAAAA5gAAANgEwY4wiWDReyJ2IjozLCJsIjoiY3NoYXJwIiwidGMiOiJyb3NseW4tc3RhYmxlIiwicnMiOiJuZXQxMC1yZWYiLCJvIjoiaml0LWFzbSIsInJ0IjoiZG90bmV0LTEwLWxpbnV4LXg2NCIsIm0iOiJyZWxlYXNlIiwicnYiOiIyMDI2MDcxMS4xIiwiYWYiOiJQcm9ncmFtLmNzIiwic28iOlsiUHJvZ3JhbS5jcyJdLCJmIjpbWyJQcm9ncmFtLmNzIiwidXNpbmcgU3lzdGVtO1xuQ29uc29sZS5Xcml0ZUxpbmUoNDIpOyJdXX0'
const deflateGolden =
  '#v3:U0xOMwEBABjmAAAAqQAAANgEwY4wiWDRVc29DoJAEATgVzFbacIRDg0mUNpamFhYIMWJC565H3N7GIjx3V2stJxvJ7MveEK5TsBACS3dVHhAArHlFDyZyQmK6mKQMRCjwygzEbBj8JzvOgpFdj5HjlcfuSG4YrQbRjEWGz7ZeQ0NKvru8EPIs7zItlKmkkV1LIfg-6Bs2hIL8Xb9S00CXKr_LIGBtOsXx4ki2ursdt6RN5iego641w6Xm3xVQdO8Pw'
const nodeZlibGolden =
  '#v3:U0xOMwEBABjmAAAArgAAANgEwY4wiWDRVYwxa8MwEEb_SvimFCRjucaBy5g1Q6BDB9eD6pxdFVkKuktIKP3vxZ3a9b3H-8IN9GwQQRjlw5cLDHQEoWSJj2RF_XtkGBQBIbG62haeYJBB-AxqvSyrVhDOWROrdbWNIV3v9t61MFjWG0f28vu5gdDUTVfvnKscDPwEwqnkufilGgUGkkH9XzQYTKC-_59dJaR58_IQ5WX_lg45SY5cvZagfAyJt23ztMcwfP8A'

describe('URL v3 golden vectors', () => {
  it('decodes the fixed raw vector', async () => {
    await expect(decodeV3(rawGolden)).resolves.toEqual({
      sourceFormat: 'v3',
      state: goldenState,
      codecId: 0,
    })
  })

  it('decodes the fixed RFC 1951 raw DEFLATE vector', async () => {
    await expect(decodeV3(deflateGolden)).resolves.toEqual({
      sourceFormat: 'v3',
      state: goldenState,
      codecId: 1,
    })
  })

  it('decodes a fixed raw DEFLATE vector produced by Node zlib', async () => {
    await expect(decodeV3(nodeZlibGolden)).resolves.toEqual({
      sourceFormat: 'v3',
      state: goldenState,
      codecId: 1,
    })
  })

  it('selects the shortest final fragment and prefers level 6 on ties', async () => {
    const live = await encodeV3(goldenState, { profile: 'live' })
    const share = await encodeV3(goldenState, { profile: 'share' })

    expect(live.fragment).toBe(deflateGolden)
    expect(live.compressionLevel).toBe(6)
    expect(share.fragment).toBe(deflateGolden)
    expect(share.compressionLevel).toBe(6)
    expect(share.fragment.length).toBeLessThan(rawGolden.length)
  })

  it('is byte-for-byte deterministic', async () => {
    const first = await encodeV3(goldenState, { profile: 'share' })
    const second = await encodeV3(structuredClone(goldenState), {
      profile: 'share',
    })
    expect(second).toEqual(first)
  })

  it('decodes fflate streams produced at every RFC 1951 compression level', async () => {
    const payload = encodeCanonicalPayload(goldenState, {
      ...defaultUrlCodecLimits,
    })
    for (const level of [0, 1, 2, 3, 4, 5, 6, 7, 8, 9] as const) {
      const envelope = await createV3Envelope(payload, 1, level)
      await expect(decodeV3(`#v3:${encodeBase64Url(envelope)}`)).resolves.toMatchObject({
        state: goldenState,
        codecId: 1,
      })
    }
  })

  it('roundtrips CRLF, emoji, Unicode, and NUL code units without normalization', async () => {
    const state = structuredClone(goldenState)
    state.files[0] = {
      path: 'Unicode.cs',
      text: `/* CRLF */\r\nConsole.WriteLine("你好 👩🏽‍💻 ${String.fromCharCode(0)}");\n`,
    }
    state.activeFile = 'Unicode.cs'
    state.sourceOrder = ['Unicode.cs']
    const encoded = await encodeV3(state, { profile: 'share' })
    await expect(decodeV3(encoded.fragment)).resolves.toMatchObject({ state })
  })

  it('accounts for the full URL when applying soft limits', async () => {
    const encoded = await encodeV3(goldenState, {
      baseUrl: 'https://example.test/lab',
      limits: {
        softUrlLength: deflateGolden.length,
        hardUrlLength: 32_768,
        maxUncompressedBytes: 1_048_576,
        maxFiles: 32,
        maxFileBytes: 262_144,
        maxTotalFileBytes: 1_048_576,
        maxPathLength: 240,
        maxSelectionIdLength: 256,
        maxLegacyEncodedLength: 32_768,
        maxLegacyDecodedCharacters: 1_048_576,
        workerTimeoutMs: 2_000,
      },
    })
    expect(encoded.lengthDisposition).toBe('explicit-warning')
  })
})
