import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  defaultEditorFontSize,
  defaultEditorForViewport,
  editorFontSizeOptions,
  editorFontSizeStorageKey,
  editorPreferenceStorageKey,
  readEditorFontSize,
  readEditorPreference,
  useEditorPreference,
  writeEditorFontSize,
  writeEditorPreference,
} from './editorPreference'

describe('editor preference', () => {
  let mobile = false
  const listeners = new Set<() => void>()

  beforeEach(() => {
    localStorage.clear()
    mobile = false
    listeners.clear()
    vi.stubGlobal('matchMedia', () => ({
      get matches() {
        return mobile
      },
      media: '(max-width: 720px)',
      onchange: null,
      addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
      removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }))
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    window.history.replaceState(null, '', '/')
  })

  it('defaults to CodeMirror on mobile and Monaco on desktop', () => {
    expect(defaultEditorForViewport(true)).toBe('codemirror')
    expect(defaultEditorForViewport(false)).toBe('monaco')
  })

  it.each([721, 800, 860])('defaults to CodeMirror across the compact workbench at %i CSS px', (viewportWidth) => {
    vi.stubGlobal('matchMedia', (query: string) => {
      const maxWidth = Number(/max-width:\s*(\d+)px/.exec(query)?.[1])
      return {
        matches: viewportWidth <= maxWidth,
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      }
    })

    const { result } = renderHook(() => useEditorPreference())

    expect(result.current.editor).toBe('codemirror')
    expect(result.current.isMobileViewport).toBe(true)
  })

  it('persists only valid manual choices', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    }
    writeEditorPreference(storage, 'codemirror')
    expect(readEditorPreference(storage)).toBe('codemirror')
    values.set(editorPreferenceStorageKey, 'unknown')
    expect(readEditorPreference(storage)).toBeNull()
    writeEditorPreference(storage, null)
    expect(values.has(editorPreferenceStorageKey)).toBe(false)
  })

  it('persists only supported editor font sizes', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
    }

    writeEditorFontSize(storage, 18)
    expect(readEditorFontSize(storage)).toBe(18)
    values.set(editorFontSizeStorageKey, '15')
    expect(readEditorFontSize(storage)).toBeNull()
    values.set(editorFontSizeStorageKey, 'not-a-number')
    expect(readEditorFontSize(storage)).toBeNull()
    expect(editorFontSizeOptions).toEqual([12, 14, 16, 18, 20])
  })

  it('defaults and restores the shared browser-local font size', () => {
    const first = renderHook(() => useEditorPreference())
    expect(first.result.current.fontSize).toBe(defaultEditorFontSize)

    act(() => first.result.current.selectFontSize(18))
    expect(first.result.current.fontSize).toBe(18)
    expect(localStorage.getItem(editorFontSizeStorageKey)).toBe('18')
    first.unmount()

    const restored = renderHook(() => useEditorPreference())
    expect(restored.result.current.fontSize).toBe(18)
  })

  it('keeps a manual choice across viewport changes and can return to automatic mode', () => {
    mobile = true
    const { result } = renderHook(() => useEditorPreference())
    expect(result.current.editor).toBe('codemirror')
    expect(result.current.isManual).toBe(false)

    act(() => result.current.selectEditor('monaco'))
    expect(result.current.editor).toBe('monaco')
    expect(localStorage.getItem(editorPreferenceStorageKey)).toBe('monaco')

    act(() => {
      mobile = false
      for (const listener of listeners) listener()
    })
    expect(result.current.editor).toBe('monaco')
    expect(result.current.isMobileViewport).toBe(false)

    act(() => result.current.useViewportDefault())
    expect(result.current.editor).toBe('monaco')
    expect(result.current.isManual).toBe(false)
    expect(localStorage.getItem(editorPreferenceStorageKey)).toBeNull()
  })

  it('restores the browser-local choice without changing any part of the share URL', () => {
    window.history.replaceState(null, '', '/?workspace=shared#v3:workspace-state')
    const shareUrl = window.location.href
    const first = renderHook(() => useEditorPreference())

    act(() => first.result.current.selectEditor('codemirror'))
    act(() => first.result.current.selectFontSize(20))

    expect(window.location.href).toBe(shareUrl)
    expect(localStorage.getItem(editorPreferenceStorageKey)).toBe('codemirror')
    expect(localStorage.getItem(editorFontSizeStorageKey)).toBe('20')
    first.unmount()

    const restored = renderHook(() => useEditorPreference())
    expect(restored.result.current.editor).toBe('codemirror')
    expect(restored.result.current.isManual).toBe(true)
    expect(restored.result.current.fontSize).toBe(20)
    expect(window.location.href).toBe(shareUrl)
  })
})
