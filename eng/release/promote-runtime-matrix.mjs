import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { validateRuntimePromotionReceipts } from './runtime-promotion-receipt-validation.mjs'
import {
  runtimePromotionPlanKeyId,
  runtimePromotionPlanSignaturePath,
  serializeRuntimePromotionPlan,
  verifyRuntimePromotionPlanSignature,
} from './runtime-promotion-plan-signature.mjs'
import {
  isWinePromotionFamily,
  loadOwnedWineOperatorBinding,
  runtimeOperatorReceiptPaths,
  validateWineOperatorBinding,
} from './runtime-wine-operator-binding.mjs'

const digestPattern = /^sha256:[0-9a-f]{64}$/
const commitPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const profileIdPattern = /^[a-z0-9][a-z0-9._-]*$/
const deploymentRepositoryPattern =
  /^[a-z0-9][a-z0-9.-]*(?::[1-9][0-9]{0,4})?(?:\/[a-z0-9][a-z0-9._-]*)*$/
const immutableImagePattern = /^([^@\s]+)@sha256:([0-9a-f]{64})$/
const maximumReceiptBytes = 1024 * 1024
const wineCoreClrUserspaceComponentId = 'wine-coreclr-userspace'

export class RuntimeMatrixPromotionError extends Error {}

export function findRuntimeMatrixBinding(matrix, profileId) {
  for (const target of matrix.coreClr ?? []) {
    if (`${target.id}-linux-x64` === profileId) {
      return {
        target,
        capability: target.linuxCapability,
        profileId,
        targetId: target.id,
        platform: 'linux',
        family: 'coreclr',
      }
    }
    if (`wine-${target.id}-linux-x64` === profileId) {
      return {
        target,
        capability: target.wineCapability,
        profileId,
        targetId: target.id,
        platform: 'wine',
        family: 'coreclr-wine',
      }
    }
  }

  if (matrix.mono?.id === profileId) {
    return {
      target: matrix.mono,
      capability: matrix.mono.capability,
      profileId,
      targetId: matrix.mono.id,
      platform: 'mono',
      family: 'mono',
    }
  }

  for (const target of matrix.framework?.targets ?? []) {
    if (`wine-${target.id}-linux-x64` === profileId) {
      return {
        target,
        capability: target.capability,
        profileId,
        targetId: target.id,
        platform: 'framework',
        family: 'netfx-clr-wine',
      }
    }
  }

  throw new RuntimeMatrixPromotionError(
    `Runtime matrix has no platform binding for profile '${profileId}'.`,
  )
}

export function replaceFilesAtomically(replacements, options = {}) {
  const faultInjector = options.faultInjector ?? (() => {})
  const verifyApplied = options.verifyApplied ?? (() => {})
  const normalized = []
  const targetPaths = new Set()

  try {
    for (const replacement of replacements) {
      const targetPath = path.resolve(replacement.path)
      const comparisonPath = process.platform === 'win32' ? targetPath.toLowerCase() : targetPath
      if (targetPaths.has(comparisonPath)) {
        throw new RuntimeMatrixPromotionError(
          `Atomic replacement contains duplicate target '${targetPath}'.`,
        )
      }
      targetPaths.add(comparisonPath)

      const directory = path.dirname(targetPath)
      fs.mkdirSync(directory, { recursive: true })
      const nonce = crypto.randomUUID().replaceAll('-', '')
      const temporaryPath = path.join(directory, `.${path.basename(targetPath)}.${nonce}.tmp`)
      const backupPath = path.join(directory, `.${path.basename(targetPath)}.${nonce}.bak`)
      const file = fs.openSync(temporaryPath, 'wx', 0o600)
      try {
        fs.writeFileSync(file, replacement.content)
        fs.fchmodSync(file, fs.existsSync(targetPath) ? fs.statSync(targetPath).mode : 0o644)
        fs.fsyncSync(file)
      } finally {
        fs.closeSync(file)
      }
      normalized.push({
        targetPath,
        temporaryPath,
        backupPath,
        existed: fs.existsSync(targetPath),
        backedUp: false,
        applied: false,
      })
    }

    for (let index = 0; index < normalized.length; index += 1) {
      const entry = normalized[index]
      faultInjector('before-backup', index, entry)
      if (entry.existed) {
        fs.renameSync(entry.targetPath, entry.backupPath)
        entry.backedUp = true
      }
      faultInjector('after-backup', index, entry)
      fs.renameSync(entry.temporaryPath, entry.targetPath)
      entry.applied = true
      faultInjector('after-replace', index, entry)
    }
    verifyApplied(normalized)
  } catch (error) {
    const rollbackErrors = []
    for (const entry of normalized.toReversed()) {
      try {
        if (entry.applied && fs.existsSync(entry.targetPath)) fs.rmSync(entry.targetPath)
        if (entry.backedUp && fs.existsSync(entry.backupPath)) {
          fs.renameSync(entry.backupPath, entry.targetPath)
          entry.backedUp = false
        }
      } catch (rollbackError) {
        rollbackErrors.push(rollbackError)
      }
    }
    cleanupTemporaryFiles(normalized, { preserveBackups: rollbackErrors.length > 0 })
    if (rollbackErrors.length > 0) {
      throw new AggregateError([error, ...rollbackErrors], 'Runtime matrix promotion failed and rollback was incomplete; backup files were retained.')
    }
    throw error
  }

  // Failure to remove a backup does not create a partial promotion. Keep the
  // committed set authoritative and leave the harmless backup for diagnosis.
  cleanupTemporaryFiles(normalized, { preserveBackups: false, ignoreErrors: true })
}

export function prepareRuntimeMatrixPromotion({
  repositoryRoot,
  profileId,
  sourceRevision,
  generatorRunner = runMatrixGenerator,
  planSignaturePublicKey,
  planSignatureKeyId,
}) {
  const root = path.resolve(repositoryRoot)
  validateProfileId(profileId)
  const paths = promotionPaths(root, profileId)
  const originalBytes = readPromotionInputs(paths, root)
  const matrix = parseJson(originalBytes.matrix, paths.matrix)
  const binding = findRuntimeMatrixBinding(matrix, profileId)
  const receiptBytes = readRegularFile(paths.receipt, maximumReceiptBytes, root)
  const receiptDigest = sha256(receiptBytes)
  const receipt = parseJson(receiptBytes, paths.receipt)

  validateReceiptIdentity(receipt, binding, sourceRevision, root)
  const receiptReference = {
    path: `profiles/runtime-promotion-receipts/${profileId}.json`,
    sha256: receiptDigest,
  }
  binding.capability.promotionState = 'verified'
  binding.capability.promotionReceipt = receiptReference
  delete binding.capability.blockedReason
  const planSignatureOptions = { planSignaturePublicKey, planSignatureKeyId }
  requireValidPromotionReceipts(matrix, root, planSignatureOptions)
  materializeInstrumentationCapabilities(binding, receipt)
  originalBytes.receipt = receiptBytes
  originalBytes.evidence = readEvidenceInputs(root, binding, receipt)
  originalBytes.wineOperator = readWineOperatorInputs(root, binding, receipt, sourceRevision)

  const stageParent = path.join(root, 'artifacts', 'runtime-matrix-promotion')
  fs.mkdirSync(stageParent, { recursive: true })
  const stageRoot = fs.mkdtempSync(path.join(stageParent, `${profileId}-`))
  try {
    const catalog = parseJson(originalBytes.catalog, paths.catalog)
    const releaseLock = parseJson(originalBytes.releaseLock, paths.releaseLock)
    const deployment = parseJson(originalBytes.deployment, paths.deployment)
    requireReleaseIdentityClosure(catalog, releaseLock)

    const stagedCatalog = structuredClone(catalog)
    deactivateCatalogBinding(stagedCatalog, binding)
    stageGeneratorInputs(
      root,
      stageRoot,
      matrix,
      stagedCatalog,
      {
        profileId,
        receipt,
        sourceRevision,
        receiptBytes,
        candidateProfileBytes: originalBytes.candidateProfile,
        promotionPlanBytes: originalBytes.promotionPlan,
        preflightProfileBytes: originalBytes.preflightProfile,
        planSignatureOptions,
        evidence: originalBytes.evidence,
      },
    )
    generatorRunner({ repositoryRoot: root, stageRoot, planSignatureOptions })

    const generatedCatalogPath = path.join(stageRoot, 'profiles', 'catalog', 'catalog.json')
    const generatedProfilePath = path.join(stageRoot, 'profiles', 'runtimes', `${profileId}.json`)
    const generatedCatalog = parseJson(fs.readFileSync(generatedCatalogPath), generatedCatalogPath)
    const profile = parseJson(fs.readFileSync(generatedProfilePath), generatedProfilePath)
    const finalCatalog = mergePromotedCatalog(catalog, generatedCatalog, binding, receiptDigest)
    const finalLock = materializeReleaseLock(releaseLock, binding, receipt)
    const finalDeployment = materializeDeploymentImages(deployment, binding, receipt)
    const finalMatrix = matrix

    validateMaterializedClosure({
      binding,
      receipt,
      receiptReference,
      receiptBytes,
      matrix: finalMatrix,
      catalog: finalCatalog,
      profile,
      releaseLock: finalLock,
      deployment: finalDeployment,
    })

    const replacements = [
      ...[...originalBytes.wineOperator.entries()].map(([relativePath, content]) => ({
        path: path.join(root, ...relativePath.split('/')), content,
      })),
      { path: paths.deployment, content: serializeJson(finalDeployment) },
      { path: paths.activeProfile, content: serializeJson(profile) },
      { path: paths.catalog, content: serializeJson(finalCatalog) },
      { path: paths.releaseLock, content: serializeJson(finalLock) },
      // The verified matrix binding is the commit point and is replaced last.
      { path: paths.matrix, content: serializeJson(finalMatrix) },
    ]
    return {
      binding,
      receipt,
      receiptReference,
      sourceRevision,
      planSignatureOptions,
      replacements,
      originalBytes,
      stageRoot,
      paths,
    }
  } catch (error) {
    fs.rmSync(stageRoot, { recursive: true, force: true })
    throw error
  }
}

