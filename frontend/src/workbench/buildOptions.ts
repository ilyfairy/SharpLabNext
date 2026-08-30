import type { BuildConfiguration, BuildOptions, BuildOutputKind, PipelineStageDescriptor } from '../api/types'

const AUTO_OUTPUT_KIND_LANGUAGES = new Set(['csharp', 'visual-basic', 'gsharp'])
const LIBRARY_OUTPUT_KIND_LANGUAGES = new Set(['il'])

export interface WorkbenchOutputKindSelection {
  languageId: string
  toolchainId: string
  referenceSetId: string
  buildMode: BuildConfiguration
  selectionRevision: number
}

export interface RememberedWorkbenchOutputKind extends WorkbenchOutputKindSelection {
  outputKind: BuildOutputKind
}

export function retainResolvedWorkbenchOutputKind(selection: WorkbenchOutputKindSelection, resolvedOutputKind: BuildOutputKind | null, remembered: RememberedWorkbenchOutputKind | null): { outputKind: BuildOutputKind; remembered: RememberedWorkbenchOutputKind | null } {
  if (resolvedOutputKind) {
    const next = { ...selection, outputKind: resolvedOutputKind }
    return { outputKind: resolvedOutputKind, remembered: next };
  }
  if (
    remembered?.languageId === selection.languageId &&
    remembered.toolchainId === selection.toolchainId &&
    remembered.referenceSetId === selection.referenceSetId &&
    remembered.buildMode === selection.buildMode &&
    remembered.selectionRevision === selection.selectionRevision
  ) {
    return { outputKind: remembered.outputKind, remembered };
  }
  return { outputKind: 'console', remembered: null };
}

export function buildOutputKindForResolvedPipeline(languageId: string, stages: readonly PipelineStageDescriptor[]): BuildOutputKind {
  if (stages.some((stage) => stage.kind === 'run')) return 'console'
  if (AUTO_OUTPUT_KIND_LANGUAGES.has(languageId)) return 'auto'
  if (languageId === 'fsharp') return 'console'
  return LIBRARY_OUTPUT_KIND_LANGUAGES.has(languageId) ? 'library' : 'console'
}

export function createWorkbenchBuildOptions(languageId: string, configuration: BuildConfiguration, stages: readonly PipelineStageDescriptor[]): BuildOptions {
  const options: BuildOptions = {
    configuration,
    optimize: configuration === 'release',
    outputKind: buildOutputKindForResolvedPipeline(languageId, stages),
  }

  if (languageId === 'jsharp') return options

  Object.assign(options, {
    allowUnsafe: languageId === 'csharp',
    emitPortablePdb: true,
    nullableContext: 'project-default',
    preprocessorSymbols: [],
    checkOverflow: false,
  } satisfies Partial<BuildOptions>)

  if (languageId === 'csharp') options.languageVersion = 'preview'
  return options
}
