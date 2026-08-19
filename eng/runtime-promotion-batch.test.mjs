import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import { findRuntimeMatrixBinding } from './promote-runtime-matrix.mjs'
import {
  escrowRuntimePromotionProfile,
  importPromoteRuntimeProfile,
  initRuntimePromotionBatch,
  runtimePromotionBatchStatus,
  verifyRuntimePromotionBatchComplete,
} from './runtime-promotion-batch.mjs'
import { formalRuntimeCandidateProfileIds } from './runtime-candidate-environment.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

function jsonBytes(value) {
  return Buffer.from(`${JSON.stringify(value, null, 2)}\n`)
}

function canonicalBytes(value) {
  return Buffer.from(`${JSON.stringify(value)}\n`)
}

function digest(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}

function runGit(root, args) {
  const result = spawnSync('git', ['-C', root, ...args], {
    encoding: 'utf8', timeout: 10_000, windowsHide: true, shell: false,
  })
  assert.equal(result.status, 0, result.stderr || result.error?.message)
  return String(result.stdout ?? '')
}

function createRepositories(t) {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-promotion-batch-'))
  t.after(() => fs.rmSync(parent, { recursive: true, force: true }))
  const aggregate = path.join(parent, 'aggregate')
  const producer = path.join(parent, 'producer')
  fs.mkdirSync(aggregate)
  for (const [relative, bytes] of [
    ['.gitignore', Buffer.from('.tmp/\nartifacts/\n')],
    ['profiles/runtime-matrix.json', fs.readFileSync(path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'))],
    ['profiles/catalog/catalog.json', jsonBytes({ applied: [] })],
    ['profiles/lock.json', jsonBytes({ applied: [] })],
    ['deploy/images.json', jsonBytes({ applied: [] })],
    ['sentinel.txt', Buffer.from('clean\n')],
  ]) {
    const filename = path.join(aggregate, ...relative.split('/'))
    fs.mkdirSync(path.dirname(filename), { recursive: true })
    fs.writeFileSync(filename, bytes)
  }
  runGit(aggregate, ['init', '--quiet'])
  runGit(aggregate, ['config', 'user.email', 'batch@example.invalid'])
  runGit(aggregate, ['config', 'user.name', 'Runtime Batch'])
  runGit(aggregate, ['add', '--all'])
  runGit(aggregate, ['commit', '--quiet', '-m', 'source A'])
  const clone = spawnSync('git', ['clone', '--quiet', aggregate, producer], {
    encoding: 'utf8', timeout: 20_000, windowsHide: true, shell: false,
  })
  assert.equal(clone.status, 0, clone.stderr || clone.error?.message)
  const sourceRevision = runGit(aggregate, ['rev-parse', 'HEAD']).trim()
  assert.equal(runGit(producer, ['rev-parse', 'HEAD']).trim(), sourceRevision)
  return { parent, aggregate, producer, sourceRevision, batchId: 'batch-test' }
}

function initialize(fixture) {
  return initRuntimePromotionBatch({
    batchId: fixture.batchId,
    producerRoot: fixture.producer,
    aggregateRoot: fixture.aggregate,
  })
}

function outputIdentity(profileId) {
  const hash = crypto.createHash('sha256').update(profileId).digest('hex')
  const other = crypto.createHash('sha256').update(`image:${profileId}`).digest('hex')
  return {
    reference: `registry.example/sharplabnext/runtime-${profileId}@sha256:${hash}`,
    imageId: `sha256:${other}`,
    sizeBytes: 1024,
  }
}

function writePromotionOutputs(root, profileId, sourceRevision) {
  const image = outputIdentity(profileId)
  const preflightPath = `profiles/runtime-promotion-plans/${profileId}.profile.json`
  const planPath = `profiles/runtime-promotion-plans/${profileId}.json`
  const receiptPath = `profiles/runtime-promotion-receipts/${profileId}.json`
  const evidencePath = `profiles/runtime-promotion-evidence/${profileId}/run.json`
  const performancePath = `profiles/runtime-promotion-evidence/${profileId}/performance.json`
  const preflightBytes = jsonBytes({ id: profileId, image: image.reference, runtimeImageId: image.imageId })
  const plan = {
    schemaVersion: 1,
    profileId,
    sourceRevision,
    image,
    preflightProfile: { path: preflightPath, sha256: digest(preflightBytes) },
  }
  const planBytes = jsonBytes(plan)
  const planSha256 = digest(planBytes)
  const evidenceBytes = jsonBytes({
    schemaVersion: 1,
    profileId,
    capability: 'run',
    sourceRevision,
    image: { reference: image.reference, imageId: image.imageId },
    producer: { sourceRevision, planSha256 },
  })
  const performanceBytes = jsonBytes({
    schemaVersion: 1,
    profileId,
    sourceRevision,
    planSha256,
    image,
  })
  const receipt = {
    schemaVersion: 2,
    profileId,
    sourceRevision,
    image,
    planSha256,
    checks: [{
      capability: 'run',
      evidencePath,
      evidenceSha256: digest(evidenceBytes),
    }],
    performance: {
      evidencePath: performancePath,
      evidenceSha256: digest(performanceBytes),
    },
  }
  const receiptBytes = jsonBytes(receipt)
  const files = new Map([
    [preflightPath, preflightBytes],
    [planPath, planBytes],
    [receiptPath, receiptBytes],
    [evidencePath, evidenceBytes],
    [performancePath, performanceBytes],
  ])
  for (const [relative, bytes] of files) {
    const filename = path.join(root, ...relative.split('/'))
    fs.mkdirSync(path.dirname(filename), { recursive: true })
    fs.writeFileSync(filename, bytes)
  }
  return { files, receipt, receiptSha256: digest(receiptBytes) }
}

function escrowProfile(fixture, profileId, options = {}) {
  const material = writePromotionOutputs(fixture.producer, profileId, fixture.sourceRevision)
  const calls = []
  const result = escrowRuntimePromotionProfile({
    batchId: fixture.batchId,
    profileId,
    producerRoot: fixture.producer,
    aggregateRoot: fixture.aggregate,
  }, {
    ...options,
    promotionRunner(input) {
      calls.push(input)
      assert.equal(input.check, true)
      options.promotionRunner?.(input)
    },
  })
  return { material, calls, result }
}

function fakePromotionRunner(fixture, behavior = {}) {
  const calls = []
  const runner = input => {
    calls.push({ ...input })
    if (input.check) {
      behavior.onCheck?.(input)
      return
    }
    if (behavior.failPromotion) throw new Error('injected promotion failure')
    const receiptPath = path.join(
      fixture.aggregate,
      'profiles',
      'runtime-promotion-receipts',
      `${input.profileId}.json`,
    )
    const receiptBytes = fs.readFileSync(receiptPath)
    const matrixPath = path.join(fixture.aggregate, 'profiles', 'runtime-matrix.json')
    const matrix = JSON.parse(fs.readFileSync(matrixPath, 'utf8'))
    const binding = findRuntimeMatrixBinding(matrix, input.profileId)
    binding.capability.promotionState = 'verified'
    binding.capability.promotionReceipt = {
      path: `profiles/runtime-promotion-receipts/${input.profileId}.json`,
      sha256: digest(receiptBytes),
    }
    delete binding.capability.blockedReason
    fs.writeFileSync(matrixPath, jsonBytes(matrix))
    for (const relative of [
      'profiles/catalog/catalog.json',
      'profiles/lock.json',
      'deploy/images.json',
    ]) {
      const filename = path.join(fixture.aggregate, ...relative.split('/'))
      const value = JSON.parse(fs.readFileSync(filename, 'utf8'))
      value.applied.push(input.profileId)
      fs.writeFileSync(filename, jsonBytes(value))
    }
    const profilePath = path.join(fixture.aggregate, 'profiles', 'runtimes', `${input.profileId}.json`)
    fs.mkdirSync(path.dirname(profilePath), { recursive: true })
    fs.writeFileSync(profilePath, jsonBytes({
      id: input.profileId,
      promotionReceipt: binding.capability.promotionReceipt,
    }))
    behavior.afterPromotion?.(input)
  }
  return { calls, runner }
}

test('init binds two clean worktrees at the same full commit and derives canonical 34-row scope', t => {
  const fixture = createRepositories(t)
  const status = initialize(fixture)
  assert.equal(status.total, 34)
  assert.equal(status.sourceRevision, fixture.sourceRevision)
  const matrix = JSON.parse(fs.readFileSync(
    path.join(fixture.aggregate, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  assert.deepEqual(status.rows.map(row => row.profileId), formalRuntimeCandidateProfileIds(matrix))
  const excluded = matrix.coreClr
    .filter(row => Number.parseInt(row.channel, 10) < 5)
    .map(row => `wine-${row.id}-linux-x64`)
  assert.equal(excluded.length, 5)
  assert.equal(excluded.some(id => status.rows.some(row => row.profileId === id)), false)
  const batchRoot = path.join(fixture.aggregate, '.tmp', 'runtime-promotion-batches', fixture.batchId)
  for (const name of ['manifest.json', 'state.json']) {
    const bytes = fs.readFileSync(path.join(batchRoot, name))
    assert.deepEqual(bytes, canonicalBytes(JSON.parse(bytes)))
  }
  assert.throws(() => initialize(fixture), /already exists/)
})

test('init rejects dirty or mismatched producer source and an active global lock', t => {
  const dirty = createRepositories(t)
  fs.writeFileSync(path.join(dirty.producer, 'unexpected.txt'), 'dirty')
  assert.throws(() => initialize(dirty), /producer repository must be clean/)

  const locked = createRepositories(t)
  initialize(locked)
  const lockPath = path.join(locked.aggregate, '.tmp', 'runtime-promotion-batches', '.batch.lock')
  fs.writeFileSync(lockPath, 'other\n')
  assert.throws(
    () => runtimePromotionBatchStatus({ batchId: locked.batchId, aggregateRoot: locked.aggregate }),
    /already held/,
  )
  fs.rmSync(lockPath)
})

test('escrow runs promotion check, commits exact files, deletes originals and is idempotent', t => {
  const fixture = createRepositories(t)
  const status = initialize(fixture)
  const profileId = status.rows[0].profileId
  const escrowed = escrowProfile(fixture, profileId)
  assert.equal(escrowed.calls.length, 1)
  assert.equal(escrowed.result.phase, 'escrowed')
  for (const relative of escrowed.material.files.keys()) {
    assert.equal(fs.existsSync(path.join(fixture.producer, ...relative.split('/'))), false)
  }
  assert.equal(runGit(fixture.producer, ['status', '--porcelain=v1']).trim(), '')
  const repeated = escrowRuntimePromotionProfile({
    batchId: fixture.batchId,
    profileId,
    producerRoot: fixture.producer,
    aggregateRoot: fixture.aggregate,
  }, { promotionRunner() { throw new Error('must not rerun') } })
  assert.equal(repeated.phase, 'escrowed')
})

test('escrow recovers after manifest commit and rejects extra files, symlinks and hash drift', t => {
  const recovered = createRepositories(t)
  const profileId = initialize(recovered).rows[0].profileId
  writePromotionOutputs(recovered.producer, profileId, recovered.sourceRevision)
  assert.throws(() => escrowRuntimePromotionProfile({
    batchId: recovered.batchId,
    profileId,
    producerRoot: recovered.producer,
    aggregateRoot: recovered.aggregate,
  }, {
    promotionRunner() {},
    faultInjector(phase) { if (phase === 'after-escrow-commit') throw new Error('crash') },
  }), /crash/)
  const result = escrowRuntimePromotionProfile({
    batchId: recovered.batchId,
    profileId,
    producerRoot: recovered.producer,
    aggregateRoot: recovered.aggregate,
  }, { promotionRunner() { throw new Error('must not rerun') } })
  assert.equal(result.phase, 'escrowed')

  const extra = createRepositories(t)
  const extraId = initialize(extra).rows[0].profileId
  writePromotionOutputs(extra.producer, extraId, extra.sourceRevision)
  fs.writeFileSync(path.join(extra.producer, 'unrelated.txt'), 'unexpected')
  assert.throws(() => escrowRuntimePromotionProfile({
    batchId: extra.batchId,
    profileId: extraId,
    producerRoot: extra.producer,
    aggregateRoot: extra.aggregate,
  }, { promotionRunner() {} }), /outside the batch closure/)

  const linked = createRepositories(t)
  const linkedId = initialize(linked).rows[0].profileId
  const linkedMaterial = writePromotionOutputs(linked.producer, linkedId, linked.sourceRevision)
  const evidence = [...linkedMaterial.files.keys()].find(value => value.endsWith('/run.json'))
  const evidencePath = path.join(linked.producer, ...evidence.split('/'))
  const target = path.join(linked.parent, 'link-target.json')
  fs.writeFileSync(target, '{}\n')
  try {
    fs.rmSync(evidencePath)
    fs.symlinkSync(target, evidencePath, 'file')
    assert.throws(() => escrowRuntimePromotionProfile({
      batchId: linked.batchId,
      profileId: linkedId,
      producerRoot: linked.producer,
      aggregateRoot: linked.aggregate,
    }, { promotionRunner() {} }), /regular non-link|link or reparse/)
  } catch (error) {
    if (error?.code !== 'EPERM') throw error
  }

  const drifted = createRepositories(t)
  const driftedId = initialize(drifted).rows[0].profileId
  escrowProfile(drifted, driftedId)
  const escrowFile = path.join(
    drifted.aggregate, '.tmp', 'runtime-promotion-batches', drifted.batchId,
    'escrow', driftedId, 'files', 'profiles', 'runtime-promotion-plans', `${driftedId}.json`,
  )
  fs.appendFileSync(escrowFile, 'drift')
  assert.throws(() => runtimePromotionBatchStatus({
    batchId: drifted.batchId,
    aggregateRoot: drifted.aggregate,
  }), /changed/)
})

test('import enforces canonical order and rolls copied trust files back on promotion failure', t => {
  const fixture = createRepositories(t)
  const status = initialize(fixture)
  const first = status.rows[0].profileId
  const second = status.rows[1].profileId
  escrowProfile(fixture, first)
  escrowProfile(fixture, second)
  assert.throws(() => importPromoteRuntimeProfile({
    batchId: fixture.batchId,
    profileId: second,
    aggregateRoot: fixture.aggregate,
  }, { promotionRunner() {} }), /out of order/)

  const failed = fakePromotionRunner(fixture, { failPromotion: true })
  assert.throws(() => importPromoteRuntimeProfile({
    batchId: fixture.batchId,
    profileId: first,
    aggregateRoot: fixture.aggregate,
  }, { promotionRunner: failed.runner }), /injected promotion failure/)
  const after = runtimePromotionBatchStatus({ batchId: fixture.batchId, aggregateRoot: fixture.aggregate })
  assert.equal(after.rows[0].phase, 'escrowed')
  assert.equal(runGit(fixture.aggregate, ['status', '--porcelain=v1']).trim(), '')
})

for (const crashPhase of ['after-copy', 'after-check', 'after-promote']) {
  test(`import recovers after ${crashPhase}`, t => {
    const fixture = createRepositories(t)
    const profileId = initialize(fixture).rows[0].profileId
    escrowProfile(fixture, profileId)
    const promotion = fakePromotionRunner(fixture)
    assert.throws(() => importPromoteRuntimeProfile({
      batchId: fixture.batchId,
      profileId,
      aggregateRoot: fixture.aggregate,
    }, {
      promotionRunner: promotion.runner,
      faultInjector(phase) { if (phase === crashPhase) throw new Error(`crash ${phase}`) },
    }), new RegExp(`crash ${crashPhase}`))
    const result = importPromoteRuntimeProfile({
      batchId: fixture.batchId,
      profileId,
      aggregateRoot: fixture.aggregate,
    }, { promotionRunner: promotion.runner })
    assert.equal(result.phase, 'applied')
    assert.equal(
      runtimePromotionBatchStatus({ batchId: fixture.batchId, aggregateRoot: fixture.aggregate }).nextIndex,
      1,
    )
  })
}

test('import rejects conflicting canonical bytes and arbitrary aggregate dirt', t => {
  const conflict = createRepositories(t)
  const profileId = initialize(conflict).rows[0].profileId
  const material = escrowProfile(conflict, profileId).material
  const firstPath = [...material.files.keys()][0]
  const target = path.join(conflict.aggregate, ...firstPath.split('/'))
  fs.mkdirSync(path.dirname(target), { recursive: true })
  fs.writeFileSync(target, 'conflict\n')
  assert.throws(() => importPromoteRuntimeProfile({
    batchId: conflict.batchId,
    profileId,
    aggregateRoot: conflict.aggregate,
  }, { promotionRunner() {} }), /conflicts with escrow/)

  const dirty = createRepositories(t)
  const dirtyId = initialize(dirty).rows[0].profileId
  escrowProfile(dirty, dirtyId)
  fs.writeFileSync(path.join(dirty.aggregate, 'arbitrary.txt'), 'dirty')
  assert.throws(() => importPromoteRuntimeProfile({
    batchId: dirty.batchId,
    profileId: dirtyId,
    aggregateRoot: dirty.aggregate,
  }, { promotionRunner() {} }), /outside the batch closure/)
})

test('all 34 rows can be escrowed/imported and complete emits A plus exact closure without commit B', t => {
  const fixture = createRepositories(t)
  const initial = initialize(fixture)
  for (const row of initial.rows) escrowProfile(fixture, row.profileId)
  const promotion = fakePromotionRunner(fixture)
  for (const row of initial.rows) {
    const result = importPromoteRuntimeProfile({
      batchId: fixture.batchId,
      profileId: row.profileId,
      aggregateRoot: fixture.aggregate,
    }, { promotionRunner: promotion.runner })
    assert.equal(result.phase, 'applied')
  }
  const result = verifyRuntimePromotionBatchComplete({
    batchId: fixture.batchId,
    aggregateRoot: fixture.aggregate,
  }, { validateReceipts: () => [] })
  assert.equal(result.complete, true)
  assert.equal(result.promotedCount, 34)
  assert.equal(result.sourceRevisionA, fixture.sourceRevision)
  assert.equal(result.promotionClosure.includes('profiles/runtime-matrix.json'), true)
  assert.equal(result.promotionClosure.includes(`profiles/runtimes/${initial.rows[0].profileId}.json`), true)
  assert.equal(runGit(fixture.aggregate, ['rev-parse', 'HEAD']).trim(), fixture.sourceRevision)
})
