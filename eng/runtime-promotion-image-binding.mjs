/**
 * Observed, fail-closed bindings for runtime candidate images.
 *
 * The first Docker inspection may use a mutable local candidate tag. Its
 * immutable image ID is captured immediately; every container/file operation
 * after that point uses only that ID. Registry manifest digests and local
 * image IDs are deliberately kept as separate identities.
 */

import { spawnSync } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const imageIdPattern = /^sha256:[0-9a-f]{64}$/
const pinnedReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const gitCommitPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const containerIdPattern = /^[0-9a-f]{12,64}$/
const trustedHelperRoot = '/opt/sharplabnext/'

export const defaultMaximumRuntimeHelperBytes = 64 * 1024 * 1024

export class RuntimePromotionImageBindingError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimePromotionImageBindingError'
  }
}

function commandFailure(description, result) {
  if (result?.error !== undefined) {
    return new RuntimePromotionImageBindingError(
      `${description}: ${result.error.message}`,
      { cause: result.error },
    )
  }
  const status = result?.status ?? '<unknown>'
  const stderr = String(result?.stderr ?? '').trim()
  return new RuntimePromotionImageBindingError(
    `${description} (command exited ${status})${stderr.length > 0 ? `: ${stderr}` : ''}`,
  )
}

function runChecked(spawn, command, arguments_, options, description) {
  const result = spawn(command, arguments_, {
    ...options,
    encoding: 'utf8',
    shell: false,
  })
  if (result?.error !== undefined || result?.status !== 0) {
    throw commandFailure(description, result)
  }
  return result
}

function requiredString(value, field, reference) {
  if (typeof value !== 'string' || value.length === 0) {
    throw new RuntimePromotionImageBindingError(
      `Docker inspection for '${reference}' has no valid ${field}.`,
    )
  }
  return value
}

/** Parse the unformatted array returned by `docker image inspect`. */
export function parseDockerImageInspection(stdout, reference) {
  let result
  try {
    result = JSON.parse(String(stdout))
  } catch (error) {
    throw new RuntimePromotionImageBindingError(
      `Docker returned invalid inspection JSON for '${reference}': ${error.message}`,
      { cause: error },
    )
  }
  if (!Array.isArray(result) || result.length !== 1 ||
      result[0] === null || typeof result[0] !== 'object' || Array.isArray(result[0])) {
    throw new RuntimePromotionImageBindingError(
      `Docker returned an invalid inspection result for '${reference}'.`,
    )
  }

  const image = result[0]
  if (!Number.isSafeInteger(image.Size) || image.Size <= 0) {
    throw new RuntimePromotionImageBindingError(
      `Docker inspection for '${reference}' has invalid Size; expected a positive safe integer.`,
    )
  }
  const repoDigests = image.RepoDigests == null
    ? []
    : Array.isArray(image.RepoDigests) && image.RepoDigests.every(value => typeof value === 'string')
      ? [...new Set(image.RepoDigests)].sort()
      : undefined
  if (repoDigests === undefined) {
    throw new RuntimePromotionImageBindingError(
      `Docker inspection for '${reference}' has invalid RepoDigests.`,
    )
  }

  const labelsValue = image.Config?.Labels
  if (labelsValue !== undefined && labelsValue !== null &&
      (typeof labelsValue !== 'object' || Array.isArray(labelsValue))) {
    throw new RuntimePromotionImageBindingError(
      `Docker inspection for '${reference}' has invalid Config.Labels.`,
    )
  }
  const labels = {}
  for (const [name, value] of Object.entries(labelsValue ?? {})) {
    if (typeof value !== 'string') {
      throw new RuntimePromotionImageBindingError(
        `Docker inspection for '${reference}' has a non-string label '${name}'.`,
      )
    }
    labels[name] = value
  }

  return Object.freeze({
    imageId: requiredString(image.Id, 'Id', reference),
    sizeBytes: image.Size,
    operatingSystem: requiredString(image.Os, 'Os', reference),
    architecture: requiredString(image.Architecture, 'Architecture', reference),
    repoDigests: Object.freeze(repoDigests),
    labels: Object.freeze(labels),
  })
}

/** Inspect one image reference without reducing the evidence to labels only. */
export function inspectDockerImage(reference, options = {}) {
  const {
    spawn = spawnSync,
    cwd = process.cwd(),
    env = process.env,
  } = options
  const result = runChecked(
    spawn,
    'docker',
    ['image', 'inspect', reference],
    { cwd, env },
    `Could not inspect Docker image '${reference}'`,
  )
  return parseDockerImageInspection(result.stdout, reference)
}

