import { containsExplicitJsonNull } from './strict-owned-json.mjs'

const sha256Pattern = /^sha256:[0-9a-f]{64}$/
const imageReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const commitPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const timestampPattern = /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z$/
const containerIdPattern = /^[0-9a-f]{64}$/
const stableIdPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const evidenceRoles = new Set([
  'helper',
  'control-host',
  'runtime-host',
  'support-assembly',
  'jit-library',
  'profiler',
])

export function validateRuntimeCapabilityEvidence({
  binding,
  profile,
  receipt,
  check,
  evidence,
  retainedImageFiles,
}) {
  const failures = []
  const capability = check?.capability ?? '<missing>'
  const prefix = `${binding.profileId}: ${capability} evidence`

  if (!isObject(evidence)) return [`${prefix} must be a JSON object`]
  if (containsExplicitJsonNull(evidence)) {
    failures.push(
      `${prefix} cannot contain explicit JSON null values; optional properties must be omitted`,
    )
  }
  expectEqual(failures, evidence.schemaVersion, 1, `${prefix} schemaVersion`)
  expectEqual(failures, evidence.profileId, binding.profileId, `${prefix} profileId`)
  expectEqual(failures, evidence.capability, capability, `${prefix} capability`)
  expectEqual(failures, evidence.result, 'passed', `${prefix} result`)
  expectEqual(failures, evidence.sourceRevision, receipt.sourceRevision, `${prefix} sourceRevision`)
  if (!commitPattern.test(evidence.sourceRevision ?? '')) {
    failures.push(`${prefix} sourceRevision must be a full lowercase Git commit`)
  }
  if (!timestampPattern.test(evidence.completedAtUtc ?? '') ||
      !Number.isFinite(Date.parse(evidence.completedAtUtc))) {
    failures.push(`${prefix} completedAtUtc must be a canonical UTC timestamp`)
  }

  validateImage(evidence.image, receipt.image, failures, prefix)
  validateProducer(evidence.producer, receipt.sourceRevision, receipt.planSha256, failures, prefix)
  const artifacts = validateArtifacts(
    binding,
    profile,
    receipt,
    check,
    evidence.artifacts,
    retainedImageFiles,
    failures,
    prefix,
  )
  validateInvocation(binding, profile, receipt, check, evidence.invocation, artifacts, failures, prefix)
  validateSandbox(binding, profile, evidence.sandbox, failures, prefix)
  validateLifecycle(evidence.lifecycle, failures, prefix)
  validateCapabilityDetails(binding, receipt, check, evidence, artifacts, failures, prefix)
  return failures
}

function validateImage(image, expected, failures, prefix) {
  if (!isObject(image)) {
    failures.push(`${prefix} image is missing`)
    return
  }
  if (!imageReferencePattern.test(image.reference ?? '')) {
    failures.push(`${prefix} image.reference is not immutable`)
  }
  if (!sha256Pattern.test(image.imageId ?? '')) {
    failures.push(`${prefix} image.imageId is not canonical`)
  }
  expectEqual(failures, image.reference, expected?.reference, `${prefix} image.reference`)
  expectEqual(failures, image.imageId, expected?.imageId, `${prefix} image.imageId`)
}

function validateProducer(producer, sourceRevision, planSha256, failures, prefix) {
  if (!isObject(producer)) {
    failures.push(`${prefix} producer is missing`)
    return
  }
  expectEqual(
    failures,
    producer.id,
    'sharplabnext-runtime-preflight-v1',
    `${prefix} producer.id`,
  )
  expectEqual(
    failures,
    producer.sourceRevision,
    sourceRevision,
    `${prefix} producer.sourceRevision`,
  )
  if (!sha256Pattern.test(producer.planSha256 ?? '')) {
    failures.push(`${prefix} producer.planSha256 is not canonical`)
  }
  expectEqual(failures, producer.planSha256, planSha256, `${prefix} producer.planSha256`)
}

