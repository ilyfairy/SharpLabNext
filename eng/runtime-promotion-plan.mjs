/**
 * Produces the trusted input plan for one runtime capability preflight.
 *
 * A plan is derived from reviewed matrix inputs and immutable Docker
 * observations. It is not evidence and it does not authorize promotion.
 */

import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import {
  candidateComponentIdentity,
  candidateExpectedImageLabels,
  candidateImageTag,
  candidateMatrixBinding,
  candidateOperationHelpers,
  validateCandidateBuildInputs,
} from './build-runtime-candidate.mjs'
import {
  bindRuntimeCandidateImage,
  hashRuntimeOperationHelpers,
  inspectDockerImage,
  inspectGitSourceState,
  validateGitSourceState,
} from './runtime-promotion-image-binding.mjs'

const defaultRepositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const pinnedReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const canonicalIdPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const maximumInputBytes = 8 * 1024 * 1024

export const runtimePromotionPlanProducerId = 'sharplabnext-runtime-preflight-v1'
export const runtimePromotionPlanUsage = `Usage:
  node eng/runtime-promotion-plan.mjs <candidate-target> \\
    --profile profiles/runtimes/candidates/<profile-id>.json \\
    --pinned-reference <repository>@sha256:<64-hex> \\
    --performance-policy profiles/runtime-performance-policies/<policy-id>.json [--check]`

export class RuntimePromotionPlanError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimePromotionPlanError'
  }
}

function sha256(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}

function parseJson(bytes, label) {
  try {
    const value = JSON.parse(bytes.toString('utf8'))
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
      throw new Error('root must be an object')
    }
    return value
  } catch (error) {
    throw new RuntimePromotionPlanError(`${label} is not valid JSON: ${error.message}`, {
      cause: error,
    })
  }
}

function readRegularFile(filename, label) {
  let before
  try {
    before = fs.lstatSync(filename)
  } catch (error) {
    throw new RuntimePromotionPlanError(`Could not read ${label}: ${error.message}`, { cause: error })
  }
  if (!before.isFile() || before.isSymbolicLink()) {
    throw new RuntimePromotionPlanError(`${label} must be a regular non-link file.`)
  }
  if (before.size <= 0 || before.size > maximumInputBytes) {
    throw new RuntimePromotionPlanError(
      `${label} must contain between 1 and ${maximumInputBytes} bytes.`,
    )
  }

  const noFollow = fs.constants.O_NOFOLLOW ?? 0
  const descriptor = fs.openSync(filename, fs.constants.O_RDONLY | noFollow)
  try {
    const opened = fs.fstatSync(descriptor)
    if (!opened.isFile() || opened.size !== before.size ||
        (before.dev !== undefined && opened.dev !== before.dev) ||
        (before.ino !== undefined && opened.ino !== before.ino)) {
      throw new RuntimePromotionPlanError(`${label} changed while it was being opened.`)
    }
    const bytes = fs.readFileSync(descriptor)
    const after = fs.fstatSync(descriptor)
    if (bytes.length !== opened.size || after.size !== opened.size ||
        after.mtimeMs !== opened.mtimeMs || after.ctimeMs !== opened.ctimeMs ||
        (opened.dev !== undefined && after.dev !== opened.dev) ||
        (opened.ino !== undefined && after.ino !== opened.ino)) {
      throw new RuntimePromotionPlanError(`${label} changed while it was being read.`)
    }
    return bytes
  } finally {
    fs.closeSync(descriptor)
  }
}

function requireCanonicalRelativePath(actual, expected, label) {
  if (typeof actual !== 'string' || actual !== expected || actual.includes('\\') ||
      path.isAbsolute(actual)) {
    throw new RuntimePromotionPlanError(`${label} must be the canonical path '${expected}'.`)
  }
}

function requireEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new RuntimePromotionPlanError(
      `${label} must equal ${JSON.stringify(expected)}; observed ${JSON.stringify(actual)}.`,
    )
  }
}

