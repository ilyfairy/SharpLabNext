import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'

import { parseOwnedJson } from './strict-owned-json.mjs'

const evidenceDirectory = 'profiles/runtime-promotion-evidence'
const policyDirectory = 'profiles/runtime-performance-policies'
const maximumMaterialBytes = 1024 * 1024
const digestPattern = /^sha256:[0-9a-f]{64}$/
const immutableReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const sourceRevisionPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const measurementHelperImplementation = 'sharplabnext-runtime-cgroup-sidecar-v1'
const measurementHelperEntrypoint = '/usr/local/bin/sharplabnext-runtime-measurement'
const measurementHelperContentSha256 =
  'sha256:f7645af4191d024c86769f3e39fd76ad237f537572c752fdfec3ff529aea9e4c'

const absoluteLimits = Object.freeze({
  minimumColdSamples: 3,
  maximumColdSamples: 20,
  minimumWarmSamples: 5,
  maximumWarmSamples: 50,
  minimumNanoCpus: 250_000_000,
  maximumNanoCpus: 4_000_000_000,
  minimumMemoryBytes: 134_217_728,
  maximumMemoryBytes: 2_147_483_648,
  maximumImageSizeBytes: 17_179_869_184,
  maximumP95LatencyMilliseconds: 60_000,
  maximumSampleLatencyMilliseconds: 120_000,
})

export function validateRuntimePerformanceEvidence({
  binding,
  receipt,
  declaredCapabilities,
  repositoryRoot,
  readFile = fs.readFileSync,
}) {
  const failures = []
  const prefix = `${binding.profileId}: promotion receipt performance`
  const performance = receipt.performance
  if (!expectObject(
    performance,
    ['result', 'policyId', 'policyPath', 'policySha256', 'evidencePath', 'evidenceSha256'],
    prefix,
    failures,
  )) return failures

  if (performance.result !== 'passed') failures.push(`${prefix} result must equal "passed"`)
  if (!isId(performance.policyId)) failures.push(`${prefix} policyId is not canonical`)
  const expectedPolicyPath = `${policyDirectory}/${performance.policyId}.json`
  if (performance.policyPath !== expectedPolicyPath) {
    failures.push(`${prefix} policyPath must equal ${JSON.stringify(expectedPolicyPath)}`)
  }
  const expectedEvidencePath = `${evidenceDirectory}/${binding.profileId}/performance.json`
  if (performance.evidencePath !== expectedEvidencePath) {
    failures.push(`${prefix} evidencePath must equal ${JSON.stringify(expectedEvidencePath)}`)
  }
  if (!digestPattern.test(performance.policySha256 ?? '')) {
    failures.push(`${prefix} policySha256 is not canonical`)
  }
  if (!digestPattern.test(performance.evidenceSha256 ?? '')) {
    failures.push(`${prefix} evidenceSha256 is not canonical`)
  }
  if (!isPositiveInteger(receipt.image?.sizeBytes) ||
      receipt.image.sizeBytes > absoluteLimits.maximumImageSizeBytes) {
    failures.push(`${prefix} image size must be a positive bounded integer`)
  }

  const policy = readTrustedJson({
    repositoryRoot,
    relativePath: performance.policyPath,
    expectedPath: expectedPolicyPath,
    trustedDirectories: [policyDirectory],
    expectedDigest: performance.policySha256,
    label: `${prefix} policy`,
    readFile,
    failures,
  })
  const evidence = readTrustedJson({
    repositoryRoot,
    relativePath: performance.evidencePath,
    expectedPath: expectedEvidencePath,
    trustedDirectories: [evidenceDirectory, `${evidenceDirectory}/${binding.profileId}`],
    expectedDigest: performance.evidenceSha256,
    label: `${prefix} evidence`,
    readFile,
    failures,
  })
  if (policy === undefined || evidence === undefined) return failures

  if (!validatePolicy(policy, performance, prefix, failures)) return failures
  validateEvidence(
    evidence,
    policy,
    performance,
    receipt,
    binding,
    declaredCapabilities,
    prefix,
    failures,
  )
  return failures
}