function validateArtifacts(
  binding,
  profile,
  receipt,
  check,
  value,
  retainedImageFiles,
  failures,
  prefix,
) {
  if (!Array.isArray(value) || value.length < 2 || value.length > 8) {
    failures.push(`${prefix} artifacts must contain between 2 and 8 entries`)
    return new Map()
  }
  const byRole = new Map()
  const byPath = new Map()
  for (const artifact of value) {
    if (!isObject(artifact) || !evidenceRoles.has(artifact.role)) {
      failures.push(`${prefix} contains an invalid artifact role`)
      continue
    }
    if (byRole.has(artifact.role)) {
      failures.push(`${prefix} contains duplicate artifact role '${artifact.role}'`)
      continue
    }
    if (!isCanonicalImagePath(artifact.path)) {
      failures.push(`${prefix} artifact '${artifact.role}' has an invalid image path`)
    } else if (byPath.has(artifact.path)) {
      failures.push(
        `${prefix} artifacts '${byPath.get(artifact.path).role}' and '${artifact.role}' share a path`,
      )
    }
    if (!sha256Pattern.test(artifact.sha256 ?? '') ||
        !Number.isSafeInteger(artifact.sizeBytes) ||
        artifact.sizeBytes <= 0 || artifact.sizeBytes > 268435456) {
      failures.push(`${prefix} artifact '${artifact.role}' has invalid bytes identity`)
    }
    if (!['elf', 'pe', 'managed-pe', 'script'].includes(artifact.format) ||
        !['x64', 'anycpu', 'shell'].includes(artifact.architecture)) {
      failures.push(`${prefix} artifact '${artifact.role}' has invalid format or architecture`)
    }
    if (!validRoleMetadata(artifact)) {
      failures.push(`${prefix} artifact '${artifact.role}' has metadata incompatible with its role`)
    }
    byRole.set(artifact.role, artifact)
    byPath.set(artifact.path, artifact)
  }

  const operationName = check.capability === 'jit-asm' ? 'jit' : 'run'
  const operation = receipt.operations?.[operationName]
  const helper = byRole.get('helper')
  if (!isObject(operation) || helper === undefined ||
      helper.path !== operation.assemblyPath || helper.sha256 !== operation.assemblySha256 ||
      helper.format !== 'managed-pe' || helper.architecture !== 'anycpu') {
    failures.push(`${prefix} helper artifact does not match receipt operations.${operationName}`)
  }
  validateExecutableHosts(profile, operationName, byRole, failures, prefix)

  const coreClr2 = isCoreClrMajor(binding, receipt, 2)
  const supportsSupportAssembly =
    (binding.family === 'coreclr' || binding.family === 'coreclr-wine') && !coreClr2
  const requiresSupportAssembly = binding.capability?.capabilities?.some(capability =>
    capability === 'inspection' || capability === 'execution-flow') === true
  const support = byRole.get('support-assembly')
  if (support !== undefined) {
    if (coreClr2) {
      failures.push(`${prefix} CoreCLR 2.x cannot bind a SharpLab.Runtime support-assembly artifact`)
    } else if (!supportsSupportAssembly ||
        support.path !== '/opt/sharplabnext/SharpLab.Runtime.dll' ||
        support?.format !== 'managed-pe' || support?.architecture !== 'anycpu') {
      failures.push(`${prefix} has an invalid SharpLab.Runtime support-assembly artifact`)
    }
  } else if (requiresSupportAssembly) {
    failures.push(coreClr2
      ? `${prefix} CoreCLR 2.x cannot declare SharpLab.Runtime instrumentation capabilities`
      : `${prefix} has no valid SharpLab.Runtime support-assembly artifact ` +
        'for its instrumentation capabilities')
  }

  const jitLibrary = byRole.get('jit-library')
  const profiler = byRole.get('profiler')
  if (check.capability === 'jit-asm') {
    if (jitLibrary === undefined) {
      failures.push(`${prefix} has no jit-library artifact`)
    } else if (binding.platform === 'mono' &&
               (jitLibrary.format !== 'elf' || jitLibrary.architecture !== 'x64' ||
                jitLibrary.path !== '/usr/bin/mono-sgen')) {
      failures.push(`${prefix} Mono jit-library must be the fixed x64 ELF /usr/bin/mono-sgen host`)
    } else if (binding.platform === 'linux' &&
               (jitLibrary.format !== 'elf' || jitLibrary.architecture !== 'x64' ||
                !jitLibrary.path.endsWith('/libclrjit.so'))) {
      failures.push(`${prefix} Linux jit-library must be the x64 ELF libclrjit.so`)
    } else if (binding.platform === 'wine' &&
               (jitLibrary.format !== 'pe' || jitLibrary.architecture !== 'x64' ||
                !jitLibrary.path.toLowerCase().endsWith('clrjit.dll'))) {
      failures.push(`${prefix} Wine jit-library must be the x64 PE clrjit.dll`)
    }
    if (check.sourceMappingKind === 'linux-profiler') {
      if (profiler?.path !== operation?.profilerPath ||
          profiler?.sha256 !== operation?.profilerSha256 ||
          profiler?.format !== 'elf' || profiler?.architecture !== 'x64') {
        failures.push(`${prefix} profiler artifact does not match the receipt JIT profiler`)
      }
    } else if (profiler !== undefined) {
      failures.push(`${prefix} cannot bind a profiler for mapping kind '${check.sourceMappingKind}'`)
    }
  } else if (jitLibrary !== undefined || profiler !== undefined) {
    failures.push(`${prefix} non-JIT capability cannot bind JIT artifacts`)
  }
  mergeRetainedImageFiles(retainedImageFiles, byPath, failures, prefix)
  return byRole
}

