/**
 * Exercise declared JIT operations against the immutable current candidate
 * image. This deliberately stays a direct one-shot smoke: artifact-store and
 * Supervisor API coverage belongs to their respective smoke suites.
 */

import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const defaultResultsPath = path.join(repositoryRoot, '.tmp', 'runtime-matrix-functional-results.json')
const candidateDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')
const probeProject = path.join(repositoryRoot, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'SharpLabNext.RuntimeCapabilityProbe.csproj')
const probeOutput = path.join(repositoryRoot, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'bin', 'Release', 'netcoreapp2.0')
const supervisorSettingsPath = path.join(repositoryRoot, 'src', 'Supervisor', 'SharpLabNext.RuntimeSupervisor', 'appsettings.json')
const probeAssembly = 'SharpLabNext.RuntimeCapabilityProbe.dll'
const methodFilter = 'SharpLabNext.RuntimeCapabilityProbe.Program.MultipleSequencePoints'
const maximumResultBytes = 16 * 1024 * 1024
const maximumFramePayloadBytes = 4 * 1024 * 1024
const maximumDockerOutputBytes = 8 * 1024 * 1024
const imageIdPattern = /^sha256:[0-9a-f]{64}$/
const sha256Pattern = /^sha256:[0-9a-f]{64}$/
const profileIdPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/

const frameKinds = Object.freeze({ exit: 7, jitAssembly: 9, jitSummary: 10 })
const supportedFrameKinds = new Set(Object.values(frameKinds))
const implementations = Object.freeze({
  checkedBridge: 'sharplabnext-checked-jit-bridge-v1',
  inspector: 'sharplabnext-jit-inspector-v1',
})

export const runtimeJitSmokeUsage = `Usage:
  node eng/runtime-jit-smoke.mjs --profile ID [--profile ID ...] [--results PATH]`

export class RuntimeJitSmokeError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimeJitSmokeError'
  }
}

function fail(message, options) {
  throw new RuntimeJitSmokeError(message, options)
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function sha256(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}

function readBoundedJson(filename, label) {
  let metadata
  try {
    metadata = fs.lstatSync(filename)
  } catch (error) {
    fail(`${label} '${filename}' could not be inspected: ${error.message}`, { cause: error })
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.size < 1 || metadata.size > maximumResultBytes) {
    fail(`${label} '${filename}' must be a bounded regular non-link file.`)
  }
  const bytes = fs.readFileSync(filename)
  try {
    return { bytes, value: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) }
  } catch (error) {
    fail(`${label} '${filename}' is invalid JSON: ${error.message}`, { cause: error })
  }
}

function positiveSafeInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0) fail(`${label} must be a positive safe integer.`)
  return value
}

function readSandbox(settingsPath = supervisorSettingsPath) {
  const resolvedSettingsPath = path.resolve(settingsPath)
  const settings = readBoundedJson(resolvedSettingsPath, 'Runtime Supervisor settings').value
  const sandbox = settings?.RuntimeSupervisor?.Sandbox
  if (!isObject(sandbox) || typeof sandbox.SeccompProfilePath !== 'string' ||
      !sha256Pattern.test(sandbox.SeccompProfileSha256 ?? '')) {
    fail('Runtime Supervisor settings has an invalid Sandbox definition.')
  }
  const seccompPath = path.resolve(path.dirname(resolvedSettingsPath), sandbox.SeccompProfilePath)
  let metadata
  try {
    metadata = fs.lstatSync(seccompPath)
  } catch (error) {
    fail(`Runtime Supervisor seccomp profile '${seccompPath}' could not be inspected: ${error.message}`, { cause: error })
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.size < 1 || metadata.size > 1024 * 1024) {
    fail('Runtime Supervisor seccomp profile must be a bounded regular non-link file.')
  }
  const seccompBytes = fs.readFileSync(seccompPath)
  const seccompSha256 = sha256(seccompBytes)
  if (seccompSha256 !== sandbox.SeccompProfileSha256) {
    fail(`Runtime Supervisor seccomp digest '${seccompSha256}' disagrees with its configured identity.`)
  }
  let policy
  try {
    policy = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(seccompBytes))
  } catch (error) {
    fail(`Runtime Supervisor seccomp profile is invalid JSON: ${error.message}`, { cause: error })
  }
  if (!isObject(policy) || ![
    'SCMP_ACT_ERRNO',
    'SCMP_ACT_KILL',
    'SCMP_ACT_KILL_PROCESS',
  ].includes(policy.defaultAction) || !Array.isArray(policy.syscalls) || policy.syscalls.length === 0) {
    fail('Runtime Supervisor seccomp profile is not deny-by-default.')
  }
  const soft = positiveSafeInteger(sandbox.OpenFilesSoftLimit, 'Runtime Supervisor open-files soft limit')
  const hard = positiveSafeInteger(sandbox.OpenFilesHardLimit, 'Runtime Supervisor open-files hard limit')
  if (soft > hard) fail('Runtime Supervisor open-files soft limit cannot exceed its hard limit.')
  return {
    seccompPath,
    seccompSha256,
    openFilesSoftLimit: soft,
    openFilesHardLimit: hard,
  }
}

