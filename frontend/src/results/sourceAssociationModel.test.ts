import { describe, expect, it } from 'vitest'
import {
  createSourceAssociation,
  isLinkedLineSourceAssociation,
  sourceAssociationActivationKey,
  sourceAssociationForSelection,
  sourceAssociationLines,
} from './sourceAssociationModel'

describe('source association activation', () => {
  it('does not carry an active range into a later result with the same association key', () => {
    const activation = {
      associationKey: 'program.cs:8:28:8:33',
      generationId: 'workflow-1',
    }

    expect(sourceAssociationActivationKey(activation, 'workflow-1')).toBe(activation.associationKey)
    expect(sourceAssociationActivationKey(activation, 'workflow-2')).toBeNull()
  })

  it('assigns whole lines deterministically and prefers the active overlapping range', () => {
    const multiline = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 2, startColumn: 1, endLine: 4, endColumn: 1 },
      },
      'Multiline source',
    )
    const overlapping = createSourceAssociation(
      {
        documentPath: 'Program.cs',
        range: { startLine: 2, startColumn: 5, endLine: 2, endColumn: 10 },
      },
      'Overlapping source',
    )

    expect(multiline.presentation).toBe('linked-lines')
    expect(isLinkedLineSourceAssociation(multiline)).toBe(true)

    expect(sourceAssociationLines([overlapping, multiline], null)).toEqual([
      { lineNumber: 2, association: multiline, active: false },
      { lineNumber: 3, association: multiline, active: false },
    ])
    expect(sourceAssociationLines([multiline, overlapping], overlapping.key)).toEqual([
      { lineNumber: 2, association: multiline, active: true },
      { lineNumber: 3, association: multiline, active: false },
    ])
  })

  it('keeps AST interaction ranges out of the linked whole-line presentation', () => {
    const association = {
      ...createSourceAssociation(
        {
          documentPath: 'Program.cs',
          range: { startLine: 2, startColumn: 3, endLine: 2, endColumn: 8 },
        },
        'AST IdentifierName',
      ),
      presentation: 'active-range' as const,
    }

    expect(isLinkedLineSourceAssociation(association)).toBe(false)
    expect(sourceAssociationLines([association], null)).toEqual([])
    expect(sourceAssociationLines([association], association.key)).toEqual([])
  })

  it('maps a source selection to the exact or largest enclosed AST node', () => {
    const astAssociation = (
      startLine: number,
      startColumn: number,
      endLine: number,
      endColumn: number,
      label: string,
    ) => ({
      ...createSourceAssociation(
        {
          documentPath: 'Program.cs',
          range: { startLine, startColumn, endLine, endColumn },
        },
        label,
      ),
      presentation: 'active-range' as const,
    })
    const classNode = astAssociation(2, 1, 12, 2, 'AST ClassDeclaration')
    const methodNode = astAssociation(4, 3, 9, 4, 'AST MethodDeclaration')
    const identifierToken = astAssociation(4, 7, 4, 14, 'AST IdentifierToken')
    const associations = [identifierToken, methodNode, classNode]

    expect(
      sourceAssociationForSelection(associations, 'Program.cs', {
        startLine: 2,
        startColumn: 1,
        endLine: 12,
        endColumn: 2,
      }),
    ).toBe(classNode)
    expect(
      sourceAssociationForSelection(associations, 'Program.cs', {
        startLine: 1,
        startColumn: 1,
        endLine: 13,
        endColumn: 1,
      }),
    ).toBe(classNode)
    expect(
      sourceAssociationForSelection(associations, 'Program.cs', {
        startLine: 4,
        startColumn: 9,
        endLine: 4,
        endColumn: 11,
      }),
    ).toBe(identifierToken)
  })

  it('selects the smallest common AST container for sibling nodes', () => {
    const astAssociation = (
      startLine: number,
      startColumn: number,
      endLine: number,
      endColumn: number,
      label: string,
    ) => ({
      ...createSourceAssociation(
        {
          documentPath: 'Program.cs',
          range: { startLine, startColumn, endLine, endColumn },
        },
        label,
      ),
      presentation: 'active-range' as const,
    })
    const compilationUnit = astAssociation(1, 1, 7, 1, 'AST CompilationUnit')
    const firstStatement = astAssociation(3, 1, 3, 11, 'AST GlobalStatement')
    const secondStatement = astAssociation(4, 1, 4, 24, 'AST GlobalStatement')
    const thirdStatement = astAssociation(5, 1, 5, 27, 'AST GlobalStatement')
    const fourthStatement = astAssociation(6, 1, 6, 27, 'AST GlobalStatement')
    const associations = [
      fourthStatement,
      thirdStatement,
      secondStatement,
      compilationUnit,
      firstStatement,
    ]

    expect(
      sourceAssociationForSelection(associations, 'Program.cs', {
        startLine: 4,
        startColumn: 1,
        endLine: 6,
        endColumn: 27,
      }),
    ).toBe(compilationUnit)
  })

  it('ignores trailing trivia when a multi-line selection ends at a newline', () => {
    const astAssociation = (
      startLine: number,
      startColumn: number,
      endLine: number,
      endColumn: number,
      label: string,
      astCategory: 'node' | 'token' | 'trivia' = 'node',
    ) => ({
      ...createSourceAssociation(
        {
          documentPath: 'Program.cs',
          range: { startLine, startColumn, endLine, endColumn },
        },
        label,
      ),
      presentation: 'active-range' as const,
      astCategory,
    })
    const compilationUnit = astAssociation(1, 1, 7, 1, 'AST CompilationUnit')
    const firstStatement = astAssociation(3, 1, 3, 11, 'AST GlobalStatement')
    const secondStatement = astAssociation(4, 1, 4, 24, 'AST GlobalStatement')
    const thirdStatement = astAssociation(5, 1, 5, 27, 'AST GlobalStatement')
    const trailingTrivia = astAssociation(6, 27, 7, 1, 'AST EndOfLineTrivia', 'trivia')

    expect(
      sourceAssociationForSelection(
        [trailingTrivia, thirdStatement, secondStatement, firstStatement, compilationUnit],
        'Program.cs',
        {
          startLine: 3,
          startColumn: 1,
          endLine: 7,
          endColumn: 1,
        },
      ),
    ).toBe(compilationUnit)
  })
})