function materializeInstrumentationCapabilities(binding, receipt) {
  const verifiedCapabilities = new Set(receipt.checks.map(check => check.capability));
  const instrumentationCapabilities = binding.capability.capabilities.filter(
    capability =>
      (capability === 'inspection' || capability === 'execution-flow') &&
      verifiedCapabilities.has(capability),
  )
  if (instrumentationCapabilities.length === 0) {
    delete binding.capability.instrumentationCapabilities
    return
  }
  binding.capability.instrumentationCapabilities = instrumentationCapabilities
}

export function promoteRuntimeMatrix(options) {
  const root = path.resolve(options.repositoryRoot)
  const lockPath = path.join(root, 'artifacts', 'runtime-matrix-promotion', '.promotion.lock')
  fs.mkdirSync(path.dirname(lockPath), { recursive: true })
  let lockHandle
  let plan
  try {
    lockHandle = fs.openSync(lockPath, 'wx', 0o600)
    fs.writeFileSync(lockHandle, `${process.pid}${os.EOL}`)
    fs.fsyncSync(lockHandle)
    const sourceRevision = readPromotionRepositoryRevision(root, options.profileId, new Set(), options)
    plan = prepareRuntimeMatrixPromotion({ ...options, repositoryRoot: root, sourceRevision })
    assertPromotionInputsUnchanged(plan)
    requireEqual(readPromotionRepositoryRevision(root, options.profileId, new Set(), options), plan.sourceRevision, 'repository source revision')
    if (!options.check) {
      const atomicOptions = options.atomicOptions ?? {}
      replaceFilesAtomically(plan.replacements, {
        ...atomicOptions,
        verifyApplied(entries) {
          atomicOptions.verifyApplied?.(entries)
          const transactionTemporaries = new Set(entries.flatMap(entry =>
            [entry.temporaryPath, entry.backupPath]
              .filter(fs.existsSync)
              .map(filename => repositoryRelativePath(root, filename))))
          requireEqual(
            readPromotionRepositoryRevision(
              root,
              options.profileId,
              transactionTemporaries,
              options,
            ),
            plan.sourceRevision,
            'repository source revision after promotion',
          )
        },
      })
    }
    return plan
  } catch (error) {
    if (error?.code === 'EEXIST' && lockHandle === undefined) {
      throw new RuntimeMatrixPromotionError(
        `Another runtime matrix promotion holds '${lockPath}'.`,
      )
    }
    throw error
  } finally {
    if (plan?.stageRoot) fs.rmSync(plan.stageRoot, { recursive: true, force: true })
    if (lockHandle !== undefined) {
      fs.closeSync(lockHandle)
      fs.rmSync(lockPath, { force: true })
    }
  }
}

function promotionPaths(root, profileId) {
  return {
    matrix: path.join(root, 'profiles', 'runtime-matrix.json'),
    catalog: path.join(root, 'profiles', 'catalog', 'catalog.json'),
    releaseLock: path.join(root, 'profiles', 'lock.json'),
    deployment: path.join(root, 'deploy', 'images.json'),
    receipt: path.join(root, 'profiles', 'runtime-promotion-receipts', `${profileId}.json`),
    activeProfile: path.join(root, 'profiles', 'runtimes', `${profileId}.json`),
    candidateProfile: path.join(root, 'profiles', 'runtimes', 'candidates', `${profileId}.json`),
    promotionPlan: path.join(root, 'profiles', 'runtime-promotion-plans', `${profileId}.json`),
    promotionPlanSignature: path.join(root, 'profiles', 'runtime-promotion-plans', `${profileId}.json.sig`),
    preflightProfile: path.join(root, 'profiles', 'runtime-promotion-plans', `${profileId}.profile.json`),
  }
}

function readPromotionInputs(paths, root) {
  return {
    matrix: fs.readFileSync(paths.matrix),
    catalog: fs.readFileSync(paths.catalog),
    releaseLock: fs.readFileSync(paths.releaseLock),
    deployment: fs.readFileSync(paths.deployment),
    activeProfile: fs.existsSync(paths.activeProfile)
      ? fs.readFileSync(paths.activeProfile)
      : undefined,
    candidateProfile: readRegularFile(paths.candidateProfile, maximumReceiptBytes, root),
    promotionPlan: readOptionalRegularFile(paths.promotionPlan, maximumReceiptBytes, root),
    promotionPlanSignature: readOptionalRegularFile(paths.promotionPlanSignature, 4096, root),
    preflightProfile: readOptionalRegularFile(paths.preflightProfile, maximumReceiptBytes, root),
  }
}

