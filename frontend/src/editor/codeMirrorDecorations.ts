import { foldService } from '@codemirror/language'
import { type Extension, Prec, RangeSet, StateEffect, StateField } from '@codemirror/state'
import {
  Decoration,
  type DecorationSet,
  EditorView,
  GutterMarker,
  gutterLineClass,
  showTooltip,
  type Tooltip,
} from '@codemirror/view'

export interface CodeMirrorDecorationRange {
  from: number
  to: number
  className: string
  title?: string
  isLine?: boolean
}

export interface CodeMirrorSignaturePresentation {
  position: number
  label: string
  activeParameterLabel?: string
  documentation?: string
  activeSignature: number
  signatureCount: number
}

export interface CodeMirrorFoldingRange {
  from: number
  to: number
}

export const setSemanticDecorations = StateEffect.define<readonly CodeMirrorDecorationRange[]>()
export const setExecutionFlowDecorations =
  StateEffect.define<readonly CodeMirrorDecorationRange[]>()
export const setSourceAssociationDecorations =
  StateEffect.define<readonly CodeMirrorDecorationRange[]>()
export const setSignatureHelp = StateEffect.define<CodeMirrorSignaturePresentation | null>()
export const setFoldingRanges = StateEffect.define<readonly CodeMirrorFoldingRange[]>()

export const semanticDecorationField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(value, transaction) {
    let next = value.map(transaction.changes)
    for (const effect of transaction.effects) {
      if (effect.is(setSemanticDecorations)) next = decorationSet(effect.value)
    }
    return next
  },
  provide: (field) => EditorView.decorations.from(field),
})

export const semanticDecorationExtension: Extension = Prec.highest(semanticDecorationField)

export const executionFlowDecorationField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(value, transaction) {
    let next = value.map(transaction.changes)
    for (const effect of transaction.effects) {
      if (effect.is(setExecutionFlowDecorations)) next = decorationSet(effect.value)
    }
    return next
  },
  provide: (field) => EditorView.decorations.from(field),
})

export const sourceAssociationDecorationField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(value, transaction) {
    let next = value.map(transaction.changes)
    for (const effect of transaction.effects) {
      if (effect.is(setSourceAssociationDecorations)) next = decorationSet(effect.value)
    }
    return next
  },
  provide: (field) => EditorView.decorations.from(field),
})

/**
 * CodeMirror's drawn selection follows glyph bounds (which are shorter than
 * the configured line-height). Keep a separate line decoration for selected
 * lines so the selection has the same full-row rhythm as Monaco/Visual Studio.
 */
export const selectionLineDecorationField = StateField.define<DecorationSet>({
  create: (state) => selectedLineDecorations(state),
  update(value, transaction) {
    return transaction.docChanged || transaction.selection !== undefined
      ? selectedLineDecorations(transaction.state)
      : value
  },
  provide: (field) => EditorView.decorations.from(field),
})

class SelectionLineGutterMarker extends GutterMarker {
  elementClass = 'cm-selection-line-gutter'
}

const selectionLineGutterMarker = new SelectionLineGutterMarker()

/** Adds the matching full-row background to line-number gutters. */
export const selectionLineDecorationExtension: Extension = [
  selectionLineDecorationField,
  gutterLineClass.compute([selectionLineDecorationField], (state) => {
    const ranges: Array<ReturnType<typeof selectionLineGutterMarker.range>> = []
    for (const line of selectedLineNumbers(state)) {
      ranges.push(selectionLineGutterMarker.range(state.doc.line(line).from))
    }
    return RangeSet.of(ranges)
  }),
]

export const signatureHelpField = StateField.define<CodeMirrorSignaturePresentation | null>({
  create: () => null,
  update(value, transaction) {
    let next = value
    if (next && transaction.docChanged) {
      let closesSignature = false
      transaction.changes.iterChanges((_fromA, _toA, _fromB, _toB, inserted) => {
        if (inserted.toString().includes(')')) closesSignature = true
      })
      next = closesSignature
        ? null
        : { ...next, position: transaction.changes.mapPos(next.position, 1) }
    }
    if (next && transaction.selection && !transaction.docChanged) next = null
    for (const effect of transaction.effects) {
      if (effect.is(setSignatureHelp)) next = effect.value
    }
    return next
  },
  provide: (field) =>
    showTooltip.from(field, (presentation) =>
      presentation ? signatureTooltip(presentation) : null,
    ),
})

