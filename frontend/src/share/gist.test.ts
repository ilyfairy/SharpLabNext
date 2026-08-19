import { describe, expect, it } from 'vitest'
import { gistFragment, parseGistFragment } from './gist'

describe('Gist URL fragments', () => {
  it('parses plain and legacy override fragments', () => {
    expect(parseGistFragment('#gist:abcdef1234')).toEqual({
      id: 'abcdef1234',
      options: { target: null, branch: null, mode: null },
    })
    expect(parseGistFragment('#gist:abcdef1234/asm/roslyn-main/debug')).toEqual({
      id: 'abcdef1234',
      options: { target: 'asm', branch: 'roslyn-main', mode: 'debug' },
    })
    expect(parseGistFragment('#gist:abcdef1234/_/_')).toEqual({
      id: 'abcdef1234',
      options: { target: null, branch: null, mode: 'release' },
    })
  })

  it('creates only canonical no-override fragments', () => {
    expect(gistFragment('abcdef1234')).toBe('#gist:abcdef1234')
    expect(() => gistFragment('../secret')).toThrow()
  })
})
