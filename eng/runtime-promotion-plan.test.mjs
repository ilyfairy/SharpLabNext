import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { candidateExpectedImageLabels } from './build-runtime-candidate.mjs'
import {
  produceRuntimePromotionPlan,
  runRuntimePromotionPlan,
  RuntimePromotionPlanError,
  verifyRuntimePromotionPlan,
} from './runtime-promotion-plan.mjs'

const target = 'runtime-wine-dotnet-matrix-candidate'
const profileId = 'wine-dotnet-7-linux-x64'
const sourceRevision = 'a'.repeat(40)
const imageId = `sha256:${'b'.repeat(64)}`
const otherImageId = `sha256:${'c'.repeat(64)}`
const pinnedReference = `registry.example/runtime@sha256:${'d'.repeat(64)}`
const helperDigest = `sha256:${'e'.repeat(64)}`

function digestReference(name, character) {
  return `registry.example/${name}@sha256:${character.repeat(64)}`
}

function createEnvironment() {
  return {
    IMAGE_PREFIX: 'registry.example/sharplabnext',
    RELEASE_ID: 'candidate-test',
    SOURCE_DATE_EPOCH: '1784678400',
    SOURCE_REVISION: sourceRevision,
    BASE_DOTNET_SDK_IMAGE: digestReference('sdk', '1'),
    RUNTIME_MATRIX_WINE_IMAGE: digestReference('wine', '2'),
    RUNTIME_MATRIX_CONTROL_IMAGE: digestReference('control', '3'),
    RUNTIME_MATRIX_PROFILE_ID: profileId,
    RUNTIME_MATRIX_RUNTIME_VERSION: '7.0.20',
    RUNTIME_MATRIX_RUNTIME_COMMIT: 'f'.repeat(40),
    RUNTIME_MATRIX_JIT_COMMIT: 'f'.repeat(40),
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI:
      'https://builds.dotnet.microsoft.com/dotnet/Runtime/7.0.20/dotnet-runtime-7.0.20-win-x64.zip',
    RUNTIME_MATRIX_WINDOWS_URL:
      'https://builds.dotnet.microsoft.com/dotnet/Runtime/7.0.20/dotnet-runtime-7.0.20-win-x64.zip',
    RUNTIME_MATRIX_WINDOWS_SHA512: '4'.repeat(128),
    WINE_CONTROL_TFM: 'net10.0',
  }
}

function createProfile(environment) {
  const image = `${environment.IMAGE_PREFIX}/runtime-${profileId}:candidate`
  return {
    schemaVersion: 1,
    id: profileId,
    image,
    family: 'coreclr-wine',
    acceptedRuntimeFamilies: ['coreclr-wine', 'coreclr'],
    acceptedFrameworks: [{ name: 'Microsoft.NETCore.App', exactVersion: '7.0.20' }],
    runtimeVersion: '7.0.20',
    runtimeCommit: environment.RUNTIME_MATRIX_RUNTIME_COMMIT,
    jitVersion: '7.0.20',
    jitCommit: environment.RUNTIME_MATRIX_JIT_COMMIT,
    runtimeImageId: image,
    rid: 'linux-x64',
    architecture: 'x64',
    capabilities: ['run', 'jit-asm'],
    allowedSecurityPolicyIds: ['runtime-job-default'],
    container: {
      isolationKind: 'wine',
      environmentKind: 'wine',
      executionUser: '1654:1654',
      winePrefixPath: '/opt/wine-dotnet',
    },
    operations: {
      run: { implementationId: 'sharplabnext-legacy-jit-inspector-v1' },
      jit: {
        implementationId: 'sharplabnext-legacy-jit-inspector-v1',
        sourceMappingKind: 'none',
      },
    },
    layout: {
      runnerAssemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    },
    securityPolicies: [{ id: 'runtime-job-default' }],
  }
}

function createFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-plan-'))
  const environment = createEnvironment()
  const profile = createProfile(environment)
  const matrix = {
    schemaVersion: 1,
    coreClr: [{
      id: 'dotnet-7',
      version: '7.0.20',
      windows: {
        url: environment.RUNTIME_MATRIX_WINDOWS_URL,
        sha512: environment.RUNTIME_MATRIX_WINDOWS_SHA512,
      },
      wineCapability: {
        executionUser: '1654:1654',
        capabilities: ['run', 'jit-asm'],
      },
    }],
  }
  const policy = {
    schemaVersion: 1,
    id: 'runtime-image-linux-x64-v1',
    image: { maximumSizeBytes: 1024 },
  }
  const profilePath = `profiles/runtimes/candidates/${profileId}.json`
  const performancePolicyPath =
    'profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json'
  for (const [relativePath, value] of [
    [profilePath, profile],
    ['profiles/runtime-matrix.json', matrix],
    [performancePolicyPath, policy],
  ]) {
    const filename = path.join(root, ...relativePath.split('/'))
    fs.mkdirSync(path.dirname(filename), { recursive: true })
    fs.writeFileSync(filename, `${JSON.stringify(value, null, 2)}\n`)
  }
  return {
    root,
    target,
    profileId,
    environment,
    profile,
    profilePath,
    performancePolicyPath,
    outputPath: path.join(root, 'profiles', 'runtime-promotion-plans', `${profileId}.json`),
    preflightProfilePath: path.join(
      root,
      'profiles',
      'runtime-promotion-plans',
      `${profileId}.profile.json`,
    ),
    dispose() {
      fs.rmSync(root, { recursive: true, force: true })
    },
  }
}

function createModernFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-modern-plan-'))
  const modernTarget = 'runtime-dotnet-matrix-candidate'
  const modernProfileId = 'dotnet-10-linux-x64'
  const runtimeVersion = '10.0.10'
  const runtimeSourceUri =
    `https://builds.dotnet.microsoft.com/dotnet/Runtime/${runtimeVersion}/` +
    `dotnet-runtime-${runtimeVersion}-linux-x64.tar.gz`
  const environment = {
    IMAGE_PREFIX: 'registry.example/sharplabnext',
    RELEASE_ID: 'candidate-test',
    SOURCE_DATE_EPOCH: '1784678400',
    SOURCE_REVISION: sourceRevision,
    BASE_DOTNET_SDK_IMAGE: digestReference('sdk', '1'),
    RUNTIME_MATRIX_BASE_IMAGE: digestReference('runtime-deps', '2'),
    RUNTIME_MATRIX_PROFILE_ID: modernProfileId,
    RUNTIME_MATRIX_RUNTIME_VERSION: runtimeVersion,
    RUNTIME_MATRIX_RUNTIME_COMMIT: 'f'.repeat(40),
    RUNTIME_MATRIX_JIT_COMMIT: 'f'.repeat(40),
    RUNTIME_MATRIX_RUNTIME_URL: runtimeSourceUri,
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI: runtimeSourceUri,
    RUNTIME_MATRIX_RUNTIME_SHA512: '4'.repeat(128),
    RUNTIME_MATRIX_PROFILER_PROVIDER_ID: 'sharplabnext-linux-profiler-v1',
    RUNTIME_MATRIX_PROFILER_BUILD_IMAGE: digestReference('profiler-builder', '3'),
    RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT: '5'.repeat(40),
    RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI:
      'https://github.com/microsoft/clr-samples/tree/' + '5'.repeat(40),
    RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT: '6'.repeat(40),
    RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI:
      'https://github.com/dotnet/runtime/tree/' + '6'.repeat(40),
    RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND: 'linux-profiler',
  }
  const image = `${environment.IMAGE_PREFIX}/runtime-${modernProfileId}:candidate`
  const profile = {
    schemaVersion: 1,
    id: modernProfileId,
    image,
    family: 'coreclr',
    acceptedRuntimeFamilies: ['coreclr'],
    acceptedFrameworks: [{ name: 'Microsoft.NETCore.App', exactVersion: runtimeVersion }],
    runtimeVersion,
    runtimeCommit: environment.RUNTIME_MATRIX_RUNTIME_COMMIT,
    jitVersion: runtimeVersion,
    jitCommit: environment.RUNTIME_MATRIX_JIT_COMMIT,
    runtimeImageId: image,
    rid: 'linux-x64',
    architecture: 'x64',
    capabilities: ['run', 'jit-asm'],
    allowedSecurityPolicyIds: ['runtime-job-default'],
    container: {
      isolationKind: 'standard',
      environmentKind: 'coreclr',
      executionUser: '1654:1654',
    },
    operations: {
      run: { implementationId: 'sharplabnext-runner-v1' },
      jit: {
        implementationId: 'sharplabnext-jit-inspector-v1',
        sourceMappingKind: 'linux-profiler',
        profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
      },
    },
    layout: {
      runnerAssemblyPath: '/opt/sharplabnext/SharpLabNext.Runner.dll',
      jitInspectorAssemblyPath: '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
    },
    securityPolicies: [{ id: 'runtime-job-default' }],
  }
  const matrix = {
    schemaVersion: 1,
    coreClr: [{
      id: 'dotnet-10',
      version: runtimeVersion,
      linux: {
        url: runtimeSourceUri,
        sha512: environment.RUNTIME_MATRIX_RUNTIME_SHA512,
      },
      profilerProvider: {
        id: environment.RUNTIME_MATRIX_PROFILER_PROVIDER_ID,
        sourceMappingKind: 'linux-profiler',
      },
      linuxCapability: {
        capabilities: ['run', 'jit-asm', 'inspection', 'execution-flow'],
        promotionState: 'blocked',
      },
    }],
  }
  const policy = {
    schemaVersion: 1,
    id: 'runtime-image-linux-x64-v1',
    image: { maximumSizeBytes: 1024 },
  }
  const profilePath = `profiles/runtimes/candidates/${modernProfileId}.json`
  const performancePolicyPath =
    'profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json'
  for (const [relativePath, value] of [
    [profilePath, profile],
    ['profiles/runtime-matrix.json', matrix],
    [performancePolicyPath, policy],
  ]) {
    const filename = path.join(root, ...relativePath.split('/'))
    fs.mkdirSync(path.dirname(filename), { recursive: true })
    fs.writeFileSync(filename, `${JSON.stringify(value, null, 2)}\n`)
  }
  return {
    root,
    target: modernTarget,
    profileId: modernProfileId,
    environment,
    profile,
    profilePath,
    performancePolicyPath,
    outputPath: path.join(root, 'profiles', 'runtime-promotion-plans', `${modernProfileId}.json`),
    preflightProfilePath: path.join(
      root,
      'profiles',
      'runtime-promotion-plans',
      `${modernProfileId}.profile.json`,
    ),
    dispose() {
      fs.rmSync(root, { recursive: true, force: true })
    },
  }
}

