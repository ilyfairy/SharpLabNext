import assert from 'node:assert/strict'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  candidateExpectedImageLabels,
  candidateOperationHelpers,
} from './build-runtime-candidate.mjs'
import {
  deriveRuntimeCandidateEnvironment,
  readRuntimeMatrix,
} from './runtime-candidate-environment.mjs'
import {
  publishRuntimeCandidate,
  runRuntimeCandidatePublish,
} from './publish-runtime-candidate.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const matrix = readRuntimeMatrix(path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'))
const target = 'runtime-dotnet-matrix-candidate'
const profileId = 'dotnet-5-linux-x64'
const sourceRevision = 'f'.repeat(40)
const releaseId = 'release-1'
const imageId = `sha256:${'a'.repeat(64)}`
const otherImageId = `sha256:${'b'.repeat(64)}`
const manifestDigest = `sha256:${'d'.repeat(64)}`
const destination = `registry.example/sharplabnext/runtime-${profileId}:${releaseId}`
const destinationRepository = destination.slice(0, destination.lastIndexOf(':'))
const pinnedReference = `${destinationRepository}@${manifestDigest}`

function pinnedImage(name, character) {
  return `registry.example/${name}@sha256:${character.repeat(64)}`
}

function environment() {
  return {
    IMAGE_PREFIX: 'sharplabnext',
    RELEASE_ID: releaseId,
    SOURCE_DATE_EPOCH: '1',
    SOURCE_REVISION: sourceRevision,
    BASE_DOTNET_SDK_IMAGE: pinnedImage('dotnet-sdk', 'c'),
    WINE_CONTROL_TFM: matrix.controlRuntime.targetFramework,
    ...deriveRuntimeCandidateEnvironment(profileId, matrix).environment,
  }
}

function inspection(values, overrides = {}) {
  return {
    imageId,
    sizeBytes: 512,
    operatingSystem: 'linux',
    architecture: 'amd64',
    repoDigests: [],
    labels: candidateExpectedImageLabels(target, values),
    ...overrides,
  }
}

function successfulFixture(overrides = {}) {
  const values = overrides.values ?? environment()
  const calls = []
  let pushed = false
  let pulled = false
  let sourceImageId = imageId
  let destinationImageId = imageId
  const extraRepoDigests = overrides.extraRepoDigests ?? []
  const fixture = {
    values,
    calls,
    setSourceImageId(value) { sourceImageId = value },
    setDestinationImageId(value) { destinationImageId = value },
    options: {
      values,
      inspectGit: overrides.inspectGit ?? (() => ({
        headRevision: sourceRevision,
        isDirty: false,
      })),
      inspectImage(reference) {
        if (overrides.inspectImage !== undefined) {
          const replacement = overrides.inspectImage(reference, {
            pushed, pulled,
            sourceImageId,
            destinationImageId,
            values,
          })
          if (replacement !== undefined) return replacement
        }
        const isPinned = reference.includes('@sha256:')
        const isDestination = reference === destination || isPinned
        return inspection(values, {
          imageId: isDestination ? destinationImageId : sourceImageId,
          repoDigests: pushed && isDestination
            ? [pinnedReference, ...extraRepoDigests]
            : [],
        })
      },
      spawn(command, arguments_) {
        calls.push([command, arguments_])
        if (arguments_[0] === 'image' && arguments_[1] === 'tag') {
          destinationImageId = arguments_[2]
          return { status: 0, stdout: '', stderr: '' }
        }
        if (arguments_[0] === 'image' && arguments_[1] === 'push') {
          if (overrides.pushFailure) return { status: 23, stdout: manifestDigest, stderr: 'push failed' }
          pushed = true
          return { status: 0, stdout: 'sha256:this-text-is-not-trusted', stderr: '' }
        }
        if (arguments_[0] === 'image' && arguments_[1] === 'pull') {
          if (overrides.pullFailure) return { status: 24, stdout: '', stderr: 'pull failed' }
          pulled = true
          return { status: 0, stdout: 'ignored pull text', stderr: '' }
        }
        throw new Error(`unexpected command ${command} ${arguments_.join(' ')}`)
      },
      operationSpecificationsFor: candidateOperationHelpers,
      hashOperations(_observedImageId, specifications) {
        if (overrides.hashOperations !== undefined) {
          return overrides.hashOperations(_observedImageId, specifications)
        }
        return Object.fromEntries(Object.entries(specifications).map(([name, value]) => [name, {
          ...value,
          assemblySha256: `sha256:${'e'.repeat(64)}`,
          ...(value.profilerPath === undefined
            ? {}
            : { profilerSha256: `sha256:${'f'.repeat(64)}` }),
        }]))
      },
    },
  }
  return fixture
}

