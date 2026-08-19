import { describe, expect, it } from 'vitest'
import type { LinkedRange } from '../api/types'
import { createIlSourceLinks } from './ilSourceMapModel'

function linkedRange(overrides: Partial<LinkedRange> = {}): LinkedRange {
  return {
    sourceFilePath: '/tmp/sharplabnext/build/src/Program.cs',
    sourceRange: { startLine: 2, startCharacter: 4, endLine: 2, endCharacter: 15 },
    outputRange: { startLine: 40, startCharacter: 0, endLine: 40, endCharacter: 1 },
    ...overrides,
  }
}

describe('IL source map model', () => {
  const files = [
    {
      path: 'src/Program.cs',
      text: 'using System;\nclass Program\n{\n    static int Value() => 42;\n}',
    },
  ]

  it('maps worker ranges to 1-based output lines and editor source coordinates', () => {
    const links = createIlSourceLinks(
      [
        linkedRange({
          sourceRange: { startLine: 3, startCharacter: 4, endLine: 3, endCharacter: 29 },
        }),
      ],
      files,
    )

    expect(links).toEqual([
      {
        startLine: 41,
        endLine: 41,
        heading: 'src/Program.cs:4:5',
        body: 'static int Value() => 42;',
        target: {
          documentPath: 'src/Program.cs',
          range: { startLine: 4, startColumn: 5, endLine: 4, endColumn: 30 },
        },
      },
    ])
  })

  it('matches sanitized Windows paths and preserves multiline source previews', () => {
    const links = createIlSourceLinks(
      [
        linkedRange({
          sourceFilePath: 'C:\\work\\build\\src\\Program.cs',
          sourceRange: { startLine: 1, startCharacter: 0, endLine: 3, endCharacter: 29 },
          outputRange: { startLine: 8, startCharacter: 0, endLine: 10, endCharacter: 0 },
        }),
      ],
      files,
    )

    expect(links[0]).toMatchObject({
      startLine: 9,
      endLine: 10,
      body: 'class Program\n{\n    static int Value() => 42;',
    })
  })

  it('treats a zero-character multiline source end as exclusive', () => {
    const link = createIlSourceLinks(
      [
        linkedRange({
          sourceRange: { startLine: 1, startCharacter: 0, endLine: 3, endCharacter: 0 },
        }),
      ],
      files,
    )[0]

    expect(link?.body).toBe('class Program\n{')
  })

  it('normalizes CRLF while preserving 0-based ranges across an empty source line', () => {
    const crlfFiles = [
      {
        path: 'Program.cs',
        text: 'using System;\r\n\r\nclass Program\r\n{\r\n    static int Value() => 42;\r\n}',
      },
    ]
    const links = createIlSourceLinks(
      [
        linkedRange({
          sourceFilePath: 'Program.cs',
          sourceRange: { startLine: 1, startCharacter: 0, endLine: 4, endCharacter: 29 },
          outputRange: { startLine: 5, startCharacter: 0, endLine: 8, endCharacter: 0 },
        }),
      ],
      crlfFiles,
    )

    expect(links[0]).toEqual({
      startLine: 6,
      endLine: 8,
      heading: 'Program.cs:2:1',
      body: 'class Program\n{\n    static int Value() => 42;',
      target: {
        documentPath: 'Program.cs',
        range: { startLine: 2, startColumn: 1, endLine: 5, endColumn: 30 },
      },
    })
  })

  it('does not guess ambiguous workspace paths', () => {
    const ambiguous = [
      { path: 'Program.cs', text: 'abc' },
      { path: 'src/Program.cs', text: 'abc' },
    ]
    expect(
      createIlSourceLinks(
        [
          linkedRange({
            sourceFilePath: '/tmp/build/src/Program.cs',
            sourceRange: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 1 },
          }),
        ],
        ambiguous,
      ),
    ).toEqual([])
  })

  it('rejects hidden, malformed, and out-of-bounds sequence points', () => {
    const invalid = [
      linkedRange({
        sourceRange: {
          startLine: 0xfeefee,
          startCharacter: 0,
          endLine: 0xfeefee,
          endCharacter: 0,
        },
      }),
      linkedRange({
        sourceRange: { startLine: 3, startCharacter: 20, endLine: 2, endCharacter: 1 },
      }),
      linkedRange({
        outputRange: { startLine: -1, startCharacter: 0, endLine: -1, endCharacter: 1 },
      }),
    ]

    expect(createIlSourceLinks(invalid, files)).toEqual([])
  })

  it('orders links by their IL line without changing same-line stability', () => {
    const sourceRange = { startLine: 3, startCharacter: 4, endLine: 3, endCharacter: 29 }
    const links = createIlSourceLinks(
      [
        linkedRange({
          sourceRange,
          outputRange: { startLine: 50, startCharacter: 0, endLine: 50, endCharacter: 1 },
        }),
        linkedRange({
          sourceRange,
          outputRange: { startLine: 12, startCharacter: 0, endLine: 12, endCharacter: 1 },
        }),
      ],
      files,
    )

    expect(links.map((link) => link.startLine)).toEqual([13, 51])
  })

  it('expands a sequence point over its following IL instructions only', () => {
    const sourceRange = { startLine: 3, startCharacter: 4, endLine: 3, endCharacter: 29 }
    const il = `.method private static int32 Value() cil managed
{
  // sequence point: source A

  IL_0000: ldc.i4.s 40
  IL_0002: ldc.i4.2
  IL_0003: add
  // sequence point: source B
  IL_0004: ret
} // end of method Program::Value`
    const links = createIlSourceLinks(
      [
        linkedRange({
          sourceRange,
          outputRange: { startLine: 3, startCharacter: 0, endLine: 3, endCharacter: 1 },
        }),
        linkedRange({
          sourceRange,
          outputRange: { startLine: 8, startCharacter: 0, endLine: 8, endCharacter: 1 },
        }),
      ],
      files,
      il,
    )

    expect(links.map(({ startLine, endLine }) => ({ startLine, endLine }))).toEqual([
      { startLine: 5, endLine: 7 },
      { startLine: 9, endLine: 9 },
    ])
  })
})