function labelFailure(labels, name, expected) {
  const observed = labels[name] === undefined ? '<missing>' : JSON.stringify(labels[name])
  return `${name} must equal ${JSON.stringify(expected)}; observed ${observed}`
}

/** Validate properties that are intrinsic to a promotable runtime image. */
export function validateRuntimeImageInspection(inspection, options) {
  const {
    sourceRevision,
    expectedLabels = {},
    pinnedReference,
  } = options
  const failures = []
  if (!imageIdPattern.test(inspection?.imageId ?? '')) {
    failures.push('image ID must be sha256:<64 lowercase hex>')
  }
  if (!Number.isSafeInteger(inspection?.sizeBytes) || inspection.sizeBytes <= 0) {
    failures.push('image Size must be a positive safe integer')
  }
  if (inspection?.operatingSystem !== 'linux' || inspection?.architecture !== 'amd64') {
    failures.push(
      `image platform must be linux/amd64; observed ` +
      `${inspection?.operatingSystem ?? '<missing>'}/${inspection?.architecture ?? '<missing>'}`,
    )
  }
  if (!gitCommitPattern.test(sourceRevision ?? '')) {
    failures.push('source revision must be a full lowercase Git commit')
  }
  for (const label of [
    'org.opencontainers.image.revision',
    'io.sharplabnext.source.revision',
  ]) {
    if (inspection?.labels?.[label] !== sourceRevision) {
      failures.push(labelFailure(inspection?.labels ?? {}, label, sourceRevision))
    }
  }
  for (const [label, expected] of Object.entries(expectedLabels)) {
    if (inspection?.labels?.[label] !== expected) {
      failures.push(labelFailure(inspection?.labels ?? {}, label, expected))
    }
  }
  if (pinnedReference !== undefined) {
    if (!pinnedReferencePattern.test(pinnedReference)) {
      failures.push('pinned image reference must be repository@sha256:<64 lowercase hex>')
    } else if (!inspection?.repoDigests?.includes(pinnedReference)) {
      failures.push(`pinned image reference '${pinnedReference}' is absent from RepoDigests`)
    }
  }
  return failures
}

/**
 * Inspect a candidate tag once, retain its image ID, and optionally prove that
 * a registry digest reference resolves to the same object. A missing
 * RepoDigest is accepted only when no registry identity is claimed.
 */
export function bindRuntimeCandidateImage(options) {
  const {
    candidateReference,
    pinnedReference,
    sourceRevision,
    expectedLabels = {},
    inspect = inspectDockerImage,
    inspectOptions,
  } = options
  const candidate = inspect(candidateReference, inspectOptions)
  const failures = validateRuntimeImageInspection(candidate, {
    sourceRevision,
    expectedLabels,
    pinnedReference,
  })

  let pinned
  if (pinnedReference !== undefined && pinnedReferencePattern.test(pinnedReference)) {
    pinned = inspect(pinnedReference, inspectOptions)
    failures.push(...validateRuntimeImageInspection(pinned, {
      sourceRevision,
      expectedLabels,
      pinnedReference,
    }).map(failure => `pinned reference: ${failure}`))
    if (candidate.imageId !== pinned.imageId) {
      failures.push(
        `pinned image reference '${pinnedReference}' resolves to ${pinned.imageId}, ` +
        `but candidate '${candidateReference}' resolved to ${candidate.imageId}`,
      )
    }
    if (candidate.sizeBytes !== pinned.sizeBytes) {
      failures.push(
        `pinned image reference '${pinnedReference}' reports Size ${pinned.sizeBytes}, ` +
        `but candidate '${candidateReference}' reports Size ${candidate.sizeBytes}`,
      )
    }
  }

  if (failures.length > 0) {
    throw new RuntimePromotionImageBindingError(
      `Runtime candidate image binding failed:\n- ${failures.join('\n- ')}`,
    )
  }
  return Object.freeze({
    imageId: candidate.imageId,
    sizeBytes: candidate.sizeBytes,
    reference: pinnedReference ?? null,
    operatingSystem: candidate.operatingSystem,
    architecture: candidate.architecture,
    repoDigests: candidate.repoDigests,
    labels: candidate.labels,
    sourceRevision,
  })
}

