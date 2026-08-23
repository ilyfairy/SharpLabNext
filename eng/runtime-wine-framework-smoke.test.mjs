import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { parseWineFrameworkFrameLog, runWineFrameworkSmokes, runRuntimeWineFrameworkSmokeCli } from './runtime-wine-framework-smoke.mjs'

const imageId = `sha256:${'a'.repeat(64)}`
const sourceRevision = '0123456789abcdef0123456789abcdef01234567'
const frameworkSourceRevision = 'fedcba9876543210fedcba9876543210fedcba98'
const parentImage = `registry.example/framework-parent@sha256:${'b'.repeat(64)}`
const operatorImage = `registry.example/netfx20@sha256:${'c'.repeat(64)}`
const methodFilter = 'SharpLabNext.RuntimeCapabilityProbe.Program.WindowsAbi'
function frame(kind, sequence, payload) { const content = Buffer.from(typeof payload === 'string' ? payload : JSON.stringify(payload)); const bytes = Buffer.alloc(18 + content.length); bytes.write('SLNR'); bytes[4] = 1; bytes[5] = kind; bytes.writeBigInt64LE(BigInt(sequence), 6); bytes.writeInt32LE(content.length, 14); content.copy(bytes, 18); return bytes.toString('base64') }
function runLog() { return [frame(1, 1, 'SLN-CAPABILITY-STDOUT-V1\nSLN-CAPABILITY-NETWORK-BLOCKED-V1\nSLN-CAPABILITY-ROOTFS-READONLY-V1\n'), frame(2, 2, 'SLN-CAPABILITY-STDERR-V1\n'), frame(7, 3, { Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 7 })].join('\n') + '\n' }
function argumentsLog(marker = 'SLN-CAPABILITY-ARGUMENTS-V1') { return [frame(1, 1, `${marker}\n`), frame(7, 2, { Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 8 })].join('\n') + '\n' }
function exceptionLog(options = {}) { return [frame(6, 1, { TypeName: 'System.InvalidOperationException', Message: 'outer capability probe failure', StackTrace: options.outerStackTrace ?? 'at Program.ThrowNestedException()', InnerException: { TypeName: 'System.ArgumentException', Message: 'inner capability probe failure', StackTrace: options.innerStackTrace ?? 'at Program.ThrowNestedException()' } }), frame(7, 2, { Status: options.status ?? 'user-exception', ExitCode: options.exitCode ?? 1, ElapsedMilliseconds: 9 })].join('\n') + '\n' }
function nonZeroLog(options = {}) { return [frame(1, 1, 'SLN-CAPABILITY-NONZERO-V1\n'), frame(7, 2, { Status: options.status ?? 'non-zero-exit', ExitCode: options.exitCode ?? 23, ElapsedMilliseconds: 10 })].join('\n') + '\n' }
function jitLog(options = {}) {
  const assembly = options.assembly ?? `; Assembly listing for method ${methodFilter}\n; Desktop CLR version 2.0.50727.42\n; Native address 0x1000\nG_M000_IG00:\n       L0000: lea rax,[rcx+rdx]\n; Total bytes of code 4`
  const method = {
    Method: '0x06000001', DisplayName: options.displayName ?? methodFilter,
    Status: 'prepared', Address: '0x1000', Error: null,
    NativeCodeSize: options.nativeCodeSize ?? 4,
    InstructionCount: options.instructionCount ?? 1,
    LinkedRanges: options.linkedRanges ?? [], MappingSource: options.mappingSource ?? 'none',
  }
  return [
    frame(9, 1, assembly),
    frame(10, 2, { RuntimeVersion: options.runtimeVersion ?? '2.0.50727.42', Assembly: 'SharpLabNext.RuntimeCapabilityProbe', MethodFilter: options.methodFilter ?? methodFilter, Methods: [method] }),
    frame(7, 3, { Status: options.status ?? 'completed', ExitCode: options.exitCode ?? 0, ElapsedMilliseconds: 11 }),
  ].join('\n') + '\n'
}
function fixture(t, options = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-wine-framework-smoke-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const id = options.id ?? 'wine-netfx20-linux-x64', targetId = id.slice('wine-'.length, -'-linux-x64'.length), version = options.version ?? '2.0', tfm = `net${targetId.slice('netfx'.length)}`
  const profileDirectory = path.join(directory, 'profiles'), probeOutputPath = path.join(directory, 'probe'); fs.mkdirSync(profileDirectory); fs.mkdirSync(probeOutputPath)
  for (const file of ['SharpLabNext.RuntimeCapabilityProbe.exe', 'SharpLabNext.RuntimeCapabilityProbe.pdb']) fs.writeFileSync(path.join(probeOutputPath, file), 'fixture')
  const profile = { schemaVersion: 1, id, image: `sharplabnext/runtime-${id}:candidate`, family: options.family ?? 'netfx-clr-wine', runtimeVersion: version, capabilities: ['run', 'jit-asm'], acceptedFrameworks: [{ name: '.NETFramework', exactVersion: version }], container: { isolationKind: 'wine', environmentKind: 'wine', executionUser: '0:0', winePrefixPath: '/opt/wine-netfx-clr2' }, layout: { runnerKind: 'wine-netfx', winePrefixPath: '/opt/wine-netfx-clr2', wineHostPath: '/usr/lib/wine/wine64', jitInspectorAssemblyPath: '/opt/sharplabnext/SharpLabNext.WineRunner.dll' }, operations: { run: { implementationId: 'sharplabnext-target-runtime-runner-v1', pathStyle: 'wine-z', command: { executable: '/usr/lib/wine/wine64', argv: ['Z:\\opt\\sharplabnext\\SharpLabNext.TargetRuntimeRunner.exe', 'run', '{entryAssembly}', '--', '{arguments}'] } }, jit: { implementationId: 'sharplabnext-desktop-clr-jit-inspector-v1', pathStyle: 'unix', sourceMappingKind: 'none', command: { executable: '/usr/share/dotnet/dotnet', argv: ['/opt/sharplabnext/SharpLabNext.WineRunner.dll', 'desktop-jit', '{entryAssembly}', '{methodFilter}'] } } }, securityPolicies: [{ id: 'runtime-job-wine-netfx', memoryBytes: 1024, nanoCpus: 1000000000, pidsLimit: 128, maximumDurationSeconds: 10, maximumOutputBytes: 1024, tmpfsBytes: 1024 }] }
  const profileBytes = Buffer.from(`${JSON.stringify(profile, null, 2)}\n`); fs.writeFileSync(path.join(profileDirectory, `${id}.json`), profileBytes)
  const matrix = { framework: { targets: [{ id: targetId, version, targetFramework: tfm, referenceSetId: `${targetId}-managed-ref`, clrGeneration: 'clr2' }] } }, matrixBytes = Buffer.from(`${JSON.stringify(matrix, null, 2)}\n`), runtimeMatrixPath = path.join(directory, 'runtime-matrix.json'); fs.writeFileSync(runtimeMatrixPath, matrixBytes)
  const labels = { 'com.sharplabnext.runtime-candidate': 'true', 'com.sharplabnext.runtime-profile': id, 'io.sharplabnext.runtime.environment': 'wine-netfx', 'io.sharplabnext.runtime.framework-version': version, 'io.sharplabnext.framework.target-id': targetId, 'io.sharplabnext.framework.matrix-selector': 'true', 'io.sharplabnext.framework.selector': '/opt/sharplabnext/.framework-selector.json', 'io.sharplabnext.source.revision': sourceRevision, 'org.opencontainers.image.revision': sourceRevision, 'io.sharplabnext.framework.source-revision': options.frameworkSourceRevision ?? frameworkSourceRevision, 'io.sharplabnext.framework.matrix-parent': parentImage, 'io.sharplabnext.framework.row-operator-image': operatorImage, 'io.sharplabnext.framework.row-digest': operatorImage.slice(operatorImage.lastIndexOf('@') + 1), 'io.sharplabnext.runtime.component-digest': operatorImage.slice(operatorImage.lastIndexOf('@') + 1), 'io.sharplabnext.runtime.component-source-uri': `docker://${operatorImage}` }
  const resultsPath = path.join(directory, 'results.json')
  const results = {
    schemaVersion: 1,
    runtimeMatrixSha256: `sha256:${crypto.createHash('sha256').update(matrixBytes).digest('hex')}`,
    rows: [{
      profileId: id, matrixTargetId: targetId, runtimeVersion: version, candidateImage: profile.image,
      profileSha256: `sha256:${crypto.createHash('sha256').update(profileBytes).digest('hex')}`,
      referenceSetId: `${targetId}-managed-ref`,
      expected: { runImplementationId: 'sharplabnext-target-runtime-runner-v1', jitImplementationId: 'sharplabnext-desktop-clr-jit-inspector-v1', sourceMappingKind: 'none' },
      image: { imageId, labels },
      verification: {
        status: 'unverified',
        smoke: { runtimeIdentity: 'unverified', compile: 'passed', run: 'unverified', ilDecompile: 'passed', jit: 'unverified', mapping: 'not-applicable' },
        evidence: { artifactPipeline: { retained: true } },
      },
    }],
  }
  fs.writeFileSync(resultsPath, `${JSON.stringify(results, null, 2)}\n`)
  return { directory, id, resultsPath, runtimeMatrixPath, profileDirectory, probeOutputPath, sandbox: { seccompPath: path.join(directory, 'seccomp.json'), seccompSha256: `sha256:${'d'.repeat(64)}`, openFilesSoftLimit: options.openFilesSoftLimit ?? 256, openFilesHardLimit: options.openFilesHardLimit ?? 256 } }
}
function smoke(t, options = {}) {
  const value = fixture(t, options), calls = []
  let wall = 100
  const smokeOptions = {
    profileIds: [value.id], representative: options.representative ?? false, resultsPath: value.resultsPath,
    runtimeMatrixPath: value.runtimeMatrixPath, profileDirectory: value.profileDirectory,
    probeProjectPath: path.join(value.directory, 'probe.csproj'), probeOutputPath: value.probeOutputPath,
    sandbox: value.sandbox, now: () => new Date('2026-08-13T08:00:00.000Z'), wallClock: () => wall += 5,
    spawn(command, argv, spawnOptions) {
      const call = { command, argv, spawnOptions }
      if (command === 'docker' && argv[0] === 'run') {
        const mount = argv[argv.indexOf('--mount') + 1]
        const workspace = mount.slice('type=bind,source='.length, mount.indexOf(',target='))
        call.readyContents = fs.readFileSync(path.join(workspace, '.sharplabnext', 'ready'), 'utf8')
        if (argv.includes('--entrypoint')) call.representativeWrapper = fs.readFileSync(path.join(workspace, '.sharplabnext', 'representative-wrapper.sh'), 'utf8')
      }
      calls.push(call)
      if (command === 'docker' && argv[0] === 'image') return { status: 0, stdout: `${options.inspectedImageId ?? imageId}\n`, stderr: '' }
      if (command === 'dotnet' || argv[0] === 'rm') return { status: 0, stdout: '', stderr: '' }
      if (options.failure) return { status: 0, stdout: 'not-a-frame\n', stderr: '' }
      const peakMemory = argv.includes('--entrypoint') ? (options.peakMemory ?? '768') : ''
      const stderr = peakMemory === 'unsupported' ? 'SLN-CAPABILITY-CGROUP-V2-MEMORY-PEAK-UNSUPPORTED-V1=cgroup-v2-memory-peak-unavailable\n' : peakMemory.length > 0 ? `SLN-CAPABILITY-CGROUP-V2-MEMORY-PEAK-V1=${peakMemory}\n` : ''
      if (argv.includes('arguments-forwarding')) return { status: 0, stdout: argumentsLog(options.argumentMarker), stderr }
      if (argv.includes('non-zero-return')) return { status: options.nonZeroProcessExitCode ?? 23, stdout: nonZeroLog(options.nonZero), stderr }
      if (argv.includes('user-exception')) return { status: options.exceptionExitCode ?? 1, stdout: exceptionLog(options.exception), stderr }
      if (argv.includes('desktop-jit')) return { status: options.jitProcessExitCode ?? 0, stdout: jitLog(options.jit), stderr }
      return { status: 0, stdout: runLog(), stderr }
    },
  }
  return { ...value, calls, invoke: () => runWineFrameworkSmokes(smokeOptions), cli: (argv, output) => runRuntimeWineFrameworkSmokeCli(argv, { ...smokeOptions, output }) }
}