export const foldingRangeField = StateField.define<readonly CodeMirrorFoldingRange[]>({
  create: () => [],
  update(value, transaction) {
    let next = transaction.docChanged ? [] : value
    for (const effect of transaction.effects) {
      if (effect.is(setFoldingRanges)) next = effect.value
    }
    return next
  },
})

export const lspFoldingExtension: Extension = [
  foldingRangeField,
  foldService.of((state, lineStart, lineEnd) => {
    const range = state
      .field(foldingRangeField)
      .find((candidate) => candidate.from >= lineStart && candidate.from <= lineEnd + 1)
    return range ? { from: range.from, to: range.to } : null
  }),
]

function decorationSet(ranges: readonly CodeMirrorDecorationRange[]): DecorationSet {
  return Decoration.set(
    ranges
      .filter((range) => range.from >= 0 && (range.isLine || range.to > range.from))
      .map((range) =>
        range.isLine
          ? Decoration.line({
              attributes: {
                class: range.className,
                ...(range.title ? { title: range.title } : {}),
              },
            }).range(range.from)
          : Decoration.mark({
              class: range.className,
              ...(range.title ? { attributes: { title: range.title } } : {}),
            }).range(range.from, range.to),
      )
      .sort((left, right) => left.from - right.from || left.to - right.to),
    true,
  )
}

function selectedLineDecorations(state: import('@codemirror/state').EditorState): DecorationSet {
  return Decoration.set(
    selectedLineNumbers(state).map((line) =>
      Decoration.line({ attributes: { class: 'cm-selection-line' } }).range(
        state.doc.line(line).from,
      ),
    ),
    true,
  )
}

function selectedLineNumbers(state: import('@codemirror/state').EditorState): number[] {
  const lines = new Set<number>()
  for (const range of state.selection.ranges) {
    if (range.empty) continue
    const first = state.doc.lineAt(range.from).number
    // An end position at the start of a line belongs to the preceding line.
    const last = state.doc.lineAt(Math.max(range.from, range.to - 1)).number
    for (let line = first; line <= last; line += 1) lines.add(line)
  }
  return [...lines].sort((left, right) => left - right)
}

function signatureTooltip(presentation: CodeMirrorSignaturePresentation): Tooltip {
  return {
    pos: presentation.position,
    above: true,
    strictSide: false,
    create: () => {
      const dom = document.createElement('div')
      dom.className = 'cm-signature-help'
      const heading = document.createElement('div')
      heading.className = 'cm-signature-heading'
      appendSignatureLabel(heading, presentation.label, presentation.activeParameterLabel)
      dom.append(heading)

      if (presentation.signatureCount > 1) {
        const count = document.createElement('span')
        count.className = 'cm-signature-count'
        count.textContent = `${presentation.activeSignature + 1}/${presentation.signatureCount}`
        heading.append(count)
      }
      if (presentation.documentation) {
        const documentation = document.createElement('div')
        documentation.className = 'cm-signature-documentation'
        documentation.textContent = presentation.documentation
        dom.append(documentation)
      }
      return { dom }
    },
  }
}

function appendSignatureLabel(
  parent: HTMLElement,
  label: string,
  activeParameterLabel: string | undefined,
): void {
  if (!activeParameterLabel) {
    parent.append(document.createTextNode(label))
    return
  }
  const index = label.indexOf(activeParameterLabel)
  if (index < 0) {
    parent.append(document.createTextNode(label))
    return
  }
  parent.append(document.createTextNode(label.slice(0, index)))
  const parameter = document.createElement('strong')
  parameter.className = 'cm-signature-parameter'
  parameter.textContent = activeParameterLabel
  parent.append(
    parameter,
    document.createTextNode(label.slice(index + activeParameterLabel.length)),
  )
}