function validateReceiptIdentity(receipt, binding, sourceRevision, root = undefined) {
  requireEqual(receipt.schemaVersion, 2, 'receipt.schemaVersion')
  requireEqual(receipt.profileId, binding.profileId, 'receipt.profileId')
  requireEqual(receipt.matrixTargetId, binding.targetId, 'receipt.matrixTargetId')
  requireEqual(receipt.platform, binding.platform, 'receipt.platform')
  requireEqual(receipt.family, binding.family, 'receipt.family')
  requireEqual(receipt.resolvedVersion, binding.target.version, 'receipt.resolvedVersion')
  if (!immutableImagePattern.test(receipt.image?.reference ?? '')) {
    throw new RuntimeMatrixPromotionError('Receipt image.reference must be an immutable repository@sha256:<64 lowercase hex> reference.')
  }
  if (!digestPattern.test(receipt.image?.imageId ?? '')) {
    throw new RuntimeMatrixPromotionError('Receipt image.imageId is not a canonical SHA-256 ID.')
  }
  if (receipt.componentIdentity === null || typeof receipt.componentIdentity !== 'object' ||
      Array.isArray(receipt.componentIdentity)) {
    throw new RuntimeMatrixPromotionError('Receipt componentIdentity is missing.')
  }
  if (!commitPattern.test(sourceRevision ?? '')) {
    throw new RuntimeMatrixPromotionError('The repository source revision is not a full Git commit.')
  }
  requireEqual(receipt.sourceRevision, sourceRevision, 'receipt.sourceRevision')
  try { validateWineOperatorBinding(receipt.wineOperator, binding.family, sourceRevision) } catch (error) {
    throw new RuntimeMatrixPromotionError(error.message, { cause: error })
  }
  if (root !== undefined && isWinePromotionFamily(binding.family)) {
    const loaded = loadOwnedWineOperatorBinding(root, sourceRevision)
    const operator = receipt.wineOperator
    for (const [field, value] of Object.entries({
      receiptPath: loaded.paths.receiptPath,
      receiptSha256: sha256(loaded.receiptBytes),
      signaturePath: loaded.paths.signaturePath,
      signatureSha256: sha256(loaded.signatureBytes),
      keyId: loaded.receipt.keyId,
      reference: loaded.receipt.operator.reference,
      imageId: loaded.receipt.operator.imageId,
      sizeBytes: loaded.receipt.operator.sizeBytes,
      sourceRevision: loaded.receipt.source.revision,
      sourceTree: loaded.receipt.source.tree,
    })) requireEqual(operator[field], value, `receipt.wineOperator.${field}`)
  }
}

function readWineOperatorInputs(root, binding, receipt, sourceRevision) {
  if (!isWinePromotionFamily(binding.family)) return new Map()
  validateReceiptIdentity(receipt, binding, sourceRevision, root)
  const paths = runtimeOperatorReceiptPaths(sourceRevision)
  return new Map([
    [paths.receiptPath, readRegularFile(path.join(root, ...paths.receiptPath.split('/')), maximumReceiptBytes, root)],
    [paths.signaturePath, readRegularFile(path.join(root, ...paths.signaturePath.split('/')), 4096, root)],
  ])
}

function stageGeneratorInputs(root, stageRoot, matrix, catalog, current) {
  writeJson(path.join(stageRoot, 'profiles', 'runtime-matrix.json'), matrix)
  writeJson(path.join(stageRoot, 'profiles', 'catalog', 'catalog.json'), catalog)

  const activeProfiles = path.join(root, 'profiles', 'runtimes')
  const stagedProfiles = path.join(stageRoot, 'profiles', 'runtimes')
  fs.mkdirSync(stagedProfiles, { recursive: true })
  for (const entry of fs.readdirSync(activeProfiles, { withFileTypes: true })) {
    if (entry.isFile() && entry.name.endsWith('.json')) {
      fs.copyFileSync(path.join(activeProfiles, entry.name), path.join(stagedProfiles, entry.name))
    }
  }
  stageVerifiedPromotionInputs(root, stageRoot, matrix, current)

  const stagedEng = path.join(stageRoot, 'eng', 'release')
  fs.mkdirSync(stagedEng, { recursive: true })
  for (const name of [
    'runtime-promotion-receipt-validation.mjs',
    'runtime-performance-evidence-validation.mjs',
    'runtime-capability-evidence-validation.mjs',
    'strict-owned-json.mjs',
    'json-schema-formats.mjs',
    'json-schema-instance-validation.mjs',
    'runtime-candidate-input-validation.mjs',
    'runtime-promotion-plan-signature.mjs',
    'runtime-wine-operator-binding.mjs',
    'wine-coreclr-operator-receipt.mjs',
  ]) {
    const sourceRelativePath = name === 'runtime-candidate-input-validation.mjs'
      ? `eng/${name}`
      : `eng/release/${name}`
    const source = path.join(root, ...sourceRelativePath.split('/'))
    const stat = fs.lstatSync(source, { throwIfNoEntry: false })
    if (stat === undefined || !stat.isFile() || stat.isSymbolicLink()) {
      throw new RuntimeMatrixPromotionError(
        `Staged generator helper '${sourceRelativePath}' must be a regular source-root file.`,
      )
    }
    fs.copyFileSync(source, path.join(stagedEng, name))
  }
  const trustSource = path.join(root, 'eng', 'profiles', 'trust', 'wine-coreclr-operator-receipt-public.pem')
  const trustTarget = path.join(stageRoot, 'eng', 'profiles', 'trust', 'wine-coreclr-operator-receipt-public.pem')
  const trustStat = fs.lstatSync(trustSource, { throwIfNoEntry: false })
  if (trustStat === undefined || !trustStat.isFile() || trustStat.isSymbolicLink()) {
    throw new RuntimeMatrixPromotionError("Staged generator trust key 'eng/profiles/trust/wine-coreclr-operator-receipt-public.pem' must be a regular source-root file.")
  }
  fs.mkdirSync(path.dirname(trustTarget), { recursive: true })
  fs.copyFileSync(trustSource, trustTarget)
  const planTrustSource = path.join(root, 'eng', 'profiles', 'trust', 'runtime-promotion-plan-public.pem')
  const planTrustTarget = path.join(stageRoot, 'eng', 'profiles', 'trust', 'runtime-promotion-plan-public.pem')
  fs.mkdirSync(path.dirname(planTrustTarget), { recursive: true })
  if (current.planSignatureOptions.planSignaturePublicKey === undefined) {
    fs.copyFileSync(planTrustSource, planTrustTarget)
  } else {
    fs.writeFileSync(planTrustTarget, current.planSignatureOptions.planSignaturePublicKey.export({ type: 'spki', format: 'pem' }))
  }
  const stagedSchemas = path.join(stageRoot, 'schemas')
  fs.mkdirSync(stagedSchemas, { recursive: true })
  for (const name of [
    'runtime-promotion-plan.schema.json',
    'runtime-promotion-receipt.schema.json',
  ]) {
    fs.copyFileSync(path.join(root, 'schemas', name), path.join(stagedSchemas, name))
  }

  // Fail before invoking the generator if a receipt names evidence outside
  // its canonical profile directory. The receipt validator repeats this gate.
  for (const check of current.receipt.checks ?? []) {
    const expected =
      `profiles/runtime-promotion-evidence/${current.profileId}/${check.capability}.json`
    requireEqual(check.evidencePath, expected, `${check.capability} evidencePath`)
  }
  requireEqual(current.receipt.performance?.evidencePath, `profiles/runtime-promotion-evidence/${current.profileId}/performance.json`, 'performance evidencePath')
  requireEqual(current.receipt.performance?.policyPath, `profiles/runtime-performance-policies/${current.receipt.performance?.policyId}.json`, 'performance policyPath')
}

