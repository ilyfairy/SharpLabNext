import { describe, expect, it, vi } from 'vitest'

vi.mock('../editor/monacoCore', () => ({
  languages: {
    CompletionItemKind: { Snippet: 28 },
    CompletionItemInsertTextRule: { InsertAsSnippet: 4 },
  },
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
}))

import {
  canConsumeEmptyCompletionRetry,
  emptyCompletionRetryForResult,
  encodeSemanticTokens,
  monacoCodeActions,
  monacoCompletionInsertion,
  monacoCompletionList,
  monacoFoldingRanges,
  monacoLanguageTriggerCharacters,
} from './monacoLanguageClient'

describe('Monaco language adapter', () => {
  it('re-encodes every shared IL semantic token and modifier added after the stable legend', () => {
    expect([
      ...encodeSemanticTokens([
        {
          line: 0,
          character: 0,
          length: 2,
          tokenType: 'typeParameter',
          tokenModifiers: ['declaration'],
        },
        {
          line: 0,
          character: 3,
          length: 4,
          tokenType: 'identifier',
          tokenModifiers: ['definition'],
        },
        {
          line: 1,
          character: 1,
          length: 5,
          tokenType: 'invalid',
          tokenModifiers: ['declaration', 'definition'],
        },
      ]),
    ]).toEqual([0, 0, 2, 6, 32, 0, 3, 4, 26, 64, 1, 1, 5, 27, 96])
  })

  it('only consumes an empty-result retry after the document advances', () => {
    const retry = { documentVersion: 2, lineNumber: 4, column: 7 }
    const insertion = {
      range: {
        startLineNumber: 4,
        startColumn: 7,
        endLineNumber: 4,
        endColumn: 7,
      },
      text: 'a',
    }

    expect(canConsumeEmptyCompletionRetry(null, 3, [insertion], ['.'])).toBe(false)
    expect(canConsumeEmptyCompletionRetry(undefined, 3, [insertion], ['.'])).toBe(false)
    expect(canConsumeEmptyCompletionRetry(retry, 2, [insertion], ['.'])).toBe(false)
    expect(canConsumeEmptyCompletionRetry(retry, 1, [insertion], ['.'])).toBe(false)
    expect(canConsumeEmptyCompletionRetry(retry, 3, [insertion], ['.'])).toBe(true)
  })

  it('does not leak an empty retry across the cursor or replace trigger-character requests', () => {
    const retry = { documentVersion: 2, lineNumber: 4, column: 7 }
    const change = (lineNumber: number, column: number, text: string) => ({
      range: {
        startLineNumber: lineNumber,
        startColumn: column,
        endLineNumber: lineNumber,
        endColumn: column,
      },
      text,
    })

    expect(canConsumeEmptyCompletionRetry(retry, 3, [change(5, 7, 'a')], ['.'])).toBe(false)
    expect(canConsumeEmptyCompletionRetry(retry, 3, [change(4, 7, '.')], ['.'])).toBe(false)
    expect(canConsumeEmptyCompletionRetry(retry, 3, [change(4, 7, ' ')], ['.'])).toBe(false)
    expect(canConsumeEmptyCompletionRetry(retry, 3, [change(4, 7, '\n  ')], ['.'])).toBe(true)
    expect(canConsumeEmptyCompletionRetry(retry, 3, [], ['.'])).toBe(false)
  })

  it('arms one recovery for empty trigger-character results but not for the recovery itself', () => {
    const position = { lineNumber: 4, column: 8 }

    expect(emptyCompletionRetryForResult(3, position, 0, false)).toEqual({
      documentVersion: 3,
      lineNumber: 4,
      column: 8,
    })
    expect(emptyCompletionRetryForResult(3, position, 0, true)).toBeNull()
    expect(emptyCompletionRetryForResult(3, position, 1, false)).toBeNull()
  })

  it('preserves incomplete completion lists so Monaco requests the narrowed word again', () => {
    expect(
      monacoCompletionList({} as never, {} as never, 'Program.cs', {
        isIncomplete: true,
        items: [],
      }),
    ).toEqual({ suggestions: [], incomplete: true })
  })

  it('preserves a complete empty response while the bridge handles its explicit retry', () => {
    expect(
      monacoCompletionList({} as never, {} as never, 'Program.cs', {
        isIncomplete: false,
        items: [],
      }),
    ).toEqual({ suggestions: [] })
  })

  it('keeps complete mixed keyword and snippet lists local while a prefix is extended', () => {
    expect(
      monacoCompletionList(
        {
          getWordUntilPosition: () => ({ word: 'c', startColumn: 1, endColumn: 2 }),
        } as never,
        {} as never,
        'Program.cs',
        {
          isIncomplete: false,
          items: [
            {
              label: 'class',
              filterText: 'class',
              kind: 14,
              raw: { label: 'class', kind: 14 },
              documentVersion: 1,
            },
            {
              label: 'class',
              filterText: 'class',
              kind: 15,
              raw: { label: 'class', kind: 15 },
              documentVersion: 1,
            },
          ],
        },
      ),
    ).toEqual({
      suggestions: [
        expect.objectContaining({ label: 'class', filterText: 'class' }),
        expect.objectContaining({ label: 'class', filterText: 'class' }),
      ],
    })
  })

  it('uses the replaced postfix expression to keep Roslyn foreach snippets visible', () => {
    const source = '        arr.foreach'
    const completion = monacoCompletionList(
      completionModel(source, 13, 20),
      { lineNumber: 1, column: 20 } as never,
      'Program.cs',
      {
        isIncomplete: false,
        items: [
          {
            label: 'foreach',
            filterText: 'foreach',
            kind: 15,
            insertTextFormat: 2,
            textEdit: {
              range: {
                start: { line: 0, character: 8 },
                end: { line: 0, character: 19 },
              },
              newText: `foreach (var item in arr)\n{\n    \${0}\n}`,
            },
            raw: { label: 'foreach' },
            documentVersion: 1,
          },
        ],
      },
    )

    expect(completion.suggestions).toEqual([
      expect.objectContaining({
        label: 'foreach',
        filterText: 'arr.foreach',
        insertText: `foreach (var item in arr)\n{\n    \${0}\n}`,
        insertTextRules: 4,
      }),
    ])
  })

  it.each([
    ['        task.', 14, 'await', 'task.await'],
    ['        task.a', 14, 'await', 'task.await'],
    ['        task.aw', 14, 'await', 'task.await'],
    ['        arr.', 13, 'for', 'arr.for'],
    ['        arr.f', 13, 'foreach', 'arr.foreach'],
    ['        arr.fo', 13, 'foreach', 'arr.foreach'],
  ])('keeps the %s postfix candidate filterable while its suffix is typed', (source, wordStartColumn, label, expectedFilterText) => {
    const positionColumn = source.length + 1
    const completion = monacoCompletionList(
      completionModel(source, wordStartColumn, positionColumn),
      { lineNumber: 1, column: positionColumn } as never,
      'Program.cs',
      {
        isIncomplete: false,
        items: [
          {
            label,
            filterText: label,
            kind: 15,
            textEdit: {
              range: {
                start: { line: 0, character: 8 },
                end: { line: 0, character: source.length },
              },
              newText: label,
            },
            raw: { label },
            documentVersion: 1,
          },
        ],
      },
    )

    expect(completion.suggestions).toEqual([
      expect.objectContaining({ label, filterText: expectedFilterText }),
    ])
  })

  it('keeps the coalesced Roslyn await edit visible and complete on the first commit', () => {
    const source = '        task.await'
    const completion = monacoCompletionList(
      completionModel(source, 14, 19),
      { lineNumber: 1, column: 19 } as never,
      'Program.cs',
      {
        isIncomplete: false,
        items: [
          {
            label: 'await',
            filterText: 'await',
            kind: 14,
            textEdit: {
              range: {
                start: { line: 0, character: 8 },
                end: { line: 0, character: 18 },
              },
              newText: 'await task',
            },
            additionalTextEdits: [
              {
                range: {
                  start: { line: 0, character: 0 },
                  end: { line: 0, character: 0 },
                },
                newText: 'using System.Threading.Tasks;\n\n',
              },
              {
                range: {
                  start: { line: 2, character: 4 },
                  end: { line: 2, character: 4 },
                },
                newText: 'async ',
              },
              {
                range: {
                  start: { line: 2, character: 10 },
                  end: { line: 2, character: 14 },
                },
                newText: 'Task',
              },
            ],
            raw: { label: 'await' },
            documentVersion: 1,
          },
        ],
      },
    )

    expect(completion.suggestions).toEqual([
      expect.objectContaining({
        label: 'await',
        filterText: 'task.await',
        insertText: 'await task',
        additionalTextEdits: [
          expect.objectContaining({ text: 'using System.Threading.Tasks;\n\n' }),
          expect.objectContaining({ text: 'async ' }),
          expect.objectContaining({ text: 'Task' }),
        ],
      }),
    ])
  })

  it('preserves resolved LSP snippets with editable and final tab stops', () => {
    expect(
      monacoCompletionInsertion({
        label: 'class',
        kind: 15,
        insertTextFormat: 2,
        textEdit: {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 5 },
          },
          newText: `class \${1:MyClass}\n{\n    \${0}\n\\}`,
        },
        raw: { label: 'class' },
        documentVersion: 1,
      }),
    ).toEqual({
      insertText: `class \${1:MyClass}\n{\n    \${0}\n\\}`,
      insertTextRules: 4,
    })
  })

  it('preserves repeated numbered placeholders when projecting snippets to Monaco', () => {
    const snippet = `for (int \${1:i} = 0; \${1:i} < \${2:length}; \${1:i}++)\n{\n    \${0}\n\\}`

    expect(
      monacoCompletionInsertion({
        label: 'for',
        kind: 15,
        insertTextFormat: 2,
        textEdit: {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 3 },
          },
          newText: snippet,
        },
        raw: { label: 'for' },
        documentVersion: 1,
      }),
    ).toEqual({
      insertText: snippet,
      insertTextRules: 4,
    })
  })

  it('preserves Roslyn generic signature trigger and retrigger characters', () => {
    expect(monacoLanguageTriggerCharacters('csharp')).toEqual({
      completion: ['.', ':', '<'],
      signature: ['(', ',', '<'],
      signatureRetrigger: [',', ')'],
    })
  })

  it('uses ILSense completion triggers except ordinary spaces for IL', () => {
    expect(monacoLanguageTriggerCharacters('il')).toEqual({
      completion: ['.', '[', ']', ':', "'", '(', ',', '<', '!'],
      signature: ['(', ','],
      signatureRetrigger: [','],
    })
  })

  it('falls back to Monaco indentation folding when the server does not support ranges', () => {
    expect(monacoFoldingRanges(null)).toBeNull()
    expect(
      monacoFoldingRanges([{ startLine: 1, endLine: 4, startCharacter: 0, endCharacter: 1 }]),
    ).toEqual([{ start: 2, end: 5 }])
  })

  it('only offers quick fixes for diagnostics represented by the requested markers', () => {
    const firstMarker = {
      startLineNumber: 1,
      startColumn: 1,
      endLineNumber: 1,
      endColumn: 4,
      message: '[CS1002] Expected semicolon.',
      code: 'CS1002',
      source: 'C#',
    }
    const firstAction = {
      title: "Insert ';'",
      diagnostics: [],
      documentEdits: [
        {
          documentPath: 'Program.cs',
          documentVersion: 1,
          edits: [
            {
              range: {
                start: { line: 0, character: 3 },
                end: { line: 0, character: 3 },
              },
              newText: ';',
            },
          ],
        },
      ],
    }
    const secondAction = {
      title: 'Create name',
      diagnostics: [],
      documentEdits: [
        {
          documentPath: 'Program.cs',
          documentVersion: 1,
          edits: [
            {
              range: {
                start: { line: 1, character: 0 },
                end: { line: 1, character: 3 },
              },
              newText: 'known',
            },
          ],
        },
      ],
    }
    const model = {
      getVersionId: () => 1,
      uri: { toString: () => 'sharplabnext://workspace/Program.cs' },
    }
    const state = {
      path: 'Program.cs',
      model,
      diagnostics: [
        {
          range: {
            start: { line: 0, character: 0 },
            end: { line: 0, character: 3 },
          },
          message: 'Expected semicolon.',
          code: 'CS1002',
          source: 'C#',
          actions: [firstAction],
        },
        {
          range: {
            start: { line: 1, character: 0 },
            end: { line: 1, character: 3 },
          },
          message: 'Unknown name.',
          code: 'CS0103',
          source: 'C#',
          actions: [secondAction],
        },
      ],
      semanticTokens: [],
      symbols: null,
      foldingRanges: null,
    }

    const actions = monacoCodeActions(
      state as never,
      new Map([['Program.cs', state]]) as never,
      [firstMarker] as never,
    )

    expect(actions).toHaveLength(1)
    expect(actions[0]).toMatchObject({
      title: "Insert ';'",
      diagnostics: [firstMarker],
      edit: { edits: [expect.objectContaining({ resource: model.uri })] },
    })
    expect(actions[0]?.title).not.toBe(secondAction.title)
  })
})

function completionModel(source: string, wordStartColumn: number, wordEndColumn: number) {
  return {
    getWordUntilPosition: () => ({
      word: source.slice(wordStartColumn - 1, wordEndColumn - 1),
      startColumn: wordStartColumn,
      endColumn: wordEndColumn,
    }),
    getValueInRange: (range: {
      startLineNumber: number
      startColumn: number
      endLineNumber: number
      endColumn: number
    }) => source.slice(range.startColumn - 1, range.endColumn - 1),
  } as never
}
