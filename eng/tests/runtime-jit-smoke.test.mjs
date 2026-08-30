import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { runRuntimeJitSmokes } from '../smoke/runtime-jit-smoke.mjs'

const probeMethod = 'SharpLabNext.RuntimeCapabilityProbe.Program.MultipleSequencePoints'
const fixedNow = () => new Date('2026-08-13T05:00:00.000Z')

function frame(kind, sequence, payload) {
  const content = Buffer.from(typeof payload === 'string' ? payload : JSON.stringify(payload))
  const bytes = Buffer.alloc(18 + content.length)
  bytes.write('SLNR', 0, 'ascii')
  bytes[4] = 1
  bytes[5] = kind
  bytes.writeBigInt64LE(BigInt(sequence), 6)
  bytes.writeInt32LE(content.length, 14)
  content.copy(bytes, 18)
  return bytes.toString('base64')
}

function mappedMethod(source = 'ordinary') {
  return {
    Method: '0x06000002',
    DisplayName: probeMethod,
    Status: 'prepared',
    NativeCodeSize: 24,
    InstructionCount: 6,
    MappingSource: source,
    LinkedRanges: [
      { SourceFilePath: 'Program.cs', SourceRange: { StartLine: 52, StartCharacter: 8, EndLine: 55, EndCharacter: 12 }, OutputRange: { StartLine: 1, StartCharacter: 0, EndLine: 1, EndCharacter: 3 }, Precision: 'sequence-point' },
      { SourceFilePath: 'Program.cs', SourceRange: { StartLine: 55, StartCharacter: 12, EndLine: 59, EndCharacter: 28 }, OutputRange: { StartLine: 2, StartCharacter: 0, EndLine: 2, EndCharacter: 3 }, Precision: 'sequence-point' },
    ],
    EvidenceRanges: [
      { IlOffset: 0, NativeStartOffset: 0, NativeEndOffset: 8, Document: 'Program.cs', StartLine: 52, StartColumn: 9, EndLine: 55, EndColumn: 13 },
      { IlOffset: 4, NativeStartOffset: 8, NativeEndOffset: 16, Document: 'Program.cs', StartLine: 55, StartColumn: 13, EndLine: 59, EndColumn: 29 },
    ],
  }
}

function noMapMethod() {
  return {
    Method: '0x06000002',
    DisplayName: probeMethod,
    Status: 'prepared',
    NativeCodeSize: 24,
    InstructionCount: 6,
    MappingSource: 'none',
    LinkedRanges: [],
    EvidenceRanges: [],
  }
}

function jitLog(method = mappedMethod(), assembly = '; Assembly listing for method') {
  return [
    ...(assembly === null ? [] : [frame(9, 1, assembly)]),
    frame(10, assembly === null ? 1 : 2, { RuntimeVersion: '10.0.10', Assembly: 'SharpLabNext.RuntimeCapabilityProbe', MethodFilter: probeMethod, Methods: [method] }),
    frame(7, assembly === null ? 2 : 3, { Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 12.5 }),
  ].join('\n') + '\n'
}

