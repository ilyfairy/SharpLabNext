import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import { validateRuntimePromotionReceipts as validateRuntimePromotionReceiptsImpl } from './runtime-promotion-receipt-validation.mjs'
import { validateJsonSchemaInstance } from './json-schema-instance-validation.mjs'
import {
  createWineCoreClrOperatorReceipt,
  serializeWineCoreClrOperatorReceipt,
  signWineCoreClrOperatorReceipt,
  wineCoreClrOperatorCommittedFiles,
} from './wine-coreclr-operator-receipt.mjs'
import {
  runtimePromotionPlanSignaturePath,
  serializeRuntimePromotionPlan,
  signRuntimePromotionPlan,
} from './runtime-promotion-plan-signature.mjs'

const hex = character => character.repeat(64)
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const performancePolicyRelativePath =
  'profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json'
const performancePolicySourcePath = path.join(
  repositoryRoot,
  ...performancePolicyRelativePath.split('/'),
)
const planSha256 = `sha256:${hex('0')}`
const operatorKeys = crypto.generateKeyPairSync('ed25519')
const planKeys = crypto.generateKeyPairSync('ed25519')
const planKeyId = `sha256:${crypto.createHash('sha256').update(
  planKeys.publicKey.export({ type: 'spki', format: 'der' }),
).digest('hex')}`
const operatorSourceTree = 'f'.repeat(40)
const operatorSourceFiles = Object.fromEntries(wineCoreClrOperatorCommittedFiles.map(relative => [
  relative, Buffer.from(`committed:${relative}`),
]))

function validateRuntimePromotionReceipts(matrixValue, root, readFile) {
  return validateRuntimePromotionReceiptsImpl(matrixValue, root, readFile, {
    operatorReceiptPublicKey: operatorKeys.publicKey,
    planSignaturePublicKey: planKeys.publicKey,
    planSignatureKeyId: planKeyId,
    gitShow(arguments_) {
      if (arguments_[0] === 'rev-parse') return Buffer.from(`${operatorSourceTree}\n`)
      return operatorSourceFiles[arguments_[1].slice(arguments_[1].indexOf(':') + 1)]
    },
  })
}

function receipt(profileId = 'wine-netfx48-linux-x64') {
  return {
    schemaVersion: 2,
    profileId,
    matrixTargetId: 'netfx48',
    platform: 'framework',
    family: 'netfx-clr-wine',
    resolvedVersion: '4.8',
    image: {
      reference: `registry.example/runtime@sha256:${hex('a')}`,
      imageId: `sha256:${hex('b')}`,
      sizeBytes: 536870912,
    },
    componentIdentity: {
      sourceUri: `docker://registry.example/operator@sha256:${hex('9')}`,
      sourceDigest: `sha256:${hex('9')}`,
    },
    runtimeIdentity: {
      runtimeCommit: 'not-applicable',
      jitVersion: 'not-applicable',
      jitCommit: 'not-applicable',
    },
    operations: {
      run: {
        implementation: 'sharplabnext-target-runtime-runner-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
        assemblySha256: `sha256:${hex('c')}`,
      },
    },
    sourceRevision: 'd'.repeat(40),
    planSha256,
    checks: [{
      capability: 'run',
      result: 'passed',
      networkDisabled: true,
      supervisorSandbox: true,
      outputLimitValidated: true,
      mappingSource: 'not-applicable',
      sourceMappingKind: 'not-applicable',
      evidenceSha256: `sha256:${hex('e')}`,
    }],
  }
}

function matrix(reference, capabilities = ['run']) {
  return {
    coreClr: [],
    framework: {
      targets: [{
        id: 'netfx48',
        version: '4.8',
        capability: {
          capabilities,
          promotionState: 'verified',
          promotionReceipt: reference,
        },
      }],
    },
  }
}

