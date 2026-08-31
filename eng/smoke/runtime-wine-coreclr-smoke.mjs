/**
 * Current-image compatibility smoke for Windows CoreCLR hosted by Wine.
 * This intentionally stays below the Supervisor preflight: it proves the exact
 * product command and bounded Docker shape, but never creates promotion evidence.
 */
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const defaultResults = path.join(root, '.tmp', 'runtime-matrix-functional-results.json')
const candidateDirectory = path.join(root, 'profiles', 'runtimes', 'candidates')
const defaultRuntimeMatrix = path.join(root, 'profiles', 'runtime-matrix.json')
const probeProject = path.join(root, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'SharpLabNext.RuntimeCapabilityProbe.csproj')
const probeOutput = path.join(root, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'bin', 'Release', 'netcoreapp2.0')
const supervisorSettings = path.join(root, 'src', 'Supervisor', 'SharpLabNext.RuntimeSupervisor', 'appsettings.json')
const probeAssembly = 'SharpLabNext.RuntimeCapabilityProbe.dll'
const maxBytes = 16 * 1024 * 1024
const maxFrameBytes = 4 * 1024 * 1024
const imageId = /^sha256:[0-9a-f]{64}$/
const sha256 = /^sha256:[0-9a-f]{64}$/
const revision = /^[0-9a-f]{40}$/
const profileId = /^wine-dotnet-(?:5|6|7|8|9|10|11-preview)-linux-x64$/
const methodFilter = 'SharpLabNext.RuntimeCapabilityProbe.Program.WindowsAbi'
const kinds = Object.freeze({ stdout: 1, stderr: 2, exception: 6, exit: 7, jitAssembly: 9, jitSummary: 10 })
const supportedKinds = new Set(Object.values(kinds))