function validateExecutableHosts(profile, operationName, artifacts, failures, prefix) {
  const operation = profile?.operations?.[operationName]
  const command = operation?.command
  if (!isObject(operation) || !isObject(command) || !isCanonicalImagePath(command.executable)) {
    failures.push(`${prefix} operation executable must be a canonical absolute image path`)
    return
  }

  let innerHostToken
  let innerHostPath
  if (operation.implementationId === 'sharplabnext-wine-runner-v1') {
    innerHostToken = command.argv?.[2]
    innerHostPath = normalizeHostImagePath(innerHostToken)
    if (innerHostPath === undefined) {
      failures.push(`${prefix} Wine runner command has no canonical fixed target host`)
      return
    }
  } else if (operation.implementationId === 'sharplabnext-legacy-jit-inspector-v1' &&
             operation.pathStyle === 'wine-z') {
    innerHostToken = command.argv?.[0]
    innerHostPath = profile?.layout?.dotNetHostPath
    const expectedToken = typeof innerHostPath === 'string'
      ? `Z:${innerHostPath.replaceAll('/', '\\')}`
      : undefined
    if (!isCanonicalImagePath(innerHostPath) || innerHostToken !== expectedToken) {
      failures.push(`${prefix} Wine dotnet.exe command token does not match the Runtime Profile image path`)
      return
    }
  } else if (operation.implementationId === 'sharplabnext-mono-jit-inspector-v1') {
    innerHostToken = '/usr/bin/mono'
    innerHostPath = '/usr/bin/mono'
  }

  if (innerHostPath === undefined) {
    if (artifacts.has('control-host')) {
      failures.push(`${prefix} single-host command cannot declare a control-host artifact`)
    }
    validateExecutableHostArtifact(
      artifacts,
      'runtime-host',
      command.executable,
      'elf',
      failures,
      prefix,
    )
    return
  }

  if (innerHostPath === command.executable) {
    failures.push(`${prefix} control and runtime hosts must resolve to distinct image paths`)
    return
  }
  validateExecutableHostArtifact(
    artifacts,
    'control-host',
    command.executable,
    'elf',
    failures,
    prefix,
  )
  validateExecutableHostArtifact(
    artifacts,
    'runtime-host',
    innerHostPath,
    innerHostToken.includes('\\') ? 'pe' : 'elf',
    failures,
    prefix,
  )
}

function validateExecutableHostArtifact(
  artifacts,
  role,
  expectedPath,
  expectedFormat,
  failures,
  prefix,
) {
  const artifact = artifacts.get(role)
  if (artifact?.path !== expectedPath || artifact?.format !== expectedFormat ||
      artifact?.architecture !== 'x64') {
    failures.push(
      `${prefix} ${role} artifact does not match the Runtime Profile executable host ` +
      `'${expectedPath}'`,
    )
  }
}

function normalizeHostImagePath(token) {
  if (isCanonicalImagePath(token)) return token
  if (typeof token !== 'string' || !token.startsWith('Z:\\')) return undefined
  const path = `/${token.slice(3).replaceAll('\\', '/')}`
  return isCanonicalImagePath(path) ? path : undefined
}

function validRoleMetadata(artifact) {
  switch (artifact.role) {
    case 'helper':
    case 'support-assembly':
      return artifact.format === 'managed-pe' && artifact.architecture === 'anycpu'
    case 'control-host':
    case 'runtime-host':
    case 'jit-library':
      return ['elf', 'pe'].includes(artifact.format) && artifact.architecture === 'x64'
    case 'profiler':
      return artifact.format === 'elf' && artifact.architecture === 'x64'
    default:
      return false
  }
}

