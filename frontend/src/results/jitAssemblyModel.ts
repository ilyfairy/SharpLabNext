import type { JitMethodSummary } from '../api/types'

export interface JitAssemblySection {
  id: string
  displayName: string
  text: string
  rawLineToCompactLine: readonly (number | null)[]
  summary: JitMethodSummary | null
  source: JitSourcePreview | null
}

export interface JitSourceFile {
  path: string
  text: string
}

export interface JitSourcePreview {
  documentPath: string
  lineNumber: number
  code: string
}

export interface JitAssemblySourceTooltip {
  startLine: number
  endLine: number
  heading: string
  body: string
}

const methodHeader = /^; Assembly listing for method\s+(.+?)(?:\s+\([^)]*Opts\))?\s*$/gm
const jitBlockLabelDeclaration = /^\s*(G_M\d+_IG\d+):(?:\s*;.*)?\s*$/i
const jitBlockLabelReference = /\bG_M\d+_IG\d+\b/gi

export function parseJitAssembly(text: string, summaries?: readonly JitMethodSummary[], sourceFiles: readonly JitSourceFile[] = []): JitAssemblySection[] {
  const matches = [...text.matchAll(methodHeader)]
  if (matches.length === 0) return []

  const unmatchedSummaries = summaries ? [...summaries] : null
  const sections = matches.map((match, index) => {
    const displayName = (match[1] ?? `Method ${index + 1}`).trim()
    const start = match.index ?? 0
    const end = matches[index + 1]?.index ?? text.length
    const summary = unmatchedSummaries ? takeMatchingSummary(displayName, unmatchedSummaries) : null
    const sectionDisplayName = summary?.displayName ?? displayName
    const compact = compactJitAssemblySection(text.slice(start, end), displayName)
    return {
      id: summary?.methodId ?? `jit-method-${index + 1}`,
      displayName: sectionDisplayName,
      text: compact.text,
      rawLineToCompactLine: compact.rawLineToCompactLine,
      summary,
      source: findJitSourcePreview(sectionDisplayName, sourceFiles),
    }
  })
  return summaries === undefined ? sections : sections.filter((section) => section.summary !== null)
}

function compactJitAssemblySection(text: string, signature: string): Pick<JitAssemblySection, 'text' | 'rawLineToCompactLine'> {
  const rawLines = text.replace(/\r\n?/g, '\n').split('\n')
  const lines = rawLines.slice(1)
  const referencedLabels = new Set<string>()

  for (const line of lines) {
    const instruction = line.split(';', 1)[0] ?? ''
    if (!instruction.trim() || jitBlockLabelDeclaration.test(instruction)) continue
    for (const match of instruction.matchAll(jitBlockLabelReference)) {
      const label = match[0]
      if (label) referencedLabels.add(label.toUpperCase())
    }
  }

  const compactLines = [`${signature}:`]
  const rawLineToCompactLine: (number | null)[] = rawLines.map(() => null)
  rawLineToCompactLine[0] = 0
  for (const [bodyIndex, line] of lines.entries()) {
    const trimmed = line.trim()
    if (!trimmed || isJitBoilerplate(trimmed)) continue

    const label = jitBlockLabelDeclaration.exec(line)?.[1]
    if (label && !referencedLabels.has(label.toUpperCase())) continue
    rawLineToCompactLine[bodyIndex + 1] = compactLines.length
    compactLines.push(line.trimEnd())
  }

  return { text: compactLines.join('\n'), rawLineToCompactLine }
}

export function remapJitLineRange(section: JitAssemblySection, rawStartLine: number, rawEndLine: number): { startLine: number; endLine: number } | null {
  if (!Number.isSafeInteger(rawStartLine) || !Number.isSafeInteger(rawEndLine) || rawStartLine < 0 || rawEndLine < rawStartLine) {
    return null
  }
  const mapped = section.rawLineToCompactLine.slice(rawStartLine, rawEndLine + 1).filter((line): line is number => line !== null)
  const startLine = mapped[0]
  const endLine = mapped.at(-1)
  return startLine === undefined || endLine === undefined ? null : { startLine, endLine }
}

function isJitBoilerplate(line: string): boolean {
  if (!line.startsWith(';')) return false
  const comment = line.slice(1).trim()
  return (
    /^Emitting\b/i.test(comment) ||
    /^(?:FullOpts|MinOpts|Tier[01]|optimized|instrumented)\s+code\b/i.test(comment) ||
    /^(?:rsp|rbp)\s+based\s+frame\b/i.test(comment) ||
    /^(?:partially|fully)\s+interruptible\b/i.test(comment) ||
    /^INL\S*\s+@/i.test(comment) ||
    /\b(?:PGO|inlinees?)\b/i.test(comment) ||
    /^Total bytes of code\b/i.test(comment)
  )
}

export function composeJitAssembly(sections: readonly JitAssemblySection[]): string {
  return sections.map((section) => section.text).join('\n\n')
}

export function jitAssemblySourceTooltips(sections: readonly JitAssemblySection[]): JitAssemblySourceTooltip[] {
  const tooltips: JitAssemblySourceTooltip[] = []
  let startLine = 1
  for (const section of sections) {
    const lineCount = section.text.split('\n').length
    if (section.source) {
      tooltips.push({
        startLine,
        endLine: startLine + lineCount - 1,
        heading: `Approximate source: ${section.source.documentPath}:${section.source.lineNumber}`,
        body: section.source.code,
      })
    }
    startLine += lineCount + 1
  }
  return tooltips
}