function validatePolicy(policy, performance, prefix, failures) {
  if (!expectObject(
    policy,
    ['schemaVersion', 'id', 'sampleCounts', 'resourceLimits', 'image', 'scenarios'],
    `${prefix} policy`,
    failures,
  )) return false
  if (policy.schemaVersion !== 1) failures.push(`${prefix} policy schemaVersion must equal 1`)
  if (policy.id !== performance.policyId) failures.push(`${prefix} policy id does not match policyId`)

  if (expectObject(policy.sampleCounts, ['cold', 'warm'], `${prefix} policy sampleCounts`, failures)) {
    validateIntegerRange(
      policy.sampleCounts.cold,
      absoluteLimits.minimumColdSamples,
      absoluteLimits.maximumColdSamples,
      `${prefix} policy cold sample count`,
      failures,
    )
    validateIntegerRange(
      policy.sampleCounts.warm,
      absoluteLimits.minimumWarmSamples,
      absoluteLimits.maximumWarmSamples,
      `${prefix} policy warm sample count`,
      failures,
    )
  }

  if (expectObject(
    policy.resourceLimits,
    ['nanoCpus', 'allowedMemoryBytes'],
    `${prefix} policy resourceLimits`,
    failures,
  )) {
    validateIntegerRange(
      policy.resourceLimits.nanoCpus,
      absoluteLimits.minimumNanoCpus,
      absoluteLimits.maximumNanoCpus,
      `${prefix} policy nanoCpus`,
      failures,
    )
    const memory = policy.resourceLimits.allowedMemoryBytes
    if (!Array.isArray(memory) || memory.length < 1 || memory.length > 8 ||
        memory.some(value => !isIntegerInRange(
          value,
          absoluteLimits.minimumMemoryBytes,
          absoluteLimits.maximumMemoryBytes,
        )) || new Set(memory).size !== memory.length ||
        memory.some((value, index) => index > 0 && value <= memory[index - 1])) {
      failures.push(`${prefix} policy allowedMemoryBytes must be 1-8 unique ascending bounded integers`)
    }
  }

  if (expectObject(policy.image, ['maximumSizeBytes'], `${prefix} policy image`, failures)) {
    validateIntegerRange(
      policy.image.maximumSizeBytes,
      1,
      absoluteLimits.maximumImageSizeBytes,
      `${prefix} policy maximum image size`,
      failures,
    )
  }

  if (expectObject(policy.scenarios, ['run', 'jit', 'mapping'], `${prefix} policy scenarios`, failures)) {
    for (const scenario of ['run', 'jit', 'mapping']) {
      const scenarioPolicy = policy.scenarios[scenario]
      if (!expectObject(
        scenarioPolicy,
        ['cold', 'warm'],
        `${prefix} policy ${scenario}`,
        failures,
      )) continue
      validateModePolicy(scenarioPolicy.cold, `${prefix} policy ${scenario}.cold`, failures)
      validateModePolicy(scenarioPolicy.warm, `${prefix} policy ${scenario}.warm`, failures)
    }
  }
  return failures.length === 0
}

function validateModePolicy(mode, label, failures) {
  if (!expectObject(
    mode,
    [
      'maximumP95LatencyMilliseconds',
      'maximumSampleLatencyMilliseconds',
      'maximumPeakMemoryBytes',
    ],
    label,
    failures,
  )) return
  validateNumberRange(
    mode.maximumP95LatencyMilliseconds,
    Number.MIN_VALUE,
    absoluteLimits.maximumP95LatencyMilliseconds,
    `${label} maximumP95LatencyMilliseconds`,
    failures,
  )
  validateNumberRange(
    mode.maximumSampleLatencyMilliseconds,
    Number.MIN_VALUE,
    absoluteLimits.maximumSampleLatencyMilliseconds,
    `${label} maximumSampleLatencyMilliseconds`,
    failures,
  )
  validateIntegerRange(
    mode.maximumPeakMemoryBytes,
    1,
    absoluteLimits.maximumMemoryBytes,
    `${label} maximumPeakMemoryBytes`,
    failures,
  )
  if (isPositiveNumber(mode.maximumP95LatencyMilliseconds) &&
      isPositiveNumber(mode.maximumSampleLatencyMilliseconds) &&
      mode.maximumP95LatencyMilliseconds > mode.maximumSampleLatencyMilliseconds) {
    failures.push(`${label} P95 limit cannot exceed its single-sample limit`)
  }
}

