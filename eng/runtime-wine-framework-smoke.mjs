/** Current-image Run smoke for exact Wine .NET Framework matrix candidates. */
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const resultsPathDefault = path.join(root, '.tmp', 'runtime-matrix-functional-results.json')
const profilesDefault = path.join(root, 'profiles', 'runtimes', 'candidates')
const matrixDefault = path.join(root, 'profiles', 'runtime-matrix.json')
const probeProjectDefault = path.join(root, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'SharpLabNext.RuntimeCapabilityProbe.csproj')
const probeOutputDefault = path.join(root, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'bin', 'Release', 'net20')
const supervisorSettingsDefault = path.join(root, 'src', 'Supervisor', 'SharpLabNext.RuntimeSupervisor', 'appsettings.json')
const probeAssembly = 'SharpLabNext.RuntimeCapabilityProbe.exe'
const methodFilter = 'SharpLabNext.RuntimeCapabilityProbe.Program.WindowsAbi'
const maxBytes = 16 * 1024 * 1024
const maxFrameBytes = 4 * 1024 * 1024
const digestPattern = /^sha256:[0-9a-f]{64}$/
const revisionPattern = /^[0-9a-f]{40}$/
const profileIdPattern = /^wine-netfx(?:20|30|35|40|45|451|452|46|461|462|47|471|472|48)-linux-x64$/
const frameKinds = Object.freeze({ stdout: 1, stderr: 2, exception: 6, exit: 7, jitAssembly: 9, jitSummary: 10 })
const argumentForwardingMarker = 'SLN-CAPABILITY-ARGUMENTS-V1'
const representativeWrapperPath = '/workspace/.sharplabnext/representative-wrapper.sh'
const peakMemoryMarker = 'SLN-CAPABILITY-CGROUP-V2-MEMORY-PEAK-V1='
const peakMemoryUnsupportedMarker = 'SLN-CAPABILITY-CGROUP-V2-MEMORY-PEAK-UNSUPPORTED-V1='
const peakMemoryUnsupportedReason = 'cgroup-v2-memory-peak-unavailable'
const representativeWrapper = `#!/bin/sh
set -u

status=0
/opt/sharplabnext/runtime-entrypoint.sh "$@" || status=$?
peak_path=/sys/fs/cgroup/memory.peak
if [ -L "$peak_path" ] || [ ! -r "$peak_path" ]; then
    printf '%s%s\\n' '${peakMemoryUnsupportedMarker}' '${peakMemoryUnsupportedReason}' >&2
else
    peak_memory_bytes=$(cat "$peak_path" 2>/dev/null) || peak_memory_bytes=
    if [ -z "$peak_memory_bytes" ]; then
        printf '%s%s\\n' '${peakMemoryUnsupportedMarker}' '${peakMemoryUnsupportedReason}' >&2
    else
        printf '%s%s\\n' '${peakMemoryMarker}' "$peak_memory_bytes" >&2
    fi
fi
exit "$status"
`

export const runtimeWineFrameworkSmokeUsage = 'Usage:\n  node eng/runtime-wine-framework-smoke.mjs --profile ID [--profile ID ...] [--representative] [--results PATH]'
export class RuntimeWineFrameworkSmokeError extends Error {
  constructor(message, options) { super(message, options); this.name = 'RuntimeWineFrameworkSmokeError' }
}
function fail(message, options) { throw new RuntimeWineFrameworkSmokeError(message, options) }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }
function digest(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}` }
function positive(value, label) { if (!Number.isSafeInteger(value) || value <= 0) fail(`${label} must be a positive safe integer.`); return value }
function text(value, label) { if (typeof value !== 'string' || value.length === 0) fail(`${label} must be a non-empty string.`); return value }

function readJson(filename, label) {
  let stat
  try { stat = fs.lstatSync(filename) } catch (error) { fail(`${label} '${filename}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maxBytes) fail(`${label} '${filename}' must be a bounded regular non-link file.`)
  const bytes = fs.readFileSync(filename)
  try { return { bytes, value: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) } } catch (error) { fail(`${label} '${filename}' is invalid JSON: ${error.message}`, { cause: error }) }
}
function readSandbox(settingsPath = supervisorSettingsDefault) {
  const settings = readJson(path.resolve(settingsPath), 'Runtime Supervisor settings').value
  const sandbox = settings?.RuntimeSupervisor?.Sandbox
  if (!object(sandbox) || !digestPattern.test(sandbox.SeccompProfileSha256 ?? '')) fail('Runtime Supervisor settings has an invalid Sandbox definition.')
  const seccompPath = path.resolve(path.dirname(settingsPath), text(sandbox.SeccompProfilePath, 'Runtime Supervisor seccomp profile path'))
  const stat = fs.lstatSync(seccompPath)
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > 1024 * 1024) fail('Runtime Supervisor seccomp profile must be a bounded regular non-link file.')
  const seccompSha256 = digest(fs.readFileSync(seccompPath))
  if (seccompSha256 !== sandbox.SeccompProfileSha256) fail('Runtime Supervisor seccomp digest disagrees with its configured identity.')
  return { seccompPath, seccompSha256, openFilesSoftLimit: positive(sandbox.OpenFilesSoftLimit, 'Runtime Supervisor open-files soft limit'), openFilesHardLimit: positive(sandbox.OpenFilesHardLimit, 'Runtime Supervisor open-files hard limit') }
}
function runProcess(spawn, command, argv, options, label, codes = [0], timeout = 120000) {
  const result = spawn(command, argv, { cwd: options.cwd, env: options.env, encoding: 'utf8', shell: false, maxBuffer: maxBytes, timeout, killSignal: 'SIGKILL' })
  if (result?.error) {
    if (result.error.code === 'ETIMEDOUT') { try { options.onTimeout?.() } catch {}; fail(`${label} exceeded its ${timeout} ms process timeout.`, { cause: result.error }) }
    fail(`${label} could not start: ${result.error.message}`, { cause: result.error })
  }
  if (!codes.includes(result.status)) fail(`${label} exited ${result.status ?? '<unknown>'}${String(result.stderr ?? '').trim() ? `: ${String(result.stderr).trim().slice(0, 1000)}` : ''}`)
  return result
}

