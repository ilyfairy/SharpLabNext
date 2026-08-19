import type {
  BuildConfiguration,
  CatalogDocument,
  OutputManifest,
  ResolvedSelection,
  ResolveSelectionResponse,
} from '../api/types'
import type { ResultIdentitySummary } from './resultIdentity'

export interface IdentityStripItem {
  id: string
  label: string
  value: string
  title: string
}

export interface ResultIdentityPresentation {
  items: IdentityStripItem[]
  copyText: string
}

interface ResultIdentityPresentationInput {
  summary: ResultIdentitySummary
  catalog: CatalogDocument | undefined
  catalogRevision: string | undefined
  referenceSetSnapshot: { id: string; displayName: string; digest: string } | null | undefined
  resolution: ResolveSelectionResponse | undefined
  selection: ResolvedSelection | undefined
  output: OutputManifest | undefined
  fallback: {
    languageId: string
    toolchainId: string | null
    referenceSetId: string | null
    outputId: string
    runtimeId: string | null
  }
  buildMode: BuildConfiguration
  operationIds: readonly string[]
}

function shortToken(value: string): string {
  if (value.startsWith('sha256:')) return `sha256:${value.slice(7, 19)}`
  return value.length <= 20 ? value : value.slice(0, 12)
}

function versionAndCommit(version: string, commit: string | null | undefined): string {
  return `${version} @ ${commit ? shortToken(commit) : 'n/a'}`
}

function unique(values: readonly string[]): string[] {
  return [...new Set(values)]
}