function writeFixture(
  value = receipt(),
  {
    executionUser = defaultExecutionUser(value),
    withPromotionPlan = true,
  } = {},
) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-runtime-receipt-'))
  const schemaDirectory = path.join(root, 'schemas')
  fs.mkdirSync(schemaDirectory, { recursive: true })
  for (const schemaName of [
    'runtime-promotion-plan.schema.json',
    'runtime-promotion-receipt.schema.json',
  ]) {
    fs.copyFileSync(
      path.join(repositoryRoot, 'schemas', schemaName),
      path.join(schemaDirectory, schemaName),
    )
  }
  const profile = runtimeProfile(value, executionUser)
  const profilePath = path.join(
    root,
    'profiles',
    'runtimes',
    'candidates',
    `${value.profileId}.json`,
  )
  fs.mkdirSync(path.dirname(profilePath), { recursive: true })
  fs.writeFileSync(profilePath, `${JSON.stringify(profile, null, 2)}\n`)
  const evidencePaths = {}
  for (const check of value.checks) {
    const relativeEvidencePath =
      `profiles/runtime-promotion-evidence/${value.profileId}/${check.capability}.json`
    const absoluteEvidencePath = path.join(root, ...relativeEvidencePath.split('/'))
    const evidenceBytes = Buffer.from(
      `${JSON.stringify(capabilityEvidence(value, check, executionUser), null, 2)}\n`,
    )
    fs.mkdirSync(path.dirname(absoluteEvidencePath), { recursive: true })
    fs.writeFileSync(absoluteEvidencePath, evidenceBytes)
    check.evidencePath ??= relativeEvidencePath
    check.evidenceSha256 =
      `sha256:${crypto.createHash('sha256').update(evidenceBytes).digest('hex')}`
    evidencePaths[check.capability] = absoluteEvidencePath
  }

  const policyBytes = fs.readFileSync(performancePolicySourcePath)
  const policyPath = path.join(root, ...performancePolicyRelativePath.split('/'))
  fs.mkdirSync(path.dirname(policyPath), { recursive: true })
  fs.writeFileSync(policyPath, policyBytes)
  const policySha256 =
    `sha256:${crypto.createHash('sha256').update(policyBytes).digest('hex')}`
  const performanceEvidenceRelativePath =
    `profiles/runtime-promotion-evidence/${value.profileId}/performance.json`
  const performanceEvidencePath = path.join(
    root,
    ...performanceEvidenceRelativePath.split('/'),
  )
  const capabilities = value.checks.map(check => check.capability).sort()
  const jitCheck = value.checks.find(check => check.capability === 'jit-asm')
  const scenarios = { run: performanceScenario() }
  if (jitCheck !== undefined) scenarios.jit = performanceScenario()
  if (jitCheck !== undefined && !['none', 'not-applicable'].includes(jitCheck.sourceMappingKind)) {
    scenarios.mapping = performanceScenario()
  }
  const performanceEvidence = {
    schemaVersion: 1,
    profileId: value.profileId,
    planSha256: value.planSha256,
    image: { ...value.image },
    measurementHelper: {
      implementation: 'sharplabnext-runtime-cgroup-sidecar-v1',
      image: {
        reference: `registry.example/runtime-supervisor@sha256:${'7'.repeat(64)}`,
        imageId: `sha256:${'8'.repeat(64)}`,
        sizeBytes: 536870912,
      },
      entrypoint: '/usr/local/bin/sharplabnext-runtime-measurement',
      sourceRevision: value.sourceRevision,
      contentSha256:
        'sha256:f7645af4191d024c86769f3e39fd76ad237f537572c752fdfec3ff529aea9e4c',
    },
    sourceRevision: value.sourceRevision,
    policy: {
      id: 'runtime-image-linux-x64-v1',
      sha256: policySha256,
    },
    capabilities,
    sourceMappingKind: jitCheck?.sourceMappingKind ?? 'not-applicable',
    environment: {
      runnerId: 'runtime-preflight-linux-x64-v2',
      operatingSystem: 'linux',
      architecture: 'x64',
      nanoCpus: 1000000000,
      memoryLimitBytes: 268435456,
    },
    completedAtUtc: '2026-07-22T00:00:00Z',
    result: 'passed',
    scenarios,
  }
  const performanceEvidenceBytes = Buffer.from(`${JSON.stringify(performanceEvidence, null, 2)}\n`)
  fs.writeFileSync(performanceEvidencePath, performanceEvidenceBytes)
  value.performance = {
    result: 'passed',
    policyId: 'runtime-image-linux-x64-v1',
    policyPath: performancePolicyRelativePath,
    policySha256,
    evidencePath: performanceEvidenceRelativePath,
    evidenceSha256:
      `sha256:${crypto.createHash('sha256').update(performanceEvidenceBytes).digest('hex')}`,
  }

  if (value.family === 'coreclr-wine' || value.family === 'netfx-clr-wine') {
    const sourceRevision = value.sourceRevision
    const operator = createWineCoreClrOperatorReceipt({
      source: {
        revision: sourceRevision,
        tree: operatorSourceTree,
        files: Object.fromEntries(Object.entries(operatorSourceFiles).map(([relative, bytes]) => [
          relative, digest(bytes),
        ])),
      },
      operator: {
        reference: `registry.example/wine@sha256:${hex('1')}`,
        imageId: `sha256:${hex('2')}`,
        sizeBytes: 1024,
        platform: 'linux/amd64',
        userspace: { version: 'wine-9.0', digest: `sha256:${hex('3')}`, sourceUri: 'https://example.test/wine' },
        baseImage: `registry.example/base@sha256:${hex('4')}`,
        labels: { 'io.sharplabnext.operator.contract': 'wine-coreclr-v1' },
      },
    })
    const operatorBytes = serializeWineCoreClrOperatorReceipt(operator)
    const signatureBytes = Buffer.from(`${signWineCoreClrOperatorReceipt(operator, operatorKeys.privateKey)}\n`)
    const operatorPath = `profiles/runtime-operator-receipts/wine-coreclr-${sourceRevision}.json`
    const signaturePath = `${operatorPath}.sig`
    for (const [relativePath, bytes] of [[operatorPath, operatorBytes], [signaturePath, signatureBytes]]) {
      const filename = path.join(root, ...relativePath.split('/'))
      fs.mkdirSync(path.dirname(filename), { recursive: true })
      fs.writeFileSync(filename, bytes)
    }
    value.wineOperator = {
      receiptPath: operatorPath,
      receiptSha256: digest(operatorBytes),
      signaturePath,
      signatureSha256: digest(signatureBytes),
      keyId: operator.keyId,
      reference: operator.operator.reference,
      imageId: operator.operator.imageId,
      sizeBytes: operator.operator.sizeBytes,
      sourceRevision,
      sourceTree: operator.source.tree,
      lineageKind: value.family === 'coreclr-wine' ? 'direct' : 'framework-row',
      ...(value.family === 'coreclr-wine' ? {} : {
        intermediaryReference: value.componentIdentity.sourceUri.slice('docker://'.length),
        intermediaryImageId: `sha256:${hex('5')}`,
        intermediarySizeBytes: 2048,
      }),
    }
  }

  const relativePath = `profiles/runtime-promotion-receipts/${value.profileId}.json`
  const absolutePath = path.join(root, ...relativePath.split('/'))
  fs.mkdirSync(path.dirname(absolutePath), { recursive: true })
  const bytes = Buffer.from(`${JSON.stringify(value, null, 2)}\n`)
  fs.writeFileSync(absolutePath, bytes)
  const fixture = {
    root,
    profilePath,
    receiptPath: absolutePath,
    evidencePaths,
    performanceEvidencePath,
    performancePolicyPath: policyPath,
    reference: {
      path: relativePath,
      sha256: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`,
    },
  }
  if (withPromotionPlan) bindPlanPreflight(fixture)
  return fixture
}

function defaultExecutionUser(value) {
  return value.platform === 'wine' || value.platform === 'framework' ? '0:0' : '1654:1654'
}

function runtimeProfile(value, executionUser = defaultExecutionUser(value)) {
  const operations = {}
  for (const [name, helper] of Object.entries(value.operations)) {
    const check = value.checks.find(item => item.capability === (name === 'jit' ? 'jit-asm' : 'run'))
    operations[name] = runtimeProfileOperation(value, name, helper, check)
  }
  const wineCoreClr = value.family === 'coreclr-wine'
  const wineFramework = value.family === 'netfx-clr-wine'
  const mono = value.family === 'mono'
  const policy = {
    id: wineFramework ? 'runtime-job-wine-netfx' : 'runtime-job-default',
    memoryBytes: wineFramework ? 1073741824 : 268435456,
    nanoCpus: 1000000000,
    pidsLimit: 64,
    maximumDurationSeconds: wineFramework ? 30 : 10,
    maximumArtifactBytes: 67108864,
    maximumOutputBytes: 1048576,
    tmpfsBytes: 33554432,
  }
  return {
    schemaVersion: 1,
    id: value.profileId,
    image: 'candidate-image',
    family: value.family,
    runtimeVersion: value.resolvedVersion,
    capabilities: value.checks.map(check => check.capability),
    allowedSecurityPolicyIds: [policy.id],
    container: {
      isolationKind: wineCoreClr || wineFramework ? 'wine' : 'standard',
      environmentKind: wineCoreClr || wineFramework ? 'wine' : mono ? 'mono' : 'coreclr',
      executionUser,
      ...(wineCoreClr || wineFramework ? { winePrefixPath: '/opt/wine-dotnet' } : {}),
    },
    operations,
    layout: {
      runnerKind: wineCoreClr ? 'wine-coreclr' : wineFramework ? 'wine-netfx' : 'dotnet',
      dotNetHostPath: wineCoreClr
        ? '/opt/wine-dotnet/drive_c/dotnet/dotnet.exe'
        : mono
        ? '/usr/bin/mono'
        : wineFramework
        ? '/opt/sharplabnext/control-dotnet/dotnet'
        : '/opt/sharplabnext/target-dotnet/dotnet',
      wineHostPath: '/usr/lib/wine/wine64',
      runnerAssemblyPath: value.operations.run.assemblyPath,
      ...(value.operations.jit === undefined
        ? {}
        : { jitInspectorAssemblyPath: value.operations.jit.assemblyPath }),
    },
    securityPolicies: [policy],
  }
}

function runtimeProfileOperation(value, name, helper, check) {
  const entry = '{entryAssembly}'
  const tail = name === 'jit' ? ['{methodFilter}'] : ['--', '{arguments}']
  const verb = name === 'jit' ? 'jit' : 'run'
  const wineCoreClr = value.family === 'coreclr-wine'
  let executable = '/opt/sharplabnext/target-dotnet/dotnet'
  let pathStyle = wineCoreClr || value.family === 'netfx-clr-wine' ? 'wine-z' : 'unix'
  let argv
  switch (helper.implementation) {
    case 'sharplabnext-target-runtime-runner-v1': {
      const wine = value.family === 'netfx-clr-wine'
      executable = wine ? '/usr/lib/wine/wine64' : '/usr/bin/mono'
      pathStyle = wine ? 'wine-z' : 'unix'
      const helperToken = wine
        ? `Z:${helper.assemblyPath.replaceAll('/', '\\')}`
        : helper.assemblyPath
      argv = [helperToken, 'run', entry, ...tail]
      break
    }
    case 'sharplabnext-wine-runner-v1':
      executable = '/opt/sharplabnext/control-dotnet/dotnet'
      argv = [
        helper.assemblyPath,
        'bridge',
        value.family === 'mono' ? '/usr/bin/mono' : '/usr/lib/wine/wine64',
        entry,
        ...tail,
      ]
      break
    case 'sharplabnext-legacy-jit-inspector-v1': {
      const helperToken = wineCoreClr
        ? `Z:${helper.assemblyPath.replaceAll('/', '\\')}`
        : helper.assemblyPath
      if (wineCoreClr) {
        executable = '/usr/lib/wine/wine64'
        argv = [
          'Z:\\opt\\wine-dotnet\\drive_c\\dotnet\\dotnet.exe',
          helperToken,
          verb,
          entry,
          ...tail,
        ]
      } else {
        argv = [helperToken, verb, entry, ...tail]
      }
      break
    }
    case 'sharplabnext-checked-jit-bridge-v1':
      argv = [helper.assemblyPath, 'jit', entry, '{methodFilter}']
      break
    case 'sharplabnext-mono-jit-inspector-v1':
      executable = '/usr/share/dotnet/dotnet'
      pathStyle = 'unix'
      argv = [helper.assemblyPath, entry, '{methodFilter}']
      break
    default:
      argv = name === 'jit'
        ? [helper.assemblyPath, entry, '{methodFilter}']
        : [helper.assemblyPath, entry, '--', '{arguments}']
      break
  }
  return {
    implementationId: helper.implementation,
    pathStyle,
    command: { executable, argv },
    ...(name === 'jit'
      ? {
          sourceMappingKind: check?.sourceMappingKind ?? 'none',
          ...(helper.profilerPath === undefined ? {} : { profilerPath: helper.profilerPath }),
        }
      : {}),
  }
}

function capabilityEvidence(value, check, executionUser = defaultExecutionUser(value)) {
  const isJit = check.capability === 'jit-asm'
  const runProbeArguments = isJit
    ? []
    : check.capability === 'run'
    ? ['success-security']
    : check.capability === 'inspection'
    ? ['inspection']
    : check.capability === 'execution-flow'
    ? ['execution-flow']
    : (() => { throw new Error(`Unsupported Run capability '${check.capability}'.`) })()
  const operation = value.operations[isJit ? 'jit' : 'run'] ?? value.operations.run
  const profile = runtimeProfile(value, executionUser)
  const profileOperation = profile.operations[isJit ? 'jit' : 'run'] ?? profile.operations.run
  const isWineCoreClr = value.platform === 'wine'
  const isWineFramework = value.platform === 'framework'
  const isMono = value.platform === 'mono'
  const entryExtension = isWineFramework || isMono ? 'exe' : 'dll'
  const entryAssemblyPath = profileOperation.pathStyle === 'wine-z'
    ? `Z:\\workspace\\SharpLabNext.Preflight.${entryExtension}`
    : `/workspace/SharpLabNext.Preflight.${entryExtension}`
  const methodFilter = isJit ? 'SharpLabNext.Preflight:MultipleSequencePoints' : undefined
  const command = [profileOperation.command.executable]
  for (const token of profileOperation.command.argv) {
    if (token === '{entryAssembly}') command.push(entryAssemblyPath)
    else if (token === '{arguments}') command.push(...runProbeArguments)
    else if (token === '{methodFilter}') command.push(methodFilter)
    else command.push(token)
  }
  let controlHostPath
  let runtimeHostPath = profileOperation.command.executable
  let runtimeHostFormat = 'elf'
  if (profileOperation.implementationId === 'sharplabnext-wine-runner-v1') {
    controlHostPath = profileOperation.command.executable
    runtimeHostPath = profileOperation.command.argv[2]
  } else if (profileOperation.implementationId === 'sharplabnext-legacy-jit-inspector-v1' &&
             profileOperation.pathStyle === 'wine-z') {
    controlHostPath = profileOperation.command.executable
    runtimeHostPath = profile.layout.dotNetHostPath
    runtimeHostFormat = 'pe'
  } else if (profileOperation.implementationId === 'sharplabnext-mono-jit-inspector-v1') {
    controlHostPath = profileOperation.command.executable
    runtimeHostPath = '/usr/bin/mono'
  }

  const artifact = (role, artifactPath, shaCharacter, format, architecture) => ({
    role,
    path: artifactPath,
    sha256: `sha256:${hex(shaCharacter)}`,
    sizeBytes: 65536,
    format,
    architecture,
  })
  const artifacts = [
    artifact('helper', operation.assemblyPath, 'c', 'managed-pe', 'anycpu'),
    artifact(
      'runtime-host',
      runtimeHostPath,
      '1',
      runtimeHostFormat,
      'x64',
    ),
  ]
  artifacts[0].sha256 = operation.assemblySha256
  if (controlHostPath !== undefined && controlHostPath !== runtimeHostPath) {
    artifacts.push(artifact('control-host', controlHostPath, '2', 'elf', 'x64'))
  }
  if ((value.family === 'coreclr' || value.family === 'coreclr-wine') &&
      !value.resolvedVersion.startsWith('2.')) {
    artifacts.push(artifact(
      'support-assembly',
      '/opt/sharplabnext/SharpLab.Runtime.dll',
      '3',
      'managed-pe',
      'anycpu',
    ))
  }
  if (isJit) {
    artifacts.push(artifact(
      'jit-library',
      isMono
        ? '/usr/bin/mono-sgen'
        : isWineCoreClr
        ? `/opt/wine-dotnet/drive_c/dotnet/shared/Microsoft.NETCore.App/${value.resolvedVersion}/clrjit.dll`
        : `/opt/sharplabnext/target-dotnet/shared/Microsoft.NETCore.App/${value.resolvedVersion}/libclrjit.so`,
      '4',
      isWineCoreClr ? 'pe' : 'elf',
      'x64',
    ))
    if (check.sourceMappingKind === 'linux-profiler') {
      const profiler = artifact('profiler', operation.profilerPath, 'e', 'elf', 'x64')
      profiler.sha256 = operation.profilerSha256
      artifacts.push(profiler)
    }
  }

  const policy = profile.securityPolicies[0]
  const evidence = {
    schemaVersion: 1,
    profileId: value.profileId,
    capability: check.capability,
    image: {
      reference: value.image.reference,
      imageId: value.image.imageId,
    },
    sourceRevision: value.sourceRevision,
    completedAtUtc: '2026-07-22T00:00:00Z',
    result: 'passed',
    producer: {
      id: 'sharplabnext-runtime-preflight-v1',
      sourceRevision: value.sourceRevision,
      planSha256: value.planSha256,
    },
    artifacts,
    invocation: {
      implementation: operation.implementation,
      command,
      entryAssembly: {
        path: entryAssemblyPath,
        sha256: `sha256:${hex('5')}`,
      },
      ...(methodFilter === undefined ? {} : { methodFilter }),
      outcome: 'succeeded',
      exitCode: 0,
      runtimeFrameCount: 3,
      terminalFrameKind: 'Exit',
      terminalStatus: 'completed',
      stdoutBytes: 32,
      stderrBytes: 16,
    },
    sandbox: {
      supervisorPolicyId: 'runtime-linux-v1',
      securityPolicyId: policy.id,
      seccompSha256: `sha256:${hex('6')}`,
      containerId: hex('7'),
      networkMode: 'none',
      networkProbeBlocked: true,
      readOnlyRootFilesystem: true,
      readOnlyProbeBlocked: true,
      capDrop: ['ALL'],
      noNewPrivileges: true,
      user: profile.container.executionUser,
      nanoCpus: 1000000000,
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
  }
  if (check.capability === 'run') {
    evidence.run = {
      expectedStdoutMarker: 'stdout-marker',
      observedStdoutMarker: 'stdout-marker',
      expectedStderrMarker: 'stderr-marker',
      observedStderrMarker: 'stderr-marker',
      exceptionFrameValidated: true,
    }
  } else if (check.capability === 'jit-asm') {
    const mapped = check.sourceMappingKind !== 'none'
    const sourceRanges = mapped
      ? [sourceRange(0, 0, 8, 10), sourceRange(4, 8, 16, 11)]
      : []
    evidence.jit = {
      runtimeVersion: value.resolvedVersion,
      jitVersion: value.runtimeIdentity.jitVersion,
      ...(mapped
        ? {
            pdb: {
              path: isWineCoreClr
                ? 'Z:\\workspace\\SharpLabNext.Preflight.pdb'
                : '/workspace/SharpLabNext.Preflight.pdb',
              sha256: `sha256:${hex('8')}`,
              contentId: '9'.repeat(40),
              sequencePointCount: 2,
            },
          }
        : {}),
      methods: [{
        metadataToken: '0x06000001',
        displayName: 'SharpLabNext.Preflight.MultipleSequencePoints',
        nativeCodeBytes: 32,
        instructionCount: 12,
        sourceRanges,
      }],
      mapping: {
        kind: check.sourceMappingKind,
        source: check.mappingSource,
        rangeCount: sourceRanges.length,
        distinctSourceRangeCount: sourceRanges.length,
        allRangesMatchPdb: mapped,
      },
    }
  } else if (check.capability === 'inspection') {
    evidence.inspection = {
      recordCount: 2,
      kinds: ['Value', 'MemoryGraph'],
      valueProbePassed: true,
      memoryGraphProbePassed: true,
    }
  } else if (check.capability === 'execution-flow') {
    evidence.executionFlow = {
      recordCount: 3,
      sequencePointCount: 2,
      branchCount: 1,
      sourceRangeCount: 2,
      derivedArtifactSha256: `sha256:${hex('a')}`,
    }
  }
  return evidence
}

function lifecycleProbe(terminalStatus) {
  return {
    result: 'passed',
    terminalStatus,
    containerRemoved: true,
    processTreeRemoved: true,
  }
}

function sourceRange(ilOffset, nativeStartOffset, nativeEndOffset, startLine) {
  return {
    ilOffset,
    nativeStartOffset,
    nativeEndOffset,
    document: 'Program.cs',
    startLine,
    startColumn: 9,
    endLine: startLine,
    endColumn: 18,
  }
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

function updatePerformanceEvidence(fixture, update) {
  const evidence = JSON.parse(fs.readFileSync(fixture.performanceEvidencePath, 'utf8'))
  update(evidence)
  const evidenceBytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`)
  fs.writeFileSync(fixture.performanceEvidencePath, evidenceBytes)
  const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
  value.performance.evidenceSha256 = digest(evidenceBytes)
  rewriteReceipt(fixture, value)
}

