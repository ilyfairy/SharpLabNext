import fc from 'fast-check'
import { describe, it } from 'vitest'
import { encodeBase64Url } from './base64'
import { defaultUrlCodecLimits } from './limits'
import type { ShareWorkspaceState } from './types'
import { createV3Envelope, decodeV3, encodeV3 } from './v3'
import { encodeCanonicalPayload } from './workspace'

const stateArbitrary = fc
  .record({
    languageId: fc.constantFrom('csharp', 'visual-basic', 'fsharp', 'php', 'il'),
    outputId: fc.constantFrom('ast', 'il', 'jit-asm', 'run'),
    buildMode: fc.constantFrom('debug' as const, 'release' as const),
    texts: fc.array(fc.string({ maxLength: 300 }), {
      minLength: 1,
      maxLength: 5,
    }),
  })
  .map(({ languageId, outputId, buildMode, texts }): ShareWorkspaceState => {
    const extension = languageId === 'csharp' ? 'cs' : languageId === 'visual-basic' ? 'vb' : languageId === 'fsharp' ? 'fs' : languageId === 'php' ? 'php' : 'il'
    const files = texts.map((text, index) => ({
      path: `src/File${index}.${extension}`,
      text,
    }))
    return {
      languageId,
      toolchainId: `toolchain-${languageId}`,
      referenceSetId: 'net10-ref',
      outputId,
      runtimeId: 'dotnet-10-linux-x64',
      buildMode,
      releaseVersion: 'property-test',
      activeFile: files[0]?.path ?? `src/File0.${extension}`,
      sourceOrder: files.map((file) => file.path),
      files,
    }
  })

describe('URL v3 property roundtrips', () => {
  it('roundtrips Unicode, multiple languages, files, and source order', async () => {
    await fc.assert(
      fc.asyncProperty(stateArbitrary, fc.constantFrom('live' as const, 'share' as const), async (state, profile) => {
        const encoded = await encodeV3(state, { profile })
        const decoded = await decodeV3(encoded.fragment)
        return JSON.stringify(decoded.state) === JSON.stringify(state)
      }),
      { numRuns: 75 },
    )
  })

  it('accepts valid raw DEFLATE streams across levels and workspace shapes', async () => {
    await fc.assert(
      fc.asyncProperty(stateArbitrary, fc.integer({ min: 0, max: 9 }), async (state, level) => {
        const payload = encodeCanonicalPayload(state, {
          ...defaultUrlCodecLimits,
        })
        const envelope = await createV3Envelope(payload, 1, level as 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9)
        const decoded = await decodeV3(`#v3:${encodeBase64Url(envelope)}`)
        return JSON.stringify(decoded.state) === JSON.stringify(state)
      }),
      { numRuns: 50 },
    )
  })
})
