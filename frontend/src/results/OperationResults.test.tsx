import { activateHover, EditorView } from '@codemirror/view'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { CSSProperties } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { OperationEvent, OperationResult, OutputChannel, OutputManifest } from '../api/types'
import '../App.css'
import { parseJitAssembly } from './jitAssemblyModel'
import { AstStatus, createJitOutputSourceLinks, JitStatus, type OperationContentView, OperationResults, RunStatus } from './OperationResults'
import type { SourceAssociation } from './sourceAssociationModel'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function outputManifest(id: string, displayName: string, renderer: string): OutputManifest {
  return {
    id,
    displayName,
    renderer,
    requiresRuntime: id === 'run' || id === 'jit-asm',
    requiredCapabilities: [],
    acceptedArtifactFormats: [],
  }
}

function chunk(sequence: number, channel: OutputChannel, text: string): OperationEvent {
  return {
    operationId: 'op-test',
    sequence,
    timestampUtc: new Date().toISOString(),
    traceId: 'trace-test',
    payload: {
      kind: 'output-chunk',
      chunk: { channel, encoding: 'utf-8', data: btoa(text), truncated: false },
    },
  }
}

describe('OperationResults', () => {
  it('renders a JavaScript artifact with the code output editor', () => {
    const content: OperationContentView = {
      contentRef: `sha256:${'a'.repeat(64)}`,
      mediaType: 'text/javascript; charset=utf-8',
      text: 'JSIL.DeclareAssembly("Sample");\n',
      loading: false,
      error: null,
    }

    render(<OperationResults output={outputManifest('javascript', 'JavaScript (JSIL)', 'javascript')} results={[]} events={[]} content={content} pending={false} editorKind="codemirror" />)

    expect(screen.getByRole('tab', { name: 'JavaScript (JSIL)' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('textbox', { name: 'JavaScript output' })).toHaveTextContent('JSIL.DeclareAssembly')
  })

  it('confines Ctrl+A to the focused Run output', () => {
    render(
      <>
        <span>Outside result controls</span>
        <OperationResults output={outputManifest('run', 'Run', 'runtime-output')} results={[]} events={[chunk(1, 'stdout', 'first line\nsecond line')]} content={null} pending={false} />
      </>,
    )

    const outputRegion = screen.getByRole('region', { name: 'Program output' })
    outputRegion.focus()
    expect(outputRegion).toHaveFocus()
    const event = new KeyboardEvent('keydown', {
      key: 'a',
      code: 'KeyA',
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    })
    outputRegion.dispatchEvent(event)

    const selection = window.getSelection()
    expect(event.defaultPrevented).toBe(true)
    expect(selection?.toString()).toBe('first line\nsecond line')
    expect(selection?.toString()).not.toContain('Outside result controls')
    expect(selection?.rangeCount).toBe(1)
    if (!selection || selection.rangeCount === 0) throw new Error('Run output was not selected.')
    const range = selection.getRangeAt(0)
    expect(outputRegion.contains(range.startContainer)).toBe(true)
    expect(outputRegion.contains(range.endContainer)).toBe(true)
  })

  it('copies the merged stdout and stderr output from the Output tab', async () => {
    const writeClipboard = vi.fn(async (_value: string) => {})
    vi.stubGlobal('navigator', { clipboard: { writeText: writeClipboard } })
    const run: OperationResult = {
      resultType: 'run',
      status: 'non-zero-exit',
      exitCode: 9,
      exception: null,
      elapsed: '00:00:00.1200000',
      outputTruncated: false,
      identity: {
        runtimeVersion: '10.0.9',
        runtimeCommit: 'runtime-commit',
        runtimeImageId: 'sha256:image',
        rid: 'linux-x64',
        architecture: 'x64',
      },
    }

    render(
      <>
        <RunStatus result={run.resultType === 'run' ? run : undefined} />
        <OperationResults output={outputManifest('run', 'Run', 'runtime-output')} results={[run]} events={[chunk(1, 'stdout', 'hello\n'), chunk(2, 'stderr', 'warning\n')]} content={null} pending={false} />
      </>,
    )

    expect(screen.getByText('hello')).toBeVisible()
    expect(screen.getByText('Failed')).toBeInTheDocument()
    expect(screen.getByText('Exit 9')).toBeInTheDocument()
    expect(screen.getByText('120 ms')).toBeInTheDocument()
    expect(screen.getByText('warning')).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Copy output' }))
    await waitFor(() => expect(writeClipboard).toHaveBeenLastCalledWith('hello\nwarning\n'))
    expect(screen.queryByRole('tab', { name: /Stderr/ })).not.toBeInTheDocument()
  })

  it('renders streamed ANSI SGR safely and copies text without consumed sequences', async () => {
    const writeClipboard = vi.fn(async (_value: string) => {})
    vi.stubGlobal('navigator', { clipboard: { writeText: writeClipboard } })
    const run: OperationResult = {
      resultType: 'run',
      status: 'completed',
      exitCode: 0,
      exception: null,
      elapsed: '00:00:00.0200000',
      outputTruncated: false,
      identity: {
        runtimeVersion: '10.0.9',
        runtimeCommit: 'runtime-commit',
        runtimeImageId: 'sha256:image',
        rid: 'linux-x64',
        architecture: 'x64',
      },
    }
    const unsupported = '\u001b[2Jcursor'
    render(
      <OperationResults
        output={outputManifest('run', 'Run', 'runtime-output')}
        results={[run]}
        events={[chunk(1, 'stdout', '\u001b[1;4;38;2;18;'), chunk(2, 'stdout', '52;86m<strong>safe</strong>\u001b[0m plain\u001b[7minverse\u001b[0m'), chunk(3, 'stdout', unsupported), chunk(4, 'stderr', '\u001b[31merror\u001b[0m')]}
        content={null}
        pending={false}
      />,
    )

    const styled = screen.getByText('<strong>safe</strong>')
    expect(styled).toHaveClass('ansi-segment--bold', 'ansi-segment--underline')
    expect(styled).toHaveStyle({ color: 'rgb(18, 52, 86)' })
    const outputDocument = styled.closest('.ansi-output')
    expect(screen.getByText('inverse')).toHaveStyle({
      color: 'var(--ansi-terminal-background)',
      backgroundColor: 'var(--ansi-terminal-foreground)',
    })
    expect(outputDocument).toHaveTextContent('<strong>safe</strong> plaininverse\u241b[2Jcursor')
    expect(outputDocument?.querySelector('strong')).not.toBeInTheDocument()
    expect(outputDocument?.querySelector('script')).not.toBeInTheDocument()
    expect(outputDocument?.querySelector('a')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Copy output' }))
    await waitFor(() => expect(writeClipboard).toHaveBeenLastCalledWith('<strong>safe</strong> plaininversecursorerror'))
    expect(screen.getByText('error')).toHaveStyle({
      color: 'rgb(197, 15, 31)',
    })
  })

  it('keeps a successful Run status compact outside stdout', () => {
    const run: OperationResult = {
      resultType: 'run',
      status: 'completed',
      exitCode: 0,
      exception: null,
      elapsed: '00:00:00.5872497',
      outputTruncated: false,
      identity: {
        runtimeVersion: '10.0.9',
        runtimeCommit: 'runtime-commit',
        runtimeImageId: 'sha256:image',
        rid: 'linux-x64',
        architecture: 'x64',
      },
    }
    render(
      <>
        <RunStatus result={run.resultType === 'run' ? run : undefined} />
        <OperationResults output={outputManifest('run', 'Run', 'runtime-output')} results={[run]} events={[chunk(1, 'stdout', 'done\n')]} content={null} pending={false} />
      </>,
    )

    const terminal = screen.getByText('done').closest('.terminal-view')
    const status = screen.getByRole('status', { name: 'Run status' })
    expect(status).toHaveTextContent('Exit 0')
    expect(status).toHaveTextContent('587 ms')
    expect(status).not.toHaveTextContent('completed')
    expect(terminal?.querySelector('.run-status')).not.toBeInTheDocument()
  })

  it('scales terminal text with the shared code size without scaling result chrome', () => {
    const run: OperationResult = {
      resultType: 'run',
      status: 'completed',
      exitCode: 0,
      exception: null,
      elapsed: '00:00:00.1000000',
      outputTruncated: false,
      identity: {
        runtimeVersion: '10.0.9',
        runtimeCommit: 'runtime-commit',
        runtimeImageId: 'sha256:image',
        rid: 'linux-x64',
        architecture: 'x64',
      },
    }
    render(
      <div className="workbench" style={{ '--code-font-size': '18px' } as CSSProperties}>
        <RunStatus result={run.resultType === 'run' ? run : undefined} />
        <OperationResults output={outputManifest('run', 'Run', 'runtime-output')} results={[run]} events={[chunk(1, 'stdout', 'scaled output\n')]} content={null} pending={false} />
      </div>,
    )

    const workbench = document.querySelector('.workbench')
    expect(workbench).toHaveStyle({ '--code-font-size': '18px' })
    expect(getComputedStyle(screen.getByText('scaled output')).fontSize).toBe('var(--code-font-size)')
    expect(getComputedStyle(screen.getByText('Exit 0').closest('.run-status') as Element).fontSize).toBe('9px')
  })

  it('scales diagnostics without exposing raw operation events as a result tab', () => {
    const diagnostic: OperationEvent = {
      operationId: 'op-test',
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: 'trace-test',
      payload: {
        kind: 'diagnostic',
        diagnostic: {
          source: 'compiler',
          code: 'CS1002',
          severity: 'error',
          message: '; expected',
          filePath: 'Program.cs',
          range: {
            startLine: 0,
            startCharacter: 10,
            endLine: 0,
            endCharacter: 10,
          },
          relatedInformation: [],
          tags: [],
          workspaceRevision: 1,
          selectionRevision: 1,
        },
      },
    }

    render(
      <div className="workbench" style={{ '--code-font-size': '18px' } as CSSProperties}>
        <OperationResults output={undefined} results={[]} events={[]} activityEvents={[diagnostic]} content={null} pending={false} />
      </div>,
    )

    expect(getComputedStyle(document.querySelector('.diagnostics-view') as Element).fontSize).toBe('var(--code-font-size)')
    const diagnosticsTabStyle = getComputedStyle(screen.getByRole('tab', { name: 'Diagnostics (1)' }))
    expect(diagnosticsTabStyle.fontSize).not.toBe('var(--code-font-size)')
    expect(diagnosticsTabStyle.width).toBe('max-content')
    expect(diagnosticsTabStyle.maxWidth).toBe('none')
    expect(diagnosticsTabStyle.overflow).toBe('visible')
    expect(diagnosticsTabStyle.textOverflow).toBe('clip')
    expect(diagnosticsTabStyle.whiteSpace).toBe('nowrap')

    expect(screen.queryByRole('tab', { name: /^Events/ })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Operation events')).not.toBeInTheDocument()
  })

  it('does not reserve a Run status row when stdout is empty', () => {
    render(
      <OperationResults
        output={outputManifest('run', 'Run', 'runtime-output')}
        results={[
          {
            resultType: 'run',
            status: 'completed',
            exitCode: 0,
            exception: null,
            elapsed: '00:00:00.0100000',
            outputTruncated: false,
            identity: {
              runtimeVersion: '10.0.9',
              runtimeCommit: 'runtime-commit',
              runtimeImageId: 'sha256:image',
              rid: 'linux-x64',
              architecture: 'x64',
            },
          },
        ]}
        events={[]}
        content={null}
        pending={false}
      />,
    )

    const emptyOutput = screen.getByText('No output.')
    const terminal = emptyOutput.closest('.terminal-view')
    expect(emptyOutput).toHaveClass('result-tab-empty')
    expect(terminal?.querySelector('.run-status')).not.toBeInTheDocument()
  })

  it('keeps exception details in a compact message slot beside the Run metrics', () => {
    const exceptionText = 'The operation failed with a deliberately long message for a narrow result pane.'
    render(
      <RunStatus
        result={{
          resultType: 'run',
          status: 'user-exception',
          exitCode: 134,
          exception: {
            typeName: 'System.InvalidOperationException',
            message: exceptionText,
          },
          elapsed: '00:00:01.2500000',
          outputTruncated: false,
          identity: {
            runtimeVersion: '10.0.9',
            runtimeCommit: 'runtime-commit',
            runtimeImageId: 'sha256:image',
            rid: 'linux-x64',
            architecture: 'x64',
          },
        }}
      />,
    )

    const summary = screen.getByText('Exception').closest('.run-status')
    const message = summary?.querySelector('.run-status-message span')
    expect(message).toHaveAttribute('title', `System.InvalidOperationException: ${exceptionText}`)
    expect(summary?.querySelector('.run-status-metrics')).toHaveTextContent('Exit 134')
    expect(summary?.querySelector('.run-status-metrics')).toHaveTextContent('1.25 s')
  })

  it('renders a user exception and its stack/inner exception in Diagnostics', () => {
    const run: OperationResult = {
      resultType: 'run',
      status: 'user-exception',
      exitCode: 1,
      exception: {
        typeName: 'System.InvalidOperationException',
        message: 'outer failure',
        stackTrace: '   at Program.Main() in Program.cs:line 4',
        innerException: {
          typeName: 'System.ArgumentException',
          message: 'inner failure',
          stackTrace: '   at Program.Throw() in Program.cs:line 9',
          innerException: null,
        },
      },
      elapsed: '00:00:00.1000000',
      outputTruncated: false,
      identity: {
        runtimeVersion: '10.0.9',
        runtimeCommit: 'runtime-commit',
        runtimeImageId: 'sha256:image',
        rid: 'linux-x64',
        architecture: 'x64',
      },
    }

    render(<OperationResults output={outputManifest('run', 'Run', 'runtime-output')} results={[run]} events={[]} content={null} pending={false} failure={new Error('Run finished with user-exception.')} attentionKey="run-exception" />)

    const exception = screen.getByRole('alert', { name: 'Runtime exception' })
    expect(exception).toHaveTextContent('System.InvalidOperationException: outer failure')
    expect(exception).toHaveTextContent('at Program.Main() in Program.cs:line 4')
    expect(exception).toHaveTextContent('InnerException1: System.ArgumentException: inner failure')
    expect(exception).toHaveTextContent('at Program.Throw() in Program.cs:line 9')
    expect(screen.queryByText('Run finished with user-exception.')).not.toBeInTheDocument()
    expect(screen.queryByText('Operation failed')).not.toBeInTheDocument()
  })

  it('keeps JIT completion status in the compact bottom-status component', () => {
    render(
      <JitStatus
        result={{
          resultType: 'jit',
          status: 'completed',
          methods: [],
          elapsed: '00:00:00.1000000',
          identity: {
            runtimeVersion: '10.0.9',
            runtimeCommit: 'runtime-commit',
            runtimeImageId: 'sha256:image',
            rid: 'linux-x64',
            architecture: 'x64',
            jitVersion: '10.0.9',
            jitCommit: 'jit-commit',
            cpuFeatureProfile: 'x64-v2',
            tieringPolicy: 'tier0-diffable',
            pgoPolicy: 'disabled',
            jitProvider: 'coreclr-jitdisasm',
            inspectionMethod: 'prepare-method',
          },
        }}
      />,
    )

    expect(screen.getByRole('status', { name: 'JIT status' })).toHaveTextContent('JIT ready100 ms')
  })

  it('shows rendered IL from the operation-scoped content response', () => {
    render(
      <OperationResults
        output={outputManifest('il', 'IL', 'il')}
        results={[
          {
            resultType: 'artifact-render',
            outcome: 'succeeded',
            contentRef: `sha256:${'b'.repeat(64)}`,
            mediaType: 'text/plain',
            linkedRanges: [],
            diagnostics: [],
          },
        ]}
        events={[]}
        content={{
          contentRef: `sha256:${'b'.repeat(64)}`,
          mediaType: 'text/plain',
          text: '.method public static void Main()',
          loading: false,
          error: null,
        }}
        pending={false}
      />,
    )

    expect(screen.getByRole('tab', { name: 'IL' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('textbox', { name: 'Intermediate language' })).toHaveTextContent('.method public static void Main()')
    expect(screen.getByRole('textbox', { name: 'Intermediate language' }).closest('.cm-editor')).not.toHaveClass('cm-lineWrapping')
    expect(screen.queryByRole('tab', { name: 'Identity' })).not.toBeInTheDocument()
  })

  it('colors linked IL instructions and navigates to the matching source range', async () => {
    const onNavigate = vi.fn()
    const onAssociationsChange = vi.fn()
    render(
      <OperationResults
        output={outputManifest('il', 'IL', 'il')}
        results={[
          {
            resultType: 'artifact-render',
            outcome: 'succeeded',
            contentRef: `sha256:${'c'.repeat(64)}`,
            mediaType: 'text/plain',
            linkedRanges: [
              {
                sourceFilePath: '/workspace/Program.cs',
                sourceRange: {
                  startLine: 2,
                  startCharacter: 4,
                  endLine: 2,
                  endCharacter: 23,
                },
                outputRange: {
                  startLine: 1,
                  startCharacter: 0,
                  endLine: 1,
                  endCharacter: 1,
                },
              },
            ],
            diagnostics: [],
          },
        ]}
        events={[]}
        content={{
          contentRef: `sha256:${'c'.repeat(64)}`,
          mediaType: 'text/plain',
          text: '.method Value()\n  IL_0000: ldc.i4.1\n  IL_0001: ret',
          loading: false,
          error: null,
        }}
        pending={false}
        sourceFiles={[
          {
            path: 'Program.cs',
            text: 'class Program\n{\n    static int Value();\n}',
          },
        ]}
        onNavigateToSource={onNavigate}
        onSourceAssociationsChange={onAssociationsChange}
      />,
    )

    const instruction = Array.from(document.querySelectorAll<HTMLElement>('.cm-line')).find((line) => line.textContent?.includes('IL_0000'))
    if (!instruction) throw new Error('The linked IL instruction was not rendered.')
    await waitFor(() => expect(instruction).toHaveClass('source-association'))
    const editorElement = instruction.closest<HTMLElement>('.cm-editor')
    const editor = editorElement ? EditorView.findFromDOM(editorElement) : null
    if (!editor) throw new Error('The linked IL CodeMirror editor was not available.')
    const linkedLine = editor.state.doc.line(2)
    act(() => activateHover(editor, linkedLine.from + 2, 1))
    await waitFor(() => expect(document.querySelector('.code-document-source-tooltip')).toHaveTextContent('Program.cs:3:5'))
    fireEvent.click(instruction, { button: 0, detail: 1 })
    await waitFor(() =>
      expect(onNavigate).toHaveBeenCalledWith(
        expect.objectContaining({
          documentPath: 'Program.cs',
          range: { startLine: 3, startColumn: 5, endLine: 3, endColumn: 24 },
        }),
      ),
    )
    expect(onAssociationsChange).toHaveBeenCalledWith([expect.objectContaining({ documentPath: 'Program.cs' })])
  })

  it('returns from Diagnostics and re-reveals the same linked IL association', async () => {
    const onAssociationsChange = vi.fn()
    const resultView = (activeKey: string | null, revision: number) => (
      <OperationResults
        output={outputManifest('il', 'IL', 'il')}
        results={[
          {
            resultType: 'artifact-render',
            outcome: 'succeeded',
            mediaType: 'text/plain',
            linkedRanges: [
              {
                sourceFilePath: 'Program.cs',
                sourceRange: {
                  startLine: 0,
                  startCharacter: 0,
                  endLine: 0,
                  endCharacter: 5,
                },
                outputRange: {
                  startLine: 0,
                  startCharacter: 0,
                  endLine: 0,
                  endCharacter: 1,
                },
              },
            ],
            diagnostics: [],
          },
        ]}
        events={[]}
        content={{
          contentRef: `sha256:${'c'.repeat(64)}`,
          mediaType: 'text/plain',
          text: 'IL_0000: ret',
          loading: false,
          error: null,
        }}
        pending={false}
        sourceFiles={[{ path: 'Program.cs', text: 'Value' }]}
        activeSourceAssociationKey={activeKey}
        activeSourceAssociationRevision={revision}
        onSourceAssociationsChange={onAssociationsChange}
      />
    )
    const view = render(resultView(null, 0))
    await waitFor(() => expect(onAssociationsChange).toHaveBeenCalled())
    const associations = onAssociationsChange.mock.calls.at(-1)?.[0] as SourceAssociation[] | undefined
    const association = associations?.[0]
    if (!association) throw new Error('Expected an IL source association.')

    fireEvent.click(screen.getByRole('tab', { name: 'Diagnostics' }))
    expect(screen.getByRole('tab', { name: 'Diagnostics' })).toHaveAttribute('aria-selected', 'true')
    view.rerender(resultView(association.key, 1))
    await waitFor(() => expect(screen.getByRole('tab', { name: 'IL' })).toHaveAttribute('aria-selected', 'true'))
    view.rerender(resultView(association.key, 2))
    expect(screen.getByRole('tab', { name: 'IL' })).toHaveAttribute('aria-selected', 'true')
  })

  it('keeps the core result tabs and stable output while a new revision is pending or fails', () => {
    const stableContent: OperationContentView = {
      contentRef: `sha256:${'e'.repeat(64)}`,
      mediaType: 'text/plain',
      text: 'public static class PreviousResult {}',
      loading: false,
      error: null,
    }
    const view = render(<OperationResults output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')} results={[]} events={[]} content={stableContent} pending={false} />)
    const resultTabs = screen.getByRole('tablist', { name: 'Result views' })
    expect(within(resultTabs).getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Diagnostics', 'Decompiled C#'])
    expect(screen.getByRole('textbox', { name: 'Decompiled C sharp' })).toHaveTextContent('PreviousResult')

    view.rerender(<OperationResults output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')} results={[]} events={[]} activityResults={[]} activityEvents={[]} content={stableContent} pending />)
    expect(screen.getByRole('textbox', { name: 'Decompiled C sharp' })).toHaveTextContent('PreviousResult')
    expect(within(resultTabs).getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Diagnostics', 'Decompiled C#'])

    const diagnostic: OperationEvent = {
      operationId: 'op-latest',
      sequence: 1,
      timestampUtc: new Date().toISOString(),
      traceId: 'trace-latest',
      payload: {
        kind: 'diagnostic',
        diagnostic: {
          source: 'compiler',
          code: 'CS1002',
          severity: 'error',
          message: '; expected',
          filePath: 'Program.cs',
          range: {
            startLine: 0,
            startCharacter: 10,
            endLine: 0,
            endCharacter: 10,
          },
          relatedInformation: [],
          tags: [],
          workspaceRevision: 4,
          selectionRevision: 2,
        },
      },
    }
    view.rerender(
      <OperationResults
        output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')}
        results={[]}
        events={[]}
        activityResults={[]}
        activityEvents={[diagnostic]}
        content={stableContent}
        pending={false}
        failure={new Error('Compilation failed. Fix the reported errors and try again.')}
        attentionKey="workflow-latest"
      />,
    )
    expect(screen.getByRole('tab', { name: 'Diagnostics (1)' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByText('; expected')).toBeVisible()
    expect(screen.getByText(/Compilation failed/)).toBeVisible()

    view.rerender(<OperationResults output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')} results={[]} events={[]} activityResults={[]} activityEvents={[]} content={stableContent} pending attentionKey="workflow-latest" />)
    expect(screen.getByRole('tab', { name: 'Diagnostics' })).toHaveAttribute('aria-selected', 'true')

    view.rerender(
      <OperationResults output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')} results={[]} events={[]} activityResults={[]} activityEvents={[]} content={stableContent} pending={false} attentionKey="workflow-latest" />,
    )
    expect(screen.getByRole('tab', { name: 'Diagnostics' })).toHaveAttribute('aria-selected', 'true')

    view.rerender(
      <OperationResults
        output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')}
        results={[]}
        events={[]}
        activityResults={[]}
        activityEvents={[]}
        content={stableContent}
        pending={false}
        attentionKey="workflow-latest"
        recoveryKey="workflow-recovered"
      />,
    )
    expect(screen.getByRole('textbox', { name: 'Decompiled C sharp' })).toHaveTextContent('PreviousResult')
    expect(screen.getByRole('tab', { name: 'Decompiled C#' })).toHaveAttribute('aria-selected', 'true')
  })

  it('preserves a manually selected Diagnostics tab across successful refreshes', () => {
    const label = 'Diagnostics'
    const tabId = 'diagnostics'
    const view = render(<OperationResults output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')} results={[]} events={[]} content={null} pending={false} />)

    fireEvent.click(screen.getByRole('tab', { name: label }))
    view.rerender(
      <OperationResults
        output={outputManifest('decompiled-csharp', 'Decompiled C#', 'csharp')}
        results={[]}
        events={[]}
        content={{
          contentRef: `sha256:${'f'.repeat(64)}`,
          mediaType: 'text/plain',
          text: 'public static class Refreshed {}',
          loading: false,
          error: null,
        }}
        pending={false}
      />,
    )

    expect(screen.getByRole('tab', { name: label })).toHaveAttribute('data-result-tab', tabId)
    expect(screen.getByRole('tab', { name: label })).toHaveAttribute('aria-selected', 'true')
  })

  it('does not expose operation identities as a result tab', () => {
    const buildIdentity = {
      releaseId: 'release-exact',
      languageId: 'csharp',
      toolchainId: 'roslyn-main',
      compilerVersion: '5.10.0',
      compilerCommit: '708c0a9669c6c996b7e13ea4b161d841bbfdf8b2',
      referenceSetId: 'net11-preview-ref',
      workerImageId: `sha256:${'a'.repeat(64)}`,
    }
    render(
      <OperationResults
        output={outputManifest('ast', 'AST', 'ast-tree')}
        results={[
          {
            resultType: 'ast',
            document: {
              languageId: 'csharp',
              toolchainId: 'roslyn-main',
              workspaceRevision: 3,
              truncated: false,
              root: {
                kind: 'CompilationUnit',
                range: {
                  startLine: 0,
                  startCharacter: 0,
                  endLine: 0,
                  endCharacter: 1,
                },
                properties: {},
                children: [],
              },
            },
            identity: buildIdentity,
          },
          {
            resultType: 'explain',
            document: {
              languageId: 'csharp',
              toolchainId: 'roslyn-main',
              workspaceRevision: 3,
              selectionRevision: 2,
              files: [],
              truncated: false,
            },
            identity: buildIdentity,
          },
          {
            resultType: 'artifact-render',
            outcome: 'succeeded',
            contentRef: `sha256:${'b'.repeat(64)}`,
            mediaType: 'text/plain',
            linkedRanges: [],
            diagnostics: [],
            identity: {
              releaseId: 'release-exact',
              processorId: 'artifacts-default',
              processorVersion: 'ilspy/10.1.0.8386+ilverify/10.0.9',
              workerImageId: `sha256:${'c'.repeat(64)}`,
            },
          },
        ]}
        events={[]}
        content={null}
        pending={false}
      />,
    )

    expect(getComputedStyle(document.querySelector('.ast-layout') as Element).fontSize).toBe('var(--code-font-size)')
    expect(document.querySelector('.ast-toolbar')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Expand the AST' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Collapse the AST' })).not.toBeInTheDocument()

    expect(screen.queryByRole('tab', { name: 'Identity' })).not.toBeInTheDocument()
    expect(screen.queryByText('708c0a9669c6c996b7e13ea4b161d841bbfdf8b2')).not.toBeInTheDocument()
    expect(screen.queryByText('ilspy/10.1.0.8386+ilverify/10.0.9')).not.toBeInTheDocument()
    expect(screen.queryByText(`sha256:${'c'.repeat(64)}`)).not.toBeInTheDocument()
  })

  it('renders AST metadata in the compact bottom status presentation', () => {
    render(
      <AstStatus
        document={{
          languageId: 'csharp',
          toolchainId: 'roslyn-main',
          workspaceRevision: 3,
          truncated: true,
          root: {
            kind: 'Workspace',
            range: {
              startLine: 0,
              startCharacter: 0,
              endLine: 0,
              endCharacter: 0,
            },
            properties: {},
            children: [],
          },
        }}
        nodeCount={135}
      />,
    )

    const status = screen.getByRole('status', { name: 'AST status' })
    expect(status).toHaveTextContent('Syntax tree')
    expect(status).toHaveTextContent('csharp')
    expect(status).toHaveTextContent('135 nodes')
    expect(status).toHaveTextContent('Truncated')
  })

  it('publishes AST hit targets without selecting or coloring the source initially', async () => {
    const onNavigate = vi.fn()
    const onAssociationsChange = vi.fn()
    render(
      <OperationResults
        output={outputManifest('ast', 'AST', 'ast-tree')}
        results={[
          {
            resultType: 'ast',
            document: {
              languageId: 'csharp',
              toolchainId: 'roslyn-main',
              workspaceRevision: 3,
              truncated: false,
              root: {
                kind: 'Workspace',
                range: {
                  startLine: 0,
                  startCharacter: 0,
                  endLine: 0,
                  endCharacter: 0,
                },
                properties: {},
                children: [
                  {
                    kind: 'Document',
                    range: {
                      startLine: 0,
                      startCharacter: 0,
                      endLine: 0,
                      endCharacter: 7,
                    },
                    properties: { path: 'Program.cs' },
                    children: [
                      {
                        kind: 'IdentifierName',
                        range: {
                          startLine: 0,
                          startCharacter: 0,
                          endLine: 0,
                          endCharacter: 7,
                        },
                        properties: {
                          type: 'IdentifierNameSyntax',
                          isNode: 'true',
                        },
                        children: [],
                      },
                    ],
                  },
                ],
              },
            },
          },
        ]}
        events={[]}
        content={null}
        pending={false}
        onNavigateToSource={onNavigate}
        onSourceAssociationsChange={onAssociationsChange}
      />,
    )

    await waitFor(() =>
      expect(onAssociationsChange).toHaveBeenCalledWith([
        expect.objectContaining({
          documentPath: 'Program.cs',
          presentation: 'active-range',
        }),
      ]),
    )
    expect(onNavigate).not.toHaveBeenCalled()
    expect(screen.getByText('Select a syntax item to inspect it.')).toBeVisible()

    fireEvent.click(screen.getByRole('treeitem', { name: /IdentifierName/ }))
    expect(onNavigate).toHaveBeenCalledWith(
      expect.objectContaining({
        documentPath: 'Program.cs',
        range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 8 },
      }),
    )
  })

  it('shows generated IL from an intermediate-language artifact render', () => {
    render(
      <OperationResults
        output={outputManifest('generated-il', 'Generated IL', 'generated-il')}
        results={[
          {
            resultType: 'artifact-render',
            outcome: 'succeeded',
            contentRef: `sha256:${'c'.repeat(64)}`,
            mediaType: 'text/plain',
            linkedRanges: [],
            diagnostics: [],
          },
        ]}
        events={[]}
        content={{
          contentRef: `sha256:${'c'.repeat(64)}`,
          mediaType: 'text/plain',
          text: '.assembly MiniLang.Program {}',
          loading: false,
          error: null,
        }}
        pending={false}
      />,
    )

    expect(screen.getByRole('tab', { name: 'Generated IL' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('textbox', { name: 'Generated intermediate language' })).toHaveTextContent('.assembly MiniLang.Program {}')
  })

  it('renders downloaded generated source documents with their declared language', () => {
    const firstRef = `sha256:${'1'.repeat(64)}`
    const secondRef = `sha256:${'2'.repeat(64)}`
    render(
      <OperationResults
        output={outputManifest('generated-source', 'Generated Source', 'source')}
        results={[
          {
            resultType: 'generated-source',
            documents: [
              {
                path: 'Generated/First.g.cs',
                contentRef: firstRef,
                languageId: 'csharp',
                generatorId: 'test-generator',
              },
              {
                path: 'Generated/Second.g.cs',
                contentRef: secondRef,
                languageId: 'csharp',
                generatorId: 'test-generator',
              },
            ],
            identity: {
              releaseId: 'release-exact',
              languageId: 'csharp',
              toolchainId: 'roslyn-stable',
              compilerVersion: '5.6.0',
              compilerCommit: 'compiler-commit',
              referenceSetId: 'net10-ref',
              workerImageId: `sha256:${'a'.repeat(64)}`,
            },
            workspaceRevision: 3,
            selectionRevision: 2,
          },
        ]}
        events={[]}
        content={null}
        generatedSourceContents={[
          {
            path: 'Generated/First.g.cs',
            contentRef: firstRef,
            languageId: 'csharp',
            generatorId: 'test-generator',
            text: 'public static class FirstGenerated {}',
            loading: false,
            error: null,
          },
          {
            path: 'Generated/Second.g.cs',
            contentRef: secondRef,
            languageId: 'csharp',
            generatorId: 'test-generator',
            text: 'public static class SecondGenerated {}',
            loading: false,
            error: null,
          },
        ]}
        pending={false}
      />,
    )

    expect(screen.getByRole('tab', { name: 'Generated Source' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByLabelText('Generated source document')).toHaveValue(`${firstRef}:Generated/First.g.cs`)
    expect(screen.getByRole('textbox', { name: 'Generated source Generated/First.g.cs' })).toHaveTextContent('FirstGenerated')

    fireEvent.change(screen.getByLabelText('Generated source document'), {
      target: { value: `${secondRef}:Generated/Second.g.cs` },
    })
    expect(screen.getByRole('textbox', { name: 'Generated source Generated/Second.g.cs' })).toHaveTextContent('SecondGenerated')
  })

  it('renders structured verification findings', () => {
    render(
      <OperationResults
        output={outputManifest('il-verify', 'IL Verify', 'verification')}
        results={[
          {
            resultType: 'artifact-verification',
            outcome: 'findings',
            findings: [
              {
                code: 'stack-unbalanced',
                message: 'Evaluation stack is not balanced.',
                metadataToken: 0x06000001,
                typeName: 'Program',
                methodName: 'Main',
              },
            ],
            verifierId: 'microsoft-ilverification',
            verifierVersion: '10.0.0',
          },
        ]}
        events={[]}
        content={null}
        pending={false}
      />,
    )

    expect(screen.getByText('stack-unbalanced')).toBeVisible()
    expect(screen.getByText('Evaluation stack is not balanced.')).toBeVisible()
    expect(screen.getByText('0x06000001')).toBeVisible()
    expect(getComputedStyle(document.querySelector('.verification-view') as Element).fontSize).toBe('var(--code-font-size)')
    expect(getComputedStyle(document.querySelector('.verification-summary') as Element).fontSize).toBe('9px')
  })

  it('shows JIT assembly as SSE chunks arrive before canonical content', () => {
    render(<OperationResults output={outputManifest('jit-asm', 'JIT ASM', 'asm')} results={[]} events={[chunk(1, 'jit', 'G_M000_IG01:\n  push rbp\n')]} content={null} pending />)

    expect(screen.getByRole('tab', { name: 'JIT' })).toHaveAttribute('aria-selected', 'true')
    const assembly = screen.getByRole('textbox', { name: 'JIT assembly' })
    expect(assembly).toHaveTextContent('G_M000_IG01:')
    expect(assembly).toHaveTextContent('push rbp')
  })

  it('keeps linked source ranges aligned across three compact All-method sections', () => {
    const text = `; Assembly listing for method Program:A():int (FullOpts)
  mov eax, 1

; Assembly listing for method Program:B():int (FullOpts)
  mov eax, 2

; Assembly listing for method Program:C():int (FullOpts)
  mov eax, 3`
    const files = [
      {
        path: 'Program.cs',
        text: 'static int A() => 1;\nstatic int B() => 2;\nstatic int C() => 3;',
      },
    ]
    const sections = parseJitAssembly(
      text,
      ['A', 'B', 'C'].map((name, index) => ({
        methodId: name.toLowerCase(),
        displayName: `Program.${name}`,
        nativeCodeSize: 3,
        instructionCount: 1,
        linkedRanges: [
          {
            sourceFilePath: '/workspace/Program.cs',
            sourceRange: {
              startLine: index,
              startCharacter: 0,
              endLine: index,
              endCharacter: 20,
            },
            outputRange: {
              startLine: 1,
              startCharacter: 0,
              endLine: 1,
              endCharacter: 1,
            },
            precision: 'sequence-point' as const,
          },
        ],
      })),
      files,
    )

    expect(createJitOutputSourceLinks(sections, files).map((link) => link.startLine)).toEqual([2, 5, 8])
  })

  it('shows, copies, and source-navigates all user JIT methods without a filter toolbar', async () => {
    const writeClipboard = vi.fn(async (_value: string) => {})
    const onNavigate = vi.fn()
    const onAssociationsChange = vi.fn()
    vi.stubGlobal('navigator', { clipboard: { writeText: writeClipboard } })
    const jitText = `; Assembly listing for method Program:<Main>$(System.String[]):int (FullOpts)
G_M000_IG01:
  call MyClass:Sum[int,int]():int
  ret
; Total bytes of code 6

; Assembly listing for method JitInspectorProgram:RunAsync(System.String[]):int (FullOpts)
G_M_HELPER_IG01:
  ret
; Total bytes of code 1

; Assembly listing for method MyClass:Sum[int,int]():int (FullOpts)
G_M001_IG01:
  add eax, 2
  ret
; Total bytes of code 4`
    render(
      <OperationResults
        output={outputManifest('jit-asm', 'JIT ASM', 'asm')}
        results={[
          {
            resultType: 'jit',
            status: 'completed',
            rawTextRef: `sha256:${'d'.repeat(64)}`,
            methods: [
              {
                methodId: 'main',
                displayName: 'Program.<Main>$',
                nativeCodeSize: 6,
                instructionCount: 2,
                linkedRanges: [],
              },
              {
                methodId: 'sum',
                displayName: 'MyClass.Sum',
                nativeCodeSize: 4,
                instructionCount: 2,
                linkedRanges: [
                  {
                    sourceFilePath: '/workspace/Program.cs',
                    sourceRange: {
                      startLine: 2,
                      startCharacter: 0,
                      endLine: 2,
                      endCharacter: 20,
                    },
                    outputRange: {
                      startLine: 2,
                      startCharacter: 0,
                      endLine: 2,
                      endCharacter: 1,
                    },
                    precision: 'sequence-point',
                  },
                ],
              },
            ],
            elapsed: '00:00:00.1000000',
            identity: {
              runtimeVersion: '10.0.9',
              runtimeCommit: 'runtime-commit',
              runtimeImageId: 'sha256:image',
              rid: 'linux-x64',
              architecture: 'x64',
              jitVersion: '10.0.9',
              jitCommit: 'jit-commit',
              cpuFeatureProfile: 'x64-v2',
              tieringPolicy: 'tier0-diffable',
              pgoPolicy: 'disabled',
              jitProvider: 'coreclr-jitdisasm',
              inspectionMethod: 'prepare-method',
            },
          },
        ]}
        events={[]}
        content={{
          contentRef: `sha256:${'d'.repeat(64)}`,
          mediaType: 'text/x-asm',
          text: jitText,
          loading: false,
          error: null,
        }}
        pending={false}
        sourceFiles={[
          {
            path: 'Program.cs',
            text: 'using System;\nConsole.WriteLine();\nstatic int Sum(int left, int right) => left + right;',
          },
        ]}
        onNavigateToSource={onNavigate}
        onSourceAssociationsChange={onAssociationsChange}
      />,
    )

    expect(screen.queryByLabelText('JIT method')).not.toBeInTheDocument()
    expect(document.querySelector('.jit-toolbar')).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'JIT assembly' })).toHaveTextContent('add eax, 2')
    expect(screen.getByRole('textbox', { name: 'JIT assembly' })).toHaveTextContent('call MyClass:Sum')
    expect(screen.getByRole('textbox', { name: 'JIT assembly' })).not.toHaveTextContent('JitInspectorProgram:RunAsync')
    const selectedJitLines = Array.from(document.querySelectorAll<HTMLElement>('.cm-line'))
    expect(selectedJitLines.find((line) => line.textContent?.includes('add eax, 2'))).toHaveClass('source-association')
    const sumInstruction = selectedJitLines.find((line) => line.textContent?.includes('add eax, 2'))
    if (!sumInstruction) throw new Error('The linked Sum instruction was not rendered.')
    fireEvent.click(sumInstruction, { button: 0, detail: 1 })
    await waitFor(() =>
      expect(onNavigate).toHaveBeenCalledWith(
        expect.objectContaining({
          documentPath: 'Program.cs',
          range: expect.objectContaining({ startLine: 3 }),
        }),
      ),
    )
    await waitFor(() => expect(onAssociationsChange).toHaveBeenCalled())
    fireEvent.click(screen.getByRole('button', { name: 'Copy output' }))
    await waitFor(() => expect(writeClipboard).toHaveBeenCalledOnce())
    expect(writeClipboard.mock.calls[0]?.[0]).toContain('add eax, 2')
    expect(writeClipboard.mock.calls[0]?.[0]).toContain('call MyClass:Sum')
    expect(writeClipboard.mock.calls[0]?.[0]).not.toContain('JitInspectorProgram:RunAsync')
    expect(screen.getByRole('textbox', { name: 'JIT assembly' })).not.toHaveTextContent('JitInspectorProgram:RunAsync')
    const mainMethodLine = Array.from(document.querySelectorAll<HTMLElement>('.cm-line')).find((line) => line.textContent?.includes('Program:<Main>$'))
    if (!mainMethodLine) throw new Error('The main JIT method line was not rendered.')
    fireEvent.click(mainMethodLine, { button: 0, detail: 1 })
    await waitFor(() =>
      expect(onNavigate).toHaveBeenLastCalledWith(
        expect.objectContaining({
          documentPath: 'Program.cs',
          range: expect.objectContaining({ startLine: 2 }),
        }),
      ),
    )
  })

  it('keeps one compact JIT method and reports an honest approximate fallback', async () => {
    const onAssociationsChange = vi.fn()
    const jitText = `JIT environment: tier0-diffable
; Assembly listing for method Program:Main():void (FullOpts)
G_M000_IG01:
  ret
; Total bytes of code 1`
    render(
      <OperationResults
        output={outputManifest('jit-asm', 'JIT ASM', 'asm')}
        results={[
          {
            resultType: 'jit',
            status: 'completed',
            rawTextRef: `sha256:${'d'.repeat(64)}`,
            methods: [
              {
                methodId: 'main',
                displayName: 'Program:Main():void',
                nativeCodeSize: 1,
                instructionCount: 1,
                linkedRanges: [],
              },
            ],
            elapsed: '00:00:00.1000000',
            identity: {
              runtimeVersion: '10.0.9',
              runtimeCommit: 'runtime-commit',
              runtimeImageId: 'sha256:image',
              rid: 'linux-x64',
              architecture: 'x64',
              jitVersion: '10.0.9',
              jitCommit: 'jit-commit',
              cpuFeatureProfile: 'x64-v2',
              tieringPolicy: 'tier0-diffable',
              pgoPolicy: 'disabled',
              jitProvider: 'coreclr-jitdisasm',
              inspectionMethod: 'prepare-method',
            },
          },
        ]}
        events={[]}
        content={{
          contentRef: `sha256:${'d'.repeat(64)}`,
          mediaType: 'text/x-asm',
          text: jitText,
          loading: false,
          error: null,
        }}
        pending={false}
        sourceFiles={[{ path: 'Program.cs', text: 'static void Main() {}' }]}
        onSourceAssociationsChange={onAssociationsChange}
      />,
    )

    expect(screen.queryByLabelText('JIT method')).not.toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'JIT assembly' })).not.toHaveTextContent('JIT environment')
    await waitFor(() =>
      expect(onAssociationsChange).toHaveBeenCalledWith([
        expect.objectContaining({
          label: 'Approximate JIT source: Program.cs:1',
        }),
      ]),
    )
    expect(screen.getByRole('textbox', { name: 'JIT assembly' })).not.toHaveTextContent('JIT environment: tier0-diffable')
  })

  it('always presents the complete user-method result without a toolbar', () => {
    const jitText = `; Assembly listing for method Program:A():int (FullOpts)
  mov eax, 1
  ret
; Total bytes of code 6

; Assembly listing for method JitInspectorProgram:RunAsync():int (FullOpts)
  ret
; Total bytes of code 1

; Assembly listing for method Program:B():int (FullOpts)
  mov eax, 2
  ret
; Total bytes of code 6`
    render(
      <OperationResults
        output={outputManifest('jit-asm', 'JIT ASM', 'asm')}
        results={[
          {
            resultType: 'jit',
            status: 'completed',
            rawTextRef: `sha256:${'e'.repeat(64)}`,
            methods: [
              {
                methodId: 'a',
                displayName: 'Program.A',
                nativeCodeSize: 6,
                instructionCount: 2,
                linkedRanges: [],
              },
              {
                methodId: 'b',
                displayName: 'Program.B',
                nativeCodeSize: 6,
                instructionCount: 2,
                linkedRanges: [],
              },
            ],
            elapsed: '00:00:00.1000000',
            identity: {
              runtimeVersion: '10.0.9',
              runtimeCommit: 'runtime-commit',
              runtimeImageId: 'sha256:image',
              rid: 'linux-x64',
              architecture: 'x64',
              jitVersion: '10.0.9',
              jitCommit: 'jit-commit',
              cpuFeatureProfile: 'x64-v2',
              tieringPolicy: 'tier0-diffable',
              pgoPolicy: 'disabled',
              jitProvider: 'coreclr-jitdisasm',
              inspectionMethod: 'prepare-method',
            },
          },
        ]}
        events={[]}
        content={{
          contentRef: `sha256:${'e'.repeat(64)}`,
          mediaType: 'text/x-asm',
          text: jitText,
          loading: false,
          error: null,
        }}
        pending={false}
      />,
    )

    expect(screen.queryByLabelText('JIT method')).not.toBeInTheDocument()
    const assembly = screen.getByRole('textbox', { name: 'JIT assembly' })
    expect(assembly).toHaveTextContent('Program:A')
    expect(assembly).toHaveTextContent('Program:B')
    expect(assembly).not.toHaveTextContent('JitInspectorProgram')
    expect(document.querySelector('.jit-toolbar')).not.toBeInTheDocument()
  })

  it('prefers the structured flow timeline for execution-flow output', () => {
    const flow = {
      EventKind: 'sequence-point',
      DocumentPath: 'Program.cs',
      Range: { StartLine: 0, StartColumn: 0, EndLine: 0, EndColumn: 11 },
      ManagedThreadId: 4,
      TaskId: null,
      Name: null,
      Value: null,
      Truncated: false,
    }
    render(<OperationResults output={outputManifest('execution-flow', 'Execution Flow', 'flow')} results={[]} events={[chunk(1, 'flow', JSON.stringify(flow))]} content={null} pending={false} />)

    expect(screen.getByRole('tab', { name: 'Flow' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByText('Program.cs:1:1')).toBeVisible()
    expect(getComputedStyle(document.querySelector('.runtime-flow-list') as Element).fontSize).toBe('var(--code-font-size)')
  })

  it('renders Explain as a structured source-range view', () => {
    render(
      <OperationResults
        output={outputManifest('explain', 'Explain', 'explain')}
        results={[
          {
            resultType: 'explain',
            document: {
              languageId: 'csharp',
              toolchainId: 'roslyn-stable',
              workspaceRevision: 3,
              selectionRevision: 2,
              truncated: false,
              files: [
                {
                  path: 'Program.cs',
                  nodes: [
                    {
                      kind: 'ClassDeclaration',
                      title: 'Class declaration: Program',
                      description: 'Declares a reference type.',
                      range: {
                        startLine: 0,
                        startCharacter: 0,
                        endLine: 0,
                        endCharacter: 16,
                      },
                      depth: 1,
                    },
                  ],
                },
              ],
            },
          },
        ]}
        events={[]}
        content={null}
        pending={false}
      />,
    )

    expect(screen.getByRole('tab', { name: 'Explain' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByText('Class declaration: Program')).toBeVisible()
    expect(getComputedStyle(document.querySelector('.explanation-view') as Element).fontSize).toBe('var(--code-font-size)')
    expect(screen.getByText('Declares a reference type.')).toBeVisible()
    expect(screen.getByText('1:1-1:17')).toBeVisible()
  })
})
