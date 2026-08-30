/**
 * Crash-safe escrow and ordered import orchestration for the 34-row runtime
 * promotion transaction. Receipt/evidence trust remains owned by
 * promote-runtime-matrix.mjs; this module sequences already-produced material.
 */

import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'

import {
  findRuntimeMatrixBinding,
  promoteRuntimeMatrix,
} from './promote-runtime-matrix.mjs'
import { formalRuntimeCandidateProfileIds } from '../runtime-candidate-environment.mjs'
import { validateRuntimePromotionReceipts } from './runtime-promotion-receipt-validation.mjs'
import {
  isWinePromotionFamily,
  runtimeOperatorReceiptPaths,
  validateWineOperatorBinding,
} from './runtime-wine-operator-binding.mjs'
import {
  runtimePromotionPlanKeyId,
  runtimePromotionPlanSignaturePath,
  serializeRuntimePromotionPlan,
  verifyRuntimePromotionPlanSignature,
} from './runtime-promotion-plan-signature.mjs'

const defaultRepositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const maximumFileBytes = 1024 * 1024
const commitPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const digestPattern = /^sha256:[0-9a-f]{64}$/
const imagePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const idPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const phases = Object.freeze(['pending', 'escrowed', 'copied', 'checked', 'applied'])
const commonPromotionOutputs = Object.freeze(['deploy/images.json', 'profiles/catalog/catalog.json', 'profiles/lock.json', 'profiles/runtime-matrix.json']);
const renameRetryDelays = Object.freeze([25, 50, 100, 200, 400, 800])
const renameRetryWaitBuffer = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT))

export const runtimePromotionBatchUsage = `Usage:
  node eng/release/runtime-promotion-batch.mjs init --batch-id ID --producer-root PATH --aggregate-root PATH
  node eng/release/runtime-promotion-batch.mjs escrow --batch-id ID --profile-id ID --producer-root PATH --aggregate-root PATH
  node eng/release/runtime-promotion-batch.mjs import-promote --batch-id ID --profile-id ID --aggregate-root PATH
  node eng/release/runtime-promotion-batch.mjs status --batch-id ID --aggregate-root PATH
  node eng/release/runtime-promotion-batch.mjs verify-complete --batch-id ID --aggregate-root PATH`

export class RuntimePromotionBatchError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimePromotionBatchError'
  }
}

function fail(message, options) { throw new RuntimePromotionBatchError(message, options); }

function canonicalJson(value) { return Buffer.from(`${JSON.stringify(value)}\n`); }