function validateEvidence(
  evidence,
  policy,
  performance,
  receipt,
  binding,
  declaredCapabilities,
  prefix,
  failures,
) {
  if (!expectObject(
    evidence,
    [
      'schemaVersion',
      'planSha256',
      'profileId',
      'image',
      'measurementHelper',
      'sourceRevision',
      'policy',
      'capabilities',
      'sourceMappingKind',
      'environment',
      'completedAtUtc',
      'result',
      'scenarios',
    ],
    `${prefix} evidence`,
    failures,
  )) return
  if (evidence.schemaVersion !== 1) failures.push(`${prefix} evidence schemaVersion must equal 1`)
  if (evidence.planSha256 !== receipt.planSha256) failures.push(`${prefix} evidence planSha256 mismatch`)
  if (evidence.profileId !== binding.profileId) failures.push(`${prefix} evidence profileId mismatch`)
  if (evidence.sourceRevision !== receipt.sourceRevision) {
    failures.push(`${prefix} evidence sourceRevision mismatch`)
  }
  if (evidence.result !== 'passed') failures.push(`${prefix} evidence result must equal "passed"`)
  if (typeof evidence.completedAtUtc !== 'string' ||
      !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(evidence.completedAtUtc) ||
      !Number.isFinite(Date.parse(evidence.completedAtUtc))) {
    failures.push(`${prefix} evidence completedAtUtc must be a valid canonical UTC timestamp`)
  }

  if (expectObject(evidence.image, ['reference', 'imageId', 'sizeBytes'], `${prefix} evidence image`, failures)) {
    for (const field of ['reference', 'imageId', 'sizeBytes']) {
      if (evidence.image[field] !== receipt.image?.[field]) {
        failures.push(`${prefix} evidence image.${field} mismatch`)
      }
    }
    if (isPositiveInteger(evidence.image.sizeBytes) &&
        evidence.image.sizeBytes > policy.image.maximumSizeBytes) {
      failures.push(
        `${prefix} image size ${evidence.image.sizeBytes} exceeds policy ` +
        `${policy.image.maximumSizeBytes}`,
      )
    }
  }
  validateMeasurementHelper(evidence.measurementHelper, evidence, receipt, prefix, failures)
  if (expectObject(evidence.policy, ['id', 'sha256'], `${prefix} evidence policy`, failures)) {
    if (evidence.policy.id !== performance.policyId ||
        evidence.policy.sha256 !== performance.policySha256) {
      failures.push(`${prefix} evidence policy identity mismatch`)
    }
  }

  const expectedCapabilities = [...new Set(declaredCapabilities)].sort()
  if (!Array.isArray(evidence.capabilities) ||
      JSON.stringify(evidence.capabilities) !== JSON.stringify(expectedCapabilities)) {
    failures.push(
      `${prefix} evidence capabilities must be canonical and exactly [${expectedCapabilities.join(', ')}]`,
    )
  }
  const jitCheck = receipt.checks?.find(check => check?.capability === 'jit-asm')
  const expectedMappingKind = jitCheck?.sourceMappingKind ?? 'not-applicable'
  if (evidence.sourceMappingKind !== expectedMappingKind) {
    failures.push(`${prefix} evidence sourceMappingKind mismatch`)
  }

  if (expectObject(
    evidence.environment,
    ['runnerId', 'operatingSystem', 'architecture', 'nanoCpus', 'memoryLimitBytes'],
    `${prefix} evidence environment`,
    failures,
  )) {
    if (evidence.environment.runnerId !== 'runtime-preflight-linux-x64-v2') {
      failures.push(`${prefix} evidence runnerId must equal "runtime-preflight-linux-x64-v2"`)
    }
    if (evidence.environment.operatingSystem !== 'linux' ||
        evidence.environment.architecture !== 'x64') {
      failures.push(`${prefix} evidence environment must be Linux x64`)
    }
    if (evidence.environment.nanoCpus !== policy.resourceLimits.nanoCpus) {
      failures.push(`${prefix} evidence nanoCpus does not match policy`)
    }
    if (!policy.resourceLimits.allowedMemoryBytes.includes(evidence.environment.memoryLimitBytes)) {
      failures.push(`${prefix} evidence memoryLimitBytes is not allowed by policy`)
    }
  }

  const expectedScenarios = ['run']
  if (expectedCapabilities.includes('jit-asm')) expectedScenarios.push('jit')
  if (!['not-applicable', 'none'].includes(expectedMappingKind)) expectedScenarios.push('mapping')
  expectedScenarios.sort()
  if (!expectObject(evidence.scenarios, expectedScenarios, `${prefix} evidence scenarios`, failures)) return
  const operationIds = new Set()
  for (const scenario of expectedScenarios) {
    validateScenario(
      scenario,
      evidence.scenarios[scenario],
      policy,
      evidence.environment?.memoryLimitBytes,
      operationIds,
      prefix,
      failures,
    )
  }
}

