import { useCallback, useEffect, useState } from 'react'

export type EditorKind = 'monaco' | 'codemirror'
export const editorFontSizeOptions = [12, 14, 16, 18, 20] as const
export type EditorFontSize = (typeof editorFontSizeOptions)[number]

export const mobileEditorMediaQuery = '(max-width: 860px)'
export const editorPreferenceStorageKey = 'sharplabnext.editor'
export const editorFontSizeStorageKey = 'sharplabnext.editor-font-size'
export const defaultEditorFontSize: EditorFontSize = 14

export interface EditorPreferenceState {
  editor: EditorKind
  fontSize: EditorFontSize
  isMobileViewport: boolean
  isManual: boolean
  selectEditor: (editor: EditorKind) => void
  selectFontSize: (fontSize: EditorFontSize) => void
  useViewportDefault: () => void
}

export function defaultEditorForViewport(isMobileViewport: boolean): EditorKind {
  return isMobileViewport ? 'codemirror' : 'monaco'
}

export function readEditorPreference(storage: Pick<Storage, 'getItem'>): EditorKind | null {
  try {
    const value = storage.getItem(editorPreferenceStorageKey)
    return value === 'monaco' || value === 'codemirror' ? value : null
  } catch {
    return null
  }
}

export function writeEditorPreference(
  storage: Pick<Storage, 'setItem' | 'removeItem'>,
  editor: EditorKind | null,
): void {
  try {
    if (editor) storage.setItem(editorPreferenceStorageKey, editor)
    else storage.removeItem(editorPreferenceStorageKey)
  } catch {
    // A private or quota-restricted browser can still use the in-memory choice.
  }
}

export function readEditorFontSize(storage: Pick<Storage, 'getItem'>): EditorFontSize | null {
  try {
    const value = Number(storage.getItem(editorFontSizeStorageKey))
    return editorFontSizeOptions.includes(value as EditorFontSize)
      ? (value as EditorFontSize)
      : null
  } catch {
    return null
  }
}

export function writeEditorFontSize(
  storage: Pick<Storage, 'setItem'>,
  fontSize: EditorFontSize,
): void {
  try {
    storage.setItem(editorFontSizeStorageKey, String(fontSize))
  } catch {
    // A private or quota-restricted browser can still use the in-memory choice.
  }
}

export function useEditorPreference(): EditorPreferenceState {
  const [manualEditor, setManualEditor] = useState<EditorKind | null>(() =>
    typeof localStorage === 'undefined' ? null : readEditorPreference(localStorage),
  )
  const [isMobileViewport, setIsMobileViewport] = useState(() => currentMobileViewport())
  const [fontSize, setFontSize] = useState<EditorFontSize>(() =>
    typeof localStorage === 'undefined'
      ? defaultEditorFontSize
      : (readEditorFontSize(localStorage) ?? defaultEditorFontSize),
  )

  useEffect(() => {
    if (typeof matchMedia !== 'function') return
    const media = matchMedia(mobileEditorMediaQuery)
    const update = () => setIsMobileViewport(media.matches)
    update()
    media.addEventListener('change', update)
    return () => media.removeEventListener('change', update)
  }, [])

  const selectEditor = useCallback((editor: EditorKind) => {
    setManualEditor(editor)
    if (typeof localStorage !== 'undefined') writeEditorPreference(localStorage, editor)
  }, [])

  const useViewportDefault = useCallback(() => {
    setManualEditor(null)
    if (typeof localStorage !== 'undefined') writeEditorPreference(localStorage, null)
  }, [])

  const selectFontSize = useCallback((nextFontSize: EditorFontSize) => {
    setFontSize(nextFontSize)
    if (typeof localStorage !== 'undefined') writeEditorFontSize(localStorage, nextFontSize)
  }, [])

  return {
    editor: manualEditor ?? defaultEditorForViewport(isMobileViewport),
    fontSize,
    isMobileViewport,
    isManual: manualEditor !== null,
    selectEditor,
    selectFontSize,
    useViewportDefault,
  }
}

function currentMobileViewport(): boolean {
  return typeof matchMedia === 'function' && matchMedia(mobileEditorMediaQuery).matches
}