function sha256(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`; }

function buffersEqual(left, right) { return left.length === right.length && crypto.timingSafeEqual(left, right); }

function parseJson(bytes, label) {
  try {
    return JSON.parse(bytes.toString('utf8'))
  } catch (error) {
    fail(`${label} is invalid JSON: ${error.message}`, { cause: error })
  }
}

function safeId(value, label) {
  if (typeof value !== 'string' || !idPattern.test(value)) {
    fail(`${label} must be a safe lowercase identifier.`)
  }
  return value
}

function canonicalRelativePath(value, label) {
  if (typeof value !== 'string' || value.length === 0 || value.includes('\\') ||
      path.isAbsolute(value) || value.split('/').some(part => part === '' || part === '.' || part === '..')) {
    fail(`${label} must be a canonical repository-relative path.`)
  }
  return value
}

function realDirectory(value, label) {
  const resolved = path.resolve(value)
  let real
  try {
    real = fs.realpathSync.native(resolved)
  } catch (error) {
    fail(`${label} does not exist.`, { cause: error })
  }
  const equal = process.platform === 'win32'
    ? real.toLowerCase() === resolved.toLowerCase()
    : real === resolved
  if (!equal || !fs.statSync(real).isDirectory() || fs.lstatSync(real).isSymbolicLink()) {
    fail(`${label} must be a real directory without symlinked or reparse path components.`)
  }
  return real
}

function assertContained(root, filename, label) {
  const full = path.resolve(filename)
  const relative = path.relative(root, full)
  if (relative === '' || relative === '..' || relative.startsWith(`..${path.sep}`) ||
      path.isAbsolute(relative)) {
    fail(`${label} escapes '${root}'.`)
  }
  return full
}

function assertNoLinkedComponents(root, filename, includeLeaf = true) {
  const full = assertContained(root, filename, 'batch path')
  const relative = path.relative(root, full)
  const parts = relative.split(path.sep)
  let current = root
  for (let index = 0; index < parts.length - (includeLeaf ? 0 : 1); index++) {
    current = path.join(current, parts[index])
    const stat = fs.lstatSync(current, { throwIfNoEntry: false })
    if (stat?.isSymbolicLink()) fail(`Path component '${current}' cannot be a link or reparse point.`)
  }
}

function readBoundedFile(root, filename, label) {
  const full = assertContained(root, filename, label)
  assertNoLinkedComponents(root, full)
  const before = fs.lstatSync(full, { throwIfNoEntry: false })
  if (!before?.isFile() || before.isSymbolicLink() || before.size < 1 ||
      before.size > maximumFileBytes) {
    fail(`${label} must be a 1..${maximumFileBytes} byte regular non-link file.`)
  }
  const descriptor = fs.openSync(full, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0))
  try {
    const opened = fs.fstatSync(descriptor)
    if (!opened.isFile() || opened.size !== before.size ||
        (opened.dev !== undefined && opened.dev !== before.dev) ||
        (opened.ino !== undefined && opened.ino !== before.ino)) {
      fail(`${label} changed while it was opened.`)
    }
    const bytes = fs.readFileSync(descriptor)
    const after = fs.fstatSync(descriptor)
    if (bytes.length !== opened.size || after.size !== opened.size || after.mtimeMs !== opened.mtimeMs) {
      fail(`${label} changed while it was read.`)
    }
    return bytes
  } finally {
    fs.closeSync(descriptor)
  }
}

function readCanonicalJson(root, filename, label) {
  const bytes = readBoundedFile(root, filename, label)
  const value = parseJson(bytes, label)
  if (!buffersEqual(bytes, canonicalJson(value))) fail(`${label} is not canonical JSON.`)
  return { bytes, value }
}

function fsyncDirectory(directory) {
  let descriptor
  try {
    descriptor = fs.openSync(directory, fs.constants.O_RDONLY)
    fs.fsyncSync(descriptor)
  } catch (error) {
    if (process.platform !== 'win32' || !['EINVAL', 'EPERM', 'EACCES'].includes(error?.code)) throw error
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor)
  }
}

function renameSyncWithRetry(source, destination) {
  for (let attempt = 0; ; attempt += 1) {
    try {
      fs.renameSync(source, destination)
      return
    } catch (error) {
      if (process.platform !== 'win32' || !['EBUSY', 'EPERM'].includes(error?.code) || attempt >= renameRetryDelays.length) throw error
      Atomics.wait(renameRetryWaitBuffer, 0, 0, renameRetryDelays[attempt])
    }
  }
}

function writeFileDurably(filename, bytes, flag = 'wx') {
  const descriptor = fs.openSync(filename, flag, 0o600)
  try {
    fs.writeFileSync(descriptor, bytes)
    fs.fsyncSync(descriptor)
  } finally {
    fs.closeSync(descriptor)
  }
}

function replaceCanonicalState(batchRoot, state) {
  const target = path.join(batchRoot, 'state.json')
  const temporary = path.join(batchRoot, `.state.${crypto.randomUUID().replaceAll('-', '')}.tmp`)
  writeFileDurably(temporary, canonicalJson(state))
  renameSyncWithRetry(temporary, target)
  fsyncDirectory(batchRoot)
}

function runGit(root, arguments_, description) {
  const result = spawnSync('git', ['-C', root, ...arguments_], {
    encoding: 'utf8',
    timeout: 10_000,
    windowsHide: true,
    shell: false,
  })
  if (result.error !== undefined || result.status !== 0) {
    fail(`${description}: ${String(result.stderr ?? result.error?.message ?? '').trim()}`)
  }
  return String(result.stdout ?? '')
}

function gitRevision(root) {
  const revision = runGit(root, ['rev-parse', '--verify', 'HEAD'], 'Could not resolve Git HEAD').trim()
  if (!commitPattern.test(revision)) fail(`Git HEAD for '${root}' is not a full commit.`)
  return revision
}

function parseGitStatus(output) {
  const result = []
  const records = output.split('\0')
  for (let index = 0; index < records.length; index++) {
    const record = records[index]
    if (record.length === 0) continue
    if (record.length < 4 || record[2] !== ' ' || /[RC]/.test(record.slice(0, 2))) {
      fail('Git returned a malformed, renamed, or copied worktree record.')
    }
    const relative = canonicalRelativePath(record.slice(3).replaceAll('\\', '/'), 'Git path')
    result.push({ status: record.slice(0, 2), path: relative })
  }
  return result
}

function gitChanges(root) {
  return parseGitStatus(runGit(
    root,
    ['status', '--porcelain=v1', '-z', '--untracked-files=all'],
    'Could not inspect Git worktree',
  ))
}

function requireCleanRepository(root, revision, label) {
  if (gitRevision(root) !== revision) fail(`${label} HEAD does not equal batch source revision '${revision}'.`)
  const changes = gitChanges(root)
  if (changes.length > 0) fail(`${label} must be clean; unexpected '${changes[0].path}'.`)
}

function requireExactChanges(root, allowed, label) {
  for (const change of gitChanges(root)) {
    if (!allowed.has(change.path)) fail(`${label} change '${change.path}' is outside the batch closure.`)
  }
}

function batchParent(aggregateRoot) { return path.join(aggregateRoot, '.tmp', 'runtime-promotion-batches'); }

function batchPath(aggregateRoot, batchId) { return path.join(batchParent(aggregateRoot), safeId(batchId, 'batch ID')); }

function ensureBatchParent(aggregateRoot) {
  const parent = batchParent(aggregateRoot)
  fs.mkdirSync(parent, { recursive: true })
  assertNoLinkedComponents(aggregateRoot, parent)
  const probe = '.tmp/runtime-promotion-batches/.ignore-probe'
  const result = spawnSync('git', ['-C', aggregateRoot, 'check-ignore', '--quiet', '--', probe], {
    encoding: 'utf8',
    timeout: 10_000,
    windowsHide: true,
    shell: false,
  })
  if (result.status !== 0) fail("'.tmp/runtime-promotion-batches' must be ignored by the repository.")
  return parent
}

function withGlobalLock(aggregateRoot, action) {
  const parent = ensureBatchParent(aggregateRoot)
  const lockPath = path.join(parent, '.batch.lock')
  let descriptor
  try {
    descriptor = fs.openSync(lockPath, 'wx', 0o600)
    fs.writeFileSync(descriptor, `${process.pid}\n`)
    fs.fsyncSync(descriptor)
  } catch (error) {
    if (error?.code === 'EEXIST') fail(`Runtime promotion batch lock '${lockPath}' is already held.`)
    throw error
  }
  try {
    return action()
  } finally {
    fs.closeSync(descriptor)
    fs.rmSync(lockPath, { force: true })
    fsyncDirectory(parent)
  }
}

function readMatrix(root) {
  const filename = path.join(root, 'profiles', 'runtime-matrix.json')
  const bytes = readBoundedFile(root, filename, 'runtime matrix')
  const value = parseJson(bytes, 'runtime matrix')
  return { bytes, canonicalBytes: canonicalJson(value), value }
}

function assertPristinePromotionBaseline(root, matrix, profileIds, label) {
  const operatorPaths = runtimeOperatorReceiptPaths(gitRevision(root))
  for (const relativePath of [operatorPaths.receiptPath, operatorPaths.signaturePath]) {
    const candidate = path.join(root, ...relativePath.split('/'))
    if (fs.lstatSync(candidate, { throwIfNoEntry: false }) !== undefined) {
      fail(`${label} contains stale promotion output '${relativePath}' before batch init.`)
    }
  }
  for (const profileId of profileIds) {
    const binding = findRuntimeMatrixBinding(matrix, profileId)
    const capability = binding.capability
    if (capability?.promotionState !== 'blocked') {
      fail(`${label} formal row '${profileId}' must have promotionState 'blocked' before batch init.`)
    }
    if (Object.hasOwn(capability ?? {}, 'promotionReceipt')) {
      fail(`${label} formal row '${profileId}' must not have a promotionReceipt before batch init.`)
    }

    for (const relativePath of [
      `profiles/runtime-promotion-receipts/${profileId}.json`,
      `profiles/runtime-promotion-plans/${profileId}.json`,
      runtimePromotionPlanSignaturePath(profileId),
      `profiles/runtime-promotion-plans/${profileId}.profile.json`,
    ]) {
      const candidate = path.join(root, ...relativePath.split('/'))
      if (fs.lstatSync(candidate, { throwIfNoEntry: false }) !== undefined) {
        fail(`${label} contains stale promotion output '${relativePath}' before batch init.`)
      }
    }

    const evidenceDirectory = path.join(root, 'profiles', 'runtime-promotion-evidence', profileId)
    const evidenceStat = fs.lstatSync(evidenceDirectory, { throwIfNoEntry: false })
    if (evidenceStat === undefined) continue
    if (!evidenceStat.isDirectory() || evidenceStat.isSymbolicLink()) {
      fail(`${label} has an invalid promotion evidence directory for '${profileId}' before batch init.`)
    }
    if (fs.readdirSync(evidenceDirectory).length > 0) {
      fail(`${label} contains stale promotion evidence for '${profileId}' before batch init.`)
    }
  }
}

function initialManifest(batchId, sourceRevision, matrixBytes, profileIds) {
  return {
    schemaVersion: 1,
    kind: 'runtime-promotion-batch-v1',
    batchId,
    sourceRevision,
    matrixSha256: sha256(matrixBytes),
    profileIds,
  }
}

function initialState(manifest) {
  return {
    schemaVersion: 1,
    batchId: manifest.batchId,
    sourceRevision: manifest.sourceRevision,
    nextIndex: 0,
    rows: manifest.profileIds.map(profileId => ({
      profileId,
      phase: 'pending',
      receiptSha256: null,
    })),
  }
}

function validateBatchDocuments(manifest, state) {
  if (manifest?.schemaVersion !== 1 || manifest.kind !== 'runtime-promotion-batch-v1' ||
      !idPattern.test(manifest.batchId ?? '') || !commitPattern.test(manifest.sourceRevision ?? '') ||
      !digestPattern.test(manifest.matrixSha256 ?? '') || !Array.isArray(manifest.profileIds) ||
      manifest.profileIds.length !== 34 || new Set(manifest.profileIds).size !== 34) {
    fail('Runtime promotion batch manifest is invalid.')
  }
  if (state?.schemaVersion !== 1 || state.batchId !== manifest.batchId ||
      state.sourceRevision !== manifest.sourceRevision || !Number.isSafeInteger(state.nextIndex) ||
      state.nextIndex < 0 || state.nextIndex > 34 || !Array.isArray(state.rows) ||
      state.rows.length !== 34) {
    fail('Runtime promotion batch state is invalid.')
  }
  for (let index = 0; index < 34; index++) {
    const row = state.rows[index]
    if (row?.profileId !== manifest.profileIds[index] || !phases.includes(row.phase) ||
        (row.receiptSha256 !== null && !digestPattern.test(row.receiptSha256))) {
      fail(`Runtime promotion batch state row ${index} is invalid.`)
    }
    if (index < state.nextIndex && row.phase !== 'applied') {
      fail(`Runtime promotion batch row '${row.profileId}' precedes nextIndex but is not applied.`)
    }
    if (index >= state.nextIndex && row.phase === 'applied') {
      fail(`Runtime promotion batch row '${row.profileId}' is applied beyond nextIndex.`)
    }
  }
}

function loadBatch(aggregateRoot, batchId) {
  const root = batchPath(aggregateRoot, batchId)
  const real = realDirectory(root, 'runtime promotion batch')
  if (path.parse(real).root.toLowerCase() !== path.parse(aggregateRoot).root.toLowerCase()) {
    fail('Runtime promotion batch must be on the aggregate repository volume.')
  }
  const manifest = readCanonicalJson(root, path.join(root, 'manifest.json'), 'batch manifest').value
  const state = readCanonicalJson(root, path.join(root, 'state.json'), 'batch state').value
  validateBatchDocuments(manifest, state)
  if (manifest.batchId !== batchId) fail('Batch path and manifest ID do not agree.')
  return { root, manifest, state }
}

export function initRuntimePromotionBatch(input, options = {}) {
  const aggregateRoot = realDirectory(input.aggregateRoot, 'aggregate repository')
  const producerRoot = realDirectory(input.producerRoot, 'producer repository')
  const batchId = safeId(input.batchId, 'batch ID')
  if (aggregateRoot.toLowerCase() === producerRoot.toLowerCase()) {
    fail('Producer and aggregate repositories must be distinct worktrees.')
  }
  if (fs.statSync(aggregateRoot).dev !== fs.statSync(producerRoot).dev ||
      path.parse(aggregateRoot).root.toLowerCase() !== path.parse(producerRoot).root.toLowerCase()) {
    fail('Producer and aggregate repositories must be on the same volume.')
  }
  return withGlobalLock(aggregateRoot, () => {
    const aggregateRevision = gitRevision(aggregateRoot)
    requireCleanRepository(aggregateRoot, aggregateRevision, 'aggregate repository')
    requireCleanRepository(producerRoot, aggregateRevision, 'producer repository')
    const aggregateMatrix = readMatrix(aggregateRoot)
    const producerMatrix = readMatrix(producerRoot)
    const aggregateProfileIds = [...formalRuntimeCandidateProfileIds(aggregateMatrix.value)]
    const producerProfileIds = [...formalRuntimeCandidateProfileIds(producerMatrix.value)]
    assertPristinePromotionBaseline(aggregateRoot, aggregateMatrix.value, aggregateProfileIds, 'aggregate repository')
    assertPristinePromotionBaseline(producerRoot, producerMatrix.value, producerProfileIds, 'producer repository')
    if (!buffersEqual(aggregateMatrix.canonicalBytes, producerMatrix.canonicalBytes)) {
      fail('Producer and aggregate runtime matrices differ at source revision A.')
    }
    const profileIds = aggregateProfileIds
    const manifest = initialManifest(batchId, aggregateRevision, aggregateMatrix.canonicalBytes, profileIds)
    const state = initialState(manifest)
    const parent = batchParent(aggregateRoot)
    const destination = batchPath(aggregateRoot, batchId)
    if (fs.existsSync(destination)) fail(`Runtime promotion batch '${batchId}' already exists.`)
    const staging = path.join(parent, `.init-${batchId}-${crypto.randomUUID().replaceAll('-', '')}`)
    fs.mkdirSync(staging, { recursive: false, mode: 0o700 })
    try {
      fs.mkdirSync(path.join(staging, 'escrow'))
      writeFileDurably(path.join(staging, 'manifest.json'), canonicalJson(manifest))
      writeFileDurably(path.join(staging, 'state.json'), canonicalJson(state))
      fsyncDirectory(path.join(staging, 'escrow'))
      fsyncDirectory(staging)
      options.faultInjector?.('before-init-commit')
      renameSyncWithRetry(staging, destination)
      fsyncDirectory(parent)
    } catch (error) {
      fs.rmSync(staging, { recursive: true, force: true })
      throw error
    }
    return batchStatusDocument(manifest, state)
  })
}

function outputPathsFromReceipt(root, profileId, sourceRevision, options = {}) {
  const receiptPath = `profiles/runtime-promotion-receipts/${profileId}.json`
  const receiptBytes = readBoundedFile(root, path.join(root, ...receiptPath.split('/')), 'promotion receipt')
  const receipt = parseJson(receiptBytes, 'promotion receipt')
  if (receipt?.schemaVersion !== 2 || receipt.profileId !== profileId ||
      receipt.sourceRevision !== sourceRevision || !imagePattern.test(receipt.image?.reference ?? '') ||
      !digestPattern.test(receipt.image?.imageId ?? '') || !digestPattern.test(receipt.planSha256 ?? '')) {
    fail(`Promotion receipt for '${profileId}' does not close source revision and image identity.`)
  }
  const planPath = `profiles/runtime-promotion-plans/${profileId}.json`
  const planSignaturePath = runtimePromotionPlanSignaturePath(profileId)
  const preflightPath = `profiles/runtime-promotion-plans/${profileId}.profile.json`
  const checks = receipt.checks
  if (!Array.isArray(checks) || checks.length === 0) fail(`Receipt '${profileId}' has no capability checks.`)
  const capabilityPaths = []
  const capabilities = new Set()
  for (const check of checks) {
    const capability = safeId(check?.capability, `${profileId} receipt capability`)
    if (capabilities.has(capability)) fail(`Receipt '${profileId}' repeats capability '${capability}'.`)
    capabilities.add(capability)
    const expected = `profiles/runtime-promotion-evidence/${profileId}/${capability}.json`
    if (check.evidencePath !== expected || !digestPattern.test(check.evidenceSha256 ?? '')) {
      fail(`Receipt '${profileId}' capability '${capability}' has a noncanonical evidence binding.`)
    }
    capabilityPaths.push(expected)
  }
  const performancePath = `profiles/runtime-promotion-evidence/${profileId}/performance.json`
  if (receipt.performance?.evidencePath !== performancePath ||
      !digestPattern.test(receipt.performance?.evidenceSha256 ?? '')) {
    fail(`Receipt '${profileId}' has a noncanonical performance evidence binding.`)
  }
  const operatorPaths = isWinePromotionFamily(receipt.family)
    ? runtimeOperatorReceiptPaths(sourceRevision)
    : undefined
  try {
    validateWineOperatorBinding(receipt.wineOperator, receipt.family, sourceRevision)
  } catch (error) {
    fail(`Promotion receipt for '${profileId}' has an invalid Wine operator binding: ${error.message}`)
  }
  if (operatorPaths !== undefined &&
      (receipt.wineOperator.receiptPath !== operatorPaths.receiptPath ||
       receipt.wineOperator.signaturePath !== operatorPaths.signaturePath)) {
    fail(`Promotion receipt for '${profileId}' has noncanonical Wine operator receipt paths.`)
  }
  const relativePaths = [
    planPath, planSignaturePath, preflightPath, receiptPath, performancePath, ...capabilityPaths,
    ...(operatorPaths === undefined ? [] : [operatorPaths.receiptPath, operatorPaths.signaturePath]),
  ]
    .sort((left, right) => left < right ? -1 : left > right ? 1 : 0)
  if (new Set(relativePaths).size !== relativePaths.length) fail(`Receipt '${profileId}' repeats an output path.`)
  const files = new Map(relativePaths.map(relativePath => [
    relativePath,
    readBoundedFile(root, path.join(root, ...relativePath.split('/')), `promotion output '${relativePath}'`),
  ]))
  const plan = parseJson(files.get(planPath), `${profileId} promotion plan`)
  const planSignature = files.get(planSignaturePath)
  const preflight = parseJson(files.get(preflightPath), `${profileId} preflight profile`)
  const performance = parseJson(files.get(performancePath), `${profileId} performance evidence`)
  if (plan.profileId !== profileId || plan.sourceRevision !== sourceRevision ||
      sha256(files.get(planPath)) !== receipt.planSha256 ||
      !serializeRuntimePromotionPlan(plan.image).equals(serializeRuntimePromotionPlan(receipt.image)) ||
      plan.preflightProfile?.path !== preflightPath ||
      plan.preflightProfile?.sha256 !== sha256(files.get(preflightPath)) ||
      preflight.id !== profileId || preflight.image !== receipt.image.reference ||
      preflight.runtimeImageId !== receipt.image.imageId ||
      performance.profileId !== profileId || performance.sourceRevision !== sourceRevision ||
      performance.planSha256 !== receipt.planSha256 ||
      !serializeRuntimePromotionPlan(performance.image).equals(serializeRuntimePromotionPlan(receipt.image)) ||
      sha256(files.get(performancePath)) !== receipt.performance.evidenceSha256) {
    fail(`Promotion output closure for '${profileId}' does not match its plan/receipt.`)
  }
  if (!planSignature || !Buffer.from(files.get(planPath)).equals(serializeRuntimePromotionPlan(plan)) ||
      receipt.planSignature?.path !== planSignaturePath ||
      receipt.planSignature?.sha256 !== sha256(planSignature) ||
      receipt.planSignature?.keyId !== (options.planSignatureKeyId ?? runtimePromotionPlanKeyId)) {
    fail(`Promotion plan signature closure for '${profileId}' is invalid.`)
  }
  try { verifyRuntimePromotionPlanSignature(files.get(planPath), planSignature,
    options.planSignaturePublicKey === undefined
      ? {}
      : { publicKey: options.planSignaturePublicKey, keyId: options.planSignatureKeyId }) } catch (error) {
    fail(`Promotion plan signature for '${profileId}' is invalid: ${error.message}`)
  }
  if (operatorPaths !== undefined &&
      (!serializeRuntimePromotionPlan(plan.wineOperator).equals(
        serializeRuntimePromotionPlan(receipt.wineOperator)) ||
       sha256(files.get(operatorPaths.receiptPath)) !== receipt.wineOperator.receiptSha256 ||
       sha256(files.get(operatorPaths.signaturePath)) !== receipt.wineOperator.signatureSha256)) {
    fail(`Promotion Wine operator closure for '${profileId}' does not match its plan/receipt.`)
  }
  for (const check of checks) {
    const evidence = parseJson(files.get(check.evidencePath), `${profileId} ${check.capability} evidence`)
    if (evidence.profileId !== profileId || evidence.capability !== check.capability ||
        evidence.sourceRevision !== sourceRevision || evidence.producer?.sourceRevision !== sourceRevision ||
        evidence.producer?.planSha256 !== receipt.planSha256 ||
        evidence.image?.reference !== receipt.image.reference ||
        evidence.image?.imageId !== receipt.image.imageId ||
        sha256(files.get(check.evidencePath)) !== check.evidenceSha256) {
      fail(`Capability evidence '${check.evidencePath}' does not match its receipt.`)
    }
  }
  const evidenceDirectory = path.join(root, 'profiles', 'runtime-promotion-evidence', profileId)
  const expectedEvidenceNames = new Set([
    path.basename(performancePath),
    ...capabilityPaths.map(capabilityPath => path.basename(capabilityPath)),
  ])
  const entries = fs.readdirSync(evidenceDirectory, { withFileTypes: true })
  if (entries.some(entry => !entry.isFile() || entry.isSymbolicLink() || !expectedEvidenceNames.has(entry.name)) ||
      entries.length !== expectedEvidenceNames.size) {
    fail(`Promotion evidence directory for '${profileId}' contains an extra or unsafe file.`)
  }
  return { receipt, receiptBytes, files, receiptSha256: sha256(receiptBytes) }
}

function escrowRoot(batchRoot, profileId) { return path.join(batchRoot, 'escrow', safeId(profileId, 'profile ID')); }

function rowManifest(batch, profileId, closure) {
  return {
    schemaVersion: 1,
    batchId: batch.manifest.batchId,
    profileId,
    sourceRevision: batch.manifest.sourceRevision,
    imageReference: closure.receipt.image.reference,
    receiptSha256: closure.receiptSha256,
    files: [...closure.files.entries()].map(([relativePath, bytes]) => ({
      path: relativePath,
      sizeBytes: bytes.length,
      sha256: sha256(bytes),
    })),
  }
}

function installEscrow(batch, profileId, closure, faultInjector, options = {}) {
  const destination = escrowRoot(batch.root, profileId)
  if (fs.existsSync(destination)) return readAndVerifyEscrow(batch, profileId, options)
  const parent = path.dirname(destination)
  const staging = path.join(parent, `.stage-${profileId}-${crypto.randomUUID().replaceAll('-', '')}`)
  fs.mkdirSync(staging, { mode: 0o700 })
  try {
    for (const [relativePath, bytes] of closure.files) {
      const target = path.join(staging, 'files', ...relativePath.split('/'))
      fs.mkdirSync(path.dirname(target), { recursive: true })
      writeFileDurably(target, bytes)
    }
    const manifest = rowManifest(batch, profileId, closure)
    writeFileDurably(path.join(staging, 'manifest.json'), canonicalJson(manifest))
    fsyncDirectory(staging)
    renameSyncWithRetry(staging, destination)
    fsyncDirectory(parent)
    faultInjector?.('after-escrow-commit', profileId)
    return readAndVerifyEscrow(batch, profileId, options)
  } catch (error) {
    fs.rmSync(staging, { recursive: true, force: true })
    throw error
  }
}

function readAndVerifyEscrow(batch, profileId, options = {}) {
  const root = realDirectory(escrowRoot(batch.root, profileId), `escrow '${profileId}'`)
  const manifest = readCanonicalJson(root, path.join(root, 'manifest.json'), `escrow manifest '${profileId}'`).value
  if (manifest?.schemaVersion !== 1 || manifest.batchId !== batch.manifest.batchId ||
      manifest.profileId !== profileId || manifest.sourceRevision !== batch.manifest.sourceRevision ||
      !imagePattern.test(manifest.imageReference ?? '') || !digestPattern.test(manifest.receiptSha256 ?? '') ||
      !Array.isArray(manifest.files) || manifest.files.length < 5) {
    fail(`Escrow manifest for '${profileId}' is invalid.`)
  }
  const paths = new Set()
  const files = new Map()
  for (const file of manifest.files) {
    const relativePath = canonicalRelativePath(file?.path, 'escrow file path')
    if (paths.has(relativePath) || !Number.isSafeInteger(file.sizeBytes) || file.sizeBytes < 1 ||
        file.sizeBytes > maximumFileBytes || !digestPattern.test(file.sha256 ?? '')) {
      fail(`Escrow file binding for '${profileId}' is invalid.`)
    }
    paths.add(relativePath)
    const bytes = readBoundedFile(root, path.join(root, 'files', ...relativePath.split('/')), `escrow file '${relativePath}'`)
    if (bytes.length !== file.sizeBytes || sha256(bytes) !== file.sha256) {
      fail(`Escrow file '${relativePath}' changed.`)
    }
    files.set(relativePath, bytes)
  }
  const actual = []
  walkRegularFiles(root, root, actual)
  const expected = new Set(['manifest.json', ...manifest.files.map(file => `files/${file.path}`)])
  if (actual.length !== expected.size || actual.some(value => !expected.has(value))) {
    fail(`Escrow '${profileId}' contains an extra or unsafe file.`)
  }
  const receiptPath = `profiles/runtime-promotion-receipts/${profileId}.json`
  const receiptBytes = files.get(receiptPath)
  if (receiptBytes === undefined || sha256(receiptBytes) !== manifest.receiptSha256) {
    fail(`Escrow '${profileId}' receipt binding is invalid.`)
  }
  const receipt = parseJson(receiptBytes, `escrow receipt '${profileId}'`)
  const planPath = `profiles/runtime-promotion-plans/${profileId}.json`
  const signaturePath = runtimePromotionPlanSignaturePath(profileId)
  const planBytes = files.get(planPath)
  const signatureBytes = files.get(signaturePath)
  const expectedKeyId = options.planSignatureKeyId ?? runtimePromotionPlanKeyId
  if (receipt?.profileId !== profileId || receipt.sourceRevision !== batch.manifest.sourceRevision ||
      planBytes === undefined || signatureBytes === undefined || receipt.planSha256 !== sha256(planBytes) ||
      receipt.planSignature?.path !== signaturePath || receipt.planSignature?.sha256 !== sha256(signatureBytes) ||
      receipt.planSignature?.keyId !== expectedKeyId) {
    fail(`Escrow '${profileId}' promotion plan signature closure is invalid.`)
  }
  const plan = parseJson(planBytes, `escrow plan '${profileId}'`)
  if (!planBytes.equals(serializeRuntimePromotionPlan(plan)) || plan.profileId !== profileId ||
      plan.sourceRevision !== batch.manifest.sourceRevision) {
    fail(`Escrow '${profileId}' promotion plan binding is invalid.`)
  }
  try { verifyRuntimePromotionPlanSignature(planBytes, signatureBytes,
    options.planSignaturePublicKey === undefined
      ? {}
      : { publicKey: options.planSignaturePublicKey, keyId: options.planSignatureKeyId }) } catch (error) {
    fail(`Escrow '${profileId}' promotion plan signature is invalid: ${error.message}`)
  }
  const operatorPaths = runtimeOperatorReceiptPaths(batch.manifest.sourceRevision)
  const hasOperatorReceipt = manifest.files.some(file => file.path === operatorPaths.receiptPath)
  const hasOperatorSignature = manifest.files.some(file => file.path === operatorPaths.signaturePath)
  if (hasOperatorReceipt !== hasOperatorSignature) fail(`Escrow '${profileId}' has a partial Wine operator receipt closure.`)
  return manifest
}

function walkRegularFiles(root, directory, output) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isSymbolicLink()) fail(`Escrow entry '${full}' cannot be a link.`)
    if (entry.isDirectory()) walkRegularFiles(root, full, output)
    else if (entry.isFile()) output.push(path.relative(root, full).replaceAll('\\', '/'))
    else fail(`Escrow entry '${full}' must be regular.`)
  }
}

function deleteCanonicalOutputs(root, manifest) {
  for (const file of manifest.files) {
    const target = path.join(root, ...file.path.split('/'))
    if (!fs.existsSync(target)) continue
    const bytes = readBoundedFile(root, target, `canonical promotion output '${file.path}'`)
    if (sha256(bytes) !== file.sha256 || bytes.length !== file.sizeBytes) {
      fail(`Canonical promotion output '${file.path}' differs from committed escrow.`)
    }
    fs.rmSync(target)
  }
}

function defaultPromotionRunner({ repositoryRoot, profileId, check }) { return promoteRuntimeMatrix({ repositoryRoot, profileId, check }); }

export function escrowRuntimePromotionProfile(input, options = {}) {
  const aggregateRoot = realDirectory(input.aggregateRoot, 'aggregate repository')
  const producerRoot = realDirectory(input.producerRoot, 'producer repository')
  const profileId = safeId(input.profileId, 'profile ID')
  return withGlobalLock(aggregateRoot, () => {
    const batch = loadBatch(aggregateRoot, input.batchId)
    const row = batch.state.rows.find(value => value.profileId === profileId)
    if (row === undefined) fail(`Profile '${profileId}' is outside the formal batch scope.`)
    if (gitRevision(producerRoot) !== batch.manifest.sourceRevision) {
      fail('Producer HEAD does not equal batch source revision A.')
    }
    let manifest
    if (fs.existsSync(escrowRoot(batch.root, profileId))) {
      manifest = readAndVerifyEscrow(batch, profileId, options)
    } else {
      if (row.phase !== 'pending') fail(`Escrow for '${profileId}' is missing at phase '${row.phase}'.`)
      const promotionRunner = options.promotionRunner ?? defaultPromotionRunner
      promotionRunner({ repositoryRoot: producerRoot, profileId, check: true })
      const closure = outputPathsFromReceipt(producerRoot, profileId, batch.manifest.sourceRevision, options)
      requireExactChanges(producerRoot, new Set(closure.files.keys()), 'producer repository')
      manifest = installEscrow(batch, profileId, closure, options.faultInjector, options)
    }
    deleteCanonicalOutputs(producerRoot, manifest)
    options.faultInjector?.('after-canonical-delete', profileId)
    requireCleanRepository(producerRoot, batch.manifest.sourceRevision, 'producer repository')
    row.phase = phases.indexOf(row.phase) < phases.indexOf('escrowed') ? 'escrowed' : row.phase
    row.receiptSha256 = manifest.receiptSha256
    replaceCanonicalState(batch.root, batch.state)
    return Object.freeze({ profileId, phase: row.phase, receiptSha256: row.receiptSha256 })
  })
}

function escrowBytes(batch, manifest, file) {
  return readBoundedFile(
    escrowRoot(batch.root, manifest.profileId),
    path.join(escrowRoot(batch.root, manifest.profileId), 'files', ...file.path.split('/')),
    `escrow file '${file.path}'`,
  )
}

function copyEscrowOutputs(batch, aggregateRoot, manifest) {
  for (const file of manifest.files) {
    const bytes = escrowBytes(batch, manifest, file)
    const target = path.join(aggregateRoot, ...file.path.split('/'))
    if (fs.existsSync(target)) {
      const existing = readBoundedFile(aggregateRoot, target, `aggregate output '${file.path}'`)
      if (!buffersEqual(existing, bytes)) fail(`Aggregate output '${file.path}' conflicts with escrow.`)
      continue
    }
    fs.mkdirSync(path.dirname(target), { recursive: true })
    assertNoLinkedComponents(aggregateRoot, path.dirname(target))
    const temporary = path.join(path.dirname(target), `.${path.basename(target)}.${crypto.randomUUID().replaceAll('-', '')}.tmp`)
    writeFileDurably(temporary, bytes)
    renameSyncWithRetry(temporary, target)
    fsyncDirectory(path.dirname(target))
  }
}

function appliedEscrowFiles(batch, options = {}) {
  const protectedFiles = new Map()
  for (const row of batch.state.rows) {
    if (row.phase !== 'applied') continue
    const applied = readAndVerifyEscrow(batch, row.profileId, options)
    for (const file of applied.files) {
      const existing = protectedFiles.get(file.path)
      if (existing !== undefined &&
          (existing.sha256 !== file.sha256 || existing.sizeBytes !== file.sizeBytes)) {
        fail(`Applied runtime rows disagree about shared output '${file.path}'.`)
      }
      protectedFiles.set(file.path, file)
    }
  }
  return protectedFiles
}

function removeImportedOutputs(batch, aggregateRoot, manifest, options = {}) {
  const protectedFiles = appliedEscrowFiles(batch, options)
  for (const file of manifest.files) {
    const target = path.join(aggregateRoot, ...file.path.split('/'))
    if (!fs.existsSync(target)) continue
    const bytes = readBoundedFile(aggregateRoot, target, `aggregate output '${file.path}'`)
    if (sha256(bytes) !== file.sha256 || bytes.length !== file.sizeBytes) {
      fail(`Cannot roll back changed aggregate output '${file.path}'.`)
    }
    const protectedFile = protectedFiles.get(file.path)
    if (protectedFile !== undefined) {
      if (protectedFile.sha256 !== file.sha256 || protectedFile.sizeBytes !== file.sizeBytes) {
        fail(`Cannot roll back conflicting shared aggregate output '${file.path}'.`)
      }
      continue
    }
    fs.rmSync(target)
  }
}

function bindingIsApplied(aggregateRoot, profileId, receiptSha256) {
  const matrix = readMatrix(aggregateRoot).value
  const binding = findRuntimeMatrixBinding(matrix, profileId)
  return binding.capability?.promotionState === 'verified' &&
    binding.capability?.promotionReceipt?.path ===
      `profiles/runtime-promotion-receipts/${profileId}.json` &&
    binding.capability?.promotionReceipt?.sha256 === receiptSha256
}

function allowedAggregateChanges(batch, currentManifest = undefined, currentApplied = false, options = {}) {
  const allowed = new Set()
  for (const row of batch.state.rows) {
    if (row.phase !== 'applied') continue
    const manifest = readAndVerifyEscrow(batch, row.profileId, options)
    for (const file of manifest.files) allowed.add(file.path)
    allowed.add(`profiles/runtimes/${row.profileId}.json`)
  }
  if (batch.state.nextIndex > 0 || currentApplied) {
    for (const value of commonPromotionOutputs) allowed.add(value)
  }
  if (currentManifest !== undefined) {
    for (const file of currentManifest.files) allowed.add(file.path)
    if (currentApplied) allowed.add(`profiles/runtimes/${currentManifest.profileId}.json`)
  }
  return allowed
}

function verifyManifestAtAggregate(batch, aggregateRoot, manifest) {
  for (const file of manifest.files) {
    const aggregate = readBoundedFile(
      aggregateRoot,
      path.join(aggregateRoot, ...file.path.split('/')),
      `aggregate output '${file.path}'`,
    )
    const escrow = escrowBytes(batch, manifest, file)
    if (!buffersEqual(aggregate, escrow)) fail(`Aggregate output '${file.path}' differs from escrow.`)
  }
}

export function importPromoteRuntimeProfile(input, options = {}) {
  const aggregateRoot = realDirectory(input.aggregateRoot, 'aggregate repository')
  const profileId = safeId(input.profileId, 'profile ID')
  return withGlobalLock(aggregateRoot, () => {
    const batch = loadBatch(aggregateRoot, input.batchId)
    if (gitRevision(aggregateRoot) !== batch.manifest.sourceRevision) {
      fail('Aggregate HEAD does not equal batch source revision A.')
    }
    const expected = batch.manifest.profileIds[batch.state.nextIndex]
    if (profileId !== expected) {
      fail(`Profile '${profileId}' is out of order; canonical next row is '${expected ?? '<complete>'}'.`)
    }
    const row = batch.state.rows[batch.state.nextIndex]
    if (phases.indexOf(row.phase) < phases.indexOf('escrowed')) {
      fail(`Profile '${profileId}' has not been escrowed.`)
    }
    const manifest = readAndVerifyEscrow(batch, profileId, options)
    if (manifest.receiptSha256 !== row.receiptSha256) fail(`State receipt binding for '${profileId}' changed.`)
    const alreadyApplied = bindingIsApplied(aggregateRoot, profileId, manifest.receiptSha256)
    requireExactChanges(
      aggregateRoot,
      allowedAggregateChanges(batch, manifest, alreadyApplied, options),
      'aggregate repository',
    )
    if (alreadyApplied) {
      verifyManifestAtAggregate(batch, aggregateRoot, manifest)
      row.phase = 'applied'
      batch.state.nextIndex += 1
      replaceCanonicalState(batch.root, batch.state)
      return Object.freeze({ profileId, phase: 'applied', recovered: true })
    }

    copyEscrowOutputs(batch, aggregateRoot, manifest)
    row.phase = 'copied'
    replaceCanonicalState(batch.root, batch.state)
    options.faultInjector?.('after-copy', profileId)
    requireExactChanges(aggregateRoot, allowedAggregateChanges(batch, manifest, false, options), 'aggregate repository')
    const promotionRunner = options.promotionRunner ?? defaultPromotionRunner
    try {
      promotionRunner({ repositoryRoot: aggregateRoot, profileId, check: true })
    } catch (error) {
      removeImportedOutputs(batch, aggregateRoot, manifest, options)
      row.phase = 'escrowed'
      replaceCanonicalState(batch.root, batch.state)
      throw error
    }
    row.phase = 'checked'
    replaceCanonicalState(batch.root, batch.state)
    options.faultInjector?.('after-check', profileId)
    try {
      promotionRunner({ repositoryRoot: aggregateRoot, profileId, check: false })
    } catch (error) {
      removeImportedOutputs(batch, aggregateRoot, manifest, options)
      row.phase = 'escrowed'
      replaceCanonicalState(batch.root, batch.state)
      throw error
    }
    options.faultInjector?.('after-promote', profileId)
    if (!bindingIsApplied(aggregateRoot, profileId, manifest.receiptSha256)) {
      fail(`Promotion tool did not materialize verified matrix row '${profileId}'.`)
    }
    verifyManifestAtAggregate(batch, aggregateRoot, manifest)
    row.phase = 'applied'
    batch.state.nextIndex += 1
    replaceCanonicalState(batch.root, batch.state)
    requireExactChanges(aggregateRoot, allowedAggregateChanges(batch, undefined, false, options), 'aggregate repository')
    return Object.freeze({ profileId, phase: 'applied', recovered: false })
  })
}

function batchStatusDocument(manifest, state) {
  const counts = Object.fromEntries(phases.map(phase => [phase, state.rows.filter(row => row.phase === phase).length]))
  return {
    schemaVersion: 1,
    batchId: manifest.batchId,
    sourceRevision: manifest.sourceRevision,
    total: manifest.profileIds.length,
    nextIndex: state.nextIndex,
    nextProfileId: manifest.profileIds[state.nextIndex] ?? null,
    complete: state.nextIndex === manifest.profileIds.length,
    counts,
    rows: state.rows,
  }
}

export function runtimePromotionBatchStatus(input, options = {}) {
  const aggregateRoot = realDirectory(input.aggregateRoot, 'aggregate repository')
  return withGlobalLock(aggregateRoot, () => {
    const batch = loadBatch(aggregateRoot, input.batchId)
    for (const row of batch.state.rows) {
      if (row.phase !== 'pending') readAndVerifyEscrow(batch, row.profileId, options)
    }
    return batchStatusDocument(batch.manifest, batch.state)
  })
}

export function verifyRuntimePromotionBatchComplete(input, options = {}) {
  const aggregateRoot = realDirectory(input.aggregateRoot, 'aggregate repository')
  return withGlobalLock(aggregateRoot, () => {
    const batch = loadBatch(aggregateRoot, input.batchId)
    if (batch.state.nextIndex !== 34 || batch.state.rows.some(row => row.phase !== 'applied')) {
      fail(`Runtime promotion batch is incomplete (${batch.state.nextIndex}/34).`)
    }
    if (gitRevision(aggregateRoot) !== batch.manifest.sourceRevision) {
      fail('Aggregate HEAD does not equal batch source revision A.')
    }
    const matrix = readMatrix(aggregateRoot).value
    const profileIds = [...formalRuntimeCandidateProfileIds(matrix)]
    if (JSON.stringify(profileIds) !== JSON.stringify(batch.manifest.profileIds)) {
      fail('Final runtime matrix formal scope/order differs from the batch manifest.')
    }
    const closure = new Set(commonPromotionOutputs)
    for (const row of batch.state.rows) {
      const manifest = readAndVerifyEscrow(batch, row.profileId, options)
      verifyManifestAtAggregate(batch, aggregateRoot, manifest)
      if (!bindingIsApplied(aggregateRoot, row.profileId, manifest.receiptSha256)) {
        fail(`Final runtime matrix row '${row.profileId}' is not verified by its escrow receipt.`)
      }
      for (const file of manifest.files) closure.add(file.path)
      closure.add(`profiles/runtimes/${row.profileId}.json`)
    }
    const receiptFailures = (options.validateReceipts ?? validateRuntimePromotionReceipts)(
      matrix,
      aggregateRoot,
      fs.readFileSync,
      options,
    )
    if (receiptFailures.length > 0) {
      fail(`Final runtime promotion receipts are invalid: ${receiptFailures.join(' ')}`)
    }
    requireExactChanges(aggregateRoot, closure, 'aggregate repository')
    return Object.freeze({
      schemaVersion: 1,
      batchId: batch.manifest.batchId,
      complete: true,
      sourceRevisionA: batch.manifest.sourceRevision,
      promotedCount: 34,
      promotionClosure: [...closure].sort((left, right) => left < right ? -1 : left > right ? 1 : 0),
    })
  })
}

function parseArguments(argv) {
  const command = argv[0]
  if (command === undefined || ['--help', '-h'].includes(command)) return { help: true }
  if (!['init', 'escrow', 'import-promote', 'status', 'verify-complete'].includes(command)) {
    fail(`Unknown runtime promotion batch command '${command}'.`)
  }
  const values = { command, aggregateRoot: defaultRepositoryRoot }
  const seen = new Set()
  for (let index = 1; index < argv.length; index++) {
    const option = argv[index]
    if (!['--batch-id', '--profile-id', '--producer-root', '--aggregate-root'].includes(option)) {
      fail(`Unknown option '${option}'.`)
    }
    if (seen.has(option)) fail(`Duplicate option '${option}'.`)
    seen.add(option)
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail(`${option} requires a value.`)
    values[{
      '--batch-id': 'batchId',
      '--profile-id': 'profileId',
      '--producer-root': 'producerRoot',
      '--aggregate-root': 'aggregateRoot',
    }[option]] = value
  }
  if (values.batchId === undefined) fail('--batch-id is required.')
  if (['escrow', 'import-promote'].includes(command) && values.profileId === undefined) {
    fail('--profile-id is required.')
  }
  if (['init', 'escrow'].includes(command) && values.producerRoot === undefined) {
    fail('--producer-root is required.')
  }
  return values
}

export function runRuntimePromotionBatch(argv, options = {}) {
  const output = options.output ?? console
  try {
    const input = parseArguments(argv)
    if (input.help) {
      output.log(runtimePromotionBatchUsage)
      return 0
    }
    const result = {
      init: initRuntimePromotionBatch,
      escrow: escrowRuntimePromotionProfile,
      'import-promote': importPromoteRuntimeProfile,
      status: runtimePromotionBatchStatus,
      'verify-complete': verifyRuntimePromotionBatchComplete,
    }[input.command](input, options)
    output.log(JSON.stringify(result))
    return 0
  } catch (error) {
    output.error(`runtime promotion batch error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimePromotionBatch(process.argv.slice(2))
}