function updateCapabilityEvidence(fixture, capability, update) {
  const evidencePath = fixture.evidencePaths[capability]
  const evidence = JSON.parse(fs.readFileSync(evidencePath, 'utf8'))
  update(evidence)
  const evidenceBytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`)
  fs.writeFileSync(evidencePath, evidenceBytes)
  const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
  const check = value.checks.find(item => item.capability === capability)
  check.evidenceSha256 = digest(evidenceBytes)
  rewriteReceipt(fixture, value)
}

function updatePerformancePolicy(fixture, update) {
  const policy = JSON.parse(fs.readFileSync(fixture.performancePolicyPath, 'utf8'))
  update(policy)
  const policyBytes = Buffer.from(`${JSON.stringify(policy, null, 2)}\n`)
  fs.writeFileSync(fixture.performancePolicyPath, policyBytes)
  const policyDigest = digest(policyBytes)
  const evidence = JSON.parse(fs.readFileSync(fixture.performanceEvidencePath, 'utf8'))
  evidence.policy.sha256 = policyDigest
  const evidenceBytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`)
  fs.writeFileSync(fixture.performanceEvidencePath, evidenceBytes)
  const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
  value.performance.policySha256 = policyDigest
  value.performance.evidenceSha256 = digest(evidenceBytes)
  rewriteReceipt(fixture, value)
}

function rewriteReceipt(fixture, value) {
  const bytes = Buffer.from(`${JSON.stringify(value, null, 2)}\n`)
  fs.writeFileSync(fixture.receiptPath, bytes)
  fixture.reference.sha256 = digest(bytes)
}