function createInspection(environment, overrides = {}, candidateTarget = target) {
  return {
    imageId,
    sizeBytes: 512,
    operatingSystem: 'linux',
    architecture: 'amd64',
    repoDigests: [pinnedReference],
    labels: candidateExpectedImageLabels(candidateTarget, environment),
    ...overrides,
  }
}

function hashOperations(_imageId, specifications) {
  return Object.fromEntries(Object.entries(specifications).map(([name, value]) => [name, {
    ...value,
    assemblySha256: helperDigest,
    ...(value.profilerPath === undefined ? {} : { profilerSha256: helperDigest }),
  }]))
}

function options(fixture, overrides = {}) {
  return {
    repositoryRoot: fixture.root,
    values: fixture.environment,
    validateCandidateInputs: () => [],
    inspectGit: () => ({ headRevision: sourceRevision, isDirty: false }),
    inspectImage: () => createInspection(fixture.environment, {}, fixture.target),
    hashOperations,
    now: () => new Date('2026-07-22T01:02:03Z'),
    ...overrides,
  }
}

function input(fixture, overrides = {}) {
  return {
    target: fixture.target,
    profilePath: fixture.profilePath,
    pinnedReference,
    performancePolicyPath: fixture.performancePolicyPath,
    ...overrides,
  }
}

test('promotion plan is derived from immutable observations and written only to its canonical path', t => {
  const fixture = createFixture()
  t.after(() => fixture.dispose())
  fs.mkdirSync(path.dirname(fixture.outputPath), { recursive: true })
  fs.writeFileSync(fixture.outputPath, '{"stale":true}\n')

  const result = produceRuntimePromotionPlan(input(fixture), options(fixture))
  assert.equal(result.outputPath, fixture.outputPath)
  assert.match(result.planSha256, /^sha256:[0-9a-f]{64}$/)
  assert.equal(result.plan.image.reference, pinnedReference)
  assert.equal(result.plan.image.imageId, imageId)
  assert.equal(result.plan.image.sizeBytes, 512)
  assert.equal(result.plan.matrixTargetId, 'dotnet-7')
  assert.equal(result.plan.platform, 'wine')
  assert.equal(result.plan.profileSha256, `sha256:${crypto.createHash('sha256')
    .update(fs.readFileSync(path.join(fixture.root, ...fixture.profilePath.split('/'))))
    .digest('hex')}`)
  assert.deepEqual(result.plan.capabilities, ['jit-asm', 'run'])
  assert.equal(
    result.plan.jitLibraryPath,
    '/opt/wine-dotnet/drive_c/dotnet/shared/Microsoft.NETCore.App/7.0.20/clrjit.dll',
  )
  assert.equal(result.plan.operations.run.assemblySha256, helperDigest)
  assert.equal(result.plan.performance.policyId, 'runtime-image-linux-x64-v1')
  assert.equal(result.plan.createdAtUtc, '2026-07-22T01:02:03.000Z')
  assert.deepEqual(JSON.parse(fs.readFileSync(fixture.outputPath, 'utf8')), result.plan)
  const preflightProfile = JSON.parse(fs.readFileSync(fixture.preflightProfilePath, 'utf8'))
  assert.equal(preflightProfile.image, pinnedReference)
  assert.equal(preflightProfile.runtimeImageId, imageId)
  assert.equal(result.plan.preflightProfile.sha256, result.preflightProfileSha256)
})

