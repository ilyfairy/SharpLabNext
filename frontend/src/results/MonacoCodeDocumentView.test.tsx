import { act, render, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MonacoCodeDocumentView } from './MonacoCodeDocumentView'
import { createSourceAssociation } from './sourceAssociationModel'

const mocks = vi.hoisted(() => {
  let mouseUp: ((event: unknown) => void) | null = null
  let mouseMove: ((event: unknown) => void) | null = null
  let mouseLeave: (() => void) | null = null
  let scrollChange: ((event: { scrollTopChanged: boolean; scrollLeftChanged: boolean }) => void) | null = null
  let foldingProvider: {
    provideFoldingRanges: (model: unknown) => readonly unknown[] | null
  } | null = null
  let semanticTokensProvider: {
    provideDocumentSemanticTokens: (model: unknown, lastResultId: string | null, token: { isCancellationRequested: boolean }) => { data: Uint32Array } | null
  } | null = null
  let hoverProvider: {
    provideHover: (model: unknown, position: { lineNumber: number; column: number }, token: { isCancellationRequested: boolean }) => Promise<unknown>
  } | null = null
  const foldingProviderDispose = vi.fn()
  const registerFoldingRangeProvider = vi.fn(
    (
      _languageId: string,
      provider: {
        provideFoldingRanges: (model: unknown) => readonly unknown[] | null
      },
    ) => {
      foldingProvider = provider
      return { dispose: foldingProviderDispose }
    },
  )
  const semanticProviderDispose = vi.fn()
  const registerDocumentSemanticTokensProvider = vi.fn((_selector: unknown, provider: typeof semanticTokensProvider) => {
    semanticTokensProvider = provider
    return { dispose: semanticProviderDispose }
  })
  const hoverProviderDispose = vi.fn()
  const registerHoverProvider = vi.fn((_selector: unknown, provider: typeof hoverProvider) => {
    hoverProvider = provider
    return { dispose: hoverProviderDispose }
  })
  const ilHover = vi.fn()
  let ilSemanticTokens = [
    {
      line: 0,
      character: 0,
      length: 7,
      tokenType: 'typeParameter',
      tokenModifiers: [],
    },
  ]
  const model = {
    value: '',
    languageId: 'asm',
    dispose: vi.fn(),
    getLanguageId: vi.fn(() => model.languageId),
    getWordAtPosition: vi.fn((): { word: string; startColumn: number; endColumn: number } | null => null),
    findMatches: vi.fn((): Array<{ range: object }> => []),
    getLineContent: vi.fn((line: number) => model.value.split('\n')[line - 1] ?? ''),
    getLineCount: vi.fn(() => model.value.split('\n').length),
    getLineMaxColumn: vi.fn((line: number) => (model.value.split('\n')[line - 1]?.length ?? 0) + 1),
    getVersionId: vi.fn(() => 1),
    getValue: vi.fn(() => model.value),
    setValue: vi.fn((value: string) => {
      model.value = value
    }),
  }
  const editor = {
    deltaDecorations: vi.fn((_old: string[], decorations: unknown[]) => decorations.map((_, index) => `decoration-${index}`)),
    dispose: vi.fn(),
    getSelection: vi.fn((): { isEmpty: () => boolean } => ({
      isEmpty: () => true,
    })),
    layout: vi.fn(),
    onDidScrollChange: vi.fn((handler: (event: { scrollTopChanged: boolean; scrollLeftChanged: boolean }) => void) => {
      scrollChange = handler
      return { dispose: vi.fn() }
    }),
    onMouseLeave: vi.fn((handler: () => void) => {
      mouseLeave = handler
      return { dispose: vi.fn() }
    }),
    onMouseMove: vi.fn((handler: (event: unknown) => void) => {
      mouseMove = handler
      return { dispose: vi.fn() }
    }),
    onMouseUp: vi.fn((handler: (event: unknown) => void) => {
      mouseUp = handler
      return { dispose: vi.fn() }
    }),
    revealLineInCenter: vi.fn(),
    revealRangeInCenter: vi.fn(),
    setSelection: vi.fn(),
    focus: vi.fn(),
    updateOptions: vi.fn(),
  }
  const createEditor = vi.fn((_host: HTMLElement, _options: Record<string, unknown>) => editor)
  const createModel = vi.fn((value: string, languageId: string) => {
    model.value = value
    model.languageId = languageId
    return model
  })
  return {
    createEditor,
    createModel,
    editor,
    model,
    emitMouseMove: (lineNumber: number | null) =>
      mouseMove?.({
        target: { position: lineNumber ? { lineNumber, column: 1 } : null },
      }),
    emitMouseLeave: () => mouseLeave?.(),
    emitScroll: () => scrollChange?.({ scrollTopChanged: true, scrollLeftChanged: false }),
    emitMouseUp: (lineNumber: number, detail = 1, ctrlKey = false, targetType = 6) =>
      mouseUp?.({
        event: {
          leftButton: true,
          ctrlKey,
          metaKey: false,
          browserEvent: { detail },
        },
        target: { position: { lineNumber, column: 1 }, type: targetType },
      }),
    foldingRanges: (model: unknown) => foldingProvider?.provideFoldingRanges(model) ?? null,
    foldingProviderDispose,
    registerFoldingRangeProvider,
    registerDocumentSemanticTokensProvider,
    registerHoverProvider,
    semanticTokens: (candidate: unknown) =>
      semanticTokensProvider?.provideDocumentSemanticTokens(candidate, null, {
        isCancellationRequested: false,
      }) ?? null,
    hoverAt: (candidate: unknown, lineNumber: number, column: number) => hoverProvider?.provideHover(candidate, { lineNumber, column }, { isCancellationRequested: false }) ?? Promise.resolve(null),
    ilHover,
    get ilSemanticTokens() {
      return ilSemanticTokens
    },
    refreshIlSemanticTokens: () => {
      ilSemanticTokens = [...ilSemanticTokens]
    },
    semanticProviderDispose,
    hoverProviderDispose,
    reset: () => {
      mouseUp = null
      mouseMove = null
      mouseLeave = null
      scrollChange = null
      foldingProvider = null
      semanticTokensProvider = null
      hoverProvider = null
      ilSemanticTokens = [
        {
          line: 0,
          character: 0,
          length: 7,
          tokenType: 'typeParameter',
          tokenModifiers: [],
        },
      ]
      ilHover.mockReset()
      for (const value of [
        createEditor,
        createModel,
        registerFoldingRangeProvider,
        registerDocumentSemanticTokensProvider,
        registerHoverProvider,
        foldingProviderDispose,
        semanticProviderDispose,
        hoverProviderDispose,
        ...Object.values(editor),
        ...Object.values(model),
      ]) {
        if (typeof value === 'function' && 'mockClear' in value) value.mockClear()
      }
    },
  }
})