function bindPlanPreflight(fixture, { candidateCapabilities } = {}) {
  const receipt = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
  const candidate = JSON.parse(fs.readFileSync(fixture.profilePath))
  const preflight = structuredClone(candidate)
  preflight.image = receipt.image.reference
  preflight.runtimeImageId = receipt.image.imageId
  delete preflight.promotionReceipt
  if (candidateCapabilities !== undefined) {
    candidate.capabilities = candidateCapabilities
    fs.writeFileSync(fixture.profilePath, `${JSON.stringify(candidate, null, 2)}\n`)
  }
  const candidateBytes = fs.readFileSync(fixture.profilePath)

  const planRoot = path.join(fixture.root, 'profiles', 'runtime-promotion-plans')
  fs.mkdirSync(planRoot, { recursive: true })
  const preflightRelativePath =
    `profiles/runtime-promotion-plans/${receipt.profileId}.profile.json`
  const preflightPath = path.join(fixture.root, ...preflightRelativePath.split('/'))
  const preflightBytes = Buffer.from(`${JSON.stringify(preflight, null, 2)}\n`)
  fs.writeFileSync(preflightPath, preflightBytes)
  const plan = {
    schemaVersion: 1,
    candidateTarget: 'fixture-candidate',
    profileId: receipt.profileId,
    profileSha256: digest(candidateBytes),
    matrixTargetId: receipt.matrixTargetId,
    platform: receipt.platform,
    family: receipt.family,
    resolvedVersion: receipt.resolvedVersion,
    sourceRevision: receipt.sourceRevision,
    sourceTree: 'f'.repeat(40),
    image: receipt.image,
    componentIdentity: receipt.componentIdentity,
    ...(receipt.wineOperator === undefined ? {} : { wineOperator: receipt.wineOperator }),
    runtimeIdentity: receipt.runtimeIdentity,
    buildInputs: { FIXTURE: 'runtime-promotion-receipt-validation' },
    buildInputsSha256: digest(serializeRuntimePromotionPlan({
      FIXTURE: 'runtime-promotion-receipt-validation',
    })),
    producer: {
      id: 'sharplabnext-runtime-preflight-v1',
      sourceRevision: receipt.sourceRevision,
    },
    securityPolicyId: candidate.allowedSecurityPolicyIds[0],
    capabilities: receipt.checks.map(check => check.capability).sort(),
    sourceMappingKind: receipt.checks.find(check => check.capability === 'jit-asm')?.sourceMappingKind ??
      'not-applicable',
    operations: receipt.operations,
    preflightProfile: {
      path: preflightRelativePath,
      sha256: digest(preflightBytes),
    },
    performance: {
      policyId: receipt.performance.policyId,
      policyPath: receipt.performance.policyPath,
      policySha256: receipt.performance.policySha256,
      evidencePath: receipt.performance.evidencePath,
    },
  }
  const planPath = path.join(planRoot, `${receipt.profileId}.json`)
  const planBytes = serializeRuntimePromotionPlan(plan)
  fs.writeFileSync(planPath, planBytes)
  receipt.planSha256 = digest(planBytes)
  const signatureRelativePath = runtimePromotionPlanSignaturePath(receipt.profileId)
  const signatureBytes = Buffer.from(`${signRuntimePromotionPlan(planBytes, planKeys.privateKey)}\n`)
  fs.writeFileSync(path.join(fixture.root, ...signatureRelativePath.split('/')), signatureBytes)
  receipt.planSignature = { path: signatureRelativePath, sha256: digest(signatureBytes), keyId: planKeyId }

  for (const check of receipt.checks) {
    const evidencePath = fixture.evidencePaths[check.capability]
    const evidence = JSON.parse(fs.readFileSync(evidencePath, 'utf8'))
    evidence.producer.planSha256 = receipt.planSha256
    const evidenceBytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`)
    fs.writeFileSync(evidencePath, evidenceBytes)
    check.evidenceSha256 = digest(evidenceBytes)
  }
  const performance = JSON.parse(fs.readFileSync(fixture.performanceEvidencePath, 'utf8'))
  performance.planSha256 = receipt.planSha256
  const performanceBytes = Buffer.from(`${JSON.stringify(performance, null, 2)}\n`)
  fs.writeFileSync(fixture.performanceEvidencePath, performanceBytes)
  receipt.performance.evidenceSha256 = digest(performanceBytes)
  rewriteReceipt(fixture, receipt)
  fixture.planPath = planPath
  fixture.planSignaturePath = path.join(fixture.root, ...signatureRelativePath.split('/'))
  return { planPath, preflightPath }
}

function rewritePlan(fixture, plan) {
  const planBytes = serializeRuntimePromotionPlan(plan)
  fs.writeFileSync(fixture.planPath, planBytes)
  const signatureBytes = Buffer.from(`${signRuntimePromotionPlan(planBytes, planKeys.privateKey)}\n`)
  fs.writeFileSync(fixture.planSignaturePath, signatureBytes)
  const receipt = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
  receipt.planSha256 = digest(planBytes)
  receipt.planSignature.sha256 = digest(signatureBytes)
  rewriteReceipt(fixture, receipt)
}

function digest(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}

test('verified runtime capability is closed against an immutable promotion receipt', t => {
  const fixture = writeFixture()
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  assert.deepEqual(validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root), [])
})

test('shared JSON Schema subset rejects promotion-contract boundary violations', () => {
  const schema = {
    type: 'object',
    maxProperties: 2,
    required: ['kind'],
    properties: {
      kind: { enum: ['receipt', 'plan'] },
      source: {
        oneOf: [
          { type: 'string', minLength: 1 },
          { type: 'integer', minimum: 1 },
        ],
      },
      profilerPath: { type: 'string' },
      profilerSha256: { type: 'string' },
    },
    dependentRequired: {
      profilerPath: ['profilerSha256'],
      profilerSha256: ['profilerPath'],
    },
    additionalProperties: false,
  }
  assert.match(
    validateJsonSchemaInstance({ kind: 'unknown' }, schema).join('\n'),
    /allowed enum/,
  )
  assert.match(
    validateJsonSchemaInstance({ kind: 'receipt', source: 0 }, schema).join('\n'),
    /exactly one allowed schema/,
  )
  assert.match(
    validateJsonSchemaInstance({ kind: 'receipt', profilerPath: '/x' }, schema).join('\n'),
    /requires property profilerSha256/,
  )
  assert.match(
    validateJsonSchemaInstance({ kind: 'receipt', source: 'x', extra: true }, schema).join('\n'),
    /too many properties/,
  )
  assert.match(
    validateJsonSchemaInstance(1, { type: 'string' }).join('\n'),
    /expected type string/,
  )
  assert.match(
    validateJsonSchemaInstance(['one', 'two'], { type: 'array', maxItems: 1 }).join('\n'),
    /array has too many items/,
  )
  assert.match(
    validateJsonSchemaInstance('toolong', { type: 'string', maxLength: 3 }).join('\n'),
    /string is longer than maxLength/,
  )
})

test('receipt and signed plan reject unknown and missing root or nested contract fields', t => {
  const cases = [
    {
      name: 'receipt unknown root',
      mutate(fixture) {
        const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
        value.injected = true
        rewriteReceipt(fixture, value)
      },
      expected: /promotion receipt has unknown property 'injected'/,
    },
    {
      name: 'receipt missing nested image identity',
      mutate(fixture) {
        const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
        delete value.image.imageId
        rewriteReceipt(fixture, value)
      },
      expected: /promotion receipt\.image is missing required property 'imageId'/,
    },
    {
      name: 'plan unknown nested helper field',
      mutate(fixture) {
        const plan = JSON.parse(fs.readFileSync(fixture.planPath, 'utf8'))
        plan.operations.run.injected = true
        rewritePlan(fixture, plan)
      },
      expected: /promotion plan\.operations\.run has unknown property 'injected'/,
    },
    {
      name: 'plan missing root source closure',
      mutate(fixture) {
        const plan = JSON.parse(fs.readFileSync(fixture.planPath, 'utf8'))
        delete plan.buildInputs
        rewritePlan(fixture, plan)
      },
      expected: /promotion plan is missing required property 'buildInputs'/,
    },
  ]
  for (const testCase of cases) {
    const fixture = writeFixture()
    t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
    testCase.mutate(fixture)
    assert.match(
      validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root).join('\n'),
      testCase.expected,
      testCase.name,
    )
  }
})

test('receipt and signed plan fail closed on their shared JSON Schema limits and dependencies', t => {
  const cases = [
    {
      name: 'receipt enum',
      mutate(fixture) {
        const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
        value.platform = 'unsupported'
        rewriteReceipt(fixture, value)
      },
      expected: /promotion receipt#\/platform: value is not in the allowed enum/,
    },
    {
      name: 'receipt dependent helper fields',
      mutate(fixture) {
        const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
        value.operations.run.profilerPath = '/opt/sharplabnext/SharpLabNext.JitProfiler.so'
        rewriteReceipt(fixture, value)
      },
      expected: /promotion receipt#\/operations\/run: property profilerPath requires property profilerSha256/,
    },
    {
      name: 'plan maximum build inputs',
      mutate(fixture) {
        const plan = JSON.parse(fs.readFileSync(fixture.planPath, 'utf8'))
        plan.buildInputs = Object.fromEntries(Array.from({ length: 65 }, (_, index) => [
          `INPUT_${index}`,
          'fixture',
        ]))
        rewritePlan(fixture, plan)
      },
      expected: /promotion plan#\/buildInputs: object has too many properties/,
    },
  ]
  for (const testCase of cases) {
    const fixture = writeFixture()
    t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
    testCase.mutate(fixture)
    assert.match(
      validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root).join('\n'),
      testCase.expected,
      testCase.name,
    )
  }
})

test('Wine operator binding rejects missing, tampered, noncanonical, and escaped retained material', t => {
  for (const [name, mutate] of [
    ['missing', fixture => { delete fixture.value.wineOperator }],
    ['escaped', fixture => { fixture.value.wineOperator.receiptPath = '../outside.json' }],
    ['noncanonical signature', fixture => {
      fs.appendFileSync(path.join(fixture.root, ...fixture.value.wineOperator.signaturePath.split('/')), ' ')
    }],
    ['tampered receipt', fixture => {
      fs.appendFileSync(path.join(fixture.root, ...fixture.value.wineOperator.receiptPath.split('/')), ' ')
    }],
  ]) {
    const fixture = writeFixture()
    t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
    fixture.value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
    mutate(fixture)
    if (name === 'missing' || name === 'escaped') rewriteReceipt(fixture, fixture.value)
    assert.notDeepEqual(validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root), [], name)
  }
})

test('Framework Wine supports row and shared-parent clean operator lineage without replacing componentIdentity', t => {
  const fixture = writeFixture()
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'))
  assert.equal(value.wineOperator.lineageKind, 'framework-row')
  const originalComponent = structuredClone(value.componentIdentity)
  value.wineOperator.lineageKind = 'framework-parent'
  value.wineOperator.intermediaryReference = `registry.example/framework-parent@sha256:${hex('6')}`
  value.wineOperator.intermediaryImageId = `sha256:${hex('7')}`
  value.wineOperator.intermediarySizeBytes = 4096
  rewriteReceipt(fixture, value)
  assert.deepEqual(value.componentIdentity, originalComponent)
  bindPlanPreflight(fixture)
  assert.deepEqual(validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root), [])
})

test('capability evidence binds the exact root or non-root profile execution identity', t => {
  for (const executionUser of ['0:0', '1654:1654']) {
    const fixture = writeFixture(receipt(), { executionUser })
    t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))

    assert.deepEqual(
      validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root),
      [],
      `Framework evidence must accept the profile-bound ${executionUser} identity`,
    )
  }

  const mismatch = writeFixture(receipt(), { executionUser: '1654:1654' })
  t.after(() => fs.rmSync(mismatch.root, { recursive: true, force: true }))
  updateCapabilityEvidence(mismatch, 'run', evidence => {
    evidence.sandbox.user = '0:0'
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(mismatch.reference), mismatch.root).join('\n'),
    /sandbox\.user must equal "1654:1654"; observed "0:0"/,
  )
})

test('owned promotion JSON documents reject explicit null values recursively', t => {
  const receiptNull = writeFixture()
  t.after(() => fs.rmSync(receiptNull.root, { recursive: true, force: true }))
  const receiptValue = JSON.parse(fs.readFileSync(receiptNull.receiptPath, 'utf8'))
  receiptValue.componentIdentity.sourceUri = null
  rewriteReceipt(receiptNull, receiptValue)
  assert.match(
    validateRuntimePromotionReceipts(matrix(receiptNull.reference), receiptNull.root).join('\n'),
    /promotion receipt cannot contain explicit JSON null values/,
  )

  const profileNull = writeFixture()
  t.after(() => fs.rmSync(profileNull.root, { recursive: true, force: true }))
  const profile = JSON.parse(fs.readFileSync(profileNull.profilePath, 'utf8'))
  profile.layout.runnerAssemblyPath = null
  fs.writeFileSync(profileNull.profilePath, `${JSON.stringify(profile, null, 2)}\n`)
  assert.match(
    validateRuntimePromotionReceipts(matrix(profileNull.reference), profileNull.root).join('\n'),
    /Runtime Profile .* cannot contain explicit JSON null values/,
  )

  const policyNull = writeFixture()
  t.after(() => fs.rmSync(policyNull.root, { recursive: true, force: true }))
  updatePerformancePolicy(policyNull, policy => {
    policy.scenarios.run.cold.maximumP95LatencyMilliseconds = null
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(policyNull.reference), policyNull.root).join('\n'),
    /performance policy cannot contain explicit JSON null values/,
  )

  const performanceNull = writeFixture()
  t.after(() => fs.rmSync(performanceNull.root, { recursive: true, force: true }))
  updatePerformanceEvidence(performanceNull, evidence => {
    evidence.scenarios.run.cold[0].peakMemoryBytes = null
  })
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(performanceNull.reference),
      performanceNull.root,
    ).join('\n'),
    /performance evidence cannot contain explicit JSON null values/,
  )

  const capabilityNull = writeFixture()
  t.after(() => fs.rmSync(capabilityNull.root, { recursive: true, force: true }))
  updateCapabilityEvidence(capabilityNull, 'run', evidence => {
    evidence.lifecycle.timeout.terminalStatus = null
  })
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(capabilityNull.reference),
      capabilityNull.root,
    ).join('\n'),
    /run evidence cannot contain explicit JSON null values/,
  )
})

test('capability evidence schema and validators reject Unix and Wine PDB dot segments', t => {
  const schema = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'schemas', 'runtime-capability-evidence.schema.json'),
    'utf8',
  ))
  assert.deepEqual(schema.$defs.producer.required, ['id', 'sourceRevision', 'planSha256'])
  assert.equal(schema.$defs.producer.properties.planSha256.$ref, '#/$defs/sha256')
  assert.deepEqual(
    schema.$defs.probeArtifact.required,
    [
      'contract',
      'sourceArtifactSha256',
      'artifactSha256',
      'entryAssemblySha256',
      'planSha256',
      'preflightProfileSha256',
    ],
  )
  assert.equal(schema.$defs.probeArtifact.properties.planSha256.$ref, '#/$defs/sha256')
  assert.equal(
    schema.$defs.probeArtifact.properties.preflightProfileSha256.$ref,
    '#/$defs/sha256',
  )
  const absolutePathPattern = new RegExp(schema.$defs.absolutePath.pattern)
  assert.equal(absolutePathPattern.test('/workspace/SharpLabNext.Preflight.pdb'), true)
  assert.equal(absolutePathPattern.test('Z:\\workspace\\SharpLabNext.Preflight.pdb'), true)
  assert.equal(absolutePathPattern.test('/workspace/../SharpLabNext.Preflight.pdb'), false)
  assert.equal(absolutePathPattern.test('Z:\\workspace\\..\\SharpLabNext.Preflight.pdb'), false)

  for (const pdbPath of [
    '/workspace/../SharpLabNext.Preflight.pdb',
    'Z:\\workspace\\..\\SharpLabNext.Preflight.pdb',
  ]) {
    const fixture = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
    t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
    updateCapabilityEvidence(fixture, 'jit-asm', evidence => {
      evidence.jit.pdb.path = pdbPath
    })
    assert.match(
      validateRuntimePromotionReceipts(
        coreClrMatrix('linuxCapability', fixture.reference),
        fixture.root,
      ).join('\n'),
      /PDB identity is invalid/,
    )
  }
})

test('capability evidence binds every executable host without substitution', t => {
  const cases = [
    {
      name: 'Linux target dotnet',
      value: () => coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'),
      matrix: reference => coreClrMatrix('linuxCapability', reference),
      role: 'runtime-host',
    },
    {
      name: 'TargetRuntimeRunner wine64',
      value: () => receipt(),
      matrix: reference => matrix(reference),
      role: 'runtime-host',
    },
    {
      name: 'TargetRuntimeRunner mono',
      value: monoReceipt,
      matrix: monoMatrix,
      role: 'runtime-host',
    },
    {
      name: 'Wine target dotnet.exe',
      value: wineCoreClrMethodReceipt,
      matrix: reference => coreClrMatrix('wineCapability', reference),
      role: 'runtime-host',
    },
  ]

  for (const testCase of cases) {
    const missing = writeFixture(testCase.value())
    t.after(() => fs.rmSync(missing.root, { recursive: true, force: true }))
    updateCapabilityEvidence(missing, 'run', evidence => {
      const artifact = evidence.artifacts.find(artifact => artifact.role === testCase.role)
      assert.notEqual(artifact, undefined, `${testCase.name} fixture must contain ${testCase.role}`)
      artifact.path = `${artifact.path}.substituted`
    })
    assert.match(
      validateRuntimePromotionReceipts(
        testCase.matrix(missing.reference),
        missing.root,
      ).join('\n'),
      new RegExp(`${testCase.role} artifact does not match`),
      `${testCase.name} omission must fail`,
    )

    const substituted = writeFixture(testCase.value())
    t.after(() => fs.rmSync(substituted.root, { recursive: true, force: true }))
    updateCapabilityEvidence(substituted, 'run', evidence => {
      const artifact = evidence.artifacts.find(item => item.role === testCase.role)
      artifact.path = `${artifact.path}.substituted`
    })
    assert.match(
      validateRuntimePromotionReceipts(
        testCase.matrix(substituted.reference),
        substituted.root,
      ).join('\n'),
      new RegExp(`${testCase.role} artifact does not match`),
      `${testCase.name} substitution must fail`,
    )
  }
})

test('repeated image paths retain role, format, architecture, hash, and size identity', t => {
  for (const [property, value] of [
    ['role', 'control-host'],
    ['format', 'pe'],
    ['architecture', 'anycpu'],
    ['sha256', `sha256:${hex('f')}`],
    ['sizeBytes', 65537],
  ]) {
    const fixture = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
    t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
    updateCapabilityEvidence(fixture, 'jit-asm', evidence => {
      const runtimeHost = evidence.artifacts.find(artifact => artifact.role === 'runtime-host')
      runtimeHost[property] = value
    })
    assert.match(
      validateRuntimePromotionReceipts(
        coreClrMatrix('linuxCapability', fixture.reference),
        fixture.root,
      ).join('\n'),
      /conflicts with another capability's path, byte, role, format, or architecture identity/,
      `${property} drift must fail`,
    )
  }
})

test('capability commands and security resources exactly match the Runtime Profile', t => {
  const command = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(command.root, { recursive: true, force: true }))
  updateCapabilityEvidence(command, 'run', evidence => {
    evidence.invocation.command[0] = '/opt/sharplabnext/substituted-dotnet'
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', command.reference),
      command.root,
    ).join('\n'),
    /invocation command does not match the selected Runtime Profile operation/,
  )

  const policy = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(policy.root, { recursive: true, force: true }))
  updateCapabilityEvidence(policy, 'run', evidence => {
    evidence.sandbox.deadlineMilliseconds += 1
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', policy.reference),
      policy.root,
    ).join('\n'),
    /sandbox\.deadlineMilliseconds does not match the selected Runtime Profile policy/,
  )
})

test('Run capability commands require the exact expanded probe arguments', t => {
  const valid = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(valid.root, { recursive: true, force: true }))
  assert.deepEqual(
    JSON.parse(fs.readFileSync(valid.evidencePaths.run)).invocation.command.slice(-2),
    ['--', 'success-security'],
  )
  assert.deepEqual(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', valid.reference),
      valid.root,
    ),
    [],
  )

  const unexpanded = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(unexpanded.root, { recursive: true, force: true }))
  updateCapabilityEvidence(unexpanded, 'run', evidence => {
    evidence.invocation.command.pop()
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', unexpanded.reference),
      unexpanded.root,
    ).join('\n'),
    /invocation command does not match the selected Runtime Profile operation/,
  )

  const substituted = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(substituted.root, { recursive: true, force: true }))
  updateCapabilityEvidence(substituted, 'run', evidence => {
    evidence.invocation.command[evidence.invocation.command.length - 1] = 'unexpected'
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', substituted.reference),
      substituted.root,
    ).join('\n'),
    /invocation command does not match the selected Runtime Profile operation/,
  )
})

test('plan-bound preflight profiles close evidence before active materialization', t => {
  const planless = writeFixture(
    coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'),
    { withPromotionPlan: false },
  )
  t.after(() => fs.rmSync(planless.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', planless.reference),
      planless.root,
    ).join('\n'),
    /plan and preflight Runtime Profile are required/,
  )

  const instrumentedValue = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  instrumentedValue.checks.push(
    {
      ...instrumentedValue.checks[0],
      capability: 'inspection',
    },
    {
      ...instrumentedValue.checks[0],
      capability: 'execution-flow',
    },
  )
  const instrumented = writeFixture(instrumentedValue)
  t.after(() => fs.rmSync(instrumented.root, { recursive: true, force: true }))
  bindPlanPreflight(instrumented, { candidateCapabilities: ['run', 'jit-asm'] })
  const instrumentedMatrix = coreClrMatrix('linuxCapability', instrumented.reference)
  instrumentedMatrix.coreClr[0].linuxCapability.capabilities =
    ['run', 'jit-asm', 'inspection', 'execution-flow']
  assert.deepEqual(
    validateRuntimePromotionReceipts(instrumentedMatrix, instrumented.root),
    [],
  )

  const valid = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(valid.root, { recursive: true, force: true }))
  bindPlanPreflight(valid, { candidateCapabilities: [] })
  assert.deepEqual(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', valid.reference),
      valid.root,
    ),
    [],
  )

  const planDrift = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(planDrift.root, { recursive: true, force: true }))
  const driftedPlan = bindPlanPreflight(planDrift, { candidateCapabilities: [] })
  fs.appendFileSync(driftedPlan.planPath, ' ')
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', planDrift.reference),
      planDrift.root,
    ).join('\n'),
    /plan digest mismatch/,
  )

  const preflightDrift = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(preflightDrift.root, { recursive: true, force: true }))
  const driftedPreflight = bindPlanPreflight(preflightDrift, { candidateCapabilities: [] })
  fs.appendFileSync(driftedPreflight.preflightPath, ' ')
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', preflightDrift.reference),
      preflightDrift.root,
    ).join('\n'),
    /plan preflightProfile\.sha256/,
  )

  const candidateDrift = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(candidateDrift.root, { recursive: true, force: true }))
  bindPlanPreflight(candidateDrift, { candidateCapabilities: [] })
  fs.appendFileSync(candidateDrift.profilePath, ' ')
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', candidateDrift.reference),
      candidateDrift.root,
    ).join('\n'),
    /plan profileSha256/,
  )

  const active = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(active.root, { recursive: true, force: true }))
  const activeBinding = bindPlanPreflight(active, { candidateCapabilities: [] })
  const activeProfile = JSON.parse(fs.readFileSync(activeBinding.preflightPath, 'utf8'))
  activeProfile.promotionReceipt = active.reference
  fs.writeFileSync(
    path.join(active.root, 'profiles', 'runtimes', `${activeProfile.id}.json`),
    `${JSON.stringify(activeProfile, null, 2)}\n`,
  )
  assert.deepEqual(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', active.reference),
      active.root,
    ),
    [],
  )
  activeProfile.operations.run.command.argv.push('substituted')
  fs.writeFileSync(
    path.join(active.root, 'profiles', 'runtimes', `${activeProfile.id}.json`),
    `${JSON.stringify(activeProfile, null, 2)}\n`,
  )
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', active.reference),
      active.root,
    ).join('\n'),
    /invocation command does not match the selected Runtime Profile operation/,
  )
})

test('performance evidence is retained, content-addressed, and image-bound', t => {
  const missing = writeFixture()
  t.after(() => fs.rmSync(missing.root, { recursive: true, force: true }))
  fs.rmSync(missing.performanceEvidencePath)
  assert.match(
    validateRuntimePromotionReceipts(matrix(missing.reference), missing.root).join('\n'),
    /performance evidence cannot be read/,
  )

  const changed = writeFixture()
  t.after(() => fs.rmSync(changed.root, { recursive: true, force: true }))
  fs.appendFileSync(changed.performanceEvidencePath, '{"changed":true}\n')
  assert.match(
    validateRuntimePromotionReceipts(matrix(changed.reference), changed.root).join('\n'),
    /performance evidence digest mismatch/,
  )

  const identity = writeFixture()
  t.after(() => fs.rmSync(identity.root, { recursive: true, force: true }))
  updatePerformanceEvidence(identity, evidence => {
    evidence.image.sizeBytes += 1
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(identity.reference), identity.root).join('\n'),
    /evidence image\.sizeBytes mismatch/,
  )
})

test('performance evidence binds the trusted measurement helper identity', t => {
  const missing = writeFixture()
  t.after(() => fs.rmSync(missing.root, { recursive: true, force: true }))
  updatePerformanceEvidence(missing, evidence => {
    delete evidence.measurementHelper
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(missing.reference), missing.root).join('\n'),
    /measurementHelper/,
  )

  const implementation = writeFixture()
  t.after(() => fs.rmSync(implementation.root, { recursive: true, force: true }))
  updatePerformanceEvidence(implementation, evidence => {
    evidence.measurementHelper.implementation = 'substituted-helper'
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(implementation.reference), implementation.root).join('\n'),
    /implementation must equal "sharplabnext-runtime-cgroup-sidecar-v1"/,
  )

  const entrypoint = writeFixture()
  t.after(() => fs.rmSync(entrypoint.root, { recursive: true, force: true }))
  updatePerformanceEvidence(entrypoint, evidence => {
    evidence.measurementHelper.entrypoint = '/tmp/substituted'
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(entrypoint.reference), entrypoint.root).join('\n'),
    /entrypoint must equal "\/usr\/local\/bin\/sharplabnext-runtime-measurement"/,
  )

  const source = writeFixture()
  t.after(() => fs.rmSync(source.root, { recursive: true, force: true }))
  updatePerformanceEvidence(source, evidence => {
    evidence.measurementHelper.sourceRevision = '9'.repeat(40)
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(source.reference), source.root).join('\n'),
    /sourceRevision must equal the evidence sourceRevision/,
  )

  const repository = writeFixture()
  t.after(() => fs.rmSync(repository.root, { recursive: true, force: true }))
  updatePerformanceEvidence(repository, evidence => {
    evidence.measurementHelper.image.reference =
      `registry.example/not-the-supervisor@sha256:${'7'.repeat(64)}`
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(repository.reference), repository.root).join('\n'),
    /immutable runtime-supervisor repository reference/,
  )

  const candidate = writeFixture()
  t.after(() => fs.rmSync(candidate.root, { recursive: true, force: true }))
  updatePerformanceEvidence(candidate, evidence => {
    evidence.measurementHelper.image.imageId = evidence.image.imageId
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(candidate.reference), candidate.root).join('\n'),
    /must be distinct from the candidate runtime image/,
  )
})

test('performance evidence enforces sample counts, positive metrics, P95, and memory budgets', t => {
  const sampleCount = writeFixture()
  t.after(() => fs.rmSync(sampleCount.root, { recursive: true, force: true }))
  updatePerformanceEvidence(sampleCount, evidence => {
    evidence.scenarios.run.cold.pop()
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(sampleCount.reference), sampleCount.root).join('\n'),
    /run\.cold must contain exactly 3 samples/,
  )

  const positive = writeFixture()
  t.after(() => fs.rmSync(positive.root, { recursive: true, force: true }))
  updatePerformanceEvidence(positive, evidence => {
    evidence.scenarios.run.warm[0].latencyMilliseconds = 0
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(positive.reference), positive.root).join('\n'),
    /latencyMilliseconds must be positive and finite/,
  )

  const p95 = writeFixture()
  t.after(() => fs.rmSync(p95.root, { recursive: true, force: true }))
  updatePerformanceEvidence(p95, evidence => {
    for (const sample of evidence.scenarios.run.cold) sample.latencyMilliseconds = 40000
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(p95.reference), p95.root).join('\n'),
    /run\.cold P95 latency 40000 exceeds policy 30000/,
  )

  const memory = writeFixture()
  t.after(() => fs.rmSync(memory.root, { recursive: true, force: true }))
  updatePerformanceEvidence(memory, evidence => {
    evidence.scenarios.run.warm[0].peakMemoryBytes = 268435457
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(memory.reference), memory.root).join('\n'),
    /peak memory exceeds the measured container limit/,
  )

  const legacyRunner = writeFixture()
  t.after(() => fs.rmSync(legacyRunner.root, { recursive: true, force: true }))
  updatePerformanceEvidence(legacyRunner, evidence => {
    evidence.environment.runnerId = 'runtime-preflight-linux-x64-v1'
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(legacyRunner.reference), legacyRunner.root).join('\n'),
    /runnerId must equal "runtime-preflight-linux-x64-v2"/,
  )

  const missingCompletionPeak = writeFixture()
  t.after(() => fs.rmSync(missingCompletionPeak.root, { recursive: true, force: true }))
  updatePerformanceEvidence(missingCompletionPeak, evidence => {
    delete evidence.scenarios.run.cold[0].completionPeakMemoryBytes
  })
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(missingCompletionPeak.reference),
      missingCompletionPeak.root,
    ).join('\n'),
    /completionPeakMemoryBytes/,
  )

  const completionPeak = writeFixture()
  t.after(() => fs.rmSync(completionPeak.root, { recursive: true, force: true }))
  updatePerformanceEvidence(completionPeak, evidence => {
    const sample = evidence.scenarios.run.cold[0]
    sample.peakMemoryBytes = 1024
    sample.completionPeakMemoryBytes = 1025
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(completionPeak.reference), completionPeak.root).join('\n'),
    /peakMemoryBytes cannot be less than completionPeakMemoryBytes/,
  )

  const nonPositiveCompletion = writeFixture()
  t.after(() => fs.rmSync(nonPositiveCompletion.root, { recursive: true, force: true }))
  updatePerformanceEvidence(nonPositiveCompletion, evidence => {
    evidence.scenarios.run.cold[0].completionPeakMemoryBytes = 0
  })
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(nonPositiveCompletion.reference),
      nonPositiveCompletion.root,
    ).join('\n'),
    /completionPeakMemoryBytes must be a positive integer/,
  )

  const missingPostCompletion = writeFixture()
  t.after(() => fs.rmSync(missingPostCompletion.root, { recursive: true, force: true }))
  updatePerformanceEvidence(missingPostCompletion, evidence => {
    delete evidence.scenarios.run.cold[0].postCompletionResourceSampleCount
  })
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(missingPostCompletion.reference),
      missingPostCompletion.root,
    ).join('\n'),
    /postCompletionResourceSampleCount/,
  )

  const nonPositivePostCompletion = writeFixture()
  t.after(() => fs.rmSync(nonPositivePostCompletion.root, { recursive: true, force: true }))
  updatePerformanceEvidence(nonPositivePostCompletion, evidence => {
    evidence.scenarios.run.cold[0].postCompletionResourceSampleCount = 0
  })
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(nonPositivePostCompletion.reference),
      nonPositivePostCompletion.root,
    ).join('\n'),
    /postCompletionResourceSampleCount must be a positive bounded integer/,
  )

  const postCompletionCount = writeFixture()
  t.after(() => fs.rmSync(postCompletionCount.root, { recursive: true, force: true }))
  updatePerformanceEvidence(postCompletionCount, evidence => {
    const sample = evidence.scenarios.run.cold[0]
    sample.resourceSampleCount = 1
    sample.postCompletionResourceSampleCount = 2
  })
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(postCompletionCount.reference),
      postCompletionCount.root,
    ).join('\n'),
    /resourceSampleCount cannot be less than postCompletionResourceSampleCount/,
  )
})

test('JIT and source mapping declarations require their own performance scenarios', t => {
  const value = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  const missingJit = writeFixture(value)
  t.after(() => fs.rmSync(missingJit.root, { recursive: true, force: true }))
  updatePerformanceEvidence(missingJit, evidence => {
    delete evidence.scenarios.jit
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', missingJit.reference),
      missingJit.root,
    ).join('\n'),
    /evidence scenarios must contain exactly \[jit, mapping, run\]/,
  )

  const missingMapping = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(missingMapping.root, { recursive: true, force: true }))
  updatePerformanceEvidence(missingMapping, evidence => {
    delete evidence.scenarios.mapping
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', missingMapping.reference),
      missingMapping.root,
    ).join('\n'),
    /evidence scenarios must contain exactly \[jit, mapping, run\]/,
  )
})

test('content-addressed policy cannot weaken absolute performance ceilings', t => {
  const fixture = writeFixture()
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  updatePerformancePolicy(fixture, policy => {
    policy.scenarios.run.cold.maximumP95LatencyMilliseconds = 60001
  })
  assert.match(
    validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root).join('\n'),
    /maximumP95LatencyMilliseconds must be greater than .* and at most 60000/,
  )
})

test('promotion receipt hash and exact matrix identity cannot drift', t => {
  const value = receipt()
  const fixture = writeFixture(value)
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))

  assert.match(
    validateRuntimePromotionReceipts(matrix({
      ...fixture.reference,
      sha256: `sha256:${hex('0')}`,
    }), fixture.root).join('\n'),
    /digest mismatch/,
  )

  value.resolvedVersion = '4.7.2'
  const identityFixture = writeFixture(value)
  t.after(() => fs.rmSync(identityFixture.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(identityFixture.reference), identityFixture.root).join('\n'),
    /resolvedVersion must equal "4\.8"/,
  )
})

test('receipt v2 closes component source and operation helper identities', t => {
  const legacy = receipt()
  legacy.schemaVersion = 1
  const legacyFixture = writeFixture(legacy)
  t.after(() => fs.rmSync(legacyFixture.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(legacyFixture.reference), legacyFixture.root).join('\n'),
    /schemaVersion must equal 2/,
  )

  const componentMismatch = receipt()
  componentMismatch.componentIdentity.sourceDigest = `sha256:${hex('8')}`
  const componentFixture = writeFixture(componentMismatch)
  t.after(() => fs.rmSync(componentFixture.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(componentFixture.reference), componentFixture.root).join('\n'),
    /sourceDigest must equal the digest in sourceUri/,
  )

  const helperMismatch = receipt()
  helperMismatch.operations.run.assemblySha256 = `sha256:${'A'.repeat(64)}`
  const helperFixture = writeFixture(helperMismatch)
  t.after(() => fs.rmSync(helperFixture.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(helperFixture.reference), helperFixture.root).join('\n'),
    /operations\.run\.assemblySha256 must be sha256/,
  )

  const undeclaredJit = receipt()
  undeclaredJit.operations.jit = {
    implementation: 'sharplabnext-legacy-jit-inspector-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    assemblySha256: `sha256:${hex('7')}`,
  }
  const jitFixture = writeFixture(undeclaredJit)
  t.after(() => fs.rmSync(jitFixture.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(jitFixture.reference), jitFixture.root).join('\n'),
    /operations must contain exactly \[run\]/,
  )
})

test('promotion receipt must be a bounded regular file inside its evidence directory', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-runtime-receipt-size-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const relativePath = 'profiles/runtime-promotion-receipts/wine-netfx48-linux-x64.json'
  const absolutePath = path.join(root, ...relativePath.split('/'))
  fs.mkdirSync(path.dirname(absolutePath), { recursive: true })
  const bytes = Buffer.alloc(1024 * 1024 + 1, 0x20)
  fs.writeFileSync(absolutePath, bytes)
  const reference = {
    path: relativePath,
    sha256: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`,
  }
  assert.match(
    validateRuntimePromotionReceipts(matrix(reference), root).join('\n'),
    /exceeds the 1 MiB size limit/,
  )
})

