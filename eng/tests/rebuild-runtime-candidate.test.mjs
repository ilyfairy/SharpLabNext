import assert from 'node:assert/strict'
import path from 'node:path'
import test from 'node:test'

import {
  pullPinnedRuntimeCandidateImage,
  rebuildRuntimeCandidateFromCommittedSource,
  requireSameRuntimeCandidateBuild,
  RuntimeCandidateRebuildError,
} from '../release/rebuild-runtime-candidate.mjs'

test('formal rebuild invokes only the reviewed wrapper with stderr-only diagnostics', () => {
  const calls = []
  const stderr = []
  const repositoryRoot = path.resolve('test-repository')
  const allowedDirtyPaths = [
    'profiles/runtime-promotion-plans/mono-6.12-linux-x64.json',
    'profiles/runtime-promotion-plans/mono-6.12-linux-x64.json.sig',
  ]
  const spawn = () => { throw new Error('the fake build must not start a real process') }
  rebuildRuntimeCandidateFromCommittedSource(
    'runtime-mono-matrix-candidate',
    {
      SOURCE_REVISION: 'a'.repeat(40),
      RUNTIME_MATRIX_PROFILE_ID: 'mono-6.12-linux-x64',
      RUNTIME_CANDIDATE_SOURCE_CONTEXT: 'working-tree-development',
      RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE: 'true',
      RUNTIME_CANDIDATE_ALLOWED_DIRTY_PATHS: 'eng/build-runtime-candidate.mjs',
      BUILDX_BAKE_FILE: 'untrusted.hcl',
      RETAINED_VALUE: 'retained',
    },
    {
      repositoryRoot,
      spawn,
      allowedDirtyPaths,
      stderr: { write(value) { stderr.push(value) } },
      runBuild(argv, environment, observedSpawn, output, buildOptions) {
        calls.push({ argv, environment, observedSpawn, buildOptions })
        output.log('formal build log')
        output.error('formal build diagnostic')
        return 0
      },
    },
  )

  const call = assertSingle(calls)
  assert.deepEqual(call.argv, ['runtime-mono-matrix-candidate'])
  assert.equal(call.observedSpawn, spawn)
  assert.equal(call.environment.RETAINED_VALUE, 'retained')
  assert.equal(call.environment.RUNTIME_CANDIDATE_SOURCE_CONTEXT, undefined)
  assert.equal(call.environment.RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE, undefined)
  assert.equal(call.environment.RUNTIME_CANDIDATE_ALLOWED_DIRTY_PATHS, undefined)
  assert.equal(call.environment.BUILDX_BAKE_FILE, undefined)
  assert.equal(call.buildOptions.repositoryRoot, repositoryRoot)
  assert.deepEqual(call.buildOptions.allowedDirtyPaths, allowedDirtyPaths)
  assert.deepEqual(call.buildOptions.buildStdio, ['inherit', 2, 2])
  assert.deepEqual(stderr, ['formal build log\n', 'formal build diagnostic\n'])
})

test('formal rebuild allows only source-revision-addressed Wine operator receipt outputs', () => {
  const revision = 'a'.repeat(40)
  const valid = [
    `profiles/runtime-operator-receipts/wine-coreclr-${revision}.json`,
    `profiles/runtime-operator-receipts/wine-coreclr-${revision}.json.sig`,
  ]
  assert.doesNotThrow(() => rebuildRuntimeCandidateFromCommittedSource(
    'runtime-wine-dotnet-matrix-candidate',
    { SOURCE_REVISION: revision, RUNTIME_MATRIX_PROFILE_ID: 'wine-dotnet-8-linux-x64' },
    { allowedDirtyPaths: valid, runBuild() { return 0 } },
  ))
  assert.throws(() => rebuildRuntimeCandidateFromCommittedSource(
    'runtime-wine-dotnet-matrix-candidate',
    { SOURCE_REVISION: revision, RUNTIME_MATRIX_PROFILE_ID: 'wine-dotnet-8-linux-x64' },
    {
      allowedDirtyPaths: ['profiles/runtime-operator-receipts/wine-coreclr-bad.json'],
      runBuild() { throw new Error('must not run') },
    },
  ), /generated path.*not allowed/)
})

