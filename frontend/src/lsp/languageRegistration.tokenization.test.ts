import { beforeAll, describe, expect, it, vi } from 'vitest'

vi.mock('../editor/monacoCore', async () => import('monaco-editor/esm/vs/editor/editor.api.js'))

import * as monaco from '../editor/monacoCore'
import { registerSourceLanguages } from './languageRegistration'

describe('Monaco lexical fallback', () => {
  beforeAll(() => {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: vi.fn((query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    })
    registerSourceLanguages()
  })

  it('classifies decompiled attributes without treating their constructors as methods', () => {
    const lines = monaco.editor.tokenize(
      ['[CompilerGenerated]', '[module: RefSafetyRules(11)]', 'static void Main() {}'].join('\n'),
      'csharp',
    )

    expect(tokenTypeAt(lines[0] ?? [], '[CompilerGenerated]', 'CompilerGenerated')).toBe(
      'type.identifier.cs',
    )
    expect(tokenTypeAt(lines[1] ?? [], '[module: RefSafetyRules(11)]', 'module')).toBe('keyword.cs')
    expect(tokenTypeAt(lines[1] ?? [], '[module: RefSafetyRules(11)]', 'RefSafetyRules')).toBe(
      'type.identifier.cs',
    )
    expect(tokenTypeAt(lines[2] ?? [], 'static void Main() {}', 'Main')).toBe('function.cs')
  })

  it('keeps adjacent and qualified attribute names in attribute context', () => {
    const line = '[System.Diagnostics.DebuggerStepThrough][CompilerGenerated]'
    const [tokens = []] = monaco.editor.tokenize(line, 'csharp')

    expect(tokenTypeAt(tokens, line, 'System')).toBe('namespace.cs')
    expect(tokenTypeAt(tokens, line, 'Diagnostics')).toBe('namespace.cs')
    expect(tokenTypeAt(tokens, line, 'DebuggerStepThrough')).toBe('type.identifier.cs')
    expect(tokenTypeAt(tokens, line, 'CompilerGenerated')).toBe('type.identifier.cs')
  })

  it('classifies modern JavaScript module declarations as keywords', () => {
    const line = 'export default function register(runtime) { const assembly = runtime.JSIL; }'
    const [tokens = []] = monaco.editor.tokenize(line, 'javascript')

    expect(tokenTypeAt(tokens, line, 'export')).toBe('keyword.javascript')
    expect(tokenTypeAt(tokens, line, 'default')).toBe('keyword.javascript')
    expect(tokenTypeAt(tokens, line, 'function')).toBe('keyword.javascript')
    expect(tokenTypeAt(tokens, line, 'const')).toBe('keyword.javascript')
  })

  it('colors only the simple name in a standalone IL hover assembly identity', () => {
    const assemblyLine =
      '[System.Console, Version=11.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a]'
    const arrayLine = 'int32[0...]'
    const qualifiedTypeLine = '[System.Runtime]System.Object'
    const lines = monaco.editor.tokenize(
      [assemblyLine, qualifiedTypeLine, '[out] int32', arrayLine, '[0]'].join('\n'),
      'il',
    )

    expect(tokenTypeAt(lines[0] ?? [], assemblyLine, 'System.Console')).toBe('macro.il')
    expect(tokenTypeAt(lines[0] ?? [], assemblyLine, 'Version')).not.toBe('macro.il')
    expect(tokenTypeAt(lines[1] ?? [], qualifiedTypeLine, 'System.Runtime')).toBe('macro.il')
    expect(tokenTypeAt(lines[2] ?? [], '[out] int32', 'out')).not.toBe('macro.il')
    expect(tokenTypeAt(lines[3] ?? [], arrayLine, '0...')).not.toBe('macro.il')
    expect(lines[4]?.some((token) => token.type === 'macro.il')).toBe(false)
  })
})

function tokenTypeAt(tokens: monaco.Token[], line: string, text: string): string | undefined {
  const offset = line.indexOf(text)
  return [...tokens].reverse().find((token) => token.offset <= offset)?.type
}