function createFixture(t, options = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-jit-smoke-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const profileDirectory = path.join(directory, 'profiles')
  const probeOutputPath = path.join(directory, 'probe')
  fs.mkdirSync(profileDirectory)
  fs.mkdirSync(probeOutputPath)
  for (const filename of [
    'SharpLabNext.RuntimeCapabilityProbe.dll',
    'SharpLabNext.RuntimeCapabilityProbe.pdb',
    'SharpLabNext.RuntimeCapabilityProbe.deps.json',
    'SharpLabNext.RuntimeCapabilityProbe.runtimeconfig.json',
  ]) fs.writeFileSync(path.join(probeOutputPath, filename), 'fixture')
  const implementationId = options.implementationId ?? 'sharplabnext-jit-inspector-v1'
  const mappingKind = options.mappingKind ?? 'linux-profiler'
  const profileId = options.profileId ?? 'dotnet-10-linux-x64'
  const candidateImage = `sharplabnext/runtime-${profileId}:candidate`
  const profile = {
    schemaVersion: 1, id: profileId, image: candidateImage, family: 'coreclr',
    container: { isolationKind: 'standard', environmentKind: 'coreclr', executionUser: '1654:1654' },
    capabilities: ['run', 'jit-asm'],
    operations: { jit: {
      implementationId, pathStyle: 'unix', sourceMappingKind: mappingKind,
      ...(implementationId === 'sharplabnext-jit-inspector-v1' ? { profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so' } : {}),
      command: { executable: '/opt/sharplabnext/target-dotnet/dotnet', argv: ['/opt/sharplabnext/SharpLabNext.JitInspector.dll', '{entryAssembly}', '{methodFilter}'] },
    } },
    securityPolicies: [{ id: 'runtime-job-default', memoryBytes: 268435456, nanoCpus: 1000000000, pidsLimit: 64, maximumDurationSeconds: 10, maximumArtifactBytes: 67108864, maximumOutputBytes: 1048576, tmpfsBytes: 33554432 }],
  }
  const profileBytes = Buffer.from(`${JSON.stringify(profile, null, 2)}\n`)
  fs.writeFileSync(path.join(profileDirectory, `${profileId}.json`), profileBytes)
  const profileSha256 = `sha256:${crypto.createHash('sha256').update(profileBytes).digest('hex')}`
  const resultsPath = path.join(directory, 'results.json')
  const imageId = `sha256:${'a'.repeat(64)}`
  fs.writeFileSync(resultsPath, `${JSON.stringify({ schemaVersion: 1, rows: [{
    profileId, candidateImage, profileSha256, referenceSetId: 'netcoreapp2.0-ref', image: { imageId },
    expected: { capabilities: ['run', 'jit-asm'], sourceMappingKind: mappingKind },
    verification: { status: 'runtime-smoke-passed', smoke: { runtimeIdentity: 'passed', compile: 'passed', run: 'passed', ilDecompile: 'passed', jit: 'unverified', mapping: 'unverified' }, evidence: options.oldEvidence ? { jit: options.oldEvidence } : {} },
  }] }, null, 2)}\n`)
  return {
    directory, profileId, resultsPath, profileDirectory, probeOutputPath, imageId,
    sandbox: { seccompPath: path.join(directory, 'seccomp.json'), seccompSha256: `sha256:${'b'.repeat(64)}`, openFilesSoftLimit: 256, openFilesHardLimit: 256 },
  }
}

function run(fixture, spawn) {
  return runRuntimeJitSmokes({
    profileIds: [fixture.profileId], resultsPath: fixture.resultsPath, profileDirectory: fixture.profileDirectory,
    probeProjectPath: path.join(fixture.directory, 'probe.csproj'), probeOutputPath: fixture.probeOutputPath,
    sandbox: fixture.sandbox, spawn, now: fixedNow,
  })
}

test('default sandbox resolves and verifies the Supervisor seccomp profile beside appsettings', t => {
  const fixture = createFixture(t, { profileId: 'dotnet-6-linux-x64', implementationId: 'sharplabnext-checked-jit-bridge-v1', mappingKind: 'none' })
  const calls = []
  runRuntimeJitSmokes({
    profileIds: [fixture.profileId], resultsPath: fixture.resultsPath,
    profileDirectory: fixture.profileDirectory,
    probeProjectPath: path.join(fixture.directory, 'probe.csproj'),
    probeOutputPath: fixture.probeOutputPath,
    spawn: (command, arguments_) => {
      calls.push({ command, arguments_ })
      return command === 'dotnet'
        ? { status: 0, stdout: '', stderr: '' }
        : { status: 0, stdout: jitLog(noMapMethod()), stderr: '' }
    },
    now: fixedNow,
  })
  const docker = calls.find(call => call.command === 'docker').arguments_
  const seccomp = docker[docker.findIndex(value => value.startsWith('seccomp='))].slice('seccomp='.length)
  assert.equal(path.basename(seccomp), 'runtime-job-seccomp.v1.json')
  assert.equal(path.basename(path.dirname(seccomp)), 'security')
  assert.equal(fs.statSync(seccomp).isFile(), true)
})

test('modern profiler JIT smoke uses the immutable image, Supervisor environment, and binding evidence', t => {
  const fixture = createFixture(t, { oldEvidence: { imageId: `sha256:${'c'.repeat(64)}`, profileSha256: `sha256:${'d'.repeat(64)}` } })
  const calls = []
  const summaries = run(fixture, (command, arguments_, options) => {
    calls.push({ command, arguments_, options })
    if (command === 'dotnet') return { status: 0, stdout: '', stderr: '' }
    return { status: 0, stdout: jitLog(), stderr: '' }
  })
  assert.equal(summaries[0].mapping, 'passed')
  const docker = calls.find(call => call.command === 'docker')
  assert.ok(docker.arguments_.includes(fixture.imageId))
  assert.ok(docker.arguments_.includes('--network'))
  assert.ok(docker.arguments_.includes('none'))
  assert.ok(docker.arguments_.includes(`seccomp=${fixture.sandbox.seccompPath}`))
  assert.ok(docker.arguments_.includes('SHARPLABNEXT_JIT_RESET_OUTPUT=1'))
  assert.ok(docker.arguments_.includes('CORECLR_ENABLE_PROFILING=1'))
  assert.ok(docker.arguments_.includes('COMPlus_JitDisasm=*SharpLabNext.RuntimeCapabilityProbe.Program:MultipleSequencePoints*'))
  assert.ok(docker.arguments_.includes('/artifact/SharpLabNext.RuntimeCapabilityProbe.dll'))
  assert.ok(docker.arguments_.includes(probeMethod))
  const saved = JSON.parse(fs.readFileSync(fixture.resultsPath, 'utf8')).rows[0].verification
  assert.equal(saved.status, 'smoke-passed')
  assert.equal(saved.smoke.jit, 'passed')
  assert.equal(saved.smoke.mapping, 'passed')
  assert.equal(saved.evidence.jit.imageId, fixture.imageId)
  assert.equal(saved.evidence.jit.referenceSetId, 'netcoreapp2.0-ref')
  assert.equal(saved.evidence.jit.methodFilter, probeMethod)
})

test('checked bridge without mapping receives no profiler environment and marks mapping not applicable', t => {
  const fixture = createFixture(t, { profileId: 'dotnet-6-linux-x64', implementationId: 'sharplabnext-checked-jit-bridge-v1', mappingKind: 'none' })
  const calls = []
  run(fixture, (command, arguments_) => {
    calls.push({ command, arguments_ })
    return command === 'dotnet' ? { status: 0, stdout: '', stderr: '' } : { status: 0, stdout: jitLog(noMapMethod()), stderr: '' }
  })
  const docker = calls.find(call => call.command === 'docker').arguments_
  assert.ok(docker.includes('DOTNET_EnableDiagnostics=0'))
  assert.equal(docker.some(value => value.startsWith('CORECLR_ENABLE_PROFILING=')), false)
  assert.equal(docker.some(value => value.startsWith('COMPlus_JitDisasm=')), false)
  const saved = JSON.parse(fs.readFileSync(fixture.resultsPath, 'utf8')).rows[0].verification
  assert.equal(saved.smoke.jit, 'passed')
  assert.equal(saved.smoke.mapping, 'not-applicable')
})

test('checked bridge mapping accepts only checked-jit-debug-info with distinct PDB evidence', t => {
  const fixture = createFixture(t, { profileId: 'dotnet-7-linux-x64', implementationId: 'sharplabnext-checked-jit-bridge-v1', mappingKind: 'checked-jit-debug-info' })
  run(fixture, command => command === 'dotnet'
    ? { status: 0, stdout: '', stderr: '' }
    : { status: 0, stdout: jitLog(mappedMethod('checked-jit-debug-info')), stderr: '' })
  const saved = JSON.parse(fs.readFileSync(fixture.resultsPath, 'utf8')).rows[0].verification
  assert.equal(saved.smoke.mapping, 'passed')
  assert.equal(saved.evidence.jit.mappingSource, 'checked-jit-debug-info')
})

test('missing assembly, prepared target, or mapped ranges leaves the result atomically unchanged', t => {
  for (const [name, log] of [
    ['assembly', jitLog(mappedMethod(), null)],
    ['target method', jitLog({ ...mappedMethod(), Method: 'Other.Method', DisplayName: 'Other.Method' })],
    ['ranges', jitLog({ ...mappedMethod(), LinkedRanges: [mappedMethod().LinkedRanges[0]], EvidenceRanges: [mappedMethod().EvidenceRanges[0]] })],
  ]) {
    const fixture = createFixture(t)
    const before = fs.readFileSync(fixture.resultsPath, 'utf8')
    assert.throws(() => run(fixture, command => command === 'dotnet'
      ? { status: 0, stdout: '', stderr: '' }
      : { status: 0, stdout: log, stderr: '' }), new RegExp(name === 'assembly' ? 'no native assembly' : name === 'target method' ? 'no prepared target' : 'at least two distinct'))
    assert.equal(fs.readFileSync(fixture.resultsPath, 'utf8'), before)
  }
})

test('profile identity drift fails before probe build and preserves prior results', t => {
  const fixture = createFixture(t)
  const profile = path.join(fixture.profileDirectory, `${fixture.profileId}.json`)
  fs.appendFileSync(profile, ' ')
  const before = fs.readFileSync(fixture.resultsPath, 'utf8')
  let calls = 0
  assert.throws(() => run(fixture, () => { calls++; return { status: 0, stdout: '', stderr: '' } }), /does not match its functional result binding/)
  assert.equal(calls, 0)
  assert.equal(fs.readFileSync(fixture.resultsPath, 'utf8'), before)
})

test('a hard timeout force-removes exactly the unique named JIT smoke container', t => {
  const fixture = createFixture(t)
  const calls = []
  assert.throws(() => run(fixture, (command, arguments_, options) => {
    calls.push({ command, arguments_, options })
    if (command === 'dotnet' || arguments_[0] === 'rm') return { status: 0, stdout: '', stderr: '' }
    const error = new Error('timed out')
    error.code = 'ETIMEDOUT'
    return { status: null, stdout: '', stderr: '', error }
  }), /exceeded its 15000 ms process timeout/)
  const dockerRun = calls.find(call => call.command === 'docker' && call.arguments_[0] === 'run')
  const removal = calls.find(call => call.command === 'docker' && call.arguments_[0] === 'rm')
  assert.equal(removal.arguments_[2], dockerRun.arguments_[dockerRun.arguments_.indexOf('--name') + 1])
  assert.match(removal.arguments_[2], /^sln-jit-dotnet-10-linux-x64-/)
})