function stageVerifiedPromotionInputs(root, stageRoot, matrix, current) {
  const inputs = new Map()
  const addInput = (relativePath, bytes) => {
    const existing = inputs.get(relativePath)
    if (existing !== undefined && !buffersEqual(existing, bytes)) {
      throw new RuntimeMatrixPromotionError(
        `Verified promotion input '${relativePath}' has conflicting bytes.`,
      )
    }
    inputs.set(relativePath, bytes)
  }
  let currentFound = false
  for (const binding of promotionBindings(matrix)) {
    if (binding.capability?.promotionState !== 'verified') continue
    const profileId = binding.profileId
    const isCurrent = profileId === current.profileId
    currentFound ||= isCurrent
    const receiptRelativePath = `profiles/runtime-promotion-receipts/${profileId}.json`
    const receiptBytes = isCurrent
      ? current.receiptBytes
      : readRegularFile(
          path.join(root, ...receiptRelativePath.split('/')),
          maximumReceiptBytes,
          root,
        )
    const receipt = isCurrent
      ? current.receipt
      : parseJson(receiptBytes, receiptRelativePath)
    validateReceiptIdentity(receipt, binding, current.sourceRevision)
    requireEqual(
      binding.capability.promotionReceipt?.path,
      receiptRelativePath,
      `${profileId} matrix receipt path`,
    )
    requireEqual(
      binding.capability.promotionReceipt?.sha256,
      sha256(receiptBytes),
      `${profileId} matrix receipt sha256`,
    )
    addInput(receiptRelativePath, receiptBytes)
    for (const [relativePath, bytes] of readWineOperatorInputs(
      root, binding, receipt, current.sourceRevision,
    )) addInput(relativePath, bytes)

    const planBinding = validatePromotionPlanBinding(
      root,
      profileId,
      receipt,
      current.sourceRevision,
      current.planSignatureOptions,
    )
    const currentPlanInputs = [
      current.promotionPlanBytes,
      current.preflightProfileBytes,
    ]
    if (isCurrent && currentPlanInputs.some(bytes => bytes === undefined)) {
      throw new RuntimeMatrixPromotionError('Promotion plan and preflight Runtime Profile must both be present.')
    }
    const exactBytes = (input, captured) => {
      if (!isCurrent) return input.bytes
      if (!buffersEqual(input.bytes, captured)) {
        throw new RuntimeMatrixPromotionError(`Promotion input '${input.relativePath}' changed before generator staging.`)
      }
      return captured
    }
    addInput(
      planBinding.candidate.relativePath,
      exactBytes(planBinding.candidate, current.candidateProfileBytes),
    )
    addInput(
      planBinding.plan.relativePath,
      exactBytes(planBinding.plan, current.promotionPlanBytes),
    )
    addInput(
      planBinding.preflight.relativePath,
      exactBytes(planBinding.preflight, current.preflightProfileBytes),
    )
    addInput(planBinding.signature.relativePath, planBinding.signature.bytes)

    const evidence = isCurrent
      ? current.evidence
      : readEvidenceInputs(root, binding, receipt)
    for (const [filename, bytes] of evidence) {
      addInput(repositoryRelativePath(root, filename), bytes)
    }
  }
  if (!currentFound) {
    throw new RuntimeMatrixPromotionError(
      `Current promotion '${current.profileId}' is absent from the verified matrix closure.`,
    )
  }
  for (const [relativePath, bytes] of inputs) {
    const target = path.join(stageRoot, ...relativePath.split('/'))
    fs.mkdirSync(path.dirname(target), { recursive: true })
    fs.writeFileSync(target, bytes)
  }
}

function runMatrixGenerator({ repositoryRoot, stageRoot }) {
  const generator = path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs')
  const result = spawnSync(
    'dotnet',
    [
      'run', generator, '--',
      '--repository-root', stageRoot,
      '--matrix', path.join(stageRoot, 'profiles', 'runtime-matrix.json'),
      '--catalog', path.join(stageRoot, 'profiles', 'catalog', 'catalog.json'),
      '--profiles', path.join(stageRoot, 'profiles', 'runtimes'),
      '--overwrite-profiles',
      '--allow-active-profile-overwrite',
    ],
    {
      cwd: repositoryRoot,
      env: process.env,
      encoding: 'utf8',
      timeout: 120_000,
      windowsHide: true,
    },
  )
  if (result.status !== 0) {
    throw new RuntimeMatrixPromotionError(
      `Runtime matrix generator failed.\n${result.stdout ?? ''}${result.stderr ?? ''}`,
    )
  }
}

function deactivateCatalogBinding(catalog, binding) {
  const referenceSetId = promotedReferenceSetId(binding)
  for (const runtime of catalog.runtimes ?? []) {
    if (runtime.id === binding.profileId) {
      runtime.availability = unavailable('staged runtime promotion')
      runtime.capabilities = []
    }
  }
  for (const reference of catalog.referenceSets ?? []) {
    if (reference.id === referenceSetId) {
      reference.availability = unavailable('staged runtime promotion')
    }
  }
  for (const preset of catalog.presets ?? []) {
    if (preset.defaultRuntimeId === binding.profileId) {
      preset.availability = unavailable('staged runtime promotion')
    }
  }
  for (const rule of catalog.compatibility ?? []) {
    if (rule.toId === binding.profileId || rule.toId === referenceSetId) {
      rule.allowed = false
      rule.reason = 'staged runtime promotion'
    }
  }
}

function mergePromotedCatalog(original, generated, binding, receiptDigest) {
  const result = structuredClone(original)
  const generatedRuntime = requireById(generated.runtimes, binding.profileId, 'runtime')
  replaceById(result.runtimes, generatedRuntime)

  const referenceSetId = promotedReferenceSetId(binding)
  if (referenceSetId !== undefined) {
    replaceById(
      result.referenceSets,
      requireById(generated.referenceSets, referenceSetId, 'reference set'),
    )
  }

  result.compatibility = (result.compatibility ?? []).filter(rule =>
    !(rule.kind === 'artifact-runtime' && rule.toId === binding.profileId) &&
    !(referenceSetId !== undefined &&
      rule.kind === 'toolchain-reference-set' && rule.toId === referenceSetId))
  result.compatibility.push(...(generated.compatibility ?? []).filter(rule =>
    rule.kind === 'artifact-runtime' && rule.toId === binding.profileId ||
    referenceSetId !== undefined &&
      rule.kind === 'toolchain-reference-set' && rule.toId === referenceSetId))

  result.presets = (result.presets ?? []).filter(preset => preset.defaultRuntimeId !== binding.profileId)
  result.presets.push(...(generated.presets ?? []).filter(
    preset => preset.defaultRuntimeId === binding.profileId,
  ))
  result.revision = `runtime-promotion-${receiptDigest.slice('sha256:'.length, 23)}`
  return result
}

function materializeReleaseLock(releaseLock, binding, receipt) {
  const result = structuredClone(releaseLock)
  if (result.components === null || typeof result.components !== 'object' ||
      Array.isArray(result.components)) {
    throw new RuntimeMatrixPromotionError('Release lock components are missing.')
  }

  const target = binding.target
  if (binding.family === 'coreclr' || binding.family === 'coreclr-wine') {
    const payload = binding.platform === 'linux' ? target.linux : target.windows
    if (!payload || !/^[0-9a-f]{128}$/.test(payload.sha512 ?? '')) {
      throw new RuntimeMatrixPromotionError(
        `Runtime '${binding.profileId}' has no locked ${binding.platform} payload SHA-512.`,
      )
    }
    result.components[binding.profileId] = withoutUndefined({
      kind: 'runtime',
      resolvedVersion: target.version,
      commit: receipt.runtimeIdentity?.runtimeCommit,
      jitCommit: receipt.runtimeIdentity?.jitCommit,
      sourceUri: receipt.componentIdentity.sourceUri,
      sha512: removeDigestPrefix(receipt.componentIdentity.sourceDigest, 'sha512:'),
      releaseDate: target.releaseDate,
    })
  } else {
    const component = receipt.componentIdentity
    if (!digestPattern.test(component?.sourceDigest ?? '') ||
        typeof component?.sourceUri !== 'string' || component.sourceUri.length === 0) {
      throw new RuntimeMatrixPromotionError(
        `Operator runtime '${binding.profileId}' cannot be promoted until its receipt binds ` +
        'componentIdentity.sourceUri and componentIdentity.sourceDigest from inspected image labels.',
      )
    }
    result.components[binding.profileId] = {
      kind: 'runtime',
      resolvedVersion: target.version,
      digest: component.sourceDigest,
      sourceUri: component.sourceUri,
    }
  }

  if (target.referencePackage !== undefined) {
    const reference = target.referencePackage
    result.components[target.referenceSetId] = {
      kind: 'reference-set',
      resolvedVersion: reference.version,
      sourceUri: reference.url,
      package: reference.id,
      packageContentHash: reference.packageContentHash,
      sha512: reference.sha512,
    }
  }
  else if (target.referenceComposition !== undefined) {
    const composition = target.referenceComposition
    if (composition.kind !== 'nuget-package-composition' ||
        composition.resolvedVersion !== 'net30-union-v1' ||
        !digestPattern.test(composition.sourceIdentityDigest ?? '')) {
      throw new RuntimeMatrixPromotionError(
        `Reference composition '${target.referenceSetId}' has an invalid locked identity.`,
      )
    }
    result.components[target.referenceSetId] = {
      kind: 'reference-set',
      resolvedVersion: composition.resolvedVersion,
      digest: composition.sourceIdentityDigest,
    }
  }

  result.components = Object.fromEntries(
    Object.entries(result.components).sort(([left], [right]) => left.localeCompare(right)),
  )
  return result
}