export function createResultIdentityPresentation({
  summary,
  catalog,
  catalogRevision,
  referenceSetSnapshot,
  resolution,
  selection,
  output,
  fallback,
  buildMode,
  operationIds,
}: ResultIdentityPresentationInput): ResultIdentityPresentation {
  const effectiveSelection = selection ?? resolution?.effectiveSelection
  const languageId =
    summary.build?.languageId ?? effectiveSelection?.languageId ?? fallback.languageId
  const toolchainId =
    summary.build?.toolchainId ??
    effectiveSelection?.toolchainId ??
    fallback.toolchainId ??
    'unresolved'
  const referenceSetId =
    summary.build?.referenceSetId ??
    effectiveSelection?.referenceSetId ??
    fallback.referenceSetId ??
    'unresolved'
  const outputId = effectiveSelection?.outputId ?? output?.id ?? fallback.outputId
  const runtimeId = effectiveSelection?.runtimeId ?? fallback.runtimeId
  const referenceSet =
    referenceSetSnapshot?.id === referenceSetId
      ? referenceSetSnapshot
      : catalog?.referenceSets.find((candidate) => candidate.id === referenceSetId)
  const processorStageIds = unique(
    resolution?.pipelinePlan.stages
      .filter(
        (stage) => stage.kind === 'transform' || stage.kind === 'render' || stage.kind === 'verify',
      )
      .map((stage) => stage.providerId) ?? [],
  )
  const processorExpected = processorStageIds.length > 0
  const jitExpected =
    summary.jit !== null ||
    outputId === 'jit-asm' ||
    resolution?.pipelinePlan.stages.some((stage) => stage.kind === 'jit') === true
  const runtimeRequired = output?.requiresRuntime ?? false
  const releaseIds = summary.releaseIds.length
    ? summary.releaseIds
    : [resolution?.pipelinePlan.releaseId ?? catalog?.releaseId ?? 'unavailable']

  const items: IdentityStripItem[] = []
  if (summary.build) {
    items.push({
      id: 'compiler',
      label: 'Compiler',
      value: versionAndCommit(summary.build.compilerVersion, summary.build.compilerCommit),
      title: [
        `toolchain=${summary.build.toolchainId}`,
        `compilerVersion=${summary.build.compilerVersion}`,
        `compilerCommit=${summary.build.compilerCommit ?? 'not-provided'}`,
        `compilerImage=${summary.build.workerImageId}`,
      ].join('\n'),
    })
  } else {
    items.push({
      id: 'compiler',
      label: 'Compiler',
      value: 'Not run',
      title: `Selected toolchain=${toolchainId}`,
    })
  }

  items.push({
    id: 'reference-set',
    label: 'Reference set',
    value: summary.build
      ? `${referenceSet?.displayName ?? referenceSetId} · ${referenceSet?.digest ? shortToken(referenceSet.digest) : 'digest unavailable'}`
      : 'Not run',
    title: [
      `referenceSet=${referenceSetId}`,
      `referenceSetDigest=${referenceSet?.digest ?? 'unavailable'}`,
    ].join('\n'),
  })

  if (processorExpected || summary.processors.length > 0) {
    const processorTitle =
      summary.processors.length > 0
        ? summary.processors
            .map((processor) => {
              const manifest = catalog?.artifactProcessors.find(
                (candidate) => candidate.id === processor.processorId,
              )
              return [
                `processor=${processor.processorId}`,
                `processorVersion=${processor.processorVersion}`,
                `catalogVersion=${manifest?.resolvedVersion ?? 'unavailable'}`,
                `processorImage=${processor.workerImageId}`,
              ].join('\n')
            })
            .join('\n\n')
        : processorStageIds
            .map((processorId) => {
              const manifest = catalog?.artifactProcessors.find(
                (candidate) => candidate.id === processorId,
              )
              return `expectedProcessor=${processorId}\ncatalogVersion=${manifest?.resolvedVersion ?? 'unavailable'}`
            })
            .join('\n\n')
    const firstProcessor = summary.processors[0]
    const firstManifest = firstProcessor
      ? catalog?.artifactProcessors.find((candidate) => candidate.id === firstProcessor.processorId)
      : undefined
    items.push({
      id: 'processor',
      label: 'Processor',
      value:
        summary.processors.length === 0
          ? 'Not run'
          : summary.processors.length === 1 && firstProcessor
            ? `${firstManifest?.displayName ?? firstProcessor.processorId} ${firstProcessor.processorVersion}`
            : `${summary.processors.length} processors`,
      title: processorTitle,
    })
  }

  items.push({
    id: 'runtime',
    label: 'Runtime',
    value: !runtimeRequired
      ? 'Not required'
      : summary.runtime
        ? versionAndCommit(summary.runtime.runtimeVersion, summary.runtime.runtimeCommit)
        : 'Not run',
    title: !runtimeRequired
      ? 'This output does not execute user code.'
      : summary.runtime
        ? [
            `runtime=${runtimeId ?? 'unresolved'}`,
            `runtimeVersion=${summary.runtime.runtimeVersion}`,
            `runtimeCommit=${summary.runtime.runtimeCommit}`,
            `runtimeImage=${summary.runtime.runtimeImageId}`,
            `rid=${summary.runtime.rid}`,
            `architecture=${summary.runtime.architecture}`,
          ].join('\n')
        : `Selected runtime=${runtimeId ?? 'unresolved'}`,
  })

  if (jitExpected) {
    items.push({
      id: 'jit',
      label: 'JIT',
      value: summary.jit
        ? versionAndCommit(summary.jit.jitVersion, summary.jit.jitCommit)
        : 'Not run',
      title: summary.jit
        ? [
            `jitVersion=${summary.jit.jitVersion}`,
            `jitCommit=${summary.jit.jitCommit}`,
            `jitProvider=${summary.jit.jitProvider}`,
            `cpuFeatureProfile=${summary.jit.cpuFeatureProfile}`,
            `tieringPolicy=${summary.jit.tieringPolicy}`,
            `pgoPolicy=${summary.jit.pgoPolicy}`,
            `inspectionMethod=${summary.jit.inspectionMethod}`,
          ].join('\n')
        : 'The selected pipeline includes a JIT stage.',
    })
  }

  const imageEntries = [
    ...(summary.build
      ? [{ role: `compiler:${summary.build.toolchainId}`, value: summary.build.workerImageId }]
      : []),
    ...summary.processors.map((processor) => ({
      role: `processor:${processor.processorId}`,
      value: processor.workerImageId,
    })),
    ...(summary.runtime ? [{ role: 'runtime', value: summary.runtime.runtimeImageId }] : []),
  ]
  const imageIds = unique(imageEntries.map((entry) => entry.value))
  if (imageIds.length > 0) {
    items.push({
      id: 'images',
      label: 'Images',
      value: imageIds.length === 1 ? shortToken(imageIds[0] ?? '') : `${imageIds.length} images`,
      title: imageEntries.map((entry) => `${entry.role}=${entry.value}`).join('\n'),
    })
  }

  items.push({
    id: 'release',
    label: 'Release',
    value:
      releaseIds.length === 1 ? (releaseIds[0] ?? 'unavailable') : `Mixed (${releaseIds.length})`,
    title: releaseIds.map((releaseId) => `release=${releaseId}`).join('\n'),
  })

  const copyLines = [
    `language=${languageId}`,
    `toolchain=${toolchainId}`,
    `referenceSet=${referenceSetId}`,
    `output=${outputId}`,
    `runtime=${runtimeRequired ? (runtimeId ?? 'unresolved') : 'not-required'}`,
    `mode=${buildMode}`,
  ]
  if (summary.build) {
    copyLines.push(
      `compilerVersion=${summary.build.compilerVersion}`,
      `compilerCommit=${summary.build.compilerCommit ?? 'not-provided'}`,
      `compilerImage=${summary.build.workerImageId}`,
      `referenceSetDigest=${referenceSet?.digest ?? 'unavailable'}`,
    )
  } else {
    copyLines.push('compiler=not-run', 'referenceSetDigest=not-run')
  }
  if (summary.processors.length > 0) {
    summary.processors.forEach((processor, index) => {
      copyLines.push(
        `processor.${index}.id=${processor.processorId}`,
        `processor.${index}.version=${processor.processorVersion}`,
        `processor.${index}.image=${processor.workerImageId}`,
      )
    })
  } else {
    copyLines.push(`processor=${processorExpected ? 'not-run' : 'not-required'}`)
  }
  if (runtimeRequired) {
    if (summary.runtime) {
      copyLines.push(
        `runtimeVersion=${summary.runtime.runtimeVersion}`,
        `runtimeCommit=${summary.runtime.runtimeCommit}`,
        `runtimeImage=${summary.runtime.runtimeImageId}`,
        `runtimeRid=${summary.runtime.rid}`,
        `runtimeArchitecture=${summary.runtime.architecture}`,
      )
    } else {
      copyLines.push('runtimeIdentity=not-run')
    }
  } else {
    copyLines.push('runtimeIdentity=not-required')
  }
  if (jitExpected) {
    if (summary.jit) {
      copyLines.push(
        `jitVersion=${summary.jit.jitVersion}`,
        `jitCommit=${summary.jit.jitCommit}`,
        `jitProvider=${summary.jit.jitProvider}`,
        `jitCpuFeatureProfile=${summary.jit.cpuFeatureProfile}`,
        `jitTieringPolicy=${summary.jit.tieringPolicy}`,
        `jitPgoPolicy=${summary.jit.pgoPolicy}`,
        `jitInspectionMethod=${summary.jit.inspectionMethod}`,
      )
    } else {
      copyLines.push('jit=not-run')
    }
  } else {
    copyLines.push('jit=not-required')
  }
  copyLines.push(
    `release=${releaseIds.join(',')}`,
    `catalog=${catalogRevision ?? catalog?.revision ?? 'unavailable'}`,
    `pipeline=${resolution?.pipelineResolutionId ?? 'unresolved'}`,
    `operation=${unique(operationIds).join(',') || 'none'}`,
  )

  return { items, copyText: copyLines.join('\n') }
}
