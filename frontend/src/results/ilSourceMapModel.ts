import type { LinkedRange, TextRange } from '../api/types'
import type { ExecutionFlowSourceTarget } from './executionFlowModel'

export interface IlSourceFile {
  path: string
  text: string
}

export interface IlSourceLink {
  startLine: number
  endLine: number
  heading: string
  body: string
  target: ExecutionFlowSourceTarget
}

export function createIlSourceLinks(linkedRanges: readonly LinkedRange[], sourceFiles: readonly IlSourceFile[], ilText?: string): IlSourceLink[] {
  const links: IlSourceLink[] = []
  for (const linkedRange of linkedRanges) {
    if (!linkedRange.sourceFilePath || !linkedRange.sourceRange) continue
    const file = resolveSourceFile(linkedRange.sourceFilePath, sourceFiles)
    if (!file || !validRange(linkedRange.sourceRange, file.text)) continue

    const outputLines = toOneBasedLineRange(linkedRange.outputRange)
    if (!outputLines) continue
    const target = toSourceTarget(file.path, linkedRange.sourceRange)
    links.push({
      ...outputLines,
      heading: `${file.path}:${target.range.startLine}:${target.range.startColumn}`,
      body: sourceExcerpt(file.text, linkedRange.sourceRange),
      target,
    })
  }
  links.sort((left, right) => left.startLine - right.startLine)
  return ilText ? expandIlSourceLinks(links, ilText) : links
}

function expandIlSourceLinks(links: readonly IlSourceLink[], ilText: string): IlSourceLink[] {
  const lines = ilText.replace(/\r\n?/g, '\n').split('\n')
  return links.map((link, index) => {
    const nextStartLine = links[index + 1]?.startLine ?? lines.length + 1
    let firstInstructionLine: number | null = null
    let lastInstructionLine: number | null = null
    for (let lineNumber = link.startLine; lineNumber < nextStartLine && lineNumber <= lines.length; lineNumber += 1) {
      const line = lines[lineNumber - 1] ?? ''
      if (lineNumber > link.startLine && isIlSourceBoundary(line)) break
      if (/^\s*IL_[0-9a-f]+:/i.test(line)) {
        firstInstructionLine ??= lineNumber
        lastInstructionLine = lineNumber
      }
    }
    return firstInstructionLine === null || lastInstructionLine === null
      ? link
      : {
          ...link,
          startLine: firstInstructionLine,
          endLine: lastInstructionLine,
        }
  })
}

function isIlSourceBoundary(line: string): boolean {
  return /^\s*\/\/\s*sequence point:/i.test(line) || /^\s*\.method\b/i.test(line) || /^\s*}\s*\/\/\s*end of method\b/i.test(line)
}

function resolveSourceFile(sourceFilePath: string, sourceFiles: readonly IlSourceFile[]): IlSourceFile | null {
  const normalizedSourcePath = normalizePath(sourceFilePath)
  const exactMatches = sourceFiles.filter((file) => normalizePath(file.path) === normalizedSourcePath)
  if (exactMatches.length === 1) return exactMatches[0] ?? null
  if (exactMatches.length > 1) return null
  const matches = sourceFiles.filter((file) => {
    const normalizedWorkspacePath = normalizePath(file.path)
    return normalizedSourcePath.endsWith(`/${normalizedWorkspacePath}`)
  })
  return matches.length === 1 ? (matches[0] ?? null) : null
}

function normalizePath(path: string): string {
  return path
    .replaceAll('\\', '/')
    .replace(/^file:\/\/+/i, '/')
    .replace(/^\.\//, '')
    .toLowerCase()
}

function validRange(range: TextRange, text: string): boolean {
  const coordinates = [range.startLine, range.startCharacter, range.endLine, range.endCharacter]
  if (!coordinates.every(isNonNegativeInteger)) return false
  if (range.endLine < range.startLine || (range.endLine === range.startLine && range.endCharacter < range.startCharacter)) {
    return false
  }

  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  const startText = lines[range.startLine]
  const endText = lines[range.endLine]
  return startText !== undefined && endText !== undefined && range.startCharacter <= startText.length && range.endCharacter <= endText.length
}

function toOneBasedLineRange(range: TextRange): Pick<IlSourceLink, 'startLine' | 'endLine'> | null {
  const coordinates = [range.startLine, range.startCharacter, range.endLine, range.endCharacter]
  if (!coordinates.every(isNonNegativeInteger) || range.endLine < range.startLine) return null
  const inclusiveEndLine = range.endLine > range.startLine && range.endCharacter === 0 ? range.endLine - 1 : range.endLine
  return {
    startLine: range.startLine + 1,
    endLine: Math.max(range.startLine, inclusiveEndLine) + 1,
  }
}

function toSourceTarget(documentPath: string, range: TextRange): ExecutionFlowSourceTarget {
  return {
    documentPath,
    range: {
      startLine: range.startLine + 1,
      startColumn: range.startCharacter + 1,
      endLine: range.endLine + 1,
      endColumn: range.endCharacter + 1,
    },
  }
}

function sourceExcerpt(text: string, range: TextRange): string {
  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  const endExclusive = range.endLine > range.startLine && range.endCharacter === 0 ? range.endLine : range.endLine + 1
  return lines.slice(range.startLine, endExclusive).join('\n').trim()
}

function isNonNegativeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && Number(value) >= 0
}