function runProcess(spawn, command, arguments_, options, label, timeoutMilliseconds) {
  const result = spawn(command, arguments_, {
    cwd: options.cwd,
    env: options.env,
    encoding: 'utf8',
    shell: false,
    maxBuffer: maximumDockerOutputBytes,
    timeout: timeoutMilliseconds,
    killSignal: 'SIGKILL',
  })
  if (result?.error !== undefined) {
    if (result.error.code === 'ETIMEDOUT') {
      try { options.onTimeout?.() } catch { /* preserve the timed operation error */ }
      fail(`${label} exceeded its ${timeoutMilliseconds} ms process timeout.`, { cause: result.error })
    }
    fail(`${label} could not start: ${result.error.message}`, { cause: result.error })
  }
  if (result.status !== 0) {
    const stderr = String(result.stderr ?? '').trim()
    fail(`${label} exited ${result.status ?? '<unknown>'}${stderr ? `: ${stderr.slice(0, 1000)}` : ''}`)
  }
  return result
}

function readInt64LittleEndian(bytes, offset) {
  const value = bytes.readBigInt64LE(offset)
  if (value <= 0n || value > BigInt(Number.MAX_SAFE_INTEGER)) fail('Runtime frame sequence is outside the positive safe-integer range.')
  return Number(value)
}

export function parseJitRuntimeFrameLog(stdout) {
  const lines = String(stdout).split(/\r?\n/).filter(line => line.length > 0)
  if (lines.length === 0) fail('JIT runtime emitted no protocol frames.')
  const frames = []
  let expectedSequence = 1
  for (const [index, line] of lines.entries()) {
    if (!/^[A-Za-z0-9+/]+={0,2}$/.test(line) || line.length % 4 !== 0) {
      fail('JIT runtime emitted a non-canonical base64 frame line.')
    }
    const bytes = Buffer.from(line, 'base64')
    if (bytes.toString('base64') !== line || bytes.length < 18 ||
        bytes.toString('ascii', 0, 4) !== 'SLNR' || bytes[4] !== 1) {
      fail('JIT runtime emitted an invalid protocol frame header.')
    }
    const kind = bytes[5]
    if (!supportedFrameKinds.has(kind)) fail(`JIT runtime frame kind ${kind} is not supported.`)
    if (kind === frameKinds.exit && index !== lines.length - 1) fail('JIT runtime emitted a frame after its terminal Exit frame.')
    const sequence = readInt64LittleEndian(bytes, 6)
    const payloadLength = bytes.readInt32LE(14)
    if (sequence !== expectedSequence++ || payloadLength < 0 || payloadLength > maximumFramePayloadBytes || bytes.length !== 18 + payloadLength) {
      fail('JIT runtime emitted an invalid frame sequence or payload length.')
    }
    frames.push({ kind, payload: bytes.subarray(18) })
  }
  return frames
}

function jsonFrame(frames, kind, label) {
  const matches = frames.filter(frame => frame.kind === kind)
  if (matches.length !== 1) fail(`${label} must contain exactly one frame; observed ${matches.length}.`)
  try {
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(matches[0].payload))
  } catch (error) {
    fail(`${label} frame contains invalid JSON: ${error.message}`, { cause: error })
  }
}

