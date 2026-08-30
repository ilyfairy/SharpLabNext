import type { SourceMethodSelection } from './sourceMethod';

export interface LspPosition {
  line: number
  character: number
}

export interface LspRange {
  start: LspPosition
  end: LspPosition
}

export interface LspDocumentSymbol {
  name: string;
  kind: number;
  range: LspRange;
  selectionRange: LspRange;
  children: readonly LspDocumentSymbol[];
}

const methodSymbolKinds = new Set([6, 9, 12]);

export function sourceMethodFromDocumentSymbols(symbols: readonly LspDocumentSymbol[], position: LspPosition, languageId: string): SourceMethodSelection | null {
  const candidates: Array<{ symbol: LspDocumentSymbol; depth: number }> = [];
  collectContainingMethods(symbols, position, languageId, 0, candidates);
  candidates.sort((left, right) => right.depth - left.depth || rangeSize(left.symbol.range) - rangeSize(right.symbol.range));
  const selected = candidates[0]?.symbol;
  return selected
    ? {
        name: selected.name,
        lineNumber: selected.selectionRange.start.line + 1,
      }
    : null;
}

export function positionInLspRange(position: LspPosition, range: LspRange): boolean {
  return comparePosition(position, range.start) >= 0 && comparePosition(position, range.end) <= 0;
}

function collectContainingMethods(symbols: readonly LspDocumentSymbol[], position: LspPosition, languageId: string, depth: number, result: Array<{ symbol: LspDocumentSymbol; depth: number }>): void {
  for (const symbol of symbols) {
    if (!positionInLspRange(position, symbol.range)) continue;
    if (methodSymbolKinds.has(symbol.kind) || (languageId === 'fsharp' && symbol.kind === 13)) {
      result.push({ symbol, depth });
    }
    collectContainingMethods(symbol.children, position, languageId, depth + 1, result);
  }
}

function comparePosition(left: LspPosition, right: LspPosition): number {
  return left.line === right.line ? left.character - right.character : left.line - right.line;
}

function rangeSize(range: LspRange): number {
  return (range.end.line - range.start.line) * 1_000_000 + range.end.character - range.start.character;
}