export const runtimeWineCoreClrSmokeUsage = `Usage:\n  node eng/smoke/runtime-wine-coreclr-smoke.mjs --profile ID [--profile ID ...] [--results PATH]`
export class RuntimeWineCoreClrSmokeError extends Error { constructor(message, options) { super(message, options); this.name = 'RuntimeWineCoreClrSmokeError' } }
function fail(message, options) { throw new RuntimeWineCoreClrSmokeError(message, options); }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }
function digest(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}` }
function positive(value, label) { if (!Number.isSafeInteger(value) || value <= 0) fail(`${label} must be a positive safe integer.`); return value }

function readJson(filename, label) {
  let stat
  try { stat = fs.lstatSync(filename) } catch (error) { fail(`${label} '${filename}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maxBytes) fail(`${label} '${filename}' must be a bounded regular non-link file.`)
  const bytes = fs.readFileSync(filename)
  try { return { bytes, value: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) } } catch (error) { fail(`${label} '${filename}' is invalid JSON: ${error.message}`, { cause: error }) }
}
function readSandbox(settingsPath = supervisorSettings) {
  const resolved = path.resolve(settingsPath)
  const settings = readJson(resolved, 'Runtime Supervisor settings').value
  const sandbox = settings?.RuntimeSupervisor?.Sandbox
  if (!object(sandbox) || typeof sandbox.SeccompProfilePath !== 'string' ||
      !sha256.test(sandbox.SeccompProfileSha256 ?? '')) {
    fail('Runtime Supervisor settings has an invalid Sandbox definition.')
  }
  const seccompPath = path.resolve(path.dirname(resolved), sandbox.SeccompProfilePath)
  let stat
  try { stat = fs.lstatSync(seccompPath) } catch (error) { fail(`Runtime Supervisor seccomp profile '${seccompPath}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > 1024 * 1024) fail('Runtime Supervisor seccomp profile must be a bounded regular non-link file.')
  const bytes = fs.readFileSync(seccompPath)
  const seccompSha256 = digest(bytes)
  if (seccompSha256 !== sandbox.SeccompProfileSha256) fail(`Runtime Supervisor seccomp digest '${seccompSha256}' disagrees with its configured identity.`)
  let policy
  try { policy = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) } catch (error) { fail(`Runtime Supervisor seccomp profile is invalid JSON: ${error.message}`, { cause: error }) }
  if (!object(policy) || !['SCMP_ACT_ERRNO', 'SCMP_ACT_KILL', 'SCMP_ACT_KILL_PROCESS'].includes(policy.defaultAction) || !Array.isArray(policy.syscalls) || policy.syscalls.length === 0) fail('Runtime Supervisor seccomp profile is not deny-by-default.')
  const soft = positive(sandbox.OpenFilesSoftLimit, 'Runtime Supervisor open-files soft limit')
  const hard = positive(sandbox.OpenFilesHardLimit, 'Runtime Supervisor open-files hard limit')
  if (soft > hard) fail('Runtime Supervisor open-files soft limit cannot exceed its hard limit.')
  return { seccompPath, seccompSha256, openFilesSoftLimit: soft, openFilesHardLimit: hard }
}
function property(value, name, label) {
  if (!object(value)) fail(`${label} must be an object.`)
  const alternate = `${name[0].toUpperCase()}${name.slice(1)}`
  const names = [name, alternate].filter(key => Object.hasOwn(value, key))
  if (names.length !== 1) fail(`${label} must contain exactly one '${name}' property.`)
  return value[names[0]]
}
function text(value, name, label) { const result = property(value, name, label); if (typeof result !== 'string' || result.length === 0) fail(`${label}.${name} must be a non-empty string.`); return result }
function integer(value, name, label) { const result = property(value, name, label); if (!Number.isSafeInteger(result)) fail(`${label}.${name} must be a safe integer.`); return result }
function runProcess(spawn, command, argv, options, label, codes = [0], timeout = 120000) {
  const result = spawn(command, argv, { cwd: options.cwd, env: options.env, encoding: 'utf8', shell: false, maxBuffer: maxBytes, timeout, killSignal: 'SIGKILL' })
  if (result?.error) { if (result.error.code === 'ETIMEDOUT') { try { options.onTimeout?.() } catch {} ; fail(`${label} exceeded its ${timeout} ms process timeout.`, { cause: result.error }) }; fail(`${label} could not start: ${result.error.message}`, { cause: result.error }) }
  if (!codes.includes(result.status)) fail(`${label} exited ${result.status ?? '<unknown>'}${String(result.stderr ?? '').trim() ? `: ${String(result.stderr).trim().slice(0, 1000)}` : ''}`)
  return result
}

export function parseWineCoreClrFrameLog(stdout) {
  const lines = String(stdout).split(/\r?\n/).filter(Boolean)
  if (lines.length === 0) fail('Wine CoreCLR emitted no protocol frames.')
  let sequence = 1; const frames = []
  for (const [index, line] of lines.entries()) {
    if (!/^[A-Za-z0-9+/]+={0,2}$/.test(line) || line.length % 4 !== 0) fail('Wine CoreCLR emitted a non-canonical base64 frame line.')
    const bytes = Buffer.from(line, 'base64')
    if (bytes.toString('base64') !== line || bytes.length < 18 || bytes.toString('ascii', 0, 4) !== 'SLNR' || bytes[4] !== 1) fail('Wine CoreCLR emitted an invalid protocol frame header.')
    const kind = bytes[5], payloadLength = bytes.readInt32LE(14), actual = bytes.readBigInt64LE(6)
    if (!supportedKinds.has(kind)) fail(`Wine CoreCLR runtime frame kind ${kind} is not supported.`)
    if (kind === kinds.exit && index !== lines.length - 1) fail('Wine CoreCLR emitted a frame after its terminal Exit frame.')
    if (actual !== BigInt(sequence++) || actual <= 0n || actual > BigInt(Number.MAX_SAFE_INTEGER) || payloadLength < 0 || payloadLength > maxFrameBytes || bytes.length !== 18 + payloadLength) fail('Wine CoreCLR emitted an invalid frame sequence or payload length.')
    frames.push({ kind, payload: bytes.subarray(18) })
  }
  return frames
}
function jsonFrame(frames, kind, label) { const values = frames.filter(frame => frame.kind === kind); if (values.length !== 1) fail(`${label} must contain exactly one frame; observed ${values.length}.`); try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(values[0].payload)) } catch (error) { fail(`${label} contains invalid JSON: ${error.message}`, { cause: error }) } }
function frameText(frames, kind) { return Buffer.concat(frames.filter(frame => frame.kind === kind).map(frame => frame.payload)).toString('utf8') }
function elapsed(exit, label) { const value = property(exit, 'elapsedMilliseconds', label); if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) fail(`${label}.elapsedMilliseconds is invalid.`); return value }
function validateRun(frames) {
  if (frames.some(frame => ![kinds.stdout, kinds.stderr, kinds.exit].includes(frame.kind))) fail('Wine CoreCLR Run emitted an unexpected frame kind.')
  const stdout = frameText(frames, kinds.stdout), stderr = frameText(frames, kinds.stderr), exit = jsonFrame(frames, kinds.exit, 'Wine CoreCLR Run Exit')
  for (const marker of ['SLN-CAPABILITY-STDOUT-V1', 'SLN-CAPABILITY-NETWORK-BLOCKED-V1', 'SLN-CAPABILITY-ROOTFS-READONLY-V1']) if (!stdout.includes(marker)) fail(`Wine CoreCLR Run stdout is missing '${marker}'.`)
  if (!stderr.includes('SLN-CAPABILITY-STDERR-V1') || text(exit, 'status', 'Wine CoreCLR Run Exit') !== 'completed' || integer(exit, 'exitCode', 'Wine CoreCLR Run Exit') !== 0) fail('Wine CoreCLR Run did not report normal output and completed exit state.')
  return { runtimeElapsedMilliseconds: elapsed(exit, 'Wine CoreCLR Run Exit') }
}
function selectedAssemblySection(assembly, method) {
  const lines = assembly.replace(/\r\n?/g, '\n').split('\n')
  const sections = []
  for (let index = 0; index < lines.length; index++) {
    const match = /^(?:;\s*)?Assembly listing for method\s+(.+)$/.exec(lines[index])
    if (!match) continue
    const signatureStart = match[1].indexOf('(')
    const name = (signatureStart < 0 ? match[1] : match[1].slice(0, signatureStart)).replaceAll(':', '.')
    sections.push({ start: index, name })
  }
  const matches = sections.map((section, index) => ({ ...section, end: sections[index + 1]?.start ?? lines.length })).filter(section => section.name.toLowerCase() === method.toLowerCase())
  if (matches.length !== 1) fail(`Wine CoreCLR JIT assembly must contain exactly one '${method}' section; observed ${matches.length}.`)
  return lines.slice(matches[0].start, matches[0].end).join('\n')
}
function validateJit(frames) {
  if (frames.some(frame => ![kinds.jitAssembly, kinds.jitSummary, kinds.exit].includes(frame.kind))) fail('Wine CoreCLR JIT emitted an unexpected frame kind.')
  const assembly = frameText(frames, kinds.jitAssembly), summary = jsonFrame(frames, kinds.jitSummary, 'Wine CoreCLR JIT summary'), exit = jsonFrame(frames, kinds.exit, 'Wine CoreCLR JIT Exit')
  if (assembly.trim().length === 0) fail('Wine CoreCLR JIT emitted no native assembly text.')
  if (property(summary, 'methodFilter', 'Wine CoreCLR JIT summary') !== methodFilter || text(exit, 'status', 'Wine CoreCLR JIT Exit') !== 'completed' || integer(exit, 'exitCode', 'Wine CoreCLR JIT Exit') !== 0) fail('Wine CoreCLR JIT did not report the required method and completion state.')
  const methods = property(summary, 'methods', 'Wine CoreCLR JIT summary'); if (!Array.isArray(methods)) fail('Wine CoreCLR JIT summary methods must be an array.')
  const selected = methods.find(method => object(method) && text(method, 'status', 'Wine CoreCLR JIT method') === 'prepared' && (property(method, 'method', 'Wine CoreCLR JIT method') === methodFilter || property(method, 'displayName', 'Wine CoreCLR JIT method') === methodFilter) && integer(method, 'nativeCodeSize', 'Wine CoreCLR JIT method') > 0 && integer(method, 'instructionCount', 'Wine CoreCLR JIT method') > 0)
  if (!selected) fail('Wine CoreCLR JIT summary has no prepared WindowsAbi method with native code and instructions.')
  const methodAssembly = selectedAssemblySection(assembly, methodFilter)
  if (!/\b(?:rcx|ecx)\b/i.test(methodAssembly) || !/\b(?:rdx|edx)\b/i.test(methodAssembly) || !/\b(?:rax|eax)\b/i.test(methodAssembly)) fail('Wine CoreCLR JIT assembly does not prove the Windows x64 ABI registers rcx, rdx, and rax/eax in the selected WindowsAbi method.')
  return { runtimeElapsedMilliseconds: elapsed(exit, 'Wine CoreCLR JIT Exit'), assemblyBytes: Buffer.byteLength(assembly), nativeCodeSize: integer(selected, 'nativeCodeSize', 'Wine CoreCLR JIT method'), instructionCount: integer(selected, 'instructionCount', 'Wine CoreCLR JIT method'), abi: ['rcx', 'rdx', 'rax/eax'] }
}
function substitute(command, operation) {
  if (!object(command) || command.executable !== '/usr/lib/wine/wine64' || !Array.isArray(command.argv)) fail('Wine CoreCLR profile command must use the explicit Wine x64 host.')
  const argv = []
  for (const token of command.argv) { if (typeof token !== 'string') fail('Wine CoreCLR profile command has a non-string argument.'); if (token === '{entryAssembly}') argv.push(`Z:\\workspace\\${probeAssembly}`); else if (token === '{methodFilter}') argv.push(methodFilter); else if (token === '{arguments}') { if (operation === 'run') argv.push('success-security') } else if (token.includes('{')) fail(`Wine CoreCLR profile command has unsupported token '${token}'.`); else argv.push(token) }
  return argv
}
function validateProfile(row, profile, bytes, target) {
  if (!sha256.test(row.profileSha256 ?? '') || !imageId.test(row.image?.imageId ?? '') || typeof row.candidateImage !== 'string' || row.candidateImage.length === 0 || typeof row.referenceSetId !== 'string' || row.referenceSetId.length === 0 || digest(bytes) !== row.profileSha256 || profile.id !== row.profileId || profile.image !== row.candidateImage) fail(`Wine CoreCLR result row '${row.profileId}' has no current immutable profile/image/reference binding.`)
  if (!profileId.test(profile.id) || profile.family !== 'coreclr-wine' || profile.container?.isolationKind !== 'wine' || profile.container?.environmentKind !== 'wine' || profile.container?.executionUser !== '1654:1654' || profile.container?.winePrefixPath !== '/opt/wine-dotnet' || profile.layout?.runnerKind !== 'wine-coreclr' || profile.layout?.winePrefixPath !== '/opt/wine-dotnet' || profile.layout?.wineHostPath !== '/usr/lib/wine/wine64' || profile.layout?.dotNetHostPath !== '/opt/wine-dotnet/drive_c/dotnet/dotnet.exe') fail(`Wine CoreCLR profile '${profile.id}' does not declare the required Wine sandbox and prefix contract.`)
  const labels = row.image.labels
  const context = labels?.['io.sharplabnext.source.context']
  if (!object(target) || target.id !== row.matrixTargetId || target.version !== profile.runtimeVersion ||
      target.referenceSetId !== row.referenceSetId || target.runtimeCommit !== profile.runtimeCommit ||
      target.jitCommit !== profile.jitCommit || !/^[0-9a-f]{128}$/.test(target.windows?.sha512 ?? '')) {
    fail(`Wine CoreCLR profile '${profile.id}' does not match its canonical matrix target.`)
  }
  if (!object(labels) || labels['com.sharplabnext.runtime-candidate'] !== 'true' || labels['com.sharplabnext.runtime-profile'] !== profile.id || labels['io.sharplabnext.runtime.environment'] !== 'wine-coreclr' || labels['io.sharplabnext.runtime.version'] !== profile.runtimeVersion || labels['io.sharplabnext.runtime.commit'] !== profile.runtimeCommit || labels['io.sharplabnext.jit.commit'] !== profile.jitCommit || labels['io.sharplabnext.runtime.payload-sha512'] !== target.windows.sha512 || !revision.test(labels['io.sharplabnext.source.revision'] ?? '') || labels['io.sharplabnext.source.revision'] !== labels['org.opencontainers.image.revision'] || !['committed', 'working-tree-content'].includes(context) || labels['com.sharplabnext.runtime-candidate.promotion-eligible'] !== (context === 'committed' ? 'true' : 'false')) fail(`Wine CoreCLR result row '${profile.id}' image labels do not prove the current source and runtime identity.`)
  const hasJit = profile.capabilities?.includes('jit-asm')
  if (!profile.capabilities?.includes('run') || row.expected?.runImplementationId !== 'sharplabnext-legacy-jit-inspector-v1' || row.expected?.sourceMappingKind !== 'none' || (hasJit ? (row.expected?.jitImplementationId !== 'sharplabnext-legacy-jit-inspector-v1' || profile.operations?.jit?.sourceMappingKind !== 'none') : (profile.runtimeVersion.split('.')[0] !== '5' && profile.runtimeVersion.split('.')[0] !== '6'))) fail(`Wine CoreCLR profile '${profile.id}' does not match the Run-only/JIT capability boundary.`)
  for (const operation of hasJit ? ['run', 'jit'] : ['run']) { const args = profile.operations?.[operation]?.command?.argv; if (!Array.isArray(args) || !args.includes('--fx-version') || args[args.indexOf('--fx-version') + 1] !== profile.runtimeVersion || !args.includes('--runtime-version') || args[args.indexOf('--runtime-version') + 1] !== profile.runtimeVersion || profile.operations[operation].implementationId !== 'sharplabnext-legacy-jit-inspector-v1' || profile.operations[operation].pathStyle !== 'wine-z') fail(`Wine CoreCLR ${operation} command '${profile.id}' does not pin its exact runtime version.`) }
  return hasJit
}
function wineEnvironment(operation, policy) {
  const environment = {
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_EnableDiagnostics: '0',
    COMPlus_EnableDiagnostics: '0',
    WINEPREFIX: '/opt/wine-dotnet',
    WINEARCH: 'win64',
    WINEDEBUG: '-all',
    WINESERVER: '/usr/lib/wine/wineserver64',
    SHARPLABNEXT_PREPARE_WINE_XDG_RUNTIME_DIR: '1',
    SHARPLABNEXT_WINE_CLEANUP: '1',
    SHARPLABNEXT_MAX_OUTPUT_BYTES: String(policy.maximumOutputBytes),
  }
  if (operation === 'run') {
    environment.SHARPLABNEXT_CAPTURE_DIRECTORY = 'Z:\\tmp'
    environment.SHARPLABNEXT_INSTRUMENTATION = 'none'
    return environment
  }
  Object.assign(environment, {
    SHARPLABNEXT_JIT_RESET_OUTPUT: '1',
    COMPlus_JitDisasm: '*SharpLabNext.RuntimeCapabilityProbe.Program:WindowsAbi*',
    COMPlus_JitDisasmAssemblies: 'SharpLabNext.RuntimeCapabilityProbe',
    COMPlus_JitDisasmWithCodeBytes: '1',
    DOTNET_JitDisasmWithCodeBytes: '1',
    COMPlus_JitStdOutFile: 'Z:\\tmp\\sharplabnext-jit.asm',
    SHARPLABNEXT_JIT_OUTPUT_PATH: 'Z:\\tmp\\sharplabnext-jit.asm',
    COMPlus_TieredCompilation: '0',
    COMPlus_JitDisasmDiffable: '0',
    COMPlus_TieredPGO: '0',
  })
  return environment
}
function dockerArguments(profile, row, sandbox, workspace, name, operation) {
  const policy = profile.securityPolicies?.find(value => value.id === 'runtime-job-default'); if (!object(policy)) fail(`Wine CoreCLR profile '${profile.id}' has no runtime-job-default policy.`)
  for (const field of ['memoryBytes', 'nanoCpus', 'pidsLimit', 'maximumDurationSeconds', 'maximumOutputBytes', 'tmpfsBytes']) positive(policy[field], `Wine CoreCLR policy ${field}`)
  const soft = Math.max(sandbox.openFilesSoftLimit, 512)
  const hard = Math.max(sandbox.openFilesHardLimit, 512)
  const argv = ['run', '--rm', '--name', name, '--pull', 'never', '--network', 'none', '--ipc', 'none', '--read-only', '--stop-timeout', '1', '--security-opt', 'no-new-privileges=true', '--security-opt', `seccomp=${sandbox.seccompPath}`, '--cap-drop', 'ALL', '--user', '1654:1654', '--ulimit', `nofile=${soft}:${hard}`, '--pids-limit', String(policy.pidsLimit), '--memory', String(policy.memoryBytes), '--memory-swap', String(policy.memoryBytes), '--cpus', String(policy.nanoCpus / 1e9), '--init', '--tmpfs', `/tmp:rw,exec,nosuid,nodev,size=${policy.tmpfsBytes},uid=0,gid=0,mode=1777`, '--mount', `type=bind,source=${workspace},target=/workspace,readonly`]
  const environment = wineEnvironment(operation, policy)
  for (const [key, value] of Object.entries(environment)) argv.push('--env', `${key}=${value}`)
  argv.push(row.image.imageId, profile.operations[operation].command.executable, ...substitute(profile.operations[operation].command, operation))
  return { argv, timeout: policy.maximumDurationSeconds * 1000 + 5000, environment }
}
function verifyCandidateImageTag(spawn, row, options) {
  const inspected = runProcess(spawn, 'docker', ['image', 'inspect', '--format', '{{.Id}}', row.candidateImage], options, `Wine CoreCLR candidate image tag '${row.candidateImage}'`, [0], 30000)
  const output = String(inspected.stdout ?? '')
  const resolvedImageId = output.replace(/\r?\n$/, '')
  if (!imageId.test(resolvedImageId) || ![resolvedImageId, `${resolvedImageId}\n`, `${resolvedImageId}\r\n`].includes(output)) fail(`Wine CoreCLR candidate image tag '${row.candidateImage}' did not resolve to exactly one canonical image ID.`)
  if (resolvedImageId !== row.image.imageId) fail(`Wine CoreCLR candidate image tag '${row.candidateImage}' resolves to '${resolvedImageId}', not recorded image ID '${row.image.imageId}'.`)
}
function ensureProbe(spawn, options, project, output) { runProcess(spawn, 'dotnet', ['build', project, '--configuration', 'Release', '--framework', 'netcoreapp2.0', '--no-restore', '--warnaserror'], options, 'Wine CoreCLR capability probe build'); for (const file of [probeAssembly, 'SharpLabNext.RuntimeCapabilityProbe.pdb', 'SharpLabNext.RuntimeCapabilityProbe.deps.json', 'SharpLabNext.RuntimeCapabilityProbe.runtimeconfig.json']) if (!fs.statSync(path.join(output, file)).isFile() || fs.statSync(path.join(output, file)).size === 0) fail(`Wine CoreCLR probe output '${file}' is missing or empty.`) }
function cleanup(spawn, name, options) { try { spawn('docker', ['rm', '--force', name], { cwd: options.cwd, env: options.env, encoding: 'utf8', shell: false, maxBuffer: maxBytes, timeout: 10000, killSignal: 'SIGKILL' }) } catch {} }

function stageProbe(outputPath) {
  const workspace = fs.mkdtempSync(path.join(path.dirname(outputPath), '.wine-coreclr-workspace-'))
  try {
    for (const filename of [probeAssembly, 'SharpLabNext.RuntimeCapabilityProbe.pdb', 'SharpLabNext.RuntimeCapabilityProbe.deps.json', 'SharpLabNext.RuntimeCapabilityProbe.runtimeconfig.json']) {
      const source = path.join(outputPath, filename)
      const metadata = fs.lstatSync(source)
      if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.size <= 0 || metadata.size > maxBytes) fail(`Wine CoreCLR probe output '${filename}' is not a bounded regular file.`)
      fs.copyFileSync(source, path.join(workspace, filename), fs.constants.COPYFILE_EXCL)
    }
    const readyDirectory = path.join(workspace, '.sharplabnext')
    fs.mkdirSync(readyDirectory, { mode: 0o755 })
    fs.writeFileSync(path.join(readyDirectory, 'ready'), '', { flag: 'wx', mode: 0o444 })
    fs.chmodSync(workspace, 0o755)
    return workspace
  } catch (error) {
    fs.rmSync(workspace, { recursive: true, force: true })
    throw error
  }
}
function writeAtomic(filename, value) { fs.mkdirSync(path.dirname(filename), { recursive: true }); const temporary = path.join(path.dirname(filename), `.${path.basename(filename)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`); try { fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' }); fs.renameSync(temporary, filename) } finally { fs.rmSync(temporary, { force: true }) } }
function update(row, profile, run, jit, sandbox, now) { const old = object(row.verification) ? row.verification : {}, smoke = { ...(object(old.smoke) ? old.smoke : {}), runtimeIdentity: 'passed', run: 'passed', jit: jit ? 'passed' : 'not-applicable', mapping: 'not-applicable' }, pending = Object.entries(smoke).filter(([, value]) => value !== 'passed' && value !== 'not-applicable').map(([key]) => key), openFilesSoftLimit = Math.max(sandbox.openFilesSoftLimit, 512), openFilesHardLimit = Math.max(sandbox.openFilesHardLimit, 512); row.verification = { ...old, status: pending.length ? 'runtime-smoke-passed' : 'smoke-passed', reason: pending.length ? `${pending.join('-')}-pending` : null, smoke, evidence: { ...(object(old.evidence) ? old.evidence : {}), wineCoreClr: { observedAt: now.toISOString(), profileSha256: row.profileSha256, imageId: row.image.imageId, referenceSetId: row.referenceSetId, sourceRevision: row.image.labels['io.sharplabnext.source.revision'], sourceContext: row.image.labels['io.sharplabnext.source.context'], probeTargetFramework: 'netcoreapp2.0', probeAssembly, run, jit: jit ? { ...jit, implementationId: profile.operations.jit.implementationId, sourceMappingKind: 'none' } : null, sandbox: { networkMode: 'none', ipcMode: 'none', readOnlyRootFileSystem: true, noNewPrivileges: true, capabilitiesDropped: 'all', user: '1654:1654', winePrefix: '/opt/wine-dotnet', readyMarker: '/workspace/.sharplabnext/ready', xdgRuntimeDirectory: '/tmp/sharplabnext-wine-runtime-1654', seccompSha256: sandbox.seccompSha256, openFilesSoftLimit, openFilesHardLimit } } } } }

export function runWineCoreClrSmokes(options) {
  const { profileIds, resultsPath = defaultResults, spawn = spawnSync, now = () => new Date(), cwd = root, env = process.env, profileDirectory = candidateDirectory, probeProjectPath = probeProject, probeOutputPath = probeOutput, runtimeMatrixPath = defaultRuntimeMatrix } = options
  const sandbox = options.sandbox ?? readSandbox(options.supervisorSettingsPath)
  if (!Array.isArray(profileIds) || profileIds.length === 0 || new Set(profileIds).size !== profileIds.length || profileIds.some(id => !profileId.test(id))) fail('Wine CoreCLR smoke profile IDs must be a non-empty unique list of supported Wine profile IDs.')
  if (!sandbox || !sha256.test(sandbox.seccompSha256 ?? '') || typeof sandbox.seccompPath !== 'string' || positive(sandbox.openFilesSoftLimit, 'Wine CoreCLR open-files soft limit') > positive(sandbox.openFilesHardLimit, 'Wine CoreCLR open-files hard limit')) fail('Wine CoreCLR smoke requires a valid Supervisor sandbox binding.')
  const file = path.resolve(resultsPath), result = readJson(file, 'Functional result').value; if (result.schemaVersion !== 1 || !Array.isArray(result.rows)) fail('Functional result must use schema version 1 with a rows array.')
  const matrix = readJson(path.resolve(runtimeMatrixPath), 'Runtime matrix').value
  if (!Array.isArray(matrix?.coreClr)) fail('Runtime matrix must contain coreClr targets.')
  const rows = profileIds.map(id => { const matches = result.rows.filter(row => row?.profileId === id); if (matches.length !== 1) fail(`Functional result must bind Wine CoreCLR profile '${id}' exactly once.`); const document = readJson(path.join(path.resolve(profileDirectory), `${id}.json`), `Wine CoreCLR profile '${id}'`); const targets = matrix.coreClr.filter(target => target?.id === matches[0].matrixTargetId); if (targets.length !== 1) fail(`Runtime matrix must bind Wine CoreCLR profile '${id}' exactly once.`); return { row: matches[0], profile: document.value, hasJit: validateProfile(matches[0], document.value, document.bytes, targets[0]) } })
  for (const item of rows) verifyCandidateImageTag(spawn, item.row, { cwd, env })
  ensureProbe(spawn, { cwd, env }, path.resolve(probeProjectPath), path.resolve(probeOutputPath))
  const workspace = stageProbe(path.resolve(probeOutputPath))
  const completed = []
  try {
    for (const item of rows) {
      const invoke = operation => {
        const name = `sln-wine-coreclr-${operation}-${item.profile.id}-${process.pid}-${crypto.randomBytes(6).toString('hex')}`
        const docker = dockerArguments(item.profile, item.row, sandbox, workspace, name, operation)
        let cleaned = false
        const cleanupOnce = () => {
          if (cleaned) return
          cleaned = true
          cleanup(spawn, name, { cwd, env })
        }
        let processResult
        try {
          processResult = runProcess(spawn, 'docker', docker.argv, { cwd, env, onTimeout: cleanupOnce }, `Wine CoreCLR ${operation} smoke '${item.profile.id}'`, [0], docker.timeout)
        } finally {
          // The in-container entrypoint already bounds wineserver shutdown. This
          // single exact-name removal is only a host-side fallback if --rm loses.
          cleanupOnce()
        }
        return processResult
      }
      const run = validateRun(parseWineCoreClrFrameLog(invoke('run').stdout)); const jit = item.hasJit ? validateJit(parseWineCoreClrFrameLog(invoke('jit').stdout)) : null; completed.push({ ...item, run, jit })
    }
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true })
  }
  for (const item of completed) update(item.row, item.profile, item.run, item.jit, sandbox, now())
  result.verificationRefreshedAt = now().toISOString(); writeAtomic(file, result)
  return completed.map(item => ({ profileId: item.profile.id, imageId: item.row.image.imageId, runtimeElapsedMilliseconds: item.run.runtimeElapsedMilliseconds, jitElapsedMilliseconds: item.jit?.runtimeElapsedMilliseconds ?? null }))
}
function parseArguments(argv) { if (argv.includes('--help') || argv.includes('-h')) return { help: true }; const profileIds = []; let resultsPath; for (let index = 0; index < argv.length; index++) { const option = argv[index], value = argv[++index]; if (!value) fail(`${option} requires a value.`); if (option === '--profile') profileIds.push(value); else if (option === '--results' && resultsPath === undefined) resultsPath = value; else fail(`Unknown or duplicate option '${option}'.`) } return { profileIds, resultsPath } }
export function runRuntimeWineCoreClrSmokeCli(argv, options = {}) { const output = options.output ?? console; try { const parsed = parseArguments(argv); if (parsed.help) { output.log(runtimeWineCoreClrSmokeUsage); return 0 }; for (const result of runWineCoreClrSmokes({ ...options, profileIds: parsed.profileIds, resultsPath: parsed.resultsPath ?? options.resultsPath })) output.log(`${result.profileId}: Run passed${result.jitElapsedMilliseconds === null ? '' : '; JIT passed'}`); return 0 } catch (error) { output.error(`runtime Wine CoreCLR smoke error: ${error.message}`); return 1 } }
if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = runRuntimeWineCoreClrSmokeCli(process.argv.slice(2))
