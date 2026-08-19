import type { CatalogDocument, GistDocument, GistWorkspaceState } from '../api/types'
import type { SelectionIntent } from './catalog'
import { normalizeSelectionIntent } from './catalog'
import type { WorkspaceReplacement } from './store'

interface GistSourceState extends SelectionIntent {
  buildMode: 'debug' | 'release'
  files: { path: string; text: string }[]
  activeFile: string
  sourceOrder: string[]
}

export interface DecodedWorkbenchGist {
  replacement: WorkspaceReplacement
  warnings: string[]
}

export function createGistWorkspaceState(
  catalog: CatalogDocument,
  state: GistSourceState,
): GistWorkspaceState {
  const toolchainId = state.toolchainId ?? catalog.languages[0]?.defaultToolchainId
  const referenceSetId =
    state.referenceSetId ??
    catalog.toolchains.find((toolchain) => toolchain.id === toolchainId)?.defaultReferenceSetId
  if (!toolchainId || !referenceSetId) {
    throw new Error('The current selection is incomplete and cannot be saved to a Gist.')
  }
  return {
    schemaVersion: 1,
    languageId: state.languageId,
    toolchainId,
    referenceSetId,
    outputId: state.outputId,
    runtimeId: state.runtimeId,
    buildMode: state.buildMode,
    releaseId: catalog.releaseId,
    activeFile: state.activeFile,
    sourceOrder: [...state.sourceOrder],
    files: state.files.map((file) => ({ ...file })),
  }
}

export function decodeWorkbenchGist(
  document: GistDocument,
  catalog: CatalogDocument,
): DecodedWorkbenchGist {
  const state = document.workspace
  const requestedToolchain = state.toolchainId ?? state.legacyBranchId ?? null
  const requested: SelectionIntent = {
    languageId: state.languageId,
    toolchainId: requestedToolchain,
    referenceSetId: state.referenceSetId ?? null,
    outputId: state.outputId,
    runtimeId: state.runtimeId ?? null,
  }
  const selection = normalizeSelectionIntent(catalog, requested)
  const language = catalog.languages.find((candidate) => candidate.id === selection.languageId)
  const template = language
    ? { fileName: language.defaultFileName, source: language.defaultSource }
    : null
  return {
    replacement: {
      files: state.files.map((file) => ({ ...file })),
      activeFile: state.activeFile,
      sourceOrder: [...state.sourceOrder],
      selection,
      buildMode: state.buildMode,
      ...(template ? { template } : {}),
    },
    warnings: [...document.warnings],
  }
}
