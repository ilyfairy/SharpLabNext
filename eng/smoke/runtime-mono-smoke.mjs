/**
 * Exercise the one supported Mono candidate without routing it through the
 * CoreCLR smoke path.  The Mono JIT helper has its own frame contract and its
 * probe must remain a framework executable.
 */

import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const defaultResultsPath = path.join(repositoryRoot, '.tmp', 'runtime-matrix-functional-results.json')
const candidateDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')
const probeProject = path.join(repositoryRoot, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'SharpLabNext.RuntimeCapabilityProbe.csproj')
const probeOutput = path.join(repositoryRoot, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'bin', 'Release', 'net20')
const supervisorSettingsPath = path.join(repositoryRoot, 'src', 'Supervisor', 'SharpLabNext.RuntimeSupervisor', 'appsettings.json')
const profileId = 'mono-6.12-linux-x64'
const probeAssembly = 'SharpLabNext.RuntimeCapabilityProbe.exe'
const methodFilter = 'SharpLabNext.RuntimeCapabilityProbe.Program.MultipleSequencePoints'
const maxJsonBytes = 16 * 1024 * 1024
const maxFramePayloadBytes = 4 * 1024 * 1024
const maxDockerOutputBytes = 8 * 1024 * 1024
const sha256Pattern = /^sha256:[0-9a-f]{64}$/
const revisionPattern = /^[0-9a-f]{40}$/
const frameKinds = Object.freeze({ stdout: 1, stderr: 2, exception: 6, exit: 7, jitAssembly: 9, jitSummary: 10 })
const supportedFrameKinds = new Set(Object.values(frameKinds))

export const runtimeMonoSmokeUsage = `Usage:
  node eng/smoke/runtime-mono-smoke.mjs --profile mono-6.12-linux-x64 [--exception-profile] [--results PATH]`

export class RuntimeMonoSmokeError extends Error {
  constructor(message, options) { super(message, options); this.name = 'RuntimeMonoSmokeError' }
}

function fail(message, options) { throw new RuntimeMonoSmokeError(message, options); }
function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }
function sha256(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}` }
function positive(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0) fail(`${label} must be a positive safe integer.`)
  return value
}

function readJson(filename, label) {
  let stat
  try { stat = fs.lstatSync(filename) } catch (error) { fail(`${label} '${filename}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maxJsonBytes) fail(`${label} '${filename}' must be a bounded regular non-link file.`)
  const bytes = fs.readFileSync(filename)
  try { return { bytes, value: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) } } catch (error) { fail(`${label} '${filename}' is invalid JSON: ${error.message}`, { cause: error }) }
}