test('capability evidence must exist and match the retained bytes', t => {
  const missing = writeFixture()
  t.after(() => fs.rmSync(missing.root, { recursive: true, force: true }))
  fs.rmSync(missing.evidencePaths.run)
  assert.match(
    validateRuntimePromotionReceipts(matrix(missing.reference), missing.root).join('\n'),
    /cannot read run evidence/,
  )

  const changed = writeFixture()
  t.after(() => fs.rmSync(changed.root, { recursive: true, force: true }))
  fs.appendFileSync(changed.evidencePaths.run, '{"changed":true}\n')
  assert.match(
    validateRuntimePromotionReceipts(matrix(changed.reference), changed.root).join('\n'),
    /run evidence digest mismatch/,
  )
})

test('capability evidence cannot be replaced by a minimal passing JSON object', t => {
  const fixture = writeFixture()
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  updateCapabilityEvidence(fixture, 'run', evidence => {
    for (const key of Object.keys(evidence)) delete evidence[key]
    Object.assign(evidence, {
      schemaVersion: 1,
      profileId: 'wine-netfx48-linux-x64',
      capability: 'run',
      result: 'passed',
    })
  })

  const failures = validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root).join('\n')
  assert.match(failures, /run evidence image is missing/)
  assert.match(failures, /artifacts must contain between 2 and 8 entries/)
  assert.match(failures, /lifecycle probes are missing/)
})

