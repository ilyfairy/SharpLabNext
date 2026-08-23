import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { parseMonoFrameLog, runMonoSmokes, runRuntimeMonoSmokeCli } from './runtime-mono-smoke.mjs'

const profileId = 'mono-6.12-linux-x64'
const methodFilter = 'SharpLabNext.RuntimeCapabilityProbe.Program.MultipleSequencePoints'
const imageId = `sha256:${'a'.repeat(64)}`

function frame(kind, sequence, payload) {
  const content = Buffer.from(typeof payload === 'string' ? payload : JSON.stringify(payload))
  const bytes = Buffer.alloc(18 + content.length)
  bytes.write('SLNR', 0, 'ascii'); bytes[4] = 1; bytes[5] = kind; bytes.writeBigInt64LE(BigInt(sequence), 6); bytes.writeInt32LE(content.length, 14); content.copy(bytes, 18)
  return bytes.toString('base64')
}
function runLog() { return [frame(1, 1, 'SLN-CAPABILITY-STDOUT-V1\nSLN-CAPABILITY-NETWORK-BLOCKED-V1\nSLN-CAPABILITY-ROOTFS-READONLY-V1\n'), frame(2, 2, 'SLN-CAPABILITY-STDERR-V1\n'), frame(7, 3, { Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 4 })].join('\n') + '\n' }
function exceptionLog() { return [frame(6, 1, { TypeName: 'System.InvalidOperationException', Message: 'outer capability probe failure', StackTrace: 'at Program.ThrowNestedException()', InnerException: { TypeName: 'System.ArgumentException', Message: 'inner capability probe failure' } }), frame(7, 2, { Status: 'user-exception', ExitCode: 1, ElapsedMilliseconds: 2 })].join('\n') + '\n' }
function jitLog(options = {}) { return [...(options.assembly === false ? [] : [frame(9, 1, '; mono native assembly')]), frame(10, options.assembly === false ? 1 : 2, { MethodFilter: methodFilter, Methods: [options.method ?? { Method: '0x06000002', DisplayName: methodFilter, Status: 'prepared', NativeCodeSize: 24, InstructionCount: 6, MappingSource: 'none', LinkedRanges: [] }] }), frame(7, options.assembly === false ? 2 : 3, { Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 6 })].join('\n') + '\n' }

function fixture(t, options = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-mono-smoke-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const profileDirectory = path.join(directory, 'profiles'); const probeOutputPath = path.join(directory, 'probe')
  fs.mkdirSync(profileDirectory); fs.mkdirSync(probeOutputPath)
  for (const name of ['SharpLabNext.RuntimeCapabilityProbe.exe', 'SharpLabNext.RuntimeCapabilityProbe.exe.config', 'SharpLabNext.RuntimeCapabilityProbe.pdb']) fs.writeFileSync(path.join(probeOutputPath, name), 'fixture')
  const candidateImage = 'sharplabnext/runtime-mono-6.12-linux-x64:candidate'
  const profile = { schemaVersion: 1, id: profileId, image: candidateImage, family: 'mono', runtimeVersion: '6.12.0.182', container: { environmentKind: 'mono', executionUser: '1654:1654' }, capabilities: ['run', 'jit-asm'], operations: { run: { command: { executable: '/usr/bin/mono', argv: ['/opt/runner.exe', 'run', '{entryAssembly}', '--', '{arguments}'] } }, jit: { implementationId: 'sharplabnext-mono-jit-inspector-v1', sourceMappingKind: 'none', command: { executable: '/usr/share/dotnet/dotnet', argv: ['/opt/mono-jit.dll', '{entryAssembly}', '{methodFilter}'] } } }, securityPolicies: [{ id: 'runtime-job-default', memoryBytes: 128, nanoCpus: 1000000000, pidsLimit: 64, maximumDurationSeconds: 10, maximumOutputBytes: 1024, tmpfsBytes: 1024 }] }
  const profileBytes = Buffer.from(`${JSON.stringify(profile, null, 2)}\n`); fs.writeFileSync(path.join(profileDirectory, `${profileId}.json`), profileBytes)
  const profileSha256 = `sha256:${crypto.createHash('sha256').update(profileBytes).digest('hex')}`
  const resultsPath = path.join(directory, 'results.json')
  const results = {
    schemaVersion: 1,
    rows: [{
      profileId,
      candidateImage,
      profileSha256,
      referenceSetId: options.referenceSetId ?? 'netfx48-managed-ref',
      expected: {
        runImplementationId: 'sharplabnext-target-runtime-runner-v1',
        jitImplementationId: 'sharplabnext-mono-jit-inspector-v1',
        sourceMappingKind: 'none',
      },
      image: {
        imageId,
        labels: {
          'com.sharplabnext.runtime-profile': profileId,
          'io.sharplabnext.runtime.environment': 'mono',
          'io.sharplabnext.runtime.version': '6.12.0.182',
          'io.sharplabnext.source.revision': options.revision ?? '0123456789abcdef0123456789abcdef01234567',
          'org.opencontainers.image.revision': '0123456789abcdef0123456789abcdef01234567',
        },
      },
      verification: {
        status: 'unverified',
        smoke: {
          runtimeIdentity: 'unverified', compile: 'passed', run: 'unverified',
          ilDecompile: 'passed', jit: 'unverified', mapping: 'unverified',
        },
      },
    }],
  }
  fs.writeFileSync(resultsPath, `${JSON.stringify(results, null, 2)}\n`)
  return { directory, resultsPath, profileDirectory, probeOutputPath, sandbox: { seccompPath: path.join(directory, 'seccomp.json'), seccompSha256: `sha256:${'b'.repeat(64)}`, openFilesSoftLimit: 64, openFilesHardLimit: 64 } }
}
function run(t, options = {}) {
  const value = fixture(t, options)
  const calls = []
  const result = () => runMonoSmokes({ resultsPath: value.resultsPath, profileDirectory: value.profileDirectory, probeProjectPath: path.join(value.directory, 'probe.csproj'), probeOutputPath: value.probeOutputPath, sandbox: value.sandbox, exceptionProfile: options.exceptionProfile, now: () => new Date('2026-08-13T06:00:00.000Z'), spawn(command, arguments_, spawnOptions) { calls.push({ command, arguments_, options: spawnOptions }); if (command === 'dotnet') return { status: 0, stdout: '', stderr: '' }; if (arguments_[0] === 'rm') return { status: 0, stdout: '', stderr: '' }; const entrypoint = arguments_[arguments_.indexOf('--entrypoint') + 1]; if (options.timeout) { const error = new Error('timed out'); error.code = 'ETIMEDOUT'; return { status: null, stdout: '', stderr: '', error } } if (entrypoint === '/usr/bin/mono') return { status: arguments_.includes('user-exception') ? 1 : 0, stdout: arguments_.includes('user-exception') ? exceptionLog() : runLog(), stderr: '' }; return { status: 0, stdout: options.jit ?? jitLog(), stderr: '' } } })
  return { ...value, calls, result }
}

