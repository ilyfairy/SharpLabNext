import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  bindRuntimeCandidateImage,
  hashDockerImageFile,
  hashRuntimeOperationHelpers,
  inspectDockerImage,
  inspectGitSourceState,
  parseDockerImageInspection,
  RuntimePromotionImageBindingError,
  validateGitSourceState,
  validateRuntimeImageInspection,
} from './runtime-promotion-image-binding.mjs'

const imageId = `sha256:${'a'.repeat(64)}`
const otherImageId = `sha256:${'b'.repeat(64)}`
const sourceRevision = 'c'.repeat(40)
const pinnedReference = `registry.example/runtime@sha256:${'d'.repeat(64)}`
const containerId = 'e'.repeat(64)
const imageSizeBytes = 536870912

function inspection(overrides = {}) {
  return {
    imageId,
    sizeBytes: imageSizeBytes,
    operatingSystem: 'linux',
    architecture: 'amd64',
    repoDigests: [],
    labels: {
      'org.opencontainers.image.revision': sourceRevision,
      'io.sharplabnext.source.revision': sourceRevision,
      'org.opencontainers.image.version': 'candidate-test',
    },
    ...overrides,
  }
}

function rawInspection(overrides = {}) {
  return JSON.stringify([{
    Id: imageId,
    Size: imageSizeBytes,
    Os: 'linux',
    Architecture: 'amd64',
    RepoDigests: [],
    Config: { Labels: inspection().labels },
    ...overrides,
  }])
}

function fakeFileDocker(copyAction) {
  const calls = []
  return {
    calls,
    spawn(command, arguments_) {
      calls.push([command, [...arguments_]])
      if (arguments_[0] === 'create') {
        return { status: 0, stdout: `${containerId}\n`, stderr: '' }
      }
      if (arguments_[0] === 'cp') {
        copyAction(arguments_[2])
        return { status: 0, stdout: '', stderr: '' }
      }
      if (arguments_[0] === 'rm') return { status: 0, stdout: '', stderr: '' }
      throw new Error(`Unexpected Docker call: ${arguments_.join(' ')}`)
    },
  }
}

test('full Docker inspection retains image identity, platform, RepoDigests and labels', () => {
  const parsed = parseDockerImageInspection(rawInspection({
    RepoDigests: [pinnedReference, pinnedReference],
  }), 'candidate:tag')
  assert.deepEqual(parsed, {
    imageId,
    sizeBytes: imageSizeBytes,
    operatingSystem: 'linux',
    architecture: 'amd64',
    repoDigests: [pinnedReference],
    labels: inspection().labels,
  })

  const calls = []
  const inspected = inspectDockerImage('candidate:tag', {
    spawn(command, arguments_) {
      calls.push([command, arguments_])
      return { status: 0, stdout: rawInspection(), stderr: '' }
    },
  })
  assert.equal(inspected.imageId, imageId)
  assert.deepEqual(calls, [['docker', ['image', 'inspect', 'candidate:tag']]])
})

test('image validation rejects malformed identity, Size, platform and either source label', () => {
  const failures = validateRuntimeImageInspection(inspection({
    imageId: `sha256:${'A'.repeat(64)}`,
    sizeBytes: 0,
    operatingSystem: 'windows',
    architecture: 'arm64',
    labels: {
      'org.opencontainers.image.revision': 'wrong',
    },
  }), {
    sourceRevision,
    expectedLabels: { 'org.opencontainers.image.version': 'candidate-test' },
  })
  assert.match(failures.join('\n'), /image ID must be sha256/)
  assert.match(failures.join('\n'), /image Size must be a positive safe integer/)
  assert.match(failures.join('\n'), /linux\/amd64/)
  assert.match(failures.join('\n'), /org\.opencontainers\.image\.revision/)
  assert.match(failures.join('\n'), /io\.sharplabnext\.source\.revision/)
  assert.match(failures.join('\n'), /org\.opencontainers\.image\.version/)
})

test('local candidates never turn an image ID into a registry manifest reference', () => {
  const bound = bindRuntimeCandidateImage({
    candidateReference: 'registry.example/runtime:candidate',
    sourceRevision,
    inspect: () => inspection(),
  })
  assert.equal(bound.imageId, imageId)
  assert.equal(bound.sizeBytes, imageSizeBytes)
  assert.equal(bound.reference, null)
  assert.deepEqual(bound.repoDigests, [])
})

