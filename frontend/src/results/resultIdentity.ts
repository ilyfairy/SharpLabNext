import type { ArtifactProcessorIdentity, BuildIdentity, JitIdentity, OperationResult, RuntimeIdentity } from '../api/types'

export type OperationIdentity = BuildIdentity | ArtifactProcessorIdentity | RuntimeIdentity | JitIdentity

export interface OperationIdentityEntry {
  resultType: OperationResult['resultType']
  identity: OperationIdentity
}

export interface ResultIdentitySummary {
  build: BuildIdentity | null
  processors: ArtifactProcessorIdentity[]
  runtime: RuntimeIdentity | null
  jit: JitIdentity | null
  releaseIds: string[]
}

function identityFor(result: OperationResult): OperationIdentity | null {
  switch (result.resultType) {
    case 'build':
    case 'compile-check':
    case 'generated-source':
      return result.identity
    case 'ast':
    case 'explain':
    case 'artifact-transform':
    case 'artifact-render':
    case 'artifact-verification':
      return result.identity ?? null
    case 'run':
    case 'jit':
      return result.identity
  }
}

export function operationIdentityEntries(results: readonly OperationResult[]): OperationIdentityEntry[] {
  return results.flatMap((result) => {
    const identity = identityFor(result)
    return identity ? [{ resultType: result.resultType, identity }] : [];
  })
}

export function summarizeResultIdentities(results: readonly OperationResult[]): ResultIdentitySummary {
  let build: BuildIdentity | null = null
  let runtime: RuntimeIdentity | null = null
  let jit: JitIdentity | null = null
  const processors = new Map<string, ArtifactProcessorIdentity>()
  const releaseIds = new Set<string>()

  for (const result of results) {
    switch (result.resultType) {
      case 'build':
      case 'compile-check':
      case 'generated-source':
        build = result.identity
        releaseIds.add(result.identity.releaseId)
        break
      case 'ast':
      case 'explain':
        if (result.identity) {
          build = result.identity
          releaseIds.add(result.identity.releaseId)
        }
        break
      case 'artifact-transform':
      case 'artifact-render':
      case 'artifact-verification':
        if (result.identity) {
          const key = [result.identity.processorId, result.identity.processorVersion, result.identity.workerImageId].join('\u0000')
          processors.set(key, result.identity)
          releaseIds.add(result.identity.releaseId)
        }
        break
      case 'run':
        runtime = result.identity
        break
      case 'jit':
        runtime = result.identity
        jit = result.identity
        break
    }
  }

  return {
    build,
    processors: [...processors.values()],
    runtime,
    jit,
    releaseIds: [...releaseIds],
  }
}