test('blocked modern Linux plan derives full matrix capabilities only into immutable preflight', t => {
  const fixture = createModernFixture()
  t.after(() => fixture.dispose())

  const result = produceRuntimePromotionPlan(input(fixture), options(fixture))
  const candidateProfile = JSON.parse(fs.readFileSync(
    path.join(fixture.root, ...fixture.profilePath.split('/')),
    'utf8',
  ))
  const preflightProfile = JSON.parse(fs.readFileSync(fixture.preflightProfilePath, 'utf8'))

  assert.deepEqual(candidateProfile.capabilities, ['run', 'jit-asm'])
  assert.deepEqual(
    result.plan.capabilities,
    ['execution-flow', 'inspection', 'jit-asm', 'run'],
  )
  assert.deepEqual(preflightProfile.capabilities, result.plan.capabilities)
  assert.equal(preflightProfile.promotionReceipt, undefined)
  assert.equal(
    result.plan.operations.run.implementation,
    'sharplabnext-runner-v1',
  )
  assert.equal(
    result.plan.operations.jit.implementation,
    'sharplabnext-jit-inspector-v1',
  )
  assert.equal(
    result.plan.operations.jit.profilerPath,
    '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
  )
})

test('instrumentation plan rejects a candidate outside the modern Runner profiler boundary', t => {
  const fixture = createModernFixture()
  t.after(() => fixture.dispose())
  const profilePath = path.join(fixture.root, ...fixture.profilePath.split('/'))
  const profile = JSON.parse(fs.readFileSync(profilePath, 'utf8'))
  profile.operations.run.implementationId = 'sharplabnext-legacy-jit-inspector-v1'
  fs.writeFileSync(profilePath, `${JSON.stringify(profile, null, 2)}\n`)

  assert.throws(
    () => produceRuntimePromotionPlan(input(fixture), options(fixture)),
    /profiler-backed candidate Run implementation|modern Runner/,
  )
  assert.equal(fs.existsSync(fixture.outputPath), false)
})

test('installed plan verification repeats immutable observations without writing files', t => {
  const fixture = createFixture()
  t.after(() => fixture.dispose())
  produceRuntimePromotionPlan(input(fixture), options(fixture))
  const planBytes = fs.readFileSync(fixture.outputPath)
  const profileBytes = fs.readFileSync(fixture.preflightProfilePath)
  const observedAllowedPaths = []

  const result = verifyRuntimePromotionPlan(input(fixture), options(fixture, {
    now: () => { throw new Error('verification must retain the installed timestamp') },
    inspectGit(gitOptions) {
      observedAllowedPaths.push(...(gitOptions.allowedDirtyPaths ?? []))
      return { headRevision: sourceRevision, isDirty: false }
    },
  }))

  assert.equal(result.planSha256, `sha256:${crypto.createHash('sha256').update(planBytes).digest('hex')}`)
  assert.deepEqual(fs.readFileSync(fixture.outputPath), planBytes)
  assert.deepEqual(fs.readFileSync(fixture.preflightProfilePath), profileBytes)
  assert.equal(observedAllowedPaths.includes(
    `profiles/runtime-promotion-evidence/${profileId}/performance.json`,
  ), true)
  assert.equal(observedAllowedPaths.includes(
    `profiles/runtime-promotion-receipts/${profileId}.json`,
  ), true)
})

