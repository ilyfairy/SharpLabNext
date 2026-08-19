import { foldable } from '@codemirror/language'
import { EditorState } from '@codemirror/state'
import { activateHover, EditorView } from '@codemirror/view'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CodeMirrorLspHover, LspPosition } from '../lsp/codeMirrorLanguageClient'
import {
  CodeDocumentView,
  codeDocumentFoldingExtensions,
  codeDocumentIlHoverSource,
  codeDocumentSourceMapHoverSource,
} from './CodeDocumentView'
import { createSourceAssociation } from './sourceAssociationModel'

const ilOutputLanguageSessionMock = vi.hoisted(() => ({
  semanticTokens: [] as Array<{
    line: number
    character: number
    length: number
    tokenType: string
    tokenModifiers: readonly string[]
  }>,
  hover: vi.fn<(position: LspPosition) => Promise<CodeMirrorLspHover | null>>(async () => null),
}))

if (!Range.prototype.getClientRects) {
  Object.defineProperty(Range.prototype, 'getClientRects', {
    value: () => [] as unknown as DOMRectList,
  })
}
if (!Range.prototype.getBoundingClientRect) {
  Object.defineProperty(Range.prototype, 'getBoundingClientRect', {
    value: () => new DOMRect(0, 0, 0, 0),
  })
}

vi.mock('./MonacoCodeDocumentView', () => ({
  MonacoCodeDocumentView: ({ text, ariaLabel }: { text: string; ariaLabel: string }) => (
    <textarea readOnly aria-label={ariaLabel} data-output-editor="monaco" value={text} />
  ),
}))

vi.mock('./ilOutputLanguageSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./ilOutputLanguageSession')>()),
  useIlOutputLanguageSession: () => ({
    semanticTokens: ilOutputLanguageSessionMock.semanticTokens,
    status: 'ready',
    hover: ilOutputLanguageSessionMock.hover,
  }),
}))

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  ilOutputLanguageSessionMock.semanticTokens = []
  ilOutputLanguageSessionMock.hover.mockReset()
  ilOutputLanguageSessionMock.hover.mockResolvedValue(null)
})

