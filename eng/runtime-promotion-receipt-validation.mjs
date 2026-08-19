import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { validateRuntimePerformanceEvidence } from './runtime-performance-evidence-validation.mjs'
import { validateRuntimeCapabilityEvidence } from './runtime-capability-evidence-validation.mjs'
import { parseOwnedJson } from './strict-owned-json.mjs'

const receiptDirectory = 'profiles/runtime-promotion-receipts'
const evidenceDirectory = 'profiles/runtime-promotion-evidence'
const maximumReceiptBytes = 1024 * 1024
const maximumEvidenceBytes = 1024 * 1024
const maximumProfileBytes = 1024 * 1024
const digestPattern = /^sha256:[0-9a-f]{64}$/
const imageReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const capabilityNames = new Set(['run', 'jit-asm', 'inspection', 'execution-flow'])

export function validateRuntimePromotionReceipts(matrix, repositoryRoot, readFile = fs.readFileSync) {
  const failures = []
  for (const binding of capabilityBindings(matrix)) {
    const capability = binding.capability
    if (capability?.promotionState !== 'verified') continue

    const reference = capability.promotionReceipt
    if (reference === null || typeof reference !== 'object' || Array.isArray(reference)) {
      failures.push(`${binding.profileId}: verified capability has no promotionReceipt object`)
      continue
    }

    const relativePath = reference.path
    if (!isCanonicalReceiptPath(relativePath, binding.profileId)) {
      failures.push(
        `${binding.profileId}: promotion receipt path must be ` +
        `${receiptDirectory}/${binding.profileId}.json`,
      )
      continue
    }
    if (!digestPattern.test(reference.sha256 ?? '')) {
      failures.push(`${binding.profileId}: promotion receipt sha256 is not canonical`)
      continue
    }

    const absolutePath = path.resolve(repositoryRoot, ...relativePath.split('/'))
    const allowedRoot = path.resolve(repositoryRoot, ...receiptDirectory.split('/'))
    if (!isPathInside(allowedRoot, absolutePath)) {
      failures.push(`${binding.profileId}: promotion receipt escapes its evidence directory`)
      continue
    }

    let bytes
    try {
      const rootStat = fs.lstatSync(allowedRoot)
      const receiptStat = fs.lstatSync(absolutePath)
      if (!rootStat.isDirectory() || rootStat.isSymbolicLink() ||
          !receiptStat.isFile() || receiptStat.isSymbolicLink()) {
        failures.push(`${binding.profileId}: promotion receipt must be a regular non-link file`)
        continue
      }
      const realRoot = fs.realpathSync.native(allowedRoot)
      const realReceipt = fs.realpathSync.native(absolutePath)
      if (!isPathInside(realRoot, realReceipt)) {
        failures.push(`${binding.profileId}: promotion receipt resolves outside its evidence directory`)
        continue
      }
      if (receiptStat.size > maximumReceiptBytes) {
        failures.push(`${binding.profileId}: promotion receipt exceeds the 1 MiB size limit`)
        continue
      }
      bytes = readFile(absolutePath)
    } catch (error) {
      failures.push(`${binding.profileId}: cannot read promotion receipt (${error.message})`)
      continue
    }

    const actualDigest = `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
    if (!constantTimeEqual(reference.sha256, actualDigest)) {
      failures.push(
        `${binding.profileId}: promotion receipt digest mismatch; ` +
        `expected ${reference.sha256}, observed ${actualDigest}`,
      )
      continue
    }

    const receipt = parseOwnedJson(
      bytes,
      `${binding.profileId}: promotion receipt`,
      failures,
    )
    if (receipt === undefined) continue
    failures.push(...validateReceiptBinding(binding, receipt, repositoryRoot, readFile))
  }
  return failures
}

function capabilityBindings(matrix) {
  const bindings = []
  for (const target of matrix.coreClr ?? []) {
    bindings.push({
      target,
      capability: target.linuxCapability,
      profileId: `${target.id}-linux-x64`,
      targetId: target.id,
      platform: 'linux',
      family: 'coreclr',
    })
    bindings.push({
      target,
      capability: target.wineCapability,
      profileId: `wine-${target.id}-linux-x64`,
      targetId: target.id,
      platform: 'wine',
      family: 'coreclr-wine',
    })
  }
  if (matrix.mono !== undefined) {
    bindings.push({
      target: matrix.mono,
      capability: matrix.mono.capability,
      profileId: matrix.mono.id,
      targetId: matrix.mono.id,
      platform: 'mono',
      family: 'mono',
    })
  }
  for (const target of matrix.framework?.targets ?? []) {
    bindings.push({
      target,
      capability: target.capability,
      profileId: `wine-${target.id}-linux-x64`,
      targetId: target.id,
      platform: 'framework',
      family: 'netfx-clr-wine',
    })
  }
  return bindings
}

function loadRuntimeProfile(binding, receipt, repositoryRoot, readFile, failures, prefix) {
  const runtimeProfilesRoot = path.resolve(repositoryRoot, 'profiles', 'runtimes')
  const relativePaths = [
    `profiles/runtimes/${binding.profileId}.json`,
    `profiles/runtimes/candidates/${binding.profileId}.json`,
  ]
  const loaded = []
  for (const relativePath of relativePaths) {
    const absolutePath = path.resolve(repositoryRoot, ...relativePath.split('/'))
    if (!fs.existsSync(absolutePath)) continue
    try {
      const rootStat = fs.lstatSync(runtimeProfilesRoot)
      const fileStat = fs.lstatSync(absolutePath)
      if (!rootStat.isDirectory() || rootStat.isSymbolicLink() ||
          !fileStat.isFile() || fileStat.isSymbolicLink() || fileStat.size > maximumProfileBytes) {
        failures.push(`${prefix} Runtime Profile '${relativePath}' is not a bounded regular non-link file`)
        continue
      }
      const realRoot = fs.realpathSync.native(runtimeProfilesRoot)
      const realFile = fs.realpathSync.native(absolutePath)
      if (!isPathInside(realRoot, realFile)) {
        failures.push(`${prefix} Runtime Profile '${relativePath}' resolves outside profiles/runtimes`)
        continue
      }
      const bytes = readFile(absolutePath)
      if (bytes.length > maximumProfileBytes) {
        failures.push(`${prefix} Runtime Profile '${relativePath}' exceeds the 1 MiB size limit`)
        continue
      }
      const profile = parseOwnedJson(
        bytes,
        `${prefix} Runtime Profile '${relativePath}'`,
        failures,
      )
      if (profile !== undefined) loaded.push({ relativePath, profile })
    } catch (error) {
      failures.push(`${prefix} cannot read Runtime Profile '${relativePath}' (${error.message})`)
    }
  }

  const receiptReference = binding.capability?.promotionReceipt
  const active = loaded.find(item => item.relativePath === relativePaths[0] &&
    item.profile?.promotionReceipt?.path === receiptReference?.path &&
    item.profile?.promotionReceipt?.sha256 === receiptReference?.sha256)
  const candidate = loaded.find(item => item.relativePath === relativePaths[1])
  const selected = active ?? candidate
  if (selected === undefined || !isObject(selected.profile)) {
    failures.push(`${prefix} has no active or candidate Runtime Profile for exact evidence validation`)
    return undefined
  }

  const profile = selected.profile
  expectEqual(failures, profile.id, binding.profileId, `${prefix} Runtime Profile id`)
  expectEqual(failures, profile.family, binding.family, `${prefix} Runtime Profile family`)
  expectEqual(
    failures,
    profile.runtimeVersion,
    receipt.resolvedVersion,
    `${prefix} Runtime Profile runtimeVersion`,
  )
  const expectedCapabilities = [...new Set(binding.capability?.capabilities ?? [])].sort()
  const observedCapabilities = Array.isArray(profile.capabilities)
    ? [...profile.capabilities].sort()
    : []
  if (!arraysEqual(observedCapabilities, expectedCapabilities)) {
    failures.push(
      `${prefix} Runtime Profile capabilities do not exactly match the promoted capability set`,
    )
  }
  if (!isObject(profile.operations) || !isObject(profile.layout)) {
    failures.push(`${prefix} Runtime Profile lacks explicit operation and layout definitions`)
  }
  return profile
}

function isCanonicalReceiptPath(value, profileId) {
  return typeof value === 'string' &&
    value === `${receiptDirectory}/${profileId}.json` &&
    !value.includes('\\') &&
    !value.split('/').includes('..')
}

function isPathInside(root, candidate) {
  const relative = path.relative(root, candidate)
  return relative.length > 0 && relative !== '..' && !relative.startsWith(`..${path.sep}`) &&
    !path.isAbsolute(relative)
}

function constantTimeEqual(left, right) {
  if (typeof left !== 'string' || left.length !== right.length) return false
  return crypto.timingSafeEqual(Buffer.from(left, 'ascii'), Buffer.from(right, 'ascii'))
}

function validateReceiptBinding(binding, receipt, repositoryRoot, readFile) {
  const failures = []
  const prefix = `${binding.profileId}: promotion receipt`
  expectEqual(failures, receipt.schemaVersion, 2, `${prefix} schemaVersion`)
  if (!digestPattern.test(receipt.planSha256 ?? '')) {
    failures.push(`${prefix} planSha256 must be sha256:<64 lowercase hex>`)
  }
  expectEqual(failures, receipt.profileId, binding.profileId, `${prefix} profileId`)
  expectEqual(failures, receipt.matrixTargetId, binding.targetId, `${prefix} matrixTargetId`)
  expectEqual(failures, receipt.platform, binding.platform, `${prefix} platform`)
  expectEqual(failures, receipt.family, binding.family, `${prefix} family`)
  expectEqual(failures, receipt.resolvedVersion, binding.target.version, `${prefix} resolvedVersion`)

  if (!imageReferencePattern.test(receipt.image?.reference ?? '')) {
    failures.push(`${prefix} image.reference must be repository@sha256:<64 lowercase hex>`)
  }
  if (!digestPattern.test(receipt.image?.imageId ?? '')) {
    failures.push(`${prefix} image.imageId must be sha256:<64 lowercase hex>`)
  }
  if (!Number.isSafeInteger(receipt.image?.sizeBytes) || receipt.image.sizeBytes <= 0) {
    failures.push(`${prefix} image.sizeBytes must be a positive integer`)
  }
  if (!/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/.test(receipt.sourceRevision ?? '')) {
    failures.push(`${prefix} sourceRevision must be a full lowercase Git commit`)
  }

  validateComponentIdentity(binding, receipt.componentIdentity, failures, prefix)

  const runtimeIdentity = receipt.runtimeIdentity
  if (runtimeIdentity === null || typeof runtimeIdentity !== 'object' || Array.isArray(runtimeIdentity)) {
    failures.push(`${prefix} runtimeIdentity is missing`)
  } else if (binding.family === 'coreclr' || binding.family === 'coreclr-wine') {
    for (const [receiptField, matrixField] of [
      ['runtimeCommit', 'runtimeCommit'],
      ['jitCommit', 'jitCommit'],
    ]) {
      const expected = binding.target[matrixField]
      if (!/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/.test(expected ?? '')) {
        failures.push(
          `${binding.profileId}: verified CoreCLR row must lock ${matrixField} before promotion`,
        )
      } else {
        expectEqual(
          failures,
          runtimeIdentity[receiptField],
          expected,
          `${prefix} runtimeIdentity.${receiptField}`,
        )
      }
    }
    expectEqual(
      failures,
      runtimeIdentity.jitVersion,
      binding.target.version,
      `${prefix} runtimeIdentity.jitVersion`,
    )
  } else {
    for (const field of ['runtimeCommit', 'jitVersion', 'jitCommit']) {
      expectEqual(
        failures,
        runtimeIdentity[field],
        'not-applicable',
        `${prefix} runtimeIdentity.${field}`,
      )
    }
  }

  const declared = [...new Set(binding.capability.capabilities ?? [])].sort()
  if (!declared.includes('run')) {
    failures.push(`${prefix} verified capabilities must include a passing run preflight`)
  }
  const profile = loadRuntimeProfile(binding, receipt, repositoryRoot, readFile, failures, prefix)
  const checks = Array.isArray(receipt.checks) ? receipt.checks : []
  const observed = checks.map(check => check?.capability).sort()
  if (JSON.stringify(observed) !== JSON.stringify(declared)) {
    failures.push(
      `${prefix} checks must cover every declared capability exactly once; ` +
      `expected [${declared.join(', ')}], observed [${observed.join(', ')}]`,
    )
  }
  validateOperationHelpers(binding, receipt, declared, failures, prefix)
  const retainedImageFiles = new Map()
  for (const check of checks) {
    const capability = check?.capability ?? '<missing>'
    if (check?.result !== 'passed' ||
        check.networkDisabled !== true ||
        check.supervisorSandbox !== true ||
        check.outputLimitValidated !== true ||
        !digestPattern.test(check.evidenceSha256 ?? '')) {
      failures.push(`${prefix} ${capability} check is not complete and passing`)
    }
    validateCapabilityEvidence(
      binding,
      profile,
      receipt,
      check,
      repositoryRoot,
      readFile,
      failures,
      prefix,
      retainedImageFiles,
    )
    if (capability === 'jit-asm') {
      validateJitMapping(binding, check, failures, prefix)
    } else if (check?.sourceMappingKind !== 'not-applicable' ||
               check?.mappingSource !== 'not-applicable') {
      failures.push(`${prefix} ${capability} check cannot claim JIT source mapping`)
    }
  }
  failures.push(...validateRuntimePerformanceEvidence({
    binding,
    receipt,
    declaredCapabilities: declared,
    repositoryRoot,
    readFile,
  }))
  return failures
}

function validateCapabilityEvidence(
  binding,
  profile,
  receipt,
  check,
  repositoryRoot,
  readFile,
  failures,
  prefix,
  retainedImageFiles,
) {
  const capability = check?.capability
  if (!capabilityNames.has(capability)) return

  const expectedPath = `${evidenceDirectory}/${binding.profileId}/${capability}.json`
  const relativePath = check.evidencePath
  if (relativePath !== expectedPath || relativePath.includes('\\') ||
      relativePath.split('/').includes('..')) {
    failures.push(
      `${prefix} ${capability} evidencePath must equal ${JSON.stringify(expectedPath)}; ` +
      `observed ${JSON.stringify(relativePath)}`,
    )
    return
  }
  if (!digestPattern.test(check.evidenceSha256 ?? '')) return

  const allowedRoot = path.resolve(repositoryRoot, ...evidenceDirectory.split('/'))
  const profileRoot = path.resolve(allowedRoot, binding.profileId)
  const absolutePath = path.resolve(repositoryRoot, ...relativePath.split('/'))
  if (!isPathInside(allowedRoot, profileRoot) || !isPathInside(profileRoot, absolutePath)) {
    failures.push(`${prefix} ${capability} evidence escapes its profile evidence directory`)
    return
  }

  let bytes
  try {
    const rootStat = fs.lstatSync(allowedRoot)
    const profileStat = fs.lstatSync(profileRoot)
    const evidenceStat = fs.lstatSync(absolutePath)
    if (!rootStat.isDirectory() || rootStat.isSymbolicLink() ||
        !profileStat.isDirectory() || profileStat.isSymbolicLink() ||
        !evidenceStat.isFile() || evidenceStat.isSymbolicLink()) {
      failures.push(
        `${prefix} ${capability} evidence must be a regular non-link file ` +
        'below regular non-link evidence directories',
      )
      return
    }

    const realRoot = fs.realpathSync.native(allowedRoot)
    const realProfile = fs.realpathSync.native(profileRoot)
    const realEvidence = fs.realpathSync.native(absolutePath)
    if (!isPathInside(realRoot, realProfile) || !isPathInside(realProfile, realEvidence)) {
      failures.push(`${prefix} ${capability} evidence resolves outside its profile evidence directory`)
      return
    }
    if (evidenceStat.size > maximumEvidenceBytes) {
      failures.push(`${prefix} ${capability} evidence exceeds the 1 MiB size limit`)
      return
    }
    bytes = readFile(absolutePath)
    if (bytes.length > maximumEvidenceBytes) {
      failures.push(`${prefix} ${capability} evidence exceeds the 1 MiB size limit`)
      return
    }
  } catch (error) {
    failures.push(`${prefix} cannot read ${capability} evidence (${error.message})`)
    return
  }

  const actualDigest = `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
  if (!constantTimeEqual(check.evidenceSha256, actualDigest)) {
    failures.push(
      `${prefix} ${capability} evidence digest mismatch; ` +
      `expected ${check.evidenceSha256}, observed ${actualDigest}`,
    )
    return
  }

  const evidence = parseOwnedJson(bytes, `${prefix} ${capability} evidence`, failures)
  if (evidence === undefined) return
  failures.push(...validateRuntimeCapabilityEvidence({
    binding,
    profile,
    receipt,
    check,
    evidence,
    retainedImageFiles,
  }))
}

function expectEqual(failures, actual, expected, label) {
  if (actual !== expected) {
    failures.push(`${label} must equal ${JSON.stringify(expected)}; observed ${JSON.stringify(actual)}`)
  }
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function arraysEqual(left, right) {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

function validateJitMapping(binding, check, failures, prefix) {
  if (binding.platform === 'framework') {
    failures.push(`${prefix} ${binding.platform} capability cannot declare jit-asm`)
    return
  }

  if (binding.platform === 'wine' || binding.platform === 'mono') {
    if (check.sourceMappingKind !== 'none' || !['none', 'method'].includes(check.mappingSource)) {
      const platformName = binding.platform === 'wine' ? 'Wine CoreCLR' : 'Mono'
      failures.push(
        `${prefix} ${platformName} jit-asm check must use sourceMappingKind=none ` +
        'and MappingSource=none or method',
      )
    }
    return
  }

  if (check.sourceMappingKind === 'linux-profiler' &&
      !['ordinary', 'rich'].includes(check.mappingSource)) {
    failures.push(`${prefix} jit-asm check must prove profiler-backed MappingSource`)
  } else if (check.sourceMappingKind === 'checked-jit-debug-info' &&
             check.mappingSource !== 'checked-jit-debug-info') {
    failures.push(`${prefix} checked JIT mapping must prove checked-jit-debug-info MappingSource`)
  } else if (check.sourceMappingKind === 'none' &&
             !['none', 'method'].includes(check.mappingSource)) {
    failures.push(`${prefix} mapping-free or method-level jit-asm check has an invalid MappingSource`)
  } else if (!['none', 'linux-profiler', 'checked-jit-debug-info'].includes(check.sourceMappingKind)) {
    failures.push(`${prefix} jit-asm check must bind its sourceMappingKind`)
  }
}

function isImmutableSourceUri(value) {
  if (typeof value !== 'string' || value.length === 0 || value !== value.trim()) return false
  if (value.startsWith('docker://')) {
    return imageReferencePattern.test(value.slice('docker://'.length))
  }
  try {
    const uri = new URL(value)
    return uri.protocol === 'https:' && uri.hostname.length > 0 &&
      uri.username.length === 0 && uri.password.length === 0
  } catch {
    return false
  }
}

function validateComponentIdentity(binding, componentIdentity, failures, prefix) {
  if (componentIdentity === null || typeof componentIdentity !== 'object' ||
      Array.isArray(componentIdentity)) {
    failures.push(`${prefix} componentIdentity is missing`)
    return
  }
  const sourceUri = componentIdentity.sourceUri
  const sourceDigest = componentIdentity.sourceDigest
  if (!isImmutableSourceUri(sourceUri)) {
    failures.push(
      `${prefix} componentIdentity.sourceUri must be HTTPS or ` +
      'docker://repository@sha256:<64 lowercase hex>',
    )
  }

  if (binding.family === 'coreclr' || binding.family === 'coreclr-wine') {
    const payload = binding.platform === 'linux' ? binding.target.linux : binding.target.windows
    if (payload === null || typeof payload !== 'object' ||
        typeof payload.url !== 'string' || !/^[0-9a-f]{128}$/.test(payload.sha512 ?? '')) {
      failures.push(`${binding.profileId}: verified CoreCLR row has no immutable platform payload`)
      return
    }
    expectEqual(
      failures,
      sourceUri,
      payload.url,
      `${prefix} componentIdentity.sourceUri`,
    )
    expectEqual(
      failures,
      sourceDigest,
      `sha512:${payload.sha512}`,
      `${prefix} componentIdentity.sourceDigest`,
    )
    return
  }

  if (!digestPattern.test(sourceDigest ?? '')) {
    failures.push(`${prefix} operator componentIdentity.sourceDigest must be sha256:<64 lowercase hex>`)
  }
  if (typeof sourceUri === 'string' && sourceUri.startsWith('docker://') &&
      sourceUri.slice(sourceUri.lastIndexOf('@') + 1) !== sourceDigest) {
    failures.push(
      `${prefix} operator componentIdentity.sourceDigest must equal the digest in sourceUri`,
    )
  }
  if (binding.target.sourceUri !== undefined) {
    expectEqual(
      failures,
      sourceUri,
      binding.target.sourceUri,
      `${prefix} componentIdentity.sourceUri`,
    )
  }
  if (binding.target.digest !== undefined) {
    expectEqual(
      failures,
      sourceDigest,
      binding.target.digest,
      `${prefix} componentIdentity.sourceDigest`,
    )
  }
}

function expectedOperationHelpers(binding, receipt, declared) {
  const legacy = {
    implementation: 'sharplabnext-legacy-jit-inspector-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
  }
  const checkedJitBridge = {
    implementation: 'sharplabnext-checked-jit-bridge-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
  }
  const targetRuntimeRunner = {
    implementation: 'sharplabnext-target-runtime-runner-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
  }
  const monoJitInspector = {
    implementation: 'sharplabnext-mono-jit-inspector-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll',
  }
  if (binding.family === 'mono') {
    return declared.includes('jit-asm')
      ? { run: targetRuntimeRunner, jit: monoJitInspector }
      : { run: targetRuntimeRunner }
  }
  if (binding.family === 'netfx-clr-wine') {
    return { run: targetRuntimeRunner }
  }
  if (binding.family === 'coreclr-wine') {
    return declared.includes('jit-asm') ? { run: legacy, jit: legacy } : { run: legacy }
  }

  const jitCheck = Array.isArray(receipt.checks)
    ? receipt.checks.find(check => check?.capability === 'jit-asm')
    : undefined
  const requiresModernRun = declared.some(capability =>
    capability === 'inspection' || capability === 'execution-flow') ||
    jitCheck?.sourceMappingKind === 'linux-profiler'
  const run = requiresModernRun
    ? {
        implementation: 'sharplabnext-runner-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.Runner.dll',
      }
    : legacy
  if (!declared.includes('jit-asm')) return { run }
  const usesCheckedJitBridge = binding.platform === 'linux' &&
    binding.target.checkedJit !== null &&
    typeof binding.target.checkedJit === 'object' &&
    !Array.isArray(binding.target.checkedJit)
  const jit = usesCheckedJitBridge
    ? checkedJitBridge
    : jitCheck?.sourceMappingKind === 'linux-profiler'
    ? {
        implementation: 'sharplabnext-jit-inspector-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
        profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
      }
    : legacy
  return { run, jit }
}

function validateOperationHelpers(binding, receipt, declared, failures, prefix) {
  const operations = receipt.operations
  if (operations === null || typeof operations !== 'object' || Array.isArray(operations)) {
    failures.push(`${prefix} operations is missing`)
    return
  }
  const expected = expectedOperationHelpers(binding, receipt, declared)
  const expectedNames = Object.keys(expected).sort()
  const observedNames = Object.keys(operations).sort()
  if (JSON.stringify(observedNames) !== JSON.stringify(expectedNames)) {
    failures.push(
      `${prefix} operations must contain exactly [${expectedNames.join(', ')}]; ` +
      `observed [${observedNames.join(', ')}]`,
    )
  }
  for (const [name, expectedHelper] of Object.entries(expected)) {
    const helper = operations[name]
    if (helper === null || typeof helper !== 'object' || Array.isArray(helper)) {
      failures.push(`${prefix} operations.${name} is missing`)
      continue
    }
    expectEqual(
      failures,
      helper.implementation,
      expectedHelper.implementation,
      `${prefix} operations.${name}.implementation`,
    )
    expectEqual(
      failures,
      helper.assemblyPath,
      expectedHelper.assemblyPath,
      `${prefix} operations.${name}.assemblyPath`,
    )
    if (!digestPattern.test(helper.assemblySha256 ?? '')) {
      failures.push(
        `${prefix} operations.${name}.assemblySha256 must be sha256:<64 lowercase hex>`,
      )
    }
    if (expectedHelper.profilerPath !== undefined) {
      expectEqual(
        failures,
        helper.profilerPath,
        expectedHelper.profilerPath,
        `${prefix} operations.${name}.profilerPath`,
      )
      if (!digestPattern.test(helper.profilerSha256 ?? '')) {
        failures.push(
          `${prefix} operations.${name}.profilerSha256 must be sha256:<64 lowercase hex>`,
        )
      }
    } else if (helper.profilerPath !== undefined || helper.profilerSha256 !== undefined) {
      failures.push(`${prefix} operations.${name} cannot bind a profiler`)
    }
  }
}

if (process.argv[1] !== undefined &&
    path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  let repositoryRoot = process.cwd()
  let matrixPath
  for (let index = 2; index < process.argv.length; index += 1) {
    const option = process.argv[index]
    if (option !== '--repository-root' && option !== '--matrix') {
      console.error(
        'Usage: node eng/runtime-promotion-receipt-validation.mjs ' +
        '[--repository-root PATH] [--matrix PATH]',
      )
      process.exit(64)
    }
    const value = process.argv[++index]
    if (value === undefined || value.length === 0) {
      console.error(`${option} requires a value`)
      process.exit(64)
    }
    if (option === '--repository-root') repositoryRoot = path.resolve(value)
    else matrixPath = path.resolve(value)
  }
  matrixPath ??= path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
  try {
    const matrix = JSON.parse(fs.readFileSync(matrixPath, 'utf8'))
    const failures = validateRuntimePromotionReceipts(matrix, repositoryRoot)
    if (failures.length > 0) {
      for (const failure of failures) console.error(`promotion receipt error: ${failure}`)
      process.exitCode = 1
    } else {
      console.log('Runtime promotion receipts are valid.')
    }
  } catch (error) {
    console.error(`promotion receipt error: ${error.message}`)
    process.exitCode = 1
  }
}
