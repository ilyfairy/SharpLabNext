import { beforeEach, describe, expect, it } from 'vitest'
import { editorFontSizeStorageKey, editorPreferenceStorageKey } from '../editor/editorPreference'
import { paneSplitPreferenceStorageKey } from './paneSplitPreference'
import { resetWorkbenchStore } from './store'
import { getWorkbenchSnapshot } from './storeSnapshot'

describe('workbench snapshot', () => {
  beforeEach(() => {
    resetWorkbenchStore()
    localStorage.clear()
  })

  it('keeps the browser-local editor preference outside workspace serialization', () => {
    localStorage.setItem(editorPreferenceStorageKey, 'codemirror')
    localStorage.setItem(editorFontSizeStorageKey, '18')
    localStorage.setItem(paneSplitPreferenceStorageKey, '63.5')

    const snapshot = getWorkbenchSnapshot()

    expect(snapshot).not.toHaveProperty('editor')
    expect(snapshot).not.toHaveProperty('fontSize')
    expect(snapshot).not.toHaveProperty('sourcePanePercent')
    expect(Object.keys(snapshot).sort()).toEqual(
      [
        'activeFile',
        'buildMode',
        'fileName',
        'files',
        'selectionRevision',
        'source',
        'sourceOrder',
        'workspaceRevision',
      ].sort(),
    )
    expect(localStorage.getItem(editorPreferenceStorageKey)).toBe('codemirror')
    expect(localStorage.getItem(editorFontSizeStorageKey)).toBe('18')
    expect(localStorage.getItem(paneSplitPreferenceStorageKey)).toBe('63.5')
  })
})