test('installed plan verification rejects plan, profile, image, helper, and Git drift', t => {
  for (const drift of ['plan', 'profile', 'image', 'helper', 'git']) {
    const fixture = createFixture()
    t.after(() => fixture.dispose())
    produceRuntimePromotionPlan(input(fixture), options(fixture))
    const overrides = {}
    if (drift === 'plan') fs.appendFileSync(fixture.outputPath, ' ')
    if (drift === 'profile') fs.appendFileSync(fixture.preflightProfilePath, ' ')
    if (drift === 'image') {
      overrides.inspectImage = () => createInspection(fixture.environment, { sizeBytes: 513 })
    }
    if (drift === 'helper') {
      overrides.hashOperations = (image, specifications) => {
        const result = hashOperations(image, specifications)
        result.run.assemblySha256 = `sha256:${'9'.repeat(64)}`
        return result
      }
    }
    if (drift === 'git') {
      overrides.inspectGit = () => ({ headRevision: sourceRevision, isDirty: true })
    }
    assert.throws(
      () => verifyRuntimePromotionPlan(input(fixture), options(fixture, overrides)),
      /changed|clean source revision|worktree is dirty/,
      drift,
    )
  }
})

test('two-file plan commit restores both previous documents when the second install fails', t => {
  const fixture = createFixture()
  t.after(() => fixture.dispose())
  fs.mkdirSync(path.dirname(fixture.outputPath), { recursive: true })
  const oldPlan = Buffer.from('{"oldPlan":true}\n')
  const oldProfile = Buffer.from('{"oldProfile":true}\n')
  fs.writeFileSync(fixture.outputPath, oldPlan)
  fs.writeFileSync(fixture.preflightProfilePath, oldProfile)

  assert.throws(
    () => produceRuntimePromotionPlan(input(fixture), options(fixture, {
      beforeStageInstall(index) {
        if (index === 1) throw new Error('injected plan install failure')
      },
    })),
    /injected plan install failure/,
  )
  assert.deepEqual(fs.readFileSync(fixture.outputPath), oldPlan)
  assert.deepEqual(fs.readFileSync(fixture.preflightProfilePath), oldProfile)
})

test('missing registry digest and tag retargeting fail without installing a plan', t => {
  const missing = createFixture()
  t.after(() => missing.dispose())
  assert.throws(() => produceRuntimePromotionPlan(input(missing), options(missing, {
    inspectImage: () => createInspection(missing.environment, { repoDigests: [] }),
  })), /absent from RepoDigests/)
  assert.equal(fs.existsSync(missing.outputPath), false)

  const retargeted = createFixture()
  t.after(() => retargeted.dispose())
  let candidateInspectionCount = 0
  assert.throws(() => produceRuntimePromotionPlan(input(retargeted), options(retargeted, {
    inspectImage(reference) {
      if (reference !== pinnedReference && ++candidateInspectionCount > 1) {
        return createInspection(retargeted.environment, { imageId: otherImageId })
      }
      return createInspection(retargeted.environment)
    },
  })), /resolves to sha256:b{64}.*candidate.*sha256:c{64}|binding changed/s)
  assert.equal(fs.existsSync(retargeted.outputPath), false)
})

test('helper substitution between capture and commit is rejected', t => {
  const fixture = createFixture()
  t.after(() => fixture.dispose())
  let calls = 0
  assert.throws(() => produceRuntimePromotionPlan(input(fixture), options(fixture, {
    hashOperations(image, specifications) {
      const result = hashOperations(image, specifications)
      calls++
      if (calls > 1) result.run.assemblySha256 = `sha256:${'9'.repeat(64)}`
      return result
    },
  })), /helper bytes changed/)
  assert.equal(fs.existsSync(fixture.outputPath), false)
})

test('dirty source cannot produce a plan, including a development candidate override', t => {
  const fixture = createFixture()
  t.after(() => fixture.dispose())
  fixture.environment.ALLOW_UNCOMMITTED_SOURCE_FOR_DEVELOPMENT = 'true'
  assert.throws(() => produceRuntimePromotionPlan(input(fixture), options(fixture, {
    inspectGit: () => ({ headRevision: sourceRevision, isDirty: true }),
  })), /clean source revision|worktree is dirty/)
  assert.equal(fs.existsSync(fixture.outputPath), false)
})

