import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AstDocument } from '../api/types'
import { AstTreeView } from './AstTreeView'
import { createAstSourceMap } from './astSourceMapModel'

const document: AstDocument = {
  languageId: 'csharp',
  toolchainId: 'roslyn-main',
  workspaceRevision: 7,
  truncated: true,
  root: {
    kind: 'Workspace',
    range: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 0 },
    properties: { fileCount: '1' },
    children: [
      {
        kind: 'Document',
        range: { startLine: 0, startCharacter: 0, endLine: 2, endCharacter: 0 },
        properties: { path: 'Program.cs' },
        children: [
          {
            kind: 'CompilationUnit',
            range: { startLine: 0, startCharacter: 0, endLine: 1, endCharacter: 18 },
            fullRange: { startLine: 0, startCharacter: 0, endLine: 2, endCharacter: 0 },
            properties: { type: 'CompilationUnitSyntax', isNode: 'true' },
            children: [
              {
                kind: 'IdentifierName',
                range: { startLine: 1, startCharacter: 0, endLine: 1, endCharacter: 7 },
                properties: {
                  type: 'IdentifierNameSyntax',
                  isNode: 'true',
                  rawKind: '8616',
                },
                children: [
                  {
                    kind: 'IdentifierToken',
                    range: { startLine: 1, startCharacter: 0, endLine: 1, endCharacter: 7 },
                    properties: {
                      type: 'SyntaxToken',
                      isNode: 'false',
                      isToken: 'true',
                      valueText: 'Console',
                    },
                    children: [
                      {
                        kind: 'WhitespaceTrivia',
                        range: {
                          startLine: 1,
                          startCharacter: 7,
                          endLine: 1,
                          endCharacter: 8,
                        },
                        properties: {
                          type: 'SyntaxTrivia',
                          isNode: 'false',
                          isToken: 'false',
                          isTrivia: 'true',
                        },
                        children: [],
                      },
                    ],
                  },
                ],
              },
              {
                kind: 'ClassDeclaration',
                range: { startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 12 },
                properties: { type: 'ClassDeclarationSyntax', isNode: 'true' },
                children: [
                  {
                    kind: 'MethodDeclaration',
                    range: { startLine: 0, startCharacter: 6, endLine: 0, endCharacter: 12 },
                    properties: { type: 'MethodDeclarationSyntax', isNode: 'true' },
                    children: [],
                  },
                ],
              },
            ],
          },
        ],
      },
    ],
  },
}

afterEach(cleanup)

describe('AstTreeView', () => {
  it('starts without a source highlight and inspects a selected syntax node', () => {
    const onNavigate = vi.fn()
    render(<AstTreeView document={document} onNavigateToSource={onNavigate} />)

    expect(screen.getByRole('tree', { name: 'Abstract syntax tree' })).toBeVisible()
    expect(screen.queryByText('Syntax tree')).not.toBeInTheDocument()
    expect(screen.queryByText('8 nodes')).not.toBeInTheDocument()
    expect(screen.queryByText('Truncated')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Expand the AST' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Collapse the AST' })).not.toBeInTheDocument()
    expect(screen.getByText('Select a syntax item to inspect it.')).toBeVisible()
    expect(
      screen
        .getAllByRole('treeitem')
        .every((item) => item.getAttribute('aria-selected') === 'false'),
    ).toBe(true)
    expect(onNavigate).not.toHaveBeenCalled()

    expect(
      screen.getByRole('treeitem', { name: /Workspace/ }).closest('.ast-tree-row'),
    ).toHaveClass('ast-tree-root-row')

    fireEvent.click(screen.getByRole('treeitem', { name: /IdentifierName/ }))
    const inspector = screen.getByRole('complementary', { name: 'Selected AST node' })
    expect(inspector).toHaveTextContent('IdentifierNameSyntax')
    expect(inspector).toHaveTextContent('rawKind')
    expect(inspector).toHaveTextContent('Program.cs')
    expect(onNavigate).toHaveBeenCalledWith(
      expect.objectContaining({
        documentPath: 'Program.cs',
        range: { startLine: 2, startColumn: 1, endLine: 2, endColumn: 8 },
      }),
    )
  })

  it('toggles a branch on double-click and uses exactly node, token, and trivia categories', () => {
    render(<AstTreeView document={document} />)
    const identifier = screen.getByRole('treeitem', { name: /IdentifierName/ })
    expect(identifier).toHaveAttribute('aria-expanded', 'false')

    fireEvent.doubleClick(identifier)
    expect(identifier).toHaveAttribute('aria-expanded', 'true')
    const token = screen.getByRole('treeitem', { name: /IdentifierToken/ })
    expect(token.closest('.ast-tree-row')).toHaveAttribute('data-ast-category', 'token')

    fireEvent.doubleClick(token)
    const trivia = screen.getByRole('treeitem', { name: /WhitespaceTrivia/ })
    expect(trivia.closest('.ast-tree-row')).toHaveAttribute('data-ast-category', 'trivia')
    expect(identifier.closest('.ast-tree-row')).toHaveAttribute('data-ast-category', 'node')
  })

  it('selects and reveals the most precise AST item activated from source', async () => {
    const sourceMap = createAstSourceMap(document)
    const tokenEntry = sourceMap.entries.get('0.0.0.0.0')
    if (!tokenEntry?.association) throw new Error('Expected a token source association.')

    const view = render(<AstTreeView document={document} sourceMap={sourceMap} />)

    const unrelated = screen.getByRole('treeitem', { name: /ClassDeclaration/ })
    fireEvent.doubleClick(unrelated)
    expect(unrelated).toHaveAttribute('aria-expanded', 'true')

    view.rerender(
      <AstTreeView
        document={document}
        sourceMap={sourceMap}
        activeSourceAssociationKey={tokenEntry.association.key}
        activeSourceAssociationRevision={1}
      />,
    )

    await waitFor(() =>
      expect(screen.getByRole('treeitem', { name: /IdentifierToken/ })).toHaveAttribute(
        'aria-selected',
        'true',
      ),
    )
    expect(screen.getByRole('complementary', { name: 'Selected AST node' })).toHaveTextContent(
      'SyntaxToken',
    )
    expect(screen.getByRole('treeitem', { name: /IdentifierToken/ })).toHaveAttribute(
      'aria-expanded',
      'true',
    )
    expect(screen.getByRole('treeitem', { name: /WhitespaceTrivia/ })).toBeVisible()
    expect(screen.getByRole('treeitem', { name: /ClassDeclaration/ })).toHaveAttribute(
      'aria-expanded',
      'false',
    )
  })
})
