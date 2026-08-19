import {
  autocompletion,
  CompletionContext,
  completionStatus,
  pickedCompletion,
} from '@codemirror/autocomplete'
import { indentUnit, syntaxHighlighting } from '@codemirror/language'
import { EditorState, Text, type Transaction } from '@codemirror/state'
import { EditorView, keymap, runScopeHandlers } from '@codemirror/view'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { OpenLanguageSessionRequest, ResolveSelectionResponse } from '../api/types'
import {
  CodeMirrorLanguageBridge,
  type CodeMirrorLspCodeAction,
  type CodeMirrorLspCompletionItem,
  type CodeMirrorLspDiagnostic,
  type CodeMirrorSemanticToken,
} from '../lsp/codeMirrorLanguageClient'
import { createSourceAssociation } from '../results/sourceAssociationModel'
import {
  appendCodeMirrorHoverSections,
  CodeMirrorEditor,
  type CodeMirrorEditorProps,
  codeMirrorCompletion,
  codeMirrorCompletionTriggerCharacter,
  codeMirrorCompletionTriggerKind,
  codeMirrorCompletionValidFor,
  codeMirrorDiagnostics,
  codeMirrorEditorKeymap,
  codeMirrorFoldingRanges,
  codeMirrorHoverSections,
  codeMirrorTextChanges,
  completionSource,
  semanticDecorationRanges,
  signaturePresentation,
} from './CodeMirrorEditor'
import { semanticDecorationExtension, setSemanticDecorations } from './codeMirrorDecorations'
import { codeMirrorLanguageExtension, visualStudioLightHighlightStyle } from './codeMirrorLanguage'

const languageSessionMocks = vi.hoisted(() => ({
  sessionUpdates: [] as Array<{
    key: string
    plan: { createRequest: () => OpenLanguageSessionRequest } | null
  } | null>,
}))