function property(value, name, label) {
  if (!isObject(value)) fail(`${label} must be an object.`)
  const alternate = `${name[0].toUpperCase()}${name.slice(1)}`
  const names = [name, alternate].filter(key => Object.prototype.hasOwnProperty.call(value, key))
  if (names.length !== 1) fail(`${label} must contain exactly one '${name}' property.`)
  return value[names[0]]
}

function stringProperty(value, name, label) {
  const result = property(value, name, label)
  if (typeof result !== 'string' || result.length === 0) fail(`${label}.${name} must be a non-empty string.`)
  return result
}

function integerProperty(value, name, label) {
  const result = property(value, name, label)
  if (!Number.isSafeInteger(result)) fail(`${label}.${name} must be a safe integer.`)
  return result
}

function mappingExpectation(mappingKind) {
  if (mappingKind === 'none') return { status: 'not-applicable', sources: null }
  if (mappingKind === 'checked-jit-debug-info') return { status: 'passed', sources: new Set(['checked-jit-debug-info']) }
  if (mappingKind === 'linux-profiler') return { status: 'passed', sources: new Set(['ordinary', 'rich']) }
  fail(`JIT source mapping kind '${mappingKind}' is not supported.`)
}

function validateTextRange(range, label) {
  const startLine = integerProperty(range, 'startLine', label)
  const startCharacter = integerProperty(range, 'startCharacter', label)
  const endLine = integerProperty(range, 'endLine', label)
  const endCharacter = integerProperty(range, 'endCharacter', label)
  if (startLine < 0 || startCharacter < 0 || endLine < startLine ||
      (endLine === startLine && endCharacter <= startCharacter)) {
    fail(`${label} is not an ordered source range.`)
  }
  return `${startLine}:${startCharacter}-${endLine}:${endCharacter}`
}

function validateMapping(method, mappingKind) {
  const expected = mappingExpectation(mappingKind)
  if (expected.sources === null) return { mapping: 'not-applicable', source: null, rangeCount: 0 }
  const source = stringProperty(method, 'mappingSource', 'Prepared JIT method')
  if (!expected.sources.has(source)) fail(`Prepared JIT method mapping source '${source}' does not match '${mappingKind}'.`)
  const linked = property(method, 'linkedRanges', 'Prepared JIT method')
  const evidence = property(method, 'evidenceRanges', 'Prepared JIT method')
  if (!Array.isArray(linked) || !Array.isArray(evidence)) fail('Prepared JIT method mapping ranges must be arrays.')
  const linkedSources = new Set()
  for (const [index, range] of linked.entries()) {
    const sourcePath = stringProperty(range, 'sourceFilePath', `Linked range ${index}`)
    const sourceRange = property(range, 'sourceRange', `Linked range ${index}`)
    linkedSources.add(`${sourcePath}:${validateTextRange(sourceRange, `Linked range ${index}.sourceRange`)}`)
  }
  const evidenceSources = new Set()
  for (const [index, range] of evidence.entries()) {
    const label = `Evidence range ${index}`
    const ilOffset = integerProperty(range, 'ilOffset', label)
    const nativeStart = integerProperty(range, 'nativeStartOffset', label)
    const nativeEnd = integerProperty(range, 'nativeEndOffset', label)
    const document = stringProperty(range, 'document', label)
    const startLine = integerProperty(range, 'startLine', label)
    const startColumn = integerProperty(range, 'startColumn', label)
    const endLine = integerProperty(range, 'endLine', label)
    const endColumn = integerProperty(range, 'endColumn', label)
    if (ilOffset < 0 || nativeStart < 0 || nativeEnd <= nativeStart || startLine < 1 || startColumn < 1 ||
        endLine < startLine || (endLine === startLine && endColumn < startColumn)) {
      fail(`${label} is not a valid PDB-backed native/source range.`)
    }
    evidenceSources.add(`${document}:${startLine}:${startColumn}-${endLine}:${endColumn}`)
  }
  if (linkedSources.size < 2 || evidenceSources.size < 2) {
    fail('Mapped JIT smoke requires at least two distinct linked and PDB evidence source ranges.')
  }
  return { mapping: 'passed', source, rangeCount: evidenceSources.size }
}

