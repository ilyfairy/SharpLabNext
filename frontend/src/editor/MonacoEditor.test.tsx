import { act, render, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { OpenLanguageSessionRequest, ResolveSelectionResponse } from '../api/types'
import type { ExecutionFlowSourceModel } from '../results/executionFlowModel'
import { createSourceAssociation } from '../results/sourceAssociationModel'
import type { LspDocumentSymbol } from './lspDocumentSymbols'
import { editorLineHeight, MonacoEditor, type MonacoLanguageSessionOptions } from './MonacoEditor'
import type { SourceMethodSelection } from './sourceMethod'

interface ModelContentEvent {
  changes: readonly {
    range: {
      startLineNumber: number
      startColumn: number
      endLineNumber: number
      endColumn: number
    }
    text: string
  }[]
}

interface MockMouseModifiers {
  altKey?: boolean
  ctrlKey?: boolean
  metaKey?: boolean
  shiftKey?: boolean
}

const mocks = vi.hoisted(() => {
  const sessionUpdates: Array<{
    key: string
    plan: { createRequest: () => OpenLanguageSessionRequest } | null
  } | null> = []
  const models: Array<{
    text: string
    uri: { toString: () => string }
    deltaDecorations: ReturnType<typeof vi.fn>
    dispose: ReturnType<typeof vi.fn>
    getLanguageId: () => string
    getValue: () => string
    getVersionId: () => number
    isDisposed: () => boolean
    onDidChangeContent: (handler: (event: ModelContentEvent) => void) => { dispose: () => void }
    emitContent: (event: ModelContentEvent) => void
    setValue: (value: string) => void
  }> = []
  let activeModel: (typeof models)[number] | null = null
  let cursorHandler:
    | ((event: { position: { lineNumber: number; column: number } }) => void)
    | null = null
  let cursorSelectionHandler:
    | ((event: {
        source: string
        selection: {
          isEmpty: () => boolean
          startLineNumber: number
          startColumn: number
          endLineNumber: number
          endColumn: number
        }
      }) => void)
    | null = null
  let keyDownHandler: (() => void) | null = null
  let mouseUpHandler:
    | ((event: {
        event: { leftButton: boolean; browserEvent: { detail: number } & MockMouseModifiers }
        target: { position: { lineNumber: number; column: number } | null }
      }) => void)
    | null = null
  let mouseDownHandler:
    | ((event: {
        event: { leftButton: boolean; browserEvent: { detail: number } & MockMouseModifiers }
        target: { position: { lineNumber: number; column: number } | null }
      }) => void)
    | null = null
  let languageStatusHandler: ((change: { status: string }) => void) | null = null
  let documentSymbolsHandler:
    | ((change: {
        path: string
        version: number
        symbols: readonly LspDocumentSymbol[] | null
      }) => void)
    | null = null
  const editor = {
    addCommand: vi.fn(
      (_keybinding: number, _handler: () => void, _context?: string) => 'snippet-enter',
    ),
    dispose: vi.fn(),
    focus: vi.fn(),
    getModel: vi.fn(() => activeModel),
    getPosition: vi.fn(() => ({ lineNumber: 1, column: 1 })),
    getSelection: vi.fn((): { isEmpty: () => boolean } => ({ isEmpty: () => true })),
    layout: vi.fn(),
    onDidChangeCursorPosition: vi.fn(
      (handler: (event: { position: { lineNumber: number; column: number } }) => void) => {
        cursorHandler = handler
        return { dispose: vi.fn() }
      },
    ),
    onDidChangeCursorSelection: vi.fn((handler: NonNullable<typeof cursorSelectionHandler>) => {
      cursorSelectionHandler = handler
      return { dispose: vi.fn() }
    }),
    onKeyDown: vi.fn((handler: () => void) => {
      keyDownHandler = handler
      return { dispose: vi.fn() }
    }),
    onMouseDown: vi.fn(
      (
        handler: (event: {
          event: { leftButton: boolean; browserEvent: { detail: number } & MockMouseModifiers }
          target: { position: { lineNumber: number; column: number } | null }
        }) => void,
      ) => {
        mouseDownHandler = handler
        return { dispose: vi.fn() }
      },
    ),
    onMouseUp: vi.fn(
      (
        handler: (event: {
          event: { leftButton: boolean; browserEvent: { detail: number } & MockMouseModifiers }
          target: { position: { lineNumber: number; column: number } | null }
        }) => void,
      ) => {
        mouseUpHandler = handler
        return { dispose: vi.fn() }
      },
    ),
    restoreViewState: vi.fn(),
    revealRangeInCenter: vi.fn(),
    saveViewState: vi.fn(() => null),
    setModel: vi.fn((model) => {
      activeModel = model
    }),
    setPosition: vi.fn(),
    setSelection: vi.fn(),
    trigger: vi.fn(),
    updateOptions: vi.fn(),
  }
  return {
    sessionUpdates,
    createEditor: vi.fn((_container: HTMLElement, _options: Record<string, unknown>) => editor),
    createModel: vi.fn((text: string, languageId: string, uri: { toString: () => string }) => {
      let contentHandler: ((event: ModelContentEvent) => void) | null = null
      const model = {
        text,
        uri,
        deltaDecorations: vi.fn((_old: string[], decorations: unknown[]) =>
          decorations.map((_, index) => `decoration-${index + 1}`),
        ),
        dispose: vi.fn(),
        getLanguageId: () => languageId,
        getValue: () => model.text,
        getVersionId: () => 1,
        isDisposed: () => false,
        onDidChangeContent: (handler: (event: ModelContentEvent) => void) => {
          contentHandler = handler
          return { dispose: vi.fn() }
        },
        emitContent: (event: ModelContentEvent) => contentHandler?.(event),
        setValue: (value: string) => {
          model.text = value
        },
      }
      models.push(model)
      return model
    }),
    editor,
    models,
    bridge: {
      changeDocument: vi.fn(),
      clearEmptyCompletionRetry: vi.fn(),
      consumeEmptyCompletionRetry: vi.fn(() => false),
      createDependencies: vi.fn(() => ({})),
      dispose: vi.fn(),
      onDidChangeDocumentSymbols: vi.fn((handler: typeof documentSymbolsHandler) => {
        documentSymbolsHandler = handler
        return { dispose: vi.fn() }
      }),
      registerDocument: vi.fn(),
      setLanguage: vi.fn(),
      setSessionStatus: vi.fn(),
      unregisterDocument: vi.fn(),
    },
    emitCursor: (lineNumber: number, column: number) =>
      cursorHandler?.({ position: { lineNumber, column } }),
    emitCursorSelection: (startColumn: number, endColumn: number, source = 'mouse') =>
      cursorSelectionHandler?.({
        source,
        selection: {
          isEmpty: () => startColumn === endColumn,
          startLineNumber: 1,
          startColumn,
          endLineNumber: 1,
          endColumn,
        },
      }),
    emitKeyDown: () => keyDownHandler?.(),
    emitModelContent: (model: (typeof models)[number]) =>
      model.emitContent({
        changes: [
          {
            range: {
              startLineNumber: 1,
              startColumn: 1,
              endLineNumber: 1,
              endColumn: 1,
            },
            text: 'x',
          },
        ],
      }),
    emitMouseDown: (
      lineNumber: number,
      column: number,
      detail = 1,
      modifiers: MockMouseModifiers = {},
    ) =>
      mouseDownHandler?.({
        event: { leftButton: true, browserEvent: { detail, ...modifiers } },
        target: { position: { lineNumber, column } },
      }),
    emitMouseUp: (
      lineNumber: number,
      column: number,
      detail = 1,
      modifiers: MockMouseModifiers = {},
    ) =>
      mouseUpHandler?.({
        event: { leftButton: true, browserEvent: { detail, ...modifiers } },
        target: { position: { lineNumber, column } },
      }),
    emitLanguageStatus: (status: string) => languageStatusHandler?.({ status }),
    emitDocumentSymbols: (symbols: readonly LspDocumentSymbol[] | null) =>
      documentSymbolsHandler?.({ path: 'Program.cs', version: 1, symbols }),
    setLanguageStatusHandler: (handler: (change: { status: string }) => void) => {
      languageStatusHandler = handler
    },
    reset: () => {
      sessionUpdates.splice(0)
      models.splice(0)
      activeModel = null
      cursorHandler = null
      cursorSelectionHandler = null
      keyDownHandler = null
      mouseDownHandler = null
      mouseUpHandler = null
      languageStatusHandler = null
      documentSymbolsHandler = null
      mocks.createEditor.mockClear()
      mocks.createModel.mockClear()
      for (const member of Object.values(mocks.bridge)) {
        if (typeof member === 'function' && 'mockClear' in member) member.mockClear()
      }
      for (const member of Object.values(editor)) {
        if (typeof member === 'function' && 'mockClear' in member) member.mockClear()
      }
    },
  }
})

vi.mock('./monacoCore', () => ({
  KeyCode: { Tab: 2, Enter: 3 },
  Range: class {
    readonly startLineNumber: number
    readonly startColumn: number
    readonly endLineNumber: number
    readonly endColumn: number

    constructor(
      startLineNumber: number,
      startColumn: number,
      endLineNumber: number,
      endColumn: number,
    ) {
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
    OverviewRulerLane: { Center: 2 },
    ScrollType: { Smooth: 0 },
    ShowLightbulbIconMode: { On: 'on' },
    TrackedRangeStickiness: { NeverGrowsWhenTypingAtEdges: 1 },
  },
}))

vi.mock('../lsp/languageRegistration', () => ({
  editorLanguageId: (_languageId: string, monacoLanguageId: string) => monacoLanguageId,
  registerSourceLanguages: vi.fn(),
  sourceEditorTheme: 'sharplabnext-light',
}))

vi.mock('../lsp/languageSessionLifecycle', () => ({
  createLanguageSessionKey: vi.fn((input: unknown) => JSON.stringify(input)),
  LanguageSessionLifecycle: class {
    constructor(handler: (change: { status: string }) => void) {
      mocks.setLanguageStatusHandler(handler)
    }
    update = vi.fn(
      (
        desired: {
          key: string
          plan: { createRequest: () => OpenLanguageSessionRequest } | null
        } | null,
      ) => mocks.sessionUpdates.push(desired),
    )
    dispose = vi.fn()
  },
}))

vi.mock('../lsp/monacoLanguageClient', () => ({
  createMonacoLanguageSessionDependencies: mocks.bridge.createDependencies,
  MonacoLanguageBridge: class {
    changeDocument = mocks.bridge.changeDocument
    clearEmptyCompletionRetry = mocks.bridge.clearEmptyCompletionRetry
    consumeEmptyCompletionRetry = mocks.bridge.consumeEmptyCompletionRetry
    dispose = mocks.bridge.dispose
    onDidChangeDocumentSymbols = mocks.bridge.onDidChangeDocumentSymbols
    registerDocument = mocks.bridge.registerDocument
    setLanguage = mocks.bridge.setLanguage
    setSessionStatus = mocks.bridge.setSessionStatus
    unregisterDocument = mocks.bridge.unregisterDocument
  },
}))

const languageSession: MonacoLanguageSessionOptions = {
  enabled: false,
  resolution: null,
  languageId: 'csharp',
  toolchainId: 'roslyn-stable',
  referenceSetId: 'net10-ref',
  buildMode: 'release',
  workspaceRevision: 1,
  selectionRevision: 1,
  sourceOrder: ['Program.cs'],
}

function resolvedLanguageSession(
  kind: 'render' | 'run',
  languageId: 'csharp' | 'il' = 'csharp',
): MonacoLanguageSessionOptions {
  const runtimeId = kind === 'run' ? 'dotnet-10-linux-x64' : null
  const outputId = kind === 'run' ? 'run' : 'il'
  const toolchainId = languageId === 'il' ? 'mobius-ilasm-stable' : 'roslyn-stable'
  const sourceOrder = [languageId === 'il' ? 'Program.il' : 'Program.cs']
  const resolution: ResolveSelectionResponse = {
    effectiveSelection: {
      languageId,
      toolchainId,
      referenceSetId: 'net10-ref',
      outputId,
      runtimeId,
    },
    selectionChanges: [],
    effectiveCapabilities: {
      languageServerCapabilities: ['diagnostics'],
      buildCapabilities: ['managed-pe'],
      outputCapabilities: [outputId],
      runtimeCapabilities: kind === 'run' ? ['run'] : [],
    },
    pipelineResolutionId: `pipeline-${outputId}`,
    pipelinePlan: {
      releaseId: 'test-release',
      languageWorkerId: toolchainId,
      compilerWorkerId: toolchainId,
      referenceSetId: 'net10-ref',
      stages: [
        { id: 'build', kind: 'build', providerId: toolchainId },
        {
          id: outputId,
          kind,
          providerId: runtimeId ?? 'artifacts-default',
        },
      ],
      runtimeId,
      securityPolicyId: runtimeId ? 'runtime-job-default' : 'compiler-default',
      workerImageIds: [],
    },
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  }
  return {
    ...languageSession,
    enabled: true,
    resolution,
    languageId,
    toolchainId,
    sourceOrder,
  }
}

const flow: ExecutionFlowSourceModel = {
  timeline: [],
  hits: [
    {
      key: 'hit-1',
      documentPath: 'Program.cs',
      range: { startLine: 2, startColumn: 1, endLine: 2, endColumn: 7 },
      eventKind: 'sequence-point',
      count: 2,
    },
  ],
}

function editor(
  executionFlow: ExecutionFlowSourceModel | null,
  navigationRevision?: number,
  onCursorMethodChange?: (selection: SourceMethodSelection | null) => void,
  source = 'line 1\nline 2',
) {
  return (
    <MonacoEditor
      files={[{ path: 'Program.cs', text: source }]}
      activeFile="Program.cs"
      monacoLanguageId="csharp"
      languageSession={languageSession}
      executionFlow={executionFlow}
      sourceNavigation={
        navigationRevision === undefined
          ? null
          : {
              documentPath: 'Program.cs',
              range: { startLine: 2, startColumn: 1, endLine: 2, endColumn: 7 },
              revision: navigationRevision,
            }
      }
      fontSize={14}
      onChange={vi.fn()}
      {...(onCursorMethodChange ? { onCursorMethodChange } : {})}
    />
  )
}

describe('Monaco execution-flow presentation', () => {
  beforeEach(() => {
    mocks.reset()
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        disconnect() {}
      },
    )
  })

  afterEach(() => vi.unstubAllGlobals())

  it.each([
    ['render', 'auto'],
    ['run', 'console'],
  ] as const)('opens a %s language session with %s output kind', async (stageKind, outputKind) => {
    const initialSession = resolvedLanguageSession(stageKind)
    const view = render(
      <MonacoEditor
        files={[{ path: 'Program.cs', text: 'class Utility { }' }]}
        activeFile="Program.cs"
        monacoLanguageId="csharp"
        languageSession={initialSession}
        executionFlow={null}
        sourceNavigation={null}
        fontSize={14}
        onChange={vi.fn()}
      />,
    )

    await waitFor(() => expect(mocks.sessionUpdates.some((update) => update?.plan)).toBe(true))
    const initialUpdate = mocks.sessionUpdates.findLast((update) => update?.plan)
    if (!initialUpdate?.plan) throw new Error('Monaco language session plan was not captured.')
    expect(initialUpdate.plan.createRequest().workspace.buildOptions.outputKind).toBe(outputKind)
    mocks.emitLanguageStatus('ready')
    mocks.bridge.setSessionStatus.mockClear()

    const updateCount = mocks.sessionUpdates.length
    view.rerender(
      <MonacoEditor
        files={[{ path: 'Program.cs', text: 'class Utility { int Value; }' }]}
        activeFile="Program.cs"
        monacoLanguageId="csharp"
        languageSession={{ ...initialSession, resolution: null, workspaceRevision: 2 }}
        executionFlow={null}
        sourceNavigation={null}
        fontSize={14}
        onChange={vi.fn()}
      />,
    )
    await waitFor(() => expect(mocks.sessionUpdates.length).toBeGreaterThan(updateCount))
    const unresolvedUpdate = mocks.sessionUpdates.at(-1)
    expect(unresolvedUpdate?.key).toBe(initialUpdate.key)
    expect(unresolvedUpdate?.plan).toBeNull()
    expect(mocks.bridge.setSessionStatus).not.toHaveBeenCalledWith('connecting')

    const unresolvedCount = mocks.sessionUpdates.length
    view.rerender(
      <MonacoEditor
        files={[{ path: 'Program.cs', text: 'class Utility { int Value; }' }]}
        activeFile="Program.cs"
        monacoLanguageId="csharp"
        languageSession={{
          ...initialSession,
          resolution: null,
          workspaceRevision: 2,
          selectionRevision: initialSession.selectionRevision + 1,
        }}
        executionFlow={null}
        sourceNavigation={null}
        fontSize={14}
        onChange={vi.fn()}
      />,
    )
    await waitFor(() => expect(mocks.sessionUpdates.length).toBeGreaterThan(unresolvedCount))
    const revisedUpdate = mocks.sessionUpdates.at(-1)
    expect(revisedUpdate?.key).not.toBe(initialUpdate.key)
    expect(JSON.parse(revisedUpdate?.key ?? '{}')).toEqual(
      expect.objectContaining({ outputKind: 'console', selectionRevision: 2 }),
    )
    view.unmount()
  })

  it('opens a non-Run IL language session as a library', async () => {
    const initialSession = resolvedLanguageSession('render', 'il')
    const view = render(
      <MonacoEditor
        files={[{ path: 'Program.il', text: '.assembly Library {}' }]}
        activeFile="Program.il"
        monacoLanguageId="il"
        languageSession={initialSession}
        executionFlow={null}
        sourceNavigation={null}
        fontSize={14}
        onChange={vi.fn()}
      />,
    )

    await waitFor(() => expect(mocks.sessionUpdates.some((update) => update?.plan)).toBe(true))
    const update = mocks.sessionUpdates.findLast((candidate) => candidate?.plan)
    if (!update?.plan) throw new Error('Monaco IL language session plan was not captured.')
    expect(update.plan.createRequest().workspace.buildOptions.outputKind).toBe('library')
    view.unmount()
  })

  it('applies shared font size changes without recreating the editor', async () => {
    const view = render(editor(null))
    await waitFor(() => expect(mocks.createEditor).toHaveBeenCalledTimes(1))
    expect(mocks.createEditor.mock.calls[0]?.[1]).toEqual(
      expect.objectContaining({ fontSize: 14, lineHeight: 21 }),
    )

    view.rerender(
      <MonacoEditor
        files={[{ path: 'Program.cs', text: 'line 1' }]}
        activeFile="Program.cs"
        monacoLanguageId="csharp"
        languageSession={languageSession}
        executionFlow={null}
        sourceNavigation={null}
        fontSize={18}
        onChange={vi.fn()}
      />,
    )

    await waitFor(() =>
      expect(mocks.editor.updateOptions).toHaveBeenCalledWith({ fontSize: 18, lineHeight: 27 }),
    )
    expect(mocks.createEditor).toHaveBeenCalledTimes(1)
    expect(editorLineHeight(20)).toBe(30)
    view.unmount()
  })

  it('uses native EditContext, language-server suggestions, and no sticky scroll', async () => {
    const view = render(editor(null))
    await waitFor(() => expect(mocks.createEditor).toHaveBeenCalledTimes(1))
    expect(mocks.createEditor.mock.calls[0]?.[1]).toEqual(
      expect.objectContaining({
        editContext: true,
        stickyScroll: { enabled: false },
        wordBasedSuggestions: 'off',
      }),
    )
    for (const keyCode of [3, 2]) {
      expect(mocks.editor.addCommand).toHaveBeenCalledWith(
        keyCode,
        expect.any(Function),
        'inSnippetMode && hasNextTabstop',
      )
      const snippetNavigationHandler = mocks.editor.addCommand.mock.calls.find(
        ([registeredKeyCode]) => registeredKeyCode === keyCode,
      )?.[1]
      if (typeof snippetNavigationHandler !== 'function') {
        throw new Error(`Snippet navigation handler missing for key code ${keyCode}.`)
      }
      snippetNavigationHandler()
    }
    expect(mocks.editor.trigger).toHaveBeenCalledTimes(2)
    expect(mocks.editor.trigger).toHaveBeenNthCalledWith(
      1,
      'keyboard',
      'jumpToNextSnippetPlaceholder',
      null,
    )
    expect(mocks.editor.trigger).toHaveBeenNthCalledWith(
      2,
      'keyboard',
      'jumpToNextSnippetPlaceholder',
      null,
    )
    view.unmount()
  })

  it('reopens suggestions once after an empty manual completion result', async () => {
    const view = render(editor(null))
    await waitFor(() => expect(mocks.createEditor).toHaveBeenCalledTimes(1))
    const model = await waitFor(() => {
      expect(mocks.models).toHaveLength(1)
      return mocks.models[0]
    })
    if (!model) throw new Error('Expected Monaco source model.')

    mocks.bridge.consumeEmptyCompletionRetry.mockReturnValueOnce(true)
    act(() => {
      model.text = 'next'
      mocks.emitModelContent(model)
    })
    await act(async () => {
      await Promise.resolve()
    })

    expect(mocks.editor.trigger).toHaveBeenCalledWith(
      'keyboard',
      'editor.action.triggerSuggest',
      null,
    )
    view.unmount()
  })

  it('uses a compact mobile gutter and restores the desktop folding layout on resize', async () => {
    let matches = true
    const listeners = new Set<() => void>()
    vi.stubGlobal('matchMedia', () => ({
      get matches() {
        return matches
      },
      media: '(max-width: 860px)',
      addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
      removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
    }))

    const view = render(editor(null))
    await waitFor(() => expect(mocks.createEditor).toHaveBeenCalledTimes(1))
    expect(mocks.createEditor.mock.calls[0]?.[1]).toEqual(
      expect.objectContaining({
        glyphMargin: false,
        lineNumbersMinChars: 2,
        folding: true,
        stickyScroll: { enabled: false },
        lineDecorationsWidth: 0,
        showFoldingControls: 'always',
      }),
    )

    matches = false
    act(() => {
      for (const listener of listeners) listener()
    })
    expect(mocks.editor.updateOptions).toHaveBeenLastCalledWith({
      glyphMargin: false,
      lineNumbersMinChars: 3,
      folding: true,
      lineDecorationsWidth: 10,
      showFoldingControls: 'mouseover',
    })

    view.unmount()
    expect(listeners).toHaveLength(0)
  })

  it('preserves the first Monaco edit while the parent local echo is pending', async () => {
    const onChange = vi.fn()
    const source = 'line 1\nline 2'
    const props = {
      files: [{ path: 'Program.cs', text: source }],
      activeFile: 'Program.cs',
      monacoLanguageId: 'csharp',
      languageSession,
      executionFlow: null,
      sourceNavigation: null,
      fontSize: 14 as const,
      onChange,
    }
    const view = render(<MonacoEditor {...props} />)
    const model = await waitFor(() => {
      expect(mocks.models).toHaveLength(1)
      return mocks.models[0]
    })
    if (!model) throw new Error('Expected Monaco source model.')

    act(() => {
      model.text = 'line X\nline 2'
      mocks.emitModelContent(model)
    })
    expect(onChange).toHaveBeenLastCalledWith('Program.cs', 'line X\nline 2')

    // A selection/association update can render once before the store echo.
    // That stale render must not put the old source back into the model.
    view.rerender(<MonacoEditor {...props} />)
    expect(model.getValue()).toBe('line X\nline 2')

    // Once the parent catches up, the guard is cleared and normal external
    // synchronization remains available.
    view.rerender(
      <MonacoEditor {...props} files={[{ path: 'Program.cs', text: 'line X\nline 2' }]} />,
    )
    view.rerender(<MonacoEditor {...props} files={[{ path: 'Program.cs', text: 'server text' }]} />)
    expect(model.getValue()).toBe('server text')
    view.unmount()
  })

  it('cancels a pending source association when Monaco content starts changing', async () => {
    const onActivate = vi.fn()
    const onChange = vi.fn()
    const association = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 6 },
      },
      'JIT source: Program.cs:1',
    )
    const view = render(
      <MonacoEditor
        files={[{ path: 'Program.cs', text: 'a + b' }]}
        activeFile="Program.cs"
        monacoLanguageId="csharp"
        languageSession={languageSession}
        executionFlow={null}
        sourceAssociations={[association]}
        sourceNavigation={null}
        fontSize={14}
        onChange={onChange}
        onSourceAssociationActivate={onActivate}
      />,
    )
    const model = await waitFor(() => {
      expect(mocks.models).toHaveLength(1)
      return mocks.models[0]
    })
    if (!model) throw new Error('Expected Monaco source model.')

    act(() => mocks.emitMouseUp(1, 1))
    await new Promise((resolve) => window.setTimeout(resolve, 10))
    act(() => {
      model.text = 'x + b'
      mocks.emitModelContent(model)
    })
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()
    view.unmount()
  })

  it('renders an aggregated 1-based execution range and clears it', async () => {
    const view = render(editor(flow))
    const model = await waitFor(() => {
      expect(mocks.models).toHaveLength(1)
      return mocks.models[0]
    })
    expect(model).toBeDefined()
    if (!model) throw new Error('Expected Monaco source model.')
    await waitFor(() => expect(model.deltaDecorations).toHaveBeenCalled())
    const flowCall = model.deltaDecorations.mock.calls.find((call) =>
      (call[1] as Array<{ options?: { afterContentClassName?: string } }>).some((decoration) =>
        decoration.options?.afterContentClassName?.includes('execution-flow-count'),
      ),
    )
    expect(flowCall?.[1]).toEqual([
      expect.objectContaining({
        range: { startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 7 },
        options: expect.objectContaining({
          afterContentClassName: 'execution-flow-count execution-flow-count-2',
        }),
      }),
    ])
    const decorations = flowCall?.[1] as Array<{ options: Record<string, unknown> }> | undefined
    expect(decorations?.[0]?.options).not.toHaveProperty('glyphMarginClassName')
    expect(
      document.head.querySelector('[data-sharplabnext-execution-flow]')?.textContent,
    ).toContain('content: "2"')

    view.rerender(editor(null))
    await waitFor(() =>
      expect(
        model.deltaDecorations.mock.calls.some(
          (call) => (call[0] as string[]).includes('decoration-1') && call[1].length === 0,
        ),
      ).toBe(true),
    )
  })

  it('selects and reveals the exact 1-based navigation range', async () => {
    const view = render(editor(flow))
    await waitFor(() => expect(mocks.models).toHaveLength(1))

    view.rerender(editor(flow, 1))

    const range = { startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 7 }
    await waitFor(() => expect(mocks.editor.setSelection).toHaveBeenCalledWith(range))
    expect(mocks.editor.revealRangeInCenter).toHaveBeenCalledWith(range, 0)
    expect(mocks.editor.focus).toHaveBeenCalled()
  })

  it('colors and activates a source association only for a collapsed click', async () => {
    const onActivate = vi.fn()
    const association = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 6 },
      },
      'JIT source: Program.cs:1',
    )
    const props = {
      files: [{ path: 'Program.cs', text: 'a + b' }],
      activeFile: 'Program.cs',
      monacoLanguageId: 'csharp',
      languageSession,
      executionFlow: null,
      sourceAssociations: [association],
      sourceNavigation: null,
      fontSize: 14 as const,
      onChange: vi.fn(),
      onSourceAssociationActivate: onActivate,
    }
    const view = render(<MonacoEditor {...props} />)
    const model = await waitFor(() => {
      expect(mocks.models).toHaveLength(1)
      return mocks.models[0]
    })
    if (!model) throw new Error('Expected Monaco source model.')
    const decorations = await waitFor(() => {
      const candidates = model.deltaDecorations.mock.calls
        .flatMap((call) => call[1] as Array<Record<string, unknown>>)
        .filter((item) => {
          const options = item.options as
            | { className?: string; inlineClassName?: string }
            | undefined
          return (
            options?.className?.includes('source-association') ||
            options?.inlineClassName?.includes('source-association')
          )
        })
      expect(candidates).toHaveLength(2)
      return candidates
    })
    const lineDecoration = decorations.find((item) =>
      String((item.options as { className?: string } | undefined)?.className).includes(
        'monaco-source-association-line',
      ),
    )
    const rangeDecoration = decorations.find((item) =>
      (item.options as { inlineClassName?: string } | undefined)?.inlineClassName?.includes(
        'monaco-source-association-range',
      ),
    )
    expect(lineDecoration).toEqual(
      expect.objectContaining({
        range: {
          startLineNumber: 1,
          startColumn: 1,
          endLineNumber: 1,
          endColumn: 1,
        },
        options: expect.objectContaining({
          isWholeLine: true,
          className: expect.stringContaining('monaco-source-association-line'),
        }),
      }),
    )
    expect(lineDecoration?.options).not.toHaveProperty('inlineClassName')
    expect(rangeDecoration).toEqual(
      expect.objectContaining({
        range: {
          startLineNumber: 1,
          startColumn: 1,
          endLineNumber: 1,
          endColumn: 6,
        },
        options: expect.objectContaining({
          inlineClassName: expect.stringContaining('monaco-source-association-range'),
        }),
      }),
    )
    expect(rangeDecoration?.options).not.toHaveProperty('className')
    expect(rangeDecoration?.options).not.toHaveProperty('hoverMessage')

    act(() => mocks.emitMouseUp(1, 3))
    await waitFor(() => expect(onActivate).toHaveBeenCalledWith(association.key))

    view.rerender(<MonacoEditor {...props} activeSourceAssociationKey={association.key} />)
    await waitFor(() =>
      expect(
        model.deltaDecorations.mock.calls.some((call) =>
          (call[1] as Array<{ options?: { className?: string; inlineClassName?: string } }>).some(
            (decoration) =>
              decoration.options?.className?.includes('monaco-source-association-line-active'),
          ),
        ),
      ).toBe(true),
    )
    expect(
      model.deltaDecorations.mock.calls.some((call) =>
        (call[1] as Array<{ options?: { inlineClassName?: string } }>).some((decoration) =>
          decoration.options?.inlineClassName?.includes('monaco-source-association-exact-active'),
        ),
      ),
    ).toBe(true)

    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => false })
    act(() => mocks.emitMouseUp(1, 3))
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).toHaveBeenCalledOnce()

    onActivate.mockClear()
    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => true })
    act(() => {
      mocks.emitMouseUp(1, 3, 1)
      mocks.emitMouseUp(1, 3, 2)
    })
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()
  })

  it('previews an AST drag selection without activating it again on mouseup', async () => {
    const onActivate = vi.fn()
    const onPreview = vi.fn()
    const onChange = vi.fn()
    const wholeExpression = {
      ...createSourceAssociation(
        {
          documentPath: 'Program.cs',
          range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 6 },
        },
        'AST IdentifierName',
      ),
      presentation: 'active-range' as const,
    }
    const identifier = {
      ...createSourceAssociation(
        {
          documentPath: 'Program.cs',
          range: { startLine: 1, startColumn: 5, endLine: 1, endColumn: 6 },
        },
        'AST IdentifierName',
      ),
      presentation: 'active-range' as const,
    }
    const props = {
      files: [{ path: 'Program.cs', text: 'a + b' }],
      activeFile: 'Program.cs',
      monacoLanguageId: 'csharp',
      languageSession,
      executionFlow: null,
      sourceAssociations: [wholeExpression, identifier],
      sourceNavigation: null,
      fontSize: 14 as const,
      onChange,
      onSourceAssociationActivate: onActivate,
      onSourceAssociationPreview: onPreview,
    }
    const view = render(<MonacoEditor {...props} />)
    const model = await waitFor(() => {
      expect(mocks.models).toHaveLength(1)
      return mocks.models[0]
    })
    if (!model) throw new Error('Expected Monaco source model.')

    await waitFor(() => expect(model.deltaDecorations).toHaveBeenCalled())
    expect(model.deltaDecorations.mock.calls.flatMap((call) => call[1] as unknown[])).toHaveLength(
      0,
    )

    act(() => {
      mocks.emitMouseDown(1, 1)
      mocks.emitCursorSelection(1, 6)
    })
    expect(onPreview).toHaveBeenCalledWith(wholeExpression.key)
    act(() => mocks.emitCursorSelection(1, 6))
    expect(onPreview).toHaveBeenCalledOnce()

    mocks.editor.getSelection.mockReturnValue({
      isEmpty: () => false,
      startLineNumber: 1,
      startColumn: 1,
      endLineNumber: 1,
      endColumn: 6,
    } as never)
    act(() => mocks.emitMouseUp(1, 6))
    await new Promise((resolve) => window.setTimeout(resolve, 10))
    expect(onActivate).not.toHaveBeenCalled()

    act(() => {
      model.text = 'x'
      mocks.emitModelContent(model)
    })
    expect(onChange).toHaveBeenCalledOnce()
    expect(onChange).toHaveBeenLastCalledWith('Program.cs', 'x')
    view.rerender(<MonacoEditor {...props} />)
    expect(model.getValue()).toBe('x')

    mocks.editor.getSelection.mockReturnValue({
      isEmpty: () => false,
      startLineNumber: 1,
      startColumn: 1,
      endLineNumber: 1,
      endColumn: 6,
    } as never)
    act(() => mocks.emitMouseUp(1, 5))
    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => true } as never)
    mocks.editor.getPosition.mockReturnValue({ lineNumber: 1, column: 5 })
    await waitFor(() => expect(onActivate).toHaveBeenCalledWith(identifier.key))
    expect(onActivate).toHaveBeenCalledOnce()
    expect(mocks.editor.setSelection).not.toHaveBeenCalled()

    mocks.editor.getSelection.mockReturnValue({
      isEmpty: () => false,
      startLineNumber: 1,
      startColumn: 1,
      endLineNumber: 1,
      endColumn: 6,
    } as never)
    act(() => {
      mocks.emitMouseDown(1, 3)
      mocks.emitMouseUp(1, 3)
    })
    expect(mocks.editor.setPosition).toHaveBeenCalledWith({ lineNumber: 1, column: 3 })

    mocks.editor.setPosition.mockClear()
    act(() => {
      mocks.emitMouseDown(1, 3, 1, { shiftKey: true })
      mocks.emitMouseUp(1, 3, 1, { shiftKey: true })
    })
    expect(mocks.editor.setPosition).not.toHaveBeenCalled()
    mocks.editor.getSelection.mockReturnValue({ isEmpty: () => true } as never)

    view.rerender(<MonacoEditor {...props} activeSourceAssociationKey={identifier.key} />)
    await waitFor(() => {
      const latest = model.deltaDecorations.mock.calls.at(-1)?.[1] as
        | Array<{ options?: { className?: string; inlineClassName?: string } }>
        | undefined
      expect(latest).toHaveLength(0)
    })
  })

  it('uses LSP document symbols for multiline current-method selection', async () => {
    const onCursorMethodChange = vi.fn()
    mocks.editor.getPosition.mockReturnValue({ lineNumber: 5, column: 9 })
    const symbols: readonly LspDocumentSymbol[] = [
      {
        name: 'Compute',
        kind: 6,
        range: { start: { line: 1, character: 4 }, end: { line: 6, character: 5 } },
        selectionRange: { start: { line: 2, character: 4 }, end: { line: 2, character: 11 } },
        children: [],
      },
    ]
    render(
      editor(
        null,
        undefined,
        onCursorMethodChange,
        'class C\n{\n    int Compute(\n        int value)\n    {\n        return value;\n    }\n}',
      ),
    )
    await waitFor(() => expect(mocks.models).toHaveLength(1))

    mocks.emitLanguageStatus('ready')
    mocks.emitDocumentSymbols(symbols)
    mocks.emitCursor(5, 9)

    await waitFor(() =>
      expect(onCursorMethodChange).toHaveBeenLastCalledWith({ name: 'Compute', lineNumber: 3 }),
    )
  })
})