test('pinned reference must be a RepoDigest resolving to the captured object', () => {
  const references = []
  const bound = bindRuntimeCandidateImage({
    candidateReference: 'registry.example/runtime:candidate',
    pinnedReference,
    sourceRevision,
    inspect(reference) {
      references.push(reference)
      return inspection({ repoDigests: [pinnedReference] })
    },
  })
  assert.equal(bound.reference, pinnedReference)
  assert.deepEqual(references, ['registry.example/runtime:candidate', pinnedReference])

  assert.throws(() => bindRuntimeCandidateImage({
    candidateReference: 'registry.example/runtime:candidate',
    pinnedReference,
    sourceRevision,
    inspect(reference) {
      return reference === pinnedReference
        ? inspection({ imageId: otherImageId, repoDigests: [pinnedReference] })
        : inspection({ repoDigests: [pinnedReference] })
    },
  }), /resolves to sha256:b{64}.*candidate.*sha256:a{64}/s)

  assert.throws(() => bindRuntimeCandidateImage({
    candidateReference: 'registry.example/runtime:candidate',
    pinnedReference,
    sourceRevision,
    inspect: () => inspection(),
  }), /absent from RepoDigests/)

  assert.throws(() => bindRuntimeCandidateImage({
    candidateReference: 'registry.example/runtime:candidate',
    pinnedReference,
    sourceRevision,
    inspect(reference) {
      return reference === pinnedReference
        ? inspection({ sizeBytes: imageSizeBytes + 1, repoDigests: [pinnedReference] })
        : inspection({ repoDigests: [pinnedReference] })
    },
  }), /reports Size 536870913.*reports Size 536870912/s)
})

test('Docker inspection rejects missing, non-integral and unsafe Size values', () => {
  for (const size of [undefined, 0, 1.5, Number.MAX_SAFE_INTEGER + 1]) {
    const overrides = { Size: size }
    assert.throws(
      () => parseDockerImageInspection(rawInspection(overrides), 'candidate:tag'),
      /invalid Size.*positive safe integer/,
      String(size),
    )
  }
})

test('Git source binding requires exact HEAD and a clean tree unless explicitly development-only', () => {
  assert.deepEqual(validateGitSourceState({
    headRevision: sourceRevision,
    isDirty: false,
  }, sourceRevision), {
    failures: [],
    promotionEligible: true,
  })
  const dirty = validateGitSourceState({
    headRevision: sourceRevision,
    isDirty: true,
  }, sourceRevision)
  assert.match(dirty.failures.join('\n'), /worktree is dirty/)
  assert.equal(dirty.promotionEligible, false)

  const development = validateGitSourceState({
    headRevision: sourceRevision,
    isDirty: true,
  }, sourceRevision, { allowUncommittedSourceForDevelopment: true })
  assert.deepEqual(development.failures, [])
  assert.equal(development.promotionEligible, false)

  const mismatch = validateGitSourceState({
    headRevision: 'f'.repeat(40),
    isDirty: false,
  }, sourceRevision)
  assert.match(mismatch.failures.join('\n'), /does not match Git HEAD/)
})

test('Git inspection independently observes HEAD and dirty state', () => {
  const calls = []
  const state = inspectGitSourceState({
    spawn(command, arguments_) {
      calls.push([command, arguments_])
      return arguments_[0] === 'rev-parse'
        ? { status: 0, stdout: `${sourceRevision}\n`, stderr: '' }
        : { status: 0, stdout: ' M eng/file.mjs\n', stderr: '' }
    },
  })
  assert.deepEqual(state, { headRevision: sourceRevision, isDirty: true })
  assert.deepEqual(calls, [
    ['git', ['rev-parse', '--verify', 'HEAD']],
    ['git', ['status', '--porcelain=v1', '-z', '--untracked-files=normal']],
  ])
})

test('Git inspection permits only explicitly bound generated paths', () => {
  const inspect = stdout => inspectGitSourceState({
    allowedDirtyPaths: [
      'profiles/runtime-promotion-plans/runtime.profile.json',
      'profiles/runtime-promotion-plans/runtime.json',
    ],
    spawn(_command, arguments_) {
      return arguments_[0] === 'rev-parse'
        ? { status: 0, stdout: `${sourceRevision}\n`, stderr: '' }
        : { status: 0, stdout, stderr: '' }
    },
  })

  assert.equal(inspect(
    '?? profiles/runtime-promotion-plans/runtime.profile.json\0' +
    '?? profiles/runtime-promotion-plans/runtime.json\0',
  ).isDirty, false)
  assert.equal(inspect(
    '?? profiles/runtime-promotion-plans/runtime.json\0 M eng/file.mjs\0',
  ).isDirty, true)
})

test('helper bytes are copied from an immutable image ID and hashed on the host', () => {
  const bytes = Buffer.from('trusted helper bytes')
  const docker = fakeFileDocker(destination => fs.writeFileSync(destination, bytes))
  const digest = hashDockerImageFile(
    imageId,
    '/opt/sharplabnext/SharpLabNext.Runner.dll',
    { spawn: docker.spawn },
  )
  assert.equal(
    digest,
    `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`,
  )
  assert.deepEqual(docker.calls.map(([, arguments_]) => arguments_.slice(0, 2)), [
    ['create', imageId],
    ['cp', `${containerId}:/opt/sharplabnext/SharpLabNext.Runner.dll`],
    ['rm', containerId],
  ])
  assert.equal(docker.calls.some(([, arguments_]) => arguments_.includes('sha256sum')), false)
})

