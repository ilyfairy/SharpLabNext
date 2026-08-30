import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { parseWineCoreClrFrameLog, runWineCoreClrSmokes, runRuntimeWineCoreClrSmokeCli } from '../smoke/runtime-wine-coreclr-smoke.mjs'

const imageId = `sha256:${'a'.repeat(64)}`
const method = 'SharpLabNext.RuntimeCapabilityProbe.Program.WindowsAbi'
const runtimeCommit = 'fedcba9876543210fedcba9876543210fedcba98'
const payloadSha512 = 'c'.repeat(128)
function frame(kind, sequence, payload) { const content = Buffer.from(typeof payload === 'string' ? payload : JSON.stringify(payload)); const bytes = Buffer.alloc(18 + content.length); bytes.write('SLNR'); bytes[4] = 1; bytes[5] = kind; bytes.writeBigInt64LE(BigInt(sequence), 6); bytes.writeInt32LE(content.length, 14); content.copy(bytes, 18); return bytes.toString('base64') }
function runLog() { return [frame(1, 1, 'SLN-CAPABILITY-STDOUT-V1\nSLN-CAPABILITY-NETWORK-BLOCKED-V1\nSLN-CAPABILITY-ROOTFS-READONLY-V1\n'), frame(2, 2, 'SLN-CAPABILITY-STDERR-V1\n'), frame(7, 3, { Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 4 })].join('\n') + '\n' }
function jitLog(options = {}) { return [frame(9, 1, options.assembly ?? `; Assembly listing for method SharpLabNext.RuntimeCapabilityProbe.Program:WindowsAbi(long,long):long\nmov rax, rcx\nadd rax, rdx\nret\n; Total bytes of code 12\n`), frame(10, 2, { MethodFilter: method, Methods: [{ Method: method, DisplayName: method, Status: 'prepared', NativeCodeSize: 12, InstructionCount: 3 }] }), frame(7, 3, { Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 6 })].join('\n') + '\n' }
function fixture(t, version = '7.0.20', options = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-wine-coreclr-smoke-')); t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const profileDirectory = path.join(directory, 'profiles'), probeOutputPath = path.join(directory, 'probe'); fs.mkdirSync(profileDirectory); fs.mkdirSync(probeOutputPath)
  for (const file of ['SharpLabNext.RuntimeCapabilityProbe.dll', 'SharpLabNext.RuntimeCapabilityProbe.pdb', 'SharpLabNext.RuntimeCapabilityProbe.deps.json', 'SharpLabNext.RuntimeCapabilityProbe.runtimeconfig.json']) fs.writeFileSync(path.join(probeOutputPath, file), 'fixture')
  const major = version.split('.')[0], targetId = `dotnet-${major === '11' ? '11-preview' : major}`, id = `wine-${targetId}-linux-x64`, hasJit = !['5', '6'].includes(major)
  const command = operation => ({ implementationId: 'sharplabnext-legacy-jit-inspector-v1', pathStyle: 'wine-z', command: { executable: '/usr/lib/wine/wine64', argv: ['Z:\\opt\\wine-dotnet\\drive_c\\dotnet\\dotnet.exe', 'exec', '--fx-version', version, 'Z:\\opt\\sharplabnext\\SharpLabNext.LegacyJitInspector.dll', '--runtime-version', version, operation, operation === 'run' ? '{entryAssembly}' : '{entryAssembly}', ...(operation === 'run' ? ['--', '{arguments}'] : ['{methodFilter}'])] }, ...(operation === 'jit' ? { sourceMappingKind: 'none' } : {}) })
  const profile = { schemaVersion: 1, id, image: `sharplabnext/runtime-${id}:candidate`, family: 'coreclr-wine', runtimeVersion: version, runtimeCommit, jitCommit: runtimeCommit, capabilities: hasJit ? ['run', 'jit-asm'] : ['run'], container: { isolationKind: 'wine', environmentKind: 'wine', executionUser: '1654:1654', winePrefixPath: '/opt/wine-dotnet' }, layout: { runnerKind: 'wine-coreclr', winePrefixPath: '/opt/wine-dotnet', wineHostPath: '/usr/lib/wine/wine64', dotNetHostPath: '/opt/wine-dotnet/drive_c/dotnet/dotnet.exe' }, operations: { run: command('run'), ...(hasJit ? { jit: command('jit') } : {}) }, securityPolicies: [{ id: 'runtime-job-default', memoryBytes: 1024, nanoCpus: 1000000000, pidsLimit: 64, maximumDurationSeconds: 10, maximumOutputBytes: 1024, tmpfsBytes: 1024 }] }
  const bytes = Buffer.from(`${JSON.stringify(profile, null, 2)}\n`); fs.writeFileSync(path.join(profileDirectory, `${id}.json`), bytes)
  const resultsPath = path.join(directory, 'results.json'), source = options.source ?? '0123456789abcdef0123456789abcdef01234567', context = options.context ?? 'working-tree-development'
  const result = { schemaVersion: 1, rows: [{
    profileId: id, matrixTargetId: targetId, candidateImage: profile.image,
    profileSha256: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`,
    referenceSetId: 'netcoreapp2.0-ref',
    expected: { runImplementationId: 'sharplabnext-legacy-jit-inspector-v1', jitImplementationId: hasJit ? 'sharplabnext-legacy-jit-inspector-v1' : null, sourceMappingKind: 'none' },
    image: { imageId, labels: { 'com.sharplabnext.runtime-candidate': 'true', 'com.sharplabnext.runtime-candidate.promotion-eligible': context === 'committed' ? 'true' : 'false', 'com.sharplabnext.runtime-profile': id, 'io.sharplabnext.runtime.environment': 'wine-coreclr', 'io.sharplabnext.runtime.version': version, 'io.sharplabnext.runtime.commit': runtimeCommit, 'io.sharplabnext.jit.commit': runtimeCommit, 'io.sharplabnext.runtime.payload-sha512': payloadSha512, 'io.sharplabnext.source.revision': source, 'io.sharplabnext.source.context': context, 'org.opencontainers.image.revision': source } },
    verification: { status: 'unverified', smoke: { runtimeIdentity: 'unverified', compile: 'passed', run: 'unverified', ilDecompile: 'passed', jit: 'unverified', mapping: 'unverified' } },
  }] }
  fs.writeFileSync(resultsPath, `${JSON.stringify(result, null, 2)}\n`)
  const runtimeMatrixPath = path.join(directory, 'runtime-matrix.json')
  fs.writeFileSync(runtimeMatrixPath, `${JSON.stringify({ coreClr: [{ id: targetId, version, referenceSetId: 'netcoreapp2.0-ref', runtimeCommit, jitCommit: runtimeCommit, windows: { sha512: payloadSha512 } }] }, null, 2)}\n`)
  return { directory, id, hasJit, resultsPath, runtimeMatrixPath, profileDirectory, probeOutputPath, sandbox: { seccompPath: path.join(directory, 'seccomp.json'), seccompSha256: `sha256:${'b'.repeat(64)}`, openFilesSoftLimit: 64, openFilesHardLimit: 64 } }
}
function smoke(t, version, options = {}) {
  const value = fixture(t, version, options), calls = []
  const invoke = () => runWineCoreClrSmokes({ profileIds: [value.id], resultsPath: value.resultsPath, runtimeMatrixPath: value.runtimeMatrixPath, profileDirectory: value.profileDirectory, probeProjectPath: path.join(value.directory, 'probe.csproj'), probeOutputPath: value.probeOutputPath, sandbox: value.sandbox, now: () => new Date('2026-08-13T07:00:00.000Z'), spawn(command, argv, spawnOptions) { calls.push({ command, argv, spawnOptions }); if (command === 'docker' && argv[0] === 'image' && argv[1] === 'inspect') return { status: 0, stdout: options.inspectedImageId ?? `${imageId}\n`, stderr: '' }; if (command === 'dotnet' || argv[0] === 'rm') return { status: 0, stdout: '', stderr: '' }; if (options.timeout) { const error = new Error('timed out'); error.code = 'ETIMEDOUT'; return { status: null, stdout: '', stderr: '', error } }; return { status: 0, stdout: argv.includes('jit') ? (options.jit ?? jitLog()) : runLog(), stderr: '' } } })
  return { ...value, calls, invoke }
}

test('Wine CoreCLR smoke binds immutable development source, creates Wine tmpfs/ready state, and records Run plus ABI JIT evidence', t => {
  const value = smoke(t, '7.0.20'); const summaries = value.invoke(); assert.equal(summaries[0].jitElapsedMilliseconds, 6)
  const inspect = value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'image' && call.argv[1] === 'inspect'); assert.equal(inspect.length, 1); assert.deepEqual(inspect[0].argv, ['image', 'inspect', '--format', '{{.Id}}', `sharplabnext/runtime-${value.id}:candidate`])
  const docker = value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'run'); assert.equal(docker.length, 2)
  const staged = []
  for (const call of docker) { assert.ok(call.argv.includes(imageId)); assert.ok(call.argv.includes('WINEPREFIX=/opt/wine-dotnet')); assert.ok(call.argv.includes('SHARPLABNEXT_PREPARE_WINE_XDG_RUNTIME_DIR=1')); assert.ok(call.argv.includes('SHARPLABNEXT_WINE_CLEANUP=1')); assert.ok(call.argv.includes('nofile=512:512')); assert.ok(call.argv.includes('/tmp:rw,exec,nosuid,nodev,size=1024,uid=0,gid=0,mode=1777')); const mount = call.argv[call.argv.indexOf('--mount') + 1]; assert.match(mount, /^type=bind,source=.+,target=\/workspace,readonly$/); staged.push(mount.slice('type=bind,source='.length, mount.indexOf(',target='))); assert.equal(call.spawnOptions.timeout, 15000) }
  assert.equal(new Set(staged).size, 1); assert.equal(fs.existsSync(staged[0]), false)
  assert.equal(value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'exec').length, 0)
  assert.equal(value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'rm').length, 2)
  const jitCall = docker.find(call => call.argv.includes('jit')); assert.ok(jitCall.argv.includes('COMPlus_JitDisasm=*SharpLabNext.RuntimeCapabilityProbe.Program:WindowsAbi*')); assert.ok(jitCall.argv.includes('COMPlus_JitDisasmAssemblies=SharpLabNext.RuntimeCapabilityProbe')); assert.ok(jitCall.argv.includes('COMPlus_TieredCompilation=0'))
  const saved = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')).rows[0].verification; assert.equal(saved.smoke.mapping, 'not-applicable'); assert.equal(saved.smoke.jit, 'passed'); assert.deepEqual(saved.evidence.wineCoreClr.jit.abi, ['rcx', 'rdx', 'rax/eax']); assert.equal(saved.evidence.wineCoreClr.sourceContext, 'working-tree-development'); assert.equal(saved.evidence.wineCoreClr.sandbox.readyMarker, '/workspace/.sharplabnext/ready'); assert.equal(saved.evidence.wineCoreClr.sandbox.openFilesSoftLimit, 512); assert.equal(saved.evidence.wineCoreClr.sandbox.openFilesHardLimit, 512)
})
test('Wine CoreCLR smoke rejects a candidate tag that resolves to a stale image before probe build or container run', t => {
  const value = smoke(t, '7.0.20', { inspectedImageId: `sha256:${'d'.repeat(64)}\n` })
  assert.throws(value.invoke, /not recorded image ID/)
  assert.equal(value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'image' && call.argv[1] === 'inspect').length, 1)
  assert.equal(value.calls.filter(call => call.command === 'dotnet' && call.argv[0] === 'build').length, 0)
  assert.equal(value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'run').length, 0)
})
test('Wine .NET 5 and 6 are deliberately Run-only while mapping remains not applicable', t => {
  for (const version of ['5.0.17', '6.0.36']) {
    const value = smoke(t, version); const summary = value.invoke()[0]
    assert.equal(summary.jitElapsedMilliseconds, null)
    assert.equal(value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'run').length, 1)
    const saved = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')).rows[0].verification
    assert.equal(saved.smoke.jit, 'not-applicable'); assert.equal(saved.smoke.mapping, 'not-applicable'); assert.equal(saved.evidence.wineCoreClr.jit, null)
  }
})
test('Wine JIT checks ABI registers only in the selected WindowsAbi assembly section', t => { const assembly = `; Assembly listing for method SharpLabNext.RuntimeCapabilityProbe.Program:WindowsAbi(long,long):long\nmov rax, rcx\nret\n; Total bytes of code 8\n; Assembly listing for method Other.Type:Other(long):long\nmov rdx, rax\nret\n`; const value = smoke(t, '8.0.29', { jit: jitLog({ assembly }) }); const before = fs.readFileSync(value.resultsPath, 'utf8'); assert.throws(value.invoke, /selected WindowsAbi method/); assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before) })
test('strict SLNR parser rejects frame drift', () => { assert.throws(() => parseWineCoreClrFrameLog('bad\n'), /non-canonical/); assert.throws(() => parseWineCoreClrFrameLog(`${frame(9, 2, 'asm')}\n`), /sequence/); assert.throws(() => parseWineCoreClrFrameLog(`${frame(255, 1, 'bad')}\n`), /not supported/) })
test('Wine timeout force-removes the exact container once and removes the staged probe', t => { const value = smoke(t, '7.0.20', { timeout: true }); assert.throws(value.invoke, /exceeded its 15000 ms/); const run = value.calls.find(call => call.command === 'docker' && call.argv[0] === 'run'); const cleanup = value.calls.filter(call => call.command === 'docker' && call.argv[0] === 'rm').map(call => call.argv); assert.deepEqual(cleanup.map(value => value[0]), ['rm']); assert.equal(cleanup[0][2], run.argv[run.argv.indexOf('--name') + 1]); const mount = run.argv[run.argv.indexOf('--mount') + 1]; const staged = mount.slice('type=bind,source='.length, mount.indexOf(',target=')); assert.equal(fs.existsSync(staged), false) })
test('Wine source label drift fails before any process and CLI rejects unsupported profiles', t => { const value = fixture(t, '7.0.20', { context: 'unknown' }); let calls = 0; assert.throws(() => runWineCoreClrSmokes({ profileIds: [value.id], resultsPath: value.resultsPath, runtimeMatrixPath: value.runtimeMatrixPath, profileDirectory: value.profileDirectory, probeProjectPath: 'x', probeOutputPath: value.probeOutputPath, sandbox: value.sandbox, spawn() { calls++; return { status: 0 } } }), /source and runtime identity/); assert.equal(calls, 0); const output = { log() {}, error() {} }; assert.equal(runRuntimeWineCoreClrSmokeCli(['--profile', 'dotnet-8-linux-x64'], { output, sandbox: value.sandbox }), 1) })
test('Wine runtime, JIT, and Windows payload identity drift fails before any process', t => {
  for (const mutation of ['profile-runtime', 'matrix-jit', 'image-payload']) {
    const value = fixture(t, '7.0.20')
    if (mutation === 'profile-runtime') { const filename = path.join(value.profileDirectory, `${value.id}.json`); const profile = JSON.parse(fs.readFileSync(filename, 'utf8')); profile.runtimeCommit = '0'.repeat(40); const bytes = Buffer.from(`${JSON.stringify(profile, null, 2)}\n`); fs.writeFileSync(filename, bytes); const results = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')); results.rows[0].profileSha256 = `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`; fs.writeFileSync(value.resultsPath, `${JSON.stringify(results, null, 2)}\n`) }
    if (mutation === 'matrix-jit') { const matrix = JSON.parse(fs.readFileSync(value.runtimeMatrixPath, 'utf8')); matrix.coreClr[0].jitCommit = '0'.repeat(40); fs.writeFileSync(value.runtimeMatrixPath, `${JSON.stringify(matrix, null, 2)}\n`) }
    if (mutation === 'image-payload') { const results = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')); results.rows[0].image.labels['io.sharplabnext.runtime.payload-sha512'] = '0'.repeat(128); fs.writeFileSync(value.resultsPath, `${JSON.stringify(results, null, 2)}\n`) }
    let calls = 0
    assert.throws(() => runWineCoreClrSmokes({ profileIds: [value.id], resultsPath: value.resultsPath, runtimeMatrixPath: value.runtimeMatrixPath, profileDirectory: value.profileDirectory, probeProjectPath: 'x', probeOutputPath: value.probeOutputPath, sandbox: value.sandbox, spawn() { calls++; return { status: 0 } } }), /canonical matrix target|runtime identity/)
    assert.equal(calls, 0)
  }
})
