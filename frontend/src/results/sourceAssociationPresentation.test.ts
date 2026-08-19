import { afterEach, describe, expect, it } from 'vitest'
import '../App.css'

afterEach(() => {
  document.body.replaceChildren()
})

describe('source association presentation', () => {
  it('keeps compact leading and trailing space around result line numbers', () => {
    const codeMirrorResult = document.createElement('div')
    codeMirrorResult.className = 'code-document-view'
    const lineNumbers = document.createElement('div')
    lineNumbers.className = 'cm-lineNumbers'
    const lineNumber = document.createElement('div')
    lineNumber.className = 'cm-gutterElement'
    lineNumbers.append(lineNumber)
    codeMirrorResult.append(lineNumbers)

    const monacoResult = document.createElement('div')
    monacoResult.className = 'code-document-view monaco-code-document'
    const margin = document.createElement('div')
    margin.className = 'margin-view-overlays'
    const monacoLineNumber = document.createElement('div')
    monacoLineNumber.className = 'line-numbers'
    margin.append(monacoLineNumber)
    monacoResult.append(margin)
    document.body.append(codeMirrorResult, monacoResult)

    const codeMirrorStyle = getComputedStyle(lineNumber)
    expect(codeMirrorStyle.paddingLeft).toBe('3px')
    expect(codeMirrorStyle.paddingRight).toBe('4px')
    expect(getComputedStyle(monacoLineNumber).transform).toBe('translateX(3px)')
  })

  it.each([
    ['Monaco', 'monaco-editor', 'monaco-source-association-line'],
    ['CodeMirror', 'codemirror-host', 'cm-line cm-source-association-line'],
  ])('puts the %s association background on one whole line', (_editor, hostClass, lineClass) => {
    const host = document.createElement('div')
    host.className = hostClass
    const line = document.createElement('div')
    line.className = `${lineClass} source-association source-association-0`
    for (const text of ['a', ' ', '+', ' ', 'b']) {
      const token = document.createElement('span')
      token.textContent = text
      line.append(token)
    }
    host.append(line)
    document.body.append(host)

    expect(line.textContent).toBe('a + b')
    expect(host.querySelector('.monaco-source-association')).not.toBeInTheDocument()
    expect(host.querySelector('.cm-source-association')).not.toBeInTheDocument()
    const style = getComputedStyle(line)
    expect(style.borderTopWidth).toBe('0px')
    expect(style.borderRadius).toBe('0px')
    expect(style.outlineStyle).toBe('none')
    expect(style.boxShadow).toBe('none')
  })

  it.each([
    [
      'Monaco',
      'monaco-editor',
      'monaco-source-association-line monaco-source-association-line-active',
    ],
    [
      'CodeMirror',
      'codemirror-host',
      'cm-line cm-source-association-line cm-source-association-line-active',
    ],
  ])('keeps the whole-line %s association while its exact source span is active', (_editor, hostClass, lineClass) => {
    const host = document.createElement('div')
    host.className = hostClass
    const line = document.createElement('div')
    line.className = `${lineClass} source-association source-association-1`
    line.textContent = 'a + b'
    const inactiveLine = document.createElement('div')
    inactiveLine.className = `${lineClass.replaceAll(
      /(?:monaco|cm)-source-association-line-active/g,
      '',
    )} source-association source-association-1`
    inactiveLine.textContent = 'a + b'
    host.append(inactiveLine, line)
    document.body.append(host)

    const style = getComputedStyle(line)
    expect(style.backgroundColor).not.toBe('rgba(0, 0, 0, 0)')
    expect(style.backgroundColor).toBe(getComputedStyle(inactiveLine).backgroundColor)
    expect(style.borderTopWidth).toBe('0px')
    expect(style.outlineWidth).toBe('0px')
    expect(style.boxShadow).toBe('none')
  })

  it.each([
    ['Monaco', 'monaco-editor', 'monaco-source-association-exact-active'],
    ['CodeMirror', 'codemirror-host', 'cm-source-association-exact-active'],
  ])('marks the exact active %s source span without a box', (_editor, hostClass, exactClass) => {
    const host = document.createElement('div')
    host.className = hostClass
    const line = document.createElement('div')
    line.className = 'source-association source-association-2'
    line.append('i', ' + ')
    const exact = document.createElement('span')
    exact.className = `${exactClass} source-association-2`
    exact.textContent = 'i++'
    line.append(exact, ';')
    host.append(line)
    document.body.append(host)

    const style = getComputedStyle(exact)
    expect(exact.textContent).toBe('i++')
    expect(style.backgroundColor).not.toBe('rgba(0, 0, 0, 0)')
    expect(style.borderTopWidth).toBe('0px')
    expect(style.borderRadius).toBe('0px')
    expect(style.outlineStyle).toBe('none')
    expect(style.boxShadow).toBe('none')
  })
})
