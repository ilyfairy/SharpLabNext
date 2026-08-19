import { describe, expect, it } from 'vitest'
import { outputFoldingRanges } from './outputFoldingModel'

describe('output folding model', () => {
  it('folds JIT methods and nested basic-block labels', () => {
    const text = [
      'Program:A():int:',
      '  mov eax, 1',
      '  ret',
      '',
      'Program:B(int,int):double:',
      'G_M000_IG01:',
      '  add edi, esi',
      'G_M000_IG02:',
      '  ret',
    ].join('\n')

    expect(outputFoldingRanges(text, 'asm')).toEqual([
      { startLine: 1, endLine: 3, kind: 'method' },
      { startLine: 5, endLine: 9, kind: 'method' },
      { startLine: 6, endLine: 7, kind: 'block' },
      { startLine: 8, endLine: 9, kind: 'block' },
    ])
  })

  it('folds IL assemblies, types, methods, and ordinary braces without reading quoted braces', () => {
    const text = [
      '.assembly Example',
      '{',
      '  .custom instance void Note::.ctor(string) = ( "not { a block" )',
      '}',
      '.class public auto ansi Program',
      '       extends [System.Runtime]System.Object',
      '{',
      '  .method public static void Main() cil managed',
      '  {',
      '    // ignored }',
      '    IL_0000: ret',
      '  }',
      '  .property int32 Value()',
      '  {',
      '    .get instance int32 Program::get_Value()',
      '  }',
      '}',
    ].join('\n')

    expect(outputFoldingRanges(text, 'il')).toEqual([
      { startLine: 1, endLine: 3, kind: 'assembly' },
      { startLine: 5, endLine: 16, kind: 'type' },
      { startLine: 8, endLine: 11, kind: 'method' },
      { startLine: 14, endLine: 15, kind: 'brace' },
    ])
  })

  it('does not invent structural folding for plain generated text', () => {
    expect(outputFoldingRanges('public static void Main() {}', 'csharp')).toEqual([])
  })
})
