import { useEffect, useRef } from 'react'
import { mobileEditorMediaQuery } from '../editor/editorPreference'
import * as monaco from '../editor/monacoCore'
import {
  type CodeMirrorLspHover,
  type LspRange,
  lspSemanticTokenModifiers,
  lspSemanticTokenTypes,
} from '../lsp/codeMirrorLanguageClient'
import { registerSourceLanguages, sourceEditorTheme } from '../lsp/languageRegistration'
import { encodeSemanticTokens } from '../lsp/monacoLanguageClient'
import type { CodeDocumentLineAction, CodeDocumentViewProps } from './CodeDocumentView'
import { useIlOutputLanguageSession } from './ilOutputLanguageSession'
import { outputFoldingRanges } from './outputFoldingModel'
import { sourceAssociationClass } from './sourceAssociationModel'

registerSourceLanguages()

export function MonacoCodeDocumentView(props: CodeDocumentViewProps) {
  const hostRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)
  const modelRef = useRef<monaco.editor.ITextModel | null>(null)
  const decorationIdsRef = useRef<string[]>([])
  const activationTimerRef = useRef<number | null>(null)
  const latestRef = useRef(props)
  const hoveredAssociationKeyRef = useRef<string | null>(null)
  const handledAssociationRevealRevisionRef = useRef<number | null>(null)
  const outputModelSchemeRef = useRef<string | null>(null)
  if (!outputModelSchemeRef.current) {
    const id = globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2)
    outputModelSchemeRef.current = `sharplabnext-output-${id.toLowerCase()}`
  }
  const ilLanguageSession = useIlOutputLanguageSession(
    props.text,
    props.generationKey ?? null,
    props.languageId === 'il' ? props.ilOutputLanguageSessionOptions : null,
  )
  latestRef.current = props
  const lineActionsSignature = (props.lineActions ?? []).map(lineActionKey).join('|')
  const pendingActivationInputsRef = useRef({
    generationKey: props.generationKey ?? null,
    lineActionsSignature,
    text: props.text,
  })
  const handledAssociationGenerationRef = useRef(props.generationKey ?? null)

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    const model = monaco.editor.createModel(
      latestRef.current.text,
      latestRef.current.languageId,
      monaco.Uri.parse(`${outputModelSchemeRef.current}:///Output.il`),
    )
    const isMobile = typeof matchMedia === 'function' && matchMedia(mobileEditorMediaQuery).matches
    const editor = monaco.editor.create(host, {
      model,
      theme: sourceEditorTheme,
      readOnly: true,
      domReadOnly: true,
      ariaLabel: latestRef.current.ariaLabel,
      automaticLayout: false,
      editContext: false,
      'semanticHighlighting.enabled': true,
      fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
      fontSize: latestRef.current.fontSize,
      lineHeight: Math.round(latestRef.current.fontSize * 1.5),
      lineNumbers: 'on',
      lineNumbersMinChars: 3,
      glyphMargin: false,
      folding: true,
      showFoldingControls: 'always',
      lineDecorationsWidth: 7,
      minimap: { enabled: !isMobile },
      overviewRulerLanes: 0,
      overviewRulerBorder: false,
      stickyScroll: { enabled: false },
      scrollBeyondLastLine: false,
      wordWrap: 'off',
      renderWhitespace: 'selection',
      contextmenu: false,
      selectionHighlight: false,
      occurrencesHighlight: 'off',
      padding: { top: 10, bottom: 10 },
    })
    editorRef.current = editor
    modelRef.current = model

    const clearActivation = () => {
      if (activationTimerRef.current === null) return
      window.clearTimeout(activationTimerRef.current)
      activationTimerRef.current = null
    }
    const updateHoveredAssociation = (associationKey: string | null) => {
      if (hoveredAssociationKeyRef.current === associationKey) return
      hoveredAssociationKeyRef.current = associationKey
      latestRef.current.onAssociationHover?.(associationKey)
    }
    const mouseUp = editor.onMouseUp((event) => {
      const detail = event.event.browserEvent.detail
      if (detail > 1) clearActivation()
      if (isGutterTarget(event.target.type)) return
      const position = event.target.position
      const selection = editor.getSelection()
      if (!event.event.leftButton || detail > 1 || !position || !selection?.isEmpty()) return
      const assemblyLabel =
        latestRef.current.languageId === 'asm' ? assemblyLabelTarget(model, position) : null
      if (assemblyLabel) {
        clearActivation()
        const scheduledGenerationKey = latestRef.current.generationKey ?? null
        const scheduledText = latestRef.current.text
        activationTimerRef.current = window.setTimeout(() => {
          activationTimerRef.current = null
          if (
            (latestRef.current.generationKey ?? null) !== scheduledGenerationKey ||
            latestRef.current.text !== scheduledText
          ) {
            return
          }
          editor.setSelection(assemblyLabel)
          editor.revealRangeInCenter(assemblyLabel)
          editor.focus()
        }, 400)
        return
      }
      const action = latestRef.current.lineActions?.find(
        (candidate) =>
          position.lineNumber >= candidate.startLine && position.lineNumber <= candidate.endLine,
      )
      if (!action) return
      clearActivation()
      const actionKey = lineActionKey(action)
      const scheduledGenerationKey = latestRef.current.generationKey ?? null
      const scheduledText = latestRef.current.text
      activationTimerRef.current = window.setTimeout(() => {
        activationTimerRef.current = null
        if (
          (latestRef.current.generationKey ?? null) !== scheduledGenerationKey ||
          latestRef.current.text !== scheduledText
        ) {
          return
        }
        latestRef.current.lineActions
          ?.find((candidate) => lineActionKey(candidate) === actionKey)
          ?.onActivate()
      }, 400)
    })
    const mouseMove = editor.onMouseMove((event) => {
      const lineNumber = event.target.position?.lineNumber
      const association =
        lineNumber === undefined
          ? null
          : latestRef.current.lineAssociations?.find(
              (candidate) => lineNumber >= candidate.startLine && lineNumber <= candidate.endLine,
            )
      updateHoveredAssociation(association?.association.key ?? null)
    })
    const mouseLeave = editor.onMouseLeave(() => updateHoveredAssociation(null))
    const scroll = editor.onDidScrollChange((event) => {
      if (event.scrollTopChanged || event.scrollLeftChanged) clearActivation()
    })
    const resizeObserver =
      typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => editor.layout())
    resizeObserver?.observe(host)
    return () => {
      clearActivation()
      updateHoveredAssociation(null)
      resizeObserver?.disconnect()
      mouseUp.dispose()
      mouseMove.dispose()
      mouseLeave.dispose()
      scroll.dispose()
      decorationIdsRef.current = []
      editor.dispose()
      model.dispose()
      editorRef.current = null
      modelRef.current = null
    }
  }, [])

  useEffect(() => {
    const generationKey = props.generationKey ?? null
    const previous = pendingActivationInputsRef.current
    pendingActivationInputsRef.current = { generationKey, lineActionsSignature, text: props.text }
    if (
      previous.generationKey === generationKey &&
      previous.lineActionsSignature === lineActionsSignature &&
      previous.text === props.text
    ) {
      return
    }
    if (activationTimerRef.current !== null) {
      window.clearTimeout(activationTimerRef.current)
      activationTimerRef.current = null
    }
  }, [lineActionsSignature, props.generationKey, props.text])

  useEffect(() => {
    const generationKey = props.generationKey ?? null
    if (handledAssociationGenerationRef.current === generationKey) return
    handledAssociationGenerationRef.current = generationKey
    handledAssociationRevealRevisionRef.current = null
  }, [props.generationKey])

  useEffect(() => {
    const model = modelRef.current
    if (model && model.getValue() !== props.text) model.setValue(props.text)
  }, [props.text])

  useEffect(() => {
    const model = modelRef.current
    if (model && model.getLanguageId() !== props.languageId) {
      monaco.editor.setModelLanguage(model, props.languageId)
    }
  }, [props.languageId])

  useEffect(() => {
    const model = modelRef.current
    const outputModelScheme = outputModelSchemeRef.current
    if (!model || !outputModelScheme || props.languageId !== 'il') return
    const selector: monaco.languages.LanguageFilter = {
      language: 'il',
      scheme: outputModelScheme,
      exclusive: true,
    }
    const semanticTokens = monaco.languages.registerDocumentSemanticTokensProvider(selector, {
      getLegend: () => ({
        tokenTypes: [...lspSemanticTokenTypes],
        tokenModifiers: [...lspSemanticTokenModifiers],
      }),
      provideDocumentSemanticTokens: (candidate, _lastResultId, token) => {
        if (candidate !== model || token.isCancellationRequested) return null
        return { data: encodeSemanticTokens(ilLanguageSession.semanticTokens) }
      },
      releaseDocumentSemanticTokens() {},
    })
    return () => semanticTokens.dispose()
  }, [ilLanguageSession.semanticTokens, props.languageId])

  useEffect(() => {
    const model = modelRef.current
    const outputModelScheme = outputModelSchemeRef.current
    if (!model || !outputModelScheme || props.languageId !== 'il') return
    const selector: monaco.languages.LanguageFilter = {
      language: 'il',
      scheme: outputModelScheme,
      exclusive: true,
    }
    const hover = monaco.languages.registerHoverProvider(selector, {
      provideHover: async (candidate, position, token) => {
        if (candidate !== model || ilLanguageSession.status !== 'ready') return null
        const version = model.getVersionId()
        const result = await ilLanguageSession.hover({
          line: position.lineNumber - 1,
          character: position.column - 1,
        })
        if (
          token.isCancellationRequested ||
          candidate !== modelRef.current ||
          model.getVersionId() !== version ||
          !result
        ) {
          return null
        }
        return monacoHover(result)
      },
    })
    return () => hover.dispose()
  }, [ilLanguageSession.hover, ilLanguageSession.status, props.languageId])

  useEffect(() => {
    const model = modelRef.current
    if (!model || (props.languageId !== 'asm' && props.languageId !== 'il')) return
    const registration = monaco.languages.registerFoldingRangeProvider(props.languageId, {
      provideFoldingRanges: (candidate) =>
        candidate === model
          ? outputFoldingRanges(model.getValue(), props.languageId).map((range) => ({
              start: range.startLine,
              end: range.endLine,
              kind: monaco.languages.FoldingRangeKind.Region,
            }))
          : [],
    })
    return () => registration.dispose()
  }, [props.languageId])

  useEffect(() => {
    editorRef.current?.updateOptions({
      ariaLabel: props.ariaLabel,
      fontSize: props.fontSize,
      lineHeight: Math.round(props.fontSize * 1.5),
    })
  }, [props.ariaLabel, props.fontSize])

  useEffect(() => {
    const editor = editorRef.current
    const model = modelRef.current
    if (!editor || !model) return
    const decorations = (props.lineAssociations ?? []).flatMap((lineAssociation) => {
      if (
        lineAssociation.startLine < 1 ||
        lineAssociation.startLine > model.getLineCount() ||
        lineAssociation.endLine < lineAssociation.startLine
      ) {
        return []
      }
      const endLine = Math.min(model.getLineCount(), lineAssociation.endLine)
      const navigable = props.lineActions?.some(
        (action) =>
          action.startLine <= lineAssociation.endLine &&
          action.endLine >= lineAssociation.startLine,
      )
      const tooltip = props.lineTooltips?.find(
        (candidate) =>
          candidate.startLine <= lineAssociation.endLine &&
          candidate.endLine >= lineAssociation.startLine,
      )
      return [
        {
          range: new monaco.Range(
            lineAssociation.startLine,
            1,
            endLine,
            model.getLineMaxColumn(endLine),
          ),
          options: {
            isWholeLine: true,
            className: [
              navigable ? 'monaco-output-source-navigable' : '',
              lineAssociation.association.key === props.activeAssociationKey
                ? 'monaco-output-source-active'
                : '',
              sourceAssociationClass(lineAssociation.association.colorIndex),
            ]
              .filter(Boolean)
              .join(' '),
            ...(tooltip
              ? {
                  hoverMessage: {
                    value: `**${escapeMarkdown(tooltip.heading)}**\n\n\`${escapeMarkdown(tooltip.body)}\``,
                    isTrusted: false,
                  },
                }
              : {}),
          },
        } satisfies monaco.editor.IModelDeltaDecoration,
      ]
    })
    decorationIdsRef.current = editor.deltaDecorations(decorationIdsRef.current, decorations)
  }, [props.activeAssociationKey, props.lineActions, props.lineAssociations, props.lineTooltips])

  useEffect(() => {
    const editor = editorRef.current
    const revision = props.activeAssociationRevision ?? 0
    if (handledAssociationRevealRevisionRef.current === revision) return
    const association = props.lineAssociations?.find(
      (candidate) => candidate.association.key === props.activeAssociationKey,
    )
    if (!editor || !association) return
    editor.revealLineInCenter(association.startLine)
    handledAssociationRevealRevisionRef.current = revision
  }, [props.activeAssociationKey, props.activeAssociationRevision, props.lineAssociations])

  return <div ref={hostRef} className="code-document-view monaco-code-document" />
}