function validateJitFrames(frames, profile) {
  const unexpected = frames.find(frame => !supportedFrameKinds.has(frame.kind))
  if (unexpected !== undefined) fail(`JIT emitted unexpected frame kind ${unexpected.kind}.`)
  const assembly = Buffer.concat(frames.filter(frame => frame.kind === frameKinds.jitAssembly).map(frame => frame.payload))
    .toString('utf8')
  if (assembly.trim().length === 0) fail('JIT emitted no native assembly text.')
  const summary = jsonFrame(frames, frameKinds.jitSummary, 'JIT summary')
  const exit = jsonFrame(frames, frameKinds.exit, 'JIT Exit')
  if (stringProperty(exit, 'status', 'JIT Exit') !== 'completed' || integerProperty(exit, 'exitCode', 'JIT Exit') !== 0) {
    fail('JIT Exit frame did not report completed status and exit code zero.')
  }
  const elapsed = property(exit, 'elapsedMilliseconds', 'JIT Exit')
  if (typeof elapsed !== 'number' || !Number.isFinite(elapsed) || elapsed < 0) fail('JIT Exit elapsedMilliseconds is invalid.')
  if (property(summary, 'methodFilter', 'JIT summary') !== methodFilter) fail('JIT summary method filter does not match the probe target.')
  const methods = property(summary, 'methods', 'JIT summary')
  if (!Array.isArray(methods)) fail('JIT summary methods must be an array.')
  const prepared = methods.filter(method => isObject(method) &&
    property(method, 'status', 'JIT method') === 'prepared' &&
    (property(method, 'method', 'JIT method') === methodFilter ||
      property(method, 'displayName', 'JIT method') === methodFilter))
  if (prepared.length === 0) fail('JIT summary contains no prepared target method.')
  const method = prepared.find(candidate =>
    integerProperty(candidate, 'nativeCodeSize', 'Prepared JIT method') > 0 &&
    integerProperty(candidate, 'instructionCount', 'Prepared JIT method') > 0)
  if (method === undefined) fail('Prepared target JIT method has no native code or instructions.')
  const mapping = validateMapping(method, profile.operations.jit.sourceMappingKind)
  return {
    runtimeElapsedMilliseconds: elapsed,
    assemblyBytes: Buffer.byteLength(assembly),
    nativeCodeSize: integerProperty(method, 'nativeCodeSize', 'Prepared JIT method'),
    instructionCount: integerProperty(method, 'instructionCount', 'Prepared JIT method'),
    mapping,
  }
}

function substituteJitCommand(command) {
  if (!isObject(command) || typeof command.executable !== 'string' || command.executable.length === 0 || !Array.isArray(command.argv)) {
    fail('Runtime profile JIT command is invalid.')
  }
  const argv = command.argv.map(token => {
    if (typeof token !== 'string') fail('Runtime profile JIT command has a non-string argument.')
    if (token === '{entryAssembly}') return `/artifact/${probeAssembly}`
    if (token === '{methodFilter}') return methodFilter
    if (token.includes('{entryAssembly}') || token.includes('{methodFilter}') || token.includes('{arguments}')) {
      fail('Runtime profile JIT command has an invalid placeholder token.')
    }
    return token
  })
  return { executable: command.executable, argv }
}