function promotedReferenceSetId(binding) {
  const hasPackage = binding.target.referencePackage !== undefined
  const hasComposition = binding.target.referenceComposition !== undefined
  if (hasPackage && hasComposition) {
    throw new RuntimeMatrixPromotionError(
      `Runtime target '${binding.target.id}' defines more than one reference source.`,
    )
  }
  return hasPackage || hasComposition ? binding.target.referenceSetId : undefined
}

function materializeDeploymentImages(deployment, binding, receipt) {
  const result = structuredClone(deployment)
  if (!Array.isArray(result.images)) {
    throw new RuntimeMatrixPromotionError('Deployment image manifest images are missing.')
  }
  const repository = deploymentRepository(receipt.image.reference)
  const matches = result.images
    .map((image, index) => ({ image, index }))
    .filter(({ image }) => image.id === binding.profileId || image.runtimeId === binding.profileId)
  if (matches.length > 1) {
    throw new RuntimeMatrixPromotionError(
      `Deployment manifest has multiple definitions for runtime '${binding.profileId}'.`,
    )
  }
  const existing = matches[0]?.image ?? {}
  if (existing.runtimeId !== undefined && existing.runtimeId !== binding.profileId) {
    throw new RuntimeMatrixPromotionError(
      `Deployment image ID '${binding.profileId}' is already bound to runtime '${existing.runtimeId}'.`,
    )
  }
  const definition = {
    ...existing,
    id: binding.profileId,
    repository,
    immutableReference: receipt.image.reference,
    runtimeId: binding.profileId,
    lockComponentId: binding.profileId,
    lockComponentIds: deploymentLockComponentIds(binding, existing.lockComponentIds),
  }
  if (matches.length === 0) result.images.push(definition)
  else result.images[matches[0].index] = definition
  return result
}

export function deploymentLockComponentIds(binding, existingComponentIds = []) {
  if (!Array.isArray(existingComponentIds) ||
      !existingComponentIds.every(componentId => typeof componentId === 'string' && componentId.length > 0)) {
    throw new RuntimeMatrixPromotionError('Deployment image lockComponentIds must be an array of non-empty strings.')
  }
  const requiresWineUserspace = binding?.family === 'coreclr-wine' ||
    binding?.family === 'netfx-clr-wine'
  return requiresWineUserspace
    ? existingComponentIds.includes(wineCoreClrUserspaceComponentId)
      ? [...existingComponentIds]
      : [...existingComponentIds, wineCoreClrUserspaceComponentId]
    : [...existingComponentIds]
}

function validateMaterializedClosure(material) {
  const {
    binding,
    receipt,
    receiptReference,
    receiptBytes,
    matrix,
    catalog,
    profile,
    releaseLock,
    deployment,
  } = material
  const rebound = findRuntimeMatrixBinding(matrix, binding.profileId)
  requireEqual(rebound.capability.promotionState, 'verified', 'matrix promotionState')
  requireJsonEqual(rebound.capability.promotionReceipt, receiptReference, 'matrix promotionReceipt')
  requireEqual(sha256(receiptBytes), receiptReference.sha256, 'receipt SHA-256')

  requireEqual(profile.id, binding.profileId, 'active profile id')
  requireEqual(profile.image, receipt.image.reference, 'active profile image')
  requireEqual(profile.runtimeImageId, receipt.image.imageId, 'active profile runtimeImageId')
  requireEqual(profile.runtimeVersion, binding.target.version, 'active profile runtimeVersion')
  requireJsonEqual(profile.promotionReceipt, receiptReference, 'active profile promotionReceipt')
  validateProfileOperationClosure(profile, receipt)
  if (profile.image.includes(':candidate') || profile.image.startsWith('sha256:')) {
    throw new RuntimeMatrixPromotionError('Active profile image must use the receipt registry digest, not a candidate tag or local image ID.')
  }

  const runtime = requireById(catalog.runtimes, binding.profileId, 'Catalog runtime')
  requireEqual(runtime.resolvedVersion, binding.target.version, 'Catalog runtime version')
  requireEqual(runtime.runtimeImageId, receipt.image.imageId, 'Catalog runtime image identity')
  if (runtime.availability?.installed !== true || runtime.availability?.health !== 'healthy') {
    throw new RuntimeMatrixPromotionError('Promoted Catalog runtime is not selectable and healthy.')
  }

  const component = releaseLock.components?.[binding.profileId]
  requireEqual(component?.kind, 'runtime', 'release lock runtime kind')
  requireEqual(component?.resolvedVersion, binding.target.version, 'release lock runtime version')
  if ('imageId' in (component ?? {})) {
    throw new RuntimeMatrixPromotionError('Source release lock must not store a local Docker image ID.')
  }
  if (binding.family === 'coreclr' || binding.family === 'coreclr-wine') {
    requireEqual(component.commit, receipt.runtimeIdentity.runtimeCommit, 'release lock runtime commit')
    requireEqual(component.jitCommit, receipt.runtimeIdentity.jitCommit, 'release lock JIT commit')
  }

  const deploymentImage = (deployment.images ?? []).find(
    image => image.runtimeId === binding.profileId,
  )
  if (!deploymentImage) {
    throw new RuntimeMatrixPromotionError('Promoted runtime has no deployment image definition.')
  }
  requireEqual(
    deploymentImage.immutableReference,
    receipt.image.reference,
    'deployment immutableReference',
  )
  requireEqual(
    deploymentImage.repository,
    deploymentRepository(receipt.image.reference),
    'deployment repository',
  )
  if ('imageId' in deploymentImage) {
    throw new RuntimeMatrixPromotionError('Deployment manifest must use an immutable registry reference, not a local image ID.')
  }
  if (binding.family === 'coreclr-wine' || binding.family === 'netfx-clr-wine') {
    if (releaseLock.components?.[wineCoreClrUserspaceComponentId] === undefined) {
      throw new RuntimeMatrixPromotionError(
        `Wine deployment closure is missing '${wineCoreClrUserspaceComponentId}' from the release lock.`,
      )
    }
    if (!deploymentImage.lockComponentIds?.includes(wineCoreClrUserspaceComponentId)) {
      throw new RuntimeMatrixPromotionError(
        `Wine deployment image '${binding.profileId}' is missing '${wineCoreClrUserspaceComponentId}'.`,
      )
    }
  }
}

function validateProfileOperationClosure(profile, receipt) {
  const receiptOperations = receipt.operations ?? {}
  const profileOperations = profile.operations ?? {}
  for (const [operationName, helper] of Object.entries(receiptOperations)) {
    const operation = profileOperations[operationName]
    if (operation === null || typeof operation !== 'object' || Array.isArray(operation)) {
      throw new RuntimeMatrixPromotionError(
        `Active profile operation '${operationName}' is missing.`,
      )
    }
    requireEqual(
      operation.implementationId,
      helper.implementation,
      `active profile ${operationName} implementation`,
    )
    if (operationName === 'run') {
      requireEqual(
        profile.layout?.runnerAssemblyPath,
        helper.assemblyPath,
        'active profile runner assembly path',
      )
    } else if (operationName === 'jit') {
      requireEqual(
        profile.layout?.jitInspectorAssemblyPath ?? profile.layout?.runnerAssemblyPath,
        helper.assemblyPath,
        'active profile JIT inspector assembly path',
      )
    }
    requireEqual(
      operation.profilerPath,
      helper.profilerPath,
      `active profile ${operationName} profiler path`,
    )
  }
  const unexpected = Object.keys(profileOperations).filter(name => !(name in receiptOperations))
  if (unexpected.length > 0) {
    throw new RuntimeMatrixPromotionError(
      `Active profile has operation(s) absent from its receipt: ${unexpected.join(', ')}.`,
    )
  }
}

