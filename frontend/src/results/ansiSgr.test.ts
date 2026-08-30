import { describe, expect, it } from 'vitest'
import { parseAnsiSgrChunks, parseAnsiSgrOutputChunks } from './ansiSgr'

describe('parseAnsiSgrChunks', () => {
  it('applies reset, standard colors, bold, underline, and inverse video', () => {
    const document = parseAnsiSgrChunks(['\u001b[31;44;1;4mstyled', '\u001b[7minverse', '\u001b[27;22;24;39;49mplain', '\u001b[90;103mbright', '\u001b[0mreset'])

    expect(document.text).toBe('styledinverseplainbrightreset')
    expect(document.copyText).toBe('styledinverseplainbrightreset')
    expect(document.segments).toEqual([
      {
        text: 'styled',
        style: {
          foreground: '#c50f1f',
          background: '#0037da',
          bold: true,
          underline: true,
          inverse: false,
        },
      },
      {
        text: 'inverse',
        style: {
          foreground: '#c50f1f',
          background: '#0037da',
          bold: true,
          underline: true,
          inverse: true,
        },
      },
      {
        text: 'plain',
        style: {
          foreground: null,
          background: null,
          bold: false,
          underline: false,
          inverse: false,
        },
      },
      {
        text: 'bright',
        style: {
          foreground: '#767676',
          background: '#f9f1a5',
          bold: false,
          underline: false,
          inverse: false,
        },
      },
      {
        text: 'reset',
        style: {
          foreground: null,
          background: null,
          bold: false,
          underline: false,
          inverse: false,
        },
      },
    ])
  })

  it('supports indexed and true colors across WebSocket chunk boundaries', () => {
    const document = parseAnsiSgrChunks(['\u001b[38;5;', '196;48;5;226mindexed', '\u001b[38;2;1;', '2;3;48:2::4:5:6mtrue', '\u001b[0', 'mplain'])

    expect(document.text).toBe('indexedtrueplain')
    expect(document.copyText).toBe('indexedtrueplain')
    expect(document.segments[0]).toEqual({
      text: 'indexed',
      style: {
        foreground: '#ff0000',
        background: '#ffff00',
        bold: false,
        underline: false,
        inverse: false,
      },
    })
    expect(document.segments[1]).toEqual({
      text: 'true',
      style: {
        foreground: '#010203',
        background: '#040506',
        bold: false,
        underline: false,
        inverse: false,
      },
    })
    expect(document.segments[2]).toEqual({
      text: 'plain',
      style: {
        foreground: null,
        background: null,
        bold: false,
        underline: false,
        inverse: false,
      },
    })
  })

  it('keeps malformed, incomplete, and non-SGR control sequences as inert text', () => {
    const unsupported = '\u001b[2J\u001b]8;;javascript:alert(1)\u0007click\u001b]8;;\u0007'
    const malformed = '\u001b[31;?m'
    const incomplete = '\u001b[38;5'
    const document = parseAnsiSgrChunks([`before\u0000${unsupported}${malformed}`, `after${incomplete}`])

    expect(document.text).toBe(`before\u2400\u241b[2J\u241b]8;;javascript:alert(1)\u2407click\u241b]8;;\u2407\u241b[31;?mafter\u241b[38;5`)
    expect(document.copyText).toBe('beforeclickafter')
    expect(document.segments).toHaveLength(1)
    expect(document.segments[0]?.style).toEqual({
      foreground: null,
      background: null,
      bold: false,
      underline: false,
      inverse: false,
    })
  })

  it('renders a long unterminated control sequence visibly without copying it', () => {
    const unterminated = `\u001b[${'1;'.repeat(80)}`
    const document = parseAnsiSgrChunks([unterminated])

    expect(document.text).toBe(`\u241b[${'1;'.repeat(80)}`)
    expect(document.copyText).toBe('')
  })
})

describe('parseAnsiSgrOutputChunks', () => {
  it('keeps stdout and stderr ANSI state independent while preserving event order', () => {
    const document = parseAnsiSgrOutputChunks([
      { channel: 'stdout', text: '\u001b[31mout' },
      { channel: 'stderr', text: 'err' },
      { channel: 'stdout', text: 'put\u001b[0m' },
      { channel: 'stderr', text: '\u001b[1mwarn\u001b[0m' },
    ])

    expect(document.text).toBe('outerrputwarn')
    expect(document.copyText).toBe('outerrputwarn')
    expect(document.segments).toEqual([
      expect.objectContaining({
        text: 'out',
        style: expect.objectContaining({ foreground: '#c50f1f' }),
      }),
      expect.objectContaining({
        text: 'err',
        style: expect.objectContaining({ foreground: '#c50f1f' }),
      }),
      expect.objectContaining({
        text: 'put',
        style: expect.objectContaining({ foreground: '#c50f1f' }),
      }),
      expect.objectContaining({
        text: 'warn',
        style: expect.objectContaining({ foreground: '#c50f1f', bold: true }),
      }),
    ])
  })
})