function selectSecurityPolicy(profile) {
  if (!Array.isArray(profile.allowedSecurityPolicyIds) ||
      profile.allowedSecurityPolicyIds.length !== 1) {
    throw new RuntimePromotionPlanError(
      'The candidate Runtime Profile must select exactly one allowed security policy.',
    )
  }
  const id = profile.allowedSecurityPolicyIds[0]
  if (!canonicalIdPattern.test(id ?? '')) {
    throw new RuntimePromotionPlanError('The candidate security policy ID is invalid.')
  }
  const definitions = Array.isArray(profile.securityPolicies)
    ? profile.securityPolicies.filter(policy => policy?.id === id)
    : []
  if (definitions.length !== 1) {
    throw new RuntimePromotionPlanError(
      `The candidate security policy '${id}' must be defined exactly once.`,
    )
  }
  return id
}

function platformCapability(binding) {
  const capability = {
    coreclr: binding.row.linuxCapability,
    'coreclr-wine': binding.row.wineCapability,
    mono: binding.row.capability,
    'netfx-clr-wine': binding.row.capability,
  }[binding.family]
  if (capability === null || typeof capability !== 'object' || Array.isArray(capability)) {
    throw new RuntimePromotionPlanError(
      `The exact '${binding.family}' matrix platform capability is missing.`,
    )
  }
  return capability
}

function selectCapabilities(profile, binding) {
  const declared = platformCapability(binding).capabilities
  if (!Array.isArray(declared) || declared.length === 0) {
    throw new RuntimePromotionPlanError('The exact matrix platform has no declared capabilities.')
  }
  const capabilities = [...declared].sort()
  if (capabilities.length > 4 || new Set(capabilities).size !== capabilities.length ||
      capabilities.some(value => !['run', 'jit-asm', 'inspection', 'execution-flow'].includes(value)) ||
      !capabilities.includes('run')) {
    throw new RuntimePromotionPlanError('The exact matrix platform capability set is invalid.')
  }

  const candidateCapabilities = Array.isArray(profile.capabilities)
    ? [...profile.capabilities].sort()
    : []
  const nonSelectableInstrumentation = new Set(['inspection', 'execution-flow'])
  const expectedCandidateCapabilities = capabilities.filter(
    capability => !nonSelectableInstrumentation.has(capability),
  )
  if (JSON.stringify(candidateCapabilities) !== JSON.stringify(expectedCandidateCapabilities)) {
    throw new RuntimePromotionPlanError(
      'The blocked candidate Runtime Profile must expose exactly the matrix operation capabilities; ' +
      'instrumentation is enabled only in the immutable preflight profile.',
    )
  }

  const profilerProvider = binding.family === 'coreclr'
    ? binding.row.profilerProvider
    : undefined
  if (profilerProvider !== undefined) {
    requireEqual(
      profile.operations?.run?.implementationId,
      'sharplabnext-runner-v1',
      'profiler-backed candidate Run implementation',
    )
    if (capabilities.includes('jit-asm')) {
      requireEqual(
        profile.operations?.jit?.implementationId,
        'sharplabnext-jit-inspector-v1',
        'profiler-backed candidate JIT implementation',
      )
      requireEqual(
        profile.operations?.jit?.sourceMappingKind,
        profilerProvider.sourceMappingKind,
        'profiler-backed candidate source mapping kind',
      )
      if (typeof profile.operations?.jit?.profilerPath !== 'string' ||
          profile.operations.jit.profilerPath.length === 0) {
        throw new RuntimePromotionPlanError(
          'A profiler-backed candidate JIT operation must bind its native profiler path.',
        )
      }
    }
  }
  if (capabilities.some(capability => nonSelectableInstrumentation.has(capability)) &&
      profile.operations?.run?.implementationId !== 'sharplabnext-runner-v1') {
    throw new RuntimePromotionPlanError(
      'Instrumentation preflight requires the modern Runner implementation.',
    )
  }
  return capabilities
}

function platformForFamily(family) {
  const platform = {
    coreclr: 'linux',
    'coreclr-wine': 'wine',
    mono: 'mono',
    'netfx-clr-wine': 'framework',
  }[family]
  if (platform === undefined) {
    throw new RuntimePromotionPlanError(`Unsupported runtime family '${family}'.`)
  }
  return platform
}

function jitLibraryPath(profile, capabilities) {
  if (!capabilities.includes('jit-asm')) return undefined
  if (profile.family === 'coreclr') {
    return `/opt/sharplabnext/target-dotnet/shared/Microsoft.NETCore.App/` +
      `${profile.runtimeVersion}/libclrjit.so`
  }
  if (profile.family === 'coreclr-wine') {
    return `/opt/wine-dotnet/drive_c/dotnet/shared/Microsoft.NETCore.App/` +
      `${profile.runtimeVersion}/clrjit.dll`
  }
  throw new RuntimePromotionPlanError(
    `Runtime family '${profile.family}' cannot declare a CoreCLR JIT library.`,
  )
}

