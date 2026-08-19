/**
 * Publish one already-built, release-scoped runtime candidate tag.
 *
 * Candidate builds remain local. This boundary independently revalidates the
 * source tree, row inputs and image labels, captures the local image ID, then
 * proves the pushed tag and its unique RepoDigest still resolve to those same
 * bytes. Docker push text is never used as identity evidence.
 */

import { spawnSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import {
  candidateExpectedImageLabels,
  candidateOperationHelpers,
  candidateReleaseImageTag,
  candidateTargetSpecifications,
  validateCandidateBuildInputs,
} from './build-runtime-candidate.mjs'
import {
  validateCandidateExpectedLabels,
  validateCandidateImageIdentity,
} from './runtime-candidate-input-validation.mjs'
import {
  bindRuntimeCandidateImage,
  hashRuntimeOperationHelpers,
  inspectDockerImage,
  inspectGitSourceState,
  validateGitSourceState,
  validateRuntimeImageInspection,
} from './runtime-promotion-image-binding.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const imageIdPattern = /^sha256:[0-9a-f]{64}$/
const digestReferencePattern = /^([^@\s]+)@(sha256:[0-9a-f]{64})$/
const localTaggedReferencePattern = /^[a-z0-9][a-z0-9._:/-]*:[a-z0-9][a-z0-9._-]{0,127}$/

export const runtimeCandidatePublishUsage = `Usage:
  node eng/publish-runtime-candidate.mjs <candidate-target>
    --destination <registry-host>/<repository>:<RELEASE_ID>`

export class RuntimeCandidatePublishError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimeCandidatePublishError'
  }
}

function fail(message, options) {
  throw new RuntimeCandidatePublishError(message, options)
}

function commandFailure(description, result) {
  if (result?.error !== undefined) {
    return new RuntimeCandidatePublishError(`${description}: ${result.error.message}`, {
      cause: result.error,
    })
  }
  const stderr = String(result?.stderr ?? '').trim()
  return new RuntimeCandidatePublishError(
    `${description} (command exited ${result?.status ?? '<unknown>'})` +
    (stderr.length === 0 ? '' : `: ${stderr}`),
  )
}

function runChecked(spawn, arguments_, environment, description, discardOutput = false) {
  const result = spawn('docker', arguments_, {
    cwd: repositoryRoot,
    env: environment,
    encoding: 'utf8',
    shell: false,
    ...(discardOutput ? { stdio: 'ignore' } : {}),
  })
  if (result?.error !== undefined || result?.status !== 0) {
    throw commandFailure(description, result)
  }
  return result
}

function parseTaggedReference(value, releaseId) {
  if (typeof value !== 'string' || !localTaggedReferencePattern.test(value) || value.includes('@')) {
    fail('destination must be a canonical tagged Docker repository reference.')
  }
  const slash = value.indexOf('/')
  if (slash <= 0) fail('destination must include an explicit registry host.')
  const host = value.slice(0, slash)
  if (host !== 'localhost' && !host.includes('.') && !host.includes(':')) {
    fail('destination must include an explicit registry host.')
  }
  const tagSeparator = value.lastIndexOf(':')
  const repository = value.slice(0, tagSeparator)
  const tag = value.slice(tagSeparator + 1)
  if (tag !== releaseId) {
    fail(`destination tag must equal the release-scoped RELEASE_ID '${releaseId}'.`)
  }
  return { repository, tag }
}

function ensureSameCapturedImage(inspection, captured, label, sourceRevision, expectedLabels) {
  const failures = validateRuntimeImageInspection(inspection, {
    sourceRevision,
    expectedLabels,
  })
  if (inspection.imageId !== captured.imageId) {
    failures.push(
      `${label} resolves to ${inspection.imageId}, but the captured image ID is ${captured.imageId}`,
    )
  }
  if (inspection.sizeBytes !== captured.sizeBytes) {
    failures.push(
      `${label} reports Size ${inspection.sizeBytes}, but the captured Size is ${captured.sizeBytes}`,
    )
  }
  if (failures.length > 0) {
    fail(`Runtime candidate publication binding changed:\n- ${failures.join('\n- ')}`)
  }
}