test('default sandbox resolves and verifies the Supervisor seccomp profile beside appsettings', t => {
  const value = fixture(t)
  const calls = []
  runMonoSmokes({
    resultsPath: value.resultsPath,
    profileDirectory: value.profileDirectory,
    probeProjectPath: path.join(value.directory, 'probe.csproj'),
    probeOutputPath: value.probeOutputPath,
    now: () => new Date('2026-08-13T06:00:00.000Z'),
    spawn(command, arguments_) {
      calls.push({ command, arguments_ })
      if (command === 'dotnet') return { status: 0, stdout: '', stderr: '' }
      const entrypoint = arguments_[arguments_.indexOf('--entrypoint') + 1]
      return entrypoint === '/usr/bin/mono'
        ? { status: 0, stdout: runLog(), stderr: '' }
        : { status: 0, stdout: jitLog(), stderr: '' }
    },
  })
  const docker = calls.find(call => call.command === 'docker').arguments_
  const seccomp = docker[docker.findIndex(value => value.startsWith('seccomp='))].slice('seccomp='.length)
  assert.equal(path.basename(seccomp), 'runtime-job-seccomp.v1.json')
  assert.equal(path.basename(path.dirname(seccomp)), 'security')
  assert.equal(fs.statSync(seccomp).isFile(), true)
})

test('Mono smoke binds profile/image/source/reference, executes normal args and JIT, and writes atomic evidence', t => {
  const value = run(t, { exceptionProfile: true }); const summary = value.result()
  assert.equal(summary.exceptionValidated, true)
  const docker = value.calls.filter(call => call.command === 'docker' && call.arguments_[0] === 'run')
  assert.equal(docker.length, 3); assert.ok(docker[0].arguments_.includes('success-security')); assert.ok(docker[1].arguments_.includes('user-exception')); assert.ok(docker[2].arguments_.includes(methodFilter)); assert.ok(docker.every(call => call.arguments_.includes(imageId)))
  const saved = JSON.parse(fs.readFileSync(value.resultsPath, 'utf8')).rows[0].verification
  assert.equal(saved.smoke.run, 'passed'); assert.equal(saved.smoke.jit, 'passed'); assert.equal(saved.smoke.mapping, 'not-applicable'); assert.equal(saved.evidence.mono.referenceSetId, 'netfx48-managed-ref'); assert.equal(saved.evidence.mono.sourceRevision, '0123456789abcdef0123456789abcdef01234567'); assert.equal(saved.evidence.mono.run.exception.outerType, 'System.InvalidOperationException'); assert.equal(saved.evidence.mono.run.exception.innerType, 'System.ArgumentException')
})