function validateMeasurementHelper(helper, evidence, receipt, prefix, failures) {
  const label = `${prefix} evidence measurementHelper`
  if (!expectObject(
    helper,
    ['implementation', 'image', 'entrypoint', 'sourceRevision', 'contentSha256'],
    label,
    failures,
  )) return
  if (helper.implementation !== measurementHelperImplementation) {
    failures.push(`${label} implementation must equal ${JSON.stringify(measurementHelperImplementation)}`)
  }
  if (helper.entrypoint !== measurementHelperEntrypoint) {
    failures.push(`${label} entrypoint must equal ${JSON.stringify(measurementHelperEntrypoint)}`)
  }
  if (helper.contentSha256 !== measurementHelperContentSha256) {
    failures.push(`${label} contentSha256 must equal the pinned helper script digest`)
  }
  if (!sourceRevisionPattern.test(helper.sourceRevision ?? '') ||
      helper.sourceRevision !== evidence.sourceRevision ||
      helper.sourceRevision !== receipt.sourceRevision) {
    failures.push(`${label} sourceRevision must equal the evidence sourceRevision`)
  }
  if (!expectObject(helper.image, ['reference', 'imageId', 'sizeBytes'], `${label} image`, failures)) {
    return
  }
  if (!immutableReferencePattern.test(helper.image.reference ?? '') ||
      imageRepositoryName(helper.image.reference) !== 'runtime-supervisor') {
    failures.push(`${label} image reference must be an immutable runtime-supervisor repository reference`)
  }
  if (!digestPattern.test(helper.image.imageId ?? '')) {
    failures.push(`${label} imageId is not canonical`)
  }
  if (!isIntegerInRange(helper.image.sizeBytes, 1, absoluteLimits.maximumImageSizeBytes)) {
    failures.push(`${label} image size must be a positive bounded integer`)
  }
  if (helper.image.reference === evidence.image?.reference ||
      helper.image.imageId === evidence.image?.imageId) {
    failures.push(`${label} image must be distinct from the candidate runtime image`)
  }
}

function imageRepositoryName(reference) {
  const digest = reference.lastIndexOf('@sha256:')
  if (digest <= 0) return undefined
  const repository = reference.slice(0, digest)
  return repository.slice(repository.lastIndexOf('/') + 1)
}

function validateScenario(name, scenario, policy, memoryLimitBytes, operationIds, prefix, failures) {
  if (!expectObject(scenario, ['cold', 'warm'], `${prefix} evidence ${name}`, failures)) return
  validateSamples(
    scenario.cold,
    policy.sampleCounts.cold,
    policy.scenarios[name].cold,
    memoryLimitBytes,
    operationIds,
    `${prefix} evidence ${name}.cold`,
    failures,
  )
  validateSamples(
    scenario.warm,
    policy.sampleCounts.warm,
    policy.scenarios[name].warm,
    memoryLimitBytes,
    operationIds,
    `${prefix} evidence ${name}.warm`,
    failures,
  )
}