function operationSpecifications(target, values, profile, capabilities) {
  const available = candidateOperationHelpers(target, values)
  const names = capabilities.includes('jit-asm') ? ['run', 'jit'] : ['run']
  const selected = {}
  for (const name of names) {
    const helper = available[name]
    const operation = profile.operations?.[name]
    if (helper === undefined || operation === undefined) {
      throw new RuntimePromotionPlanError(`The candidate has no ${name} operation binding.`)
    }
    requireEqual(
      operation.implementationId,
      helper.implementation,
      `candidate ${name} implementation`,
    )
    if (name === 'run') {
      requireEqual(profile.layout?.runnerAssemblyPath, helper.assemblyPath, 'candidate Run helper path')
    } else {
      const declaredPath = profile.layout?.jitInspectorAssemblyPath ??
        (helper.assemblyPath === profile.layout?.runnerAssemblyPath ? helper.assemblyPath : undefined)
      requireEqual(declaredPath, helper.assemblyPath, 'candidate JIT helper path')
      requireEqual(operation.profilerPath, helper.profilerPath, 'candidate JIT profiler path')
    }
    selected[name] = helper
  }
  return Object.freeze(selected)
}

function validatePolicy(policy, policyPath, imageSizeBytes) {
  if (policy?.schemaVersion !== 1 || !canonicalIdPattern.test(policy?.id ?? '')) {
    throw new RuntimePromotionPlanError('The runtime performance policy identity is invalid.')
  }
  const expectedPath = `profiles/runtime-performance-policies/${policy.id}.json`
  requireCanonicalRelativePath(policyPath, expectedPath, 'performance policy path')
  if (!Number.isSafeInteger(policy.image?.maximumSizeBytes) ||
      policy.image.maximumSizeBytes <= 0) {
    throw new RuntimePromotionPlanError('The runtime performance policy image limit is invalid.')
  }
  if (imageSizeBytes > policy.image.maximumSizeBytes) {
    throw new RuntimePromotionPlanError(
      `The candidate image size ${imageSizeBytes} exceeds policy '${policy.id}' limit ` +
      `${policy.image.maximumSizeBytes}.`,
    )
  }
}

function sameJson(left, right) {
  return JSON.stringify(left) === JSON.stringify(right)
}

function requireUnchanged(actual, expected, label) {
  if (!actual.equals(expected)) {
    throw new RuntimePromotionPlanError(`${label} changed before the promotion plan commit.`)
  }
}

function createAtomicStage(repositoryRoot, relativeOutputPath, bytes) {
  const outputPath = path.join(repositoryRoot, ...relativeOutputPath.split('/'))
  const outputDirectory = path.dirname(outputPath)
  fs.mkdirSync(outputDirectory, { recursive: true })
  const repositoryRealPath = fs.realpathSync(repositoryRoot)
  const directoryRealPath = fs.realpathSync(outputDirectory)
  const expectedDirectory = path.join(repositoryRealPath, 'profiles', 'runtime-promotion-plans')
  if (directoryRealPath !== expectedDirectory) {
    throw new RuntimePromotionPlanError('The runtime promotion plan output directory is not canonical.')
  }
  if (fs.existsSync(outputPath)) {
    const existing = fs.lstatSync(outputPath)
    if (!existing.isFile() || existing.isSymbolicLink()) {
      throw new RuntimePromotionPlanError('The runtime promotion plan output must be a regular file.')
    }
  }

  const temporaryPath = path.join(
    outputDirectory,
    `.${path.basename(outputPath)}.${process.pid}.${crypto.randomUUID()}.tmp`,
  )
  const descriptor = fs.openSync(temporaryPath, 'wx', 0o600)
  try {
    fs.writeFileSync(descriptor, bytes)
    fs.fsyncSync(descriptor)
  } finally {
    fs.closeSync(descriptor)
  }
  let backupPath
  let installed = false
  return {
    outputPath,
    install() {
      if (fs.existsSync(outputPath)) {
        backupPath = path.join(
          outputDirectory,
          `.${path.basename(outputPath)}.${process.pid}.${crypto.randomUUID()}.bak`,
        )
        fs.renameSync(outputPath, backupPath)
      }
      try {
        fs.renameSync(temporaryPath, outputPath)
        installed = true
      } catch (error) {
        if (backupPath !== undefined && fs.existsSync(backupPath) && !fs.existsSync(outputPath)) {
          fs.renameSync(backupPath, outputPath)
          backupPath = undefined
        }
        throw error
      }
    },
    verify() {
      requireUnchanged(readRegularFile(outputPath, 'staged promotion plan output'), bytes,
        'A staged promotion plan output')
    },
    rollback() {
      if (installed && fs.existsSync(outputPath)) fs.rmSync(outputPath)
      installed = false
      if (backupPath !== undefined && fs.existsSync(backupPath)) {
        fs.renameSync(backupPath, outputPath)
        backupPath = undefined
      }
    },
    finish() {
      if (backupPath !== undefined) {
        fs.rmSync(backupPath, { force: true })
        backupPath = undefined
      }
    },
    dispose() {
      fs.rmSync(temporaryPath, { force: true })
    },
  }
}