export function preferredJitSectionId(sections: readonly JitAssemblySection[], sourceMethodName: string | null | undefined): string | null {
  if (sections.length === 0) return null
  if (sourceMethodName) {
    const exact = sections.find((section) => methodName(section.displayName) === sourceMethodName)
    if (exact) return exact.id
    const contains = sections.find((section) => section.displayName.toLowerCase().includes(sourceMethodName.toLowerCase()))
    if (contains) return contains.id
  }
  return sections.find((section) => !section.displayName.includes('<'))?.id ?? sections[0]?.id ?? null
}

function takeMatchingSummary(displayName: string, summaries: JitMethodSummary[]): JitMethodSummary | null {
  const index = summaries.findIndex((summary) => methodNamesMatch(displayName, summary.displayName))
  if (index < 0) return null
  return summaries.splice(index, 1)[0] ?? null
}

function methodNamesMatch(jitDisplayName: string, summaryDisplayName: string): boolean {
  const jitName = normalizeMethodDisplayName(jitDisplayName)
  const summaryName = normalizeMethodDisplayName(summaryDisplayName)
  const jitNameWithoutSignature = removeSignature(jitName)
  return jitName.includes(summaryName) || (jitNameWithoutSignature.length > 0 && summaryName.includes(jitNameWithoutSignature))
}

function normalizeMethodDisplayName(displayName: string): string {
  return displayName.replace(/:/g, '.').toLowerCase()
}

function removeSignature(displayName: string): string {
  const signatureStart = displayName.indexOf('(')
  return signatureStart < 0 ? displayName : displayName.slice(0, signatureStart)
}

function methodName(displayName: string): string {
  const generatedLocal = /g__([^|>]+)\|/i.exec(displayName)?.[1]
  if (generatedLocal) return generatedLocal
  const withoutSignature = displayName.replace(/\(.*$/, '')
  const candidate = withoutSignature.split(/::|:|\./).at(-1) ?? withoutSignature
  return candidate.replace(/\[.*$/, '').replace(/^<|>\$?$/g, '')
}

function findJitSourcePreview(displayName: string, sourceFiles: readonly JitSourceFile[]): JitSourcePreview | null {
  if (sourceFiles.length === 0) return null
  const name = sourceMethodName(displayName)
  if (!name) return null

  let best: { file: JitSourceFile; lineIndex: number; score: number } | null = null
  for (const file of sourceFiles) {
    const lines = file.text.replace(/\r\n?/g, '\n').split('\n')
    if (name === '<Main>$') {
      const lineIndex = firstTopLevelStatement(lines)
      if (lineIndex !== null && (!best || best.score < 1)) best = { file, lineIndex, score: 1 }
      continue
    }

    for (let lineIndex = 0; lineIndex < lines.length; lineIndex += 1) {
      const score = declarationScore(lines[lineIndex] ?? '', name)
      if (score <= 0 || (best && best.score >= score)) continue
      best = { file, lineIndex, score }
    }
  }
  if (!best) return null
  const code = best.file.text.replace(/\r\n?/g, '\n').split('\n')[best.lineIndex]?.trim() ?? ''
  return {
    documentPath: best.file.path,
    lineNumber: best.lineIndex + 1,
    code,
  }
}

function sourceMethodName(displayName: string): string | null {
  const generatedLocal = /g__([^|>]+)\|/i.exec(displayName)?.[1]
  if (generatedLocal) return generatedLocal
  if (/<Main>\$/i.test(displayName)) return '<Main>$'

  const withoutSignature = displayName.replace(/\(.*$/, '').replace(/\[[^\]]*]$/, '')
  const candidate = withoutSignature.split(/::|:|\./).at(-1) ?? withoutSignature
  if (/^\.?(?:c|typec)ctor$/i.test(candidate)) {
    const declaringType = withoutSignature.split(/::|:|\./).at(-2)
    return declaringType?.replace(/`\d+$/, '') ?? null
  }
  const normalized = candidate.replace(/`\d+$/, '').replace(/^<|>\$?$/g, '')
  return /^[A-Za-z_][\w']*$/u.test(normalized) ? normalized : null
}

function declarationScore(line: string, name: string): number {
  const escaped = escapeRegExp(name)
  if (!new RegExp(`\\b${escaped}\\b`, 'u').test(line)) return 0
  if (new RegExp(`\\b(?:func|fn|function|sub|let|member)\\s+(?:[A-Za-z_][\\w']*\\.)?${escaped}\\b`, 'iu').test(line)) {
    return 4
  }
  if (new RegExp(`(?:^|\\s)(?:[A-Za-z_][\\w.<>,?\\[\\]']*\\s+)+${escaped}\\s*(?:<[^>{}]*>)?\\s*\\(`, 'u').test(line)) {
    return 3
  }
  if (new RegExp(`\\b${escaped}\\s*\\([^;]*\\)\\s*(?:=>|\\{)`, 'u').test(line)) return 2
  return 0
}

function firstTopLevelStatement(lines: readonly string[]): number | null {
  let inBlockComment = false
  for (let index = 0; index < lines.length; index += 1) {
    let line = lines[index]?.trim() ?? ''
    if (inBlockComment) {
      const end = line.indexOf('*/')
      if (end < 0) continue
      line = line.slice(end + 2).trim()
      inBlockComment = false
    }
    if (line.startsWith('/*')) {
      const end = line.indexOf('*/', 2)
      if (end < 0) {
        inBlockComment = true
        continue
      }
      line = line.slice(end + 2).trim()
    }
    if (!line || line.startsWith('//') || line.startsWith('#') || /^(?:global\s+)?using\b/.test(line) || /^extern\s+alias\b/.test(line)) {
      continue
    }
    return index
  }
  return null
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