function jitEnvironment(profile) {
  const jit = profile.operations.jit
  const implementation = jit.implementationId
  if (![implementations.checkedBridge, implementations.inspector].includes(implementation)) {
    fail(`Profile '${profile.id}' has unsupported JIT implementation '${implementation ?? '<missing>'}'.`)
  }
  const mappingKind = jit.sourceMappingKind
  const environment = {
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    COMPlus_TieredCompilation: '0',
    COMPlus_JitDisasmDiffable: '0',
    COMPlus_TieredPGO: '0',
  }
  if (implementation === implementations.checkedBridge) {
    environment.DOTNET_EnableDiagnostics = '0'
    environment.COMPlus_EnableDiagnostics = '0'
    return environment
  }
  if (mappingKind !== 'linux-profiler') fail(`JIT inspector profile '${profile.id}' must declare linux-profiler mapping.`)
  Object.assign(environment, {
    SHARPLABNEXT_JIT_RESET_OUTPUT: '1',
    COMPlus_JitDisasm: '*SharpLabNext.RuntimeCapabilityProbe.Program:MultipleSequencePoints*',
    COMPlus_JitDisasmAssemblies: 'SharpLabNext.RuntimeCapabilityProbe',
    COMPlus_JitDisasmWithCodeBytes: '1',
    DOTNET_JitDisasmWithCodeBytes: '1',
    COMPlus_JitStdOutFile: '/tmp/sharplabnext-jit.asm',
    SHARPLABNEXT_JIT_OUTPUT_PATH: '/tmp/sharplabnext-jit.asm',
    DOTNET_EnableDiagnostics: '1',
    COMPlus_EnableDiagnostics: '1',
    DOTNET_EnableDiagnostics_IPC: '0',
    COMPlus_EnableDiagnostics_IPC: '0',
    DOTNET_EnableDiagnostics_Debugger: '0',
    COMPlus_EnableDiagnostics_Debugger: '0',
    DOTNET_EnableDiagnostics_Profiler: '1',
    COMPlus_EnableDiagnostics_Profiler: '1',
    CORECLR_ENABLE_PROFILING: '1',
    CORECLR_PROFILER: '{cf0d821e-299b-5307-a3d8-b283c03916dd}',
    CORECLR_PROFILER_PATH: jit.profilerPath ?? '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
    COMPlus_RichDebugInfo: '1',
    DOTNET_RichDebugInfo: '1',
    SHARPLABNEXT_JIT_MAP_MODULE: probeAssembly,
    SHARPLABNEXT_JIT_MAP_PATH: '/tmp/sharplabnext-jit.map',
    SHARPLABNEXT_JIT_RICH_MAP_PATH: '/tmp/sharplabnext-jit-rich.map',
  })
  return environment
}

function dockerArguments(profile, row, sandbox, outputPath, containerName, containerLabel) {
  if (profile.family !== 'coreclr' || profile.container?.isolationKind !== 'standard' ||
      profile.container?.environmentKind !== 'coreclr' || profile.operations?.jit?.pathStyle !== 'unix') {
    fail(`Profile '${profile.id}' is not supported by the standard CoreCLR JIT smoke path.`)
  }
  if (profile.container.executionUser !== '1654:1654') fail(`Profile '${profile.id}' must use the standard non-root execution user.`)
  const policy = profile.securityPolicies?.find(value => value.id === 'runtime-job-default')
  if (!isObject(policy)) fail(`Profile '${profile.id}' has no runtime-job-default policy.`)
  for (const name of ['memoryBytes', 'nanoCpus', 'pidsLimit', 'maximumDurationSeconds', 'maximumArtifactBytes', 'maximumOutputBytes', 'tmpfsBytes']) {
    positiveSafeInteger(policy[name], `Profile '${profile.id}' policy ${name}`)
  }
  const command = substituteJitCommand(profile.operations.jit.command)
  const environment = jitEnvironment(profile)
  const arguments_ = [
    'run', '--rm', '--name', containerName, '--label', `com.sharplabnext.runtime-jit-smoke=${containerLabel}`,
    '--pull', 'never', '--network', 'none', '--ipc', 'none', '--read-only', '--stop-timeout', '1',
    '--security-opt', 'no-new-privileges=true', '--security-opt', `seccomp=${sandbox.seccompPath}`,
    '--cap-drop', 'ALL', '--user', profile.container.executionUser,
    '--ulimit', `nofile=${sandbox.openFilesSoftLimit}:${sandbox.openFilesHardLimit}`,
    '--pids-limit', String(policy.pidsLimit), '--memory', String(policy.memoryBytes), '--memory-swap', String(policy.memoryBytes),
    '--cpus', String(policy.nanoCpus / 1_000_000_000), '--init',
    '--tmpfs', `/tmp:rw,noexec,nosuid,nodev,size=${policy.tmpfsBytes},uid=1654,gid=1654,mode=0700`,
    '--mount', `type=bind,source=${outputPath},target=/artifact,readonly`,
  ]
  for (const [key, value] of Object.entries(environment)) arguments_.push('--env', `${key}=${value}`)
  // The production entrypoint waits for a Supervisor-written workspace ready file.
  // This standalone smoke intentionally mounts only the immutable probe, so it executes
  // the profile command directly instead of bypassing that wait with an unsafe writable bind.
  arguments_.push('--entrypoint', command.executable, row.image.imageId, ...command.argv)
  return { arguments_, timeoutMilliseconds: policy.maximumDurationSeconds * 1000 + 5000, environment }
}