function installAtomicStages(stages, beforeInstall) {
  const installed = []
  try {
    for (let index = 0; index < stages.length; index++) {
      const stage = stages[index]
      beforeInstall?.(index, stage.outputPath)
      stage.install()
      installed.push(stage)
      stage.verify()
    }
    for (const stage of stages) stage.finish()
  } catch (error) {
    const rollbackFailures = []
    for (const stage of installed.reverse()) {
      try {
        stage.rollback()
      } catch (rollbackError) {
        rollbackFailures.push(rollbackError)
      }
    }
    if (rollbackFailures.length > 0) {
      throw new RuntimePromotionPlanError(
        'Runtime promotion plan commit failed and could not be fully rolled back.',
        { cause: new AggregateError([error, ...rollbackFailures]) },
      )
    }
    throw error
  } finally {
    for (const stage of stages) stage.dispose()
  }
}

function materializePreflightProfile(profile, image, capabilities) {
  if (profile.promotionReceipt !== undefined) {
    throw new RuntimePromotionPlanError(
      'A candidate Runtime Profile cannot already contain a promotion receipt.',
    )
  }
  const materialized = structuredClone(profile)
  materialized.image = image.reference
  materialized.runtimeImageId = image.imageId
  materialized.capabilities = [...capabilities]
  return materialized
}

function canonicalTimestamp(now) {
  const value = now instanceof Date ? now : new Date(now)
  if (!Number.isFinite(value.getTime())) {
    throw new RuntimePromotionPlanError('The promotion plan clock returned an invalid timestamp.')
  }
  return value.toISOString()
}

export function produceRuntimePromotionPlan(input, options = {}) {
  return createOrVerifyRuntimePromotionPlan(input, options, false)
}

export function verifyRuntimePromotionPlan(input, options = {}) {
  return createOrVerifyRuntimePromotionPlan(input, options, true)
}

