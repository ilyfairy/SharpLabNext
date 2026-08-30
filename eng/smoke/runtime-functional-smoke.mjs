/**
 * Run lightweight current-image functional smoke tests without promotion
 * plans or release evidence. Deep Supervisor/API tests remain a separate
 * deployment-readiness step.
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
const probeOutput = path.join(repositoryRoot, 'tests', 'Fixtures', 'SharpLabNext.RuntimeCapabilityProbe', 'bin', 'Release', 'netcoreapp2.0')
const probeAssembly = 'SharpLabNext.RuntimeCapabilityProbe.dll'
const supervisorSettingsPath = path.join(repositoryRoot, 'src', 'Supervisor', 'SharpLabNext.RuntimeSupervisor', 'appsettings.json')
const maximumResultBytes = 16 * 1024 * 1024
const maximumFramePayloadBytes = 4 * 1024 * 1024
const maximumDockerOutputBytes = 8 * 1024 * 1024
const imageIdPattern = /^sha256:[0-9a-f]{64}$/
const profileIdPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const sha256Pattern = /^sha256:[0-9a-f]{64}$/
const supportedFrameKinds = new Set([1, 2, 3, 4, 5, 6, 7, 8, 9, 10])

const frameKinds = Object.freeze({
  stdout: 1,
  stderr: 2,
  exception: 6,
  exit: 7,
})

export const runtimeFunctionalSmokeUsage = `Usage:
  node eng/smoke/runtime-functional-smoke.mjs --profile ID [--profile ID ...]
    [--exception-profile ID ...] [--results PATH]`

export class RuntimeFunctionalSmokeError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimeFunctionalSmokeError'
  }
}

function fail(message, options) { throw new RuntimeFunctionalSmokeError(message, options); }

function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

function readBoundedJson(filename, label) {
  let metadata
  try {
    metadata = fs.lstatSync(filename)
  } catch (error) {
    fail(`${label} '${filename}' could not be inspected: ${error.message}`, { cause: error })
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() ||
      metadata.size < 1 || metadata.size > maximumResultBytes) {
    fail(`${label} '${filename}' must be a bounded regular non-link file.`)
  }
  let bytes
  try {
    bytes = fs.readFileSync(filename)
  } catch (error) {
    fail(`${label} '${filename}' could not be read: ${error.message}`, { cause: error })
  }
  try {
    return { bytes, value: JSON.parse(bytes.toString('utf8')) }
  } catch (error) {
    fail(`${label} '${filename}' is invalid JSON: ${error.message}`, { cause: error })
  }
}

function sha256(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`; }

function runProcess(spawn, command, arguments_, options, label, expectedExitCodes = [0], timeoutMilliseconds = 120_000) {
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
      try {
        options.onTimeout?.()
      } catch {
        // The original bounded operation remains the authoritative failure.
      }
      fail(`${label} exceeded its ${timeoutMilliseconds} ms process timeout.`, {
        cause: result.error,
      })
    }
    fail(`${label} could not start: ${result.error.message}`, { cause: result.error })
  }
  if (!expectedExitCodes.includes(result.status)) {
    const stderr = String(result.stderr ?? '').trim()
    fail(
      `${label} exited ${result.status ?? '<unknown>'}` +
      (stderr.length > 0 ? `: ${stderr.slice(0, 1000)}` : ''),
    )
  }
  return result
}

function readInt64LittleEndian(bytes, offset) {
  const value = bytes.readBigInt64LE(offset)
  if (value <= 0n || value > BigInt(Number.MAX_SAFE_INTEGER)) {
    fail('Runtime frame sequence is outside the positive safe-integer range.')
  }
  return Number(value)
}

export function parseRuntimeFrameLog(stdout) {
  const lines = String(stdout).split(/\r?\n/).filter(line => line.length > 0)
  if (lines.length === 0) fail('Runtime emitted no protocol frames.')
  const frames = []
  let expectedSequence = 1
  for (const [lineIndex, line] of lines.entries()) {
    if (!/^[A-Za-z0-9+/]+={0,2}$/.test(line) || line.length % 4 !== 0) {
      fail('Runtime emitted a non-canonical base64 frame line.')
    }
    let bytes
    try {
      bytes = Buffer.from(line, 'base64')
    } catch (error) {
      fail(`Runtime emitted invalid base64: ${error.message}`, { cause: error })
    }
    if (bytes.toString('base64') !== line || bytes.length < 18 ||
        bytes.toString('ascii', 0, 4) !== 'SLNR' || bytes[4] !== 1) {
      fail('Runtime emitted an invalid protocol frame header.')
    }
    const kind = bytes[5]
    if (!supportedFrameKinds.has(kind)) {
      fail(`Runtime frame kind ${kind} is not supported.`)
    }
    if (kind === frameKinds.exit && lineIndex !== lines.length - 1) {
      fail('Runtime emitted a frame after its terminal Exit frame.')
    }
    const sequence = readInt64LittleEndian(bytes, 6)
    const payloadLength = bytes.readInt32LE(14)
    if (sequence !== expectedSequence++ || payloadLength < 0 ||
        payloadLength > maximumFramePayloadBytes || bytes.length !== 18 + payloadLength) {
      fail('Runtime emitted an invalid frame sequence or payload length.')
    }
    frames.push({
      sequence,
      kind,
      payload: bytes.subarray(18),
    })
  }
  return frames
}

function utf8(frames, kind) {
  return Buffer.concat(frames.filter(frame => frame.kind === kind).map(frame => frame.payload))
    .toString('utf8')
}

function jsonFrame(frames, kind, label) {
  const matches = frames.filter(frame => frame.kind === kind)
  if (matches.length !== 1) fail(`${label} must contain exactly one frame; observed ${matches.length}.`)
  try {
    return JSON.parse(matches[0].payload.toString('utf8'))
  } catch (error) {
    fail(`${label} frame contains invalid JSON: ${error.message}`, { cause: error })
  }
}

function validateSuccessFrames(frames) {
  if (frames.some(frame => ![
    frameKinds.stdout,
    frameKinds.stderr,
    frameKinds.exit,
  ].includes(frame.kind))) {
    fail('Run success emitted an unexpected frame kind.')
  }
  const stdout = utf8(frames, frameKinds.stdout)
  const stderr = utf8(frames, frameKinds.stderr)
  for (const marker of [
    'SLN-CAPABILITY-STDOUT-V1',
    'SLN-CAPABILITY-NETWORK-BLOCKED-V1',
    'SLN-CAPABILITY-ROOTFS-READONLY-V1',
  ]) {
    if (!stdout.includes(marker)) fail(`Run stdout is missing '${marker}'.`)
  }
  if (!stderr.includes('SLN-CAPABILITY-STDERR-V1')) {
    fail("Run stderr is missing 'SLN-CAPABILITY-STDERR-V1'.")
  }
  const exit = jsonFrame(frames, frameKinds.exit, 'Run Exit')
  if (exit.Status !== 'completed' || exit.ExitCode !== 0 ||
      typeof exit.ElapsedMilliseconds !== 'number' || exit.ElapsedMilliseconds < 0) {
    fail('Run Exit frame did not report a successful bounded completion.')
  }
  return {
    runtimeElapsedMilliseconds: exit.ElapsedMilliseconds,
    frameKinds: frames.map(frame => frame.kind),
    networkBlocked: true,
    rootFileSystemReadOnly: true,
    stdoutMarker: true,
    stderrMarker: true,
  }
}

function validateExceptionFrames(frames) {
  if (frames.some(frame => ![frameKinds.exception, frameKinds.exit].includes(frame.kind))) {
    fail('Nested exception run emitted an unexpected frame kind.')
  }
  const exception = jsonFrame(frames, frameKinds.exception, 'Exception')
  const exit = jsonFrame(frames, frameKinds.exit, 'Exception Exit')
  if (exception.TypeName !== 'System.InvalidOperationException' ||
      exception.Message !== 'outer capability probe failure' ||
      exception.InnerException?.TypeName !== 'System.ArgumentException' ||
      exception.InnerException?.Message !== 'inner capability probe failure' ||
      typeof exception.StackTrace !== 'string' || !exception.StackTrace.includes('ThrowNestedException') ||
      exit.Status !== 'user-exception' || exit.ExitCode !== 1) {
    fail('Nested user-exception frames do not retain the expected type, message, stack, and exit state.')
  }
  return {
    runtimeElapsedMilliseconds: exit.ElapsedMilliseconds,
    outerType: exception.TypeName,
    innerType: exception.InnerException.TypeName,
    stackRetained: true,
  }
}

function substituteCommand(command, entryAssembly, userArguments) {
  if (!isObject(command) || typeof command.executable !== 'string' ||
      !Array.isArray(command.argv)) {
    fail('Runtime profile Run command is invalid.')
  }
  const result = []
  for (const token of command.argv) {
    if (typeof token !== 'string') fail('Runtime profile Run argv must contain only strings.')
    if (token === '{entryAssembly}') result.push(entryAssembly)
    else if (token === '{arguments}') result.push(...userArguments)
    else if (token.includes('{')) fail(`Runtime profile contains unsupported Run token '${token}'.`)
    else result.push(token)
  }
  return { executable: command.executable, argv: result }
}

function positiveSafeInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0) fail(`${label} must be a positive safe integer.`)
  return value
}

function readSandbox() {
  const { value: settings } = readBoundedJson(supervisorSettingsPath, 'Runtime Supervisor settings')
  const sandbox = settings?.RuntimeSupervisor?.Sandbox
  if (!isObject(sandbox) || !sha256Pattern.test(sandbox.SeccompProfileSha256 ?? '')) {
    fail('Runtime Supervisor sandbox settings are invalid.')
  }
  const seccompPath = path.resolve(path.dirname(supervisorSettingsPath), sandbox.SeccompProfilePath ?? '')
  const metadata = fs.lstatSync(seccompPath)
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.size < 1 ||
      metadata.size > 1024 * 1024) {
    fail('Runtime Supervisor seccomp profile must be a bounded regular non-link file.')
  }
  const bytes = fs.readFileSync(seccompPath)
  const digest = sha256(bytes)
  if (digest !== sandbox.SeccompProfileSha256) {
    fail(`Runtime Supervisor seccomp digest '${digest}' disagrees with its configured identity.`)
  }
  let policy
  try {
    policy = JSON.parse(bytes.toString('utf8'))
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
  return {
    seccompPath,
    seccompSha256: digest,
    openFilesSoftLimit: positiveSafeInteger(sandbox.OpenFilesSoftLimit, 'Runtime Supervisor open-files soft limit'),
    openFilesHardLimit: positiveSafeInteger(sandbox.OpenFilesHardLimit, 'Runtime Supervisor open-files hard limit'),
  }
}

function dockerArguments(profile, resultRow, mode, sandbox, outputPath, containerName) {
  if (profile.family !== 'coreclr' || profile.container?.isolationKind !== 'standard' ||
      profile.operations?.run?.pathStyle !== 'unix') {
    fail(`Profile '${profile.id}' is not supported by the standard CoreCLR smoke path.`)
  }
  if (profile.container.executionUser !== '1654:1654') {
    fail(`Profile '${profile.id}' must use the standard non-root execution user.`)
  }
  const policy = profile.securityPolicies?.find(value => value.id === 'runtime-job-default')
  if (!isObject(policy)) fail(`Profile '${profile.id}' has no runtime-job-default policy.`)
  for (const property of [
    'memoryBytes',
    'nanoCpus',
    'pidsLimit',
    'maximumDurationSeconds',
    'maximumArtifactBytes',
    'maximumOutputBytes',
    'tmpfsBytes',
  ]) {
    positiveSafeInteger(policy[property], `Profile '${profile.id}' policy ${property}`)
  }
  if (sandbox.openFilesSoftLimit > sandbox.openFilesHardLimit) {
    fail('Runtime Supervisor open-files soft limit cannot exceed its hard limit.')
  }
  const command = substituteCommand(profile.operations.run.command, `/artifact/${probeAssembly}`, [mode])
  return [
    'run', '--rm', '--name', containerName, '--pull', 'never', '--network', 'none',
    '--ipc', 'none', '--read-only', '--stop-timeout', '1',
    '--security-opt', 'no-new-privileges=true',
    '--security-opt', `seccomp=${sandbox.seccompPath}`,
    '--cap-drop', 'ALL',
    '--user', profile.container.executionUser,
    '--ulimit', `nofile=${sandbox.openFilesSoftLimit}:${sandbox.openFilesHardLimit}`,
    '--pids-limit', String(policy.pidsLimit),
    '--memory', String(policy.memoryBytes),
    '--memory-swap', String(policy.memoryBytes),
    '--cpus', String(policy.nanoCpus / 1_000_000_000),
    '--init',
    '--tmpfs', `/tmp:rw,noexec,nosuid,nodev,size=${policy.tmpfsBytes},uid=1654,gid=1654,mode=0700`,
    '--mount', `type=bind,source=${outputPath},target=/artifact,readonly`,
    '--env', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
    '--env', 'COMPlus_EnableDiagnostics=0',
    '--env', 'DOTNET_EnableDiagnostics=0',
    '--env', `SHARPLABNEXT_MAX_OUTPUT_BYTES=${policy.maximumOutputBytes}`,
    '--env', 'SHARPLABNEXT_INSTRUMENTATION=none',
    '--entrypoint', command.executable,
    resultRow.image.imageId,
    ...command.argv,
  ]
}

function ensureProbe(spawn, options, projectPath, outputPath) {
  runProcess(spawn, 'dotnet', [
    'build', projectPath,
    '--configuration', 'Release',
    '--framework', 'netcoreapp2.0',
    '--no-restore',
    '--warnaserror',
  ], options, 'Runtime capability probe build')
  for (const filename of [
    probeAssembly,
    'SharpLabNext.RuntimeCapabilityProbe.pdb',
    'SharpLabNext.RuntimeCapabilityProbe.deps.json',
    'SharpLabNext.RuntimeCapabilityProbe.runtimeconfig.json',
  ]) {
    const fullPath = path.join(outputPath, filename)
    if (!fs.statSync(fullPath).isFile() || fs.statSync(fullPath).size === 0) {
      fail(`Runtime capability probe output '${filename}' is missing or empty.`)
    }
  }
}

function readInputs(resultsPath, profileIds, profileDirectory) {
  const { value: results } = readBoundedJson(resultsPath, 'Functional result')
  if (results.schemaVersion !== 1 || !Array.isArray(results.rows)) {
    fail('Functional result must use schema version 1 with a rows array.')
  }
  const byId = new Map(results.rows.map(row => [row.profileId, row]))
  const rows = []
  for (const profileId of profileIds) {
    const resultRow = byId.get(profileId)
    if (!isObject(resultRow)) fail(`Functional result has no row '${profileId}'.`)
    if (!imageIdPattern.test(resultRow.image?.imageId ?? '')) {
      fail(`Functional result row '${profileId}' has no immutable local image ID.`)
    }
    if (!sha256Pattern.test(resultRow.profileSha256 ?? '')) {
      fail(`Functional result row '${profileId}' has no valid profile SHA-256.`)
    }
    const profileDocument = readBoundedJson(
      path.join(profileDirectory, `${profileId}.json`),
      `Runtime profile '${profileId}'`,
    )
    const profile = profileDocument.value
    const profileSha256 = sha256(profileDocument.bytes)
    if (profileSha256 !== resultRow.profileSha256) {
      fail(
        `Runtime profile '${profileId}' digest '${profileSha256}' disagrees with the ` +
        `functional result '${resultRow.profileSha256}'. Refresh the inventory first.`,
      )
    }
    if (profile.id !== profileId || profile.image !== resultRow.candidateImage) {
      fail(`Runtime profile '${profileId}' disagrees with the functional result row.`)
    }
    rows.push({ profile, resultRow })
  }
  return { results, rows }
}

function writeJsonAtomically(filename, value) {
  fs.mkdirSync(path.dirname(filename), { recursive: true })
  const temporary = path.join(path.dirname(filename), `.${path.basename(filename)}.${process.pid}.${Date.now()}.tmp`)
  try {
    fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' })
    fs.renameSync(temporary, filename)
  } finally {
    fs.rmSync(temporary, { force: true })
  }
}

function updateVerification(resultRow, profile, success, exception, sandbox, now) {
  const previous = isObject(resultRow.verification) ? resultRow.verification : {}
  const smoke = isObject(previous.smoke) ? previous.smoke : {}
  const artifactPipeline = previous.evidence?.artifactPipeline
  const artifactEvidenceMatches = isObject(artifactPipeline) &&
    artifactPipeline.imageId === resultRow.image.imageId &&
    artifactPipeline.profileSha256 === resultRow.profileSha256 &&
    artifactPipeline.referenceSetId === resultRow.referenceSetId
  const updatedSmoke = {
      runtimeIdentity: 'passed',
      compile: artifactEvidenceMatches && artifactPipeline.compilePassed === true
        ? 'passed'
        : 'unverified',
      run: 'passed',
      ilDecompile: artifactEvidenceMatches &&
        artifactPipeline.ilPassed === true &&
        artifactPipeline.decompiledCSharpPassed === true
        ? 'passed'
        : 'unverified',
      jit: resultRow.expected.capabilities.includes('jit-asm')
        ? smoke.jit ?? 'unverified'
        : 'not-applicable',
      mapping: resultRow.expected.sourceMappingKind === 'none'
        ? 'not-applicable'
        : smoke.mapping ?? 'unverified',
  }
  const pending = Object.entries(updatedSmoke).filter(([, status]) => status !== 'passed' && status !== 'not-applicable').map(([name]) => name)
  resultRow.verification = {
    ...previous,
    status: pending.length === 0 ? 'smoke-passed' : 'runtime-smoke-passed',
    reason: pending.length === 0 ? null : `${pending.join('-')}-pending`,
    smoke: updatedSmoke,
    evidence: {
      ...(isObject(previous.evidence) ? previous.evidence : {}),
      directRun: {
        observedAt: now.toISOString(),
        imageId: resultRow.image.imageId,
        profileSha256: resultRow.profileSha256,
        probeTargetFramework: 'netcoreapp2.0',
        sandbox: {
          networkMode: 'none',
          ipcMode: 'none',
          readOnlyRootFileSystem: true,
          noNewPrivileges: true,
          capabilitiesDropped: 'all',
          user: profile.container.executionUser,
          seccompSha256: sandbox.seccompSha256,
          openFilesSoftLimit: sandbox.openFilesSoftLimit,
          openFilesHardLimit: sandbox.openFilesHardLimit,
        },
        success,
        exception: exception ?? null,
      },
    },
  }
}

export function runFunctionalSmokes(options) {
  const {
    profileIds,
    exceptionProfileIds = [],
    resultsPath = defaultResultsPath,
    spawn = spawnSync,
    now = () => new Date(),
    cwd = repositoryRoot,
    env = process.env,
    profileDirectory = candidateDirectory,
    probeProjectPath = probeProject,
    probeOutputPath = probeOutput,
    sandbox = readSandbox(),
  } = options
  if (!Array.isArray(profileIds) || profileIds.length === 0 ||
      new Set(profileIds).size !== profileIds.length ||
      profileIds.some(id => !profileIdPattern.test(id))) {
    fail('Smoke profile IDs must be a non-empty unique list of safe IDs.')
  }
  const exceptionSet = new Set(exceptionProfileIds)
  if ([...exceptionSet].some(id => !profileIds.includes(id))) {
    fail('Every exception profile must also be selected for smoke testing.')
  }
  const absoluteResultsPath = path.resolve(resultsPath)
  const { results, rows } = readInputs(absoluteResultsPath, profileIds, path.resolve(profileDirectory))
  ensureProbe(spawn, { cwd, env }, path.resolve(probeProjectPath), path.resolve(probeOutputPath))
  const summaries = []
  for (const { profile, resultRow } of rows) {
    const policy = profile.securityPolicies.find(value => value.id === 'runtime-job-default')
    const containerName = `sln-functional-${profile.id}-${process.pid}-${crypto.randomBytes(4).toString('hex')}`
    const processOptions = {
      cwd,
      env,
      onTimeout: () => spawn('docker', ['rm', '--force', containerName], {
        cwd,
        env,
        encoding: 'utf8',
        shell: false,
        maxBuffer: maximumDockerOutputBytes,
        timeout: 10_000,
        killSignal: 'SIGKILL',
      }),
    }
    const successResult = runProcess(
      spawn,
      'docker',
      dockerArguments(
        profile,
        resultRow,
        'success-security',
        sandbox,
        path.resolve(probeOutputPath),
        containerName,
      ),
      processOptions,
      `Runtime smoke '${profile.id}'`,
      [0],
      policy.maximumDurationSeconds * 1000 + 5000,
    )
    const success = validateSuccessFrames(parseRuntimeFrameLog(successResult.stdout))
    let exception
    if (exceptionSet.has(profile.id)) {
      const exceptionResult = runProcess(
        spawn,
        'docker',
        dockerArguments(
          profile,
          resultRow,
          'user-exception',
          sandbox,
          path.resolve(probeOutputPath),
          `${containerName}-exception`,
        ),
        {
          ...processOptions,
          onTimeout: () => spawn('docker', ['rm', '--force', `${containerName}-exception`], {
            cwd,
            env,
            encoding: 'utf8',
            shell: false,
            maxBuffer: maximumDockerOutputBytes,
            timeout: 10_000,
            killSignal: 'SIGKILL',
          }),
        },
        `Runtime exception smoke '${profile.id}'`,
        [1],
        policy.maximumDurationSeconds * 1000 + 5000,
      )
      exception = validateExceptionFrames(parseRuntimeFrameLog(exceptionResult.stdout))
    }
    updateVerification(resultRow, profile, success, exception, sandbox, now())
    summaries.push({
      profileId: profile.id,
      imageId: resultRow.image.imageId,
      runtimeElapsedMilliseconds: success.runtimeElapsedMilliseconds,
      exceptionValidated: exception !== undefined,
    })
  }
  results.verificationRefreshedAt = now().toISOString()
  writeJsonAtomically(absoluteResultsPath, results)
  return summaries
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  const profileIds = []
  const exceptionProfileIds = []
  let resultsPath
  for (let index = 0; index < argv.length; index++) {
    const option = argv[index]
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail(`${option} requires a value.`)
    if (option === '--profile') profileIds.push(value)
    else if (option === '--exception-profile') exceptionProfileIds.push(value)
    else if (option === '--results' && resultsPath === undefined) resultsPath = value
    else fail(`Unknown or duplicate option '${option}'.`)
  }
  return { profileIds, exceptionProfileIds, resultsPath }
}

export function runRuntimeFunctionalSmoke(argv, options = {}) {
  const output = options.output ?? console
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) {
      output.log(runtimeFunctionalSmokeUsage)
      return 0
    }
    const summaries = runFunctionalSmokes({
      ...options,
      profileIds: parsed.profileIds,
      exceptionProfileIds: parsed.exceptionProfileIds,
      resultsPath: parsed.resultsPath ?? options.resultsPath,
      output: undefined,
    })
    for (const summary of summaries) {
      output.log(
        `${summary.profileId}: Run passed in ` +
        `${summary.runtimeElapsedMilliseconds.toFixed(1)} ms` +
        (summary.exceptionValidated ? '; nested exception passed' : ''),
      )
    }
    return 0
  } catch (error) {
    output.error(`runtime functional smoke error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimeFunctionalSmoke(process.argv.slice(2))
}
