import { beforeAll, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  defineTheme: vi.fn(),
  getLanguages: vi.fn(() => []),
  register: vi.fn(),
  setLanguageConfiguration: vi.fn(),
  setMonarchTokensProvider: vi.fn(),
}))

vi.mock('../editor/monacoCore', () => ({
  editor: { defineTheme: mocks.defineTheme },
  languages: {
    getLanguages: mocks.getLanguages,
    register: mocks.register,
    setLanguageConfiguration: mocks.setLanguageConfiguration,
    setMonarchTokensProvider: mocks.setMonarchTokensProvider,
  },
}))

vi.mock('monaco-editor/esm/vs/basic-languages/csharp/csharp.js', () => ({
  conf: {},
  language: { tokenizer: { root: [] } },
}))
vi.mock('monaco-editor/esm/vs/basic-languages/cpp/cpp.js', () => ({
  conf: {},
  language: {
    tokenizer: {
      root: [
        [/RAW_STRING/, 'string.raw'],
        [/[a-zA-Z_]\w*/, 'identifier'],
        [/^\s*#\s*include/, 'keyword.directive.include'],
        [/"/, 'string'],
      ],
    },
  },
}))
vi.mock('monaco-editor/esm/vs/basic-languages/fsharp/fsharp.js', () => ({
  conf: {},
  language: { tokenizer: { root: [] } },
}))
vi.mock('monaco-editor/esm/vs/basic-languages/java/java.js', () => ({
  conf: {},
  language: { tokenPostfix: '.java', tokenizer: { root: [] } },
}))
vi.mock('monaco-editor/esm/vs/basic-languages/php/php.js', () => ({
  conf: {},
  language: { tokenizer: { root: [] } },
}))
vi.mock('monaco-editor/esm/vs/basic-languages/vb/vb.js', () => ({
  conf: {},
  language: { tokenizer: { root: [] } },
}))

import {
  assemblyTokens,
  cppCliTokens,
  csharpVsTokens,
  editorLanguageId,
  ilTokens,
  javascriptTokens,
  registerSourceLanguages,
  sourceEditorTheme,
} from './languageRegistration'

describe('Monaco language registration', () => {
  beforeAll(() => registerSourceLanguages())

  it('registers MiniLang as a real Monaco language instead of plaintext', () => {
    expect(mocks.register).toHaveBeenCalledWith({
      id: 'minilang',
      extensions: ['.mini'],
      aliases: ['MiniLang', 'minilang'],
    })
    expect(mocks.setMonarchTokensProvider).toHaveBeenCalledWith(
      'minilang',
      expect.objectContaining({
        tokenizer: expect.objectContaining({ string: expect.any(Array) }),
      }),
    )
  })

  it('registers the built-in C++ tokenizer for C++/CLI source files', () => {
    expect(mocks.register).toHaveBeenCalledWith({
      id: 'cpp',
      extensions: ['.cpp', '.cc', '.cxx'],
      aliases: ['C++/CLI', 'cpp'],
    })
    expect(mocks.setMonarchTokensProvider).toHaveBeenCalledWith('cpp', cppCliTokens)
    expect(cppCliTokens).toEqual(
      expect.objectContaining({
        cppCliTypeKeywords: expect.arrayContaining(['int', 'void']),
        tokenizer: expect.objectContaining({ root: expect.any(Array) }),
      }),
    )
    expect(cppCliTokens.tokenizer.root?.[0]).toEqual([/RAW_STRING/, 'string.raw'])
    expect(cppCliTokens.tokenizer.root).toEqual(
      expect.arrayContaining([
        [/^\s*#\s*include/, 'keyword.directive.include'],
        [/"/, 'string'],
      ]),
    )
    expect(cppCliTokens.tokenizer.root).not.toContainEqual([/[a-zA-Z_]\w*/, 'identifier'])
  })

  it('registers JavaScript for JSIL result documents', () => {
    expect(mocks.register).toHaveBeenCalledWith({
      id: 'javascript',
      extensions: ['.js'],
      aliases: ['JavaScript', 'javascript'],
    })
    expect(mocks.setMonarchTokensProvider).toHaveBeenCalledWith('javascript', javascriptTokens)
    expect(javascriptTokens).toEqual(
      expect.objectContaining({
        keywords: expect.arrayContaining(['function', 'return', 'var']),
        tokenizer: expect.objectContaining({ root: expect.any(Array) }),
      }),
    )
  })

  it('keeps C# predefined aliases and current contextual keywords in the keyword palette', () => {
    expect(mocks.setMonarchTokensProvider).toHaveBeenCalledWith('csharp', csharpVsTokens)
    expect(csharpVsTokens.keywords).toEqual(
      expect.arrayContaining([
        'int',
        'double',
        'string',
        'void',
        'nint',
        'nuint',
        'record',
        'required',
        'scoped',
      ]),
    )
    expect(csharpVsTokens.tokenizer.root).toEqual(
      expect.arrayContaining([
        [
          /(class|struct|interface|enum|record)(\s+)([A-Za-z_]\w*)/,
          ['keyword', 'white', 'type.identifier'],
        ],
        [/[A-Z][A-Za-z0-9_]*(?=\s*(?:\.|<|\[|\?|\s+[A-Za-z_]\w*))/, 'type.identifier'],
      ]),
    )
    expect(csharpVsTokens.tokenizer.attribute).toEqual(
      expect.arrayContaining([
        [/(@csharpAttributeTargets)(\s*)(:)/, ['keyword', '', 'delimiter']],
        [/@?[A-Za-z_]\w*/, { token: 'type.identifier', switchTo: '@attributeAfterName' }],
      ]),
    )
    expect(mocks.defineTheme).toHaveBeenCalledWith(
      sourceEditorTheme,
      expect.objectContaining({
        rules: expect.arrayContaining([
          expect.objectContaining({ token: 'keyword', foreground: '0000FF' }),
          expect.objectContaining({ token: 'type', foreground: '2B91AF' }),
        ]),
      }),
    )
  })

  it('uses an accessible selected suggestion and VS symbol colors in Monaco', () => {
    expect(mocks.defineTheme).toHaveBeenCalledWith(
      sourceEditorTheme,
      expect.objectContaining({
        colors: expect.objectContaining({
          'editor.selectionBackground': '#ADD6FF',
          'editor.inactiveSelectionBackground': '#D8E2EC',
          'editorSuggestWidget.selectedBackground': '#0067C0',
          'editorSuggestWidget.selectedForeground': '#FFFFFF',
          'editorSuggestWidget.selectedIconForeground': '#FFFFFF',
          'symbolIcon.keywordForeground': '#0000FF',
          'symbolIcon.classForeground': '#2B91AF',
          'symbolIcon.methodForeground': '#795E26',
          'symbolIcon.variableForeground': '#001080',
        }),
      }),
    )
  })

  it.each([
    ['csharp', 'csharp'],
    ['visual-basic', 'visual-basic'],
    ['fsharp', 'fsharp'],
    ['gsharp', 'gsharp'],
    ['php', 'php'],
    ['cppcli', 'cpp'],
    ['jsharp', 'jsharp'],
    ['il', 'il'],
    ['asm', 'asm'],
    ['minilang', 'minilang'],
  ])('keeps the built-in %s model on its registered language', (languageId, expected) => {
    expect(editorLanguageId(languageId, 'plaintext')).toBe(expected)
  })

  it('registers J# as an independent .jsl language backed by the Java tokenizer', () => {
    expect(mocks.register).toHaveBeenCalledWith({
      id: 'jsharp',
      extensions: ['.jsl'],
      aliases: ['J#', 'jsharp'],
    })
    expect(mocks.setLanguageConfiguration).toHaveBeenCalledWith('jsharp', expect.anything())
    expect(mocks.setMonarchTokensProvider).toHaveBeenCalledWith(
      'jsharp',
      expect.objectContaining({
        tokenPostfix: '.jsharp',
        tokenizer: expect.objectContaining({ root: expect.any(Array) }),
      }),
    )
    expect(editorLanguageId('jsharp', 'plaintext')).toBe('jsharp')
    expect(editorLanguageId('jsharp', 'java')).toBe('jsharp')
  })

  it('registers assembly with method, label, opcode, register, and comment tokens', () => {
    expect(mocks.register).toHaveBeenCalledWith({
      id: 'asm',
      extensions: [],
      aliases: ['Assembly', 'asm'],
    })
    expect(mocks.setMonarchTokensProvider).toHaveBeenCalledWith('asm', assemblyTokens)
    expect(assemblyTokens.tokenizer.root).toEqual(
      expect.arrayContaining([
        [/^(\s*)(G_M\w+)(:)/, ['white', 'label', 'delimiter']],
        [/^(\s*)([A-Za-z][\w.]*)/, ['white', 'keyword']],
        [/[;#].*$/, 'comment'],
        [/\bG_M\w+\b/i, 'label'],
      ]),
    )
  })

  it('assigns the VS escape color to Roslyn semantic escape tokens', () => {
    expect(mocks.defineTheme).toHaveBeenCalledWith(
      sourceEditorTheme,
      expect.objectContaining({
        rules: expect.arrayContaining([
          expect.objectContaining({ token: 'stringEscapeCharacter', foreground: 'EE0000' }),
        ]),
      }),
    )
  })

  it('assigns distinct Monaco tokens to IL directives, assemblies, and types', () => {
    expect(mocks.setMonarchTokensProvider).toHaveBeenCalledWith('il', ilTokens)
    expect(ilTokens.tokenizer.root).toEqual(
      expect.arrayContaining([
        [/^(\s*)([A-Za-z_][\w.$]*)(:(?!:))/, ['white', 'label', 'delimiter']],
        [
          /^(\s*)(\.assembly)(\s+)(extern)(\s+)([^\s{]+)/,
          ['white', 'keyword', 'white', 'keyword', 'white', 'macro'],
        ],
        [/^(\s*)(\.assembly)(\s+)([^\s{]+)/, ['white', 'keyword', 'white', 'macro']],
        [
          /(\[)((?!(?:in|out|opt|retval)\])[^\]\r\n]+)(\])(?=[A-Za-z_'<])/,
          ['delimiter', 'macro', 'delimiter'],
        ],
        [
          /([A-Za-z_][\w.`]*(?:\.[A-Za-z_][\w.`]*)*)(::)(\.[A-Za-z_][\w.]*)/,
          ['type.identifier', 'delimiter', 'keyword'],
        ],
        [
          /([A-Za-z_][\w.`]*(?:\.[A-Za-z_][\w.`]*)*)(::)([A-Za-z_.$<>][\w.$<>`]*)/,
          ['type.identifier', 'delimiter', 'function'],
        ],
        [/\.[A-Za-z_][\w.]*/, 'keyword'],
      ]),
    )
    const rootRules = ilTokens.tokenizer.root as Array<unknown>
    const labelRule = rootRules[0] as [RegExp, unknown]
    expect(labelRule[0].test('IL_0000: ret')).toBe(true)
    expect(labelRule[0].test('System.Type::Method')).toBe(false)

    const assemblyScopeRule = rootRules.find(
      (rule) =>
        Array.isArray(rule) &&
        rule[0] instanceof RegExp &&
        String(rule[0]).includes('[^\\]\\r\\n]+') &&
        Array.isArray(rule[1]) &&
        rule[1].includes('macro'),
    ) as [RegExp, unknown]
    expect(assemblyScopeRule[0].test('[System.Runtime]System.Object')).toBe(true)
    expect(assemblyScopeRule[0].test('[out] int32')).toBe(false)
    expect(assemblyScopeRule[0].test('[out]int32')).toBe(false)
    expect(assemblyScopeRule[0].test('int32[0...]')).toBe(false)
    expect(ilTokens.keywords).toEqual(expect.arrayContaining(['extern', 'reqmin', 'algorithm']))
    expect(mocks.defineTheme).toHaveBeenCalledWith(
      sourceEditorTheme,
      expect.objectContaining({
        rules: expect.arrayContaining([
          expect.objectContaining({ token: 'keyword', foreground: '0000FF' }),
          expect.objectContaining({ token: 'macro', foreground: 'AF00DB' }),
          expect.objectContaining({ token: 'type.identifier', foreground: '2B91AF' }),
        ]),
      }),
    )
  })
})