test('JIT evidence binds real image files and multiple PDB source ranges', t => {
  const missingJit = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(missingJit.root, { recursive: true, force: true }))
  updateCapabilityEvidence(missingJit, 'jit-asm', evidence => {
    evidence.artifacts = evidence.artifacts.filter(artifact => artifact.role !== 'jit-library')
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', missingJit.reference),
      missingJit.root,
    ).join('\n'),
    /has no jit-library artifact/,
  )

  const weakMapping = writeFixture(coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr'))
  t.after(() => fs.rmSync(weakMapping.root, { recursive: true, force: true }))
  updateCapabilityEvidence(weakMapping, 'jit-asm', evidence => {
    evidence.jit.methods[0].sourceRanges.splice(1)
    evidence.jit.mapping.rangeCount = 1
    evidence.jit.mapping.distinctSourceRangeCount = 1
  })
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', weakMapping.reference),
      weakMapping.root,
    ).join('\n'),
    /lacks multiple PDB-matched source ranges/,
  )
})

test('capability evidence requires Supervisor cleanup for every failure path', t => {
  const fixture = writeFixture()
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  updateCapabilityEvidence(fixture, 'run', evidence => {
    evidence.lifecycle.cancellation.processTreeRemoved = false
  })

  assert.match(
    validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root).join('\n'),
    /lifecycle\.cancellation did not pass with complete cleanup/,
  )
})

