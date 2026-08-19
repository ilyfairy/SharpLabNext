import { ensureSyntaxTree } from '@codemirror/language'
import { EditorState } from '@codemirror/state'
import { EditorView } from '@codemirror/view'
import { describe, expect, it } from 'vitest'
import {
  codeMirrorLanguageExtension,
  semanticTokenCssClass,
  visualStudioLightEditorTheme,
} from './codeMirrorLanguage'

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

describe('CodeMirror semantic token palette', () => {
  it.each([
    ['class', 'type'],
    ['interface', 'type'],
    ['typeParameter', 'type'],
    ['method', 'method'],
    ['function', 'method'],
    ['extensionMethod', 'method'],
    ['property', 'property'],
    ['field', 'field'],
    ['enumMember', 'enum-member'],
    ['parameter', 'parameter'],
    ['local', 'variable'],
    ['macro', 'macro'],
    ['modifier', 'keyword'],
    ['stringEscapeCharacter', 'escape'],
    ['regex', 'regexp'],
  ])('maps %s to %s', (tokenType, expected) => {
    expect(semanticTokenCssClass(tokenType)).toBe(expected)
  })

  it('keeps focused and inactive light-theme selections readable without replacing token colors', () => {
    const parent = document.createElement('div')
    document.body.append(parent)
    const view = new EditorView({
      parent,
      state: EditorState.create({
        doc: 'class SelectedType {}',
        extensions: [visualStudioLightEditorTheme],
      }),
    })

    const themeCss = Array.from(document.querySelectorAll('style'))
      .map((style) => style.textContent ?? '')
      .join('\n')
    expect(themeCss).toContain('background-color: #add6ff')
    expect(themeCss).toContain('background-color: #d8e2ec')
    expect(themeCss).toContain('background-color: transparent')
    expect(themeCss).toContain('background-color: rgba(237, 242, 247, 0.58)')
    expect(themeCss).toContain('color: inherit')

    view.destroy()
    parent.remove()
  })

  it('classifies CoreCLR assembly method titles, labels, opcodes, registers, and comments', () => {
    const state = EditorState.create({
      doc: [
        '; Assembly listing for method Demo.Program:Run()',
        'G_M000_IG02:',
        '       mov      eax, dword ptr [rcx+08H]',
        '       vxorps   xmm12, xmm12, xmm12',
        '       vcvtsi2sd ymm31, ymm31, r15d',
        '       vmovdqu64 zmmword ptr [rax], zmm31',
        '       call     Demo.Helper:Write()',
        '; Total bytes of code 12',
      ].join('\n'),
      extensions: [codeMirrorLanguageExtension('asm')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const names: string[] = []
    tree?.iterate({
      enter: (node) => {
        names.push(node.name)
      },
    })
    expect(names).toEqual(
      expect.arrayContaining([
        'comment',
        'functionName',
        'labelName',
        'keyword',
        'variableName',
        'number',
      ]),
    )
    expect(names.filter((name) => name === 'keyword').length).toBeGreaterThanOrEqual(5)
    expect(names.filter((name) => name === 'variableName').length).toBeGreaterThanOrEqual(8)
  })

  it('classifies the full compound IL opcode surface without splitting suffixes', () => {
    const state = EditorState.create({
      doc: [
        'IL_0000: beq.s IL_0001',
        'conv.ovf.i4.un',
        'ldelem.ref',
        'tail. call void Demo.Program::Run()',
      ].join('\n'),
      extensions: [codeMirrorLanguageExtension('il')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const tokens: Array<{ name: string; text: string }> = []
    tree?.iterate({
      enter: (node) => {
        if (node.from === node.to) return
        tokens.push({ name: node.name, text: state.doc.sliceString(node.from, node.to) })
      },
    })

    expect(tokens).toEqual(
      expect.arrayContaining([
        { name: 'labelName', text: 'IL_0000' },
        { name: 'keyword', text: 'beq.s' },
        { name: 'keyword', text: 'conv.ovf.i4.un' },
        { name: 'keyword', text: 'ldelem.ref' },
        { name: 'keyword', text: 'tail.' },
        { name: 'keyword', text: 'call' },
      ]),
    )
  })

  it('uses IL keyword, assembly, and type colors for disassembler output', () => {
    const state = EditorState.create({
      doc: [
        '.assembly Demo.Playground',
        '.assembly extern Demo.Dependency',
        '    .permissionset reqmin = ( 01 00 )',
        '    .hash algorithm 0x00008004',
        '{',
        '}',
        '.class public auto ansi Program extends [System.Runtime]System.Object',
        '{',
        '    .custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor() = ( 01 00 00 00 )',
        '    .method public hidebysig static void Main([out] int32 value) cil managed',
        '    {',
        '        .maxstack 8',
        '        .entrypoint',
        '        .locals init (int32[0...] Values)',
        '    }',
        '}',
      ].join('\n'),
      extensions: [codeMirrorLanguageExtension('il')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const tokens: Array<{ name: string; text: string }> = []
    tree?.iterate({
      enter: (node) => {
        if (node.from === node.to) return
        tokens.push({ name: node.name, text: state.doc.sliceString(node.from, node.to) })
      },
    })
    const namesFor = (text: string) =>
      tokens.filter((token) => token.text === text).map((token) => token.name)

    for (const directive of [
      '.assembly',
      '.class',
      '.custom',
      '.method',
      '.maxstack',
      '.entrypoint',
    ]) {
      expect(namesFor(directive), directive).toContain('keyword')
    }
    expect(namesFor('Demo.Playground')).toContain('macroName')
    expect(namesFor('extern')).toContain('keyword')
    expect(namesFor('Demo.Dependency')).toContain('macroName')
    expect(namesFor('reqmin')).toContain('keyword')
    expect(namesFor('algorithm')).toContain('keyword')
    expect(namesFor('System.Runtime')).toEqual(['macroName', 'macroName'])
    expect(namesFor('out')).toContain('keyword')
    expect(namesFor('out')).not.toContain('macroName')
    expect(namesFor('System.Object')).toContain('typeName')
    expect(namesFor('System.ObsoleteAttribute')).toContain('typeName')
    expect(namesFor('Program')).toContain('typeName')
    expect(namesFor('.ctor')).toContain('keyword')
  })

  it('classifies decompiled C# attributes, targets, types, members, parameters, and strings', () => {
    const state = EditorState.create({
      doc: [
        'using System;',
        '[assembly: System.Reflection.AssemblyTitle("Demo", Name = "Value")]',
        'namespace Demo;',
        'public sealed class Widget',
        '{',
        '    public Widget(string name) { Name = name; }',
        '    public string Name { get; }',
        '    public int Count { get; }',
        '    public double Scale(double value) => value;',
        '    public void Write(string value) { Console.WriteLine(value); }',
        '}',
      ].join('\n'),
      extensions: [codeMirrorLanguageExtension('csharp')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const tokens: Array<{ name: string; text: string }> = []
    tree?.iterate({
      enter: (node) => {
        if (node.from === node.to) return
        tokens.push({ name: node.name, text: state.doc.sliceString(node.from, node.to) })
      },
    })
    const namesFor = (text: string) =>
      tokens.filter((token) => token.text === text).map((token) => token.name)

    expect(namesFor('assembly')).toContain('keyword')
    expect(namesFor('System')).toContain('namespace')
    expect(namesFor('Reflection')).toContain('namespace')
    expect(namesFor('AssemblyTitle')).toContain('typeName')
    expect(namesFor('Widget')).toContain('typeName')
    for (const keyword of ['int', 'double', 'string', 'void']) {
      expect(namesFor(keyword), keyword).not.toHaveLength(0)
      expect(namesFor(keyword), keyword).toEqual(expect.not.arrayContaining(['typeName']))
      expect(namesFor(keyword), keyword).toEqual(expect.arrayContaining(['keyword']))
    }
    expect(namesFor('Write')).toContain('variableName.function')
    expect(namesFor('WriteLine')).toContain('variableName.function')
    expect(namesFor('Name')).toContain('variableName')
    expect(namesFor('value')).toContain('variableName')
    expect(namesFor('"Demo"')).toContain('string')
  })

  it('uses the official CodeMirror PHP parser for declarations and variables', () => {
    const state = EditorState.create({
      doc: '<?php\nfunction square(int $value): int { return $value * $value; }',
      extensions: [codeMirrorLanguageExtension('php')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const names: string[] = []
    tree?.iterate({
      enter: (node) => {
        names.push(node.name)
      },
    })

    expect(names).toEqual(
      expect.arrayContaining([
        'PhpOpen',
        'FunctionDefinition',
        'Parameter',
        'NamedType',
        'VariableName',
        'ReturnStatement',
      ]),
    )
  })

  it('uses the Java mode for J# keywords, types, methods, and the default sample', () => {
    const state = EditorState.create({
      doc: [
        'public class Program {',
        '    public static void main(String[] args) {',
        '        System.Console.WriteLine("Hello from J#");',
        '    }',
        '}',
      ].join('\n'),
      extensions: [codeMirrorLanguageExtension('jsharp')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const tokens: Array<{ name: string; text: string }> = []
    tree?.iterate({
      enter: (node) => {
        if (node.from === node.to) return
        tokens.push({ name: node.name, text: state.doc.sliceString(node.from, node.to) })
      },
    })

    expect(tokens).toEqual(
      expect.arrayContaining([
        { name: 'keyword', text: 'public' },
        { name: 'keyword', text: 'class' },
        { name: 'typeName', text: 'Program' },
        { name: 'variableName.function', text: 'main' },
        { name: 'variableName.function', text: 'WriteLine' },
        { name: 'string', text: '"Hello from J#"' },
      ]),
    )
  })

  it('uses the JavaScript mode for JSIL result documents', () => {
    const state = EditorState.create({
      doc: [
        "'use strict';",
        'var assembly = JSIL.DeclareAssembly("Demo");',
        'function Program_Main(value) { return value + 1; }',
      ].join('\n'),
      extensions: [codeMirrorLanguageExtension('javascript')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const tokens: Array<{ name: string; text: string }> = []
    tree?.iterate({
      enter: (node) => {
        if (node.from === node.to) return
        tokens.push({ name: node.name, text: state.doc.sliceString(node.from, node.to) })
      },
    })

    expect(tokens).toEqual(
      expect.arrayContaining([
        { name: 'string', text: "'use strict'" },
        { name: 'keyword', text: 'var' },
        { name: 'propertyName', text: 'DeclareAssembly' },
        { name: 'keyword', text: 'function' },
        { name: 'keyword', text: 'return' },
        { name: 'number', text: '1' },
      ]),
    )
  })

  it('uses the C++ mode for C++/CLI keywords, types, and managed handles', () => {
    const state = EditorState.create({
      doc: [
        'using namespace System;',
        'int main(array<String^>^ args) {',
        '    Object^ value = gcnew Object();',
        '    Console::WriteLine(value);',
        '}',
      ].join('\n'),
      extensions: [codeMirrorLanguageExtension('cppcli')],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const tokens: Array<{ name: string; text: string }> = []
    tree?.iterate({
      enter: (node) => {
        if (node.from === node.to) return
        tokens.push({ name: node.name, text: state.doc.sliceString(node.from, node.to) })
      },
    })

    expect(tokens).toEqual(
      expect.arrayContaining([
        { name: 'keyword', text: 'using' },
        { name: 'keyword', text: 'namespace' },
        { name: 'keyword', text: 'gcnew' },
        { name: 'typeName', text: 'int' },
      ]),
    )
  })

  it.each([
    'minilang',
    'gsharp',
    'il',
  ])('classifies string escapes separately in %s source and result documents', (languageId) => {
    const state = EditorState.create({
      doc: 'print "first\\nsecond \\u0041"',
      extensions: [codeMirrorLanguageExtension(languageId)],
    })
    const tree = ensureSyntaxTree(state, state.doc.length, 100)
    const tokens: Array<{ name: string; text: string }> = []
    tree?.iterate({
      enter: (node) => {
        if (node.from === node.to) return
        tokens.push({ name: node.name, text: state.doc.sliceString(node.from, node.to) })
      },
    })

    expect(tokens).toContainEqual({ name: 'escape', text: '\\n' })
    expect(tokens).toContainEqual({ name: 'escape', text: '\\u0041' })
  })
})