function createOrVerifyRuntimePromotionPlan(input, options, verifyExisting) {
  const {
    repositoryRoot = defaultRepositoryRoot,
    values: rawValues = process.env,
    validateCandidateInputs = validateCandidateBuildInputs,
    inspectGit = inspectGitSourceState,
    validateGit = validateGitSourceState,
    inspectImage = inspectDockerImage,
    hashOperations = hashRuntimeOperationHelpers,
    now = () => new Date(),
    beforeRecheck,
    beforeStageInstall,
  } = options
  const values = Object.freeze({ ...rawValues })
  const { target, profilePath, pinnedReference, performancePolicyPath } = input
  if (typeof target !== 'string' || target.length === 0) {
    throw new RuntimePromotionPlanError('A candidate target is required.')
  }
  if (!pinnedReferencePattern.test(pinnedReference ?? '')) {
    throw new RuntimePromotionPlanError(
      'The pinned image reference must be repository@sha256:<64 lowercase hex>.',
    )
  }

  const failures = validateCandidateInputs(target, values)
  if (failures.length > 0) {
    throw new RuntimePromotionPlanError(
      `Runtime candidate inputs are invalid:\n- ${failures.join('\n- ')}`,
    )
  }
  const profileId = values.RUNTIME_MATRIX_PROFILE_ID
  if (!canonicalIdPattern.test(profileId ?? '')) {
    throw new RuntimePromotionPlanError('RUNTIME_MATRIX_PROFILE_ID is invalid.')
  }
  const expectedProfilePath = `profiles/runtimes/candidates/${profileId}.json`
  requireCanonicalRelativePath(profilePath, expectedProfilePath, 'candidate Runtime Profile path')
  if (typeof performancePolicyPath !== 'string' ||
      !/^profiles\/runtime-performance-policies\/[a-z0-9][a-z0-9._-]{0,127}\.json$/.test(
        performancePolicyPath,
      )) {
    throw new RuntimePromotionPlanError(
      'The performance policy must use its canonical path below ' +
      'profiles/runtime-performance-policies.',
    )
  }

  const profileAbsolutePath = path.join(repositoryRoot, ...profilePath.split('/'))
  const matrixAbsolutePath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
  const policyAbsolutePath = path.join(repositoryRoot, ...performancePolicyPath.split('/'))
  const profileBytes = readRegularFile(profileAbsolutePath, 'candidate Runtime Profile')
  const matrixBytes = readRegularFile(matrixAbsolutePath, 'runtime matrix')
  const policyBytes = readRegularFile(policyAbsolutePath, 'runtime performance policy')
  const profile = parseJson(profileBytes, 'candidate Runtime Profile')
  const matrix = parseJson(matrixBytes, 'runtime matrix')
  const policy = parseJson(policyBytes, 'runtime performance policy')
  requireEqual(profile.id, profileId, 'candidate Runtime Profile ID')

  const binding = candidateMatrixBinding(target, profileId, matrix)
  const capabilities = selectCapabilities(profile, binding)
  const relativeOutputPath = `profiles/runtime-promotion-plans/${profileId}.json`
  const preflightProfilePath = `profiles/runtime-promotion-plans/${profileId}.profile.json`
  const performanceEvidencePath =
    `profiles/runtime-promotion-evidence/${profileId}/performance.json`
  const allowedGeneratedPaths = verifyExisting
    ? [
        relativeOutputPath,
        preflightProfilePath,
        performanceEvidencePath,
        `profiles/runtime-promotion-receipts/${profileId}.json`,
        ...capabilities.map(capability =>
          `profiles/runtime-promotion-evidence/${profileId}/${capability}.json`),
      ]
    : []
  const existingPlanBytes = verifyExisting
    ? readRegularFile(
        path.join(repositoryRoot, ...relativeOutputPath.split('/')),
        'installed runtime promotion plan',
      )
    : undefined
  const existingPreflightProfileBytes = verifyExisting
    ? readRegularFile(
        path.join(repositoryRoot, ...preflightProfilePath.split('/')),
        'installed runtime promotion preflight profile',
      )
    : undefined
  const existingPlan = existingPlanBytes === undefined
    ? undefined
    : parseJson(existingPlanBytes, 'installed runtime promotion plan')

  const sourceState = inspectGit({
    cwd: repositoryRoot,
    env: values,
    allowedDirtyPaths: allowedGeneratedPaths,
  })
  const sourceBinding = validateGit(sourceState, values.SOURCE_REVISION)
  if (sourceBinding.failures.length > 0 || !sourceBinding.promotionEligible) {
    throw new RuntimePromotionPlanError(
      `Promotion plans require the exact clean source revision:\n- ` +
      `${sourceBinding.failures.join('\n- ') || 'the source is development-only'}`,
    )
  }

  requireEqual(profile.family, binding.family, 'candidate Runtime Profile family')
  requireEqual(
    profile.runtimeVersion,
    binding.row.version ?? binding.row.resolvedVersion,
    'candidate Runtime Profile runtime version',
  )
  requireEqual(
    profile.container?.environmentKind,
    binding.environment,
    'candidate Runtime Profile environment',
  )
  requireEqual(
    profile.container?.isolationKind,
    binding.isolation,
    'candidate Runtime Profile isolation',
  )
  requireEqual(
    profile.container?.executionUser,
    binding.executionUser,
    'candidate Runtime Profile execution user',
  )
  const candidateReference = candidateImageTag(target, values)
  requireEqual(profile.image, candidateReference, 'candidate Runtime Profile image')
  requireEqual(profile.runtimeImageId, candidateReference, 'candidate Runtime Profile image identity')
  const expectedLabels = candidateExpectedImageLabels(target, values)
  const inspect = reference => inspectImage(reference, { cwd: repositoryRoot, env: values })
  const image = bindRuntimeCandidateImage({
    candidateReference,
    pinnedReference,
    sourceRevision: values.SOURCE_REVISION,
    expectedLabels,
    inspect,
  })
  if (!Number.isSafeInteger(image.sizeBytes) || image.sizeBytes <= 0 ||
      image.sizeBytes > 17_179_869_184) {
    throw new RuntimePromotionPlanError('Docker did not provide a valid positive candidate image Size.')
  }
  validatePolicy(policy, performancePolicyPath, image.sizeBytes)

  const securityPolicyId = selectSecurityPolicy(profile)
  const helperSpecifications = operationSpecifications(target, values, profile, capabilities)
  const hashOptions = { cwd: repositoryRoot, env: values }
  const operations = hashOperations(image.imageId, helperSpecifications, hashOptions)
  const componentIdentity = candidateComponentIdentity(target, values)
  const sourceMappingKind = profile.operations?.jit?.sourceMappingKind ?? 'not-applicable'
  const jitPath = jitLibraryPath(profile, capabilities)
  const preflightProfile = materializePreflightProfile(profile, image, capabilities)
  const preflightProfileBytes = Buffer.from(`${JSON.stringify(preflightProfile, null, 2)}\n`, 'utf8')
  const plan = {
    schemaVersion: 1,
    profileId,
    profileSha256: sha256(profileBytes),
    matrixTargetId: binding.matrixTargetId,
    platform: platformForFamily(profile.family),
    family: profile.family,
    resolvedVersion: profile.runtimeVersion,
    image: {
      reference: image.reference,
      imageId: image.imageId,
      sizeBytes: image.sizeBytes,
    },
    componentIdentity,
    runtimeIdentity: {
      runtimeCommit: profile.runtimeCommit,
      jitVersion: profile.jitVersion,
      jitCommit: profile.jitCommit,
    },
    sourceRevision: values.SOURCE_REVISION,
    createdAtUtc: canonicalTimestamp(
      verifyExisting ? existingPlan?.createdAtUtc : now(),
    ),
    producer: {
      id: runtimePromotionPlanProducerId,
      sourceRevision: values.SOURCE_REVISION,
    },
    securityPolicyId,
    capabilities,
    sourceMappingKind,
    operations,
    ...(jitPath === undefined ? {} : { jitLibraryPath: jitPath }),
    preflightProfile: {
      path: preflightProfilePath,
      sha256: sha256(preflightProfileBytes),
    },
    performance: {
      policyId: policy.id,
      policyPath: performancePolicyPath,
      policySha256: sha256(policyBytes),
      evidencePath: performanceEvidencePath,
    },
  }
  const planBytes = Buffer.from(`${JSON.stringify(plan, null, 2)}\n`, 'utf8')
  beforeRecheck?.()
  {
    const repeatedFailures = validateCandidateInputs(target, values)
    if (repeatedFailures.length > 0) {
      throw new RuntimePromotionPlanError(
        `Runtime candidate inputs drifted before commit:\n- ${repeatedFailures.join('\n- ')}`,
      )
    }
    requireUnchanged(
      readRegularFile(profileAbsolutePath, 'candidate Runtime Profile'),
      profileBytes,
      'The candidate Runtime Profile',
    )
    requireUnchanged(
      readRegularFile(matrixAbsolutePath, 'runtime matrix'),
      matrixBytes,
      'The runtime matrix',
    )
    requireUnchanged(
      readRegularFile(policyAbsolutePath, 'runtime performance policy'),
      policyBytes,
      'The runtime performance policy',
    )
    const repeatedSourceState = inspectGit({
      cwd: repositoryRoot,
      env: values,
      allowedDirtyPaths: allowedGeneratedPaths,
    })
    const repeatedSourceBinding = validateGit(repeatedSourceState, values.SOURCE_REVISION)
    if (repeatedSourceBinding.failures.length > 0 || !repeatedSourceBinding.promotionEligible ||
        !sameJson(repeatedSourceState, sourceState)) {
      throw new RuntimePromotionPlanError('The Git source state changed before the plan commit.')
    }
    const repeatedImage = bindRuntimeCandidateImage({
      candidateReference,
      pinnedReference,
      sourceRevision: values.SOURCE_REVISION,
      expectedLabels,
      inspect,
    })
    if (!sameJson(repeatedImage, image)) {
      throw new RuntimePromotionPlanError('The candidate image binding changed before the plan commit.')
    }
    const repeatedOperations = hashOperations(image.imageId, helperSpecifications, hashOptions)
    if (!sameJson(repeatedOperations, operations)) {
      throw new RuntimePromotionPlanError('Runtime helper bytes changed before the plan commit.')
    }
    if (verifyExisting) {
      requireUnchanged(
        readRegularFile(
          path.join(repositoryRoot, ...relativeOutputPath.split('/')),
          'installed runtime promotion plan',
        ),
        existingPlanBytes,
        'The installed runtime promotion plan',
      )
      requireUnchanged(existingPlanBytes, planBytes, 'The installed runtime promotion plan')
      requireUnchanged(
        readRegularFile(
          path.join(repositoryRoot, ...preflightProfilePath.split('/')),
          'installed runtime promotion preflight profile',
        ),
        existingPreflightProfileBytes,
        'The installed runtime promotion preflight profile',
      )
      requireUnchanged(
        existingPreflightProfileBytes,
        preflightProfileBytes,
        'The installed runtime promotion preflight profile',
      )
    } else {
      const preflightProfileStage = createAtomicStage(
        repositoryRoot,
        preflightProfilePath,
        preflightProfileBytes,
      )
      const planStage = createAtomicStage(repositoryRoot, relativeOutputPath, planBytes)
      installAtomicStages([preflightProfileStage, planStage], beforeStageInstall)
    }
  }

  return Object.freeze({
    profileId,
    outputPath: path.join(repositoryRoot, ...relativeOutputPath.split('/')),
    preflightProfilePath: path.join(repositoryRoot, ...preflightProfilePath.split('/')),
    plan: Object.freeze(plan),
    planSha256: sha256(planBytes),
    preflightProfileSha256: sha256(preflightProfileBytes),
  })
}