function validateSamples(
  samples,
  expectedCount,
  budget,
  memoryLimitBytes,
  operationIds,
  label,
  failures,
) {
  if (!Array.isArray(samples) || samples.length !== expectedCount) {
    failures.push(`${label} must contain exactly ${expectedCount} samples`)
    return
  }
  const latencies = []
  samples.forEach((sample, index) => {
    const sampleLabel = `${label}[${index}]`
    if (!expectObject(
      sample,
      [
        'latencyMilliseconds',
        'peakMemoryBytes',
        'completionPeakMemoryBytes',
        'operationId',
        'resourceSampleCount',
        'postCompletionResourceSampleCount',
        'completedAtUtc',
      ],
      sampleLabel,
      failures,
    )) return
    if (typeof sample.operationId !== 'string' || !/^op_[0-9a-f]{32}$/.test(sample.operationId)) {
      failures.push(`${sampleLabel} operationId must be canonical`)
    } else if (operationIds.has(sample.operationId)) {
      failures.push(`${sampleLabel} operationId is duplicated`)
    } else {
      operationIds.add(sample.operationId)
    }
    if (!isIntegerInRange(sample.resourceSampleCount, 1, 1_000_000)) {
      failures.push(`${sampleLabel} resourceSampleCount must be a positive bounded integer`)
    }
    if (!isIntegerInRange(sample.postCompletionResourceSampleCount, 1, 1_000_000)) {
      failures.push(
        `${sampleLabel} postCompletionResourceSampleCount must be a positive bounded integer`,
      )
    }
    if (isPositiveInteger(sample.resourceSampleCount) &&
        isPositiveInteger(sample.postCompletionResourceSampleCount) &&
        sample.resourceSampleCount < sample.postCompletionResourceSampleCount) {
      failures.push(
        `${sampleLabel} resourceSampleCount cannot be less than postCompletionResourceSampleCount`,
      )
    }
    if (typeof sample.completedAtUtc !== 'string' ||
        !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(sample.completedAtUtc) ||
        !Number.isFinite(Date.parse(sample.completedAtUtc))) {
      failures.push(`${sampleLabel} completedAtUtc must be a canonical UTC timestamp`)
    }
    if (!isPositiveNumber(sample.latencyMilliseconds)) {
      failures.push(`${sampleLabel} latencyMilliseconds must be positive and finite`)
    } else {
      latencies.push(sample.latencyMilliseconds)
      if (sample.latencyMilliseconds > budget.maximumSampleLatencyMilliseconds) {
        failures.push(`${sampleLabel} latency exceeds the single-sample budget`)
      }
    }
    if (!isPositiveInteger(sample.peakMemoryBytes)) {
      failures.push(`${sampleLabel} peakMemoryBytes must be a positive integer`)
    } else {
      if (sample.peakMemoryBytes > budget.maximumPeakMemoryBytes) {
        failures.push(`${sampleLabel} peak memory exceeds the scenario budget`)
      }
      if (!isPositiveInteger(memoryLimitBytes) || sample.peakMemoryBytes > memoryLimitBytes) {
        failures.push(`${sampleLabel} peak memory exceeds the measured container limit`)
      }
    }
    if (!isPositiveInteger(sample.completionPeakMemoryBytes)) {
      failures.push(`${sampleLabel} completionPeakMemoryBytes must be a positive integer`)
    } else if (isPositiveInteger(sample.peakMemoryBytes) &&
        sample.peakMemoryBytes < sample.completionPeakMemoryBytes) {
      failures.push(`${sampleLabel} peakMemoryBytes cannot be less than completionPeakMemoryBytes`)
    }
  })
  if (latencies.length === expectedCount) {
    const p95 = nearestRankPercentile(latencies, 0.95)
    if (p95 > budget.maximumP95LatencyMilliseconds) {
      failures.push(`${label} P95 latency ${p95} exceeds policy ${budget.maximumP95LatencyMilliseconds}`)
    }
  }
}

function nearestRankPercentile(values, percentile) {
  const sorted = [...values].sort((left, right) => left - right)
  return sorted[Math.max(0, Math.ceil(sorted.length * percentile) - 1)]
}

