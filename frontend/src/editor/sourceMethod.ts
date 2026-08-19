export interface SourceMethodSelection {
  name: string
  lineNumber: number
  jitMethodFilter?: string | null
}

interface MethodPattern {
  expression: RegExp
  nameGroup: number
}

const patternsByLanguage: Record<string, readonly MethodPattern[]> = {
  csharp: [
    {
      expression:
        /^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|unsafe|extern|new|partial)\s+)*(?:[\w.<>[\],?]+\s+)+([A-Za-z_]\w*)\s*(?:<[^>{}]*>)?\s*\([^;{}]*\)\s*(?:=>|\{)?/,
      nameGroup: 1,
    },
  ],
  'visual-basic': [
    {
      expression:
        /^\s*(?:(?:Public|Private|Protected|Friend|Shared|Async|Overrides|Overridable|MustOverride)\s+)*(?:Sub|Function)\s+([A-Za-z_]\w*)\b/i,
      nameGroup: 1,
    },
  ],
  fsharp: [
    {
      expression: /^\s*let\s+(?:inline\s+)?(?:rec\s+)?(?:private\s+)?([A-Za-z_][\w']*)\b/,
      nameGroup: 1,
    },
    {
      expression: /^\s*member\s+(?:[A-Za-z_][\w']*\.)?([A-Za-z_][\w']*)\b/,
      nameGroup: 1,
    },
  ],
  il: [
    {
      expression: /^\s*\.method\b.*?\s([A-Za-z_.$<>][\w.$<>]*)\s*\(/,
      nameGroup: 1,
    },
  ],
  gsharp: [
    {
      expression:
        /^\s*(?:(?:public|private|protected|internal|shared|static|async|unsafe|virtual|override)\s+)*func\s+([A-Za-z_]\w*)\b/,
      nameGroup: 1,
    },
  ],
  minilang: [
    {
      expression: /^\s*(?:func|fn)\s+([A-Za-z_]\w*)\b/,
      nameGroup: 1,
    },
  ],
}

const braceScopedLanguages = new Set(['csharp', 'gsharp', 'il', 'minilang'])

const controlKeywords = new Set([
  'if',
  'for',
  'foreach',
  'while',
  'switch',
  'catch',
  'using',
  'lock',
])

export function findSourceMethodAtLine(
  text: string,
  languageId: string,
  lineNumber: number,
  filePath?: string,
): SourceMethodSelection | null {
  if (languageId === 'php') return findPhpFunctionAtLine(text, lineNumber, filePath)

  const patterns = patternsByLanguage[languageId]
  if (!patterns) return null

  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  const lastLine = Math.min(Math.max(lineNumber, 1), lines.length)
  for (let index = lastLine - 1; index >= 0; index -= 1) {
    const line = lines[index] ?? ''
    for (const pattern of patterns) {
      const match = pattern.expression.exec(line)
      const name = match?.[pattern.nameGroup]
      if (!name || isControlKeyword(name)) continue
      if (
        braceScopedLanguages.has(languageId) &&
        !braceMethodContainsLine(lines, index, lastLine - 1)
      ) {
        continue
      }
      if (
        languageId === 'visual-basic' &&
        !visualBasicMethodContainsLine(lines, index, lastLine - 1)
      ) {
        continue
      }
      if (languageId === 'fsharp' && !fsharpMethodContainsLine(lines, index, lastLine - 1)) {
        continue
      }
      return { name, lineNumber: index + 1 }
    }
  }
  if (languageId === 'csharp') {
    const topLevelLine = cSharpTopLevelMethodLine(lines, lastLine - 1)
    if (topLevelLine !== null) return { name: '<Main>$', lineNumber: topLevelLine + 1 }
  }
  return null
}

interface PhpFunctionCandidate {
  name: string
  declarationLine: number
  bodyStartLine: number
  bodyEndLine: number
}

function findPhpFunctionAtLine(
  text: string,
  lineNumber: number,
  _filePath: string | undefined,
): SourceMethodSelection | null {
  const normalized = text.replace(/\r\n?/g, '\n')
  const lines = normalized.split('\n')
  const cursorLine = Math.min(Math.max(lineNumber, 1), lines.length)
  const code = maskPhpStringsAndComments(normalized)
  const lineStarts = sourceLineStarts(code)
  const candidates: PhpFunctionCandidate[] = []
  const declaration =
    /\b(?:(?:abstract|final|public|protected|private|static|readonly)\s+)*function\s*&?\s*([A-Za-z_\x80-\uffff][A-Za-z0-9_\x80-\uffff]*)\s*\(/giu

  for (const match of code.matchAll(declaration)) {
    const name = match[1]
    if (!name || match.index === undefined) continue
    const parametersStart = match.index + match[0].lastIndexOf('(')
    const parametersEnd = findMatchingDelimiter(code, parametersStart, '(', ')')
    if (parametersEnd < 0) continue
    const bodyStart = findPhpFunctionBodyStart(code, parametersEnd + 1)
    if (bodyStart < 0) continue
    const bodyEnd = findMatchingDelimiter(code, bodyStart, '{', '}')
    if (bodyEnd < 0) continue

    const candidate = {
      name,
      declarationLine: lineNumberAtOffset(lineStarts, match.index + match[0].lastIndexOf(name)),
      bodyStartLine: lineNumberAtOffset(lineStarts, bodyStart),
      bodyEndLine: lineNumberAtOffset(lineStarts, bodyEnd),
    }
    if (candidate.declarationLine <= cursorLine && candidate.bodyEndLine >= cursorLine) {
      candidates.push(candidate)
    }
  }

  candidates.sort(
    (left, right) =>
      right.bodyStartLine - left.bodyStartLine || right.declarationLine - left.declarationLine,
  )
  const selected = candidates[0]
  return selected
    ? {
        name: selected.name,
        lineNumber: selected.declarationLine,
        jitMethodFilter: isVerifiedPhpJitIdentifier(selected.name) ? `*${selected.name}*` : null,
      }
    : null
}

function isVerifiedPhpJitIdentifier(name: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/u.test(name)
}

function findPhpFunctionBodyStart(code: string, start: number): number {
  for (let index = start; index < code.length; index += 1) {
    const character = code[index]
    if (character === '{') return index
    if (character === ';') return -1
  }
  return -1
}

function findMatchingDelimiter(
  code: string,
  start: number,
  open: '(' | '{',
  close: ')' | '}',
): number {
  if (code[start] !== open) return -1
  let depth = 0
  for (let index = start; index < code.length; index += 1) {
    const character = code[index]
    if (character === open) depth += 1
    if (character === close) depth -= 1
    if (depth === 0) return index
  }
  return -1
}

function sourceLineStarts(value: string): number[] {
  const starts = [0]
  for (let index = 0; index < value.length; index += 1) {
    if (value.charCodeAt(index) === 10) starts.push(index + 1)
  }
  return starts
}

function lineNumberAtOffset(lineStarts: readonly number[], offset: number): number {
  let low = 0
  let high = lineStarts.length
  while (low < high) {
    const middle = Math.floor((low + high) / 2)
    if ((lineStarts[middle] ?? 0) <= offset) low = middle + 1
    else high = middle
  }
  return Math.max(low, 1)
}

function maskPhpStringsAndComments(value: string): string {
  const result = value.split('')
  let quote: "'" | '"' | '`' | null = null
  let blockComment = false
  let lineComment = false
  let heredocLabel: string | null = null
  let lineStart = 0
  let phpActive = !value.includes('<?')

  for (let index = 0; index < value.length; index += 1) {
    const character = value[index] ?? ''
    const next = value[index + 1] ?? ''

    if (character === '\n') {
      lineComment = false
      result[index] = '\n'
      lineStart = index + 1
      continue
    }

    if (heredocLabel !== null) {
      if (index === lineStart) {
        const lineEnd = value.indexOf('\n', lineStart)
        const end = lineEnd < 0 ? value.length : lineEnd
        const line = value.slice(lineStart, end)
        if (new RegExp(`^[ \\t]*${escapeRegExp(heredocLabel)};?[ \\t]*$`, 'u').test(line)) {
          maskPhpRange(result, lineStart, end)
          heredocLabel = null
          index = end - 1
          continue
        }
      }
      result[index] = ' '
      continue
    }

    if (!phpActive) {
      const openTag = /^<\?(?:php|=)?/iu.exec(value.slice(index))?.[0]
      if (openTag) {
        maskPhpRange(result, index, index + openTag.length)
        phpActive = true
        index += openTag.length - 1
      } else {
        result[index] = ' '
      }
      continue
    }

    if (lineComment) {
      if (character === '?' && next === '>') {
        result[index] = ' '
        result[index + 1] = ' '
        lineComment = false
        phpActive = false
        index += 1
        continue
      }
      result[index] = ' '
      continue
    }

    if (blockComment) {
      result[index] = ' '
      if (character === '*' && next === '/') {
        result[index + 1] = ' '
        blockComment = false
        index += 1
      }
      continue
    }

    if (quote !== null) {
      result[index] = ' '
      if (character === '\\') {
        if (next && next !== '\n') {
          result[index + 1] = ' '
          index += 1
        }
      } else if (character === quote) {
        quote = null
      }
      continue
    }

    if (character === '?' && next === '>') {
      result[index] = ' '
      result[index + 1] = ' '
      phpActive = false
      index += 1
      continue
    }

    if (character === '/' && next === '/') {
      result[index] = ' '
      result[index + 1] = ' '
      lineComment = true
      index += 1
      continue
    }
    if (character === '#' && next !== '[') {
      result[index] = ' '
      lineComment = true
      continue
    }
    if (character === '/' && next === '*') {
      result[index] = ' '
      result[index + 1] = ' '
      blockComment = true
      index += 1
      continue
    }
    if (character === "'" || character === '"' || character === '`') {
      result[index] = ' '
      quote = character
      continue
    }
    if (value.startsWith('<<<', index)) {
      const lineEnd = value.indexOf('\n', index)
      const end = lineEnd < 0 ? value.length : lineEnd
      const declaration =
        /^<<<[ \\t]*(?:'([^']+)'|"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))[ \\t]*$/u.exec(
          value.slice(index, end),
        )
      const label = declaration?.[1] ?? declaration?.[2] ?? declaration?.[3]
      if (label) {
        maskPhpRange(result, index, end)
        heredocLabel = label
        index = end - 1
      }
    }
  }
  return result.join('')
}

function maskPhpRange(result: string[], start: number, end: number): void {
  for (let index = start; index < end; index += 1) result[index] = ' '
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function braceMethodContainsLine(
  lines: readonly string[],
  declarationIndex: number,
  cursorIndex: number,
): boolean {
  const declaration = stripStringsAndLineComment(lines[declarationIndex] ?? '')
  if (declaration.includes('=>')) return declarationIndex === cursorIndex

  let bodyStarted = false
  let depth = 0
  for (let index = declarationIndex; index <= cursorIndex; index += 1) {
    const line = stripStringsAndLineComment(lines[index] ?? '')
    if (!bodyStarted && line.includes(';') && !line.includes('{'))
      return declarationIndex === cursorIndex
    for (const character of line) {
      if (character === '{') {
        bodyStarted = true
        depth += 1
      } else if (character === '}' && bodyStarted) {
        depth -= 1
      }
    }
    if (index < cursorIndex && bodyStarted && depth <= 0) return false
  }
  return bodyStarted
}

function visualBasicMethodContainsLine(
  lines: readonly string[],
  declarationIndex: number,
  cursorIndex: number,
): boolean {
  for (let index = declarationIndex + 1; index < cursorIndex; index += 1) {
    if (/^\s*End\s+(?:Sub|Function)\b/i.test(lines[index] ?? '')) return false
  }
  return true
}

function fsharpMethodContainsLine(
  lines: readonly string[],
  declarationIndex: number,
  cursorIndex: number,
): boolean {
  if (cursorIndex === declarationIndex) return true
  const declarationIndent = leadingWhitespace(lines[declarationIndex] ?? '')
  for (let index = declarationIndex + 1; index <= cursorIndex; index += 1) {
    const line = lines[index] ?? ''
    if (!line.trim()) continue
    if (leadingWhitespace(line) <= declarationIndent) return false
  }
  return true
}

function leadingWhitespace(value: string): number {
  return /^\s*/.exec(value)?.[0].replace(/\t/g, '    ').length ?? 0
}

function stripStringsAndLineComment(value: string): string {
  return value
    .replace(/@?"(?:""|\\.|[^"])*"/g, '""')
    .replace(/'(?:\\.|[^'\\])'/g, "''")
    .replace(/\/\/.*$/, '')
}

