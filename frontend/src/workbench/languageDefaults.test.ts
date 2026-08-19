import { describe, expect, it } from 'vitest'
import type { LanguageManifest } from '../api/types'
import {
  jsharpDefaultFileName,
  jsharpDefaultSource,
  jsharpDisplayName,
  languageForWorkbench,
} from './languageDefaults'

describe('workbench language defaults', () => {
  it('activates the independent J# workspace only for a catalog-provided language', () => {
    const manifest: LanguageManifest = {
      id: 'jsharp',
      displayName: 'J#',
      monacoLanguageId: 'plaintext',
      extensions: ['.java'],
      defaultFileName: 'Main.java',
      defaultSource: 'class Main {}',
      defaultToolchainId: 'vjc-jsharp20',
      capabilities: ['diagnostics'],
      legacyAliases: ['j#'],
    }

    const language = languageForWorkbench(manifest)

    expect(language).toEqual({
      ...manifest,
      displayName: 'J#',
      monacoLanguageId: 'jsharp',
      extensions: ['.jsl'],
      defaultFileName: 'Program.jsl',
      defaultSource: jsharpDefaultSource,
    })
    expect(jsharpDefaultFileName).toBe('Program.jsl')
    expect(jsharpDisplayName).toBe('J#')
    expect(jsharpDefaultSource).toContain('public static void main(String[] args)')
    expect(jsharpDefaultSource).toContain('Hello from J#')
    expect(manifest).toMatchObject({ defaultFileName: 'Main.java', defaultSource: 'class Main {}' })
  })

  it('does not add or rewrite any language not supplied as J# by the catalog', () => {
    const language: LanguageManifest = {
      id: 'csharp',
      displayName: 'C#',
      monacoLanguageId: 'csharp',
      extensions: ['.cs'],
      defaultFileName: 'Program.cs',
      defaultSource: 'Console.WriteLine();',
      defaultToolchainId: 'roslyn-stable',
      capabilities: [],
      legacyAliases: ['cs'],
    }

    expect(languageForWorkbench(language)).toBe(language)
  })
})