function escapeMarkdown(value: string): string {
  return value.replace(/[\\`*_{}[\]()#+\-.!]/g, '\\$&').replaceAll('\n', ' ')
}

function monacoHover(hover: CodeMirrorLspHover): monaco.languages.Hover | null {
  const contents = markdownContents(hover.contents)
  if (contents.length === 0) return null
  return {
    contents,
    ...(hover.range ? { range: toMonacoRange(hover.range) } : {}),
  }
}

function markdownContents(value: unknown): monaco.IMarkdownString[] {
  if (Array.isArray(value)) return value.flatMap(markdownContents)
  if (typeof value === 'string') return value ? [safeMarkdown(value)] : []
  if (!isRecord(value) || typeof value.value !== 'string' || !value.value) return []
  const content =
    typeof value.language === 'string'
      ? `\`\`\`${value.language}\n${value.value}\n\`\`\``
      : value.value
  return [safeMarkdown(content)]
}

function safeMarkdown(value: string): monaco.IMarkdownString {
  return { value, isTrusted: false, supportHtml: false }
}

function toMonacoRange(range: LspRange): monaco.Range {
  return new monaco.Range(
    range.start.line + 1,
    range.start.character + 1,
    range.end.line + 1,
    range.end.character + 1,
  )
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isGutterTarget(target: monaco.editor.MouseTargetType): boolean {
  return (
    target === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN ||
    target === monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS ||
    target === monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS ||
    target === monaco.editor.MouseTargetType.GUTTER_VIEW_ZONE
  )
}

function assemblyLabelTarget(
  model: monaco.editor.ITextModel,
  position: monaco.Position,
): monaco.Range | null {
  const reference = model.getWordAtPosition(position)?.word
  if (!reference || !/^G_M\w+$/i.test(reference)) return null
  const clickedWord = model.getWordAtPosition(position)
  if (clickedWord && model.getLineContent(position.lineNumber)[clickedWord.endColumn - 1] === ':') {
    return null
  }
  const match = model.findMatches(
    `^\\s*${escapeRegExp(reference)}:`,
    false,
    true,
    false,
    null,
    false,
    1,
  )[0]
  return match ? monaco.Range.lift(match.range) : null
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function lineActionKey(action: CodeDocumentLineAction): string {
  return `${action.startLine}:${action.endLine}:${action.ariaLabel}`
}
