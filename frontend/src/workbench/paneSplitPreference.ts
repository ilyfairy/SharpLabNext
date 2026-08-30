import { useCallback, useEffect, useState } from 'react'

export const paneSplitPreferenceStorageKey = 'sharplabnext.source-pane-percent';
export const defaultSourcePanePercent = 50;
export const minimumSourcePanePercent = 20;
export const maximumSourcePanePercent = 80;

export interface PaneSplitPreferenceState {
  sourcePercent: number
  selectSourcePercent: (percent: number) => void
  resetSourcePercent: () => void
}

export function clampSourcePanePercent(percent: number): number {
  if (!Number.isFinite(percent)) return defaultSourcePanePercent;
  const bounded = Math.min(maximumSourcePanePercent, Math.max(minimumSourcePanePercent, percent))
  return Math.round(bounded * 10) / 10;
}

export function readPaneSplitPreference(storage: Pick<Storage, 'getItem'>): number | null {
  try {
    const stored = storage.getItem(paneSplitPreferenceStorageKey)
    if (stored === null || stored.trim() === '') return null;
    const percent = Number(stored)
    if (!Number.isFinite(percent) || percent < minimumSourcePanePercent || percent > maximumSourcePanePercent) {
      return null;
    }
    return clampSourcePanePercent(percent);
  } catch {
    return null
  }
}

export function writePaneSplitPreference(storage: Pick<Storage, 'setItem'>, percent: number): void {
  try {
    storage.setItem(paneSplitPreferenceStorageKey, String(clampSourcePanePercent(percent)))
  } catch {
    // A private or quota-restricted browser can still use the in-memory split.
  }
}

export function usePaneSplitPreference(): PaneSplitPreferenceState {
  const [sourcePercent, setSourcePercent] = useState(() => (typeof localStorage === 'undefined' ? defaultSourcePanePercent : (readPaneSplitPreference(localStorage) ?? defaultSourcePanePercent)))

  useEffect(() => {
    if (typeof window === 'undefined' || typeof localStorage === 'undefined') return
    const synchronize = (event: StorageEvent) => {
      if (event.key !== paneSplitPreferenceStorageKey || event.storageArea !== localStorage) return
      setSourcePercent(readPaneSplitPreference(localStorage) ?? defaultSourcePanePercent)
    }
    window.addEventListener('storage', synchronize)
    return () => window.removeEventListener('storage', synchronize)
  }, [])

  const selectSourcePercent = useCallback((percent: number) => {
    const next = clampSourcePanePercent(percent)
    setSourcePercent(next)
    if (typeof localStorage !== 'undefined') writePaneSplitPreference(localStorage, next)
  }, [])

  const resetSourcePercent = useCallback(() => {
    setSourcePercent(defaultSourcePanePercent)
    if (typeof localStorage !== 'undefined') {
      writePaneSplitPreference(localStorage, defaultSourcePanePercent)
    }
  }, [])

  return { sourcePercent, selectSourcePercent, resetSourcePercent }
}