test('helper extraction rejects missing, symlink, empty and oversized files and cleans containers', t => {
  const cases = [
    ['missing', () => {}],
    ['empty', destination => fs.writeFileSync(destination, Buffer.alloc(0))],
    ['oversized', destination => fs.writeFileSync(destination, Buffer.alloc(5))],
  ]
  for (const [name, action] of cases) {
    const docker = fakeFileDocker(action)
    assert.throws(() => hashDockerImageFile(
      imageId,
      '/opt/sharplabnext/SharpLabNext.Runner.dll',
      { spawn: docker.spawn, maximumBytes: name === 'oversized' ? 4 : 64 },
    ), name === 'missing' ? /ENOENT/ : name === 'empty' ? /is empty/ : /exceeds/)
    assert.deepEqual(docker.calls.at(-1)[1], ['rm', containerId], name)
  }

  const symlinkTarget = path.join(os.tmpdir(), `sharplabnext-helper-link-target-${process.pid}`)
  fs.writeFileSync(symlinkTarget, 'outside')
  t.after(() => fs.rmSync(symlinkTarget, { force: true }))
  const linked = fakeFileDocker(destination => fs.symlinkSync(symlinkTarget, destination, 'file'))
  try {
    assert.throws(() => hashDockerImageFile(
      imageId,
      '/opt/sharplabnext/SharpLabNext.Runner.dll',
      { spawn: linked.spawn },
    ), /regular non-link file/)
    assert.deepEqual(linked.calls.at(-1)[1], ['rm', containerId])
  } catch (error) {
    if (error?.code !== 'EPERM') throw error
    t.diagnostic('Current Windows policy does not permit creating a file symlink.')
  }
})

test('helper extraction accepts only canonical paths below /opt/sharplabnext', () => {
  for (const invalidPath of [
    '/tmp/SharpLabNext.Runner.dll',
    '/opt/sharplabnext//SharpLabNext.Runner.dll',
    '/opt/sharplabnext/SharpLabNext.Runner.dll/',
    '/opt/sharplabnext/../outside.dll',
  ]) {
    let dockerCalls = 0
    assert.throws(() => hashDockerImageFile(imageId, invalidPath, {
      spawn() {
        dockerCalls++
        return { status: 0, stdout: '' }
      },
    }), /outside|not canonical/, invalidPath)
    assert.equal(dockerCalls, 0, invalidPath)
  }
})

test('operation helper binding deduplicates shared assembly paths and binds profiler separately', () => {
  const copied = []
  const docker = fakeFileDocker(destination => {
    copied.push(destination)
    fs.writeFileSync(destination, `helper-${copied.length}`)
  })
  const legacyPath = '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll'
  const profilerPath = '/opt/sharplabnext/SharpLabNext.JitProfiler.so'
  const operations = hashRuntimeOperationHelpers(imageId, {
    run: {
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: legacyPath,
    },
    jit: {
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: legacyPath,
      profilerPath,
    },
  }, { spawn: docker.spawn })
  assert.equal(operations.run.assemblySha256, operations.jit.assemblySha256)
  assert.match(operations.jit.profilerSha256, /^sha256:[0-9a-f]{64}$/)
  assert.equal(docker.calls.filter(([, arguments_]) => arguments_[0] === 'create').length, 2)
  assert.equal(
    docker.calls.filter(([, arguments_]) => arguments_[0] === 'create')
      .every(([, arguments_]) => arguments_[1] === imageId),
    true,
  )
})

test('mutable tag retargeting cannot change helper extraction after binding', () => {
  let candidateImageId = imageId
  const bound = bindRuntimeCandidateImage({
    candidateReference: 'registry.example/runtime:candidate',
    sourceRevision,
    inspect: () => inspection({ imageId: candidateImageId }),
  })
  candidateImageId = otherImageId

  const docker = fakeFileDocker(destination => fs.writeFileSync(destination, 'fixed bytes'))
  hashDockerImageFile(
    bound.imageId,
    '/opt/sharplabnext/SharpLabNext.Runner.dll',
    { spawn: docker.spawn },
  )
  assert.deepEqual(docker.calls[0][1], ['create', imageId])
})

test('Docker command errors are surfaced without attempting unsafe extraction', () => {
  assert.throws(() => inspectDockerImage('candidate:tag', {
    spawn: () => ({ status: 1, stdout: '', stderr: 'not found' }),
  }), error => {
    assert.equal(error instanceof RuntimePromotionImageBindingError, true)
    assert.match(error.message, /not found/)
    return true
  })
})