function mergeRetainedImageFiles(retainedImageFiles, artifactsByPath, failures, prefix) {
  if (!(retainedImageFiles instanceof Map)) return
  for (const [path, artifact] of artifactsByPath) {
    const existing = retainedImageFiles.get(path)
    if (existing !== undefined && ![
      'sha256',
      'sizeBytes',
      'role',
      'format',
      'architecture',
    ].every(property => existing[property] === artifact[property])) {
      failures.push(
        `${prefix} conflicts with another capability's path, byte, role, format, or ` +
        `architecture identity for image file '${path}'`,
      )
    } else if (existing === undefined) {
      retainedImageFiles.set(path, { ...artifact })
    }
  }
}

function createExpectedCommand(operation, invocation) {
  if (!isObject(operation.command) || !Array.isArray(operation.command.argv) ||
      typeof operation.command.executable !== 'string') return undefined
  const command = [operation.command.executable]
  for (const token of operation.command.argv) {
    if (token === '{entryAssembly}') command.push(invocation.entryAssembly?.path)
    else if (token === '{arguments}') continue
    else if (token === '{methodFilter}') {
      if (typeof invocation.methodFilter === 'string' && invocation.methodFilter.length > 0) {
        command.push(invocation.methodFilter)
      }
    } else command.push(token)
  }
  return command.every(token => typeof token === 'string') ? command : undefined
}

function validateInvocation(binding, profile, receipt, check, invocation, artifacts, failures, prefix) {
  if (!isObject(invocation)) {
    failures.push(`${prefix} invocation is missing`)
    return
  }
  const operationName = check.capability === 'jit-asm' ? 'jit' : 'run'
  const operation = receipt.operations?.[operationName]
  const profileOperation = profile?.operations?.[operationName]
  expectEqual(
    failures,
    invocation.implementation,
    operation?.implementation,
    `${prefix} invocation.implementation`,
  )
  if (!Array.isArray(invocation.command) || invocation.command.length < 2 ||
      invocation.command.length > 64 || invocation.command.some(token =>
        typeof token !== 'string' || token.length === 0 || token.length > 4096 || /[\0\r\n]/.test(token))) {
    failures.push(`${prefix} invocation.command is invalid`)
  } else if (!isObject(profileOperation)) {
    failures.push(`${prefix} Runtime Profile operation is missing`)
  } else {
    const expectedCommand = createExpectedCommand(profileOperation, invocation)
    if (expectedCommand === undefined || !arraysEqual(invocation.command, expectedCommand)) {
      failures.push(`${prefix} invocation command does not match the selected Runtime Profile operation`)
    }
  }
  if (!isObject(invocation.entryAssembly) ||
      !isWorkspacePath(invocation.entryAssembly.path, binding.platform) ||
      !sha256Pattern.test(invocation.entryAssembly.sha256 ?? '')) {
    failures.push(`${prefix} invocation entry assembly identity is invalid`)
  } else if (!invocation.command?.includes(invocation.entryAssembly.path)) {
    failures.push(`${prefix} invocation command does not contain the entry assembly path`)
  }
  if (check.capability === 'jit-asm') {
    if (typeof invocation.methodFilter !== 'string' || invocation.methodFilter.length < 1 ||
        invocation.methodFilter.length > 256 || /[\0\r\n]/.test(invocation.methodFilter) ||
        !invocation.command?.includes(invocation.methodFilter)) {
      failures.push(`${prefix} JIT invocation must bind a concrete method filter`)
    }
  } else if (invocation.methodFilter !== undefined) {
    failures.push(`${prefix} non-JIT invocation cannot declare a method filter`)
  }
  if (invocation.outcome !== 'succeeded' || invocation.exitCode !== 0 ||
      !Number.isSafeInteger(invocation.runtimeFrameCount) || invocation.runtimeFrameCount < 1 ||
      invocation.terminalFrameKind !== 'Exit' || invocation.terminalStatus !== 'completed' ||
      !isBoundedByteCount(invocation.stdoutBytes) || !isBoundedByteCount(invocation.stderrBytes)) {
    failures.push(`${prefix} invocation result is not a successful bounded RuntimeFrame result`)
  }
}