test('Mono parser rejects frame-family drift', () => {
  assert.throws(() => parseMonoFrameLog('not-base64\n'), /non-canonical base64/)
  assert.throws(() => parseMonoFrameLog(`${frame(9, 2, 'asm')}\n`), /sequence/)
  assert.throws(() => parseMonoFrameLog(`${frame(255, 1, 'x')}\n`), /not supported/)
})

test('Mono rejects mismatched identity and preserves results', t => {
  const value = fixture(t, { referenceSetId: 'wrong-ref' }); const before = fs.readFileSync(value.resultsPath, 'utf8'); let calls = 0
  assert.throws(() => runMonoSmokes({ resultsPath: value.resultsPath, profileDirectory: value.profileDirectory, probeProjectPath: 'x', probeOutputPath: value.probeOutputPath, sandbox: value.sandbox, spawn() { calls++; return { status: 0 } } }), /identity binding/)
  assert.equal(calls, 0); assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
})

test('Mono rejects source-revision drift and malformed nested exception families', t => {
  const sourceDrift = fixture(t, { revision: 'not-a-revision' })
  assert.throws(() => runMonoSmokes({ resultsPath: sourceDrift.resultsPath, profileDirectory: sourceDrift.profileDirectory, probeProjectPath: 'x', probeOutputPath: sourceDrift.probeOutputPath, sandbox: sourceDrift.sandbox, spawn() { return { status: 0 } } }), /source revision identity/)

  const value = run(t, { exceptionProfile: true })
  const original = exceptionLog
  assert.notEqual(original().length, 0)
  const before = fs.readFileSync(value.resultsPath, 'utf8')
  const badException = [
    frame(6, 1, { TypeName: 'System.InvalidOperationException', Message: 'outer capability probe failure', StackTrace: 'at Program.ThrowNestedException()', InnerException: { TypeName: 'System.Exception', Message: 'wrong family' } }),
    frame(7, 2, { Status: 'user-exception', ExitCode: 1, ElapsedMilliseconds: 2 }),
  ].join('\n') + '\n'
  let runCount = 0
  assert.throws(() => runMonoSmokes({
    resultsPath: value.resultsPath, profileDirectory: value.profileDirectory,
    probeProjectPath: path.join(value.directory, 'probe.csproj'), probeOutputPath: value.probeOutputPath,
    sandbox: value.sandbox, exceptionProfile: true,
    spawn(command, arguments_) {
      if (command === 'dotnet') return { status: 0, stdout: '', stderr: '' }
      if (arguments_[0] === 'rm') return { status: 0, stdout: '', stderr: '' }
      const entrypoint = arguments_[arguments_.indexOf('--entrypoint') + 1]
      if (entrypoint !== '/usr/bin/mono') return { status: 0, stdout: jitLog(), stderr: '' }
      runCount++
      return { status: runCount === 1 ? 0 : 1, stdout: runCount === 1 ? runLog() : badException, stderr: '' }
    },
  }), /nested exception frames/)
  assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
})

test('Mono rejects missing native assembly or invalid prepared summary without changing evidence', t => {
  for (const log of [jitLog({ assembly: false }), jitLog({ method: { Method: 'x', DisplayName: methodFilter, Status: 'prepared', NativeCodeSize: 0, InstructionCount: 0 } })]) {
    const value = run(t, { jit: log }); const before = fs.readFileSync(value.resultsPath, 'utf8')
    assert.throws(value.result, /no native assembly|no prepared target/); assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
  }
})

test('Mono hard timeout removes the unique named container', t => {
  const value = run(t, { timeout: true })
  assert.throws(value.result, /exceeded its 15000 ms process timeout/)
  const runCall = value.calls.find(call => call.command === 'docker' && call.arguments_[0] === 'run'); const removal = value.calls.find(call => call.command === 'docker' && call.arguments_[0] === 'rm')
  assert.equal(removal.arguments_[2], runCall.arguments_[runCall.arguments_.indexOf('--name') + 1]); assert.match(removal.arguments_[2], /^sln-mono-run-/)
})

test('CLI requires the exact Mono profile', () => {
  const output = { log() {}, error() {} }
  assert.equal(runRuntimeMonoSmokeCli(['--profile', 'dotnet-10-linux-x64'], { output }), 1)
  assert.equal(runRuntimeMonoSmokeCli(['--profile', profileId, '--exception-profile', 'dotnet-10-linux-x64'], { output }), 1)
})