function ensureProbe(spawn, options, projectPath, outputPath) {
  runProcess(spawn, 'dotnet', ['build', projectPath, '--configuration', 'Release', '--framework', 'netcoreapp2.0', '--no-restore', '--warnaserror'], options, 'Runtime capability probe build', 120_000)
  for (const filename of [probeAssembly, 'SharpLabNext.RuntimeCapabilityProbe.pdb', 'SharpLabNext.RuntimeCapabilityProbe.deps.json', 'SharpLabNext.RuntimeCapabilityProbe.runtimeconfig.json']) {
    const target = path.join(outputPath, filename)
    if (!fs.statSync(target).isFile() || fs.statSync(target).size === 0) fail(`Runtime capability probe output '${filename}' is missing or empty.`)
  }
}

function readInputs(resultsPath, profileIds, profileDirectory) {
  const { value: results } = readBoundedJson(resultsPath, 'Functional result')
  if (results.schemaVersion !== 1 || !Array.isArray(results.rows)) fail('Functional result must use schema version 1 with a rows array.')
  const byId = new Map(results.rows.map(row => [row.profileId, row]))
  const rows = []
  for (const profileId of profileIds) {
    const row = byId.get(profileId)
    if (!isObject(row) || !imageIdPattern.test(row.image?.imageId ?? '') || !sha256Pattern.test(row.profileSha256 ?? '') ||
        typeof row.referenceSetId !== 'string' || row.referenceSetId.length === 0) {
      fail(`Functional result row '${profileId}' has no immutable image, profile SHA, and reference-set binding.`)
    }
    const document = readBoundedJson(path.join(profileDirectory, `${profileId}.json`), `Runtime profile '${profileId}'`)
    const profile = document.value
    if (sha256(document.bytes) !== row.profileSha256 || profile.id !== profileId || profile.image !== row.candidateImage) {
      fail(`Runtime profile '${profileId}' does not match its functional result binding.`)
    }
    if (!Array.isArray(profile.capabilities) || !profile.capabilities.includes('jit-asm') ||
        !Array.isArray(row.expected?.capabilities) || !row.expected.capabilities.includes('jit-asm') ||
        row.expected.sourceMappingKind !== profile.operations?.jit?.sourceMappingKind) {
      fail(`Runtime profile '${profileId}' JIT capability does not match its functional result row.`)
    }
    rows.push({ row, profile })
  }
  return { results, rows }
}

function writeJsonAtomically(filename, value) {
  fs.mkdirSync(path.dirname(filename), { recursive: true })
  const temporary = path.join(path.dirname(filename), `.${path.basename(filename)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`)
  try {
    fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' })
    fs.renameSync(temporary, filename)
  } finally {
    fs.rmSync(temporary, { force: true })
  }
}

function updateVerification(row, profile, result, sandbox, now) {
  const previous = isObject(row.verification) ? row.verification : {}
  const oldSmoke = isObject(previous.smoke) ? previous.smoke : {}
  const smoke = {
    runtimeIdentity: oldSmoke.runtimeIdentity ?? 'unverified',
    compile: oldSmoke.compile ?? 'unverified',
    run: oldSmoke.run ?? 'unverified',
    ilDecompile: oldSmoke.ilDecompile ?? 'unverified',
    jit: 'passed',
    mapping: result.mapping.mapping,
  }
  const pending = Object.entries(smoke).filter(([, status]) => status !== 'passed' && status !== 'not-applicable').map(([name]) => name)
  const evidence = isObject(previous.evidence) ? { ...previous.evidence } : {}
  // Replace, rather than merge, previous JIT evidence. It cannot be evidence for a new
  // profile/image/reference-set binding.
  evidence.jit = {
    observedAt: now.toISOString(), profileSha256: row.profileSha256, imageId: row.image.imageId,
    referenceSetId: row.referenceSetId, probeTargetFramework: 'netcoreapp2.0', probeAssembly,
    methodFilter, implementationId: profile.operations.jit.implementationId,
    sourceMappingKind: profile.operations.jit.sourceMappingKind, mappingSource: result.mapping.source,
    assemblyBytes: result.assemblyBytes, nativeCodeSize: result.nativeCodeSize,
    instructionCount: result.instructionCount, distinctSourceRanges: result.mapping.rangeCount,
    sandbox: { networkMode: 'none', ipcMode: 'none', readOnlyRootFileSystem: true, noNewPrivileges: true,
      capabilitiesDropped: 'all', user: profile.container.executionUser, seccompSha256: sandbox.seccompSha256,
      openFilesSoftLimit: sandbox.openFilesSoftLimit, openFilesHardLimit: sandbox.openFilesHardLimit },
  }
  row.verification = { ...previous, status: pending.length === 0 ? 'smoke-passed' : 'runtime-smoke-passed',
    reason: pending.length === 0 ? null : `${pending.join('-')}-pending`, smoke, evidence }
}