function uniqueDestinationDigest(repoDigests, repository) {
  const matches = repoDigests.filter(reference => {
    const match = digestReferencePattern.exec(reference)
    return match !== null && match[1] === repository
  })
  if (matches.length !== 1) {
    fail(
      `published destination must expose exactly one RepoDigest for '${repository}'; ` +
      `observed ${matches.length}.`,
    )
  }
  return matches[0]
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  const target = argv[0]
  if (target === undefined || target.startsWith('-')) fail('candidate target is required.')
  let destination
  let seenDestination = false
  for (let index = 1; index < argv.length; index++) {
    const argument = argv[index]
    if (argument !== '--destination') fail(`unknown option '${argument}'.`)
    if (seenDestination) fail("duplicate option '--destination'.")
    seenDestination = true
    destination = argv[++index]
    if (destination === undefined || destination.length === 0) {
      fail('--destination requires a value.')
    }
  }
  if (destination === undefined) fail('--destination is required.')
  return { target, destination }
}

/** Publish and return the immutable registry binding as structured data. */
export function publishRuntimeCandidate(input, options = {}) {
  const {
    target,
    destination,
  } = input
  const {
    values = process.env,
    spawn = spawnSync,
    inspectGit = gitOptions => inspectGitSourceState(gitOptions),
    inspectImage = (reference, inspectOptions) => inspectDockerImage(reference, inspectOptions),
    validateInputs = validateCandidateBuildInputs,
    expectedLabelsFor = candidateExpectedImageLabels,
    operationSpecificationsFor = candidateOperationHelpers,
    hashOperations = (imageId, specifications) => hashRuntimeOperationHelpers(
      imageId,
      specifications,
      { spawn, cwd: repositoryRoot, env: values },
    ),
    beforePush = () => {},
    afterPush = () => {},
  } = options
  const specification = candidateTargetSpecifications[target]
  if (specification === undefined || specification.matrixBindingKind === 'combined-mono-wine') {
    fail(`candidate target '${target}' is not a publishable runtime matrix row.`)
  }
  const requireValidInputs = () => {
    const failures = validateInputs(target, values)
    if (failures.length > 0) {
      fail(`Runtime candidate publication inputs are invalid:\n- ${failures.join('\n- ')}`)
    }
  }
  requireValidInputs()
  const releaseId = values.RELEASE_ID
  if (typeof releaseId !== 'string' || !/^[a-z0-9][a-z0-9._-]{0,127}$/.test(releaseId)) {
    fail('RELEASE_ID must be a canonical Docker tag value.')
  }
  const destinationParts = parseTaggedReference(destination, releaseId)
  const sourceReference = candidateReleaseImageTag(target, values)
  if (!localTaggedReferencePattern.test(sourceReference)) {
    fail(`derived release-scoped local tag '${sourceReference}' is invalid.`)
  }

  const requireCleanSource = () => {
    const sourceState = inspectGit({
      spawn,
      cwd: repositoryRoot,
      env: values,
    })
    const sourceBinding = validateGitSourceState(sourceState, values.SOURCE_REVISION)
    if (!sourceBinding.promotionEligible || sourceBinding.failures.length > 0) {
      fail(`Runtime candidate publication requires clean exact source:\n- ${sourceBinding.failures.join('\n- ')}`)
    }
  }
  requireCleanSource()

  const expectedLabels = expectedLabelsFor(target, values)
  const inspect = reference => inspectImage(reference, {
    spawn,
    cwd: repositoryRoot,
    env: values,
  })
  const captured = bindRuntimeCandidateImage({
    candidateReference: sourceReference,
    sourceRevision: values.SOURCE_REVISION,
    expectedLabels,
    inspect,
  })
  if (!imageIdPattern.test(captured.imageId)) fail('captured image ID is invalid.')
  const identityFailures = [
    ...validateCandidateImageIdentity(values, captured.labels, specification.imageInputs),
    ...validateCandidateExpectedLabels(captured.labels, expectedLabels),
  ]
  if (identityFailures.length > 0) {
    fail(`Runtime candidate publication identity is invalid:\n- ${identityFailures.join('\n- ')}`)
  }
  const operationSpecifications = operationSpecificationsFor(target, values)
  const capturedHelpers = hashOperations(captured.imageId, operationSpecifications)

  ensureSameCapturedImage(
    inspect(sourceReference),
    captured,
    'release-scoped local tag before publication',
    values.SOURCE_REVISION,
    expectedLabels,
  )
  if (destination !== sourceReference) {
    runChecked(
      spawn,
      ['image', 'tag', captured.imageId, destination],
      values,
      `Could not bind captured image '${captured.imageId}' to '${destination}'`,
    )
  }
  ensureSameCapturedImage(
    inspect(destination),
    captured,
    'destination tag before publication',
    values.SOURCE_REVISION,
    expectedLabels,
  )
  ensureSameCapturedImage(
    inspect(sourceReference),
    captured,
    'release-scoped local tag immediately before publication',
    values.SOURCE_REVISION,
    expectedLabels,
  )
  requireValidInputs()
  requireCleanSource()

  beforePush({ sourceReference, destination, imageId: captured.imageId })
  runChecked(
    spawn,
    ['image', 'push', destination],
    values,
    `Could not push '${destination}'`,
    true,
  )
  afterPush({ sourceReference, destination, imageId: captured.imageId })

  const pushedTag = inspect(destination)
  ensureSameCapturedImage(
    pushedTag,
    captured,
    'destination tag after publication',
    values.SOURCE_REVISION,
    expectedLabels,
  )
  const pinnedReference = uniqueDestinationDigest(
    pushedTag.repoDigests,
    destinationParts.repository,
  )
  runChecked(
    spawn,
    ['image', 'pull', pinnedReference],
    values,
    `Could not fetch immutable published image '${pinnedReference}'`,
    true,
  )
  const finalBinding = bindRuntimeCandidateImage({
    candidateReference: destination,
    pinnedReference,
    sourceRevision: values.SOURCE_REVISION,
    expectedLabels,
    inspect,
  })
  ensureSameCapturedImage(
    inspect(sourceReference),
    captured,
    'release-scoped local tag after publication',
    values.SOURCE_REVISION,
    expectedLabels,
  )
  if (finalBinding.imageId !== captured.imageId) {
    fail(
      `pinned reference '${pinnedReference}' no longer resolves to captured image ` +
      `'${captured.imageId}'.`,
    )
  }
  requireValidInputs()
  requireCleanSource()
  const repeatedExpectedLabels = expectedLabelsFor(target, values)
  if (JSON.stringify(repeatedExpectedLabels) !== JSON.stringify(expectedLabels)) {
    fail('runtime candidate expected label/input bindings changed across publication.')
  }
  const publishedHelpers = hashOperations(finalBinding.imageId, operationSpecifications)
  if (JSON.stringify(publishedHelpers) !== JSON.stringify(capturedHelpers)) {
    fail('runtime operation helper bytes changed across candidate publication.')
  }
  return Object.freeze({
    schemaVersion: 1,
    candidateTarget: target,
    profileId: values.RUNTIME_MATRIX_PROFILE_ID,
    sourceReference,
    destinationTag: destination,
    imageId: captured.imageId,
    pinnedReference,
    platform: Object.freeze({ os: finalBinding.operatingSystem, architecture: finalBinding.architecture }),
    operations: publishedHelpers,
    sourceRevision: values.SOURCE_REVISION,
  })
}

export function runRuntimeCandidatePublish(argv, options = {}) {
  const { output = console } = options
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) {
      output.log(runtimeCandidatePublishUsage)
      return 0
    }
    const result = publishRuntimeCandidate(parsed, options)
    output.log(JSON.stringify(result))
    return 0
  } catch (error) {
    output.error(`runtime candidate publish error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimeCandidatePublish(process.argv.slice(2))
}
