import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'
import {
  clampSourcePanePercent,
  defaultSourcePanePercent,
  maximumSourcePanePercent,
  minimumSourcePanePercent,
  paneSplitPreferenceStorageKey,
  readPaneSplitPreference,
  usePaneSplitPreference,
  writePaneSplitPreference,
} from './paneSplitPreference'

describe('pane split preference', () => {
  beforeEach(() => localStorage.clear())

  it('validates, bounds, and rounds source-pane percentages', () => {
    expect(clampSourcePanePercent(10)).toBe(minimumSourcePanePercent)
    expect(clampSourcePanePercent(87)).toBe(maximumSourcePanePercent)
    expect(clampSourcePanePercent(43.26)).toBe(43.3)
    expect(clampSourcePanePercent(Number.NaN)).toBe(defaultSourcePanePercent)

    localStorage.setItem(paneSplitPreferenceStorageKey, '43.26')
    expect(readPaneSplitPreference(localStorage)).toBe(43.3)
    for (const invalid of ['', 'NaN', '19.9', '80.1']) {
      localStorage.setItem(paneSplitPreferenceStorageKey, invalid)
      expect(readPaneSplitPreference(localStorage)).toBeNull()
    }
  })

  it('persists selection and restores the default split', () => {
    writePaneSplitPreference(localStorage, 61.27)
    const first = renderHook(() => usePaneSplitPreference())
    expect(first.result.current.sourcePercent).toBe(61.3)

    act(() => first.result.current.selectSourcePercent(72.54))
    expect(first.result.current.sourcePercent).toBe(72.5)
    expect(localStorage.getItem(paneSplitPreferenceStorageKey)).toBe('72.5')
    first.unmount()

    const restored = renderHook(() => usePaneSplitPreference())
    expect(restored.result.current.sourcePercent).toBe(72.5)
    act(() => restored.result.current.resetSourcePercent())
    expect(restored.result.current.sourcePercent).toBe(defaultSourcePanePercent)
    expect(localStorage.getItem(paneSplitPreferenceStorageKey)).toBe('50')
  })
})