vi.mock('../editor/monacoCore', () => ({
  Range: class {
    readonly startLineNumber: number
    readonly startColumn: number
    readonly endLineNumber: number
    readonly endColumn: number

    static lift(range: object) {
      return range
    }

    constructor(startLineNumber: number, startColumn: number, endLineNumber: number, endColumn: number) {
      this.startLineNumber = startLineNumber
      this.startColumn = startColumn
      this.endLineNumber = endLineNumber
      this.endColumn = endColumn
    }
  },
  Uri: { parse: (value: string) => ({ toString: () => value }) },
  editor: {
    create: mocks.createEditor,
    createModel: mocks.createModel,
    setModelLanguage: vi.fn(),
    MouseTargetType: {
      GUTTER_GLYPH_MARGIN: 2,
      GUTTER_LINE_NUMBERS: 3,
      GUTTER_LINE_DECORATIONS: 4,
      GUTTER_VIEW_ZONE: 5,
      CONTENT_TEXT: 6,
    },
  },
  languages: {
    FoldingRangeKind: { Region: { value: 'region' } },
    registerFoldingRangeProvider: mocks.registerFoldingRangeProvider,
    registerDocumentSemanticTokensProvider: mocks.registerDocumentSemanticTokensProvider,
    registerHoverProvider: mocks.registerHoverProvider,
  },
}))

vi.mock('./ilOutputLanguageSession', () => ({
  useIlOutputLanguageSession: () => ({
    semanticTokens: mocks.ilSemanticTokens,
    status: 'ready',
    hover: mocks.ilHover,
  }),
}))

vi.mock('../lsp/languageRegistration', () => ({
  registerSourceLanguages: vi.fn(),
  sourceEditorTheme: 'sharplabnext-light',
}))

