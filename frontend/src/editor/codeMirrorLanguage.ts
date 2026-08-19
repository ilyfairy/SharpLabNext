import { php } from '@codemirror/lang-php'
import {
  HighlightStyle,
  StreamLanguage,
  type StreamParser,
  type StringStream,
  syntaxHighlighting,
} from '@codemirror/language'
import { cpp, csharp, java } from '@codemirror/legacy-modes/mode/clike'
import { javascript } from '@codemirror/legacy-modes/mode/javascript'
import { fSharp } from '@codemirror/legacy-modes/mode/mllike'
import { vb } from '@codemirror/legacy-modes/mode/vb'
import { EditorState, type Extension } from '@codemirror/state'
import { EditorView } from '@codemirror/view'
import { tags } from '@lezer/highlight'
import { ilWordTokens } from './ilLanguageTokens'

const controlNames = new Set([
  'catch',
  'for',
  'foreach',
  'if',
  'lock',
  'match',
  'nameof',
  'sizeof',
  'switch',
  'typeof',
  'using',
  'while',
])

const csharpPredefinedTypeKeywords = new Set([
  'bool',
  'byte',
  'char',
  'decimal',
  'double',
  'float',
  'int',
  'long',
  'nint',
  'nuint',
  'object',
  'sbyte',
  'short',
  'string',
  'uint',
  'ulong',
  'ushort',
  'void',
])

let languages: ReadonlyMap<string, Extension> | null = null

export const visualStudioLightHighlightStyle = HighlightStyle.define([
  { tag: tags.namespace, color: '#000000' },
  { tag: [tags.typeName, tags.className], color: '#2B91AF' },
  { tag: tags.typeName, color: '#2B91AF' },
  {
    tag: [tags.function(tags.variableName), tags.function(tags.propertyName)],
    color: '#795E26',
  },
  { tag: tags.propertyName, color: '#001080' },
  { tag: [tags.variableName, tags.name], color: '#001080' },
  { tag: tags.labelName, color: '#AF00DB' },
  { tag: tags.macroName, color: '#AF00DB' },
  { tag: [tags.keyword, tags.modifier, tags.atom, tags.bool, tags.null], color: '#0000FF' },
  { tag: [tags.string, tags.character], color: '#A31515' },
  { tag: tags.escape, color: '#EE0000' },
  { tag: tags.number, color: '#098658' },
  { tag: tags.regexp, color: '#811F3F' },
  { tag: tags.operator, color: '#000000' },
  { tag: [tags.meta, tags.processingInstruction], color: '#AF00DB' },
  { tag: tags.comment, color: '#008000' },
  { tag: tags.invalid, color: '#A31515', textDecoration: 'underline wavy' },
])

export const visualStudioLightEditorTheme = EditorView.theme(
  {
    '&': { backgroundColor: '#ffffff', color: '#1f2328' },
    '.cm-content': { caretColor: '#1f6feb' },
    '.cm-cursor, .cm-dropCursor': { borderLeftColor: '#1f6feb' },
    '.cm-selectionBackground': {
      backgroundColor: '#d8e2ec',
    },
    '&.cm-focused .cm-selectionBackground': {
      backgroundColor: '#add6ff',
    },
    '.cm-content ::selection': {
      backgroundColor: 'transparent',
      color: 'inherit',
    },
    '.cm-activeLine': { backgroundColor: 'rgba(237, 242, 247, 0.58)', color: 'inherit' },
    '.cm-gutters': {
      backgroundColor: '#f7f8fa',
      color: '#68717d',
      borderRightColor: '#e4e8ed',
    },
    '.cm-activeLineGutter': { backgroundColor: '#edf2f7', color: '#1f2328' },
  },
  { dark: false },
)

export function codeMirrorLanguageExtension(languageId: string): Extension {
  languages ??= new Map<string, Extension>([
    ['csharp', StreamLanguage.define(withFunctionNames(csharp, 'csharp'))],
    ['jsharp', StreamLanguage.define(withFunctionNames(java, 'jsharp'))],
    ['cppcli', StreamLanguage.define(withCppCliTokens(cpp))],
    ['visual-basic', StreamLanguage.define(withFunctionNames(vb, 'visual-basic'))],
    ['fsharp', StreamLanguage.define(withFunctionNames(fSharp, 'fsharp'))],
    ['php', php()],
    ['javascript', StreamLanguage.define(javascript)],
    ['asm', StreamLanguage.define(createSimpleParser('asm'))],
    ['il', StreamLanguage.define(createSimpleParser('il'))],
    ['gsharp', StreamLanguage.define(createSimpleParser('gsharp'))],
    ['minilang', StreamLanguage.define(createSimpleParser('minilang'))],
  ])
  return languages.get(languageId) ?? []
}