test('publisher emits a machine-readable pinned reference from post-push inspection', () => {
  const fixture = successfulFixture({
    extraRepoDigests: [`other.example/unrelated@sha256:${'7'.repeat(64)}`],
  })
  const result = publishRuntimeCandidate({ target, destination }, fixture.options)
  assert.equal(result.imageId, imageId)
  assert.equal(result.pinnedReference, pinnedReference)
  assert.equal(result.sourceReference, `sharplabnext/runtime-${profileId}:${releaseId}`)
  assert.equal(result.destinationTag, destination)
  assert.equal(result.platform.os, 'linux')
  assert.equal(result.operations.run.assemblySha256, `sha256:${'e'.repeat(64)}`)
  assert.deepEqual(
    fixture.calls.filter(([, arguments_]) => arguments_[0] === 'image'),
    [
      ['docker', ['image', 'tag', imageId, destination]],
      ['docker', ['image', 'push', destination]],
      ['docker', ['image', 'pull', pinnedReference]],
    ],
  )
})

test('publisher rejects non-registry and non-release-scoped destinations before mutation', () => {
  for (const invalid of [
    `sharplabnext/runtime-${profileId}:${releaseId}`,
    `registry.example/sharplabnext/runtime-${profileId}:latest`,
    `${destinationRepository}@${manifestDigest}`,
  ]) {
    const fixture = successfulFixture()
    assert.throws(
      () => publishRuntimeCandidate({ target, destination: invalid }, fixture.options),
      /registry host|RELEASE_ID|tagged Docker repository/,
      invalid,
    )
    assert.equal(fixture.calls.length, 0, invalid)
  }
})

test('publisher rejects source, input and label drift before push', () => {
  const dirty = successfulFixture({
    inspectGit: () => ({ headRevision: sourceRevision, isDirty: true }),
  })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, dirty.options),
    /requires clean exact source|worktree is dirty/,
  )

  const inputDrift = successfulFixture()
  inputDrift.values.RUNTIME_MATRIX_RUNTIME_VERSION = '5.0.16'
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, inputDrift.options),
    /publication inputs.*RUNTIME_MATRIX_RUNTIME_VERSION/s,
  )

  const labelDrift = successfulFixture({
    inspectImage(reference, state) {
      return inspection(state.values, {
        imageId: reference === destination ? state.destinationImageId : state.sourceImageId,
        labels: {
          ...candidateExpectedImageLabels(target, state.values),
          'io.sharplabnext.runtime.commit': '0'.repeat(40),
        },
      })
    },
  })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, labelDrift.options),
    /runtime\.commit/,
  )
  assert.equal(labelDrift.calls.some(([, args]) => args[1] === 'push'), false)
})

test('publisher detects tag races and a pinned digest resolving to the wrong image', () => {
  const raced = successfulFixture()
  raced.options.afterPush = () => raced.setDestinationImageId(otherImageId)
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, raced.options),
    /captured image ID|binding changed/,
  )

  const sourceRace = successfulFixture()
  sourceRace.options.beforePush = () => sourceRace.setSourceImageId(otherImageId)
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, sourceRace.options),
    /release-scoped local tag after publication.*captured image ID/s,
  )

  const wrongDigest = successfulFixture({
    inspectImage(reference, state) {
      if (reference === pinnedReference) {
        return inspection(state.values, {
          imageId: otherImageId,
          repoDigests: [pinnedReference],
        })
      }
      return undefined
    },
  })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, wrongDigest.options),
    /pinned image reference.*resolves to.*but candidate/s,
  )
})

