import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import {
  deploymentLockComponentIds,
  findRuntimeMatrixBinding,
  prepareRuntimeMatrixPromotion as prepareRuntimeMatrixPromotionImpl,
  promoteRuntimeMatrix as promoteRuntimeMatrixImpl,
  replaceFilesAtomically,
  RuntimeMatrixPromotionError,
} from './promote-runtime-matrix.mjs'
import { validateRuntimePromotionReceipts } from './runtime-promotion-receipt-validation.mjs'
import {
  runtimePromotionPlanSignaturePath,
  serializeRuntimePromotionPlan,
  signRuntimePromotionPlan,
} from './runtime-promotion-plan-signature.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const sourceRevision = 'd'.repeat(40)
const planKeys = crypto.generateKeyPairSync('ed25519')
const planKeyId = `sha256:${crypto.createHash('sha256').update(
  planKeys.publicKey.export({ type: 'spki', format: 'der' }),
).digest('hex')}`
const planSignatureOptions = Object.freeze({
  planSignaturePublicKey: planKeys.publicKey,
  planSignatureKeyId: planKeyId,
})

function prepareRuntimeMatrixPromotion(options) {
  return prepareRuntimeMatrixPromotionImpl({ ...planSignatureOptions, ...options })
}

function promoteRuntimeMatrix(options) {
  return promoteRuntimeMatrixImpl({ ...planSignatureOptions, ...options })
}

test('Wine deployment definitions retain userspace component closure for all 21 final Wine rows', () => {
  const matrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  const wineCoreClrIds = matrix.coreClr
    .filter(target => Number.parseInt(target.channel, 10) >= 5)
    .map(target => `wine-${target.id}-linux-x64`)
  const wineFrameworkIds = matrix.framework.targets
    .map(target => `wine-${target.id}-linux-x64`)
  const allWineIds = [...wineCoreClrIds, ...wineFrameworkIds]

  assert.deepEqual(wineCoreClrIds, [
    'wine-dotnet-5-linux-x64',
    'wine-dotnet-6-linux-x64',
    'wine-dotnet-7-linux-x64',
    'wine-dotnet-8-linux-x64',
    'wine-dotnet-9-linux-x64',
    'wine-dotnet-10-linux-x64',
    'wine-dotnet-11-preview-linux-x64',
  ])
  assert.equal(wineFrameworkIds.length, 14)
  assert.equal(allWineIds.length, 21)

  for (const profileId of allWineIds) {
    const binding = findRuntimeMatrixBinding(matrix, profileId)
    assert.deepEqual(
      deploymentLockComponentIds(binding, ['existing-component']),
      ['existing-component', 'wine-coreclr-userspace'],
      profileId,
    )
  }
  for (const profileId of [
    'dotnet-10-linux-x64',
    'dotnet-core-3.1-linux-x64',
    'mono-6.12-linux-x64',
  ]) {
    assert.deepEqual(
      deploymentLockComponentIds(findRuntimeMatrixBinding(matrix, profileId), ['existing-component']),
      ['existing-component'],
      profileId,
    )
  }
})

