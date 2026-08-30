import { create } from 'zustand'
import type { BuildConfiguration, LanguageManifest, ResolvedSelection } from '../api/types'
import { fallbackLanguage, type MobilePane, type SelectionIntent } from './catalog'
import { clearLanguageWorkspaces, readLanguageWorkspace, removeLanguageWorkspace, type StoredLanguageWorkspace, writeLanguageWorkspace } from './languageWorkspaceStorage'

interface SelectionRevisionGuard {
  selectionRevision: number
  workspaceRevision: number
}

export interface WorkspaceFileState {
  path: string
  text: string
}

export interface WorkspaceReplacement {
  files: WorkspaceFileState[]
  activeFile: string
  sourceOrder: string[]
  selection?: SelectionIntent
  buildMode?: BuildConfiguration
  template?: {
    fileName: string
    source: string
  }
}

export type SourceOrderMove = 'earlier' | 'later'

interface WorkbenchState extends SelectionIntent {
  buildMode: BuildConfiguration
  mobilePane: MobilePane
  files: WorkspaceFileState[]
  activeFile: string
  sourceOrder: string[]
  source: string
  fileName: string
  templateFileName: string
  templateSource: string
  sourceIsTemplate: boolean
  workspaceRevision: number
  selectionRevision: number
  setSelectionIntent: (selection: SelectionIntent) => void
  selectLanguage: (language: LanguageManifest, selection: SelectionIntent) => void
  applyResolvedSelection: (selection: ResolvedSelection, guard: SelectionRevisionGuard) => boolean
  setBuildMode: (buildMode: BuildConfiguration) => void
  setMobilePane: (mobilePane: MobilePane) => void
  setSource: (source: string) => void
  setFileSource: (path: string, source: string) => void
  selectFile: (path: string) => void
  addFile: (path: string, text?: string) => boolean
  removeFile: (path: string) => boolean
  renameFile: (path: string, nextPath: string) => boolean
  moveFileInSourceOrder: (path: string, direction: SourceOrderMove) => boolean
  replaceWorkspace: (replacement: WorkspaceReplacement) => void
}

function selectionChanged(state: SelectionIntent, selection: SelectionIntent): boolean {
  return state.languageId !== selection.languageId || state.toolchainId !== selection.toolchainId || state.referenceSetId !== selection.referenceSetId || state.outputId !== selection.outputId || state.runtimeId !== selection.runtimeId
}

function browserStorage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

function workspaceForLanguage(language: LanguageManifest): StoredLanguageWorkspace {
  return readLanguageWorkspace(browserStorage(), language) ?? defaultWorkspaceForLanguage(language)
}

function defaultWorkspaceForLanguage(language: LanguageManifest): StoredLanguageWorkspace {
  return {
    files: [{ path: language.defaultFileName, text: language.defaultSource }],
    activeFile: language.defaultFileName,
    sourceOrder: [language.defaultFileName],
  }
}

function workspaceState(language: LanguageManifest, workspace = workspaceForLanguage(language)) {
  const activeFile = workspace.files.find((file) => file.path === workspace.activeFile) ?? workspace.files[0]
  const sourceIsTemplate = workspace.files.length === 1 && activeFile?.path === language.defaultFileName && activeFile.text === language.defaultSource
  return {
    files: workspace.files.map((file) => ({ ...file })),
    activeFile: activeFile?.path ?? language.defaultFileName,
    sourceOrder: [...workspace.sourceOrder],
    source: activeFile?.text ?? language.defaultSource,
    fileName: activeFile?.path ?? language.defaultFileName,
    templateFileName: language.defaultFileName,
    templateSource: language.defaultSource,
    sourceIsTemplate,
  }
}

function storedWorkspace(state: Pick<WorkbenchState, 'files' | 'activeFile' | 'sourceOrder'>) {
  return {
    files: state.files.map((file) => ({ ...file })),
    activeFile: state.activeFile,
    sourceOrder: [...state.sourceOrder],
  }
}

function persistLanguageWorkspace(state: Pick<WorkbenchState, 'languageId' | 'files' | 'activeFile' | 'sourceOrder' | 'sourceIsTemplate'>): void {
  if (state.sourceIsTemplate) {
    removeLanguageWorkspace(browserStorage(), state.languageId)
    return
  }
  writeLanguageWorkspace(browserStorage(), state.languageId, storedWorkspace(state))
}