describe('MonacoCodeDocumentView', () => {
  beforeEach(() => {
    mocks.reset()
    vi.stubGlobal('matchMedia', () => ({ matches: false }))
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        disconnect() {}
      },
    )
  })

  afterEach(() => vi.unstubAllGlobals())

  it('disables the output minimap on a compact mobile viewport', async () => {
    vi.stubGlobal('matchMedia', () => ({ matches: true }))
    render(<MonacoCodeDocumentView text="ret" languageId="asm" ariaLabel="Mobile assembly" fontSize={14} />)

    await waitFor(() => expect(mocks.createEditor).toHaveBeenCalledOnce())
    expect(mocks.createEditor.mock.calls[0]?.[1]).toEqual(expect.objectContaining({ minimap: { enabled: false } }))
  })

  it('provides generated IL semantic tokens and hover for its read-only model only', async () => {
    mocks.ilHover.mockResolvedValue({
      contents: { kind: 'markdown', value: '```il\n[System.Console]\n```' },
      range: {
        start: { line: 0, character: 0 },
        end: { line: 0, character: 7 },
      },
    })
    const view = render(
      <MonacoCodeDocumentView
        text="Console"
        languageId="il"
        ariaLabel="Generated IL"
        fontSize={14}
        generationKey="generation-1"
        ilOutputLanguageSessionOptions={{
          catalogRevision: 'catalog-1',
          referenceSetId: 'net10-ref',
          buildMode: 'debug',
          workspaceRevision: 1,
          selectionRevision: 1,
        }}
      />,
    )

    await waitFor(() => expect(mocks.registerDocumentSemanticTokensProvider).toHaveBeenCalledOnce())
    expect(mocks.registerDocumentSemanticTokensProvider.mock.calls[0]?.[0]).toEqual(expect.objectContaining({ language: 'il', exclusive: true }))
    expect(mocks.semanticTokens(mocks.model)?.data).toEqual(new Uint32Array([0, 0, 7, 6, 0]))
    expect(mocks.semanticTokens({})).toBeNull()

    mocks.refreshIlSemanticTokens()
    view.rerender(
      <MonacoCodeDocumentView
        text="Console"
        languageId="il"
        ariaLabel="Generated IL"
        fontSize={14}
        generationKey="generation-1"
        ilOutputLanguageSessionOptions={{
          catalogRevision: 'catalog-1',
          referenceSetId: 'net10-ref',
          buildMode: 'debug',
          workspaceRevision: 1,
          selectionRevision: 1,
        }}
      />,
    )
    await waitFor(() => expect(mocks.registerDocumentSemanticTokensProvider).toHaveBeenCalledTimes(2))
    expect(mocks.semanticProviderDispose).toHaveBeenCalledOnce()

    await waitFor(() => expect(mocks.registerHoverProvider).toHaveBeenCalledOnce())
    const hover = await mocks.hoverAt(mocks.model, 1, 4)
    expect(mocks.ilHover).toHaveBeenCalledWith({ line: 0, character: 3 })
    expect(hover).toEqual({
      contents: [
        {
          value: '```il\n[System.Console]\n```',
          isTrusted: false,
          supportHtml: false,
        },
      ],
      range: expect.objectContaining({
        startLineNumber: 1,
        startColumn: 1,
        endLineNumber: 1,
        endColumn: 8,
      }),
    })
    expect(mocks.createEditor.mock.calls[0]?.[1]).toEqual(
      expect.objectContaining({
        readOnly: true,
        domReadOnly: true,
        'semanticHighlighting.enabled': true,
      }),
    )
  })

  it('renders read-only mapped output with hover, safe click, and active reveal', async () => {
    const onActivate = vi.fn()
    const onHover = vi.fn()
    const association = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 3, startColumn: 1, endLine: 3, endColumn: 10 },
      },
      'JIT source: Program.cs:3',
    )
    const props = {
      text: 'Program:Main():\n  mov eax, 1\n  ret',
      languageId: 'asm',
      ariaLabel: 'JIT assembly',
      fontSize: 14 as const,
      lineAssociations: [{ startLine: 2, endLine: 2, association }],
      lineActions: [
        {
          startLine: 2,
          endLine: 2,
          ariaLabel: 'Open Program.cs:3',
          onActivate,
        },
      ],
      onAssociationHover: onHover,
    }
    const view = render(<MonacoCodeDocumentView {...props} />)

    await waitFor(() => expect(mocks.createEditor).toHaveBeenCalledOnce())
    expect(mocks.createEditor.mock.calls[0]?.[1]).toEqual(
      expect.objectContaining({
        readOnly: true,
        domReadOnly: true,
        ariaLabel: 'JIT assembly',
        minimap: { enabled: true },
        folding: true,
        showFoldingControls: 'always',
        lineDecorationsWidth: 7,
      }),
    )
    await waitFor(() => expect(mocks.registerFoldingRangeProvider).toHaveBeenCalledWith('asm', expect.objectContaining({ provideFoldingRanges: expect.any(Function) })))
    expect(mocks.foldingRanges(mocks.model)).toEqual([{ start: 1, end: 3, kind: { value: 'region' } }])
    expect(mocks.foldingRanges({})).toEqual([])
    await waitFor(() => expect(mocks.editor.deltaDecorations).toHaveBeenCalled())
    expect(mocks.editor.deltaDecorations.mock.calls.at(-1)?.[1]).toEqual([
      expect.objectContaining({
        options: expect.objectContaining({
          className: expect.stringContaining('source-association'),
        }),
      }),
    ])
    const latestDecorations = mocks.editor.deltaDecorations.mock.calls.at(-1)?.[1] as Array<{ options?: Record<string, unknown> }> | undefined
    expect(latestDecorations?.[0]?.options).not.toHaveProperty('hoverMessage')

    act(() => mocks.emitMouseMove(2))
    expect(onHover).toHaveBeenLastCalledWith(association.key)
    act(() => mocks.emitMouseLeave())
    expect(onHover).toHaveBeenLastCalledWith(null)

    act(() => mocks.emitMouseUp(2, 1, false, 4))
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()

    act(() => {
      mocks.emitMouseUp(2)
      mocks.emitScroll()
    })
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()

    act(() => mocks.emitMouseUp(2))
    await waitFor(() => expect(onActivate).toHaveBeenCalledOnce())

    onActivate.mockClear()
    act(() => {
      mocks.emitMouseUp(2, 1)
      mocks.emitMouseUp(2, 2)
    })
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()

    view.rerender(<MonacoCodeDocumentView {...props} activeAssociationKey={association.key} activeAssociationRevision={1} />)
    await waitFor(() => expect(mocks.editor.revealLineInCenter).toHaveBeenCalledWith(2))
    expect(mocks.editor.deltaDecorations.mock.calls.at(-1)?.[1]).toEqual([
      expect.objectContaining({
        options: expect.objectContaining({
          className: expect.stringContaining('monaco-output-source-active'),
        }),
      }),
    ])
    view.rerender(<MonacoCodeDocumentView {...props} lineAssociations={[...props.lineAssociations]} activeAssociationKey={association.key} activeAssociationRevision={1} />)
    await new Promise((resolve) => window.setTimeout(resolve, 0))
    expect(mocks.editor.revealLineInCenter).toHaveBeenCalledOnce()
    view.rerender(<MonacoCodeDocumentView {...props} activeAssociationKey={association.key} activeAssociationRevision={2} />)
    await waitFor(() => expect(mocks.editor.revealLineInCenter).toHaveBeenCalledTimes(2))

    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => false })
    act(() => mocks.emitMouseUp(2))
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()

    const labelRange = {
      startLineNumber: 1,
      startColumn: 1,
      endLineNumber: 1,
      endColumn: 12,
    }
    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => true })
    mocks.model.getWordAtPosition.mockReturnValue({
      word: 'G_M000_IG01',
      startColumn: 7,
      endColumn: 18,
    })
    mocks.model.findMatches.mockReturnValue([{ range: labelRange }])
    act(() => mocks.emitMouseUp(2))
    await waitFor(() => expect(mocks.editor.setSelection).toHaveBeenCalledWith(labelRange))
    expect(mocks.editor.revealRangeInCenter).toHaveBeenCalledWith(labelRange)

    mocks.editor.setSelection.mockClear()
    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => false })
    act(() => mocks.emitMouseUp(2))
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(mocks.editor.setSelection).not.toHaveBeenCalled()
    view.unmount()
    expect(mocks.foldingProviderDispose).toHaveBeenCalledOnce()
  })

  it('uses the latest line action and cancels it when the result generation changes', async () => {
    const firstAction = vi.fn()
    const latestAction = vi.fn()
    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => true })
    mocks.model.getWordAtPosition.mockReturnValue(null)
    const props = {
      text: 'Program:Main():\n  ret',
      languageId: 'asm',
      ariaLabel: 'Generation-safe assembly',
      fontSize: 14 as const,
      generationKey: 'workflow-1',
      lineActions: [
        {
          startLine: 1,
          endLine: 2,
          ariaLabel: 'Open Program.cs:1',
          onActivate: firstAction,
        },
      ],
    }
    const view = render(<MonacoCodeDocumentView {...props} />)
    await waitFor(() => expect(mocks.createEditor).toHaveBeenCalledOnce())

    act(() => mocks.emitMouseUp(1))
    view.rerender(
      <MonacoCodeDocumentView
        {...props}
        lineActions={[
          {
            startLine: 1,
            endLine: 2,
            ariaLabel: 'Open Program.cs:1',
            onActivate: latestAction,
          },
        ]}
      />,
    )
    await waitFor(() => expect(latestAction).toHaveBeenCalledOnce())
    expect(firstAction).not.toHaveBeenCalled()

    latestAction.mockClear()
    act(() => mocks.emitMouseUp(1))
    view.rerender(
      <MonacoCodeDocumentView
        {...props}
        generationKey="workflow-2"
        lineActions={[
          {
            startLine: 1,
            endLine: 2,
            ariaLabel: 'Open Program.cs:1',
            onActivate: latestAction,
          },
        ]}
      />,
    )
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(latestAction).not.toHaveBeenCalled()
  })
})