function requireReleaseIdentityClosure(catalog, releaseLock) {
  requireEqual(catalog.releaseId, releaseLock.releaseId, 'Catalog/release lock releaseId')
}

function assertPromotionInputsUnchanged(plan) {
  for (const [name, filePath] of Object.entries({
    matrix: plan.paths.matrix,
    catalog: plan.paths.catalog,
    releaseLock: plan.paths.releaseLock,
    deployment: plan.paths.deployment,
  })) {
    const current = fs.readFileSync(filePath)
    if (!buffersEqual(current, plan.originalBytes[name])) {
      throw new RuntimeMatrixPromotionError(
        `Promotion input '${filePath}' changed while staged material was being generated.`,
      )
    }
  }
  const repositoryRoot = path.dirname(path.dirname(plan.paths.matrix))
  const currentCandidateProfile = readRegularFile(
    plan.paths.candidateProfile,
    maximumReceiptBytes,
    repositoryRoot,
  )
  if (!buffersEqual(currentCandidateProfile, plan.originalBytes.candidateProfile)) {
    throw new RuntimeMatrixPromotionError(
      `Candidate profile '${plan.paths.candidateProfile}' changed while promotion was staged.`,
    )
  }
  for (const [name, filePath] of [
    ['promotionPlan', plan.paths.promotionPlan],
    ['promotionPlanSignature', plan.paths.promotionPlanSignature],
    ['preflightProfile', plan.paths.preflightProfile],
  ]) {
    const original = plan.originalBytes[name]
    const exists = fs.existsSync(filePath)
    if (exists !== (original !== undefined)) {
      throw new RuntimeMatrixPromotionError(
        `Promotion input '${filePath}' changed while promotion was staged.`,
      )
    }
    if (exists) {
      const current = readRegularFile(filePath, maximumReceiptBytes, repositoryRoot)
      if (!buffersEqual(current, original)) {
        throw new RuntimeMatrixPromotionError(
          `Promotion input '${filePath}' changed while promotion was staged.`,
        )
      }
    }
  }
  const activeExists = fs.existsSync(plan.paths.activeProfile)
  const originalProfile = plan.originalBytes.activeProfile
  if (activeExists !== (originalProfile !== undefined) ||
      activeExists && !buffersEqual(fs.readFileSync(plan.paths.activeProfile), originalProfile)) {
    throw new RuntimeMatrixPromotionError(
      `Active profile '${plan.paths.activeProfile}' changed while promotion was staged.`,
    )
  }
  const currentReceipt = readRegularFile(plan.paths.receipt, maximumReceiptBytes, path.dirname(plan.paths.receipt))
  requireEqual(sha256(currentReceipt), plan.receiptReference.sha256, 'promotion receipt SHA-256')
  if (!buffersEqual(currentReceipt, plan.originalBytes.receipt)) {
    throw new RuntimeMatrixPromotionError('Promotion receipt changed while material was staged.')
  }
  for (const [evidencePath, original] of plan.originalBytes.evidence) {
    const current = readRegularFile(evidencePath, maximumReceiptBytes, path.dirname(evidencePath))
    if (!buffersEqual(current, original)) {
      throw new RuntimeMatrixPromotionError(
        `Promotion evidence '${evidencePath}' changed while material was staged.`,
      )
    }
  }
  for (const [relativePath, original] of plan.originalBytes.wineOperator) {
    const current = readRegularFile(
      path.join(repositoryRoot, ...relativePath.split('/')),
      maximumReceiptBytes,
      repositoryRoot,
    )
    if (!buffersEqual(current, original)) {
      throw new RuntimeMatrixPromotionError(`Wine operator input '${relativePath}' changed while material was staged.`)
    }
  }
  const currentMatrix = parseJson(
    plan.replacements.at(-1).content,
    plan.paths.matrix,
  )
  requireValidPromotionReceipts(
    currentMatrix,
    path.resolve(path.dirname(plan.paths.matrix), '..'),
    plan.planSignatureOptions,
  )
}

function readEvidenceInputs(root, binding, receipt) {
  const result = new Map()
  for (const check of receipt.checks ?? []) {
    const expectedPath =
      `profiles/runtime-promotion-evidence/${binding.profileId}/${check.capability}.json`
    requireEqual(check.evidencePath, expectedPath, `${check.capability} evidencePath`)
    const fullPath = path.join(root, ...expectedPath.split('/'))
    result.set(fullPath, readRegularFile(fullPath, maximumReceiptBytes, root))
  }
  const performanceEvidencePath =
    `profiles/runtime-promotion-evidence/${binding.profileId}/performance.json`
  requireEqual(
    receipt.performance?.evidencePath,
    performanceEvidencePath,
    'performance evidencePath',
  )
  const performanceEvidenceFullPath = path.join(root, ...performanceEvidencePath.split('/'))
  result.set(
    performanceEvidenceFullPath,
    readRegularFile(performanceEvidenceFullPath, maximumReceiptBytes, root),
  )
  const performancePolicyPath =
    `profiles/runtime-performance-policies/${receipt.performance?.policyId}.json`
  requireEqual(receipt.performance?.policyPath, performancePolicyPath, 'performance policyPath')
  const performancePolicyFullPath = path.join(root, ...performancePolicyPath.split('/'))
  result.set(
    performancePolicyFullPath,
    readRegularFile(performancePolicyFullPath, maximumReceiptBytes, root),
  )
  return result
}

function requireValidPromotionReceipts(matrix, root, options = {}) {
  const failures = validateRuntimePromotionReceipts(matrix, root, fs.readFileSync, options)
  if (failures.length > 0) throw new RuntimeMatrixPromotionError(`Runtime promotion receipt validation failed: ${failures.join(' ')}`);
}

function buffersEqual(left, right) { return left.length === right.length && crypto.timingSafeEqual(left, right); }

function readRepositoryRevision(root) {
  const result = spawnSync('git', ['-C', root, 'rev-parse', 'HEAD'], {
    encoding: 'utf8',
    timeout: 10_000,
    windowsHide: true,
  })
  const revision = result.status === 0 ? result.stdout.trim() : ''
  if (!commitPattern.test(revision)) {
    throw new RuntimeMatrixPromotionError(
      `Could not resolve a full source revision for repository '${root}'.`,
    )
  }
  return revision
}

function readPromotionRepositoryRevision(root, profileId, transactionTemporaries = new Set(), options = {}) {
  const revision = readRepositoryRevision(root)
  const allowed = derivePromotionDirtyPaths(root, profileId, revision, options)
  for (const relativePath of transactionTemporaries) allowed.add(relativePath)
  const result = spawnSync(
    'git',
    ['-C', root, 'status', '--porcelain=v1', '-z', '--untracked-files=all'],
    {
      encoding: 'utf8',
      timeout: 10_000,
      windowsHide: true,
    },
  )
  if (result.status !== 0) {
    throw new RuntimeMatrixPromotionError(
      `Could not inspect the Git worktree for repository '${root}'.`,
    )
  }
  for (const entry of parseGitStatus(result.stdout)) {
    if (!allowed.has(entry.path)) {
      throw new RuntimeMatrixPromotionError(
        `Repository change '${entry.path}' is outside the exact verified runtime promotion transaction.`,
      )
    }
  }
  return revision
}

