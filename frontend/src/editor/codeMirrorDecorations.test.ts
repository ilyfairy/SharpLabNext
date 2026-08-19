import { foldable } from '@codemirror/language'
import { EditorState } from '@codemirror/state'
import { EditorView, lineNumbers } from '@codemirror/view'
import { describe, expect, it } from 'vitest'
import {
  foldingRangeField,
  lspFoldingExtension,
  selectionLineDecorationExtension,
  semanticDecorationExtension,
  semanticDecorationField,
  setFoldingRanges,
  setSemanticDecorations,
  setSignatureHelp,
  signatureHelpField,
} from './codeMirrorDecorations'

describe('CodeMirror language feature state', () => {
  it('keeps signature help stable while typing and closes it at the end of the invocation', () => {
    let state = EditorState.create({ doc: 'Write(', extensions: [signatureHelpField] })
    state = state.update({
      effects: setSignatureHelp.of({
        position: 6,
        label: 'void Write(string value)',
        activeParameterLabel: 'string value',
        activeSignature: 0,
        signatureCount: 1,
      }),
    }).state
    state = state.update({ changes: { from: 6, insert: 'v' } }).state
    expect(state.field(signatureHelpField)?.position).toBe(7)
    state = state.update({ changes: { from: 7, insert: ')' } }).state
    expect(state.field(signatureHelpField)).toBeNull()
  })

  it('exposes LSP folding ranges through CodeMirror foldService', () => {
    let state = EditorState.create({
      doc: 'class C {\n  void Run() {}\n}',
      extensions: [lspFoldingExtension],
    })
    state = state.update({ effects: setFoldingRanges.of([{ from: 9, to: 27 }]) }).state
    const firstLine = state.doc.line(1)
    expect(state.field(foldingRangeField)).toEqual([{ from: 9, to: 27 }])
    expect(foldable(state, firstLine.from, firstLine.to)).toEqual({ from: 9, to: 27 })
  })

  it('maps the previous semantic token set through edits until a replacement arrives', () => {
    let state = EditorState.create({
      doc: 'class Widget {}',
      extensions: [semanticDecorationExtension],
    })
    state = state.update({
      effects: setSemanticDecorations.of([
        { from: 6, to: 12, className: 'cm-semantic-token cm-semantic-type' },
      ]),
    }).state

    state = state.update({ changes: { from: 0, insert: '// ' } }).state

    const ranges: Array<{ from: number; to: number }> = []
    state.field(semanticDecorationField).between(0, state.doc.length, (from, to) => {
      ranges.push({ from, to })
    })
    expect(ranges).toEqual([{ from: 9, to: 15 }])
  })

  it('marks every selected row, including the line-number gutter, and clears on collapse', () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: 'first\nsecond\nthird',
        extensions: [lineNumbers(), selectionLineDecorationExtension],
      }),
    })

    view.dispatch({ selection: { anchor: 0, head: 12 } })
    expect(parent.querySelectorAll('.cm-selection-line')).toHaveLength(2)
    expect(parent.querySelectorAll('.cm-selection-line-gutter')).toHaveLength(2)

    view.dispatch({ selection: { anchor: 2 } })
    expect(parent.querySelectorAll('.cm-selection-line')).toHaveLength(0)
    expect(parent.querySelectorAll('.cm-selection-line-gutter')).toHaveLength(0)

    view.destroy()
    parent.remove()
  })
})