function validateSandbox(binding, profile, sandbox, failures, prefix) {
  if (!isObject(sandbox)) {
    failures.push(`${prefix} sandbox is missing`)
    return
  }
  if (!stableIdPattern.test(sandbox.supervisorPolicyId ?? '') ||
      !stableIdPattern.test(sandbox.securityPolicyId ?? '') ||
      !sha256Pattern.test(sandbox.seccompSha256 ?? '') ||
      !containerIdPattern.test(sandbox.containerId ?? '') ||
      sandbox.networkMode !== 'none' || sandbox.networkProbeBlocked !== true ||
      sandbox.readOnlyRootFilesystem !== true || sandbox.readOnlyProbeBlocked !== true ||
      JSON.stringify(sandbox.capDrop) !== JSON.stringify(['ALL']) ||
      sandbox.noNewPrivileges !== true) {
    failures.push(`${prefix} sandbox does not prove the required Supervisor isolation`)
  }
  const expectedUser = profile?.container?.executionUser
  if (!['0:0', '1654:1654'].includes(expectedUser)) {
    failures.push(`${prefix} Runtime Profile container.executionUser is invalid`)
  } else {
    expectEqual(failures, sandbox.user, expectedUser, `${prefix} sandbox.user`)
  }
  const policy = Array.isArray(profile?.securityPolicies)
    ? profile.securityPolicies.find(item => item?.id === sandbox.securityPolicyId)
    : undefined
  if (!isObject(policy) || !profile.allowedSecurityPolicyIds?.includes(policy.id)) {
    failures.push(`${prefix} security policy is not selected by the Runtime Profile`)
    return
  }
  for (const [evidenceName, profileName, transform = value => value] of [
    ['nanoCpus', 'nanoCpus'],
    ['memoryBytes', 'memoryBytes'],
    ['pidsLimit', 'pidsLimit'],
    ['deadlineMilliseconds', 'maximumDurationSeconds', value => value * 1000],
    ['outputLimitBytes', 'maximumOutputBytes'],
    ['tmpfsBytes', 'tmpfsBytes'],
  ]) {
    const expected = transform(policy[profileName])
    if (!Number.isSafeInteger(expected) || sandbox[evidenceName] !== expected) {
      failures.push(
        `${prefix} sandbox.${evidenceName} does not match the selected Runtime Profile policy`,
      )
    }
  }
}

function validateLifecycle(lifecycle, failures, prefix) {
  if (!isObject(lifecycle)) {
    failures.push(`${prefix} lifecycle probes are missing`)
    return
  }
  for (const [name, terminalStatus] of [
    ['outputOverflow', 'output-limit-exceeded'],
    ['timeout', 'timeout'],
    ['cancellation', 'cancelled'],
    ['processTreeCleanup', 'completed'],
  ]) {
    const probe = lifecycle[name]
    if (!isObject(probe) || probe.result !== 'passed' ||
        probe.terminalStatus !== terminalStatus ||
        probe.containerRemoved !== true || probe.processTreeRemoved !== true) {
      failures.push(`${prefix} lifecycle.${name} did not pass with complete cleanup`)
    }
  }
}

function validateCapabilityDetails(binding, receipt, check, evidence, artifacts, failures, prefix) {
  const details = ['run', 'jit', 'inspection', 'executionFlow'].filter(name => evidence[name] !== undefined)
  const expectedDetail = {
    run: 'run',
    'jit-asm': 'jit',
    inspection: 'inspection',
    'execution-flow': 'executionFlow',
  }[check.capability]
  if (details.length !== 1 || details[0] !== expectedDetail) {
    failures.push(`${prefix} must contain exactly its '${expectedDetail}' capability result`)
    return
  }
  switch (check.capability) {
    case 'run':
      validateRunEvidence(evidence.run, failures, prefix)
      break
    case 'jit-asm':
      validateJitEvidence(binding, receipt, check, evidence.jit, artifacts, failures, prefix)
      break
    case 'inspection':
      validateInspectionEvidence(evidence.inspection, failures, prefix)
      break
    case 'execution-flow':
      validateExecutionFlowEvidence(evidence.executionFlow, failures, prefix)
      break
  }
}

function validateRunEvidence(run, failures, prefix) {
  if (!isObject(run) || typeof run.expectedStdoutMarker !== 'string' ||
      run.expectedStdoutMarker.length === 0 ||
      run.expectedStdoutMarker !== run.observedStdoutMarker ||
      typeof run.expectedStderrMarker !== 'string' ||
      run.expectedStderrMarker.length === 0 ||
      run.expectedStderrMarker !== run.observedStderrMarker ||
      run.exceptionFrameValidated !== true) {
    failures.push(`${prefix} Run markers or structured exception probe did not pass`)
  }
}