function parseGitStatus(output) {
  const entries = []
  const records = output.split('\0')
  for (let index = 0; index < records.length; index += 1) {
    const record = records[index]
    if (record.length === 0) continue
    if (record.length < 4 || record[2] !== ' ') {
      throw new RuntimeMatrixPromotionError('Git returned a malformed promotion worktree status.')
    }
    const status = record.slice(0, 2)
    if (/[RC]/.test(status)) {
      throw new RuntimeMatrixPromotionError('Renamed or copied paths are not allowed in a runtime promotion transaction.')
    }
    const relativePath = record.slice(3)
    if (relativePath.includes('\\') || path.isAbsolute(relativePath) ||
        relativePath.split('/').some(segment => segment === '' || segment === '.' || segment === '..')) {
      throw new RuntimeMatrixPromotionError(
        `Git returned non-canonical promotion path '${relativePath}'.`,
      )
    }
    entries.push({ status, path: relativePath })
  }
  return entries
}

function derivePromotionDirtyPaths(root, profileId, sourceRevision, options = {}) {
  validateProfileId(profileId)
  const matrixPath = path.join(root, 'profiles', 'runtime-matrix.json')
  const matrix = parseJson(fs.readFileSync(matrixPath), matrixPath)
  const verifiedIds = promotionBindings(matrix)
    .filter(binding => binding.capability?.promotionState === 'verified')
    .map(binding => binding.profileId)
  const transactionIds = [...new Set([...verifiedIds, profileId])].sort()
  const allowed = new Set()
  for (const transactionId of transactionIds) {
    const binding = findRuntimeMatrixBinding(matrix, transactionId)
    const receiptRelativePath =
      `profiles/runtime-promotion-receipts/${transactionId}.json`
    const receiptPath = path.join(root, ...receiptRelativePath.split('/'))
    const receiptBytes = readRegularFile(receiptPath, maximumReceiptBytes, root)
    const receipt = parseJson(receiptBytes, receiptPath)
    validateReceiptIdentity(receipt, binding, sourceRevision, root)
    if (verifiedIds.includes(transactionId)) {
      requireEqual(
        binding.capability.promotionReceipt?.path,
        receiptRelativePath,
        `${transactionId} matrix receipt path`,
      )
      requireEqual(
        binding.capability.promotionReceipt?.sha256,
        sha256(receiptBytes),
        `${transactionId} matrix receipt sha256`,
      )
    }
    allowed.add(receiptRelativePath)
    for (const relativePath of readWineOperatorInputs(root, binding, receipt, sourceRevision).keys()) {
      allowed.add(relativePath)
    }
    for (const evidencePath of readEvidenceInputs(root, binding, receipt).keys()) {
      allowed.add(repositoryRelativePath(root, evidencePath))
    }
    const planBinding = validatePromotionPlanBinding(
      root,
      transactionId,
      receipt,
      sourceRevision,
      options,
    )
    allowed.add(planBinding.plan.relativePath)
    allowed.add(planBinding.preflight.relativePath)
    allowed.add(planBinding.signature.relativePath)
  }

  if (verifiedIds.length > 0) {
    validateExistingMaterializedPromotions(root, matrix, verifiedIds, options)
    for (const relativePath of [
      'profiles/runtime-matrix.json',
      'profiles/catalog/catalog.json',
      'profiles/lock.json',
      'deploy/images.json',
      ...verifiedIds.map(id => `profiles/runtimes/${id}.json`),
    ]) {
      allowed.add(relativePath)
    }
  }
  return allowed
}

function promotionBindings(matrix) {
  const result = []
  for (const target of matrix.coreClr ?? []) {
    result.push(findRuntimeMatrixBinding(matrix, `${target.id}-linux-x64`))
    result.push(findRuntimeMatrixBinding(matrix, `wine-${target.id}-linux-x64`))
  }
  if (matrix.mono !== undefined) result.push(findRuntimeMatrixBinding(matrix, matrix.mono.id))
  for (const target of matrix.framework?.targets ?? []) {
    result.push(findRuntimeMatrixBinding(matrix, `wine-${target.id}-linux-x64`))
  }
  return result
}

function validatePromotionPlanBinding(root, profileId, receipt, sourceRevision, options = {}) {
  const planRelativePath = `profiles/runtime-promotion-plans/${profileId}.json`
  const preflightRelativePath = `profiles/runtime-promotion-plans/${profileId}.profile.json`
  const candidateRelativePath = `profiles/runtimes/candidates/${profileId}.json`
  const planBytes = readRegularFile(path.join(root, ...planRelativePath.split('/')), maximumReceiptBytes, root)
  requireEqual(sha256(planBytes), receipt.planSha256, `${profileId} promotion plan sha256`)
  const plan = parseJson(planBytes, planRelativePath)
  requireJsonEqual(planBytes.toString('utf8'), serializeRuntimePromotionPlan(plan).toString('utf8'), `${profileId} canonical promotion plan`)
  const signatureRelativePath = runtimePromotionPlanSignaturePath(profileId)
  const signatureBytes = readRegularFile(path.join(root, ...signatureRelativePath.split('/')), 4096, root)
  requireEqual(receipt.planSignature?.path, signatureRelativePath, `${profileId} promotion plan signature path`)
  requireEqual(receipt.planSignature?.sha256, sha256(signatureBytes), `${profileId} promotion plan signature sha256`)
  requireEqual(receipt.planSignature?.keyId, options.planSignatureKeyId ?? runtimePromotionPlanKeyId, `${profileId} promotion plan signature keyId`)
  try { verifyRuntimePromotionPlanSignature(planBytes, signatureBytes,
    options.planSignaturePublicKey === undefined
      ? {}
      : { publicKey: options.planSignaturePublicKey, keyId: options.planSignatureKeyId }) } catch (error) {
    throw new RuntimeMatrixPromotionError(`${profileId} promotion plan signature is invalid: ${error.message}`, { cause: error })
  }
  requireEqual(plan.schemaVersion, 1, `${profileId} promotion plan schemaVersion`)
  requireEqual(plan.profileId, profileId, `${profileId} promotion plan profileId`)
  requireEqual(plan.sourceRevision, sourceRevision, `${profileId} promotion plan sourceRevision`)
  requireJsonEqual(plan.image, receipt.image, `${profileId} promotion plan image`)
  requireJsonEqual(
    plan.runtimeIdentity,
    receipt.runtimeIdentity,
    `${profileId} promotion plan runtimeIdentity`,
  )
  requireJsonEqual(
    plan.componentIdentity,
    receipt.componentIdentity,
    `${profileId} promotion plan componentIdentity`,
  )
  requireJsonEqual(plan.wineOperator, receipt.wineOperator, `${profileId} promotion plan wineOperator`)
  requireEqual(
    plan.preflightProfile?.path,
    preflightRelativePath,
    `${profileId} promotion plan preflightProfile.path`,
  )
  const preflightBytes = readRegularFile(
    path.join(root, ...preflightRelativePath.split('/')),
    maximumReceiptBytes,
    root,
  )
  requireEqual(
    plan.preflightProfile?.sha256,
    sha256(preflightBytes),
    `${profileId} promotion plan preflightProfile.sha256`,
  )
  const preflight = parseJson(preflightBytes, preflightRelativePath)
  requireEqual(preflight.id, profileId, `${profileId} immutable preflight profile id`)
  requireEqual(preflight.image, receipt.image.reference, `${profileId} preflight image`)
  requireEqual(preflight.runtimeImageId, receipt.image.imageId, `${profileId} preflight image ID`)
  if (preflight.promotionReceipt !== undefined) {
    throw new RuntimeMatrixPromotionError(
      `Immutable preflight profile '${profileId}' cannot contain a promotion receipt.`,
    )
  }
  const candidateBytes = readRegularFile(
    path.join(root, ...candidateRelativePath.split('/')),
    maximumReceiptBytes,
    root,
  )
  requireEqual(plan.profileSha256, sha256(candidateBytes), `${profileId} candidate profile sha256`)
  return {
    plan: { relativePath: planRelativePath, bytes: planBytes },
    signature: { relativePath: signatureRelativePath, bytes: signatureBytes },
    preflight: { relativePath: preflightRelativePath, bytes: preflightBytes },
    candidate: { relativePath: candidateRelativePath, bytes: candidateBytes },
  }
}