test('capability evidence path is canonical and cannot escape through a link', t => {
  const escapedValue = receipt()
  escapedValue.checks[0].evidencePath =
    'profiles/runtime-promotion-evidence/wine-netfx48-linux-x64/../run.json'
  const escaped = writeFixture(escapedValue)
  t.after(() => fs.rmSync(escaped.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(escaped.reference), escaped.root).join('\n'),
    /run evidencePath must equal/,
  )

  const linked = writeFixture()
  t.after(() => fs.rmSync(linked.root, { recursive: true, force: true }))
  const profileDirectory = path.dirname(linked.evidencePaths.run)
  const outsideDirectory = path.join(linked.root, 'linked-evidence-target')
  fs.renameSync(profileDirectory, outsideDirectory)
  fs.symlinkSync(
    outsideDirectory,
    profileDirectory,
    process.platform === 'win32' ? 'junction' : 'dir',
  )
  assert.match(
    validateRuntimePromotionReceipts(matrix(linked.reference), linked.root).join('\n'),
    /evidence must be a regular non-link file below regular non-link evidence directories/,
  )
})

test('capability evidence is size bounded', t => {
  const fixture = writeFixture()
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  fs.writeFileSync(fixture.evidencePaths.run, Buffer.alloc(1024 * 1024 + 1, 0x20))
  assert.match(
    validateRuntimePromotionReceipts(matrix(fixture.reference), fixture.root).join('\n'),
    /run evidence exceeds the 1 MiB size limit/,
  )
})

test('receipt must cover each declared capability once with passing sandbox evidence', t => {
  const value = receipt()
  value.checks.push({ ...value.checks[0] })
  const duplicate = writeFixture(value)
  t.after(() => fs.rmSync(duplicate.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(duplicate.reference), duplicate.root).join('\n'),
    /checks must cover every declared capability exactly once/,
  )

  const failed = receipt()
  failed.checks[0].networkDisabled = false
  const failedFixture = writeFixture(failed)
  t.after(() => fs.rmSync(failedFixture.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(matrix(failedFixture.reference), failedFixture.root).join('\n'),
    /run check is not complete and passing/,
  )
})

test('CoreCLR JIT promotion requires exact commits and profiler-backed mapping', t => {
  const value = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  value.checks[1].mappingSource = 'method'
  const fixture = writeFixture(value)
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  const coreMatrix = coreClrMatrix('linuxCapability', fixture.reference)
  assert.match(
    validateRuntimePromotionReceipts(coreMatrix, fixture.root).join('\n'),
    /jit-asm check must prove profiler-backed MappingSource/,
  )

  value.checks[1].mappingSource = 'ordinary'
  const validFixture = writeFixture(value)
  t.after(() => fs.rmSync(validFixture.root, { recursive: true, force: true }))
  coreMatrix.coreClr[0].linuxCapability.promotionReceipt = validFixture.reference
  assert.deepEqual(validateRuntimePromotionReceipts(coreMatrix, validFixture.root), [])

  value.checks[1].sourceMappingKind = 'none'
  value.checks[1].mappingSource = 'method'
  value.operations = legacyCoreClrOperations()
  const methodFixture = writeFixture(value)
  t.after(() => fs.rmSync(methodFixture.root, { recursive: true, force: true }))
  coreMatrix.coreClr[0].linuxCapability.promotionReceipt = methodFixture.reference
  assert.deepEqual(validateRuntimePromotionReceipts(coreMatrix, methodFixture.root), [])
})

test('Checked-JIT debug mapping is bound to the isolated bridge and exact evidence', t => {
  const value = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  value.checks[1].sourceMappingKind = 'checked-jit-debug-info'
  value.checks[1].mappingSource = 'checked-jit-debug-info'
  value.operations = {
    run: legacyCoreClrOperations().run,
    jit: {
      implementation: 'sharplabnext-checked-jit-bridge-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
      assemblySha256: `sha256:${hex('d')}`,
    },
  }
  const fixture = writeFixture(value)
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  const coreMatrix = coreClrMatrix('linuxCapability', fixture.reference)
  coreMatrix.coreClr[0].checkedJit = { commit: '1'.repeat(40) }

  assert.deepEqual(validateRuntimePromotionReceipts(coreMatrix, fixture.root), [])

  value.checks[1].mappingSource = 'method'
  const invalidFixture = writeFixture(value)
  t.after(() => fs.rmSync(invalidFixture.root, { recursive: true, force: true }))
  coreMatrix.coreClr[0].linuxCapability.promotionReceipt = invalidFixture.reference
  assert.match(
    validateRuntimePromotionReceipts(coreMatrix, invalidFixture.root).join('\n'),
    /checked JIT mapping must prove checked-jit-debug-info MappingSource/,
  )
})

test('.NET 6 Checked-JIT bridge identity is independent from source-mapping precision', t => {
  const value = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  value.checks[1].sourceMappingKind = 'none'
  value.checks[1].mappingSource = 'none'
  value.operations = {
    run: legacyCoreClrOperations().run,
    jit: {
      implementation: 'sharplabnext-checked-jit-bridge-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
      assemblySha256: `sha256:${hex('d')}`,
    },
  }
  const fixture = writeFixture(value)
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  const checkedMatrix = coreClrMatrix('linuxCapability', fixture.reference)
  checkedMatrix.coreClr[0].checkedJit = { commit: '1'.repeat(40) }

  assert.deepEqual(validateRuntimePromotionReceipts(checkedMatrix, fixture.root), [])

  value.checks[1].mappingSource = 'ordinary'
  const falseMapping = writeFixture(value)
  t.after(() => fs.rmSync(falseMapping.root, { recursive: true, force: true }))
  checkedMatrix.coreClr[0].linuxCapability.promotionReceipt = falseMapping.reference
  assert.match(
    validateRuntimePromotionReceipts(checkedMatrix, falseMapping.root).join('\n'),
    /mapping-free or method-level jit-asm check has an invalid MappingSource/,
  )
  value.checks[1].mappingSource = 'none'

  value.operations = legacyCoreClrOperations()
  const legacyMismatch = writeFixture(value)
  t.after(() => fs.rmSync(legacyMismatch.root, { recursive: true, force: true }))
  checkedMatrix.coreClr[0].linuxCapability.promotionReceipt = legacyMismatch.reference
  assert.match(
    validateRuntimePromotionReceipts(checkedMatrix, legacyMismatch.root).join('\n'),
    /operations\.jit\.implementation must equal "sharplabnext-checked-jit-bridge-v1"/,
  )

  value.operations = {
    run: legacyCoreClrOperations().run,
    jit: {
      implementation: 'sharplabnext-checked-jit-bridge-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
      assemblySha256: `sha256:${hex('d')}`,
    },
  }
  const bridgeMismatch = writeFixture(value)
  t.after(() => fs.rmSync(bridgeMismatch.root, { recursive: true, force: true }))
  const retailMatrix = coreClrMatrix('linuxCapability', bridgeMismatch.reference)
  assert.match(
    validateRuntimePromotionReceipts(retailMatrix, bridgeMismatch.root).join('\n'),
    /operations\.jit\.implementation must equal "sharplabnext-legacy-jit-inspector-v1"/,
  )
})

test('profiler mapping is bound to the modern Linux JIT inspector', t => {
  const value = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  value.operations.jit.implementation = 'sharplabnext-legacy-jit-inspector-v1'
  value.operations.jit.assemblyPath = '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll'
  const fixture = writeFixture(value)
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))

  assert.match(
    validateRuntimePromotionReceipts(coreClrMatrix('linuxCapability', fixture.reference), fixture.root).join('\n'),
    /operations\.jit\.implementation must equal "sharplabnext-jit-inspector-v1"/,
  )

  value.operations.jit.implementation = 'sharplabnext-jit-inspector-v1'
  value.operations.jit.assemblyPath = '/opt/sharplabnext/SharpLabNext.JitInspector.dll'
  delete value.operations.jit.profilerSha256
  const missingProfilerHash = writeFixture(value)
  t.after(() => fs.rmSync(missingProfilerHash.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', missingProfilerHash.reference),
      missingProfilerHash.root,
    ).join('\n'),
    /operations\.jit\.profilerSha256 must be sha256/,
  )
})