describe('CodeDocumentView', () => {
  it('renders IL semantic tokens in the read-only CodeMirror result document', async () => {
    ilOutputLanguageSessionMock.semanticTokens = [
      {
        line: 0,
        character: 7,
        length: 7,
        tokenType: 'type',
        tokenModifiers: [],
      },
    ]
    const text = '.class Console {}'
    const languageSessionOptions = {
      catalogRevision: 'catalog-1',
      referenceSetId: 'net10-ref',
      buildMode: 'release' as const,
      workspaceRevision: 2,
      selectionRevision: 3,
    }
    const rendered = render(
      <CodeDocumentView
        text={text}
        languageId="il"
        ariaLabel="Semantic IL output"
        fontSize={14}
        generationKey="result-1"
        ilOutputLanguageSessionOptions={languageSessionOptions}
      />,
    )

    const textbox = screen.getByRole('textbox', { name: 'Semantic IL output' })
    expect(textbox).toHaveAttribute('contenteditable', 'false')
    expect(rendered.container.querySelector('.code-document-view')).toHaveClass('codemirror-host')
    await waitFor(() => {
      expect(rendered.container.querySelector('.cm-semantic-type')).toHaveTextContent('Console')
    })

    rendered.rerender(
      <CodeDocumentView
        text=".class Program {}"
        languageId="il"
        ariaLabel="Semantic IL output"
        fontSize={14}
        generationKey="result-2"
        ilOutputLanguageSessionOptions={languageSessionOptions}
      />,
    )
    await waitFor(() => {
      expect(rendered.container.querySelector('.cm-semantic-type')).toBeNull()
    })

    ilOutputLanguageSessionMock.semanticTokens = [
      {
        line: 0,
        character: 7,
        length: 7,
        tokenType: 'type',
        tokenModifiers: [],
      },
    ]
    rendered.rerender(
      <CodeDocumentView
        text=".class Program {}"
        languageId="il"
        ariaLabel="Semantic IL output"
        fontSize={14}
        generationKey="result-2"
        ilOutputLanguageSessionOptions={languageSessionOptions}
      />,
    )
    await waitFor(() => {
      expect(rendered.container.querySelector('.cm-semantic-type')).toHaveTextContent('Program')
    })
  })

  it('uses the IL language-session hover range and rich tooltip content', async () => {
    ilOutputLanguageSessionMock.hover.mockResolvedValue({
      contents: {
        kind: 'markdown',
        value:
          '```il\n[System.Console, Version=11.0.0.0, Culture=neutral, PublicKeyToken=null]\n```\n\nAssembly reference',
      },
      range: {
        start: { line: 0, character: 1 },
        end: { line: 0, character: 15 },
      },
    })
    const state = EditorState.create({ doc: '[System.Console]Type' })
    const view = new EditorView({ state })
    const sessionRef = {
      current: {
        semanticTokens: [],
        status: 'ready' as const,
        hover: ilOutputLanguageSessionMock.hover,
      },
    }
    const source = codeDocumentIlHoverSource(sessionRef)

    const tooltip = await source(view, 4)

    expect(ilOutputLanguageSessionMock.hover).toHaveBeenCalledWith({ line: 0, character: 4 })
    expect(tooltip).toMatchObject({ pos: 1, end: 15, above: true })
    const dom = tooltip?.create(view).dom
    expect(dom).toHaveClass('cm-lsp-hover')
    expect(dom?.querySelector('.cm-lsp-hover-assembly')).toHaveTextContent('System.Console')
    expect(dom?.querySelector('.cm-lsp-hover-documentation')).toHaveTextContent(
      'Assembly reference',
    )
    view.destroy()
  })

  it('shows mapped source immediately alongside a pending ready-session IL hover', async () => {
    const view = new EditorView({ state: EditorState.create({ doc: 'line one' }) })
    let resolveIlHover: ((hover: CodeMirrorLspHover | null) => void) | undefined
    ilOutputLanguageSessionMock.hover.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveIlHover = resolve
        }),
    )
    const sessionRef = {
      current: {
        semanticTokens: [],
        status: 'ready' as const,
        hover: ilOutputLanguageSessionMock.hover,
      },
    }
    const sourceMapSource = codeDocumentSourceMapHoverSource({
      current: [{ startLine: 1, endLine: 1, heading: 'Program.cs:1', body: 'mapped source' }],
    })
    const ilSource = codeDocumentIlHoverSource(sessionRef)

    const ilTooltipPromise = ilSource(view, 3)
    const sourceTooltip = sourceMapSource(view, 3)

    expect(sourceTooltip).not.toBeInstanceOf(Promise)
    const sourceDom = sourceTooltip?.create(view).dom
    expect(sourceDom).toHaveClass('code-document-source-tooltip')
    expect(sourceDom).toHaveTextContent('Program.cs:1')
    expect(sourceDom).toHaveTextContent('mapped source')
    expect(ilOutputLanguageSessionMock.hover).toHaveBeenCalledWith({ line: 0, character: 3 })

    resolveIlHover?.({
      contents: { kind: 'plaintext', value: 'IL semantic details' },
      range: {
        start: { line: 0, character: 0 },
        end: { line: 0, character: 4 },
      },
    })
    const ilTooltip = await ilTooltipPromise
    const ilDom = ilTooltip?.create(view).dom
    expect(ilDom).toHaveClass('cm-lsp-hover')
    expect(ilDom).toHaveTextContent('IL semantic details')
    view.destroy()
  })

  it('keeps the mapped-source tooltip visible when ready-session IL hover resolves', async () => {
    let resolveIlHover: ((hover: CodeMirrorLspHover | null) => void) | undefined
    ilOutputLanguageSessionMock.hover.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveIlHover = resolve
        }),
    )
    const rendered = render(
      <CodeDocumentView
        text="line one"
        languageId="il"
        ariaLabel="Mapped IL output"
        fontSize={14}
        lineTooltips={[
          { startLine: 1, endLine: 1, heading: 'Program.cs:1', body: 'mapped source' },
        ]}
      />,
    )
    const editor = screen
      .getByRole('textbox', { name: 'Mapped IL output' })
      .closest<HTMLElement>('.cm-editor')
    if (!editor) throw new Error('The mapped IL output editor was not rendered.')
    const editorView = EditorView.findFromDOM(editor)
    if (!editorView) throw new Error('The mapped IL output editor view was not available.')

    act(() => activateHover(editorView, 3, 1))

    expect(rendered.container.querySelector('.code-document-source-tooltip')).toHaveTextContent(
      'mapped source',
    )
    expect(rendered.container.querySelector('.cm-lsp-hover')).toBeNull()

    await act(async () => {
      resolveIlHover?.({
        contents: { kind: 'plaintext', value: 'IL semantic details' },
        range: {
          start: { line: 0, character: 0 },
          end: { line: 0, character: 4 },
        },
      })
      await Promise.resolve()
    })

    await waitFor(() => {
      expect(rendered.container.querySelector('.cm-lsp-hover')).toHaveTextContent(
        'IL semantic details',
      )
    })
    expect(rendered.container.querySelector('.code-document-source-tooltip')).toHaveTextContent(
      'mapped source',
    )
  })

  it('projects JIT structure into CodeMirror fold ranges without changing the document', () => {
    const text = 'Program:Main():\nG_M000_IG01:\n  mov eax, 1\n  ret'
    const state = EditorState.create({
      doc: text,
      extensions: codeDocumentFoldingExtensions('asm'),
    })

    const methodLine = state.doc.line(1)
    const blockLine = state.doc.line(2)
    expect(foldable(state, methodLine.from, methodLine.to)).toEqual({
      from: methodLine.to,
      to: state.doc.line(4).to,
    })
    expect(foldable(state, blockLine.from, blockLine.to)).toEqual({
      from: blockLine.to,
      to: state.doc.line(4).to,
    })
    expect(state.doc.toString()).toBe(text)
  })

  it('folds from the compact gutter without activating a source line action', async () => {
    const onActivate = vi.fn()
    const text = 'Program:Main():\n  mov eax, 1\n  ret'
    const association = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 5 },
      },
      'JIT source: Program.cs:1',
    )
    const { container } = render(
      <CodeDocumentView
        text={text}
        languageId="asm"
        ariaLabel="Foldable JIT"
        fontSize={14}
        lineActions={[{ startLine: 1, endLine: 3, ariaLabel: 'Open source', onActivate }]}
        lineAssociations={[{ startLine: 2, endLine: 3, association }]}
      />,
    )
    const marker = await waitFor(() => {
      const candidate = Array.from(
        container.querySelectorAll<HTMLElement>('.cm-foldGutter .cm-gutterElement'),
      ).find((element) => element.style.visibility !== 'hidden' && element.textContent?.trim())
      expect(candidate).toBeDefined()
      return candidate
    })
    if (!marker) throw new Error('The JIT fold marker was not rendered.')

    fireEvent.mouseDown(marker, { button: 0 })
    fireEvent.click(marker, { button: 0, detail: 1 })
    await waitFor(() => expect(container.querySelector('.cm-foldPlaceholder')).toBeInTheDocument())
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()

    const editor = container.querySelector<HTMLElement>('.cm-editor')
    if (!editor) throw new Error('The folded result editor was not rendered.')
    const editorView = EditorView.findFromDOM(editor)
    if (!editorView) throw new Error('The folded result editor view was not available.')
    editorView.contentDOM.focus()
    editorView.contentDOM.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'a',
        code: 'KeyA',
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      }),
    )
    expect(editorView.state.selection.main.from).toBe(0)
    expect(editorView.state.selection.main.to).toBe(text.length)
    expect(editorView.state.doc.toString()).toBe(text)

    const closedMarker = await waitFor(() => {
      const candidate = Array.from(
        container.querySelectorAll<HTMLElement>('.cm-foldGutter .cm-gutterElement'),
      ).find((element) => element.style.visibility !== 'hidden' && element.textContent?.trim())
      expect(candidate).toBeDefined()
      return candidate
    })
    if (!closedMarker) throw new Error('The folded JIT marker disappeared.')
    fireEvent.mouseDown(closedMarker, { button: 0 })
    fireEvent.click(closedMarker, { button: 0, detail: 1 })
    const mappedLine = await waitFor(() => {
      const candidate = container.querySelector<HTMLElement>('.cm-line.source-association')
      expect(candidate).not.toBeNull()
      return candidate
    })
    if (!mappedLine) throw new Error('The mapped JIT line was not restored after unfolding.')
    editorView.dispatch({ selection: { anchor: 0 } })
    fireEvent.click(mappedLine, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    await waitFor(() => expect(onActivate).toHaveBeenCalledOnce())
  })

  it('loads the Monaco renderer only when Monaco is selected', async () => {
    render(
      <CodeDocumentView
        text="mov eax, 1"
        languageId="asm"
        ariaLabel="Monaco result"
        fontSize={14}
        editorKind="monaco"
      />,
    )

    const output = await screen.findByRole('textbox', { name: 'Monaco result' })
    expect(output).toHaveAttribute('data-output-editor', 'monaco')
    expect(output).toHaveValue('mov eax, 1')
    expect(document.querySelector('.cm-editor')).not.toBeInTheDocument()
  })

  it.each([
    ['il', '.method public static void Main()'],
    ['csharp', 'public static void Main() {}'],
  ])('confines Mod+A to the complete %s result document', (languageId, text) => {
    render(
      <>
        <span>Outside result controls</span>
        <CodeDocumentView
          text={text}
          languageId={languageId}
          ariaLabel="Result code"
          fontSize={14}
        />
      </>,
    )
    const textbox = screen.getByRole('textbox', { name: 'Result code' })
    const editor = textbox.closest<HTMLElement>('.cm-editor')
    expect(editor).not.toBeNull()
    if (!editor) throw new Error('Result editor was not rendered.')
    const editorView = EditorView.findFromDOM(editor)
    expect(editorView).not.toBeNull()
    if (!editorView) throw new Error('Result editor view was not available.')

    expect(editorView.contentDOM.tabIndex).toBe(0)
    expect(editorView.contentDOM).toHaveAttribute('contenteditable', 'false')
    editorView.contentDOM.focus()
    expect(editorView.contentDOM).toHaveFocus()
    const event = new KeyboardEvent('keydown', {
      key: 'a',
      code: 'KeyA',
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    })
    editorView.contentDOM.dispatchEvent(event)

    expect(event.defaultPrevented).toBe(true)
    expect(editorView.state.selection.main.from).toBe(0)
    expect(editorView.state.selection.main.to).toBe(text.length)
    expect(editorView.state.doc.toString()).toBe(text)
    expect(editor.querySelector('.cm-selectionLayer')).not.toBeNull()
  })

  it('remeasures shared font-size changes without recreating the result editor', () => {
    const requestMeasure = vi.spyOn(EditorView.prototype, 'requestMeasure')
    const text = `line 1\n${'long output '.repeat(80)}\nline 3`
    const view = render(
      <CodeDocumentView text={text} languageId="csharp" ariaLabel="Result code" fontSize={14} />,
    )
    const editor = screen.getByRole('textbox', { name: 'Result code' }).closest('.cm-editor')
    const callsBeforeResize = requestMeasure.mock.calls.length

    view.rerender(
      <CodeDocumentView text={text} languageId="csharp" ariaLabel="Result code" fontSize={18} />,
    )

    expect(screen.getByRole('textbox', { name: 'Result code' }).closest('.cm-editor')).toBe(editor)
    expect(document.querySelector('.code-document-view')).toHaveStyle({
      '--code-font-size': '18px',
    })
    expect(requestMeasure.mock.calls.length).toBeGreaterThan(callsBeforeResize)
    expect(screen.getByRole('textbox', { name: 'Result code' })).toHaveTextContent('line 3')
  })

  it('activates a mapped output line without treating text selection as navigation', async () => {
    const onActivate = vi.fn()
    const onHover = vi.fn()
    const scrollIntoView = vi.spyOn(EditorView, 'scrollIntoView')
    const dispatch = vi.spyOn(EditorView.prototype, 'dispatch')
    const association = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 4, startColumn: 5, endLine: 4, endColumn: 18 },
      },
      'JIT source: Program.cs:4',
    )
    const view = render(
      <CodeDocumentView
        text={'Method A:\n  mov eax, 1\n  ret\nMethod B:'}
        languageId="asm"
        ariaLabel="Mapped result"
        fontSize={14}
        lineActions={[
          {
            startLine: 1,
            endLine: 3,
            ariaLabel: 'Open Program.cs:4',
            onActivate,
          },
        ]}
        lineAssociations={[{ startLine: 1, endLine: 3, association }]}
        onAssociationHover={onHover}
      />,
    )
    const textbox = screen.getByRole('textbox', { name: 'Mapped result' })
    const editor = textbox.closest<HTMLElement>('.cm-editor')
    if (!editor) throw new Error('Result editor was not rendered.')
    const editorView = EditorView.findFromDOM(editor)
    if (!editorView) throw new Error('Result editor view was not available.')
    const lines = editor.querySelectorAll<HTMLElement>('.cm-line')
    const instruction = lines[1]
    if (!instruction) throw new Error('Mapped instruction line was not rendered.')

    fireEvent.click(instruction, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    fireEvent.scroll(editorView.scrollDOM)
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()

    fireEvent.click(instruction, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    await waitFor(() => expect(onActivate).toHaveBeenCalledOnce())
    expect(instruction).toHaveClass('source-association')
    fireEvent.mouseMove(instruction)
    expect(onHover).toHaveBeenLastCalledWith(association.key)
    fireEvent.mouseLeave(textbox)
    expect(onHover).toHaveBeenLastCalledWith(null)

    onActivate.mockClear()
    editorView.dispatch({ selection: { anchor: 0, head: 8 } })
    fireEvent.click(instruction, { button: 0, detail: 1, clientX: 40, clientY: 20 })
    expect(onActivate).not.toHaveBeenCalled()

    editorView.dispatch({ selection: { anchor: 0 } })
    fireEvent.click(instruction, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    fireEvent.click(instruction, { button: 0, detail: 2, clientX: 20, clientY: 20 })
    fireEvent.doubleClick(instruction, { button: 0, detail: 2, clientX: 20, clientY: 20 })
    await new Promise((resolve) => window.setTimeout(resolve, 200))
    expect(onActivate).not.toHaveBeenCalled()

    view.rerender(
      <CodeDocumentView
        text={'Method A:\n  mov eax, 1\n  ret\nMethod B:'}
        languageId="asm"
        ariaLabel="Mapped result"
        fontSize={14}
        lineActions={[
          {
            startLine: 1,
            endLine: 3,
            ariaLabel: 'Open Program.cs:4',
            onActivate,
          },
        ]}
        lineAssociations={[{ startLine: 1, endLine: 3, association }]}
        activeAssociationKey={association.key}
        activeAssociationRevision={1}
        onAssociationHover={onHover}
      />,
    )
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledOnce())
    expect(editor.querySelectorAll<HTMLElement>('.cm-line')[1]).toHaveClass(
      'cm-source-association-active',
    )
    const scroller = editor.querySelector<HTMLElement>('.cm-scroller')
    if (!scroller) throw new Error('The result scroller was not rendered.')
    scroller.scrollTop = 120
    const dispatchCallsBeforeEquivalentRerender = dispatch.mock.calls.length

    view.rerender(
      <CodeDocumentView
        text={'Method A:\n  mov eax, 1\n  ret\nMethod B:'}
        languageId="asm"
        ariaLabel="Mapped result"
        fontSize={14}
        lineActions={[
          {
            startLine: 1,
            endLine: 3,
            ariaLabel: 'Open Program.cs:4',
            onActivate,
          },
        ]}
        lineAssociations={[{ startLine: 1, endLine: 3, association }]}
        activeAssociationKey={association.key}
        activeAssociationRevision={1}
        onAssociationHover={onHover}
      />,
    )
    await new Promise((resolve) => window.setTimeout(resolve, 0))
    expect(scrollIntoView).toHaveBeenCalledOnce()
    expect(dispatch).toHaveBeenCalledTimes(dispatchCallsBeforeEquivalentRerender)
    expect(scroller.scrollTop).toBe(120)

    view.rerender(
      <CodeDocumentView
        text={'Method A:\n  mov eax, 1\n  ret\nMethod B:'}
        languageId="asm"
        ariaLabel="Mapped result"
        fontSize={14}
        lineActions={[
          {
            startLine: 1,
            endLine: 3,
            ariaLabel: 'Open Program.cs:4',
            onActivate,
          },
        ]}
        lineAssociations={[{ startLine: 1, endLine: 3, association }]}
        activeAssociationKey={association.key}
        activeAssociationRevision={2}
        onAssociationHover={onHover}
      />,
    )
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledTimes(2))
  })

  it('uses the latest line action and cancels it when the result generation changes', async () => {
    const firstAction = vi.fn()
    const latestAction = vi.fn()
    const props = {
      text: 'Program:Main():\n  ret',
      languageId: 'asm',
      ariaLabel: 'Generation-safe result',
      fontSize: 14 as const,
      generationKey: 'workflow-1',
      lineActions: [
        { startLine: 1, endLine: 2, ariaLabel: 'Open Program.cs:1', onActivate: firstAction },
      ],
    }
    const view = render(<CodeDocumentView {...props} />)
    const editor = screen
      .getByRole('textbox', { name: 'Generation-safe result' })
      .closest<HTMLElement>('.cm-editor')
    if (!editor) throw new Error('Result editor was not rendered.')
    const line = editor.querySelector<HTMLElement>('.cm-line')
    if (!line) throw new Error('Result line was not rendered.')

    fireEvent.click(line, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    view.rerender(
      <CodeDocumentView
        {...props}
        lineActions={[
          { startLine: 1, endLine: 2, ariaLabel: 'Open Program.cs:1', onActivate: latestAction },
        ]}
      />,
    )
    await waitFor(() => expect(latestAction).toHaveBeenCalledOnce())
    expect(firstAction).not.toHaveBeenCalled()

    latestAction.mockClear()
    fireEvent.click(line, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    view.rerender(
      <CodeDocumentView
        {...props}
        generationKey="workflow-2"
        lineActions={[
          { startLine: 1, endLine: 2, ariaLabel: 'Open Program.cs:1', onActivate: latestAction },
        ]}
      />,
    )
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(latestAction).not.toHaveBeenCalled()
  })

  it('reveals a referenced JIT block label with a selection-safe click', async () => {
    const text = 'G_M000_IG01:\n  jne G_M000_IG01\n  ret'
    render(
      <CodeDocumentView text={text} languageId="asm" ariaLabel="Label navigation" fontSize={14} />,
    )
    const textbox = screen.getByRole('textbox', { name: 'Label navigation' })
    const editor = textbox.closest<HTMLElement>('.cm-editor')
    if (!editor) throw new Error('Result editor was not rendered.')
    const editorView = EditorView.findFromDOM(editor)
    if (!editorView) throw new Error('Result editor view was not available.')
    vi.spyOn(editorView, 'posAtCoords').mockReturnValue(text.lastIndexOf('G_M000_IG01') + 2)
    const referenceLine = editor.querySelectorAll<HTMLElement>('.cm-line')[1]
    if (!referenceLine) throw new Error('The JIT branch line was not rendered.')

    fireEvent.click(referenceLine, { button: 0, detail: 1 })

    await waitFor(() =>
      expect(
        editorView.state.sliceDoc(
          editorView.state.selection.main.from,
          editorView.state.selection.main.to,
        ),
      ).toBe('G_M000_IG01'),
    )
    expect(editorView.state.selection.main.from).toBe(0)

    editorView.dispatch({ selection: { anchor: text.length } })
    fireEvent.click(referenceLine, { button: 2, detail: 1 })
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(editorView.state.selection.main.from).toBe(text.length)
  })
})
