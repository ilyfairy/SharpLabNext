import type { CatalogDocument } from '../api/types'
import type { DecodedShare, ShareWorkspaceState } from '../share'
import type { SelectionIntent } from './catalog'
import { normalizeSelectionIntent } from './catalog'
import type { WorkspaceReplacement } from './store'

interface ShareSourceState extends SelectionIntent {
  buildMode: 'debug' | 'release'
  files: { path: string; text: string }[]
  activeFile: string
  sourceOrder: string[]
}

export interface DecodedWorkbenchShare {
  replacement: WorkspaceReplacement
  warnings: string[]
}

export function createShareWorkspaceState(
  catalog: CatalogDocument,
  state: ShareSourceState,
): ShareWorkspaceState {
  const toolchainId = state.toolchainId ?? catalog.languages[0]?.defaultToolchainId
  const referenceSetId =
    state.referenceSetId ??
    catalog.toolchains.find((toolchain) => toolchain.id === toolchainId)?.defaultReferenceSetId
  const runtimeId =
    state.runtimeId ??
    catalog.runtimes.find((runtime) => runtime.visibility !== 'hidden')?.id ??
    'not-required'
  if (!toolchainId || !referenceSetId) {
    throw new Error('The current selection is incomplete and cannot be shared.')
  }

  return {
    languageId: state.languageId,
    toolchainId,
    referenceSetId,
    outputId: state.outputId,
    runtimeId,
    buildMode: state.buildMode,
    releaseVersion: catalog.releaseId,
    activeFile: state.activeFile,
    sourceOrder: [...state.sourceOrder],
    files: state.files.map((file) => ({ ...file })),
  }
}

export function decodeWorkbenchShare(
  decoded: DecodedShare,
  catalog: CatalogDocument,
): DecodedWorkbenchShare {
  if (decoded.sourceFormat === 'v3') {
    const requested: SelectionIntent = {
      languageId: decoded.state.languageId,
      toolchainId: decoded.state.toolchainId,
      referenceSetId: decoded.state.referenceSetId,
      outputId: decoded.state.outputId,
      runtimeId: decoded.state.runtimeId === 'not-required' ? null : decoded.state.runtimeId,
    }
    const selection = normalizeSelectionIntent(catalog, requested)
    const template = templateForLanguage(catalog, selection.languageId)
    return {
      replacement: {
        files: decoded.state.files,
        activeFile: decoded.state.activeFile,
        sourceOrder: decoded.state.sourceOrder,
        selection,
        buildMode: decoded.state.buildMode,
        ...(template ? { template } : {}),
      },
      warnings: [],
    }
  }

  const requestedOptions = decoded.requestedLegacyOptions
  const preset = requestedOptions.branchId
    ? catalog.presets.find(
        (candidate) =>
          candidate.visibility !== 'hidden' &&
          (candidate.id === requestedOptions.branchId ||
            candidate.legacyAliases.includes(requestedOptions.branchId ?? '')),
      )
    : undefined
  const toolchain = requestedOptions.branchId
    ? catalog.toolchains.find(
        (candidate) =>
          candidate.id === requestedOptions.branchId ||
          candidate.legacyAliases.includes(requestedOptions.branchId ?? ''),
      )
    : undefined
  const requested: SelectionIntent = {
    languageId: requestedOptions.languageId ?? decoded.workspace.languageId,
    toolchainId: preset?.toolchainId ?? toolchain?.id ?? null,
    referenceSetId: preset?.referenceSetId ?? toolchain?.defaultReferenceSetId ?? null,
    outputId: requestedOptions.outputId ?? preset?.defaultOutputId ?? 'compile-check',
    runtimeId: preset?.defaultRuntimeId ?? null,
  }
  const selection = normalizeSelectionIntent(catalog, requested)
  const template = templateForLanguage(catalog, selection.languageId)
  return {
    replacement: {
      files: decoded.workspace.files,
      activeFile: decoded.workspace.activeFile,
      sourceOrder: decoded.workspace.sourceOrder,
      selection,
      buildMode: requestedOptions.buildMode,
      ...(template ? { template } : {}),
    },
    warnings: decoded.warnings,
  }
}

function templateForLanguage(catalog: CatalogDocument, languageId: string) {
  const language = catalog.languages.find((candidate) => candidate.id === languageId)
  return language
    ? { fileName: language.defaultFileName, source: language.defaultSource }
    : undefined
}