/** Inspect the independent Git identity used to label a candidate image. */
export function inspectGitSourceState(options = {}) {
  const {
    spawn = spawnSync,
    cwd = process.cwd(),
    env = process.env,
    allowedDirtyPaths = [],
  } = options
  const revision = runChecked(
    spawn,
    'git',
    ['rev-parse', '--verify', 'HEAD'],
    { cwd, env },
    'Could not resolve Git HEAD',
  )
  const status = runChecked(
    spawn,
    'git',
    ['status', '--porcelain=v1', '-z', '--untracked-files=normal'],
    { cwd, env },
    'Could not inspect Git worktree state',
  )
  const allowed = new Set(allowedDirtyPaths)
  return Object.freeze({
    headRevision: String(revision.stdout).trim(),
    isDirty: gitStatusHasUnexpectedPaths(String(status.stdout), allowed),
  })
}

function gitStatusHasUnexpectedPaths(stdout, allowedPaths) {
  if (stdout.length === 0) return false
  const records = stdout.includes('\0')
    ? stdout.split('\0').filter(record => record.length > 0)
    : stdout.split(/\r?\n/).filter(record => record.length > 0)
  for (let index = 0; index < records.length; index++) {
    const record = records[index]
    if (record.length < 4 || record[2] !== ' ') return true
    const status = record.slice(0, 2)
    const filename = record.slice(3).replaceAll('\\', '/')
    if (!allowedPaths.has(filename)) return true
    if (status[0] === 'R' || status[0] === 'C' || status[1] === 'R' || status[1] === 'C') {
      const source = records[++index]?.replaceAll('\\', '/')
      if (source === undefined || !allowedPaths.has(source)) return true
    }
  }
  return false
}

export function validateGitSourceState(state, requestedRevision, options = {}) {
  const { allowUncommittedSourceForDevelopment = false } = options
  const failures = []
  if (!gitCommitPattern.test(requestedRevision ?? '')) {
    failures.push('SOURCE_REVISION must be a full lowercase Git commit')
  }
  if (!gitCommitPattern.test(state?.headRevision ?? '')) {
    failures.push('Git HEAD must be a full lowercase Git commit')
  } else if (state.headRevision !== requestedRevision) {
    failures.push(
      `SOURCE_REVISION '${requestedRevision}' does not match Git HEAD '${state.headRevision}'`,
    )
  }
  if (state?.isDirty === true && !allowUncommittedSourceForDevelopment) {
    failures.push(
      'runtime candidate source worktree is dirty; use the explicit development override ' +
      'only for a non-promotable local candidate',
    )
  }
  return Object.freeze({
    failures: Object.freeze(failures),
    promotionEligible: failures.length === 0 && state?.isDirty === false,
  })
}

function validateHelperPath(containerPath) {
  if (typeof containerPath !== 'string' || !containerPath.startsWith(trustedHelperRoot) ||
      containerPath.includes('\\') || containerPath.includes('\0')) {
    throw new RuntimePromotionImageBindingError(
      `Runtime helper path '${containerPath}' is outside ${trustedHelperRoot}.`,
    )
  }
  const segments = containerPath.slice(1).split('/')
  if (segments.some(segment => segment.length === 0 || segment === '.' || segment === '..')) {
    throw new RuntimePromotionImageBindingError(
      `Runtime helper path '${containerPath}' is not canonical.`,
    )
  }
}

function hashRegularFile(filename, maximumBytes) {
  const before = fs.lstatSync(filename)
  if (!before.isFile() || before.isSymbolicLink()) {
    throw new RuntimePromotionImageBindingError(
      `Copied runtime helper '${filename}' must be a regular non-link file.`,
    )
  }
  if (before.size === 0) {
    throw new RuntimePromotionImageBindingError(`Copied runtime helper '${filename}' is empty.`)
  }
  if (before.size > maximumBytes) {
    throw new RuntimePromotionImageBindingError(
      `Copied runtime helper '${filename}' exceeds the ${maximumBytes}-byte limit.`,
    )
  }

  const noFollow = fs.constants.O_NOFOLLOW ?? 0
  const descriptor = fs.openSync(filename, fs.constants.O_RDONLY | noFollow)
  try {
    const opened = fs.fstatSync(descriptor)
    if (!opened.isFile() || opened.size !== before.size ||
        (before.dev !== undefined && opened.dev !== before.dev) ||
        (before.ino !== undefined && opened.ino !== before.ino)) {
      throw new RuntimePromotionImageBindingError(
        `Copied runtime helper '${filename}' changed while it was being verified.`,
      )
    }
    const bytes = fs.readFileSync(descriptor)
    if (bytes.length !== opened.size || bytes.length > maximumBytes) {
      throw new RuntimePromotionImageBindingError(
        `Copied runtime helper '${filename}' changed while it was being read.`,
      )
    }
    return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
  } finally {
    fs.closeSync(descriptor)
  }
}

