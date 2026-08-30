/**
 * Rebuild a formal candidate through the reviewed committed-source wrapper.
 * Callers compare the immutable image identity before and after this command;
 * this command deliberately has no development override.
 */

import { spawnSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { runCandidateBuild } from '../build-runtime-candidate.mjs'
import { runtimeOperatorReceiptPaths } from './runtime-wine-operator-binding.mjs'

const defaultRepositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const candidateTargetPattern = /^[a-z0-9][a-z0-9-]{0,127}$/
const pinnedReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const profileIdPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/

export class RuntimeCandidateRebuildError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimeCandidateRebuildError'
  }
}

function canonicalGeneratedPaths(profileId) {
  if (!profileIdPattern.test(profileId ?? '')) {
    throw new RuntimeCandidateRebuildError('Runtime candidate rebuild profile ID is invalid.')
  }
  const evidenceRoot = `profiles/runtime-promotion-evidence/${profileId}`
  return new Set([
    `profiles/runtime-promotion-plans/${profileId}.json`,
    `profiles/runtime-promotion-plans/${profileId}.json.sig`,
    `profiles/runtime-promotion-plans/${profileId}.profile.json`,
    `${evidenceRoot}/performance.json`,
    ...['run', 'jit-asm', 'inspection', 'execution-flow'].map(
      capability => `${evidenceRoot}/${capability}.json`,
    ),
    `profiles/runtime-promotion-receipts/${profileId}.json`,
  ])
}

function validateAllowedDirtyPaths(profileId, sourceRevision, paths) {
  if (!Array.isArray(paths)) {
    throw new RuntimeCandidateRebuildError('Runtime candidate rebuild generated-path allowlist must be an array.')
  }
  if (paths.length === 0) return Object.freeze([])
  const canonical = canonicalGeneratedPaths(profileId)
  if (/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/.test(sourceRevision ?? '')) {
    const operator = runtimeOperatorReceiptPaths(sourceRevision)
    canonical.add(operator.receiptPath)
    canonical.add(operator.signaturePath)
  }
  const result = []
  for (const value of paths) {
    if (typeof value !== 'string' || !canonical.has(value) || result.includes(value)) {
      throw new RuntimeCandidateRebuildError(
        `Runtime candidate rebuild generated path '${value}' is not allowed.`,
      )
    }
    result.push(value)
  }
  return Object.freeze(result)
}

function stderrOutput(stream) {
  const write = value => stream.write(`${String(value)}\n`)
  return Object.freeze({ log: write, error: write })
}

/** Run the formal build entry without permitting caller-supplied source-state labels. */
export function rebuildRuntimeCandidateFromCommittedSource(
  target,
  values = process.env,
  options = {},
) {
  if (typeof target !== 'string' || !candidateTargetPattern.test(target)) {
    throw new RuntimeCandidateRebuildError('Runtime candidate rebuild target is invalid.')
  }
  const {
    repositoryRoot = defaultRepositoryRoot,
    spawn = spawnSync,
    runBuild = runCandidateBuild,
    allowedDirtyPaths = [],
    stderr = process.stderr,
  } = options
  const generatedPaths = validateAllowedDirtyPaths(
    values.RUNTIME_MATRIX_PROFILE_ID,
    values.SOURCE_REVISION,
    allowedDirtyPaths,
  )
  const environment = { ...values }
  for (const name of [
    'BUILDX_BAKE_FILE',
    'BUILDX_BAKE_FILE_SEPARATOR',
    'RUNTIME_CANDIDATE_SOURCE_CONTEXT',
    'RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE',
    'RUNTIME_CANDIDATE_ALLOWED_DIRTY_PATHS',
  ]) {
    delete environment[name]
  }
  let status
  try {
    status = runBuild(
      [target],
      environment,
      spawn,
      stderrOutput(stderr),
      {
        repositoryRoot,
        allowedDirtyPaths: generatedPaths,
        buildStdio: ['inherit', 2, 2],
      },
    )
  } catch (error) {
    throw new RuntimeCandidateRebuildError(
      `Could not run the formal runtime candidate rebuild: ${error.message}`,
      { cause: error },
    )
  }
  if (status !== 0) {
    throw new RuntimeCandidateRebuildError(
      `Formal runtime candidate rebuild exited ${status ?? '<unknown>'}.`,
    )
  }
}

/** Pull the exact registry object immediately before the promotion binding. */
export function pullPinnedRuntimeCandidateImage(reference, values = process.env, options = {}) {
  if (typeof reference !== 'string' || !pinnedReferencePattern.test(reference)) {
    throw new RuntimeCandidateRebuildError('Pinned runtime candidate reference is invalid.')
  }
  const {
    repositoryRoot = defaultRepositoryRoot,
    spawn = spawnSync,
  } = options
  const result = spawn('docker', ['image', 'pull', reference], {
    cwd: repositoryRoot,
    env: values,
    stdio: 'ignore',
    shell: false,
  })
  if (result?.error !== undefined) {
    throw new RuntimeCandidateRebuildError(
      `Could not pull the pinned runtime candidate: ${result.error.message}`,
      { cause: result.error },
    )
  }
  if (result?.status !== 0) {
    throw new RuntimeCandidateRebuildError(
      `Pinned runtime candidate pull exited ${result?.status ?? '<unknown>'}.`,
    )
  }
}

/** Require deterministic rebuild identity without depending on label key order. */
export function requireSameRuntimeCandidateBuild(before, after) {
  const beforeLabels = Object.entries(before?.labels ?? {}).sort(([left], [right]) =>
    left.localeCompare(right, 'en'))
  const afterLabels = Object.entries(after?.labels ?? {}).sort(([left], [right]) =>
    left.localeCompare(right, 'en'))
  if (before?.imageId !== after?.imageId ||
      before?.sizeBytes !== after?.sizeBytes ||
      before?.operatingSystem !== after?.operatingSystem ||
      before?.architecture !== after?.architecture ||
      before?.sourceRevision !== after?.sourceRevision ||
      JSON.stringify(beforeLabels) !== JSON.stringify(afterLabels)) {
    throw new RuntimeCandidateRebuildError('Formal committed-source rebuild changed the runtime candidate image identity.')
  }
}