function readTrustedJson({
  repositoryRoot,
  relativePath,
  expectedPath,
  trustedDirectories,
  expectedDigest,
  label,
  readFile,
  failures,
}) {
  if (relativePath !== expectedPath || typeof relativePath !== 'string' ||
      relativePath.includes('\\') || relativePath.split('/').includes('..')) return undefined
  if (!digestPattern.test(expectedDigest ?? '')) return undefined

  const absolutePath = path.resolve(repositoryRoot, ...relativePath.split('/'))
  const trustedPaths = trustedDirectories.map(directory =>
    path.resolve(repositoryRoot, ...directory.split('/')))
  if (!trustedPaths.every((directory, index) =>
    index === 0 || isPathInside(trustedPaths[index - 1], directory)) ||
    !isPathInside(trustedPaths.at(-1), absolutePath)) {
    failures.push(`${label} escapes its trusted directory`)
    return undefined
  }

  let bytes
  try {
    for (const directory of trustedPaths) {
      const stat = fs.lstatSync(directory)
      if (!stat.isDirectory() || stat.isSymbolicLink()) {
        failures.push(`${label} must be below regular non-link directories`)
        return undefined
      }
    }
    const stat = fs.lstatSync(absolutePath)
    if (!stat.isFile() || stat.isSymbolicLink()) {
      failures.push(`${label} must be a regular non-link file`)
      return undefined
    }
    const realDirectories = trustedPaths.map(directory => fs.realpathSync.native(directory))
    const realFile = fs.realpathSync.native(absolutePath)
    if (!realDirectories.every((directory, index) =>
      index === 0 || isPathInside(realDirectories[index - 1], directory)) ||
      !isPathInside(realDirectories.at(-1), realFile)) {
      failures.push(`${label} resolves outside its trusted directory`)
      return undefined
    }
    if (stat.size > maximumMaterialBytes) {
      failures.push(`${label} exceeds the 1 MiB size limit`)
      return undefined
    }
    bytes = readFile(absolutePath)
    if (bytes.length > maximumMaterialBytes) {
      failures.push(`${label} exceeds the 1 MiB size limit`)
      return undefined
    }
  } catch (error) {
    failures.push(`${label} cannot be read (${error.message})`)
    return undefined
  }

  const actualDigest = `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
  if (!constantTimeEqual(expectedDigest, actualDigest)) {
    failures.push(`${label} digest mismatch; expected ${expectedDigest}, observed ${actualDigest}`)
    return undefined
  }
  return parseOwnedJson(bytes, label, failures)
}

function expectObject(value, expectedKeys, label, failures) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    failures.push(`${label} must be an object`)
    return false
  }
  const observed = Object.keys(value).sort()
  const expected = [...expectedKeys].sort()
  if (JSON.stringify(observed) !== JSON.stringify(expected)) {
    failures.push(
      `${label} must contain exactly [${expected.join(', ')}]; observed [${observed.join(', ')}]`,
    )
    return false
  }
  return true
}

function validateIntegerRange(value, minimum, maximum, label, failures) {
  if (!isIntegerInRange(value, minimum, maximum)) {
    failures.push(`${label} must be an integer between ${minimum} and ${maximum}`)
  }
}

function validateNumberRange(value, minimumExclusive, maximum, label, failures) {
  if (!Number.isFinite(value) || value <= minimumExclusive || value > maximum) {
    failures.push(`${label} must be greater than ${minimumExclusive} and at most ${maximum}`)
  }
}

function isIntegerInRange(value, minimum, maximum) { return Number.isSafeInteger(value) && value >= minimum && value <= maximum; }

function isPositiveInteger(value) { return Number.isSafeInteger(value) && value > 0; }

function isPositiveNumber(value) { return Number.isFinite(value) && value > 0; }

function isId(value) { return typeof value === 'string' && value.length <= 128 && /^[a-z0-9][a-z0-9._-]*$/.test(value); }

function isPathInside(root, candidate) {
  const relative = path.relative(root, candidate)
  return relative.length > 0 && relative !== '..' && !relative.startsWith(`..${path.sep}`) &&
    !path.isAbsolute(relative)
}

function constantTimeEqual(left, right) {
  if (typeof left !== 'string' || left.length !== right.length) return false
  return crypto.timingSafeEqual(Buffer.from(left, 'ascii'), Buffer.from(right, 'ascii'))
}
