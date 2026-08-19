import type { BuildConfiguration } from '../api/types'
import { useWorkbenchStore } from './store'

export interface WorkbenchSnapshot {
  source: string
  fileName: string
  files: { path: string; text: string }[]
  activeFile: string
  sourceOrder: string[]
  buildMode: BuildConfiguration
  workspaceRevision: number
  selectionRevision: number
}

export function getWorkbenchSnapshot(): WorkbenchSnapshot {
  const state = useWorkbenchStore.getState()
  return {
    source: state.source,
    fileName: state.fileName,
    files: state.files.map((file) => ({ ...file })),
    activeFile: state.activeFile,
    sourceOrder: [...state.sourceOrder],
    buildMode: state.buildMode,
    workspaceRevision: state.workspaceRevision,
    selectionRevision: state.selectionRevision,
  }
}