export function runRuntimeJitSmokes(options) {
  const { profileIds, resultsPath = defaultResultsPath, spawn = spawnSync, now = () => new Date(), cwd = repositoryRoot,
    env = process.env, profileDirectory = candidateDirectory, probeProjectPath = probeProject, probeOutputPath = probeOutput,
    sandbox = readSandbox() } = options
  if (!Array.isArray(profileIds) || profileIds.length === 0 || new Set(profileIds).size !== profileIds.length ||
      profileIds.some(id => !profileIdPattern.test(id))) fail('JIT smoke profile IDs must be a non-empty unique list of safe IDs.')
  const absoluteResultsPath = path.resolve(resultsPath)
  const { results, rows } = readInputs(absoluteResultsPath, profileIds, path.resolve(profileDirectory))
  ensureProbe(spawn, { cwd, env }, path.resolve(probeProjectPath), path.resolve(probeOutputPath))
  const completed = []
  for (const { row, profile } of rows) {
    const suffix = crypto.randomBytes(6).toString('hex')
    const name = `sln-jit-${profile.id}-${process.pid}-${suffix}`
    const label = `jit-${profile.id}-${process.pid}-${suffix}`
    const docker = dockerArguments(profile, row, sandbox, path.resolve(probeOutputPath), name, label)
    const result = runProcess(spawn, 'docker', docker.arguments_, { cwd, env, onTimeout: () => spawn('docker', ['rm', '--force', name], {
      cwd, env, encoding: 'utf8', shell: false, maxBuffer: maximumDockerOutputBytes, timeout: 10_000, killSignal: 'SIGKILL',
    }) }, `Runtime JIT smoke '${profile.id}'`, docker.timeoutMilliseconds)
    completed.push({ row, profile, result: validateJitFrames(parseJitRuntimeFrameLog(result.stdout), profile) })
  }
  for (const item of completed) updateVerification(item.row, item.profile, item.result, sandbox, now())
  results.verificationRefreshedAt = now().toISOString()
  writeJsonAtomically(absoluteResultsPath, results)
  return completed.map(item => ({ profileId: item.profile.id, imageId: item.row.image.imageId,
    runtimeElapsedMilliseconds: item.result.runtimeElapsedMilliseconds, mapping: item.result.mapping.mapping }))
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  const profileIds = []
  let resultsPath
  for (let index = 0; index < argv.length; index++) {
    const option = argv[index]
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail(`${option} requires a value.`)
    if (option === '--profile') profileIds.push(value)
    else if (option === '--results' && resultsPath === undefined) resultsPath = value
    else fail(`Unknown or duplicate option '${option}'.`)
  }
  return { profileIds, resultsPath }
}

export function runRuntimeJitSmokeCli(argv, options = {}) {
  const output = options.output ?? console
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) { output.log(runtimeJitSmokeUsage); return 0 }
    const summaries = runRuntimeJitSmokes({ ...options, profileIds: parsed.profileIds, resultsPath: parsed.resultsPath ?? options.resultsPath })
    for (const summary of summaries) output.log(`${summary.profileId}: JIT passed in ${summary.runtimeElapsedMilliseconds.toFixed(1)} ms`)
    return 0
  } catch (error) {
    output.error(`runtime JIT smoke error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimeJitSmokeCli(process.argv.slice(2))
}
