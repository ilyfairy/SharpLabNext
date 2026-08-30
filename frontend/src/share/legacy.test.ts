import LZString from 'lz-string'
import { describe, expect, it } from 'vitest'
import { importSharpLabLegacy } from './legacy'
import { legacyDictionaries, legacyPrecompress, legacyPredecompress } from './legacyPrecompressor'
import { defaultUrlCodecLimits } from './limits'

const createV2 = (options: string, code: string, languageId: string): string => `#v2:${LZString.compressToBase64(`${options}|${legacyPrecompress(code, languageId)}`)}`

describe('SharpLab legacy URL import', () => {
  it('imports the fixed host-swap sample', async () => {
    const result = await importSharpLabLegacy('#v2:EYLgtghglgdgNAFxFANgHwQUwM4IAQDGA9gCaZA=')
    expect(result).toMatchObject({
      sourceFormat: 'sharplab-v2',
      workspace: {
        languageId: 'csharp',
        files: [{ path: 'Program.cs', text: 'test code' }],
      },
      requestedLegacyOptions: {
        branchId: 'main',
        languageKey: 'cs',
        languageId: 'csharp',
        targetKey: 'il',
        outputId: 'il',
        buildMode: 'release',
      },
      resolvedSelection: null,
    })
  })

  it('imports all v2 language and target keys', async () => {
    const cases = [
      ['cs', 'csharp', 'asm', 'jit-asm'],
      ['vb', 'visual-basic', 'verify', 'il-verify'],
      ['fs', 'fsharp', 'run', 'run'],
      ['php', 'php', 'cs', 'decompiled-csharp'],
      ['il', 'il', 'run-il', 'run-il'],
    ] as const

    for (const [languageKey, languageId, targetKey, outputId] of cases) {
      const result = await importSharpLabLegacy(createV2(`l:${languageKey},t:${targetKey},d:+`, 'source @ @', languageId))
      expect(result.workspace.files[0]?.text).toBe('source @ @')
      expect(result.requestedLegacyOptions).toMatchObject({
        languageKey,
        languageId,
        targetKey,
        outputId,
        buildMode: 'debug',
      })
    }
  })

  it('restores PHP URLs with the canonical index.php workspace path', async () => {
    const source = '<?php echo "Hello";'
    const result = await importSharpLabLegacy(createV2('l:php,t:cs,d:+', source, 'php'))

    expect(result.workspace).toEqual({
      languageId: 'php',
      activeFile: 'index.php',
      sourceOrder: ['index.php'],
      files: [{ path: 'index.php', text: source }],
    })
  })

  it('reproduces the complete C# and IL dictionaries and @ escaping', () => {
    for (const languageId of ['csharp', 'il'] as const) {
      const dictionary = legacyDictionaries[languageId]
      const code = `${dictionary.join('\n')}\n@literal`
      expect(legacyPredecompress(legacyPrecompress(code, languageId), languageId)).toBe(code)
    }
  })

  it('preserves the exact CRLF C# Run help token', () => {
    const runHelp = legacyDictionaries.csharp[20]
    expect(runHelp).toContain('\r\n    • value.Inspect()')
    expect(runHelp).toContain('value2, …')
    expect(legacyPrecompress(runHelp, 'csharp')).toBe('@20')
    expect(legacyPredecompress('@20', 'csharp')).toBe(runHelp)
  })

  it('does not replace a dictionary entry followed by a digit', () => {
    expect(legacyPrecompress('void Func13() {}', 'csharp')).toBe('@4 Func13() {}')
  })

  it('retains the VB/F# replace-first historical behavior', () => {
    expect(legacyPrecompress('@ one @ two', 'visual-basic')).toBe('@@ one @ two')
    expect(legacyPredecompress('@@ one @@ two', 'visual-basic')).toBe('@ one @@ two')
  })

  it('imports v1 branch, language, target, and release flags', async () => {
    const code = 'let answer = 42'
    const compressed = LZString.compressToBase64(code)
    const result = await importSharpLabLegacy(`#b:legacy/f:fs>asmr/${compressed}`)
    expect(result.workspace.files[0]?.text).toBe(code)
    expect(result.requestedLegacyOptions).toEqual({
      branchId: 'legacy',
      languageKey: 'fs',
      languageId: 'fsharp',
      targetKey: 'asm',
      outputId: 'jit-asm',
      buildMode: 'release',
    })
  })

  it('uses v1 C#/decompiled-C# debug defaults', async () => {
    const result = await importSharpLabLegacy(LZString.compressToBase64('class C {}'))
    expect(result.requestedLegacyOptions).toMatchObject({
      languageId: 'csharp',
      outputId: 'decompiled-csharp',
      buildMode: 'debug',
    })
  })

  it("accepts SharpLab's empty v1 hash shape", async () => {
    const result = await importSharpLabLegacy('#/')
    expect(result.workspace).toMatchObject({
      languageId: 'csharp',
      files: [{ path: 'Program.cs', text: '' }],
    })
  })

  it('decodes URI-escaped legacy hashes', async () => {
    const fragment = createV2('l:cs,t:il', 'code', 'csharp')
    const encoded = `#${encodeURIComponent(fragment.slice(1))}`
    await expect(importSharpLabLegacy(encoded)).resolves.toMatchObject({
      workspace: { files: [{ text: 'code' }] },
    })
  })

  it('enforces encoded and decoded legacy limits', async () => {
    await expect(
      importSharpLabLegacy('#v2:AAAA', {
        ...defaultUrlCodecLimits,
        maxLegacyEncodedLength: 3,
      }),
    ).rejects.toMatchObject({ code: 'legacy-too-large' })

    const fragment = createV2('', '123456', 'csharp')
    await expect(
      importSharpLabLegacy(fragment, {
        ...defaultUrlCodecLimits,
        maxLegacyDecodedCharacters: 4,
      }),
    ).rejects.toMatchObject({ code: 'legacy-too-large' })
  })

  it('rejects malformed v2 Base64 and unknown dictionary tokens', async () => {
    await expect(importSharpLabLegacy('#v2:not-base64')).rejects.toMatchObject({
      code: 'invalid-base64',
    })
    const badToken = `#v2:${LZString.compressToBase64('l:cs|@999')}`
    await expect(importSharpLabLegacy(badToken)).rejects.toMatchObject({
      code: 'legacy-invalid',
    })
  })
})