export function codeMirrorSyntaxExtensions(languageId: string): Extension[] {
  return [
    codeMirrorLanguageExtension(languageId),
    syntaxHighlighting(visualStudioLightHighlightStyle),
    visualStudioLightEditorTheme,
  ]
}

export function codeMirrorReadOnlyExtensions(languageId: string): Extension[] {
  return [
    ...codeMirrorSyntaxExtensions(languageId),
    EditorState.readOnly.of(true),
    EditorView.editable.of(false),
  ]
}

export function semanticTokenCssClass(tokenType: string): string {
  switch (tokenType.toLowerCase()) {
    case 'namespace':
      return 'namespace'
    case 'type':
    case 'class':
    case 'struct':
    case 'interface':
    case 'enum':
    case 'delegate':
    case 'typeparameter':
      return 'type'
    case 'method':
    case 'function':
    case 'extensionmethod':
      return 'method'
    case 'property':
      return 'property'
    case 'field':
      return 'field'
    case 'event':
      return 'event'
    case 'enummember':
      return 'enum-member'
    case 'parameter':
      return 'parameter'
    case 'variable':
    case 'local':
      return 'variable'
    case 'label':
      return 'label'
    case 'macro':
      return 'macro'
    case 'keyword':
    case 'modifier':
      return 'keyword'
    case 'string':
      return 'string'
    case 'stringescapecharacter':
    case 'escape':
      return 'escape'
    case 'number':
      return 'number'
    case 'regexp':
    case 'regex':
      return 'regexp'
    case 'operator':
      return 'operator'
    case 'comment':
      return 'comment'
    default:
      return 'identifier'
  }
}

