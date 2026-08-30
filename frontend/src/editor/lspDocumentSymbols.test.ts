import { describe, expect, it } from 'vitest'
import type { CodeMirrorDocumentSymbol } from '../lsp/codeMirrorLanguageClient'
import { sourceMethodFromDocumentSymbols } from './lspDocumentSymbols'

describe('document-symbol source method selection', () => {
  it('selects the deepest method or local function containing the cursor', () => {
    const symbols: CodeMirrorDocumentSymbol[] = [symbol('Program', 5, 0, 20, [symbol('Run', 6, 2, 15, [symbol('Local', 12, 7, 10, [])])])]
    expect(sourceMethodFromDocumentSymbols(symbols, { line: 8, character: 3 }, 'csharp')).toEqual({
      name: 'Local',
      lineNumber: 8,
    })
    expect(sourceMethodFromDocumentSymbols(symbols, { line: 4, character: 1 }, 'csharp')).toEqual({
      name: 'Run',
      lineNumber: 3,
    })
  })

  it('treats F# value symbols as functions and ignores non-method symbols elsewhere', () => {
    const value = symbol('compute', 13, 1, 4, [])
    expect(sourceMethodFromDocumentSymbols([value], { line: 2, character: 0 }, 'fsharp')).toEqual({
      name: 'compute',
      lineNumber: 2,
    })
    expect(sourceMethodFromDocumentSymbols([value], { line: 2, character: 0 }, 'csharp')).toBeNull()
  })
})

function symbol(name: string, kind: number, startLine: number, endLine: number, children: readonly CodeMirrorDocumentSymbol[]): CodeMirrorDocumentSymbol {
  return {
    name,
    kind,
    range: {
      start: { line: startLine, character: 0 },
      end: { line: endLine, character: 1 },
    },
    selectionRange: {
      start: { line: startLine, character: 2 },
      end: { line: startLine, character: 2 + name.length },
    },
    children,
  }
}