test('image size and required image labels are fail-closed', t => {
  const oversized = createFixture()
  t.after(() => oversized.dispose())
  assert.throws(() => produceRuntimePromotionPlan(input(oversized), options(oversized, {
    inspectImage: () => createInspection(oversized.environment, { sizeBytes: 1025 }),
  })), /exceeds policy/)
  assert.equal(fs.existsSync(oversized.outputPath), false)

  const relabelled = createFixture()
  t.after(() => relabelled.dispose())
  assert.throws(() => produceRuntimePromotionPlan(input(relabelled), options(relabelled, {
    inspectImage: () => createInspection(relabelled.environment, {
      labels: {
        ...candidateExpectedImageLabels(target, relabelled.environment),
        'io.sharplabnext.runtime.commit': '0'.repeat(40),
      },
    }),
  })), /runtime\.commit/)
  assert.equal(fs.existsSync(relabelled.outputPath), false)
})

test('image Size and label drift between capture and commit are rejected', t => {
  for (const drift of ['size', 'label']) {
    const fixture = createFixture()
    t.after(() => fixture.dispose())
    let inspections = 0
    assert.throws(() => produceRuntimePromotionPlan(input(fixture), options(fixture, {
      inspectImage() {
        inspections++
        if (inspections <= 2) return createInspection(fixture.environment)
        if (drift === 'size') {
          return createInspection(fixture.environment, { sizeBytes: 513 })
        }
        return createInspection(fixture.environment, {
          labels: {
            ...candidateExpectedImageLabels(target, fixture.environment),
            'org.opencontainers.image.version': 'retargeted',
          },
        })
      },
    })), drift === 'size' ? /binding changed/ : /image\.version/, drift)
    assert.equal(fs.existsSync(fixture.outputPath), false, drift)
  }
})

test('profile, matrix and policy byte drift roll back the staged plan', t => {
  for (const drift of ['profile', 'matrix', 'policy']) {
    const fixture = createFixture()
    t.after(() => fixture.dispose())
    fs.mkdirSync(path.dirname(fixture.outputPath), { recursive: true })
    const previousPlan = Buffer.from('{"previous":true}\n')
    fs.writeFileSync(fixture.outputPath, previousPlan)
    const filename = drift === 'profile'
      ? path.join(fixture.root, ...fixture.profilePath.split('/'))
      : drift === 'matrix'
        ? path.join(fixture.root, 'profiles', 'runtime-matrix.json')
        : path.join(fixture.root, ...fixture.performancePolicyPath.split('/'))
    assert.throws(() => produceRuntimePromotionPlan(input(fixture), options(fixture, {
      beforeRecheck() {
        fs.appendFileSync(filename, ' ')
      },
    })), /changed before the promotion plan commit/, drift)
    assert.deepEqual(fs.readFileSync(fixture.outputPath), previousPlan, drift)
  }
})

test('only canonical candidate profile and performance policy paths are accepted', t => {
  const fixture = createFixture()
  t.after(() => fixture.dispose())
  for (const [field, value] of [
    ['profilePath', `profiles/runtimes/candidates/../candidates/${profileId}.json`],
    ['performancePolicyPath', `profiles/runtime-performance-policies/../` +
      'runtime-performance-policies/runtime-image-linux-x64-v1.json'],
  ]) {
    assert.throws(() => produceRuntimePromotionPlan(
      input(fixture, { [field]: value }),
      options(fixture),
    ), error => {
      assert.equal(error instanceof RuntimePromotionPlanError, true)
      assert.match(error.message, /canonical path/)
      return true
    })
  }
})

test('CLI help and invalid invocation never inspect Docker or write a plan', () => {
  const output = {
    logs: [],
    errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  assert.equal(runRuntimePromotionPlan(['--help'], { output }), 0)
  assert.match(output.logs.join('\n'), /runtime-promotion-plan\.mjs/)
  assert.equal(runRuntimePromotionPlan([], { output }), 1)
  assert.match(output.errors.join('\n'), /candidate target is required/)
})

test('CLI check verifies the installed plan and rejects duplicate flags', t => {
  const fixture = createFixture()
  t.after(() => fixture.dispose())
  produceRuntimePromotionPlan(input(fixture), options(fixture))
  const output = {
    logs: [],
    errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  const arguments_ = [
    target,
    '--profile', fixture.profilePath,
    '--pinned-reference', pinnedReference,
    '--performance-policy', fixture.performancePolicyPath,
    '--check',
  ]
  assert.equal(runRuntimePromotionPlan(arguments_, { ...options(fixture), output }), 0)
  assert.match(output.logs.join('\n'), /Verified .*no files were written/)
  assert.equal(runRuntimePromotionPlan([...arguments_, '--check'], {
    ...options(fixture),
    output,
  }), 1)
  assert.match(output.errors.join('\n'), /duplicate.*--check/)
})