/**
 * Copy one trusted helper from an unstarted container and hash the host bytes.
 * No command, including sha256sum, is executed inside the candidate image.
 */
export function hashDockerImageFile(imageId, containerPath, options = {}) {
  if (!imageIdPattern.test(imageId ?? '')) {
    throw new RuntimePromotionImageBindingError(
      'Runtime helper extraction requires sha256:<64 lowercase hex> image ID.',
    )
  }
  validateHelperPath(containerPath)
  const {
    spawn = spawnSync,
    cwd = process.cwd(),
    env = process.env,
    maximumBytes = defaultMaximumRuntimeHelperBytes,
    temporaryRoot = os.tmpdir(),
  } = options
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes <= 0) {
    throw new RuntimePromotionImageBindingError('Runtime helper size limit must be a positive integer.')
  }

  const temporaryDirectory = fs.mkdtempSync(path.join(temporaryRoot, 'sharplabnext-helper-'))
  const destination = path.join(temporaryDirectory, 'payload')
  let containerId
  let primaryError
  try {
    const created = runChecked(
      spawn,
      'docker',
      ['create', imageId],
      { cwd, env },
      `Could not create stopped helper container from '${imageId}'`,
    )
    containerId = String(created.stdout).trim()
    if (!containerIdPattern.test(containerId)) {
      throw new RuntimePromotionImageBindingError(
        `Docker returned invalid helper container ID '${containerId}'.`,
      )
    }
    runChecked(
      spawn,
      'docker',
      ['cp', `${containerId}:${containerPath}`, destination],
      { cwd, env },
      `Could not copy runtime helper '${containerPath}' from '${imageId}'`,
    )
    return hashRegularFile(destination, maximumBytes)
  } catch (error) {
    primaryError = error
    throw error
  } finally {
    let cleanupError
    if (containerId !== undefined) {
      try {
        runChecked(
          spawn,
          'docker',
          ['rm', containerId],
          { cwd, env },
          `Could not remove stopped helper container '${containerId}'`,
        )
      } catch (error) {
        cleanupError = error
      }
    }
    fs.rmSync(temporaryDirectory, { recursive: true, force: true })
    if (primaryError === undefined && cleanupError !== undefined) throw cleanupError
  }
}

/** Hash operation-specific helpers while copying shared paths only once. */
export function hashRuntimeOperationHelpers(imageId, operations, options = {}) {
  if (operations === null || typeof operations !== 'object' || Array.isArray(operations)) {
    throw new RuntimePromotionImageBindingError('Runtime helper operations must be an object.')
  }
  const digests = new Map()
  const hashPath = helperPath => {
    let digest = digests.get(helperPath)
    if (digest === undefined) {
      digest = hashDockerImageFile(imageId, helperPath, options)
      digests.set(helperPath, digest)
    }
    return digest
  }
  const observed = {}
  for (const [operationName, operation] of Object.entries(operations)) {
    if (!['run', 'jit'].includes(operationName) || operation === null ||
        typeof operation !== 'object' || Array.isArray(operation)) {
      throw new RuntimePromotionImageBindingError(
        `Unsupported runtime helper operation '${operationName}'.`,
      )
    }
    if (typeof operation.implementation !== 'string' || operation.implementation.length === 0) {
      throw new RuntimePromotionImageBindingError(
        `Runtime helper operation '${operationName}' has no implementation.`,
      )
    }
    validateHelperPath(operation.assemblyPath)
    const binding = {
      implementation: operation.implementation,
      assemblyPath: operation.assemblyPath,
      assemblySha256: hashPath(operation.assemblyPath),
    }
    if (operation.profilerPath !== undefined) {
      if (operationName !== 'jit') {
        throw new RuntimePromotionImageBindingError('Only the JIT operation may bind a profiler.')
      }
      validateHelperPath(operation.profilerPath)
      binding.profilerPath = operation.profilerPath
      binding.profilerSha256 = hashPath(operation.profilerPath)
    }
    observed[operationName] = Object.freeze(binding)
  }
  if (observed.run === undefined) {
    throw new RuntimePromotionImageBindingError('Runtime helper operations must include run.')
  }
  return Object.freeze(observed)
}
