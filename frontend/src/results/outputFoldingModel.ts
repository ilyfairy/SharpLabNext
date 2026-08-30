export type OutputFoldingKind = 'assembly' | 'type' | 'method' | 'block' | 'brace'

export interface OutputFoldingRange {
  startLine: number
  endLine: number
  kind: OutputFoldingKind
}

export function outputFoldingRanges(text: string, languageId: string): OutputFoldingRange[] {
  if (!text) return [];
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  const ranges = languageId === 'asm' ? jitFoldingRanges(lines) : languageId === 'il' ? ilFoldingRanges(lines) : []
  return ranges.sort((left, right) => left.startLine - right.startLine || right.endLine - left.endLine);
}

function jitFoldingRanges(lines: readonly string[]): OutputFoldingRange[] {
  const ranges: OutputFoldingRange[] = []
  const methodLines = lines.map((line, index) => (isJitMethodHeader(line) ? index + 1 : 0)).filter((lineNumber) => lineNumber > 0)

  for (let index = 0; index < methodLines.length; index += 1) {
    const startLine = methodLines[index]
    if (!startLine) continue
    const nextMethodLine = methodLines[index + 1] ?? lines.length + 1
    const endLine = trimTrailingBlankLines(lines, startLine, nextMethodLine - 1)
    addRange(ranges, startLine, endLine, 'method')

    const labelLines: number[] = []
    for (let lineNumber = startLine + 1; lineNumber <= endLine; lineNumber += 1) {
      if (isJitBlockLabel(lines[lineNumber - 1] ?? '')) labelLines.push(lineNumber)
    }
    for (let labelIndex = 0; labelIndex < labelLines.length; labelIndex += 1) {
      const labelLine = labelLines[labelIndex]
      if (!labelLine) continue
      const nextLabelLine = labelLines[labelIndex + 1] ?? endLine + 1
      addRange(ranges, labelLine, trimTrailingBlankLines(lines, labelLine, nextLabelLine - 1), 'block')
    }
  }
  return ranges
}

function ilFoldingRanges(lines: readonly string[]): OutputFoldingRange[] {
  const ranges: OutputFoldingRange[] = []
  const stack: Array<{ startLine: number; kind: OutputFoldingKind }> = []
  let pendingDeclaration: {
    startLine: number
    kind: OutputFoldingKind
  } | null = null
  let blockComment = false

  for (let index = 0; index < lines.length; index += 1) {
    const lineNumber = index + 1
    const line = lines[index] ?? ''
    const declarationKind = ilDeclarationKind(line)
    if (declarationKind) pendingDeclaration = { startLine: lineNumber, kind: declarationKind }

    const braces = structuralBraces(line, blockComment)
    blockComment = braces.blockComment
    for (const brace of braces.characters) {
      if (brace === '{') {
        stack.push(pendingDeclaration ?? { startLine: lineNumber, kind: 'brace' })
        pendingDeclaration = null
        continue
      }
      const opening = stack.pop()
      if (!opening) continue
      // Keep the closing brace visible, matching normal editor folding behavior.
      addRange(ranges, opening.startLine, lineNumber - 1, opening.kind)
    }
  }
  return ranges
}

function isJitMethodHeader(line: string): boolean {
  return /^\S.*\([^)]*\).*:\s*$/.test(line)
}

function isJitBlockLabel(line: string): boolean {
  return /^\s*[A-Za-z_.$?][\w.$?]*:\s*$/.test(line) && !isJitMethodHeader(line)
}

function ilDeclarationKind(line: string): OutputFoldingKind | null {
  const directive = /^\s*\.(assembly|class|interface|method|namespace)\b/i.exec(line)?.[1]
  switch (directive?.toLowerCase()) {
    case 'assembly':
      return 'assembly'
    case 'class':
    case 'interface':
    case 'namespace':
      return 'type'
    case 'method':
      return 'method'
    default:
      return null
  }
}

function structuralBraces(line: string, startsInBlockComment: boolean): { characters: Array<'{' | '}'>; blockComment: boolean } {
  const characters: Array<'{' | '}'> = []
  let blockComment = startsInBlockComment
  let quote: '"' | "'" | null = null
  for (let index = 0; index < line.length; index += 1) {
    const current = line[index]
    const next = line[index + 1]
    if (blockComment) {
      if (current === '*' && next === '/') {
        blockComment = false
        index += 1
      }
      continue
    }
    if (quote) {
      if (current === '\\') index += 1
      else if (current === quote) quote = null
      continue
    }
    if (current === '/' && next === '/') break
    if (current === '/' && next === '*') {
      blockComment = true
      index += 1
      continue
    }
    if (current === '"' || current === "'") {
      quote = current
      continue
    }
    if (current === '{' || current === '}') characters.push(current)
  }
  return { characters, blockComment }
}

function trimTrailingBlankLines(lines: readonly string[], startLine: number, candidateEndLine: number): number {
  let endLine = Math.min(lines.length, candidateEndLine)
  while (endLine > startLine && !lines[endLine - 1]?.trim()) endLine -= 1
  return endLine
}

function addRange(ranges: OutputFoldingRange[], startLine: number, endLine: number, kind: OutputFoldingKind): void {
  if (startLine < 1 || endLine <= startLine) return
  if (ranges.some((range) => range.startLine === startLine && range.endLine === endLine && range.kind === kind)) {
    return
  }
  ranges.push({ startLine, endLine, kind })
}