function parseArguments(argv) {
  const [target, ...arguments_] = argv
  if (target === undefined) throw new RuntimePromotionPlanError('A candidate target is required.')
  const parsed = { target, check: false }
  const optionNames = new Map([
    ['--profile', 'profilePath'],
    ['--pinned-reference', 'pinnedReference'],
    ['--performance-policy', 'performancePolicyPath'],
  ])
  for (let index = 0; index < arguments_.length;) {
    const name = arguments_[index]
    if (name === '--check') {
      if (parsed.check) {
        throw new RuntimePromotionPlanError("Invalid or duplicate promotion plan option '--check'.")
      }
      parsed.check = true
      index++
      continue
    }
    const field = optionNames.get(name)
    const value = arguments_[index + 1]
    if (field === undefined || value === undefined || value.length === 0 || parsed[field] !== undefined) {
      throw new RuntimePromotionPlanError(`Invalid or duplicate promotion plan option '${name}'.`)
    }
    parsed[field] = value
    index += 2
  }
  for (const field of optionNames.values()) {
    if (parsed[field] === undefined) throw new RuntimePromotionPlanError(`Missing required ${field}.`)
  }
  return parsed
}

export function runRuntimePromotionPlan(argv, options = {}) {
  const output = options.output ?? console
  if (argv.length === 1 && (argv[0] === '--help' || argv[0] === '-h')) {
    output.log(runtimePromotionPlanUsage)
    return 0
  }
  try {
    const parsed = parseArguments(argv)
    const result = parsed.check
      ? verifyRuntimePromotionPlan(parsed, options)
      : produceRuntimePromotionPlan(parsed, options)
    output.log(
      parsed.check
        ? `Verified ${result.outputPath} as ${result.planSha256}; no files were written.`
        : `Wrote ${result.outputPath} as ${result.planSha256}; ` +
          'no capability evidence or promotion receipt was created.',
    )
    return 0
  } catch (error) {
    output.error(`runtime promotion plan error: ${error.message}`)
    output.error(runtimePromotionPlanUsage)
    return error instanceof RuntimePromotionPlanError ? 1 : 2
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimePromotionPlan(process.argv.slice(2))
}