test('publisher requires one matching repository digest and ignores unrelated repositories', () => {
  const none = successfulFixture({
    inspectImage(reference, state) {
      if (state.pushed && reference === destination) {
        return inspection(state.values, {
          repoDigests: [`other.example/runtime@sha256:${'4'.repeat(64)}`],
        })
      }
      return undefined
    },
  })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, none.options),
    /exactly one RepoDigest.*observed 0/,
  )

  const duplicate = successfulFixture({
    extraRepoDigests: [`${destinationRepository}@sha256:${'8'.repeat(64)}`],
  })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, duplicate.options),
    /exactly one RepoDigest.*observed 2/,
  )
})

test('push failure and helper drift fail closed without trusting push output', () => {
  const failed = successfulFixture({ pushFailure: true })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, failed.options),
    /Could not push.*push failed/,
  )

  const pullFailed = successfulFixture({ pullFailure: true })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, pullFailed.options),
    /Could not fetch immutable published image.*pull failed/,
  )

  let helperReads = 0
  const helperDrift = successfulFixture({
    hashOperations(_image, specifications) {
      helperReads++
      return Object.fromEntries(Object.entries(specifications).map(([name, value]) => [name, {
        ...value,
        assemblySha256: `sha256:${(helperReads === 1 ? 'e' : 'f').repeat(64)}`,
      }]))
    },
  })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, helperDrift.options),
    /helper bytes changed/,
  )
})

test('remote immutable re-fetch rejects post-pull label drift', () => {
  const fixture = successfulFixture({
    inspectImage(reference, state) {
      if (state.pulled && reference === pinnedReference) {
        return inspection(state.values, {
          repoDigests: [pinnedReference],
          labels: {
            ...candidateExpectedImageLabels(target, state.values),
            'io.sharplabnext.runtime.commit': '0'.repeat(40),
          },
        })
      }
      return undefined
    },
  })
  assert.throws(
    () => publishRuntimeCandidate({ target, destination }, fixture.options),
    /pinned reference:.*runtime\.commit/s,
  )
})

test('publisher retains operation-specific profiler identity in its pinned output', () => {
  const fixture = successfulFixture()
  fixture.options.operationSpecificationsFor = () => ({
    run: {
      implementation: 'runner-v1',
      assemblyPath: '/opt/sharplabnext/Runner.dll',
    },
    jit: {
      implementation: 'jit-v1',
      assemblyPath: '/opt/sharplabnext/Jit.dll',
      profilerPath: '/opt/sharplabnext/Profiler.so',
    },
  })
  const result = publishRuntimeCandidate({ target, destination }, fixture.options)
  assert.equal(result.operations.jit.profilerPath, '/opt/sharplabnext/Profiler.so')
  assert.equal(result.operations.jit.profilerSha256, `sha256:${'f'.repeat(64)}`)
})

test('CLI returns canonical JSON and surfaces push failures', () => {
  const output = {
    logs: [],
    errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  const fixture = successfulFixture()
  assert.equal(runRuntimeCandidatePublish([
    target, '--destination', destination,
  ], { ...fixture.options, output }), 0)
  assert.deepEqual(JSON.parse(output.logs[0]), publishShape())

  const failureOutput = { ...output, logs: [], errors: [] }
  const failure = successfulFixture({ pushFailure: true })
  assert.equal(runRuntimeCandidatePublish([
    target, '--destination', destination,
  ], { ...failure.options, output: failureOutput }), 1)
  assert.match(failureOutput.errors.join('\n'), /push failed/)
})

function publishShape() {
  const specifications = candidateOperationHelpers(target, environment())
  return {
    schemaVersion: 1,
    candidateTarget: target,
    profileId,
    sourceReference: `sharplabnext/runtime-${profileId}:${releaseId}`,
    destinationTag: destination,
    imageId,
    pinnedReference,
    platform: { os: 'linux', architecture: 'amd64' },
    operations: Object.fromEntries(Object.entries(specifications).map(([name, value]) => [name, {
      ...value,
      assemblySha256: `sha256:${'e'.repeat(64)}`,
    }])),
    sourceRevision,
  }
}