function withFunctionNames(
  parser: StreamParser<unknown>,
  languageId: 'csharp' | 'visual-basic' | 'fsharp' | 'jsharp',
): StreamParser<unknown> {
  return {
    ...parser,
    token(stream, state) {
      const style = parser.token(stream, state)
      const identifier = stream.current()
      const before = stream.string.slice(0, stream.start)
      const after = stream.string.slice(stream.pos)
      if (isCompilerGeneratedMethodPart(identifier, before, after)) {
        return 'variableName.function'
      }
      if (!/^[A-Za-z_][\w']*$/.test(identifier) || controlNames.has(identifier.toLowerCase())) {
        return style
      }

      if (languageId === 'csharp') {
        if (csharpPredefinedTypeKeywords.has(identifier)) return 'keyword'
        const attributeStyle = csharpAttributeStyle(identifier, before, after)
        if (attributeStyle) return attributeStyle
        if (isCsharpNamespace(identifier, before, after)) return 'namespace'
        if (isCsharpTypeReference(identifier, before, after)) return 'typeName'
        if (identifier === 'value') return 'variableName'
      }
      if (isNonIdentifierStyle(style)) return style

      if (/\bnamespace\s+$/i.test(before)) return 'namespace'
      if (
        /\b(?:class|struct|interface|enum|delegate|record|module|type|data|object)\s+$/i.test(
          before,
        )
      ) {
        return 'typeName'
      }
      if (
        /^\s*(?:<[^>{}]*>\s*)?\(/.test(after) ||
        /\b(?:Sub|Function|func|fn)\s+$/i.test(before) ||
        /\b(?:let|member)\s+(?:(?:inline|rec|private|internal|public|static)\s+)*(?:[A-Za-z_][\w']*\.)?$/i.test(
          before,
        )
      ) {
        return 'variableName.function'
      }
      return style
    },
  }
}

const cppCliKeywords = new Set([
  'array',
  'delegate',
  'event',
  'finally',
  'gcnew',
  'generic',
  'initonly',
  'interface',
  'interior_ptr',
  'literal',
  'pin_ptr',
  'property',
  'ref',
  'safe_cast',
  'sealed',
  'value',
  'where',
])

function withCppCliTokens(parser: StreamParser<unknown>): StreamParser<unknown> {
  return {
    ...parser,
    token(stream, state) {
      const style = parser.token(stream, state)
      const text = stream.current()
      const before = stream.string.slice(0, stream.start)
      const after = stream.string.slice(stream.pos)
      if (cppCliKeywords.has(text)) return 'keyword'
      if (/\busing\s+namespace\s+$/i.test(before)) return 'namespace'
      if (/^[A-Z][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)+$/.test(text)) {
        return /^\s*\(/.test(after) ? 'variableName.function' : 'variableName'
      }
      if (/^[A-Z][A-Za-z0-9_]*\^*$/.test(text)) return 'typeName'
      if (
        /^[A-Za-z_][A-Za-z0-9_]*$/.test(text) &&
        !controlNames.has(text.toLowerCase()) &&
        /^\s*\(/.test(after)
      ) {
        return 'variableName.function'
      }
      return style
    },
  }
}

function csharpAttributeStyle(
  identifier: string,
  before: string,
  after: string,
): 'keyword' | 'namespace' | 'typeName' | null {
  const openBracket = before.lastIndexOf('[')
  if (openBracket < 0 || before.lastIndexOf(']') > openBracket) return null
  const beforeAttribute = before.slice(0, openBracket)
  if (!/^(?:\s*\[[^\]]*\])*\s*$/.test(beforeAttribute)) return null

  const content = before.slice(openBracket + 1)
  if (
    /^\s*$/.test(content) &&
    /^(?:assembly|module|return|method|field|property|event|type|param)$/i.test(identifier) &&
    /^\s*:/.test(after)
  ) {
    return 'keyword'
  }

  const namePrefix = content.replace(
    /^\s*(?:assembly|module|return|method|field|property|event|type|param)\s*:\s*/i,
    '',
  )
  if (!/^(?:\s*(?:global\s*::\s*)?)?(?:[A-Za-z_]\w*\s*\.\s*)*$/.test(namePrefix)) {
    return null
  }
  return /^\s*\./.test(after) ? 'namespace' : 'typeName'
}

function isCsharpNamespace(identifier: string, before: string, after: string): boolean {
  if (!/^[A-Z]/.test(identifier)) return false
  if (/\busing\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*\.\s*)*$/i.test(before)) return true
  const next = /^\s*\.\s*[A-Za-z_]\w*/.exec(after)
  if (!next) return false
  const remaining = after.slice(next[0].length)
  return /^\s*\./.test(remaining) || /^\s+[A-Za-z_]\w*/.test(remaining)
}

function isCsharpTypeReference(identifier: string, before: string, after: string): boolean {
  if (!/^[A-Z]/.test(identifier)) return false
  if (/\b(?:new|typeof|sizeof|default|is|as)\s*$/i.test(before)) return true
  if (/\bcatch\s*\(\s*$/i.test(before)) return true
  if (/\b(?:public|private|protected|internal|static)\s+$/i.test(before) && /^\s*\(/.test(after)) {
    return true
  }
  if (/^(?:\s*<[^>\n]*>)?(?:\s*\[\s*\])*\s+[A-Za-z_]\w*/.test(after)) return true

  const member = /^\s*\.\s*[A-Za-z_]\w*/.exec(after)
  if (!member) return false
  const remaining = after.slice(member[0].length)
  return !/^\s*\./.test(remaining) && !/^\s+[A-Za-z_]\w*/.test(remaining)
}

function isCompilerGeneratedMethodPart(identifier: string, before: string, after: string): boolean {
  if (
    /^[A-Za-z_][\w']*$/.test(identifier) &&
    /<$/.test(before) &&
    /^>\$?\s*(?:<[^>{}]*>\s*)?\(/.test(after)
  ) {
    return true
  }
  return identifier === '$' && /<[A-Za-z_][\w']*>$/.test(before) && /^\s*\(/.test(after)
}

function isNonIdentifierStyle(style: string | null): boolean {
  return (
    style !== null &&
    /(?:comment|string|keyword|type|number|operator|meta|atom|property)/.test(style)
  )
}

type SimpleLanguage = 'asm' | 'il' | 'gsharp' | 'minilang'

interface SimpleState {
  blockComment: boolean
  quote: string | null
}

function createSimpleParser(language: SimpleLanguage): StreamParser<SimpleState> {
  const keywords = simpleKeywords[language]
  return {
    name: language,
    startState: () => ({ blockComment: false, quote: null }),
    copyState: (state) => ({ ...state }),
    tokenTable: {
      functionName: tags.function(tags.variableName),
    },
    languageData: {
      closeBrackets: {
        brackets:
          language === 'il' || language === 'asm'
            ? ['(', '[', '{', '"']
            : ['(', '[', '{', '"', "'"],
      },
      commentTokens:
        language === 'asm' ? { line: ';' } : { line: '//', block: { open: '/*', close: '*/' } },
    },
    token(stream, state) {
      if (stream.eatSpace()) return null
      if (state.blockComment) return readBlockComment(stream, state)
      if (state.quote) return readString(stream, state)

      if (language === 'asm' && stream.match(/^;\s*Assembly listing for method\s+/i)) {
        return 'comment'
      }
      if (language === 'asm' && (stream.peek() === ';' || stream.peek() === '#')) {
        stream.skipToEnd()
        return 'comment'
      }
      if (stream.match('//')) {
        stream.skipToEnd()
        return 'comment'
      }
      if (stream.match('/*')) {
        state.blockComment = true
        return readBlockComment(stream, state)
      }

      const quote = stream.peek()
      if (quote === '"' || (quote === "'" && language !== 'il')) {
        state.quote = quote
        stream.next()
        return readString(stream, state)
      }

      if (
        stream.match(
          /^(?:0x[0-9a-f_]+|0b[01_]+|[0-9][0-9a-f_]*h\b|\d(?:[\d_]*\.?[\d_]*)(?:e[+-]?\d+)?)/i,
        )
      ) {
        return 'number'
      }
      if (language === 'il' && stream.match(/^\.[A-Za-z_][\w.]*/)) return 'keyword'
      if (
        stream.match(
          language === 'il' ? /^[A-Za-z_][\w']*(?:\.[A-Za-z0-9_]+)*\.?/ : /^[A-Za-z_][\w']*/,
        )
      ) {
        const word = stream.current()
        if (language === 'il' && isIlAssemblyName(stream, word)) return 'macroName'
        if (stream.match(/^\s*:(?!:)/, false)) return 'labelName'
        if (language === 'asm' && isAssemblyOpcodePosition(stream.string, stream.start)) {
          return 'keyword'
        }
        if (language === 'asm' && isAssemblyRegister(word)) return 'variableName'
        if (keywords.has(word.toLowerCase())) return 'keyword'
        if (language === 'asm' && /\bcall\s+$/i.test(stream.string.slice(0, stream.start))) {
          return 'functionName'
        }
        if (looksLikeFunction(stream, word)) return 'functionName'
        if (/^[A-Z]/.test(word)) return 'typeName'
        return 'variableName'
      }
      if (stream.match(/^(?:::|=>|->|==|!=|<=|>=|&&|\|\||[=><!~?:&|+\-*/%^])/)) {
        return 'operator'
      }
      stream.next()
      return null
    },
  }
}

function isAssemblyOpcodePosition(line: string, tokenStart: number): boolean {
  const before = line.slice(0, tokenStart)
  return /^\s*(?:[A-Za-z_.$?][\w.$?]*\s*:\s*)?(?:(?:lock|rep|repe|repz|repne|repnz)\s+)*$/i.test(
    before,
  )
}

function isAssemblyRegister(word: string): boolean {
  const value = word.toLowerCase()
  return (
    assemblyRegisters.has(value) ||
    /^(?:[xyz]mm(?:[12]?\d|3[01])|k[0-7]|bnd[0-3]|st[0-7])$/.test(value)
  )
}

function isIlAssemblyName(stream: StringStream, word: string): boolean {
  const before = stream.string.slice(0, stream.start)
  const after = stream.string.slice(stream.pos)
  if (/^(?:in|out|opt|retval)$/i.test(word)) return false
  if (/\[[^\]]*$/.test(before) && /^\](?=[A-Za-z_'<])/.test(after)) return true
  if (word.toLowerCase() === 'extern') return false
  return /^\s*\.assembly\s+(?:extern\s+)?$/i.test(before)
}

function readBlockComment(stream: StringStream, state: SimpleState): string {
  while (!stream.eol()) {
    if (stream.match('*/')) {
      state.blockComment = false
      break
    }
    stream.next()
  }
  return 'comment'
}

function readString(stream: StringStream, state: SimpleState): string {
  while (!stream.eol()) {
    const character = stream.next()
    if (character === '\\') {
      if (stream.pos - stream.start > 1) {
        stream.backUp(1)
        return 'string'
      }
      const marker = stream.next()
      const hexDigits = marker === 'u' ? 4 : marker === 'U' ? 8 : marker === 'x' ? 4 : 0
      for (let index = 0; index < hexDigits && /^[0-9a-f]$/i.test(stream.peek() ?? ''); index++) {
        stream.next()
      }
      return 'escape'
    }
    if (character === state.quote) {
      state.quote = null
      break
    }
  }
  return 'string'
}

function looksLikeFunction(stream: StringStream, word: string): boolean {
  if (controlNames.has(word.toLowerCase())) return false
  const before = stream.string.slice(0, stream.start)
  const after = stream.string.slice(stream.pos)
  return (
    /^\s*(?:<[^>{}]*>\s*)?\(/.test(after) ||
    /\b(?:func|fn)\s+$/i.test(before) ||
    /::\s*$/.test(before)
  )
}

const simpleKeywords: Record<SimpleLanguage, ReadonlySet<string>> = {
  asm: new Set([
    'adc',
    'add',
    'align',
    'and',
    'byte',
    'call',
    'cmp',
    'cmove',
    'cmovg',
    'cmovge',
    'cmovl',
    'cmovle',
    'cmovne',
    'dec',
    'dword',
    'inc',
    'ja',
    'jae',
    'jb',
    'jbe',
    'je',
    'jg',
    'jge',
    'jl',
    'jle',
    'jmp',
    'jne',
    'lea',
    'near',
    'mov',
    'movsx',
    'movsxd',
    'movzx',
    'mul',
    'neg',
    'nop',
    'not',
    'or',
    'oword',
    'pop',
    'ptr',
    'push',
    'qword',
    'ret',
    'rol',
    'ror',
    'sar',
    'sbb',
    'seta',
    'setae',
    'setb',
    'setbe',
    'sete',
    'setg',
    'setge',
    'setl',
    'setle',
    'setne',
    'shl',
    'shr',
    'sub',
    'short',
    'tbyte',
    'test',
    'word',
    'xmmword',
    'xor',
    'ymmword',
    'zmmword',
  ]),
  gsharp: new Set([
    'async',
    'await',
    'break',
    'case',
    'catch',
    'class',
    'const',
    'continue',
    'data',
    'defer',
    'else',
    'enum',
    'extension',
    'false',
    'finally',
    'for',
    'func',
    'guard',
    'if',
    'import',
    'in',
    'interface',
    'internal',
    'let',
    'match',
    'nil',
    'object',
    'override',
    'package',
    'partial',
    'private',
    'protected',
    'public',
    'return',
    'scope',
    'shared',
    'static',
    'struct',
    'throw',
    'true',
    'try',
    'var',
    'virtual',
    'while',
  ]),
  il: ilWordTokens,
  minilang: new Set(['false', 'fn', 'func', 'let', 'print', 'true']),
}

const assemblyRegisters = new Set([
  'al',
  'ah',
  'ax',
  'eax',
  'rax',
  'bl',
  'bh',
  'bx',
  'ebx',
  'rbx',
  'cl',
  'ch',
  'cx',
  'ecx',
  'rcx',
  'dl',
  'dh',
  'dx',
  'edx',
  'rdx',
  'sil',
  'si',
  'esi',
  'rsi',
  'dil',
  'di',
  'edi',
  'rdi',
  'spl',
  'sp',
  'esp',
  'rsp',
  'bpl',
  'bp',
  'ebp',
  'rbp',
  'r8b',
  'r8w',
  'r8d',
  'r8',
  'r9b',
  'r9w',
  'r9d',
  'r9',
  'r10b',
  'r10w',
  'r10d',
  'r10',
  'r11b',
  'r11w',
  'r11d',
  'r11',
  'r12b',
  'r12w',
  'r12d',
  'r12',
  'r13b',
  'r13w',
  'r13d',
  'r13',
  'r14b',
  'r14w',
  'r14d',
  'r14',
  'r15b',
  'r15w',
  'r15d',
  'r15',
  'rip',
  'xmm0',
  'xmm1',
  'xmm2',
  'xmm3',
  'xmm4',
  'xmm5',
  'xmm6',
  'xmm7',
  'ymm0',
  'ymm1',
  'ymm2',
  'ymm3',
  'zmm0',
  'zmm1',
  'zmm2',
  'zmm3',
])