export const useWorkbenchStore = create<WorkbenchState>((set, get) => {
  const initialWorkspace = workspaceState(fallbackLanguage)
  return {
    languageId: fallbackLanguage.id,
    toolchainId: fallbackLanguage.defaultToolchainId,
    referenceSetId: 'net11-preview-ref',
    outputId: 'decompiled-csharp',
    runtimeId: null,
    buildMode: 'release',
    mobilePane: 'code',
    ...initialWorkspace,
    workspaceRevision: 1,
    selectionRevision: 1,

    setSelectionIntent: (selection) =>
      set((state) => {
        if (!selectionChanged(state, selection)) return state
        return { ...selection, selectionRevision: state.selectionRevision + 1 }
      }),

    selectLanguage: (language, selection) =>
      set((state) => {
        const switchingLanguage = state.languageId !== language.id
        if (switchingLanguage) persistLanguageWorkspace(state)
        const refreshingTemplate = !switchingLanguage && state.sourceIsTemplate && state.templateSource !== language.defaultSource
        const nextWorkspace = switchingLanguage ? workspaceState(language) : refreshingTemplate ? workspaceState(language, defaultWorkspaceForLanguage(language)) : null
        const selectionDidChange = selectionChanged(state, selection)
        if (!nextWorkspace && !selectionDidChange) return state

        return {
          ...selection,
          ...(nextWorkspace ?? {}),
          workspaceRevision: nextWorkspace ? state.workspaceRevision + 1 : state.workspaceRevision,
          selectionRevision: selectionDidChange ? state.selectionRevision + 1 : state.selectionRevision,
        }
      }),

    applyResolvedSelection: (selection, guard) => {
      const state = get()
      if (state.selectionRevision !== guard.selectionRevision || state.workspaceRevision !== guard.workspaceRevision) {
        return false
      }

      const effective: SelectionIntent = {
        ...selection,
        runtimeId: selection.runtimeId ?? null,
      }
      if (!selectionChanged(state, effective)) return true
      set({ ...effective, selectionRevision: state.selectionRevision + 1 })
      return true
    },

    setBuildMode: (buildMode) => set((state) => (state.buildMode === buildMode ? state : { buildMode, selectionRevision: state.selectionRevision + 1 })),
    setMobilePane: (mobilePane) => set({ mobilePane }),
    setSource: (source) =>
      set((state) => {
        if (state.source === source) return state
        return {
          files: state.files.map((file) => (file.path === state.activeFile ? { ...file, text: source } : file)),
          source,
          sourceIsTemplate: state.files.length === 1 && source === state.templateSource,
          workspaceRevision: state.workspaceRevision + 1,
        }
      }),
    setFileSource: (path, source) =>
      set((state) => {
        const file = state.files.find((candidate) => candidate.path === path)
        if (!file || file.text === source) return state
        const active = path === state.activeFile
        return {
          files: state.files.map((candidate) => (candidate.path === path ? { ...candidate, text: source } : candidate)),
          ...(active
            ? {
                source,
                sourceIsTemplate: state.files.length === 1 && source === state.templateSource,
              }
            : {}),
          workspaceRevision: state.workspaceRevision + 1,
        }
      }),

    selectFile: (path) =>
      set((state) => {
        if (path === state.activeFile) return state
        const file = state.files.find((candidate) => candidate.path === path)
        if (!file) return state
        return {
          activeFile: path,
          fileName: path,
          source: file.text,
          sourceIsTemplate: false,
        }
      }),

    addFile: (path, text = '') => {
      const state = get()
      if (!path || state.files.some((file) => file.path === path)) return false
      set({
        files: [...state.files, { path, text }],
        sourceOrder: [...state.sourceOrder, path],
        activeFile: path,
        fileName: path,
        source: text,
        sourceIsTemplate: false,
        workspaceRevision: state.workspaceRevision + 1,
      })
      return true
    },

    removeFile: (path) => {
      const state = get()
      if (!state.files.some((file) => file.path === path)) return false
      if (state.files.length === 1) {
        const templateFile = {
          path: state.templateFileName,
          text: state.templateSource,
        }
        set({
          files: [templateFile],
          sourceOrder: [templateFile.path],
          activeFile: templateFile.path,
          fileName: templateFile.path,
          source: templateFile.text,
          sourceIsTemplate: true,
          workspaceRevision: state.workspaceRevision + 1,
        })
        return true
      }
      const files = state.files.filter((file) => file.path !== path)
      const firstFile = files[0]
      if (!firstFile) return false
      const sourceOrder = state.sourceOrder.filter((candidate) => candidate !== path)
      const nextActive = path === state.activeFile ? (sourceOrder[0] ?? firstFile.path) : state.activeFile
      const activeFile = files.find((file) => file.path === nextActive) ?? firstFile
      set({
        files,
        sourceOrder,
        activeFile: activeFile.path,
        fileName: activeFile.path,
        source: activeFile.text,
        sourceIsTemplate: files.length === 1 && activeFile.path === state.templateFileName && activeFile.text === state.templateSource,
        workspaceRevision: state.workspaceRevision + 1,
      })
      return true
    },

    renameFile: (path, nextPath) => {
      const state = get()
      if (!nextPath || !state.files.some((file) => file.path === path) || state.files.some((file) => file.path === nextPath)) {
        return false
      }
      const files = state.files.map((file) => (file.path === path ? { ...file, path: nextPath } : file))
      const activeFile = state.activeFile === path ? nextPath : state.activeFile
      set({
        files,
        sourceOrder: state.sourceOrder.map((candidate) => (candidate === path ? nextPath : candidate)),
        activeFile,
        fileName: activeFile,
        sourceIsTemplate: false,
        workspaceRevision: state.workspaceRevision + 1,
      })
      return true
    },

    moveFileInSourceOrder: (path, direction) => {
      const state = get()
      const offset = direction === 'earlier' ? -1 : direction === 'later' ? 1 : 0
      const filePaths = new Set(state.files.map((file) => file.path))
      const sourcePaths = new Set(state.sourceOrder)
      if (offset === 0 || filePaths.size !== state.files.length || sourcePaths.size !== state.sourceOrder.length || state.sourceOrder.length !== state.files.length || state.sourceOrder.some((candidate) => !filePaths.has(candidate))) {
        return false
      }

      const index = state.sourceOrder.indexOf(path)
      const targetIndex = index + offset
      if (index < 0 || !filePaths.has(path) || targetIndex < 0 || targetIndex >= state.sourceOrder.length) {
        return false
      }

      const sourceOrder = [...state.sourceOrder]
      const targetPath = sourceOrder[targetIndex]
      if (!targetPath) return false
      sourceOrder[targetIndex] = path
      sourceOrder[index] = targetPath
      set({ sourceOrder, workspaceRevision: state.workspaceRevision + 1 })
      return true
    },

    replaceWorkspace: (replacement) =>
      set((state) => {
        const activeFile = replacement.files.find((file) => file.path === replacement.activeFile) ?? replacement.files[0]
        if (!activeFile) return state
        const selection = replacement.selection ?? currentSelection(state)
        if (selection.languageId !== state.languageId) persistLanguageWorkspace(state)
        const selectionDidChange = selectionChanged(state, selection)
        const sourceIsTemplate = replacement.template !== undefined && replacement.files.length === 1 && activeFile.path === replacement.template.fileName && activeFile.text === replacement.template.source
        return {
          ...selection,
          buildMode: replacement.buildMode ?? state.buildMode,
          files: replacement.files.map((file) => ({ ...file })),
          activeFile: activeFile.path,
          sourceOrder: [...replacement.sourceOrder],
          source: activeFile.text,
          fileName: activeFile.path,
          templateFileName: replacement.template?.fileName ?? state.templateFileName,
          templateSource: replacement.template?.source ?? state.templateSource,
          sourceIsTemplate,
          workspaceRevision: state.workspaceRevision + 1,
          selectionRevision: selectionDidChange || replacement.buildMode !== undefined ? state.selectionRevision + 1 : state.selectionRevision,
        }
      }),
  }
})

useWorkbenchStore.subscribe((state, previous) => {
  if (state.languageId !== previous.languageId || state.files !== previous.files || state.activeFile !== previous.activeFile || state.sourceOrder !== previous.sourceOrder || state.sourceIsTemplate !== previous.sourceIsTemplate) {
    persistLanguageWorkspace(state)
  }
})

function currentSelection(state: SelectionIntent): SelectionIntent {
  return {
    languageId: state.languageId,
    toolchainId: state.toolchainId,
    referenceSetId: state.referenceSetId,
    outputId: state.outputId,
    runtimeId: state.runtimeId,
  }
}

export function resetWorkbenchStore(options: { preserveLanguageWorkspaces?: boolean } = {}): void {
  if (!options.preserveLanguageWorkspaces) clearLanguageWorkspaces(browserStorage())
  const initial = useWorkbenchStore.getInitialState()
  const workspace = workspaceState(fallbackLanguage, options.preserveLanguageWorkspaces ? workspaceForLanguage(fallbackLanguage) : undefined)
  useWorkbenchStore.setState({ ...initial, ...workspace }, true)
}