function validateJitEvidence(binding, receipt, check, jit, artifacts, failures, prefix) {
  if (!isObject(jit)) {
    failures.push(`${prefix} JIT result is missing`)
    return
  }
  expectEqual(failures, jit.runtimeVersion, receipt.resolvedVersion, `${prefix} jit.runtimeVersion`)
  expectEqual(failures, jit.jitVersion, receipt.runtimeIdentity?.jitVersion, `${prefix} jit.jitVersion`)
  if (!Array.isArray(jit.methods) || jit.methods.length < 1 || jit.methods.length > 10000) {
    failures.push(`${prefix} JIT result has no bounded method list`)
    return
  }
  let rangeCount = 0
  const sourceRanges = new Set()
  for (const method of jit.methods) {
    if (!isObject(method) || !/^0x06[0-9a-f]{6}$/.test(method.metadataToken ?? '') ||
        typeof method.displayName !== 'string' || method.displayName.length === 0 ||
        !Number.isSafeInteger(method.nativeCodeBytes) || method.nativeCodeBytes < 1 ||
        !Number.isSafeInteger(method.instructionCount) || method.instructionCount < 1 ||
        !Array.isArray(method.sourceRanges)) {
      failures.push(`${prefix} contains an invalid or empty JIT method`)
      continue
    }
    for (const range of method.sourceRanges) {
      if (!validSourceRange(range)) {
        failures.push(`${prefix} contains an invalid JIT source range`)
        continue
      }
      rangeCount += 1
      sourceRanges.add([
        range.document,
        range.startLine,
        range.startColumn,
        range.endLine,
        range.endColumn,
      ].join(':'))
    }
  }
  const mapping = jit.mapping
  if (!isObject(mapping)) {
    failures.push(`${prefix} JIT mapping result is missing`)
    return
  }
  expectEqual(failures, mapping.kind, check.sourceMappingKind, `${prefix} jit.mapping.kind`)
  expectEqual(failures, mapping.source, check.mappingSource, `${prefix} jit.mapping.source`)
  expectEqual(failures, mapping.rangeCount, rangeCount, `${prefix} jit.mapping.rangeCount`)
  expectEqual(
    failures,
    mapping.distinctSourceRangeCount,
    sourceRanges.size,
    `${prefix} jit.mapping.distinctSourceRangeCount`,
  )

  if (check.sourceMappingKind === 'none') {
    if (jit.pdb !== undefined || rangeCount !== 0 || mapping.allRangesMatchPdb !== false) {
      failures.push(`${prefix} mapping-free or method-level JIT evidence cannot claim PDB source ranges`)
    }
  } else {
    const pdbIdentityIsValid = isObject(jit.pdb) && isWorkspacePdb(jit.pdb) &&
      sha256Pattern.test(jit.pdb.sha256 ?? '') &&
      /^[0-9a-f]{40}$/.test(jit.pdb.contentId ?? '')
    if (!pdbIdentityIsValid) {
      failures.push(`${prefix} PDB identity is invalid`)
    }
    if (!Number.isSafeInteger(jit.pdb?.sequencePointCount) ||
        jit.pdb.sequencePointCount < 2 || rangeCount < 2 || sourceRanges.size < 2 ||
        mapping.allRangesMatchPdb !== true) {
      failures.push(`${prefix} mapped JIT evidence lacks multiple PDB-matched source ranges`)
    }
  }
  if (check.sourceMappingKind === 'linux-profiler' && !artifacts.has('profiler')) {
    failures.push(`${prefix} profiler mapping has no bound profiler bytes`)
  }
  if (binding.platform === 'linux' && artifacts.get('jit-library')?.format !== 'elf') {
    failures.push(`${prefix} Linux JIT did not bind an ELF JIT library`)
  }
}

function validateInspectionEvidence(inspection, failures, prefix) {
  if (!isObject(inspection) || !Number.isSafeInteger(inspection.recordCount) ||
      inspection.recordCount < 2 || !Array.isArray(inspection.kinds) ||
      new Set(inspection.kinds).size !== inspection.kinds.length ||
      !inspection.kinds.includes('Value') || !inspection.kinds.includes('MemoryGraph') ||
      inspection.valueProbePassed !== true || inspection.memoryGraphProbePassed !== true) {
    failures.push(`${prefix} inspection records did not prove Value and MemoryGraph behavior`)
  }
}