function readSandbox(settingsPath = supervisorSettingsPath) {
  const resolvedSettingsPath = path.resolve(settingsPath)
  const sandbox = readJson(resolvedSettingsPath, 'Runtime Supervisor settings').value?.RuntimeSupervisor?.Sandbox
  if (!isObject(sandbox) || typeof sandbox.SeccompProfilePath !== 'string' || !sha256Pattern.test(sandbox.SeccompProfileSha256 ?? '')) fail('Runtime Supervisor settings has an invalid Sandbox definition.')
  const seccompPath = path.resolve(path.dirname(resolvedSettingsPath), sandbox.SeccompProfilePath)
  let stat
  try { stat = fs.lstatSync(seccompPath) } catch (error) { fail(`Runtime Supervisor seccomp profile '${seccompPath}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > 1024 * 1024) fail('Runtime Supervisor seccomp profile must be a bounded regular non-link file.')
  const bytes = fs.readFileSync(seccompPath)
  const seccompSha256 = sha256(bytes)
  if (seccompSha256 !== sandbox.SeccompProfileSha256) fail(`Runtime Supervisor seccomp digest '${seccompSha256}' disagrees with its configured identity.`)
  let policy
  try { policy = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) } catch (error) { fail(`Runtime Supervisor seccomp profile is invalid JSON: ${error.message}`, { cause: error }) }
  if (!isObject(policy) || !['SCMP_ACT_ERRNO', 'SCMP_ACT_KILL', 'SCMP_ACT_KILL_PROCESS'].includes(policy.defaultAction) || !Array.isArray(policy.syscalls) || policy.syscalls.length === 0) fail('Runtime Supervisor seccomp profile is not deny-by-default.')
  const soft = positive(sandbox.OpenFilesSoftLimit, 'Runtime Supervisor open-files soft limit')
  const hard = positive(sandbox.OpenFilesHardLimit, 'Runtime Supervisor open-files hard limit')
  if (soft > hard) fail('Runtime Supervisor open-files soft limit cannot exceed its hard limit.')
  return { seccompPath, seccompSha256, openFilesSoftLimit: soft, openFilesHardLimit: hard }
}

function runProcess(spawn, command, arguments_, options, label, expectedExitCodes = [0], timeoutMilliseconds = 120_000) {
  const result = spawn(command, arguments_, { cwd: options.cwd, env: options.env, encoding: 'utf8', shell: false, maxBuffer: maxDockerOutputBytes, timeout: timeoutMilliseconds, killSignal: 'SIGKILL' })
  if (result?.error !== undefined) {
    if (result.error.code === 'ETIMEDOUT') {
      try { options.onTimeout?.() } catch { /* timeout is authoritative */ }
      fail(`${label} exceeded its ${timeoutMilliseconds} ms process timeout.`, { cause: result.error })
    }
    fail(`${label} could not start: ${result.error.message}`, { cause: result.error })
  }
  if (!expectedExitCodes.includes(result.status)) {
    const stderr = String(result.stderr ?? '').trim()
    fail(`${label} exited ${result.status ?? '<unknown>'}${stderr ? `: ${stderr.slice(0, 1000)}` : ''}`)
  }
  return result
}

function property(value, name, label) {
  if (!isObject(value)) fail(`${label} must be an object.`)
  const alternate = `${name[0].toUpperCase()}${name.slice(1)}`
  const names = [name, alternate].filter(key => Object.prototype.hasOwnProperty.call(value, key))
  if (names.length !== 1) fail(`${label} must contain exactly one '${name}' property.`)
  return value[names[0]]
}
function textProperty(value, name, label) {
  const result = property(value, name, label)
  if (typeof result !== 'string' || result.length === 0) fail(`${label}.${name} must be a non-empty string.`)
  return result
}
function integerProperty(value, name, label) {
  const result = property(value, name, label)
  if (!Number.isSafeInteger(result)) fail(`${label}.${name} must be a safe integer.`)
  return result
}

export function parseMonoFrameLog(stdout) {
  const lines = String(stdout).split(/\r?\n/).filter(line => line.length > 0)
  if (lines.length === 0) fail('Mono runtime emitted no protocol frames.')
  const frames = []
  let expectedSequence = 1
  for (const [index, line] of lines.entries()) {
    if (!/^[A-Za-z0-9+/]+={0,2}$/.test(line) || line.length % 4 !== 0) fail('Mono runtime emitted a non-canonical base64 frame line.')
    const bytes = Buffer.from(line, 'base64')
    if (bytes.toString('base64') !== line || bytes.length < 18 || bytes.toString('ascii', 0, 4) !== 'SLNR' || bytes[4] !== 1) fail('Mono runtime emitted an invalid protocol frame header.')
    const kind = bytes[5]
    if (!supportedFrameKinds.has(kind)) fail(`Mono runtime frame kind ${kind} is not supported.`)
    if (kind === frameKinds.exit && index !== lines.length - 1) fail('Mono runtime emitted a frame after its terminal Exit frame.')
    const sequence = bytes.readBigInt64LE(6)
    const payloadLength = bytes.readInt32LE(14)
    if (sequence !== BigInt(expectedSequence++) || sequence <= 0n || sequence > BigInt(Number.MAX_SAFE_INTEGER) || payloadLength < 0 || payloadLength > maxFramePayloadBytes || bytes.length !== 18 + payloadLength) fail('Mono runtime emitted an invalid frame sequence or payload length.')
    frames.push({ kind, payload: bytes.subarray(18) })
  }
  return frames
}

function jsonFrame(frames, kind, label) {
  const matches = frames.filter(frame => frame.kind === kind)
  if (matches.length !== 1) fail(`${label} must contain exactly one frame; observed ${matches.length}.`)
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(matches[0].payload)) } catch (error) { fail(`${label} frame contains invalid JSON: ${error.message}`, { cause: error }) }
}
function frameText(frames, kind) { return Buffer.concat(frames.filter(frame => frame.kind === kind).map(frame => frame.payload)).toString('utf8') }

function validateRun(frames, exception) {
  const allowed = exception ? [frameKinds.exception, frameKinds.exit] : [frameKinds.stdout, frameKinds.stderr, frameKinds.exit]
  if (frames.some(frame => !allowed.includes(frame.kind))) fail(`Mono ${exception ? 'exception' : 'Run'} emitted an unexpected frame kind.`)
  const exit = jsonFrame(frames, frameKinds.exit, exception ? 'Mono exception Exit' : 'Mono Run Exit')
  if (exception) {
    const value = jsonFrame(frames, frameKinds.exception, 'Mono exception')
    if (textProperty(value, 'typeName', 'Mono exception') !== 'System.InvalidOperationException' || textProperty(value, 'message', 'Mono exception') !== 'outer capability probe failure' || textProperty(property(value, 'innerException', 'Mono exception'), 'typeName', 'Mono nested exception') !== 'System.ArgumentException' || textProperty(property(value, 'innerException', 'Mono exception'), 'message', 'Mono nested exception') !== 'inner capability probe failure' || !textProperty(value, 'stackTrace', 'Mono exception').includes('ThrowNestedException') || textProperty(exit, 'status', 'Mono exception Exit') !== 'user-exception' || integerProperty(exit, 'exitCode', 'Mono exception Exit') !== 1) fail('Mono nested exception frames do not retain the expected error family.')
  } else {
    const stdout = frameText(frames, frameKinds.stdout)
    const stderr = frameText(frames, frameKinds.stderr)
    for (const marker of ['SLN-CAPABILITY-STDOUT-V1', 'SLN-CAPABILITY-NETWORK-BLOCKED-V1', 'SLN-CAPABILITY-ROOTFS-READONLY-V1']) if (!stdout.includes(marker)) fail(`Mono Run stdout is missing '${marker}'.`)
    if (!stderr.includes('SLN-CAPABILITY-STDERR-V1') || textProperty(exit, 'status', 'Mono Run Exit') !== 'completed' || integerProperty(exit, 'exitCode', 'Mono Run Exit') !== 0) fail('Mono Run did not report normal output and completed exit state.')
  }
  const elapsed = property(exit, 'elapsedMilliseconds', exception ? 'Mono exception Exit' : 'Mono Run Exit')
  if (typeof elapsed !== 'number' || !Number.isFinite(elapsed) || elapsed < 0) fail('Mono Run elapsedMilliseconds is invalid.')
  return exception
    ? {
        runtimeElapsedMilliseconds: elapsed,
        outerType: textProperty(jsonFrame(frames, frameKinds.exception, 'Mono exception'), 'typeName', 'Mono exception'),
        innerType: textProperty(property(jsonFrame(frames, frameKinds.exception, 'Mono exception'), 'innerException', 'Mono exception'), 'typeName', 'Mono nested exception'),
      }
    : { runtimeElapsedMilliseconds: elapsed }
}

function validateJit(frames) {
  if (frames.some(frame => ![frameKinds.jitAssembly, frameKinds.jitSummary, frameKinds.exit].includes(frame.kind))) fail('Mono JIT emitted an unexpected frame kind.')
  const assembly = frameText(frames, frameKinds.jitAssembly)
  if (assembly.trim().length === 0) fail('Mono JIT emitted no native assembly text.')
  const summary = jsonFrame(frames, frameKinds.jitSummary, 'Mono JIT summary')
  const exit = jsonFrame(frames, frameKinds.exit, 'Mono JIT Exit')
  if (property(summary, 'methodFilter', 'Mono JIT summary') !== methodFilter) fail('Mono JIT summary method filter does not match the probe target.')
  if (textProperty(exit, 'status', 'Mono JIT Exit') !== 'completed' || integerProperty(exit, 'exitCode', 'Mono JIT Exit') !== 0) fail('Mono JIT Exit frame did not report completed status and exit code zero.')
  const methods = property(summary, 'methods', 'Mono JIT summary')
  if (!Array.isArray(methods)) fail('Mono JIT summary methods must be an array.')
  const prepared = methods.filter(method => isObject(method) && textProperty(method, 'status', 'Mono JIT method') === 'prepared' && textProperty(method, 'displayName', 'Mono JIT method') === methodFilter)
  const selected = prepared.find(method => integerProperty(method, 'nativeCodeSize', 'Prepared Mono JIT method') > 0 && integerProperty(method, 'instructionCount', 'Prepared Mono JIT method') > 0)
  if (selected === undefined) fail('Mono JIT summary contains no prepared target method with native code and instructions.')
  const elapsed = property(exit, 'elapsedMilliseconds', 'Mono JIT Exit')
  if (typeof elapsed !== 'number' || !Number.isFinite(elapsed) || elapsed < 0) fail('Mono JIT elapsedMilliseconds is invalid.')
  return { runtimeElapsedMilliseconds: elapsed, assemblyBytes: Buffer.byteLength(assembly), nativeCodeSize: integerProperty(selected, 'nativeCodeSize', 'Prepared Mono JIT method'), instructionCount: integerProperty(selected, 'instructionCount', 'Prepared Mono JIT method') }
}

function substitute(command, arguments_) {
  if (!isObject(command) || typeof command.executable !== 'string' || command.executable.length === 0 || !Array.isArray(command.argv)) fail('Mono profile command is invalid.')
  const argv = []
  for (const token of command.argv) {
    if (typeof token !== 'string') fail('Mono profile command has a non-string argument.')
    if (token === '{entryAssembly}') argv.push(`/artifact/${probeAssembly}`)
    else if (token === '{methodFilter}') argv.push(methodFilter)
    else if (token === '{arguments}') argv.push(...arguments_)
    else if (token.includes('{')) fail(`Mono profile command has an unsupported token '${token}'.`)
    else argv.push(token)
  }
  return { executable: command.executable, argv }
}

function readInputs(resultsPath, profileDirectory) {
  const { value: results } = readJson(resultsPath, 'Functional result')
  if (results.schemaVersion !== 1 || !Array.isArray(results.rows)) fail('Functional result must use schema version 1 with a rows array.')
  const matches = results.rows.filter(row => row?.profileId === profileId)
  if (matches.length !== 1) fail(`Functional result must bind '${profileId}' exactly once.`)
  const row = matches[0]
  if (!sha256Pattern.test(row.profileSha256 ?? '') || !sha256Pattern.test(row.image?.imageId ?? '') || row.referenceSetId !== 'netfx48-managed-ref') fail(`Functional result row '${profileId}' has an invalid Mono identity binding.`)
  const document = readJson(path.join(profileDirectory, `${profileId}.json`), `Mono profile '${profileId}'`)
  const profile = document.value
  if (sha256(document.bytes) !== row.profileSha256 || profile.id !== profileId || profile.image !== row.candidateImage || profile.family !== 'mono' || profile.runtimeVersion !== '6.12.0.182' || profile.container?.environmentKind !== 'mono' || profile.container?.executionUser !== '1654:1654' || !profile.capabilities?.includes('run') || !profile.capabilities?.includes('jit-asm') || row.expected?.runImplementationId !== 'sharplabnext-target-runtime-runner-v1' || row.expected?.jitImplementationId !== 'sharplabnext-mono-jit-inspector-v1' || row.expected?.sourceMappingKind !== 'none' || profile.operations?.jit?.sourceMappingKind !== 'none') fail(`Mono profile '${profileId}' does not match its expected functional identity.`)
  const labels = row.image.labels
  if (!isObject(labels) || labels['com.sharplabnext.runtime-profile'] !== profileId || labels['io.sharplabnext.runtime.environment'] !== 'mono' || labels['io.sharplabnext.runtime.version'] !== profile.runtimeVersion || labels['io.sharplabnext.source.revision'] !== labels['org.opencontainers.image.revision'] || !revisionPattern.test(labels['io.sharplabnext.source.revision'] ?? '')) fail(`Functional result row '${profileId}' image labels do not prove the Mono source revision identity.`)
  return { results, row, profile }
}

function dockerArguments(profile, row, sandbox, outputPath, name, operation, userArguments) {
  const policy = profile.securityPolicies?.find(value => value.id === 'runtime-job-default')
  if (!isObject(policy)) fail(`Mono profile '${profileId}' has no runtime-job-default policy.`)
  for (const field of ['memoryBytes', 'nanoCpus', 'pidsLimit', 'maximumDurationSeconds', 'maximumOutputBytes', 'tmpfsBytes']) positive(policy[field], `Mono policy ${field}`)
  const command = substitute(profile.operations[operation].command, userArguments)
  return { arguments_: ['run', '--rm', '--name', name, '--pull', 'never', '--network', 'none', '--ipc', 'none', '--read-only', '--stop-timeout', '1', '--security-opt', 'no-new-privileges=true', '--security-opt', `seccomp=${sandbox.seccompPath}`, '--cap-drop', 'ALL', '--user', profile.container.executionUser, '--ulimit', `nofile=${sandbox.openFilesSoftLimit}:${sandbox.openFilesHardLimit}`, '--pids-limit', String(policy.pidsLimit), '--memory', String(policy.memoryBytes), '--memory-swap', String(policy.memoryBytes), '--cpus', String(policy.nanoCpus / 1_000_000_000), '--init', '--tmpfs', `/tmp:rw,noexec,nosuid,nodev,size=${policy.tmpfsBytes},uid=1654,gid=1654,mode=0700`, '--mount', `type=bind,source=${outputPath},target=/artifact,readonly`, '--env', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', '--entrypoint', command.executable, row.image.imageId, ...command.argv], timeoutMilliseconds: policy.maximumDurationSeconds * 1000 + 5000 }
}

function ensureProbe(spawn, options, projectPath, outputPath) {
  runProcess(spawn, 'dotnet', ['build', projectPath, '--configuration', 'Release', '--framework', 'net20', '--no-restore', '--warnaserror'], options, 'Mono capability probe build')
  for (const file of [probeAssembly, 'SharpLabNext.RuntimeCapabilityProbe.exe.config', 'SharpLabNext.RuntimeCapabilityProbe.pdb']) {
    const target = path.join(outputPath, file)
    if (!fs.statSync(target).isFile() || fs.statSync(target).size === 0) fail(`Mono capability probe output '${file}' is missing or empty.`)
  }
}

function writeJsonAtomically(filename, value) {
  fs.mkdirSync(path.dirname(filename), { recursive: true })
  const temporary = path.join(path.dirname(filename), `.${path.basename(filename)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`)
  try { fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' }); fs.renameSync(temporary, filename) } finally { fs.rmSync(temporary, { force: true }) }
}

function update(row, profile, run, exception, jit, sandbox, now) {
  const previous = isObject(row.verification) ? row.verification : {}
  const smoke = { ...(isObject(previous.smoke) ? previous.smoke : {}), runtimeIdentity: 'passed', run: 'passed', jit: 'passed', mapping: 'not-applicable' }
  const pending = Object.entries(smoke).filter(([, status]) => status !== 'passed' && status !== 'not-applicable').map(([name]) => name)
  row.verification = { ...previous, status: pending.length === 0 ? 'smoke-passed' : 'runtime-smoke-passed', reason: pending.length === 0 ? null : `${pending.join('-')}-pending`, smoke, evidence: { ...(isObject(previous.evidence) ? previous.evidence : {}), mono: { observedAt: now.toISOString(), profileSha256: row.profileSha256, imageId: row.image.imageId, referenceSetId: row.referenceSetId, sourceRevision: row.image.labels['io.sharplabnext.source.revision'], probeTargetFramework: 'net20', probeAssembly, methodFilter, run: { ...run, exception: exception ?? null }, jit: { ...jit, implementationId: profile.operations.jit.implementationId, sourceMappingKind: 'none' }, sandbox: { networkMode: 'none', ipcMode: 'none', readOnlyRootFileSystem: true, noNewPrivileges: true, capabilitiesDropped: 'all', user: profile.container.executionUser, seccompSha256: sandbox.seccompSha256, openFilesSoftLimit: sandbox.openFilesSoftLimit, openFilesHardLimit: sandbox.openFilesHardLimit } } } }
}

export function runMonoSmokes(options) {
  const { exceptionProfile = false, resultsPath = defaultResultsPath, spawn = spawnSync, now = () => new Date(), cwd = repositoryRoot, env = process.env, profileDirectory = candidateDirectory, probeProjectPath = probeProject, probeOutputPath = probeOutput, sandbox = readSandbox() } = options
  const absoluteResultsPath = path.resolve(resultsPath)
  const { results, row, profile } = readInputs(absoluteResultsPath, path.resolve(profileDirectory))
  ensureProbe(spawn, { cwd, env }, path.resolve(probeProjectPath), path.resolve(probeOutputPath))
  const invoke = (operation, userArguments, expectedExitCodes = [0]) => {
    const name = `sln-mono-${operation}-${process.pid}-${crypto.randomBytes(6).toString('hex')}`
    const docker = dockerArguments(profile, row, sandbox, path.resolve(probeOutputPath), name, operation, userArguments)
    return runProcess(spawn, 'docker', docker.arguments_, { cwd, env, onTimeout: () => spawn('docker', ['rm', '--force', name], { cwd, env, encoding: 'utf8', shell: false, maxBuffer: maxDockerOutputBytes, timeout: 10_000, killSignal: 'SIGKILL' }) }, `Mono ${operation} smoke '${profileId}'`, expectedExitCodes, docker.timeoutMilliseconds)
  }
  const run = validateRun(parseMonoFrameLog(invoke('run', ['success-security']).stdout), false)
  const exception = exceptionProfile ? validateRun(parseMonoFrameLog(invoke('run', ['user-exception'], [1]).stdout), true) : undefined
  const jit = validateJit(parseMonoFrameLog(invoke('jit', []).stdout))
  update(row, profile, run, exception, jit, sandbox, now())
  results.verificationRefreshedAt = now().toISOString()
  writeJsonAtomically(absoluteResultsPath, results)
  return { profileId, imageId: row.image.imageId, runtimeElapsedMilliseconds: run.runtimeElapsedMilliseconds, exceptionValidated: exception !== undefined, jitElapsedMilliseconds: jit.runtimeElapsedMilliseconds }
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  let selected = false; let exceptionProfile = false; let resultsPath
  for (let index = 0; index < argv.length; index++) {
    const option = argv[index]
    if (option === '--exception-profile') {
      const value = argv[index + 1]
      if (value === undefined || value.startsWith('--')) exceptionProfile = true
      else if (value === profileId) { exceptionProfile = true; index++ }
      else fail(`--exception-profile supports only '${profileId}'.`)
      continue
    }
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail(`${option} requires a value.`)
    if (option === '--profile' && !selected && value === profileId) selected = true
    else if (option === '--results' && resultsPath === undefined) resultsPath = value
    else fail(`Unknown, duplicate, or unsupported option '${option}'.`)
  }
  if (!selected) fail(`--profile ${profileId} is required.`)
  return { exceptionProfile, resultsPath }
}

export function runRuntimeMonoSmokeCli(argv, options = {}) {
  const output = options.output ?? console
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) { output.log(runtimeMonoSmokeUsage); return 0 }
    const summary = runMonoSmokes({ ...options, exceptionProfile: parsed.exceptionProfile, resultsPath: parsed.resultsPath ?? options.resultsPath })
    output.log(`${summary.profileId}: Run and JIT passed${summary.exceptionValidated ? '; nested exception passed' : ''}`)
    return 0
  } catch (error) { output.error(`runtime Mono smoke error: ${error.message}`); return 1 }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = runRuntimeMonoSmokeCli(process.argv.slice(2))