test('Wine CoreCLR JIT cannot claim Linux profiler mapping', t => {
  const value = coreClrReceipt('wine-dotnet-10-linux-x64', 'wine', 'coreclr-wine')
  const fixture = writeFixture(value)
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))

  assert.match(
    validateRuntimePromotionReceipts(coreClrMatrix('wineCapability', fixture.reference), fixture.root).join('\n'),
    /Wine CoreCLR jit-asm check must use sourceMappingKind=none/,
  )

  value.checks[1].sourceMappingKind = 'none'
  value.checks[1].mappingSource = 'method'
  const validFixture = writeFixture(value)
  t.after(() => fs.rmSync(validFixture.root, { recursive: true, force: true }))
  assert.deepEqual(
    validateRuntimePromotionReceipts(coreClrMatrix('wineCapability', validFixture.reference), validFixture.root),
    [],
  )
})

test('Framework rejects JIT while Mono binds its dedicated JIT provider', t => {
  const framework = receipt()
  framework.checks.push({
    ...framework.checks[0],
    capability: 'jit-asm',
    sourceMappingKind: 'none',
    mappingSource: 'method',
  })
  const frameworkFixture = writeFixture(framework)
  t.after(() => fs.rmSync(frameworkFixture.root, { recursive: true, force: true }))
  assert.match(
    validateRuntimePromotionReceipts(
      matrix(frameworkFixture.reference, ['run', 'jit-asm']),
      frameworkFixture.root,
    ).join('\n'),
    /framework capability cannot declare jit-asm/,
  )

  const mono = receipt('mono-6.8-linux-x64')
  mono.matrixTargetId = 'mono-6.8-linux-x64'
  mono.platform = 'mono'
  mono.family = 'mono'
  mono.resolvedVersion = '6.8.0.105'
  mono.checks.push({
    ...mono.checks[0],
    capability: 'jit-asm',
    sourceMappingKind: 'none',
    mappingSource: 'none',
  })
  mono.operations.jit = {
    implementation: 'sharplabnext-mono-jit-inspector-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll',
    assemblySha256: `sha256:${hex('d')}`,
  }
  const monoFixture = writeFixture(mono)
  t.after(() => fs.rmSync(monoFixture.root, { recursive: true, force: true }))
  const monoMatrix = {
    coreClr: [],
    mono: {
      id: 'mono-6.8-linux-x64',
      version: '6.8.0.105',
      capability: {
        capabilities: ['run', 'jit-asm'],
        promotionState: 'verified',
        promotionReceipt: monoFixture.reference,
      },
    },
    framework: { targets: [] },
  }
  assert.deepEqual(validateRuntimePromotionReceipts(monoMatrix, monoFixture.root), [])
})

test('verified receipts require Run and bind instrumentation to the modern Runner', t => {
  const noRun = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  noRun.checks = [noRun.checks[1]]
  const noRunFixture = writeFixture(noRun)
  t.after(() => fs.rmSync(noRunFixture.root, { recursive: true, force: true }))
  const noRunMatrix = coreClrMatrix('linuxCapability', noRunFixture.reference)
  noRunMatrix.coreClr[0].linuxCapability.capabilities = ['jit-asm']
  assert.match(
    validateRuntimePromotionReceipts(noRunMatrix, noRunFixture.root).join('\n'),
    /verified capabilities must include a passing run preflight/,
  )

  const instrumentation = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  instrumentation.operations.run.implementation = 'sharplabnext-legacy-jit-inspector-v1'
  instrumentation.operations.run.assemblyPath =
    '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll'
  instrumentation.checks = [
    instrumentation.checks[0],
    {
      ...instrumentation.checks[0],
      capability: 'inspection',
    },
  ]
  const instrumentationFixture = writeFixture(instrumentation)
  t.after(() => fs.rmSync(instrumentationFixture.root, { recursive: true, force: true }))
  const instrumentationMatrix = coreClrMatrix('linuxCapability', instrumentationFixture.reference)
  instrumentationMatrix.coreClr[0].linuxCapability.capabilities = ['run', 'inspection']
  assert.match(
    validateRuntimePromotionReceipts(
      instrumentationMatrix,
      instrumentationFixture.root,
    ).join('\n'),
    /operations\.run\.implementation must equal "sharplabnext-runner-v1"/,
  )

  const missingSupport = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  missingSupport.checks = [
    missingSupport.checks[0],
    {
      ...missingSupport.checks[0],
      capability: 'inspection',
    },
  ]
  const missingSupportFixture = writeFixture(missingSupport)
  t.after(() => fs.rmSync(missingSupportFixture.root, { recursive: true, force: true }))
  updateCapabilityEvidence(missingSupportFixture, 'inspection', evidence => {
    evidence.artifacts = evidence.artifacts.filter(artifact => artifact.role !== 'support-assembly')
  })
  const missingSupportMatrix = coreClrMatrix('linuxCapability', missingSupportFixture.reference)
  missingSupportMatrix.coreClr[0].linuxCapability.capabilities = ['run', 'inspection']
  assert.match(
    validateRuntimePromotionReceipts(
      missingSupportMatrix,
      missingSupportFixture.root,
    ).join('\n'),
    /has no valid SharpLab\.Runtime support-assembly artifact for its instrumentation capabilities/,
  )
})

test('Run and JIT-only CoreCLR evidence may omit an incompatible support assembly', t => {
  const value = coreClrReceipt('dotnet-10-linux-x64', 'linux', 'coreclr')
  const fixture = writeFixture(value)
  t.after(() => fs.rmSync(fixture.root, { recursive: true, force: true }))
  for (const capability of ['run', 'jit-asm']) {
    updateCapabilityEvidence(fixture, capability, evidence => {
      evidence.artifacts = evidence.artifacts.filter(artifact => artifact.role !== 'support-assembly')
    })
  }

  assert.deepEqual(
    validateRuntimePromotionReceipts(
      coreClrMatrix('linuxCapability', fixture.reference),
      fixture.root,
    ),
    [],
  )
})

function monoReceipt() {
  const value = receipt('mono-6.12-linux-x64')
  value.matrixTargetId = 'mono-6.12-linux-x64'
  value.platform = 'mono'
  value.family = 'mono'
  value.resolvedVersion = '6.12.0.182'
  return value
}

function monoMatrix(reference) {
  return {
    coreClr: [],
    mono: {
      id: 'mono-6.12-linux-x64',
      version: '6.12.0.182',
      capability: {
        capabilities: ['run'],
        promotionState: 'verified',
        promotionReceipt: reference,
      },
    },
    framework: { targets: [] },
  }
}

function wineCoreClrMethodReceipt() {
  const value = coreClrReceipt('wine-dotnet-10-linux-x64', 'wine', 'coreclr-wine')
  value.checks[1].sourceMappingKind = 'none'
  value.checks[1].mappingSource = 'method'
  return value
}

function legacyCoreClrOperations() {
  const helper = {
    implementation: 'sharplabnext-legacy-jit-inspector-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    assemblySha256: `sha256:${hex('c')}`,
  }
  return {
    run: { ...helper },
    jit: { ...helper },
  }
}

function coreClrReceipt(profileId, platform, family) {
  const value = receipt(profileId)
  value.matrixTargetId = 'dotnet-10'
  value.platform = platform
  value.family = family
  value.resolvedVersion = '10.0.10'
  value.runtimeIdentity = {
    runtimeCommit: '1'.repeat(40),
    jitVersion: '10.0.10',
    jitCommit: '2'.repeat(40),
  }
  value.componentIdentity = platform === 'linux'
    ? {
        sourceUri: 'https://example.invalid/runtime-linux.tar.gz',
        sourceDigest: `sha512:${'3'.repeat(128)}`,
      }
    : {
        sourceUri: 'https://example.invalid/runtime-windows.zip',
        sourceDigest: `sha512:${'4'.repeat(128)}`,
      }
  value.operations = family === 'coreclr-wine'
    ? legacyCoreClrOperations()
    : {
        run: {
          implementation: 'sharplabnext-runner-v1',
          assemblyPath: '/opt/sharplabnext/SharpLabNext.Runner.dll',
          assemblySha256: `sha256:${hex('c')}`,
        },
        jit: {
          implementation: 'sharplabnext-jit-inspector-v1',
          assemblyPath: '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
          assemblySha256: `sha256:${hex('d')}`,
          profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
          profilerSha256: `sha256:${hex('e')}`,
        },
      }
  value.checks = [
    value.checks[0],
    {
      ...value.checks[0],
      capability: 'jit-asm',
      sourceMappingKind: 'linux-profiler',
      mappingSource: 'ordinary',
    },
  ]
  return value
}

function coreClrMatrix(capabilityName, reference) {
  const blocked = {
    capabilities: [],
    promotionState: 'blocked',
  }
  return {
    coreClr: [{
      id: 'dotnet-10',
      version: '10.0.10',
      runtimeCommit: '1'.repeat(40),
      jitCommit: '2'.repeat(40),
      linux: {
        url: 'https://example.invalid/runtime-linux.tar.gz',
        sha512: '3'.repeat(128),
      },
      windows: {
        url: 'https://example.invalid/runtime-windows.zip',
        sha512: '4'.repeat(128),
      },
      linuxCapability: capabilityName === 'linuxCapability'
        ? {
            capabilities: ['run', 'jit-asm'],
            promotionState: 'verified',
            promotionReceipt: reference,
          }
        : blocked,
      wineCapability: capabilityName === 'wineCapability'
        ? {
            capabilities: ['run', 'jit-asm'],
            promotionState: 'verified',
            promotionReceipt: reference,
          }
        : blocked,
    }],
    framework: { targets: [] },
  }
}