test('formal rebuild rejects invalid targets and command failures', () => {
  let calls = 0
  assert.throws(
    () => rebuildRuntimeCandidateFromCommittedSource('--unsupported-option', {}, {
      runBuild() { calls++; return 0 },
    }),
    RuntimeCandidateRebuildError,
  )
  assert.equal(calls, 0)

  assert.throws(
    () => rebuildRuntimeCandidateFromCommittedSource('runtime-mono-matrix-candidate', {
      RUNTIME_MATRIX_PROFILE_ID: 'mono-6.12-linux-x64',
    }, {
      runBuild() { return 1 },
    }),
    /rebuild exited 1/,
  )
  assert.throws(
    () => rebuildRuntimeCandidateFromCommittedSource('runtime-mono-matrix-candidate', {
      RUNTIME_MATRIX_PROFILE_ID: 'mono-6.12-linux-x64',
    }, {
      runBuild() { throw new Error('injected build failure') },
    }),
    /injected build failure/,
  )
  assert.throws(
    () => rebuildRuntimeCandidateFromCommittedSource('runtime-mono-matrix-candidate', {
      RUNTIME_MATRIX_PROFILE_ID: 'mono-6.12-linux-x64',
    }, {
      allowedDirtyPaths: ['eng/build-runtime-candidate.mjs'],
      runBuild() { throw new Error('must not run') },
    }),
    /generated path.*not allowed/,
  )
})

test('pinned pull is exact and deterministic rebuild identity includes every label', () => {
  const reference = `registry.example/runtime@sha256:${'d'.repeat(64)}`
  const calls = []
  pullPinnedRuntimeCandidateImage(reference, { RETAINED_VALUE: 'retained' }, {
    repositoryRoot: path.resolve('test-repository'),
    spawn(command, arguments_, options) {
      calls.push({ command, arguments_, options })
      return { status: 0 }
    },
  })
  const pull = assertSingle(calls)
  assert.equal(pull.command, 'docker')
  assert.deepEqual(pull.arguments_, ['image', 'pull', reference])
  assert.equal(pull.options.env.RETAINED_VALUE, 'retained')
  assert.equal(pull.options.stdio, 'ignore')

  const identity = {
    imageId: `sha256:${'a'.repeat(64)}`,
    sizeBytes: 512,
    operatingSystem: 'linux',
    architecture: 'amd64',
    sourceRevision: 'b'.repeat(40),
    labels: { second: '2', first: '1' },
  }
  assert.doesNotThrow(() => requireSameRuntimeCandidateBuild(identity, {
    ...identity,
    labels: { first: '1', second: '2' },
  }))
  for (const [name, mutate] of [
    ['image ID', value => { value.imageId = `sha256:${'c'.repeat(64)}` }],
    ['size', value => { value.sizeBytes++ }],
    ['operating system', value => { value.operatingSystem = 'windows' }],
    ['architecture', value => { value.architecture = 'arm64' }],
    ['source revision', value => { value.sourceRevision = 'c'.repeat(40) }],
    ['labels', value => { value.labels = { ...value.labels, injected: 'changed' } }],
  ]) {
    const changed = { ...identity, labels: { ...identity.labels } }
    mutate(changed)
    assert.throws(
      () => requireSameRuntimeCandidateBuild(identity, changed),
      /rebuild changed.*image identity/,
      name,
    )
  }
  assert.throws(
    () => pullPinnedRuntimeCandidateImage('registry.example/runtime:latest', {}, {
      spawn() { throw new Error('must not run') },
    }),
    RuntimeCandidateRebuildError,
  )
})

function assertSingle(values) {
  assert.equal(values.length, 1)
  return values[0]
}
