import { describe, expect, it } from 'vitest'
import type { AstDocument } from '../api/types'
import { createAstSourceMap } from './astSourceMapModel'

const document: AstDocument = {
  languageId: 'csharp',
  toolchainId: 'roslyn-main',
  workspaceRevision: 1,
  truncated: false,
  root: {
    kind: 'Workspace',
    range: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 0 },
    properties: {},
    children: [
      {
        kind: 'Document',
        range: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 8 },
        properties: { path: 'Program.cs' },
        children: [
          {
            kind: 'IdentifierName',
            range: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 7 },
            properties: { isNode: 'true' },
            children: [
              {
                kind: 'IdentifierToken',
                range: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 7 },
                properties: { isToken: 'true' },
                children: [],
              },
              {
                kind: 'WhitespaceTrivia',
                range: { startLine: 0, startCharacter: 7, endLine: 0, endCharacter: 8 },
                properties: { isTrivia: 'true' },
                children: [],
              },
            ],
          },
        ],
      },
    ],
  },
}

describe('AST source map', () => {
  it('uses interaction-only one-based ranges and prefers the deepest duplicate range', () => {
    const sourceMap = createAstSourceMap(document)

    expect(sourceMap.nodeCount).toBe(5)
    expect(sourceMap.entries.get('0')?.association).toBeNull()
    expect(sourceMap.entries.get('0.0')?.association).toBeNull()
    expect(sourceMap.entries.get('0.0.0')?.category).toBe('node')
    expect(sourceMap.entries.get('0.0.0.0')?.category).toBe('token')
    expect(sourceMap.entries.get('0.0.0.1')?.category).toBe('trivia')
    expect(sourceMap.associations).toHaveLength(2)
    expect(sourceMap.associations[0]).toEqual(
      expect.objectContaining({
        documentPath: 'Program.cs',
        presentation: 'active-range',
        range: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 8 },
      }),
    )
    const duplicateKey = sourceMap.entries.get('0.0.0')?.association?.key
    expect(sourceMap.preferredNodeIdByAssociationKey.get(duplicateKey ?? '')).toBe('0.0.0.0')
  })
})