export function parseWineFrameworkFrameLog(stdout) {
  const lines = String(stdout).split(/\r?\n/).filter(Boolean)
  if (lines.length === 0) fail('Wine Framework emitted no protocol frames.')
  let sequence = 1
  return lines.map((line, index) => {
    if (!/^[A-Za-z0-9+/]+={0,2}$/.test(line) || line.length % 4 !== 0) fail('Wine Framework emitted a non-canonical base64 frame line.')
    const bytes = Buffer.from(line, 'base64')
    if (bytes.toString('base64') !== line || bytes.length < 18 || bytes.toString('ascii', 0, 4) !== 'SLNR' || bytes[4] !== 1) fail('Wine Framework emitted an invalid protocol frame header.')
    const kind = bytes[5], payloadLength = bytes.readInt32LE(14), actual = bytes.readBigInt64LE(6)
    if (!Object.values(frameKinds).includes(kind)) fail(`Wine Framework runtime frame kind ${kind} is not supported.`)
    if (kind === frameKinds.exit && index !== lines.length - 1) fail('Wine Framework emitted a frame after its terminal Exit frame.')
    if (actual !== BigInt(sequence++) || payloadLength < 0 || payloadLength > maxFrameBytes || bytes.length !== 18 + payloadLength) fail('Wine Framework emitted an invalid frame sequence or payload length.')
    return { kind, payload: bytes.subarray(18) }
  })
}
function property(value, name, label) {
  if (!object(value)) fail(`${label} must be an object.`)
  const names = [name, `${name[0].toUpperCase()}${name.slice(1)}`].filter(key => Object.hasOwn(value, key))
  if (names.length !== 1) fail(`${label} must contain exactly one '${name}' property.`)
  return value[names[0]]
}
function frameText(frames, kind) { return Buffer.concat(frames.filter(frame => frame.kind === kind).map(frame => frame.payload)).toString('utf8') }
function jsonFrame(frames, kind, label) {
  const matches = frames.filter(frame => frame.kind === kind)
  if (matches.length !== 1) fail(`${label} must contain exactly one frame; observed ${matches.length}.`)
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(matches[0].payload)) } catch (error) { fail(`${label} contains invalid JSON: ${error.message}`, { cause: error }) }
}
function elapsed(exit, label) {
  const elapsedMilliseconds = property(exit, 'elapsedMilliseconds', label)
  if (typeof elapsedMilliseconds !== 'number' || !Number.isFinite(elapsedMilliseconds) || elapsedMilliseconds < 0) fail(`${label}.elapsedMilliseconds is invalid.`)
  return elapsedMilliseconds
}
function integer(value, name, label) {
  const result = property(value, name, label)
  if (!Number.isSafeInteger(result)) fail(`${label}.${name} must be a safe integer.`)
  return result
}
function validateRun(frames, mode = 'success-security') {
  const allowed = mode === 'user-exception' ? [frameKinds.exception, frameKinds.exit] : [frameKinds.stdout, frameKinds.stderr, frameKinds.exit]
  if (frames.some(frame => !allowed.includes(frame.kind))) fail(`Wine Framework ${mode} emitted an unexpected frame kind.`)
  const label = `Wine Framework ${mode}`
  const exit = jsonFrame(frames, frameKinds.exit, `${label} Exit`)
  if (mode === 'user-exception') {
    const value = jsonFrame(frames, frameKinds.exception, `${label} exception`)
    const inner = property(value, 'innerException', `${label} exception`)
    const outerType = text(property(value, 'typeName', `${label} exception`), `${label} exception typeName`)
    const outerMessage = text(property(value, 'message', `${label} exception`), `${label} exception message`)
    const stackTrace = text(property(value, 'stackTrace', `${label} exception`), `${label} exception stackTrace`)
    const innerType = text(property(inner, 'typeName', `${label} nested exception`), `${label} nested exception typeName`)
    const innerMessage = text(property(inner, 'message', `${label} nested exception`), `${label} nested exception message`)
    const innerStackTrace = text(property(inner, 'stackTrace', `${label} nested exception`), `${label} nested exception stackTrace`)
    if (outerType !== 'System.InvalidOperationException' || outerMessage !== 'outer capability probe failure' || innerType !== 'System.ArgumentException' || innerMessage !== 'inner capability probe failure' || !stackTrace.includes('ThrowNestedException') || !innerStackTrace.includes('ThrowNestedException') || property(exit, 'status', `${label} Exit`) !== 'user-exception' || property(exit, 'exitCode', `${label} Exit`) !== 1) fail('Wine Framework nested exception frames do not retain the expected error family.')
    return { runtimeElapsedMilliseconds: elapsed(exit, `${label} Exit`), outerType, outerMessage, outerStackTrace: stackTrace, innerType, innerMessage, innerStackTrace, exitCode: 1 }
  }
  const stdout = frameText(frames, frameKinds.stdout), stderr = frameText(frames, frameKinds.stderr)
  const markers = mode === 'arguments-forwarding'
    ? [argumentForwardingMarker]
    : mode === 'non-zero-return'
      ? ['SLN-CAPABILITY-NONZERO-V1']
      : ['SLN-CAPABILITY-STDOUT-V1', 'SLN-CAPABILITY-NETWORK-BLOCKED-V1', 'SLN-CAPABILITY-ROOTFS-READONLY-V1']
  for (const marker of markers) if (!stdout.includes(marker)) fail(`${label} stdout is missing '${marker}'.`)
  const expectedExitCode = mode === 'non-zero-return' ? 23 : 0
  const expectedStatus = mode === 'non-zero-return' ? 'non-zero-exit' : 'completed'
  if ((mode === 'success-security' && !stderr.includes('SLN-CAPABILITY-STDERR-V1')) ||
      property(exit, 'status', `${label} Exit`) !== expectedStatus ||
      property(exit, 'exitCode', `${label} Exit`) !== expectedExitCode) {
    fail(`${label} did not report the expected output and exit state.`)
  }
  return { runtimeElapsedMilliseconds: elapsed(exit, `${label} Exit`), exitCode: expectedExitCode }
}
function selectedAssemblySection(assembly, method) {
  const lines = assembly.replace(/\r\n?/g, '\n').split('\n')
  const sections = []
  for (let index = 0; index < lines.length; index++) {
    const match = /^(?:;\s*)?Assembly listing for method\s+(.+)$/.exec(lines[index])
    if (match) sections.push({ start: index, name: match[1] })
  }
  const matches = sections
    .map((section, index) => ({ ...section, end: sections[index + 1]?.start ?? lines.length }))
    .filter(section => section.name.toLowerCase() === method.toLowerCase())
  if (matches.length !== 1) fail(`Wine Framework JIT assembly must contain exactly one '${method}' section; observed ${matches.length}.`)
  return lines.slice(matches[0].start, matches[0].end).join('\n')
}
function validateJit(frames) {
  if (frames.some(frame => ![frameKinds.jitAssembly, frameKinds.jitSummary, frameKinds.exit].includes(frame.kind))) fail('Wine Framework JIT emitted an unexpected frame kind.')
  const assembly = frameText(frames, frameKinds.jitAssembly)
  const summary = jsonFrame(frames, frameKinds.jitSummary, 'Wine Framework JIT summary')
  const exit = jsonFrame(frames, frameKinds.exit, 'Wine Framework JIT Exit')
  if (assembly.trim().length === 0) fail('Wine Framework JIT emitted no native assembly text.')
  if (property(summary, 'methodFilter', 'Wine Framework JIT summary') !== methodFilter ||
      property(exit, 'status', 'Wine Framework JIT Exit') !== 'completed' ||
      integer(exit, 'exitCode', 'Wine Framework JIT Exit') !== 0) {
    fail('Wine Framework JIT did not report the required method and completion state.')
  }
  const runtimeVersion = text(property(summary, 'runtimeVersion', 'Wine Framework JIT summary'), 'Wine Framework JIT runtime version')
  if (!/^\d+\.\d+\.\d+(?:\.\d+)?$/.test(runtimeVersion)) fail('Wine Framework JIT summary has an invalid Desktop CLR version.')
  const methods = property(summary, 'methods', 'Wine Framework JIT summary')
  if (!Array.isArray(methods)) fail('Wine Framework JIT summary methods must be an array.')
  const selected = methods.find(method =>
    object(method) &&
    property(method, 'status', 'Wine Framework JIT method') === 'prepared' &&
    property(method, 'displayName', 'Wine Framework JIT method') === methodFilter &&
    integer(method, 'nativeCodeSize', 'Wine Framework JIT method') > 0 &&
    integer(method, 'instructionCount', 'Wine Framework JIT method') > 0 &&
    property(method, 'mappingSource', 'Wine Framework JIT method') === 'none' &&
    Array.isArray(property(method, 'linkedRanges', 'Wine Framework JIT method')) &&
    property(method, 'linkedRanges', 'Wine Framework JIT method').length === 0)
  if (!selected) fail('Wine Framework JIT summary has no prepared WindowsAbi method with native code, instructions, and honest no-mapping evidence.')
  const methodAssembly = selectedAssemblySection(assembly, methodFilter)
  if (!/\b(?:rcx|ecx)\b/i.test(methodAssembly) ||
      !/\b(?:rdx|edx)\b/i.test(methodAssembly) ||
      !/\b(?:rax|eax)\b/i.test(methodAssembly)) {
    fail('Wine Framework JIT assembly does not prove the Windows x64 ABI registers rcx, rdx, and rax/eax in WindowsAbi.')
  }
  return {
    runtimeElapsedMilliseconds: elapsed(exit, 'Wine Framework JIT Exit'),
    runtimeVersion,
    assemblyBytes: Buffer.byteLength(assembly),
    nativeCodeSize: integer(selected, 'nativeCodeSize', 'Wine Framework JIT method'),
    instructionCount: integer(selected, 'instructionCount', 'Wine Framework JIT method'),
    abi: ['rcx', 'rdx', 'rax/eax'],
  }
}
function substitute(operation, arguments_) {
  const command = operation?.command
  if (!object(operation) || !object(command) || typeof command.executable !== 'string' || !Array.isArray(command.argv)) fail('Wine Framework profile operation command is invalid.')
  const argv = []
  for (const token of command.argv) {
    if (typeof token !== 'string') fail('Wine Framework profile command has a non-string argument.')
    if (token === '{entryAssembly}') argv.push(operation.pathStyle === 'wine-z' ? `Z:\\workspace\\${probeAssembly}` : `/workspace/${probeAssembly}`)
    else if (token === '{arguments}') argv.push(...arguments_)
    else if (token === '{methodFilter}') argv.push(methodFilter)
    else if (token.includes('{')) fail(`Wine Framework profile command has unsupported token '${token}'.`)
    else argv.push(token)
  }
  return { executable: command.executable, argv }
}
function validateProfile(row, profile, bytes, target, matrixDigest) {
  if (!object(row) || !digestPattern.test(row.profileSha256 ?? '') || !digestPattern.test(row.image?.imageId ?? '') || digest(bytes) !== row.profileSha256 || profile?.id !== row.profileId || profile?.image !== row.candidateImage) fail(`Wine Framework result row '${row?.profileId ?? '<unknown>'}' has no current immutable profile/image binding.`)
  if (!digestPattern.test(matrixDigest ?? '')) fail('Functional result has no runtime matrix SHA-256 binding.')
  const requiredPrefix = target.clrGeneration === 'clr2' ? '/opt/wine-netfx-clr2' : target.clrGeneration === 'clr4' ? '/opt/wine-netfx-clr4' : undefined
  if (profileIdPattern.test(profile.id) === false || profile.family !== 'netfx-clr-wine' || profile.runtimeVersion !== target.version || profile.container?.isolationKind !== 'wine' || profile.container?.environmentKind !== 'wine' || profile.container?.executionUser !== '0:0' || profile.layout?.runnerKind !== 'wine-netfx' || profile.layout?.wineHostPath !== '/usr/lib/wine/wine64' || profile.layout?.winePrefixPath !== requiredPrefix || profile.container?.winePrefixPath !== requiredPrefix) fail(`Wine Framework profile '${profile.id}' does not declare the required Wine Framework sandbox and prefix contract.`)
  if (!Array.isArray(profile.capabilities) || profile.capabilities.length !== 2 || profile.capabilities[0] !== 'run' || profile.capabilities[1] !== 'jit-asm' ||
      row.expected?.runImplementationId !== 'sharplabnext-target-runtime-runner-v1' ||
      row.expected?.jitImplementationId !== 'sharplabnext-desktop-clr-jit-inspector-v1' ||
      row.expected?.sourceMappingKind !== 'none' ||
      profile.operations?.run?.implementationId !== 'sharplabnext-target-runtime-runner-v1' ||
      profile.operations?.run?.pathStyle !== 'wine-z' ||
      profile.operations?.run?.command?.executable !== '/usr/lib/wine/wine64' ||
      profile.operations?.jit?.implementationId !== 'sharplabnext-desktop-clr-jit-inspector-v1' ||
      profile.operations?.jit?.pathStyle !== 'unix' ||
      profile.operations?.jit?.sourceMappingKind !== 'none' ||
      profile.operations?.jit?.command?.executable !== '/usr/share/dotnet/dotnet' ||
      profile.layout?.jitInspectorAssemblyPath !== '/opt/sharplabnext/SharpLabNext.WineRunner.dll') {
    fail(`Wine Framework profile '${profile.id}' does not match the Run and Desktop CLR JIT capability boundary.`)
  }
  const accepted = profile.acceptedFrameworks
  if (!Array.isArray(accepted) || accepted.length !== 1 || accepted[0]?.name !== '.NETFramework' || accepted[0]?.exactVersion !== target.version || row.matrixTargetId !== target.id || row.runtimeVersion !== target.version || row.referenceSetId !== target.referenceSetId || target.targetFramework !== `net${target.id.slice('netfx'.length)}`) fail(`Wine Framework profile '${profile.id}' does not match its exact matrix target.`)
  const labels = row.image.labels
  const required = {
    'com.sharplabnext.runtime-candidate': 'true',
    'com.sharplabnext.runtime-profile': profile.id,
    'io.sharplabnext.runtime.environment': 'wine-netfx',
    'io.sharplabnext.runtime.framework-version': target.version,
    'io.sharplabnext.framework.target-id': target.id,
    'io.sharplabnext.framework.matrix-selector': 'true',
    'io.sharplabnext.framework.selector': '/opt/sharplabnext/.framework-selector.json',
  }
  if (!object(labels) || Object.entries(required).some(([name, value]) => labels[name] !== value) || !revisionPattern.test(labels['io.sharplabnext.source.revision'] ?? '') || labels['io.sharplabnext.source.revision'] !== labels['org.opencontainers.image.revision'] || !revisionPattern.test(labels['io.sharplabnext.framework.source-revision'] ?? '') || !/^[^\s@]+@sha256:[0-9a-f]{64}$/.test(labels['io.sharplabnext.framework.matrix-parent'] ?? '') || !/^[^\s@]+@sha256:[0-9a-f]{64}$/.test(labels['io.sharplabnext.framework.row-operator-image'] ?? '') || !digestPattern.test(labels['io.sharplabnext.framework.row-digest'] ?? '') || labels['io.sharplabnext.runtime.component-digest'] !== labels['io.sharplabnext.framework.row-operator-image'].slice(labels['io.sharplabnext.framework.row-operator-image'].lastIndexOf('@') + 1) || labels['io.sharplabnext.runtime.component-source-uri'] !== `docker://${labels['io.sharplabnext.framework.row-operator-image']}`) fail(`Wine Framework result row '${profile.id}' image labels do not prove the exact selected Framework identity.`)
}
function wineEnvironment(profile, policy) {
  return {
    WINEPREFIX: profile.container.winePrefixPath,
    WINEARCH: 'win64', WINEDEBUG: '-all', WINESERVER: '/usr/lib/wine/wineserver64',
    SHARPLABNEXT_PREPARE_WINE_XDG_RUNTIME_DIR: '1', SHARPLABNEXT_WINE_CLEANUP: '1',
    SHARPLABNEXT_CAPTURE_DIRECTORY: 'Z:\\tmp', SHARPLABNEXT_INSTRUMENTATION: 'none',
    SHARPLABNEXT_MAX_OUTPUT_BYTES: String(policy.maximumOutputBytes),
  }
}
function effectiveWineOpenFiles(sandbox) { return { soft: Math.max(sandbox.openFilesSoftLimit, 512), hard: Math.max(sandbox.openFilesHardLimit, 512) } }
function dockerArguments(profile, row, sandbox, workspace, name, operationName, arguments_, representative = false) {
  const policy = profile.securityPolicies?.find(value => value.id === 'runtime-job-wine-netfx')
  if (!object(policy)) fail(`Wine Framework profile '${profile.id}' has no runtime-job-wine-netfx policy.`)
  for (const field of ['memoryBytes', 'nanoCpus', 'pidsLimit', 'maximumDurationSeconds', 'maximumOutputBytes', 'tmpfsBytes']) positive(policy[field], `Wine Framework policy ${field}`)
  if (sandbox.openFilesSoftLimit > sandbox.openFilesHardLimit) fail('Wine Framework open-files soft limit cannot exceed its hard limit.')
  const { soft, hard } = effectiveWineOpenFiles(sandbox)
  const argv = ['run', '--rm', '--name', name, '--pull', 'never', '--network', 'none', '--ipc', 'none', '--read-only', '--stop-timeout', '1', '--security-opt', 'no-new-privileges=true', '--security-opt', `seccomp=${sandbox.seccompPath}`, '--cap-drop', 'ALL', '--user', profile.container.executionUser, '--ulimit', `nofile=${soft}:${hard}`, '--pids-limit', String(policy.pidsLimit), '--memory', String(policy.memoryBytes), '--memory-swap', String(policy.memoryBytes), '--cpus', String(policy.nanoCpus / 1e9), '--init', '--tmpfs', `/tmp:rw,exec,nosuid,nodev,size=${policy.tmpfsBytes},uid=0,gid=0,mode=1777`, '--mount', `type=bind,source=${workspace},target=/workspace,readonly`]
  for (const [key, value] of Object.entries(wineEnvironment(profile, policy))) argv.push('--env', `${key}=${value}`)
  const operation = profile.operations?.[operationName]
  const substituted = substitute(operation, arguments_)
  const command = [substituted.executable, ...substituted.argv]
  if (representative) argv.push('--entrypoint', '/bin/sh', row.image.imageId, representativeWrapperPath, ...command)
  else argv.push(row.image.imageId, ...command)
  return { argv, timeout: policy.maximumDurationSeconds * 1000 + 5000, policy }
}
function verifyCandidateImageTag(spawn, row, options) {
  const inspected = runProcess(
    spawn,
    'docker',
    ['image', 'inspect', '--format', '{{.Id}}', row.candidateImage],
    options,
    `Wine Framework candidate image tag '${row.candidateImage}'`,
    [0],
    30000,
  )
  const output = String(inspected.stdout ?? '')
  const resolvedImageId = output.replace(/\r?\n$/, '')
  if (!digestPattern.test(resolvedImageId) ||
      ![resolvedImageId, `${resolvedImageId}\n`, `${resolvedImageId}\r\n`].includes(output)) {
    fail(`Wine Framework candidate image tag '${row.candidateImage}' did not resolve to exactly one canonical image ID.`)
  }
  if (resolvedImageId !== row.image.imageId) {
    fail(`Wine Framework candidate image tag '${row.candidateImage}' resolves to '${resolvedImageId}', not recorded image ID '${row.image.imageId}'.`)
  }
}
function ensureProbe(spawn, options, project, output) {
  runProcess(spawn, 'dotnet', ['build', project, '--configuration', 'Release', '--framework', 'net20', '--no-restore', '--warnaserror'], options, 'Wine Framework capability probe build')
  for (const file of [probeAssembly, 'SharpLabNext.RuntimeCapabilityProbe.pdb']) if (!fs.statSync(path.join(output, file)).isFile() || fs.statSync(path.join(output, file)).size === 0) fail(`Wine Framework probe output '${file}' is missing or empty.`)
}
function stageProbe(outputPath, representative) {
  const workspace = fs.mkdtempSync(path.join(path.dirname(outputPath), '.wine-framework-workspace-'))
  try {
    for (const file of [probeAssembly, 'SharpLabNext.RuntimeCapabilityProbe.pdb']) {
      const source = path.join(outputPath, file), stat = fs.lstatSync(source)
      if (!stat.isFile() || stat.isSymbolicLink() || stat.size <= 0 || stat.size > maxBytes) fail(`Wine Framework probe output '${file}' is not a bounded regular file.`)
      fs.copyFileSync(source, path.join(workspace, file), fs.constants.COPYFILE_EXCL)
    }
    const supervisorState = path.join(workspace, '.sharplabnext')
    fs.mkdirSync(supervisorState, { mode: 0o755 })
    fs.writeFileSync(path.join(supervisorState, 'ready'), 'ready\n', { flag: 'wx', mode: 0o444 })
    if (representative) fs.writeFileSync(path.join(supervisorState, 'representative-wrapper.sh'), representativeWrapper, { flag: 'wx', mode: 0o444 })
    fs.chmodSync(workspace, 0o755)
    return workspace
  } catch (error) { fs.rmSync(workspace, { recursive: true, force: true }); throw error }
}
function cleanup(spawn, name, options) { try { spawn('docker', ['rm', '--force', name], { cwd: options.cwd, env: options.env, encoding: 'utf8', shell: false, maxBuffer: maxBytes, timeout: 10000, killSignal: 'SIGKILL' }) } catch {} }
function writeAtomic(filename, value) { fs.mkdirSync(path.dirname(filename), { recursive: true }); const temporary = path.join(path.dirname(filename), `.${path.basename(filename)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`); try { fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' }); fs.renameSync(temporary, filename) } finally { fs.rmSync(temporary, { force: true }) } }
function representativePeakMemory(stderr, maximum) {
  const lines = String(stderr ?? '').split(/\r?\n/).filter(Boolean)
  const values = lines.filter(line => line.startsWith(peakMemoryMarker)).map(line => line.slice(peakMemoryMarker.length))
  const unsupported = lines.filter(line => line.startsWith(peakMemoryUnsupportedMarker)).map(line => line.slice(peakMemoryUnsupportedMarker.length))
  if (values.length + unsupported.length !== 1) fail('Wine Framework representative wrapper must emit exactly one peak-memory record.')
  if (unsupported.length === 1) {
    if (unsupported[0] !== peakMemoryUnsupportedReason) fail('Wine Framework representative wrapper reported an unsupported peak-memory reason.')
    return { containerPeakMemoryBytes: null, containerPeakMemoryAvailability: 'unsupported: cgroup-v2 memory.peak is unavailable' }
  }
  const value = values[0]
  if (!/^[1-9][0-9]*$/.test(value)) fail('Wine Framework representative wrapper emitted a non-canonical peak-memory value.')
  const bytes = Number(value)
  if (!Number.isSafeInteger(bytes) || bytes > maximum) fail('Wine Framework representative wrapper emitted a peak-memory value outside the container memory limit.')
  return { containerPeakMemoryBytes: bytes, containerPeakMemoryAvailability: 'measured: cgroup-v2 memory.peak' }
}
function representativeMeasurement(run, wallElapsedMilliseconds, stderr, maximumMemoryBytes) {
  return { ...run, wallElapsedMilliseconds, ...representativePeakMemory(stderr, maximumMemoryBytes) }
}
function update(row, profile, run, jit, representative, sandbox, now) {
  const old = object(row.verification) ? row.verification : {}, smoke = { ...(object(old.smoke) ? old.smoke : {}), runtimeIdentity: 'passed', run: 'passed', jit: 'passed', mapping: 'not-applicable' }
  const pending = Object.entries(smoke).filter(([, value]) => value !== 'passed' && value !== 'not-applicable').map(([key]) => key)
  const effectiveOpenFiles = effectiveWineOpenFiles(sandbox)
  row.verification = { ...old, status: pending.length ? 'runtime-smoke-passed' : 'smoke-passed', reason: pending.length ? `${pending.join('-')}-pending` : null, smoke, evidence: { ...(object(old.evidence) ? old.evidence : {}), wineFramework: { observedAt: now.toISOString(), profileSha256: row.profileSha256, imageId: row.image.imageId, referenceSetId: row.referenceSetId, frameworkVersion: profile.runtimeVersion, sourceRevision: row.image.labels['io.sharplabnext.source.revision'], run, jit: { ...jit, methodFilter, implementationId: profile.operations.jit.implementationId, sourceMappingKind: 'none' }, ...(representative === undefined ? {} : { representative }), sandbox: { networkMode: 'none', ipcMode: 'none', readOnlyRootFileSystem: true, noNewPrivileges: true, capabilitiesDropped: 'all', user: profile.container.executionUser, winePrefix: profile.container.winePrefixPath, seccompSha256: sandbox.seccompSha256, openFiles: { configured: { softLimit: sandbox.openFilesSoftLimit, hardLimit: sandbox.openFilesHardLimit }, effective: { softLimit: effectiveOpenFiles.soft, hardLimit: effectiveOpenFiles.hard } } } } } }
}

export function runWineFrameworkSmokes(options = {}) {
  const { profileIds, representative = false, resultsPath = resultsPathDefault, spawn = spawnSync, now = () => new Date(), wallClock = () => Number(process.hrtime.bigint() / 1000000n), cwd = root, env = process.env, profileDirectory = profilesDefault, probeProjectPath = probeProjectDefault, probeOutputPath = probeOutputDefault, runtimeMatrixPath = matrixDefault } = options
  const sandbox = options.sandbox ?? readSandbox(options.supervisorSettingsPath)
  if (!Array.isArray(profileIds) || profileIds.length === 0 || new Set(profileIds).size !== profileIds.length || profileIds.some(id => !profileIdPattern.test(id))) fail('Wine Framework smoke profile IDs must be a non-empty unique list of exact Wine Framework profile IDs.')
  if (!object(sandbox) || !digestPattern.test(sandbox.seccompSha256 ?? '') || typeof sandbox.seccompPath !== 'string' || positive(sandbox.openFilesSoftLimit, 'Wine Framework open-files soft limit') > positive(sandbox.openFilesHardLimit, 'Wine Framework open-files hard limit')) fail('Wine Framework smoke requires a valid Supervisor sandbox binding.')
  const file = path.resolve(resultsPath), result = readJson(file, 'Functional result').value
  if (result.schemaVersion !== 1 || !Array.isArray(result.rows)) fail('Functional result must use schema version 1 with a rows array.')
  const matrixDocument = readJson(path.resolve(runtimeMatrixPath), 'Runtime matrix')
  if (digest(matrixDocument.bytes) !== result.runtimeMatrixSha256 || !Array.isArray(matrixDocument.value?.framework?.targets)) fail('Functional result does not match its Framework runtime matrix binding.')
  const rows = profileIds.map(id => {
    const matches = result.rows.filter(row => row?.profileId === id)
    if (matches.length !== 1) fail(`Functional result must bind Wine Framework profile '${id}' exactly once.`)
    const document = readJson(path.join(path.resolve(profileDirectory), `${id}.json`), `Wine Framework profile '${id}'`)
    const targets = matrixDocument.value.framework.targets.filter(target => target?.id === matches[0].matrixTargetId)
    if (targets.length !== 1) fail(`Runtime matrix must bind Wine Framework profile '${id}' exactly once.`)
    validateProfile(matches[0], document.value, document.bytes, targets[0], result.runtimeMatrixSha256)
    return { row: matches[0], profile: document.value }
  })
  for (const item of rows) verifyCandidateImageTag(spawn, item.row, { cwd, env })
  ensureProbe(spawn, { cwd, env }, path.resolve(probeProjectPath), path.resolve(probeOutputPath))
  const workspace = stageProbe(path.resolve(probeOutputPath), representative), completed = []
  try {
    for (const item of rows) {
      const invokeRun = (mode, expectedCodes = [0], measure = false) => {
        const name = `sln-wine-framework-${item.profile.id}-${process.pid}-${crypto.randomBytes(6).toString('hex')}`, docker = dockerArguments(item.profile, item.row, sandbox, workspace, name, 'run', mode === 'arguments-forwarding' ? [mode, argumentForwardingMarker] : [mode], measure)
        let cleaned = false
        const cleanupOnce = () => { if (!cleaned) { cleaned = true; cleanup(spawn, name, { cwd, env }) } }
        const started = wallClock()
        let processResult
        try { processResult = runProcess(spawn, 'docker', docker.argv, { cwd, env, onTimeout: cleanupOnce }, `Wine Framework ${mode} smoke '${item.profile.id}'`, expectedCodes, docker.timeout) } finally { cleanupOnce() }
        const wallElapsedMilliseconds = wallClock() - started
        if (!Number.isFinite(wallElapsedMilliseconds) || wallElapsedMilliseconds < 0) fail('Wine Framework wall clock must report a non-negative finite elapsed time.')
        const run = validateRun(parseWineFrameworkFrameLog(processResult.stdout), mode)
        return measure ? representativeMeasurement(run, wallElapsedMilliseconds, processResult.stderr, docker.policy.memoryBytes) : run
      }
      const invokeJit = () => {
        const name = `sln-wine-framework-jit-${item.profile.id}-${process.pid}-${crypto.randomBytes(6).toString('hex')}`
        const docker = dockerArguments(item.profile, item.row, sandbox, workspace, name, 'jit', [], false)
        let cleaned = false
        const cleanupOnce = () => { if (!cleaned) { cleaned = true; cleanup(spawn, name, { cwd, env }) } }
        let processResult
        try { processResult = runProcess(spawn, 'docker', docker.argv, { cwd, env, onTimeout: cleanupOnce }, `Wine Framework JIT smoke '${item.profile.id}'`, [0], docker.timeout) } finally { cleanupOnce() }
        return validateJit(parseWineFrameworkFrameLog(processResult.stdout))
      }
      const run = invokeRun('success-security')
      const jit = invokeJit()
      const representativeEvidence = representative ? { cold: invokeRun('success-security', [0], true), warm: invokeRun('success-security', [0], true), arguments: invokeRun('arguments-forwarding', [0], true), nonZeroReturn: invokeRun('non-zero-return', [23], true), exception: invokeRun('user-exception', [1], true) } : undefined
      completed.push({ ...item, run: { runtimeElapsedMilliseconds: run.runtimeElapsedMilliseconds }, jit, representative: representativeEvidence })
    }
  } finally { fs.rmSync(workspace, { recursive: true, force: true }) }
  for (const item of completed) update(item.row, item.profile, item.run, item.jit, item.representative, sandbox, now())
  result.verificationRefreshedAt = now().toISOString(); writeAtomic(file, result)
  return completed.map(item => ({ profileId: item.profile.id, imageId: item.row.image.imageId, runtimeElapsedMilliseconds: item.run.runtimeElapsedMilliseconds, jitElapsedMilliseconds: item.jit.runtimeElapsedMilliseconds }))
}
function parseArguments(argv) { if (argv.includes('--help') || argv.includes('-h')) return { help: true }; const profileIds = []; let resultsPath; let representative = false; for (let index = 0; index < argv.length; index++) { const option = argv[index]; if (option === '--representative') { if (representative) fail('Unknown or duplicate option \'--representative\'.'); representative = true; continue } const value = argv[++index]; if (!value) fail(`${option} requires a value.`); if (option === '--profile') profileIds.push(value); else if (option === '--results' && resultsPath === undefined) resultsPath = value; else fail(`Unknown or duplicate option '${option}'.`) } return { profileIds, representative, resultsPath } }
export function runRuntimeWineFrameworkSmokeCli(argv, options = {}) { const output = options.output ?? console; try { const parsed = parseArguments(argv); if (parsed.help) { output.log(runtimeWineFrameworkSmokeUsage); return 0 }; for (const result of runWineFrameworkSmokes({ ...options, profileIds: parsed.profileIds, representative: parsed.representative, resultsPath: parsed.resultsPath ?? options.resultsPath })) output.log(`${result.profileId}: Run and JIT passed${parsed.representative ? '; representative checks passed' : ''}`); return 0 } catch (error) { output.error(`runtime Wine Framework smoke error: ${error.message}`); return 1 } }
if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = runRuntimeWineFrameworkSmokeCli(process.argv.slice(2))