function validateExistingMaterializedPromotions(root, matrix, profileIds, options = {}) {
  const failures = validateRuntimePromotionReceipts(matrix, root, fs.readFileSync, options)
  if (failures.length > 0) {
    throw new RuntimeMatrixPromotionError(
      `Existing runtime promotion receipt validation failed: ${failures.join(' ')}`,
    )
  }
  const catalog = parseJson(
    fs.readFileSync(path.join(root, 'profiles', 'catalog', 'catalog.json')),
    'profiles/catalog/catalog.json',
  )
  const releaseLock = parseJson(
    fs.readFileSync(path.join(root, 'profiles', 'lock.json')),
    'profiles/lock.json',
  )
  const deployment = parseJson(
    fs.readFileSync(path.join(root, 'deploy', 'images.json')),
    'deploy/images.json',
  )
  for (const profileId of profileIds) {
    const binding = findRuntimeMatrixBinding(matrix, profileId)
    const receiptRelativePath = `profiles/runtime-promotion-receipts/${profileId}.json`
    const receiptBytes = readRegularFile(
      path.join(root, ...receiptRelativePath.split('/')),
      maximumReceiptBytes,
      root,
    )
    const receipt = parseJson(receiptBytes, receiptRelativePath)
    const receiptReference = binding.capability.promotionReceipt
    const profilePath = path.join(root, 'profiles', 'runtimes', `${profileId}.json`)
    validateMaterializedClosure({
      binding,
      receipt,
      receiptReference,
      receiptBytes,
      matrix,
      catalog,
      profile: parseJson(fs.readFileSync(profilePath), profilePath),
      releaseLock,
      deployment,
    })
  }
}

function repositoryRelativePath(root, filename) {
  const relativePath = path.relative(root, filename).replaceAll('\\', '/')
  if (relativePath.length === 0 || relativePath === '..' || relativePath.startsWith('../') ||
      path.isAbsolute(relativePath)) {
    throw new RuntimeMatrixPromotionError(`Promotion path '${filename}' escapes the repository.`)
  }
  return relativePath
}

function deploymentRepository(reference) {
  const match = immutableImagePattern.exec(reference)
  if (!match) throw new RuntimeMatrixPromotionError('Image reference is not immutable.')
  let repository = match[1]
  const lastSlash = repository.lastIndexOf('/')
  const tagSeparator = repository.lastIndexOf(':')
  if (tagSeparator > lastSlash) repository = repository.slice(0, tagSeparator)
  const firstSlash = repository.indexOf('/')
  const portSeparator = firstSlash < 0 ? -1 : repository.lastIndexOf(':', firstSlash)
  const port = portSeparator < 0
    ? undefined
    : Number(repository.slice(portSeparator + 1, firstSlash))
  if (!deploymentRepositoryPattern.test(repository) ||
      port !== undefined && (!Number.isSafeInteger(port) || port < 1 || port > 65535)) {
    throw new RuntimeMatrixPromotionError(
      `Image repository '${repository}' cannot be represented by deploy/images.json.`,
    )
  }
  return repository
}

function replaceById(values, replacement) {
  const index = (values ?? []).findIndex(value => value.id === replacement.id)
  if (index < 0) values.push(structuredClone(replacement))
  else values[index] = structuredClone(replacement)
}

function requireById(values, id, kind) {
  const matches = (values ?? []).filter(value => value.id === id)
  if (matches.length !== 1) {
    throw new RuntimeMatrixPromotionError(
      `Generated ${kind} '${id}' must occur exactly once; observed ${matches.length}.`,
    )
  }
  return structuredClone(matches[0])
}

function readRegularFile(filePath, maximumBytes, allowedRoot) {
  const fullPath = path.resolve(filePath)
  const fullRoot = path.resolve(allowedRoot)
  const relative = path.relative(fullRoot, fullPath)
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    throw new RuntimeMatrixPromotionError(`Promotion file '${fullPath}' escapes '${fullRoot}'.`)
  }
  const stat = fs.lstatSync(fullPath)
  if (!stat.isFile() || stat.isSymbolicLink()) {
    throw new RuntimeMatrixPromotionError(`Promotion file '${fullPath}' is not a regular non-link file.`)
  }
  if (stat.size > maximumBytes) {
    throw new RuntimeMatrixPromotionError(`Promotion file '${fullPath}' exceeds the size limit.`)
  }
  return fs.readFileSync(fullPath)
}

function readOptionalRegularFile(filePath, maximumBytes, allowedRoot) {
  return fs.existsSync(filePath)
    ? readRegularFile(filePath, maximumBytes, allowedRoot)
    : undefined
}

function cleanupTemporaryFiles(entries, options) {
  for (const entry of entries) {
    for (const candidate of [
      entry.temporaryPath,
      ...(options.preserveBackups ? [] : [entry.backupPath]),
    ]) {
      try {
        fs.rmSync(candidate, { force: true })
      } catch (error) {
        if (!options.ignoreErrors) throw error
      }
    }
  }
}

function validateProfileId(profileId) {
  if (!profileIdPattern.test(profileId ?? '')) {
    throw new RuntimeMatrixPromotionError(`Invalid runtime profile ID '${profileId}'.`)
  }
}

function sha256(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`; }

function parseJson(bytes, filePath) {
  try {
    return JSON.parse(bytes.toString('utf8'))
  } catch (error) {
    throw new RuntimeMatrixPromotionError(`Invalid JSON in '${filePath}': ${error.message}`)
  }
}

function serializeJson(value) { return Buffer.from(`${JSON.stringify(value, null, 2)}\n`); }

function writeJson(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true })
  fs.writeFileSync(filePath, serializeJson(value))
}

function requireEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new RuntimeMatrixPromotionError(
      `${label} must equal ${JSON.stringify(expected)}; observed ${JSON.stringify(actual)}.`,
    )
  }
}

function requireJsonEqual(actual, expected, label) {
  if (!serializeRuntimePromotionPlan(actual).equals(serializeRuntimePromotionPlan(expected))) {
    throw new RuntimeMatrixPromotionError(`${label} does not match the canonical receipt binding.`)
  }
}

function withoutUndefined(value) { return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)); }

function removeDigestPrefix(value, prefix) {
  if (typeof value !== 'string' || !value.startsWith(prefix)) {
    throw new RuntimeMatrixPromotionError(
      `Receipt component source digest must start with '${prefix}'.`,
    )
  }
  return value.slice(prefix.length)
}

function unavailable(reason) { return { installed: false, health: 'not-installed', reason }; }

function parseOptions(args) {
  let repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
  let profileId
  let check = false
  const seen = new Set()
  for (let index = 0; index < args.length; index += 1) {
    const option = args[index]
    if (!seen.add(option)) throw new RuntimeMatrixPromotionError(`Duplicate option '${option}'.`)
    if (option === '--check') {
      check = true
      continue
    }
    if (option !== '--repository-root' && option !== '--profile-id') {
      throw new RuntimeMatrixPromotionError('Usage: node eng/release/promote-runtime-matrix.mjs --profile-id ID [--repository-root PATH] [--check]')
    }
    const value = args[++index]
    if (!value) throw new RuntimeMatrixPromotionError(`${option} requires a value.`)
    if (option === '--repository-root') repositoryRoot = path.resolve(value)
    else profileId = value
  }
  if (!profileId) throw new RuntimeMatrixPromotionError('--profile-id is required.')
  return { repositoryRoot, profileId, check }
}

if (process.argv[1] !== undefined &&
    path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  try {
    const options = parseOptions(process.argv.slice(2))
    const result = promoteRuntimeMatrix(options)
    const action = options.check ? 'validated' : 'promoted'
    console.log(
      `Runtime matrix profile '${result.binding.profileId}' ${action} with receipt ` +
      `${result.receiptReference.sha256}.`,
    )
  } catch (error) {
    console.error(`Runtime matrix promotion failed: ${error.message}`)
    process.exitCode = 1
  }
}