test('Wine Framework smoke runs exact Run and Desktop CLR JIT commands in the Wine security shape', t => {
  const value = smoke(t), summary = value.invoke()[0]; assert.equal(summary.runtimeElapsedMilliseconds, 7)
  const inspection = value.calls.find(call => call.command === 'docker' && call.argv[0] === 'image'); assert.deepEqual(inspection.argv, ['image', 'inspect', '--format', '{{.Id}}', `sharplabnext/runtime-${value.id}:candidate`])
  const dockerRuns = value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'run')
  const run = dockerRuns.find(call => call.argv.includes('success-security')); assert.ok(run.argv.includes(imageId)); assert.ok(run.argv.includes('WINEPREFIX=/opt/wine-netfx-clr2')); assert.ok(run.argv.includes('SHARPLABNEXT_PREPARE_WINE_XDG_RUNTIME_DIR=1')); assert.ok(run.argv.includes('SHARPLABNEXT_WINE_CLEANUP=1')); assert.ok(run.argv.includes('nofile=512:512')); assert.ok(run.argv.includes('no-new-privileges=true')); assert.ok(run.argv.includes('ALL')); assert.ok(run.argv.includes('Z:\\workspace\\SharpLabNext.RuntimeCapabilityProbe.exe')); assert.ok(run.argv.includes('/tmp:rw,exec,nosuid,nodev,size=1024,uid=0,gid=0,mode=1777')); assert.equal(run.spawnOptions.timeout, 15000)
  const jit = dockerRuns.find(call => call.argv.includes('desktop-jit')); assert.ok(jit.argv.includes('/usr/share/dotnet/dotnet')); assert.ok(jit.argv.includes('/workspace/SharpLabNext.RuntimeCapabilityProbe.exe')); assert.ok(jit.argv.includes(methodFilter)); assert.ok(jit.argv.includes('WINEPREFIX=/opt/wine-netfx-clr2'))
  const mount = run.argv[run.argv.indexOf('--mount') + 1], workspace = mount.slice('type=bind,source='.length, mount.indexOf(',target=')); assert.match(mount, /^type=bind,source=.+,target=\/workspace,readonly$/)
  assert.equal(run.readyContents, 'ready\n'); assert.equal(fs.existsSync(workspace), false)
  const saved = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')).rows[0].verification; assert.equal(saved.smoke.runtimeIdentity, 'passed'); assert.equal(saved.smoke.run, 'passed'); assert.equal(saved.smoke.jit, 'passed'); assert.deepEqual(saved.evidence.artifactPipeline, { retained: true }); assert.equal(saved.evidence.wineFramework.frameworkVersion, '2.0'); assert.equal(saved.evidence.wineFramework.jit.implementationId, 'sharplabnext-desktop-clr-jit-inspector-v1'); assert.equal(saved.evidence.wineFramework.jit.sourceMappingKind, 'none'); assert.equal(saved.evidence.wineFramework.jit.nativeCodeSize, 4); assert.deepEqual(saved.evidence.wineFramework.jit.abi, ['rcx', 'rdx', 'rax/eax']); assert.deepEqual(saved.evidence.wineFramework.sandbox.openFiles, { configured: { softLimit: 256, hardLimit: 256 }, effective: { softLimit: 512, hardLimit: 512 } }); assert.equal(Object.hasOwn(saved.evidence.wineFramework, 'representative'), false); assert.equal(dockerRuns.length, 2)
})
test('Wine Framework smoke preserves configured Wine nofile limits above the 512 minimum', t => {
  const value = smoke(t, { openFilesSoftLimit: 768, openFilesHardLimit: 1024 })
  value.invoke()
  const run = value.calls.find(call => call.command === 'docker' && call.argv[0] === 'run')
  assert.ok(run.argv.includes('nofile=768:1024'))
  const saved = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')).rows[0].verification.evidence.wineFramework.sandbox.openFiles
  assert.deepEqual(saved, { configured: { softLimit: 768, hardLimit: 1024 }, effective: { softLimit: 768, hardLimit: 1024 } })
})
test('Wine Framework representative smoke exercises cold and warm execution, forwarded arguments, and nested exceptions', t => {
  const value = smoke(t, { representative: true }), summary = value.invoke()[0]
  assert.equal(summary.runtimeElapsedMilliseconds, 7)
  const runs = value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'run')
  const runOperations = runs.filter(call => !call.argv.includes('desktop-jit'))
  assert.equal(runs.length, 7)
  assert.equal(runs.filter(call => call.argv.includes('desktop-jit')).length, 1)
  assert.deepEqual(runOperations.map(call => call.argv.slice(call.argv.indexOf('--') + 1)), [
    ['success-security'], ['success-security'], ['success-security'], ['arguments-forwarding', 'SLN-CAPABILITY-ARGUMENTS-V1'], ['non-zero-return'], ['user-exception'],
  ])
  const exact = runOperations[0]; assert.equal(exact.argv.includes('--entrypoint'), false)
  for (const run of runOperations.slice(1)) { assert.ok(run.argv.includes(imageId)); assert.ok(run.argv.includes('no-new-privileges=true')); assert.ok(run.argv.includes('ALL')); assert.ok(run.argv.includes('WINEPREFIX=/opt/wine-netfx-clr2')); assert.deepEqual(run.argv.slice(run.argv.indexOf('--entrypoint'), run.argv.indexOf('--entrypoint') + 3), ['--entrypoint', '/bin/sh', imageId]); assert.match(run.representativeWrapper, /\/opt\/sharplabnext\/runtime-entrypoint\.sh "\$@"/); assert.match(run.representativeWrapper, /memory\.peak/) }
  const saved = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')).rows[0].verification.evidence.wineFramework.representative
  assert.deepEqual([saved.cold.runtimeElapsedMilliseconds, saved.warm.runtimeElapsedMilliseconds], [7, 7]); assert.deepEqual([saved.cold.wallElapsedMilliseconds, saved.warm.wallElapsedMilliseconds], [5, 5]); assert.equal(saved.cold.containerPeakMemoryBytes, 768); assert.equal(saved.cold.containerPeakMemoryAvailability, 'measured: cgroup-v2 memory.peak')
  assert.equal(saved.arguments.runtimeElapsedMilliseconds, 8); assert.equal(saved.exception.runtimeElapsedMilliseconds, 9); assert.equal(saved.exception.outerType, 'System.InvalidOperationException'); assert.equal(saved.exception.outerMessage, 'outer capability probe failure'); assert.equal(saved.exception.innerType, 'System.ArgumentException'); assert.equal(saved.exception.innerMessage, 'inner capability probe failure'); assert.match(saved.exception.outerStackTrace, /ThrowNestedException/); assert.match(saved.exception.innerStackTrace, /ThrowNestedException/); assert.equal(saved.exception.exitCode, 1)
  assert.equal(saved.nonZeroReturn.runtimeElapsedMilliseconds, 10); assert.equal(saved.nonZeroReturn.exitCode, 23)
})
test('Wine Framework representative smoke requires the canonical non-zero terminal status', t => {
  const value = smoke(t, { representative: true, nonZero: { status: 'completed' } })
  const before = fs.readFileSync(value.resultsPath, 'utf8')
  assert.throws(value.invoke, /expected output and exit state/)
  assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
})
test('Wine Framework accepts distinct candidate and immutable Framework source revisions', t => {
  const value = smoke(t), summary = value.invoke()[0]
  assert.equal(summary.profileId, value.id)
  assert.notEqual(sourceRevision, frameworkSourceRevision)
})
test('Wine Framework stale candidate tag rejects before probe build or container run', t => {
  const value = smoke(t, { inspectedImageId: `sha256:${'e'.repeat(64)}` }), before = fs.readFileSync(value.resultsPath, 'utf8')
  assert.throws(value.invoke, /not recorded image ID/)
  assert.deepEqual(value.calls.map(call => [call.command, call.argv[0]]), [['docker', 'image']])
  assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
})
test('Wine Framework rejects wrong family, version, and image/profile binding before any process', t => {
  for (const mutation of ['family', 'version', 'profile', 'image']) {
    const value = fixture(t); const result = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8'))
    if (mutation === 'family') { const profile = JSON.parse(fs.readFileSync(path.join(value.profileDirectory, `${value.id}.json`), 'utf8')); profile.family = 'coreclr-wine'; const bytes = Buffer.from(`${JSON.stringify(profile)}\n`); fs.writeFileSync(path.join(value.profileDirectory, `${value.id}.json`), bytes); result.rows[0].profileSha256 = `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}` }
    if (mutation === 'version') result.rows[0].image.labels['io.sharplabnext.runtime.framework-version'] = '4.8'
    if (mutation === 'profile') result.rows[0].profileSha256 = `sha256:${'f'.repeat(64)}`
    if (mutation === 'image') result.rows[0].image.labels['com.sharplabnext.runtime-profile'] = 'wine-netfx48-linux-x64'
    fs.writeFileSync(value.resultsPath, `${JSON.stringify(result, null, 2)}\n`); let calls = 0
    assert.throws(() => runWineFrameworkSmokes({ profileIds: [value.id], resultsPath: value.resultsPath, runtimeMatrixPath: value.runtimeMatrixPath, profileDirectory: value.profileDirectory, probeOutputPath: value.probeOutputPath, sandbox: value.sandbox, spawn() { calls++; return { status: 0 } } }), /binding|contract|identity/); assert.equal(calls, 0)
  }
})
test('Wine Framework rejects a malformed immutable Framework source revision before any process', t => {
  const value = fixture(t, { frameworkSourceRevision: 'not-a-commit' }), before = fs.readFileSync(value.resultsPath, 'utf8'); let calls = 0
  assert.throws(() => runWineFrameworkSmokes({ profileIds: [value.id], resultsPath: value.resultsPath, runtimeMatrixPath: value.runtimeMatrixPath, profileDirectory: value.profileDirectory, probeOutputPath: value.probeOutputPath, sandbox: value.sandbox, spawn() { calls++; return { status: 0 } } }), /exact selected Framework identity/)
  assert.equal(calls, 0); assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
})
test('Wine Framework frame failure leaves the result document unchanged', t => { const value = smoke(t, { failure: true }), before = fs.readFileSync(value.resultsPath, 'utf8'); assert.throws(value.invoke, /non-canonical/); assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before) })
test('Wine Framework rejects fabricated JIT mapping and leaves the result unchanged', t => {
  const value = smoke(t, { jit: { mappingSource: 'rich', linkedRanges: [{ Start: 0, End: 4 }] } })
  const before = fs.readFileSync(value.resultsPath, 'utf8')
  assert.throws(value.invoke, /no prepared WindowsAbi method/)
  assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
})
test('Wine Framework representative failures leave the result document unchanged', t => {
  for (const options of [{ representative: true, argumentMarker: 'wrong-marker' }, { representative: true, nonZero: { exitCode: 22 } }, { representative: true, exception: { innerStackTrace: 'at Other.Method()' } }, { representative: true, exception: { status: 'completed' } }, { representative: true, peakMemory: '' }, { representative: true, peakMemory: '0' }, { representative: true, peakMemory: '1025' }, { representative: true, peakMemory: '9007199254740992' }, { representative: true, peakMemory: '768\nSLN-CAPABILITY-CGROUP-V2-MEMORY-PEAK-V1=767' }]) {
    const value = smoke(t, options), before = fs.readFileSync(value.resultsPath, 'utf8')
    assert.throws(value.invoke, /missing|nested exception|expected output and exit state|peak-memory/)
    assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
  }
})
test('Wine Framework representative smoke records cgroup v2 peak-memory unsupported honestly', t => {
  const value = smoke(t, { representative: true, peakMemory: 'unsupported' })
  value.invoke()
  const saved = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')).rows[0].verification.evidence.wineFramework.representative
  assert.equal(saved.cold.containerPeakMemoryBytes, null)
  assert.equal(saved.cold.containerPeakMemoryAvailability, 'unsupported: cgroup-v2 memory.peak is unavailable')
})
test('Wine Framework parser and CLI reject invalid protocol and unsupported profiles', t => {
  assert.throws(() => parseWineFrameworkFrameLog('bad\n'), /non-canonical/)
  assert.throws(() => parseWineFrameworkFrameLog(`${frame(7, 2, {})}\n`), /sequence/)
  const value = fixture(t), errors = [], output = { log() {}, error(message) { errors.push(message) } }
  assert.equal(runRuntimeWineFrameworkSmokeCli(['--profile', 'wine-dotnet-8-linux-x64'], { output, sandbox: value.sandbox }), 1)
  assert.match(errors.pop(), /profile IDs/)
  assert.equal(runRuntimeWineFrameworkSmokeCli(['--representative', '--profile', 'wine-dotnet-8-linux-x64'], { output, sandbox: value.sandbox }), 1)
  assert.match(errors.pop(), /profile IDs/)
  assert.equal(runRuntimeWineFrameworkSmokeCli(['--representative', '--representative', '--profile', value.id], { output, sandbox: value.sandbox }), 1)
  assert.match(errors.pop(), /duplicate/)
  const representative = smoke(t, { representative: true }), logs = []
  assert.equal(representative.cli(['--representative', '--profile', representative.id], { log(message) { logs.push(message) }, error(message) { errors.push(message) } }), 0)
  assert.match(logs[0], /representative checks passed/)
  assert.equal(representative.calls.filter(call => call.command === 'docker' && call.argv[0] === 'run').length, 7)
})
