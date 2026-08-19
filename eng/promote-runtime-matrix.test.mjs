import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import {
  prepareRuntimeMatrixPromotion,
  promoteRuntimeMatrix,
  replaceFilesAtomically,
  RuntimeMatrixPromotionError,
} from './promote-runtime-matrix.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const sourceRevision = 'd'.repeat(40)

test('promotion stages one closed runtime material set without mutating active inputs', t => {
  const fixture = createFixture(t)
  const before = snapshotActiveInputs(fixture.root, fixture.profileId)
  const plan = prepareRuntimeMatrixPromotion({
    repositoryRoot: fixture.root,
    profileId: fixture.profileId,
    sourceRevision,
    generatorRunner: fakeGenerator,
  })
  t.after(() => fs.rmSync(plan.stageRoot, { recursive: true, force: true }))

  assert.deepEqual(snapshotActiveInputs(fixture.root, fixture.profileId), before)
  assert.deepEqual(
    plan.replacements.map(replacement =>
      path.relative(fixture.root, replacement.path).replaceAll('\\', '/')),
    [
      'deploy/images.json',
      `profiles/runtimes/${fixture.profileId}.json`,
      'profiles/catalog/catalog.json',
      'profiles/lock.json',
      'profiles/runtime-matrix.json',
    ],
  )

  const material = Object.fromEntries(plan.replacements.map(replacement => [
    path.relative(fixture.root, replacement.path).replaceAll('\\', '/'),
    JSON.parse(replacement.content.toString('utf8')),
  ]))
  const matrixTarget = material['profiles/runtime-matrix.json'].coreClr.find(
    target => target.id === fixture.targetId,
  )
  assert.equal(matrixTarget.linuxCapability.promotionState, 'verified')
  assert.deepEqual(matrixTarget.linuxCapability.promotionReceipt, fixture.receiptReference)

  const profile = material[`profiles/runtimes/${fixture.profileId}.json`]
  assert.deepEqual(profile.promotionReceipt, fixture.receiptReference)
  assert.equal(profile.image, fixture.receipt.image.reference)
  assert.equal(profile.runtimeImageId, fixture.receipt.image.imageId)

  const deployment = material['deploy/images.json'].images.find(
    image => image.runtimeId === fixture.profileId,
  )
  assert.equal(deployment.immutableReference, fixture.receipt.image.reference)
  assert.equal(deployment.repository, 'registry.example/sharplabnext/runtime-core21')
  assert.equal('imageId' in deployment, false)

  const runtimeLock = material['profiles/lock.json'].components[fixture.profileId]
  assert.equal(runtimeLock.resolvedVersion, fixture.target.version)
  assert.equal(runtimeLock.commit, fixture.target.runtimeCommit)
  assert.equal(runtimeLock.jitCommit, fixture.target.jitCommit)
  assert.equal('imageId' in runtimeLock, false)

  replaceFilesAtomically(plan.replacements)
  assert.equal(
    JSON.parse(fs.readFileSync(
      path.join(fixture.root, 'profiles', 'runtime-matrix.json'),
      'utf8',
    )).coreClr.find(target => target.id === fixture.targetId).linuxCapability.promotionState,
    'verified',
  )
})

test('atomic replace restores every original file when an apply step fails', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-promotion-atomic-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const first = path.join(root, 'first.json')
  const second = path.join(root, 'second.json')
  const added = path.join(root, 'added.json')
  fs.writeFileSync(first, 'first-before\n')
  fs.writeFileSync(second, 'second-before\n')

  assert.throws(
    () => replaceFilesAtomically(
      [
        { path: added, content: Buffer.from('added-after\n') },
        { path: first, content: Buffer.from('first-after\n') },
        { path: second, content: Buffer.from('second-after\n') },
      ],
      {
        faultInjector(phase, index) {
          if (phase === 'after-backup' && index === 2) throw new Error('injected failure')
        },
      },
    ),
    /injected failure/,
  )
  assert.equal(fs.readFileSync(first, 'utf8'), 'first-before\n')
  assert.equal(fs.readFileSync(second, 'utf8'), 'second-before\n')
  assert.equal(fs.existsSync(added), false)
  assert.deepEqual(fs.readdirSync(root).sort(), ['first.json', 'second.json'])
})