vi.mock('../lsp/languageSessionLifecycle', () => ({
  createLanguageSessionKey: vi.fn((input: unknown) => JSON.stringify(input)),
  LanguageSessionLifecycle: class {
    update = vi.fn(
      (
        desired: {
          key: string
          plan: { createRequest: () => OpenLanguageSessionRequest } | null
        } | null,
      ) => languageSessionMocks.sessionUpdates.push(desired),
    )
    dispose = vi.fn()
  },
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

describe('CodeMirror editor LSP projections', () => {
  it.each([
    ['render', 'auto'],
    ['run', 'console'],
  ] as const)('opens a %s language session with %s output kind', async (stageKind, outputKind) => {
    languageSessionMocks.sessionUpdates.splice(0)
    const props = editorProps('Program.cs', vi.fn())
    const initialSession = resolvedLanguageSession(stageKind)
    props.languageSession = initialSession
    const view = render(<CodeMirrorEditor {...props} />)

    await waitFor(() =>
      expect(languageSessionMocks.sessionUpdates.some((update) => update?.plan)).toBe(true),
    )
    const initialUpdate = languageSessionMocks.sessionUpdates.findLast((update) => update?.plan)
    if (!initialUpdate?.plan) throw new Error('CodeMirror language session plan was not captured.')
    expect(initialUpdate.plan.createRequest().workspace.buildOptions.outputKind).toBe(outputKind)

    const updateCount = languageSessionMocks.sessionUpdates.length
    view.rerender(
      <CodeMirrorEditor
        {...props}
        files={props.files.map((file) =>
          file.path === 'Program.cs' ? { ...file, text: 'class Utility { int Value; }' } : file,
        )}
        languageSession={{ ...initialSession, resolution: null, workspaceRevision: 2 }}
      />,
    )
    await waitFor(() =>
      expect(languageSessionMocks.sessionUpdates.length).toBeGreaterThan(updateCount),
    )
    const unresolvedUpdate = languageSessionMocks.sessionUpdates.at(-1)
    expect(unresolvedUpdate?.key).toBe(initialUpdate.key)
    expect(unresolvedUpdate?.plan).toBeNull()

    const unresolvedCount = languageSessionMocks.sessionUpdates.length
    view.rerender(
      <CodeMirrorEditor
        {...props}
        files={props.files.map((file) =>
          file.path === 'Program.cs' ? { ...file, text: 'class Utility { int Value; }' } : file,
        )}
        languageSession={{
          ...initialSession,
          resolution: null,
          workspaceRevision: 2,
          selectionRevision: initialSession.selectionRevision + 1,
        }}
      />,
    )
    await waitFor(() =>
      expect(languageSessionMocks.sessionUpdates.length).toBeGreaterThan(unresolvedCount),
    )
    const revisedUpdate = languageSessionMocks.sessionUpdates.at(-1)
    expect(revisedUpdate?.key).not.toBe(initialUpdate.key)
    expect(JSON.parse(revisedUpdate?.key ?? '{}')).toEqual(
      expect.objectContaining({ outputKind: 'console', selectionRevision: 2 }),
    )
    view.unmount()
  })

  it('opens a non-Run IL language session as a library', async () => {
    languageSessionMocks.sessionUpdates.splice(0)
    const props = editorProps('Program.il', vi.fn())
    props.files = [{ path: 'Program.il', text: '.assembly Library {}' }]
    props.languageSession = resolvedLanguageSession('render', 'il')
    const view = render(<CodeMirrorEditor {...props} />)

    await waitFor(() =>
      expect(languageSessionMocks.sessionUpdates.some((update) => update?.plan)).toBe(true),
    )
    const update = languageSessionMocks.sessionUpdates.findLast((candidate) => candidate?.plan)
    if (!update?.plan) throw new Error('CodeMirror IL language session plan was not captured.')
    expect(update.plan.createRequest().workspace.buildOptions.outputKind).toBe('library')
    view.unmount()
  })

  it('requeries incomplete server completion lists as the word narrows', () => {
    expect(codeMirrorCompletionValidFor(true)).toBeUndefined()
    expect(codeMirrorCompletionValidFor(false, 0)).toBeUndefined()
    expect(codeMirrorCompletionValidFor(false)?.test('svm')).toBe(true)
  })

  it('uses ILSense completion triggers except ordinary spaces only for IL', () => {
    const triggers = ['.', '[', ']', ':', "'", '(', ',', '<', '!']
    expect(triggers.map((trigger) => codeMirrorCompletionTriggerCharacter('il', trigger))).toEqual(
      triggers,
    )
    expect(codeMirrorCompletionTriggerCharacter('il', ' ')).toBeUndefined()
    expect(codeMirrorCompletionTriggerCharacter('il', ';')).toBeUndefined()

    expect(codeMirrorCompletionTriggerCharacter('csharp', ':')).toBe(':')
    expect(codeMirrorCompletionTriggerCharacter('csharp', '[')).toBeUndefined()
  })

  it('does not request completion for an ordinary space but allows explicit Ctrl+Space', async () => {
    const bridge = new CodeMirrorLanguageBridge()
    const completion = vi.spyOn(bridge, 'completion').mockResolvedValue({
      isIncomplete: false,
      items: [],
    })
    const source = completionSource(
      'Program.il',
      () => 'il',
      bridge,
      () => 1,
    )
    const state = EditorState.create({ doc: '.class ' })

    await expect(source(new CompletionContext(state, state.doc.length, false))).resolves.toBeNull()
    expect(completion).not.toHaveBeenCalled()

    await source(new CompletionContext(state, state.doc.length, true))
    expect(completion).toHaveBeenCalledOnce()
    expect(completion).toHaveBeenCalledWith(
      'Program.il',
      expect.objectContaining({ triggerKind: 1 }),
    )
    expect(completion.mock.calls[0]?.[1]).not.toHaveProperty('triggerCharacter')
  })

  it('reads the current language when a mounted CodeMirror source changes language', async () => {
    const bridge = new CodeMirrorLanguageBridge()
    const completion = vi
      .spyOn(bridge, 'completion')
      .mockResolvedValueOnce({ isIncomplete: true, items: [] })
      .mockResolvedValue({ isIncomplete: false, items: [] })
    let languageId = 'csharp'
    const source = completionSource(
      'Program.il',
      () => languageId,
      bridge,
      () => 1,
    )

    await source(new CompletionContext(EditorState.create({ doc: 's' }), 1, false))
    languageId = 'il'
    await source(new CompletionContext(EditorState.create({ doc: 'x' }), 1, false))
    await source(new CompletionContext(EditorState.create({ doc: '[' }), 1, false))

    expect(completion).toHaveBeenNthCalledWith(
      2,
      'Program.il',
      expect.objectContaining({ triggerKind: 1 }),
    )
    expect(completion).toHaveBeenNthCalledWith(
      3,
      'Program.il',
      expect.objectContaining({ triggerKind: 2, triggerCharacter: '[' }),
    )
  })

  it('uses the LSP incomplete-list trigger for CodeMirror follow-up queries', () => {
    expect(codeMirrorCompletionTriggerKind('il', 's', false, true)).toBe(3)
    expect(codeMirrorCompletionTriggerKind('il', 's', true, true)).toBe(1)
    expect(codeMirrorCompletionTriggerKind('il', '.', true, true)).toBe(1)
    expect(codeMirrorCompletionTriggerKind('csharp', '.', true, false)).toBe(1)
    expect(codeMirrorCompletionTriggerKind('il', '.', false, true)).toBe(2)
    expect(codeMirrorCompletionTriggerKind('il', 's', false, false)).toBe(1)
  })

  it('uses LSP filterText for matching while retaining the display label', () => {
    const item: CodeMirrorLspCompletionItem = {
      label: '[System.Runtime] System.Runtime',
      filterText: 'System.Runtime',
      sortText: '0001:System.Runtime',
      raw: { label: '[System.Runtime] System.Runtime' },
      documentVersion: 1,
    }
    const completion = codeMirrorCompletion(item, 'Program.il', new CodeMirrorLanguageBridge())
    expect(completion.label).toBe('System.Runtime')
    expect(completion.displayLabel).toBe('[System.Runtime] System.Runtime')
    expect(completion.sortText).toBe('0001:System.Runtime')
  })

  it('renders a readable source face and an accessible kind-aware completion selection', () => {
    const host = document.createElement('div')
    host.className = 'codemirror-host'
    const editor = document.createElement('div')
    editor.className = 'cm-editor'
    const tooltip = document.createElement('div')
    tooltip.className = 'cm-tooltip-autocomplete'
    const list = document.createElement('ul')
    const option = document.createElement('li')
    const icon = document.createElement('span')
    icon.className = 'cm-completionIcon cm-completionIcon-keyword'
    const label = document.createElement('span')
    label.className = 'cm-completionLabel'
    label.textContent = 'abstract'
    option.append(icon, label)
    list.append(option)
    tooltip.append(list)
    host.append(editor, tooltip)
    document.body.append(host)

    expect(getComputedStyle(editor).color).toBe('rgb(30, 30, 30)')
    expect(getComputedStyle(editor).fontWeight).toBe('450')
    expect(getComputedStyle(icon).color).toBe('rgb(0, 0, 255)')
    expect(getComputedStyle(label).color).toBe('rgb(0, 0, 255)')

    option.setAttribute('aria-selected', 'true')
    expect(getComputedStyle(option).backgroundColor).toBe('rgb(0, 103, 192)')
    expect(getComputedStyle(label).color).toBe('rgb(255, 255, 255)')
    expect(getComputedStyle(icon).color).toBe('rgb(255, 255, 255)')

    host.remove()
  })

  it('converts UTF-16 LSP diagnostics to CodeMirror offsets', () => {
    const document = Text.of(['let smile = "😀"', 'smile'])
    const diagnostics: CodeMirrorLspDiagnostic[] = [
      {
        range: {
          start: { line: 1, character: 0 },
          end: { line: 1, character: 5 },
        },
        severity: 2,
        code: 'W1',
        source: 'test',
        message: 'Unused value.',
      },
    ]
    expect(codeMirrorDiagnostics(document, diagnostics)).toEqual([
      {
        from: 17,
        to: 22,
        severity: 'warning',
        message: '[W1] Unused value.',
        source: 'test',
      },
    ])
  })

  it('maps semantic methods to the VS gold decoration and rejects out-of-line ranges', () => {
    const document = Text.of(['void Run() {}'])
    const tokens: CodeMirrorSemanticToken[] = [
      {
        line: 0,
        character: 5,
        length: 3,
        tokenType: 'method',
        tokenModifiers: ['static'],
      },
      {
        line: 0,
        character: 40,
        length: 2,
        tokenType: 'field',
        tokenModifiers: [],
      },
    ]
    expect(semanticDecorationRanges(document, tokens)).toEqual([
      {
        from: 5,
        to: 8,
        className: 'cm-semantic-token cm-semantic-method cm-semantic-static',
      },
    ])
  })

  it('renders semantic colors inside lexical fallback highlighting', () => {
    const parent = document.createElement('div')
    parent.className = 'codemirror-host'
    document.body.append(parent)
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: 'class SemanticWidget {}',
        extensions: [
          syntaxHighlighting(visualStudioLightHighlightStyle),
          codeMirrorLanguageExtension('csharp'),
          semanticDecorationExtension,
        ],
      }),
    })

    view.dispatch({
      effects: setSemanticDecorations.of([
        {
          from: 6,
          to: 20,
          className: 'cm-semantic-token cm-semantic-type',
        },
      ]),
    })

    const semanticType = view.dom.querySelector<HTMLElement>('.cm-semantic-type')
    expect(semanticType).toBeInstanceOf(HTMLElement)
    if (!(semanticType instanceof HTMLElement)) throw new Error('Semantic type was not rendered.')
    expect(semanticType.textContent).toBe('SemanticWidget')
    expect(semanticType.querySelector('span')).toBeNull()
    expect(semanticType.parentElement?.tagName).toBe('SPAN')
    expect(getComputedStyle(semanticType).color).toBe('rgb(43, 145, 175)')

    view.destroy()
    parent.remove()
  })

  it('renders fenced hover signatures separately from cleaned Markdown documentation', () => {
    expect(
      codeMirrorHoverSections({
        contents: {
          kind: 'markdown',
          value:
            '```csharp\r\nvoid System.Console.WriteLine() (+ 19 overloads)\r\n```\r\n\r\nWrites **text** with [`Console`](https://learn.microsoft.com/dotnet/api/system.console).',
        },
      }),
    ).toEqual([
      {
        kind: 'code',
        text: 'void System.Console.WriteLine() (+ 19 overloads)',
        language: 'csharp',
      },
      {
        kind: 'documentation',
        text: 'Writes text with Console.',
      },
    ])
  })

  it('colors only a standalone IL hover assembly identity as an assembly', () => {
    const hover = document.createElement('div')
    appendCodeMirrorHoverSections(hover, [
      {
        kind: 'code',
        text: '[System.Console, Version=11.0.0.0, Culture=neutral]',
        language: 'il',
      },
      { kind: 'code', text: 'int32[0...]', language: 'il' },
      { kind: 'documentation', text: '[System.Console] documentation' },
    ])

    const assembly = hover.querySelector<HTMLElement>('.cm-lsp-hover-assembly')
    expect(assembly?.textContent).toBe('System.Console')
    expect(assembly?.parentElement?.textContent).toBe(
      '[System.Console, Version=11.0.0.0, Culture=neutral]',
    )
    expect(assembly?.nextSibling?.textContent).toBe(', Version=11.0.0.0, Culture=neutral]')
    expect(hover.querySelectorAll('.cm-lsp-hover-assembly')).toHaveLength(1)
    expect(hover.querySelectorAll('.cm-lsp-hover-code')[1]?.textContent).toBe('int32[0...]')
  })

  it('projects signature help with an active parameter', () => {
    expect(
      signaturePresentation(
        {
          signatures: [
            {
              label: 'void Write(string value, int count)',
              documentation: { kind: 'markdown', value: 'Writes a value.' },
              parameters: [{ label: 'string value' }, { label: [25, 34] }],
            },
          ],
          activeSignature: 0,
          activeParameter: 1,
        },
        12,
      ),
    ).toEqual({
      position: 12,
      label: 'void Write(string value, int count)',
      activeParameterLabel: 'int count',
      documentation: 'Writes a value.',
      activeSignature: 0,
      signatureCount: 1,
    })
  })

  it('converts folding ranges and rejects stale or overlapping text edits', () => {
    const document = Text.of(['class C {', '  void Run() {', '  }', '}'])
    expect(codeMirrorFoldingRanges(document, [{ startLine: 0, endLine: 3 }])).toEqual([
      { from: 9, to: 30 },
    ])
    expect(
      codeMirrorTextChanges(document, [
        {
          range: { start: { line: 1, character: 2 }, end: { line: 1, character: 6 } },
          newText: 'int',
        },
      ]),
    ).toEqual([{ from: 12, to: 16, insert: 'int' }])
    expect(
      codeMirrorTextChanges(document, [
        {
          range: { start: { line: 1, character: 2 }, end: { line: 1, character: 7 } },
          newText: 'one',
        },
        {
          range: { start: { line: 1, character: 5 }, end: { line: 1, character: 8 } },
          newText: 'two',
        },
      ]),
    ).toBeNull()
  })

  it('attaches preferred LSP quick fixes to lint diagnostics', () => {
    const apply = vi.fn()
    const action: CodeMirrorLspCodeAction = {
      title: "Insert missing ';'",
      kind: 'quickfix',
      isPreferred: true,
      diagnostics: [],
      documentEdits: [],
    }
    const diagnostics = codeMirrorDiagnostics(
      Text.of(['value']),
      [
        {
          range: { start: { line: 0, character: 0 }, end: { line: 0, character: 5 } },
          message: 'Missing semicolon.',
          actions: [action],
        },
      ],
      apply,
    )
    expect(diagnostics[0]?.actions?.[0]).toEqual(
      expect.objectContaining({
        name: "Insert missing ';'",
        markClass: 'cm-lint-action-preferred',
      }),
    )
    diagnostics[0]?.actions?.[0]?.apply({} as never, 0, 5)
    expect(apply).toHaveBeenCalledWith(action)
  })

  it('writes edits through the shared file callback and keeps per-file scroll state', async () => {
    const onChange = vi.fn()
    const first = editorProps('Program.cs', onChange)
    const { container, rerender } = render(<CodeMirrorEditor {...first} />)
    const textbox = screen.getByRole('textbox', { name: 'Source editor' })
    const line = container.querySelector<HTMLElement>('.cm-line')
    expect(line).not.toBeNull()
    if (!line) return
    line.textContent = 'first!'
    fireEvent.input(textbox, { inputType: 'insertText', data: '!' })
    await waitFor(() => expect(onChange).toHaveBeenCalledWith('Program.cs', 'first!'))

    const scroller = container.querySelector<HTMLElement>('.cm-scroller')
    expect(scroller).not.toBeNull()
    if (!scroller) return
    scroller.scrollTop = 40
    fireEvent.scroll(scroller)

    rerender(<CodeMirrorEditor {...editorProps('Other.cs', onChange)} />)
    expect(scroller.scrollTop).toBe(0)
    scroller.scrollTop = 12
    fireEvent.scroll(scroller)

    rerender(<CodeMirrorEditor {...editorProps('Program.cs', onChange)} />)
    expect(scroller.scrollTop).toBe(40)
  })

  it('keeps long source lines unwrapped for editor-local horizontal scrolling', () => {
    const props = editorProps('Program.cs', vi.fn())
    props.files = [{ path: 'Program.cs', text: `class C { string Value = "${'x'.repeat(240)}"; }` }]
    const { container } = render(<CodeMirrorEditor {...props} />)

    expect(container.querySelector('.cm-editor')).not.toHaveClass('cm-lineWrapping')
    expect(getComputedStyle(container.querySelector('.cm-line') as Element).whiteSpace).toBe('pre')
  })

  it('applies shared font size changes without remounting the editor', () => {
    const requestMeasure = vi.spyOn(EditorView.prototype, 'requestMeasure')
    const props = editorProps('Program.cs', vi.fn())
    const { container, rerender } = render(<CodeMirrorEditor {...props} />)
    const host = container.querySelector<HTMLElement>('.codemirror-host')
    const editor = container.querySelector('.cm-editor')

    expect(host?.style.getPropertyValue('--editor-font-size')).toBe('14px')
    requestMeasure.mockClear()
    rerender(<CodeMirrorEditor {...props} fontSize={18} />)
    expect(host?.style.getPropertyValue('--editor-font-size')).toBe('18px')
    expect(container.querySelector('.cm-editor')).toBe(editor)
    expect(requestMeasure).toHaveBeenCalledOnce()
  })

  it('colors and activates a source association without hijacking a text selection', async () => {
    const onActivate = vi.fn()
    const association = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 6 },
      },
      'IL source: Program.cs:1',
    )
    const props = editorProps('Program.cs', vi.fn())
    props.files = [{ path: 'Program.cs', text: 'a + b' }]
    props.sourceAssociations = [association]
    props.onSourceAssociationActivate = onActivate
    const { container, rerender } = render(<CodeMirrorEditor {...props} />)
    const line = await waitFor(() => {
      const candidate = container.querySelector<HTMLElement>('.cm-line.cm-source-association-line')
      expect(candidate).not.toBeNull()
      return candidate
    })
    if (!line) throw new Error('The source association line decoration was not rendered.')
    expect(line.textContent).toBe('a + b')
    expect(container.querySelector('.cm-source-association')).not.toBeInTheDocument()
    expect(
      Array.from(container.querySelectorAll<HTMLElement>('.cm-source-association-range'))
        .map((candidate) => candidate.textContent)
        .join(''),
    ).toBe('a + b')

    fireEvent.mouseUp(line, { button: 0, detail: 1 })
    await waitFor(() => expect(onActivate).toHaveBeenCalledWith(association.key))

    rerender(<CodeMirrorEditor {...props} activeSourceAssociationKey={association.key} />)
    await waitFor(() =>
      expect(container.querySelector('.cm-source-association-line')).toHaveClass(
        'cm-source-association-line-active',
      ),
    )
    const exactActive = container.querySelector<HTMLElement>(
      '.cm-source-association-range.cm-source-association-exact-active',
    )
    expect(exactActive).not.toBeNull()
    expect(exactActive).toHaveClass('source-association-0')
    expect(exactActive?.textContent).toBe('a + b')
    const activeLine = container.querySelector<HTMLElement>('.cm-source-association-line')
    if (!activeLine) throw new Error('The active source association line was not rendered.')

    const editor = container.querySelector<HTMLElement>('.cm-editor')
    if (!editor) throw new Error('CodeMirror editor was not rendered.')
    const editorView = EditorView.findFromDOM(editor)
    if (!editorView) throw new Error('CodeMirror view was not available.')
    editorView.dispatch({ selection: { anchor: 0, head: 3 } })
    fireEvent.mouseUp(activeLine, { button: 0, detail: 1 })
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).toHaveBeenCalledOnce()

    onActivate.mockClear()
    editorView.dispatch({ selection: { anchor: 0 } })
    const currentLine = container.querySelector<HTMLElement>('.cm-source-association-line')
    if (!currentLine) throw new Error('The source association line decoration disappeared.')
    fireEvent.mouseUp(currentLine, { button: 0, detail: 1 })
    fireEvent.mouseUp(currentLine, { button: 0, detail: 2 })
    fireEvent.doubleClick(currentLine, { button: 0, detail: 2 })
    await new Promise((resolve) => window.setTimeout(resolve, 450))
    expect(onActivate).not.toHaveBeenCalled()
  })

  it('keeps AST source selection native and resolves the finalized mouseup selection', async () => {
    const onActivate = vi.fn()
    const onPreview = vi.fn()
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
    const props = editorProps('Program.cs', vi.fn())
    props.files = [{ path: 'Program.cs', text: 'a + b' }]
    props.sourceAssociations = [wholeExpression, identifier]
    props.onSourceAssociationActivate = onActivate
    props.onSourceAssociationPreview = onPreview
    const { container, rerender } = render(<CodeMirrorEditor {...props} />)

    await waitFor(() => expect(container.querySelector('.cm-line')).not.toBeNull())
    expect(container.querySelector('.cm-source-association-line')).not.toBeInTheDocument()
    expect(container.querySelector('.cm-source-association-range')).not.toBeInTheDocument()

    const line = container.querySelector<HTMLElement>('.cm-line')
    if (!line) throw new Error('The CodeMirror source line was not rendered.')
    const editor = container.querySelector<HTMLElement>('.cm-editor')
    if (!editor) throw new Error('CodeMirror editor was not rendered.')
    const editorView = EditorView.findFromDOM(editor)
    if (!editorView) throw new Error('CodeMirror view was not available.')
    editorView.dispatch({ selection: { anchor: 0, head: 5 }, userEvent: 'select.pointer' })
    await waitFor(() => expect(onPreview).toHaveBeenCalledWith(wholeExpression.key))
    editorView.dispatch({ selection: { anchor: 0, head: 5 }, userEvent: 'select.pointer' })
    expect(onPreview).toHaveBeenCalledOnce()
    fireEvent.mouseUp(line, { button: 0, detail: 1 })
    await waitFor(() => expect(onActivate).toHaveBeenCalledWith(wholeExpression.key))

    onActivate.mockClear()
    editorView.dispatch({ selection: { anchor: 0, head: 5 } })
    fireEvent.mouseUp(line, { button: 0, detail: 1 })
    editorView.dispatch({ selection: { anchor: 4 } })
    await waitFor(() => expect(onActivate).toHaveBeenCalledWith(identifier.key))
    expect(onActivate).toHaveBeenCalledOnce()
    expect(editorView.state.selection.main.empty).toBe(true)
    expect(editorView.state.selection.main.head).toBe(4)

    const posAtCoords = vi.spyOn(editorView, 'posAtCoords').mockReturnValue(2)
    editorView.dispatch({ selection: { anchor: 0, head: 5 } })
    fireEvent.mouseDown(line, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    editorView.dispatch({ selection: { anchor: 0, head: 5 } })
    fireEvent.mouseUp(line, { button: 0, detail: 1, clientX: 20, clientY: 20 })
    editorView.dispatch({ selection: { anchor: 0, head: 5 } })
    await waitFor(() => expect(editorView.state.selection.main.empty).toBe(true))
    expect(editorView.state.selection.main.head).toBe(2)
    posAtCoords.mockRestore()

    rerender(<CodeMirrorEditor {...props} activeSourceAssociationKey={identifier.key} />)
    await waitFor(() => expect(container.querySelector('.cm-line')).not.toBeNull())
    expect(container.querySelector('.cm-source-association-range')).not.toBeInTheDocument()
    expect(container.querySelector('.cm-source-association-line')).not.toBeInTheDocument()
  })

  it('opens completion with Ctrl+Space and accepts the selected item with Tab', async () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: 'Con',
        selection: { anchor: 3 },
        extensions: [
          autocompletion({
            override: [() => ({ from: 0, options: [{ label: 'Console' }] })],
            defaultKeymap: false,
            interactionDelay: 0,
          }),
          keymap.of(codeMirrorEditorKeymap),
        ],
      }),
    })

    view.contentDOM.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: ' ',
        code: 'Space',
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      }),
    )
    await waitFor(() => expect(completionStatus(view.state)).toBe('active'))

    view.contentDOM.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Tab',
        code: 'Tab',
        bubbles: true,
        cancelable: true,
      }),
    )
    await waitFor(() => expect(view.state.doc.toString()).toBe('Console'))
    expect(view.state.doc.toString()).not.toContain('\t')

    view.destroy()
    parent.remove()
  })

  it('applies resolved primary and additional completion edits in one transaction', async () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const transactions: Transaction[] = []
    const source = 'class C {\n  Con\n}'
    const primaryFrom = source.indexOf('Con')
    const primaryTo = primaryFrom + 3
    const bridge = new CodeMirrorLanguageBridge()
    const item: CodeMirrorLspCompletionItem = {
      label: 'Console',
      insertText: 'Console',
      raw: { label: 'Console', data: { resolveId: 'completion-1' } },
      documentVersion: 1,
    }
    const resolved: CodeMirrorLspCompletionItem = {
      ...item,
      textEdit: {
        range: {
          start: { line: 1, character: 2 },
          end: { line: 1, character: 5 },
        },
        newText: 'Console.WriteLine',
      },
      additionalTextEdits: [
        {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 0 },
          },
          newText: 'using System;\n',
        },
      ],
      raw: { label: 'Console', detail: 'resolved' },
    }
    const resolveCompletion = vi.spyOn(bridge, 'resolveCompletion').mockResolvedValue(resolved)
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: source,
        selection: { anchor: primaryTo },
        extensions: EditorView.updateListener.of((update) => {
          transactions.push(...update.transactions.filter((transaction) => transaction.docChanged))
        }),
      }),
    })
    const completion = codeMirrorCompletion(item, 'Program.cs', bridge, () => 1)
    if (typeof completion.apply !== 'function') throw new Error('Completion apply is not callable.')

    completion.apply(view, completion, primaryFrom, primaryTo)

    const expected = 'using System;\nclass C {\n  Console.WriteLine\n}'
    await vi.waitFor(() => expect(view.state.doc.toString()).toBe(expected))
    expect(resolveCompletion).toHaveBeenCalledWith('Program.cs', item)
    expect(transactions).toHaveLength(1)
    expect(transactions[0]?.annotation(pickedCompletion)).toBe(completion)
    expect(transactions[0]?.isUserEvent('input.complete')).toBe(true)
    expect(view.state.selection.main.head).toBe(expected.indexOf('Console.WriteLine') + 17)

    view.destroy()
    parent.remove()
  })

  it('applies an initially resolved snippet immediately with its using edit and caret', () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const source = 'class C {\n  void M() {\n    cw\n  }\n}'
    const primaryFrom = source.indexOf('cw')
    const primaryTo = primaryFrom + 2
    const bridge = new CodeMirrorLanguageBridge()
    const item: CodeMirrorLspCompletionItem = {
      label: 'cw',
      kind: 15,
      insertTextFormat: 2,
      textEdit: {
        range: {
          start: { line: 2, character: 4 },
          end: { line: 2, character: 6 },
        },
        newText: `Console.WriteLine(\${0});`,
      },
      additionalTextEdits: [
        {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 0 },
          },
          newText: 'using System;\n',
        },
      ],
      raw: { label: 'cw', detail: 'resolved' },
      documentVersion: 1,
    }
    const resolveCompletion = vi.spyOn(bridge, 'resolveCompletion')
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: source,
        selection: { anchor: primaryTo },
      }),
    })
    const completion = codeMirrorCompletion(item, 'Program.cs', bridge, () => 1)
    if (typeof completion.apply !== 'function') throw new Error('Completion apply is not callable.')

    completion.apply(view, completion, primaryFrom, primaryTo)

    const expected = 'using System;\nclass C {\n  void M() {\n    Console.WriteLine();\n  }\n}'
    expect(view.state.doc.toString()).toBe(expected)
    expect(resolveCompletion).not.toHaveBeenCalled()
    expect(view.state.selection.main.head).toBe(expected.indexOf('Console.WriteLine(') + 18)

    view.destroy()
    parent.remove()
  })

  it('selects the class name placeholder before advancing into the class body', () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const source = 'class'
    const bridge = new CodeMirrorLanguageBridge()
    const item: CodeMirrorLspCompletionItem = {
      label: 'class',
      kind: 15,
      insertTextFormat: 2,
      textEdit: {
        range: {
          start: { line: 0, character: 0 },
          end: { line: 0, character: source.length },
        },
        newText: `class \${1:MyClass}\n{\n    \${0}\n\\}`,
      },
      raw: { label: 'class', detail: 'resolved' },
      documentVersion: 1,
    }
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: source,
        selection: { anchor: source.length },
        extensions: keymap.of(codeMirrorEditorKeymap),
      }),
    })
    const completion = codeMirrorCompletion(item, 'Program.cs', bridge, () => 1)
    if (typeof completion.apply !== 'function') throw new Error('Completion apply is not callable.')

    completion.apply(view, completion, 0, source.length)

    const expanded = 'class MyClass\n{\n    \n}'
    const classNameStart = expanded.indexOf('MyClass')
    expect(view.state.doc.toString()).toBe(expanded)
    expect(view.state.selection.main.from).toBe(classNameStart)
    expect(view.state.selection.main.to).toBe(classNameStart + 'MyClass'.length)

    view.dispatch(view.state.replaceSelection('Widget'))
    expect(
      runScopeHandlers(
        view,
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }),
        'editor',
      ),
    ).toBe(true)

    const renamed = 'class Widget\n{\n    \n}'
    const bodyPosition = renamed.indexOf('    \n') + 4
    expect(view.state.doc.toString()).toBe(renamed)
    expect(view.state.selection.main.from).toBe(bodyPosition)
    expect(view.state.selection.main.to).toBe(bodyPosition)

    view.destroy()
    parent.remove()
  })

  it('keeps linked for-loop placeholders synchronized before advancing through later fields', () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const source = 'for'
    const bridge = new CodeMirrorLanguageBridge()
    const item: CodeMirrorLspCompletionItem = {
      label: 'for',
      kind: 15,
      insertTextFormat: 2,
      textEdit: {
        range: {
          start: { line: 0, character: 0 },
          end: { line: 0, character: source.length },
        },
        newText: `for (int \${1:i} = 0; \${1:i} < \${2:length}; \${1:i}++)\n{\n    \${0}\n\\}`,
      },
      raw: { label: 'for', detail: 'resolved' },
      documentVersion: 1,
    }
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: source,
        selection: { anchor: source.length },
        extensions: [
          EditorState.allowMultipleSelections.of(true),
          keymap.of(codeMirrorEditorKeymap),
        ],
      }),
    })
    const completion = codeMirrorCompletion(item, 'Program.cs', bridge, () => 1)
    if (typeof completion.apply !== 'function') throw new Error('Completion apply is not callable.')

    completion.apply(view, completion, 0, source.length)

    const expanded = 'for (int i = 0; i < length; i++)\n{\n    \n}'
    const iteratorOffsets = [...expanded.matchAll(/\bi\b/g)].map((match) => match.index)
    expect(view.state.doc.toString()).toBe(expanded)
    expect(view.state.selection.ranges).toHaveLength(3)
    expect(view.state.selection.ranges.map(({ from, to }) => ({ from, to }))).toEqual(
      iteratorOffsets.map((from) => ({ from, to: from + 1 })),
    )

    view.dispatch(view.state.replaceSelection('index'))

    const renamed = 'for (int index = 0; index < length; index++)\n{\n    \n}'
    expect(view.state.doc.toString()).toBe(renamed)
    expect(view.state.selection.ranges).toHaveLength(3)
    expect(
      runScopeHandlers(
        view,
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }),
        'editor',
      ),
    ).toBe(true)

    const lengthStart = renamed.indexOf('length')
    expect(view.state.selection.ranges).toHaveLength(1)
    expect(view.state.selection.main.from).toBe(lengthStart)
    expect(view.state.selection.main.to).toBe(lengthStart + 'length'.length)
    expect(
      runScopeHandlers(view, new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }), 'editor'),
    ).toBe(true)

    const bodyPosition = renamed.indexOf('    \n') + 4
    expect(view.state.selection.main.from).toBe(bodyPosition)
    expect(view.state.selection.main.to).toBe(bodyPosition)

    view.destroy()
    parent.remove()
  })

  it('applies an initially resolved non-snippet immediately with its using edit', () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const source = 'class C {\n  global::Con\n}'
    const primaryFrom = source.indexOf('global::Con')
    const primaryTo = primaryFrom + 'global::Con'.length
    const completionFrom = source.indexOf('Con')
    const completionTo = completionFrom + 3
    const bridge = new CodeMirrorLanguageBridge()
    const item: CodeMirrorLspCompletionItem = {
      label: 'Console',
      textEdit: {
        range: {
          start: { line: 1, character: 2 },
          end: { line: 1, character: 13 },
        },
        newText: 'Console',
      },
      additionalTextEdits: [
        {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 0 },
          },
          newText: 'using System;\n',
        },
      ],
      raw: { label: 'Console', detail: 'initially resolved' },
      documentVersion: 1,
    }
    const resolveCompletion = vi.spyOn(bridge, 'resolveCompletion')
    const transactions: Transaction[] = []
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: source,
        selection: { anchor: primaryTo },
        extensions: EditorView.updateListener.of((update) => {
          transactions.push(...update.transactions.filter((transaction) => transaction.docChanged))
        }),
      }),
    })
    const completion = codeMirrorCompletion(item, 'Program.cs', bridge, () => 1)
    if (typeof completion.apply !== 'function') throw new Error('Completion apply is not callable.')

    completion.apply(view, completion, completionFrom, completionTo)

    const expected = 'using System;\nclass C {\n  Console\n}'
    expect(view.state.doc.toString()).toBe(expected)
    expect(resolveCompletion).not.toHaveBeenCalled()
    expect(transactions).toHaveLength(1)
    expect(transactions[0]?.annotation(pickedCompletion)).toBe(completion)
    expect(transactions[0]?.isUserEvent('input.complete')).toBe(true)
    expect(view.state.selection.main.head).toBe(expected.indexOf('Console') + 7)

    view.destroy()
    parent.remove()
  })

  it('does not apply initially resolved edits from a stale document version', async () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const source = '// newer line\nclass C {\n  Con\n}'
    const completionFrom = source.indexOf('Con')
    const completionTo = completionFrom + 3
    const bridge = new CodeMirrorLanguageBridge()
    const item: CodeMirrorLspCompletionItem = {
      label: 'Console',
      textEdit: {
        range: {
          start: { line: 1, character: 2 },
          end: { line: 1, character: 5 },
        },
        newText: 'Console',
      },
      additionalTextEdits: [
        {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 0 },
          },
          newText: 'using System;\n',
        },
      ],
      raw: { label: 'Console', detail: 'initially resolved' },
      documentVersion: 1,
    }
    const resolveCompletion = vi.spyOn(bridge, 'resolveCompletion')
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: source,
        selection: { anchor: completionTo },
      }),
    })
    const completion = codeMirrorCompletion(item, 'Program.cs', bridge, () => 2)
    if (typeof completion.apply !== 'function') throw new Error('Completion apply is not callable.')

    completion.apply(view, completion, completionFrom, completionTo)

    const expected = '// newer line\nclass C {\n  Console\n}'
    await vi.waitFor(() => expect(view.state.doc.toString()).toBe(expected))
    expect(resolveCompletion).not.toHaveBeenCalled()
    expect(view.state.doc.toString()).not.toContain('using System;')

    view.destroy()
    parent.remove()
  })

  it('falls back to the original insertion when completion resolve fails', async () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const bridge = new CodeMirrorLanguageBridge()
    const item: CodeMirrorLspCompletionItem = {
      label: 'Console',
      insertText: 'Console',
      raw: { label: 'Console', data: { resolveId: 'completion-1' } },
      documentVersion: 1,
    }
    vi.spyOn(bridge, 'resolveCompletion').mockRejectedValue(new Error('resolve failed'))
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: 'Con',
        selection: { anchor: 3 },
      }),
    })
    const completion = codeMirrorCompletion(item, 'Program.cs', bridge, () => 1)
    if (typeof completion.apply !== 'function') throw new Error('Completion apply is not callable.')

    completion.apply(view, completion, 0, 3)

    await vi.waitFor(() => expect(view.state.doc.toString()).toBe('Console'))
    expect(view.state.selection.main.head).toBe(7)

    view.destroy()
    parent.remove()
  })

  it('uses four spaces for Tab, selected lines, and automatic indentation', () => {
    const props = editorProps('Program.cs', vi.fn())
    props.files = [{ path: 'Program.cs', text: 'value' }]
    const { container } = render(<CodeMirrorEditor {...props} />)
    const editor = container.querySelector<HTMLElement>('.cm-editor')
    expect(editor).toBeInstanceOf(HTMLElement)
    if (!editor) throw new Error('CodeMirror editor was not rendered.')
    const view = EditorView.findFromDOM(editor)
    expect(view).not.toBeNull()
    if (!view) throw new Error('CodeMirror view was not available.')

    expect(completionStatus(view.state)).toBeNull()
    expect(view.state.facet(indentUnit)).toBe('    ')

    view.dispatch({ selection: { anchor: 0 } })

    view.contentDOM.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Tab',
        code: 'Tab',
        bubbles: true,
        cancelable: true,
      }),
    )

    expect(view.state.doc.toString()).toBe('    value')
    expect(view.state.doc.toString()).not.toContain('\t')

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: 'first\nsecond' },
      selection: { anchor: 0, head: 12 },
    })
    view.contentDOM.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Tab',
        code: 'Tab',
        bubbles: true,
        cancelable: true,
      }),
    )
    expect(view.state.doc.toString()).toBe('    first\n    second')

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: 'class C {' },
      selection: { anchor: 9 },
    })
    view.contentDOM.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Enter',
        code: 'Enter',
        bubbles: true,
        cancelable: true,
      }),
    )
    expect(view.state.doc.toString()).toBe('class C {\n    ')
  })
})

function editorProps(
  activeFile: string,
  onChange: CodeMirrorEditorProps['onChange'],
): CodeMirrorEditorProps {
  return {
    files: [
      { path: 'Program.cs', text: 'first' },
      { path: 'Other.cs', text: 'second' },
    ],
    activeFile,
    languageSession: {
      enabled: false,
      resolution: null,
      languageId: 'csharp',
      toolchainId: null,
      referenceSetId: null,
      buildMode: 'release',
      workspaceRevision: 1,
      selectionRevision: 1,
      sourceOrder: ['Program.cs', 'Other.cs'],
    },
    executionFlow: null,
    sourceNavigation: null,
    fontSize: 14,
    onChange,
  }
}

function resolvedLanguageSession(
  kind: 'render' | 'run',
  languageId: 'csharp' | 'il' = 'csharp',
): CodeMirrorEditorProps['languageSession'] {
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
    enabled: true,
    resolution,
    languageId,
    toolchainId,
    referenceSetId: 'net10-ref',
    buildMode: 'release',
    workspaceRevision: 1,
    selectionRevision: 1,
    sourceOrder,
  }
}