function validateExecutionFlowEvidence(flow, failures, prefix) {
  if (!isObject(flow) || !Number.isSafeInteger(flow.recordCount) || flow.recordCount < 2 ||
      !Number.isSafeInteger(flow.sequencePointCount) || flow.sequencePointCount < 1 ||
      !Number.isSafeInteger(flow.branchCount) || flow.branchCount < 1 ||
      !Number.isSafeInteger(flow.sourceRangeCount) || flow.sourceRangeCount < 2 ||
      !sha256Pattern.test(flow.derivedArtifactSha256 ?? '')) {
    failures.push(`${prefix} execution-flow evidence lacks sequence, branch, or source-range proof`)
  }
}

function validSourceRange(range) {
  return isObject(range) && Number.isSafeInteger(range.ilOffset) && range.ilOffset >= 0 &&
    Number.isSafeInteger(range.nativeStartOffset) && range.nativeStartOffset >= 0 &&
    Number.isSafeInteger(range.nativeEndOffset) && range.nativeEndOffset > range.nativeStartOffset &&
    typeof range.document === 'string' && range.document.length > 0 &&
    Number.isSafeInteger(range.startLine) && range.startLine >= 1 &&
    Number.isSafeInteger(range.startColumn) && range.startColumn >= 1 &&
    Number.isSafeInteger(range.endLine) && range.endLine >= range.startLine &&
    Number.isSafeInteger(range.endColumn) && range.endColumn >= 1 &&
    (range.endLine > range.startLine || range.endColumn > range.startColumn)
}

function isWorkspacePdb(pdb) {
  if (typeof pdb.path !== 'string' || !pdb.path.endsWith('.pdb') ||
      pdb.path.length < 16 || pdb.path.length > 4096 || /[\0\r\n]/.test(pdb.path)) return false
  if (pdb.path.startsWith('/workspace/')) {
    return !pdb.path.includes('\\') && canonicalPathSuffix(pdb.path.slice('/workspace/'.length), '/')
  }
  return pdb.path.startsWith('Z:\\workspace\\') && !pdb.path.includes('/') &&
    canonicalPathSuffix(pdb.path.slice('Z:\\workspace\\'.length), '\\')
}

function isWorkspacePath(value, platform) {
  if (typeof value !== 'string' || /[\0\r\n]/.test(value)) return false
  if (platform === 'wine' || platform === 'framework') {
    const prefix = 'Z:\\workspace\\'
    return value.startsWith(prefix) && !value.includes('/') &&
      canonicalPathSuffix(value.slice(prefix.length), '\\') && /\.(?:dll|exe)$/i.test(value)
  }
  const prefix = '/workspace/'
  return value.startsWith(prefix) && !value.includes('\\') &&
    canonicalPathSuffix(value.slice(prefix.length), '/') && /\.(?:dll|exe)$/.test(value)
}

function canonicalPathSuffix(value, separator) {
  const segments = value.split(separator)
  return segments.length > 0 &&
    segments.every(segment => segment.length > 0 && segment !== '.' && segment !== '..')
}

function isCanonicalImagePath(value) {
  if (typeof value !== 'string' || !value.startsWith('/') || value.endsWith('/') ||
      value.includes('//') || value.includes('\\') || /[\0\r\n]/.test(value)) return false
  const segments = value.split('/').slice(1)
  return segments.length > 0 && segments.every(segment => segment.length > 0 && segment !== '.' && segment !== '..')
}

function isBoundedByteCount(value) {
  return Number.isSafeInteger(value) && value >= 0 && value <= 16777216
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function isCoreClrMajor(binding, receipt, expectedMajor) {
  if (binding.family !== 'coreclr' && binding.family !== 'coreclr-wine') return false
  const match = /^(\d+)(?:[.-]|$)/.exec(receipt.resolvedVersion ?? '')
  return match !== null && Number(match[1]) === expectedMajor
}

function arraysEqual(left, right) {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

function expectEqual(failures, actual, expected, label) {
  if (actual !== expected) {
    failures.push(`${label} must equal ${JSON.stringify(expected)}; observed ${JSON.stringify(actual)}`)
  }
}