test('source revision mismatch fails before generator execution or active writes', t => {
  const fixture = createFixture(t)
  const before = snapshotActiveInputs(fixture.root, fixture.profileId)
  let generatorCalled = false
  assert.throws(
    () => prepareRuntimeMatrixPromotion({
      repositoryRoot: fixture.root,
      profileId: fixture.profileId,
      sourceRevision: 'e'.repeat(40),
      generatorRunner() {
        generatorCalled = true
      },
    }),
    error => error instanceof RuntimeMatrixPromotionError && /sourceRevision/.test(error.message),
  )
  assert.equal(generatorCalled, false)
  assert.deepEqual(snapshotActiveInputs(fixture.root, fixture.profileId), before)
})

test('canonical generated trust material is promoted while Git HEAD remains the build revision', t => {
  const fixture = createFixture(t)
  initializeFixtureRepository(fixture)
  const buildRevision = runGit(fixture.root, ['rev-parse', 'HEAD']).trim()

  const result = promoteRuntimeMatrix({
    repositoryRoot: fixture.root,
    profileId: fixture.profileId,
    generatorRunner: fakeGenerator,
  })

  assert.equal(result.sourceRevision, buildRevision)
  assert.equal(runGit(fixture.root, ['rev-parse', 'HEAD']).trim(), buildRevision)
  const matrix = JSON.parse(fs.readFileSync(
    path.join(fixture.root, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  assert.equal(
    matrix.coreClr.find(target => target.id === fixture.targetId)
      .linuxCapability.promotionState,
    'verified',
  )
  const changed = runGit(
    fixture.root,
    ['status', '--porcelain=v1', '--untracked-files=all'],
  )
  assert.match(changed, /profiles\/runtime-promotion-plans\//)
  assert.match(changed, /profiles\/runtime-promotion-evidence\//)
  assert.match(changed, /profiles\/runtime-promotion-receipts\//)
  assert.doesNotMatch(changed, /tracked-sentinel/)
})

test('dirty tracked or untracked content prevents generator execution and active writes', async t => {
  for (const dirtyKind of ['tracked', 'untracked']) {
    await t.test(dirtyKind, t => {
      const fixture = createFixture(t)
      initializeFixtureRepository(fixture)
      const before = snapshotActiveInputs(fixture.root, fixture.profileId)
      if (dirtyKind === 'tracked') {
        fs.appendFileSync(path.join(fixture.root, 'tracked-sentinel.txt'), 'dirty\n')
      } else {
        fs.writeFileSync(path.join(fixture.root, 'untracked-sentinel.txt'), 'dirty\n')
      }

      let generatorCalled = false
      assert.throws(
        () => promoteRuntimeMatrix({
          repositoryRoot: fixture.root,
          profileId: fixture.profileId,
          generatorRunner() {
            generatorCalled = true
          },
        }),
        error => error instanceof RuntimeMatrixPromotionError &&
          /outside the exact verified runtime promotion transaction/.test(error.message),
      )
      assert.equal(generatorCalled, false)
      assert.deepEqual(snapshotActiveInputs(fixture.root, fixture.profileId), before)
    })
  }
})

test('worktree drift during staged generation prevents active writes', t => {
  const fixture = createFixture(t)
  initializeFixtureRepository(fixture)
  const before = snapshotActiveInputs(fixture.root, fixture.profileId)
  let generatorCalled = false

  assert.throws(
    () => promoteRuntimeMatrix({
      repositoryRoot: fixture.root,
      profileId: fixture.profileId,
      generatorRunner(options) {
        generatorCalled = true
        fakeGenerator(options)
        fs.writeFileSync(path.join(fixture.root, 'generator-drift.txt'), 'dirty\n')
      },
    }),
    error => error instanceof RuntimeMatrixPromotionError &&
      /outside the exact verified runtime promotion transaction/.test(error.message),
  )
  assert.equal(generatorCalled, true)
  assert.deepEqual(snapshotActiveInputs(fixture.root, fixture.profileId), before)
})

function createFixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-runtime-promotion-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  copyFile('profiles/runtime-matrix.json', root)
  copyFile('profiles/catalog/catalog.json', root)
  copyFile('profiles/lock.json', root)
  copyFile('deploy/images.json', root)
  copyFile('eng/runtime-promotion-receipt-validation.mjs', root)
  copyFile('eng/runtime-performance-evidence-validation.mjs', root)
  copyFile('eng/runtime-capability-evidence-validation.mjs', root)
  copyFile('eng/strict-owned-json.mjs', root)
  copyFile('eng/runtime-candidate-input-validation.mjs', root)
  const sourceProfiles = path.join(repositoryRoot, 'profiles', 'runtimes')
  const targetProfiles = path.join(root, 'profiles', 'runtimes')
  fs.mkdirSync(targetProfiles, { recursive: true })
  for (const entry of fs.readdirSync(sourceProfiles, { withFileTypes: true })) {
    if (entry.isFile() && entry.name.endsWith('.json')) {
      fs.copyFileSync(path.join(sourceProfiles, entry.name), path.join(targetProfiles, entry.name))
    }
  }

  const matrix = JSON.parse(fs.readFileSync(path.join(root, 'profiles', 'runtime-matrix.json'), 'utf8'))
  const target = matrix.coreClr.find(candidate => candidate.id === 'dotnet-core-2.1')
  assert.ok(target)
  const profileId = `${target.id}-linux-x64`
  const candidateProfileRelativePath = `profiles/runtimes/candidates/${profileId}.json`
  copyFile(candidateProfileRelativePath, root)
  const candidateProfile = JSON.parse(fs.readFileSync(
    path.join(root, ...candidateProfileRelativePath.split('/')),
    'utf8',
  ))
  const receipt = {
    schemaVersion: 2,
    profileId,
    matrixTargetId: target.id,
    platform: 'linux',
    family: 'coreclr',
    resolvedVersion: target.version,
    image: {
      reference: `registry.example/sharplabnext/runtime-core21@sha256:${'a'.repeat(64)}`,
      imageId: `sha256:${'b'.repeat(64)}`,
      sizeBytes: 536870912,
    },
    componentIdentity: {
      sourceUri: target.linux.url,
      sourceDigest: `sha512:${target.linux.sha512}`,
    },
    runtimeIdentity: {
      runtimeCommit: target.runtimeCommit,
      jitVersion: target.version,
      jitCommit: target.jitCommit,
    },
    operations: {
      run: {
        implementation: 'sharplabnext-legacy-jit-inspector-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
        assemblySha256: `sha256:${'c'.repeat(64)}`,
      },
    },
    sourceRevision,
    planSha256: `sha256:${'0'.repeat(64)}`,
    checks: [
      {
        capability: 'run',
        result: 'passed',
        networkDisabled: true,
        supervisorSandbox: true,
        outputLimitValidated: true,
        sourceMappingKind: 'not-applicable',
        mappingSource: 'not-applicable',
        evidencePath: `profiles/runtime-promotion-evidence/${profileId}/run.json`,
        evidenceSha256: `sha256:${'e'.repeat(64)}`,
      },
    ],
  }
  const evidencePath = path.join(
    root,
    'profiles',
    'runtime-promotion-evidence',
    profileId,
    'run.json',
  )
  fs.mkdirSync(path.dirname(evidencePath), { recursive: true })
  const profileOperation = candidateProfile.operations.run
  const entryAssembly = {
    path: '/workspace/app.dll',
    sha256: `sha256:${'7'.repeat(64)}`,
  }
  const command = [profileOperation.command.executable]
  for (const token of profileOperation.command.argv) {
    if (token === '{entryAssembly}') command.push(entryAssembly.path)
    else if (token !== '{arguments}') command.push(token)
  }
  const policy = candidateProfile.securityPolicies[0]
  const lifecycleProbe = terminalStatus => ({
    result: 'passed',
    terminalStatus,
    containerRemoved: true,
    processTreeRemoved: true,
  })
  const evidence = {
    schemaVersion: 1,
    profileId,
    capability: 'run',
    image: {
      reference: receipt.image.reference,
      imageId: receipt.image.imageId,
    },
    sourceRevision,
    completedAtUtc: '2026-07-22T00:00:00Z',
    result: 'passed',
    producer: {
      id: 'sharplabnext-runtime-preflight-v1',
      sourceRevision,
      planSha256: receipt.planSha256,
    },
    artifacts: [
      {
        role: 'helper',
        path: receipt.operations.run.assemblyPath,
        sha256: receipt.operations.run.assemblySha256,
        sizeBytes: 1048576,
        format: 'managed-pe',
        architecture: 'anycpu',
      },
      {
        role: 'runtime-host',
        path: profileOperation.command.executable,
        sha256: `sha256:${'6'.repeat(64)}`,
        sizeBytes: 1052672,
        format: 'elf',
        architecture: 'x64',
      },
    ],
    invocation: {
      implementation: receipt.operations.run.implementation,
      command,
      entryAssembly,
      outcome: 'succeeded',
      exitCode: 0,
      runtimeFrameCount: 3,
      terminalFrameKind: 'Exit',
      terminalStatus: 'completed',
      stdoutBytes: 32,
      stderrBytes: 16,
    },
    sandbox: {
      supervisorPolicyId: 'runtime-supervisor-v1',
      securityPolicyId: policy.id,
      seccompSha256: `sha256:${'8'.repeat(64)}`,
      containerId: '9'.repeat(64),
      networkMode: 'none',
      networkProbeBlocked: true,
      readOnlyRootFilesystem: true,
      readOnlyProbeBlocked: true,
      capDrop: ['ALL'],
      noNewPrivileges: true,
      user: '1654:1654',
      nanoCpus: policy.nanoCpus,
      memoryBytes: policy.memoryBytes,
      pidsLimit: policy.pidsLimit,
      deadlineMilliseconds: policy.maximumDurationSeconds * 1000,
      outputLimitBytes: policy.maximumOutputBytes,
      tmpfsBytes: policy.tmpfsBytes,
    },
    lifecycle: {
      outputOverflow: lifecycleProbe('output-limit-exceeded'),
      timeout: lifecycleProbe('timeout'),
      cancellation: lifecycleProbe('cancelled'),
      processTreeCleanup: lifecycleProbe('completed'),
    },
    run: {
      expectedStdoutMarker: 'runtime-preflight-stdout',
      observedStdoutMarker: 'runtime-preflight-stdout',
      expectedStderrMarker: 'runtime-preflight-stderr',
      observedStderrMarker: 'runtime-preflight-stderr',
      exceptionFrameValidated: true,
    },
  }
  fs.writeFileSync(evidencePath, `${JSON.stringify(evidence, null, 2)}\n`)
  receipt.checks[0].evidenceSha256 = digest(fs.readFileSync(evidencePath))

  const performancePolicyRelativePath =
    'profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json'
  copyFile(performancePolicyRelativePath, root)
  const performancePolicyBytes = fs.readFileSync(
    path.join(root, ...performancePolicyRelativePath.split('/')),
  )
  const performancePolicyDigest = digest(performancePolicyBytes)
  const performanceEvidenceRelativePath =
    `profiles/runtime-promotion-evidence/${profileId}/performance.json`
  const performanceEvidencePath = path.join(root, ...performanceEvidenceRelativePath.split('/'))
  const performanceEvidence = {
    schemaVersion: 1,
    profileId,
    planSha256: receipt.planSha256,
    image: { ...receipt.image },
    sourceRevision,
    policy: {
      id: 'runtime-image-linux-x64-v1',
      sha256: performancePolicyDigest,
    },
    capabilities: ['run'],
    sourceMappingKind: 'not-applicable',
    environment: {
      runnerId: 'runtime-preflight-linux-x64-v1',
      operatingSystem: 'linux',
      architecture: 'x64',
      nanoCpus: 1000000000,
      memoryLimitBytes: 268435456,
    },
    completedAtUtc: '2026-07-22T00:00:00Z',
    result: 'passed',
    scenarios: { run: performanceScenario() },
  }
  const performanceEvidenceBytes = Buffer.from(`${JSON.stringify(performanceEvidence, null, 2)}\n`)
  fs.writeFileSync(performanceEvidencePath, performanceEvidenceBytes)
  receipt.performance = {
    result: 'passed',
    policyId: 'runtime-image-linux-x64-v1',
    policyPath: performancePolicyRelativePath,
    policySha256: performancePolicyDigest,
    evidencePath: performanceEvidenceRelativePath,
    evidenceSha256: digest(performanceEvidenceBytes),
  }

  const receiptPath = path.join(
    root,
    'profiles',
    'runtime-promotion-receipts',
    `${profileId}.json`,
  )
  fs.mkdirSync(path.dirname(receiptPath), { recursive: true })
  const receiptBytes = Buffer.from(`${JSON.stringify(receipt, null, 2)}\n`)
  fs.writeFileSync(receiptPath, receiptBytes)
  const receiptReference = {
    path: `profiles/runtime-promotion-receipts/${profileId}.json`,
    sha256: digest(receiptBytes),
  }
  return { root, profileId, targetId: target.id, target, receipt, receiptReference }

  function copyFile(relativePath, destinationRoot) {
    const destination = path.join(destinationRoot, ...relativePath.split('/'))
    fs.mkdirSync(path.dirname(destination), { recursive: true })
    fs.copyFileSync(path.join(repositoryRoot, ...relativePath.split('/')), destination)
  }
}

let performanceSampleSequence = 0

function performanceScenario() {
  const sample = latencyMilliseconds => ({
    latencyMilliseconds,
    peakMemoryBytes: 134217728,
    operationId: `op_${(++performanceSampleSequence).toString(16).padStart(32, '0')}`,
    resourceSampleCount: 1,
    completedAtUtc: '2026-07-22T00:00:00.0000000Z',
  })
  return {
    cold: Array.from({ length: 3 }, () => sample(100)),
    warm: Array.from({ length: 10 }, () => sample(50)),
  }
}

function initializeFixtureRepository(fixture) {
  fs.writeFileSync(
    path.join(fixture.root, '.gitignore'),
    [
      'artifacts/',
      '',
    ].join('\n'),
  )
  fs.writeFileSync(path.join(fixture.root, 'tracked-sentinel.txt'), 'clean\n')
  runGit(fixture.root, ['init', '--quiet'])
  runGit(fixture.root, ['config', 'user.email', 'runtime-promotion-tests@example.invalid'])
  runGit(fixture.root, ['config', 'user.name', 'Runtime Promotion Tests'])
  runGit(fixture.root, [
    'add', '--all', '--', '.',
    ':(exclude)profiles/runtime-promotion-evidence/**',
    ':(exclude)profiles/runtime-promotion-receipts/**',
    ':(exclude)profiles/runtime-promotion-plans/**',
  ])
  runGit(fixture.root, ['commit', '--quiet', '-m', 'fixture'])

  fixture.receipt.sourceRevision = runGit(fixture.root, ['rev-parse', 'HEAD']).trim()
  const candidateProfilePath = path.join(
    fixture.root,
    'profiles',
    'runtimes',
    'candidates',
    `${fixture.profileId}.json`,
  )
  const candidateProfileBytes = fs.readFileSync(candidateProfilePath)
  const candidateProfile = JSON.parse(candidateProfileBytes)
  const preflightProfile = structuredClone(candidateProfile)
  preflightProfile.image = fixture.receipt.image.reference
  preflightProfile.runtimeImageId = fixture.receipt.image.imageId
  delete preflightProfile.promotionReceipt
  const planDirectory = path.join(fixture.root, 'profiles', 'runtime-promotion-plans')
  fs.mkdirSync(planDirectory, { recursive: true })
  const preflightProfilePath = path.join(
    planDirectory,
    `${fixture.profileId}.profile.json`,
  )
  const preflightProfileBytes = Buffer.from(`${JSON.stringify(preflightProfile, null, 2)}\n`)
  fs.writeFileSync(preflightProfilePath, preflightProfileBytes)
  const plan = {
    schemaVersion: 1,
    profileId: fixture.profileId,
    profileSha256: digest(candidateProfileBytes),
    sourceRevision: fixture.receipt.sourceRevision,
    image: structuredClone(fixture.receipt.image),
    componentIdentity: structuredClone(fixture.receipt.componentIdentity),
    runtimeIdentity: structuredClone(fixture.receipt.runtimeIdentity),
    preflightProfile: {
      path: `profiles/runtime-promotion-plans/${fixture.profileId}.profile.json`,
      sha256: digest(preflightProfileBytes),
    },
  }
  const planBytes = Buffer.from(`${JSON.stringify(plan, null, 2)}\n`)
  fs.writeFileSync(path.join(planDirectory, `${fixture.profileId}.json`), planBytes)
  fixture.receipt.planSha256 = digest(planBytes)
  const capabilityEvidencePath = path.join(
    fixture.root,
    'profiles',
    'runtime-promotion-evidence',
    fixture.profileId,
    'run.json',
  )
  const capabilityEvidence = JSON.parse(fs.readFileSync(capabilityEvidencePath, 'utf8'))
  capabilityEvidence.sourceRevision = fixture.receipt.sourceRevision
  capabilityEvidence.producer.sourceRevision = fixture.receipt.sourceRevision
  capabilityEvidence.producer.planSha256 = fixture.receipt.planSha256
  const capabilityEvidenceBytes = Buffer.from(
    `${JSON.stringify(capabilityEvidence, null, 2)}\n`,
  )
  fs.writeFileSync(capabilityEvidencePath, capabilityEvidenceBytes)
  fixture.receipt.checks[0].evidenceSha256 = digest(capabilityEvidenceBytes)
  const performanceEvidencePath = path.join(
    fixture.root,
    'profiles',
    'runtime-promotion-evidence',
    fixture.profileId,
    'performance.json',
  )
  const performanceEvidence = JSON.parse(fs.readFileSync(performanceEvidencePath, 'utf8'))
  performanceEvidence.sourceRevision = fixture.receipt.sourceRevision
  performanceEvidence.planSha256 = fixture.receipt.planSha256
  const performanceEvidenceBytes = Buffer.from(
    `${JSON.stringify(performanceEvidence, null, 2)}\n`,
  )
  fs.writeFileSync(performanceEvidencePath, performanceEvidenceBytes)
  fixture.receipt.performance.evidenceSha256 = digest(performanceEvidenceBytes)
  const receiptPath = path.join(
    fixture.root,
    'profiles',
    'runtime-promotion-receipts',
    `${fixture.profileId}.json`,
  )
  const receiptBytes = Buffer.from(`${JSON.stringify(fixture.receipt, null, 2)}\n`)
  fs.writeFileSync(receiptPath, receiptBytes)
  fixture.receiptReference.sha256 = digest(receiptBytes)
}

function runGit(root, args) {
  const result = spawnSync('git', ['-C', root, ...args], {
    encoding: 'utf8',
    timeout: 10_000,
    windowsHide: true,
  })
  assert.equal(
    result.status,
    0,
    `git ${args.join(' ')} failed: ${result.stderr || result.error?.message || '<no error>'}`,
  )
  return result.stdout
}

function fakeGenerator({ repositoryRoot: fixtureRoot, stageRoot }) {
  const matrixPath = path.join(stageRoot, 'profiles', 'runtime-matrix.json')
  const catalogPath = path.join(stageRoot, 'profiles', 'catalog', 'catalog.json')
  const matrix = JSON.parse(fs.readFileSync(matrixPath, 'utf8'))
  const target = matrix.coreClr.find(candidate => candidate.id === 'dotnet-core-2.1')
  const profileId = `${target.id}-linux-x64`
  const receiptReference = target.linuxCapability.promotionReceipt
  const receipt = JSON.parse(fs.readFileSync(
    path.join(stageRoot, ...receiptReference.path.split('/')),
    'utf8',
  ))
  const catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'))
  const runtime = catalog.runtimes.find(candidate => candidate.id === profileId)
  Object.assign(runtime, {
    resolvedVersion: target.version,
    runtimeCommit: target.runtimeCommit,
    jitVersion: target.version,
    jitCommit: target.jitCommit,
    runtimeImageId: receipt.image.imageId,
    capabilities: ['run'],
    availability: { installed: true, health: 'healthy' },
  })
  const reference = catalog.referenceSets.find(candidate => candidate.id === target.referenceSetId)
  Object.assign(reference, {
    displayName: target.version,
    digest: target.referencePackage.packageContentHash,
    availability: { installed: true, health: 'healthy' },
  })
  for (const rule of catalog.compatibility) {
    if (rule.toId === profileId || rule.toId === target.referenceSetId) {
      rule.allowed = true
      delete rule.reason
    }
  }
  for (const preset of catalog.presets) {
    if (preset.defaultRuntimeId === profileId) {
      preset.availability = { installed: true, health: 'healthy' }
    }
  }
  fs.writeFileSync(catalogPath, `${JSON.stringify(catalog, null, 2)}\n`)

  const profile = JSON.parse(fs.readFileSync(
    path.join(
      fixtureRoot,
      'profiles',
      'runtimes',
      'candidates',
      `${profileId}.json`,
    ),
    'utf8',
  ))
  Object.assign(profile, {
    image: receipt.image.reference,
    runtimeVersion: target.version,
    runtimeCommit: target.runtimeCommit,
    jitVersion: target.version,
    jitCommit: target.jitCommit,
    runtimeImageId: receipt.image.imageId,
    capabilities: ['run'],
    promotionReceipt: receiptReference,
  })
  const profilePath = path.join(stageRoot, 'profiles', 'runtimes', `${profileId}.json`)
  fs.writeFileSync(profilePath, `${JSON.stringify(profile, null, 2)}\n`)
}

function snapshotActiveInputs(root, profileId) {
  const relativePaths = [
    'profiles/runtime-matrix.json',
    'profiles/catalog/catalog.json',
    'profiles/lock.json',
    'deploy/images.json',
    `profiles/runtimes/${profileId}.json`,
  ]
  return Object.fromEntries(relativePaths.map(relativePath => {
    const filePath = path.join(root, ...relativePath.split('/'))
    return [relativePath, fs.existsSync(filePath) ? fs.readFileSync(filePath, 'hex') : null]
  }))
}

function digest(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}