function cSharpTopLevelMethodLine(lines: readonly string[], cursorIndex: number): number | null {
  const structuralLines = stripCSharpBlockComments(lines)
  const declarationIndex = structuralLines.findIndex((line) =>
    isCSharpCompilationUnitDeclaration(stripStringsAndLineComment(line)),
  )
  const boundary = declarationIndex < 0 ? structuralLines.length : declarationIndex
  if (cursorIndex >= boundary) return null

  let attributeDepth = 0
  for (let index = 0; index < boundary; index += 1) {
    const value = stripStringsAndLineComment(structuralLines[index] ?? '').trim()
    if (!value) continue
    if (attributeDepth > 0 || value.startsWith('[')) {
      attributeDepth += bracketDelta(value)
      if (attributeDepth < 0) attributeDepth = 0
      continue
    }
    if (
      /^(?:global\s+)?using\b/.test(value) ||
      /^extern\s+alias\b/.test(value) ||
      /^#/.test(value)
    ) {
      continue
    }
    return cursorIndex >= index ? index : null
  }
  return null
}

function isCSharpCompilationUnitDeclaration(value: string): boolean {
  return /(?:^|\]\s*)(?:(?:public|private|protected|internal|static|abstract|sealed|partial|file|readonly|ref|unsafe|new)\s+)*(?:namespace|class|struct|interface|record|enum|delegate)\b/.test(
    value.trim(),
  )
}

function stripCSharpBlockComments(lines: readonly string[]): string[] {
  return lines
    .join('\n')
    .replace(/\/\*[\s\S]*?(?:\*\/|$)/g, (comment) => comment.replace(/[^\n]/g, ' '))
    .split('\n')
}

function bracketDelta(value: string): number {
  return [...value].reduce(
    (depth, character) => depth + (character === '[' ? 1 : character === ']' ? -1 : 0),
    0,
  )
}

function isControlKeyword(value: string): boolean {
  return controlKeywords.has(value.toLowerCase())
}