test('promotion stages one closed runtime material set without mutating active inputs', t => {
  const fixture = createFixture(t)
  const unboundEvidenceRelativePath =
    `profiles/runtime-promotion-evidence/${fixture.profileId}/unbound.json`
  fs.writeFileSync(
    path.join(fixture.root, ...unboundEvidenceRelativePath.split('/')),
    '{}\n',
  )
  const before = snapshotActiveInputs(fixture.root, fixture.profileId)
  const plan = prepareRuntimeMatrixPromotion({
    repositoryRoot: fixture.root,
    profileId: fixture.profileId,
    sourceRevision,
    generatorRunner(options) {
      const stagedCandidateProfile = path.join(
        options.stageRoot,
        'profiles',
        'runtimes',
        'candidates',
        `${fixture.profileId}.json`,
      )
      assert.deepEqual(
        fs.readFileSync(stagedCandidateProfile),
        fs.readFileSync(path.join(
          fixture.root,
          'profiles',
          'runtimes',
          'candidates',
          `${fixture.profileId}.json`,
        )),
      )
      assert.equal(
        fs.existsSync(path.join(options.stageRoot, ...unboundEvidenceRelativePath.split('/'))),
        false,
      )
      fakeGenerator(options)
    },
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

test('promotion preserves a bounded registry authority port', t => {
  const repositoryPattern = new RegExp(JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'schemas', 'deployment-images.schema.json'),
    'utf8',
  )).$defs.image.properties.repository.pattern)
  assert.equal(repositoryPattern.test('localhost:5000/sharplabnext/runtime-core21'), true)
  assert.equal(repositoryPattern.test('localhost:70000/sharplabnext/runtime-core21'), false)

  const fixture = createFixture(t, {
    imageReference:
      `localhost:5000/sharplabnext/runtime-core21@sha256:${'a'.repeat(64)}`,
  })
  const plan = prepareRuntimeMatrixPromotion({
    repositoryRoot: fixture.root,
    profileId: fixture.profileId,
    sourceRevision,
    generatorRunner: fakeGenerator,
  })
  t.after(() => fs.rmSync(plan.stageRoot, { recursive: true, force: true }))
  const deployment = JSON.parse(plan.replacements.find(replacement =>
    path.relative(fixture.root, replacement.path).replaceAll('\\', '/') ===
      'deploy/images.json').content)
  assert.equal(
    deployment.images.find(image => image.runtimeId === fixture.profileId).repository,
    'localhost:5000/sharplabnext/runtime-core21',
  )

  const invalid = createFixture(t, {
    imageReference:
      `localhost:70000/sharplabnext/runtime-core21@sha256:${'a'.repeat(64)}`,
  })
  assert.throws(
    () => prepareRuntimeMatrixPromotion({
      repositoryRoot: invalid.root,
      profileId: invalid.profileId,
      sourceRevision,
      generatorRunner: fakeGenerator,
    }),
    /cannot be represented by deploy\/images\.json/,
  )
})

test('promotion materializes verified instrumentation through the real generator', t => {
  const fixture = createFixture(t, { targetId: 'dotnet-10' })
  addInstrumentationCapabilities(fixture, ['inspection', 'execution-flow'])
  initializeFixtureRepository(fixture)

  const plan = prepareRuntimeMatrixPromotion({
    repositoryRoot: fixture.root,
    profileId: fixture.profileId,
    sourceRevision: fixture.receipt.sourceRevision,
    generatorRunner: runRealGeneratorWithFixturePlanTrust,
  })
  t.after(() => fs.rmSync(plan.stageRoot, { recursive: true, force: true }))

  assert.deepEqual(
    plan.binding.capability.instrumentationCapabilities,
    ['inspection', 'execution-flow'],
  )
  assert.deepEqual(
    JSON.parse(fs.readFileSync(
      path.join(
        fixture.root,
        'profiles',
        'runtimes',
        'candidates',
        `${fixture.profileId}.json`,
      ),
      'utf8',
    )).capabilities,
    ['run', 'jit-asm'],
  )

  const material = Object.fromEntries(plan.replacements.map(replacement => [
    path.relative(fixture.root, replacement.path).replaceAll('\\', '/'),
    JSON.parse(replacement.content.toString('utf8')),
  ]))
  const profile = material[`profiles/runtimes/${fixture.profileId}.json`]
  assert.deepEqual(
    profile.capabilities,
    ['run', 'jit-asm', 'inspection', 'execution-flow'],
  )
  assert.equal(
    profile.operations.run.implementationId,
    'sharplabnext-runner-v1',
  )
  assert.equal(
    profile.operations.jit.implementationId,
    'sharplabnext-jit-inspector-v1',
  )
  assert.equal(profile.operations.jit.sourceMappingKind, 'linux-profiler')
  const runtime = material['profiles/catalog/catalog.json'].runtimes.find(
    candidate => candidate.id === fixture.profileId,
  )
  assert.deepEqual(
    runtime.capabilities,
    ['run', 'jit-asm', 'inspection', 'execution-flow'],
  )
})

test('real generator preserves the exact prior trust closure across consecutive promotions', t => {
  const first = createFixture(t, { targetId: 'dotnet-core-2.0' })
  const secondSource = createFixture(t, { targetId: 'dotnet-core-2.1' })
  const second = {
    ...secondSource,
    root: first.root,
    receipt: structuredClone(secondSource.receipt),
    receiptReference: structuredClone(secondSource.receiptReference),
  }
  const secondCandidateRelativePath =
    `profiles/runtimes/candidates/${second.profileId}.json`
  const secondCandidatePath = path.join(
    first.root,
    ...secondCandidateRelativePath.split('/'),
  )
  fs.mkdirSync(path.dirname(secondCandidatePath), { recursive: true })
  fs.copyFileSync(
    path.join(secondSource.root, ...secondCandidateRelativePath.split('/')),
    secondCandidatePath,
  )
  initializeFixtureRepository(first)

  promoteRuntimeMatrix({
    repositoryRoot: first.root,
    profileId: first.profileId,
    generatorRunner: runRealGeneratorWithFixturePlanTrust,
  })
  const retainedPaths = [
    `profiles/runtimes/${first.profileId}.json`,
    `profiles/runtime-promotion-plans/${first.profileId}.json`,
    `profiles/runtime-promotion-plans/${first.profileId}.profile.json`,
    `profiles/runtime-promotion-receipts/${first.profileId}.json`,
    `profiles/runtime-promotion-evidence/${first.profileId}/run.json`,
    `profiles/runtime-promotion-evidence/${first.profileId}/performance.json`,
  ]
  const retainedBytes = new Map(retainedPaths.map(relativePath => [
    relativePath,
    fs.readFileSync(path.join(first.root, ...relativePath.split('/'))),
  ]))

  const secondEvidenceRelativePath =
    `profiles/runtime-promotion-evidence/${second.profileId}`
  fs.cpSync(
    path.join(secondSource.root, ...secondEvidenceRelativePath.split('/')),
    path.join(first.root, ...secondEvidenceRelativePath.split('/')),
    { recursive: true },
  )
  second.receipt.sourceRevision = first.receipt.sourceRevision
  bindFixturePromotionPlan(second)

  const activeBeforeFailures = snapshotActiveInputs(first.root, second.profileId)
  const firstReceiptPath = path.join(
    first.root,
    'profiles',
    'runtime-promotion-receipts',
    `${first.profileId}.json`,
  )
  const firstReceiptBytes = fs.readFileSync(firstReceiptPath)
  const mismatchedReceipt = JSON.parse(firstReceiptBytes)
  mismatchedReceipt.sourceRevision = 'e'.repeat(40)
  fs.writeFileSync(firstReceiptPath, `${JSON.stringify(mismatchedReceipt, null, 2)}\n`)
  let generatorCalled = false
  try {
    assert.throws(
      () => promoteRuntimeMatrix({
        repositoryRoot: first.root,
        profileId: second.profileId,
        generatorRunner() { generatorCalled = true },
      }),
      /receipt\.sourceRevision/,
    )
  } finally {
    fs.writeFileSync(firstReceiptPath, firstReceiptBytes)
  }
  assert.equal(generatorCalled, false)
  assert.deepEqual(snapshotActiveInputs(first.root, second.profileId), activeBeforeFailures)

  const firstPlanPath = path.join(
    first.root,
    'profiles',
    'runtime-promotion-plans',
    `${first.profileId}.json`,
  )
  const firstPlanBytes = fs.readFileSync(firstPlanPath)
  fs.rmSync(firstPlanPath)
  generatorCalled = false
  try {
    assert.throws(() => promoteRuntimeMatrix({
      repositoryRoot: first.root,
      profileId: second.profileId,
      generatorRunner() { generatorCalled = true },
    }))
  } finally {
    fs.writeFileSync(firstPlanPath, firstPlanBytes)
  }
  assert.equal(generatorCalled, false)
  assert.deepEqual(snapshotActiveInputs(first.root, second.profileId), activeBeforeFailures)

  try {
    assert.throws(() => promoteRuntimeMatrix({
      repositoryRoot: first.root,
      profileId: second.profileId,
      generatorRunner(options) {
        fs.appendFileSync(firstPlanPath, ' ')
        fakeGenerator(options)
      },
    }))
  } finally {
    fs.writeFileSync(firstPlanPath, firstPlanBytes)
  }
  assert.deepEqual(snapshotActiveInputs(first.root, second.profileId), activeBeforeFailures)

  promoteRuntimeMatrix({
    repositoryRoot: first.root,
    profileId: second.profileId,
    generatorRunner: runRealGeneratorWithFixturePlanTrust,
  })

  const matrix = JSON.parse(fs.readFileSync(
    path.join(first.root, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  for (const fixture of [first, second]) {
    const target = matrix.coreClr.find(candidate => candidate.id === fixture.targetId)
    assert.equal(target.linuxCapability.promotionState, 'verified')
    assert.deepEqual(target.linuxCapability.promotionReceipt, fixture.receiptReference)
    assert.equal(
      JSON.parse(fs.readFileSync(
        path.join(first.root, 'profiles', 'runtimes', `${fixture.profileId}.json`),
        'utf8',
      )).runtimeImageId,
      fixture.receipt.image.imageId,
    )
  }
  assert.deepEqual(validateRuntimePromotionReceipts(
    matrix, first.root, fs.readFileSync, planSignatureOptions,
  ), [])
  for (const [relativePath, before] of retainedBytes) {
    assert.deepEqual(
      fs.readFileSync(path.join(first.root, ...relativePath.split('/'))),
      before,
      `${relativePath} changed while the second row was promoted`,
    )
  }
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

test('staging rejects a missing trusted helper before generator execution', t => {
  const fixture = createFixture(t)
  fs.rmSync(path.join(fixture.root, 'eng', 'runtime-wine-operator-binding.mjs'))
  let generatorCalled = false

  assert.throws(
    () => prepareRuntimeMatrixPromotion({
      repositoryRoot: fixture.root,
      profileId: fixture.profileId,
      sourceRevision,
      generatorRunner() { generatorCalled = true },
    }),
    error => error instanceof RuntimeMatrixPromotionError &&
      /eng\/runtime-wine-operator-binding\.mjs.*regular source-root file/.test(error.message),
  )
  assert.equal(generatorCalled, false)
})

test('canonical generated trust material is promoted while Git HEAD remains the build revision', t => {
  const fixture = createFixture(t)
  initializeFixtureRepository(fixture)
  const buildRevision = runGit(fixture.root, ['rev-parse', 'HEAD']).trim()

  const result = promoteRuntimeMatrix({
    repositoryRoot: fixture.root,
    profileId: fixture.profileId,
    generatorRunner(options) {
      stageFixtureSchemaClosure(options)
      const stagedMatrix = JSON.parse(fs.readFileSync(
        path.join(options.stageRoot, 'profiles', 'runtime-matrix.json'),
        'utf8',
      ))
      assert.deepEqual(
        validateRuntimePromotionReceipts(
          stagedMatrix, options.stageRoot, fs.readFileSync, planSignatureOptions,
        ),
        [],
      )
      fakeGenerator(options)
    },
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

test('candidate, plan, and preflight drift during generation fail closed', t => {
  for (const drift of ['candidate', 'plan', 'preflight']) {
    const fixture = createFixture(t)
    initializeFixtureRepository(fixture)
    const filePath = drift === 'candidate'
      ? path.join(
          fixture.root,
          'profiles',
          'runtimes',
          'candidates',
          `${fixture.profileId}.json`,
        )
      : path.join(
          fixture.root,
          'profiles',
          'runtime-promotion-plans',
          `${fixture.profileId}${drift === 'preflight' ? '.profile' : ''}.json`,
        )

    assert.throws(
      () => promoteRuntimeMatrix({
        repositoryRoot: fixture.root,
        profileId: fixture.profileId,
        generatorRunner(options) {
          fakeGenerator(options)
          fs.appendFileSync(filePath, ' ')
        },
      }),
      error => error instanceof RuntimeMatrixPromotionError &&
        (drift === 'candidate'
          ? /Candidate profile .* changed while promotion was staged/.test(error.message)
          : /Promotion input .* changed while promotion was staged/.test(error.message)),
      `${drift} drift must fail before active writes`,
    )
  }
})

function createFixture(t, {
  imageReference =
    `registry.example/sharplabnext/runtime-core21@sha256:${'a'.repeat(64)}`,
  targetId = 'dotnet-core-2.1',
} = {}) {
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
  copyFile('eng/runtime-promotion-plan-signature.mjs', root)
  copyFile('eng/runtime-wine-operator-binding.mjs', root)
  copyFile('eng/wine-coreclr-operator-receipt.mjs', root)
  copyFile('eng/profiles/trust/wine-coreclr-operator-receipt-public.pem', root)
  copyFile('eng/json-schema-instance-validation.mjs', root)
  copyFile('eng/json-schema-formats.mjs', root)
  copyFile('eng/profiles/trust/runtime-promotion-plan-public.pem', root)
  copyFile('schemas/runtime-promotion-plan.schema.json', root)
  copyFile('schemas/runtime-promotion-receipt.schema.json', root)
  copyFile('eng/generate-runtime-matrix.cs', root)
  const sourceProfiles = path.join(repositoryRoot, 'profiles', 'runtimes')
  const targetProfiles = path.join(root, 'profiles', 'runtimes')
  fs.mkdirSync(targetProfiles, { recursive: true })
  for (const entry of fs.readdirSync(sourceProfiles, { withFileTypes: true })) {
    if (entry.isFile() && entry.name.endsWith('.json')) {
      fs.copyFileSync(path.join(sourceProfiles, entry.name), path.join(targetProfiles, entry.name))
    }
  }

  const matrix = JSON.parse(fs.readFileSync(path.join(root, 'profiles', 'runtime-matrix.json'), 'utf8'))
  blockAllMatrixCapabilities(matrix)
  fs.writeFileSync(
    path.join(root, 'profiles', 'runtime-matrix.json'),
    `${JSON.stringify(matrix, null, 2)}\n`,
  )
  const target = matrix.coreClr.find(candidate => candidate.id === targetId)
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
      reference: imageReference,
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
    else if (token === '{arguments}') command.push('success-security')
    else command.push(token)
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
    measurementHelper: {
      implementation: 'sharplabnext-runtime-cgroup-sidecar-v1',
      image: {
        reference: `registry.example/runtime-supervisor@sha256:${'7'.repeat(64)}`,
        imageId: `sha256:${'8'.repeat(64)}`,
        sizeBytes: 536870912,
      },
      entrypoint: '/usr/local/bin/sharplabnext-runtime-measurement',
      sourceRevision,
      contentSha256:
        'sha256:f7645af4191d024c86769f3e39fd76ad237f537572c752fdfec3ff529aea9e4c',
    },
    sourceRevision,
    policy: {
      id: 'runtime-image-linux-x64-v1',
      sha256: performancePolicyDigest,
    },
    capabilities: ['run'],
    sourceMappingKind: 'not-applicable',
    environment: {
      runnerId: 'runtime-preflight-linux-x64-v2',
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
  const fixture = { root, profileId, targetId: target.id, target, receipt, receiptReference }
  bindFixturePromotionPlan(fixture)
  return fixture

  function copyFile(relativePath, destinationRoot) {
    const destination = path.join(destinationRoot, ...relativePath.split('/'))
    fs.mkdirSync(path.dirname(destination), { recursive: true })
    fs.copyFileSync(path.join(repositoryRoot, ...relativePath.split('/')), destination)
  }
}

function addInstrumentationCapabilities(fixture, capabilities) {
  const matrixPath = path.join(fixture.root, 'profiles', 'runtime-matrix.json')
  const matrix = JSON.parse(fs.readFileSync(matrixPath, 'utf8'))
  const target = matrix.coreClr.find(candidate => candidate.id === fixture.targetId)
  const profilePath = path.join(
    fixture.root,
    'profiles',
    'runtimes',
    'candidates',
    `${fixture.profileId}.json`,
  )
  const profile = JSON.parse(fs.readFileSync(profilePath, 'utf8'))
  target.linuxCapability.capabilities = [...profile.capabilities, ...capabilities]
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  profile.operations.run = {
    implementationId: 'sharplabnext-runner-v1',
    pathStyle: 'unix',
    command: {
      executable: '/opt/sharplabnext/target-dotnet/dotnet',
      argv: [
        '/opt/sharplabnext/SharpLabNext.Runner.dll',
        '{entryAssembly}',
        '--',
        '{arguments}',
      ],
    },
  }
  profile.layout.runnerAssemblyPath = '/opt/sharplabnext/SharpLabNext.Runner.dll'
  fs.writeFileSync(profilePath, `${JSON.stringify(profile, null, 2)}\n`)

  fixture.receipt.operations.run = {
    implementation: 'sharplabnext-runner-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.Runner.dll',
    assemblySha256: `sha256:${'c'.repeat(64)}`,
  }
  const runEvidencePath = path.join(
    fixture.root,
    'profiles',
    'runtime-promotion-evidence',
    fixture.profileId,
    'run.json',
  )
  const runEvidence = JSON.parse(fs.readFileSync(runEvidencePath, 'utf8'))
  runEvidence.artifacts[0].path = fixture.receipt.operations.run.assemblyPath
  runEvidence.artifacts[0].sha256 = fixture.receipt.operations.run.assemblySha256
  runEvidence.artifacts.push({
    role: 'support-assembly',
    path: '/opt/sharplabnext/SharpLab.Runtime.dll',
    sha256: `sha256:${'5'.repeat(64)}`,
    sizeBytes: 1048576,
    format: 'managed-pe',
    architecture: 'anycpu',
  })
  runEvidence.invocation.implementation = fixture.receipt.operations.run.implementation
  runEvidence.invocation.command = [
    profile.operations.run.command.executable,
    profile.operations.run.command.argv[0],
    runEvidence.invocation.entryAssembly.path,
    '--',
    'success-security',
  ]
  fs.writeFileSync(runEvidencePath, `${JSON.stringify(runEvidence, null, 2)}\n`)
  fixture.receipt.checks[0].evidenceSha256 = digest(fs.readFileSync(runEvidencePath))

  const jitOperation = profile.operations.jit
  fixture.receipt.operations.jit = {
    implementation: jitOperation.implementationId,
    assemblyPath: profile.layout.jitInspectorAssemblyPath,
    assemblySha256: `sha256:${'d'.repeat(64)}`,
    profilerPath: jitOperation.profilerPath,
    profilerSha256: `sha256:${'e'.repeat(64)}`,
  }
  const jitEvidence = structuredClone(runEvidence)
  jitEvidence.capability = 'jit-asm'
  jitEvidence.artifacts[0].path = fixture.receipt.operations.jit.assemblyPath
  jitEvidence.artifacts[0].sha256 = fixture.receipt.operations.jit.assemblySha256
  jitEvidence.artifacts.push(
    {
      role: 'jit-library',
      path:
        `/opt/sharplabnext/target-dotnet/shared/Microsoft.NETCore.App/` +
        `${fixture.target.version}/libclrjit.so`,
      sha256: `sha256:${'9'.repeat(64)}`,
      sizeBytes: 2097152,
      format: 'elf',
      architecture: 'x64',
    },
    {
      role: 'profiler',
      path: fixture.receipt.operations.jit.profilerPath,
      sha256: fixture.receipt.operations.jit.profilerSha256,
      sizeBytes: 524288,
      format: 'elf',
      architecture: 'x64',
    },
  )
  const methodFilter = 'Program:Main'
  jitEvidence.invocation.implementation = fixture.receipt.operations.jit.implementation
  jitEvidence.invocation.command = [
    jitOperation.command.executable,
    jitOperation.command.argv[0],
    jitEvidence.invocation.entryAssembly.path,
    methodFilter,
  ]
  jitEvidence.invocation.methodFilter = methodFilter
  delete jitEvidence.run
  jitEvidence.jit = {
    runtimeVersion: fixture.receipt.resolvedVersion,
    jitVersion: fixture.receipt.runtimeIdentity.jitVersion,
    pdb: {
      path: '/workspace/app.pdb',
      sha256: `sha256:${'3'.repeat(64)}`,
      contentId: '4'.repeat(40),
      sequencePointCount: 2,
    },
    methods: [{
      metadataToken: '0x06000001',
      displayName: methodFilter,
      nativeCodeBytes: 64,
      instructionCount: 8,
      sourceRanges: [
        {
          ilOffset: 0,
          nativeStartOffset: 0,
          nativeEndOffset: 8,
          document: '/workspace/Program.cs',
          startLine: 3,
          startColumn: 5,
          endLine: 3,
          endColumn: 20,
        },
        {
          ilOffset: 4,
          nativeStartOffset: 8,
          nativeEndOffset: 16,
          document: '/workspace/Program.cs',
          startLine: 4,
          startColumn: 5,
          endLine: 4,
          endColumn: 20,
        },
      ],
    }],
    mapping: {
      kind: 'linux-profiler',
      source: 'ordinary',
      rangeCount: 2,
      distinctSourceRangeCount: 2,
      allRangesMatchPdb: true,
    },
  }
  const jitRelativePath =
    `profiles/runtime-promotion-evidence/${fixture.profileId}/jit-asm.json`
  const jitEvidenceBytes = Buffer.from(`${JSON.stringify(jitEvidence, null, 2)}\n`)
  fs.writeFileSync(path.join(fixture.root, ...jitRelativePath.split('/')), jitEvidenceBytes)
  fixture.receipt.checks.push({
    ...fixture.receipt.checks[0],
    capability: 'jit-asm',
    sourceMappingKind: 'linux-profiler',
    mappingSource: 'ordinary',
    evidencePath: jitRelativePath,
    evidenceSha256: digest(jitEvidenceBytes),
  })

  for (const capability of capabilities) {
    const evidence = structuredClone(runEvidence)
    evidence.capability = capability
    evidence.invocation.command[evidence.invocation.command.length - 1] = capability
    delete evidence.run
    if (capability === 'inspection') {
      evidence.inspection = {
        recordCount: 2,
        kinds: ['Value', 'MemoryGraph'],
        valueProbePassed: true,
        memoryGraphProbePassed: true,
      }
    } else {
      evidence.executionFlow = {
        recordCount: 2,
        sequencePointCount: 1,
        branchCount: 1,
        sourceRangeCount: 2,
        derivedArtifactSha256: `sha256:${'4'.repeat(64)}`,
      }
    }
    const relativePath =
      `profiles/runtime-promotion-evidence/${fixture.profileId}/${capability}.json`
    const evidencePath = path.join(fixture.root, ...relativePath.split('/'))
    const evidenceBytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`)
    fs.writeFileSync(evidencePath, evidenceBytes)
    fixture.receipt.checks.push({
      ...fixture.receipt.checks[0],
      capability,
      evidencePath: relativePath,
      evidenceSha256: digest(evidenceBytes),
    })
  }

  const performancePath = path.join(
    fixture.root,
    'profiles',
    'runtime-promotion-evidence',
    fixture.profileId,
    'performance.json',
  )
  const performance = JSON.parse(fs.readFileSync(performancePath, 'utf8'))
  performance.capabilities = ['run', 'jit-asm', ...capabilities].sort()
  performance.sourceMappingKind = 'linux-profiler'
  performance.scenarios.jit = performanceScenario()
  performance.scenarios.mapping = performanceScenario()
  const performanceBytes = Buffer.from(`${JSON.stringify(performance, null, 2)}\n`)
  fs.writeFileSync(performancePath, performanceBytes)
  fixture.receipt.performance.evidenceSha256 = digest(performanceBytes)

  const receiptPath = path.join(
    fixture.root,
    'profiles',
    'runtime-promotion-receipts',
    `${fixture.profileId}.json`,
  )
  const receiptBytes = Buffer.from(`${JSON.stringify(fixture.receipt, null, 2)}\n`)
  fs.writeFileSync(receiptPath, receiptBytes)
  fixture.receiptReference.sha256 = digest(receiptBytes)
  bindFixturePromotionPlan(fixture)
}

function blockAllMatrixCapabilities(matrix) {
  const block = capability => {
    if (capability.promotionState !== 'verified') return
    capability.promotionState = 'blocked'
    capability.blockedReason = 'Fixture requires an explicit promotion receipt for this row.'
    delete capability.promotionReceipt
  }

  for (const row of matrix.coreClr) {
    block(row.linuxCapability)
    block(row.wineCapability)
  }
  block(matrix.mono.capability)
  for (const row of matrix.framework.targets) block(row.capability)
}

let performanceSampleSequence = 0

function performanceScenario() {
  const sample = latencyMilliseconds => ({
    latencyMilliseconds,
    peakMemoryBytes: 134217728,
    completionPeakMemoryBytes: 134217728,
    operationId: `op_${(++performanceSampleSequence).toString(16).padStart(32, '0')}`,
    resourceSampleCount: 1,
    postCompletionResourceSampleCount: 1,
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
  bindFixturePromotionPlan(fixture)
}

function bindFixturePromotionPlan(fixture) {
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
  preflightProfile.capabilities = fixture.receipt.checks
    .map(check => check.capability)
    .sort()
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
    candidateTarget: 'runtime-dotnet-matrix-candidate',
    profileId: fixture.profileId,
    profileSha256: digest(candidateProfileBytes),
    matrixTargetId: fixture.targetId,
    platform: fixture.receipt.platform,
    family: fixture.receipt.family,
    resolvedVersion: fixture.receipt.resolvedVersion,
    sourceRevision: fixture.receipt.sourceRevision,
    sourceTree: fixtureSourceTree(fixture.root, fixture.receipt.sourceRevision),
    buildInputs: fixtureBuildInputs(fixture),
    buildInputsSha256: '',
    producer: {
      id: 'sharplabnext-runtime-preflight-v1',
      sourceRevision: fixture.receipt.sourceRevision,
    },
    securityPolicyId: candidateProfile.allowedSecurityPolicyIds[0],
    image: structuredClone(fixture.receipt.image),
    componentIdentity: structuredClone(fixture.receipt.componentIdentity),
    runtimeIdentity: structuredClone(fixture.receipt.runtimeIdentity),
    capabilities: fixture.receipt.checks.map(check => check.capability).sort(),
    sourceMappingKind: fixture.receipt.checks.some(check => check.capability === 'jit-asm')
      ? fixture.receipt.checks.find(check => check.capability === 'jit-asm').sourceMappingKind
      : 'not-applicable',
    operations: structuredClone(fixture.receipt.operations),
    preflightProfile: {
      path: `profiles/runtime-promotion-plans/${fixture.profileId}.profile.json`,
      sha256: digest(preflightProfileBytes),
    },
    performance: {
      policyId: fixture.receipt.performance.policyId,
      policyPath: fixture.receipt.performance.policyPath,
      policySha256: fixture.receipt.performance.policySha256,
      evidencePath: fixture.receipt.performance.evidencePath,
    },
  }
  plan.buildInputsSha256 = digest(serializeRuntimePromotionPlan(plan.buildInputs))
  const planBytes = serializeRuntimePromotionPlan(plan)
  fs.writeFileSync(path.join(planDirectory, `${fixture.profileId}.json`), planBytes)
  const planSignatureBytes = Buffer.from(
    `${signRuntimePromotionPlan(planBytes, planKeys.privateKey)}\n`,
  )
  fs.writeFileSync(
    path.join(planDirectory, `${fixture.profileId}.json.sig`),
    planSignatureBytes,
  )
  fixture.receipt.planSha256 = digest(planBytes)
  fixture.receipt.planSignature = {
    path: runtimePromotionPlanSignaturePath(fixture.profileId),
    sha256: digest(planSignatureBytes),
    keyId: planKeyId,
  }
  for (const check of fixture.receipt.checks) {
    const capabilityEvidencePath = path.join(
      fixture.root,
      ...check.evidencePath.split('/'),
    )
    const capabilityEvidence = JSON.parse(fs.readFileSync(capabilityEvidencePath, 'utf8'))
    capabilityEvidence.sourceRevision = fixture.receipt.sourceRevision
    capabilityEvidence.producer.sourceRevision = fixture.receipt.sourceRevision
    capabilityEvidence.producer.planSha256 = fixture.receipt.planSha256
    const capabilityEvidenceBytes = Buffer.from(
      `${JSON.stringify(capabilityEvidence, null, 2)}\n`,
    )
    fs.writeFileSync(capabilityEvidencePath, capabilityEvidenceBytes)
    check.evidenceSha256 = digest(capabilityEvidenceBytes)
  }
  const performanceEvidencePath = path.join(
    fixture.root,
    'profiles',
    'runtime-promotion-evidence',
    fixture.profileId,
    'performance.json',
  )
  const performanceEvidence = JSON.parse(fs.readFileSync(performanceEvidencePath, 'utf8'))
  performanceEvidence.sourceRevision = fixture.receipt.sourceRevision
  performanceEvidence.measurementHelper.sourceRevision = fixture.receipt.sourceRevision
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

function fixtureSourceTree(root, revision) {
  const result = spawnSync('git', ['-C', root, 'rev-parse', `${revision}^{tree}`], {
    encoding: 'utf8',
    timeout: 10_000,
    windowsHide: true,
  })
  const tree = result.status === 0 ? result.stdout.trim() : ''
  return /^[0-9a-f]{40}$/.test(tree) ? tree : revision
}

function fixtureBuildInputs(fixture) {
  return {
    RUNTIME_MATRIX_PROFILE_ID: fixture.profileId,
    RUNTIME_MATRIX_RUNTIME_VERSION: fixture.receipt.resolvedVersion,
    RUNTIME_MATRIX_RUNTIME_COMMIT: fixture.receipt.runtimeIdentity.runtimeCommit,
    RUNTIME_MATRIX_JIT_COMMIT: fixture.receipt.runtimeIdentity.jitCommit,
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI: fixture.receipt.componentIdentity.sourceUri,
    RUNTIME_MATRIX_RUNTIME_SHA512: fixture.receipt.componentIdentity.sourceDigest,
    SOURCE_REVISION: fixture.receipt.sourceRevision,
  }
}

function runRealGeneratorWithFixturePlanTrust({ repositoryRoot: fixtureRoot, stageRoot }) {
  stageFixtureSchemaClosure({ repositoryRoot: fixtureRoot, stageRoot })
  for (const relativePath of [
    'eng/json-schema-instance-validation.mjs',
    'eng/json-schema-formats.mjs',
    'schemas/runtime-promotion-plan.schema.json',
    'schemas/runtime-promotion-receipt.schema.json',
  ]) {
    assert.equal(
      fs.existsSync(path.join(stageRoot, ...relativePath.split('/'))),
      true,
      `staged generator is missing ${relativePath}`,
    )
  }
  const validatorPath = path.join(stageRoot, 'eng', 'runtime-promotion-receipt-validation.mjs')
  const implementationPath = path.join(
    stageRoot,
    'eng',
    'runtime-promotion-receipt-validation.impl.mjs',
  )
  fs.renameSync(validatorPath, implementationPath)
  const publicKey = planKeys.publicKey.export({ type: 'spki', format: 'pem' })
  fs.writeFileSync(validatorPath, [
    "import fs from 'node:fs'",
    "import path from 'node:path'",
    "import { validateRuntimePromotionReceipts } from './runtime-promotion-receipt-validation.impl.mjs'",
    `const publicKey = ${JSON.stringify(publicKey)}`,
    `const keyId = ${JSON.stringify(planKeyId)}`,
    'let repositoryRoot = process.cwd()',
    'let matrixPath',
    'for (let index = 2; index < process.argv.length; index += 1) {',
    '  const option = process.argv[index]',
    "  if (option !== '--repository-root' && option !== '--matrix') { process.exitCode = 64; break }",
    '  const value = process.argv[++index]',
    "  if (!value) { process.exitCode = 64; break }",
    "  if (option === '--repository-root') repositoryRoot = path.resolve(value)",
    '  else matrixPath = path.resolve(value)',
    '}',
    'if (process.exitCode === undefined) {',
    "  matrixPath ??= path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')",
    '  const matrix = JSON.parse(fs.readFileSync(matrixPath, \'utf8\'))',
    '  const failures = validateRuntimePromotionReceipts(matrix, repositoryRoot, fs.readFileSync, {',
    '    planSignaturePublicKey: publicKey,',
    '    planSignatureKeyId: keyId,',
    '  })',
    '  if (failures.length > 0) {',
    '    for (const failure of failures) console.error(`promotion receipt error: ${failure}`)',
    '    process.exitCode = 1',
    "  } else console.log('Runtime promotion receipts are valid.')",
    '}',
    '',
  ].join('\n'))
  const result = spawnSync('dotnet', [
    'run', path.join(fixtureRoot, 'eng', 'generate-runtime-matrix.cs'), '--',
    '--repository-root', stageRoot,
    '--matrix', path.join(stageRoot, 'profiles', 'runtime-matrix.json'),
    '--catalog', path.join(stageRoot, 'profiles', 'catalog', 'catalog.json'),
    '--profiles', path.join(stageRoot, 'profiles', 'runtimes'),
    '--overwrite-profiles',
    '--allow-active-profile-overwrite',
  ], {
    cwd: fixtureRoot,
    encoding: 'utf8',
    timeout: 120_000,
    windowsHide: true,
  })
  assert.equal(
    result.status,
    0,
    `Runtime matrix generator failed.\n${result.stdout ?? ''}${result.stderr ?? ''}`,
  )
}

function stageFixtureSchemaClosure({ repositoryRoot: fixtureRoot, stageRoot }) {
  for (const relativePath of [
    'eng/json-schema-instance-validation.mjs',
    'eng/json-schema-formats.mjs',
    'schemas/runtime-promotion-plan.schema.json',
    'schemas/runtime-promotion-receipt.schema.json',
  ]) {
    const source = path.join(fixtureRoot, ...relativePath.split('/'))
    const target = path.join(stageRoot, ...relativePath.split('/'))
    fs.mkdirSync(path.dirname(target), { recursive: true })
    fs.copyFileSync(source, target)
  }
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
