import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  parseRuntimeFrameLog,
  runFunctionalSmokes,
} from '../smoke/runtime-functional-smoke.mjs'

function frame(kind, sequence, payload) {
  const content = Buffer.from(payload)
  const bytes = Buffer.alloc(18 + content.length)
  bytes.write('SLNR', 0, 'ascii')
  bytes[4] = 1
  bytes[5] = kind
  bytes.writeBigInt64LE(BigInt(sequence), 6)
  bytes.writeInt32LE(content.length, 14)
  content.copy(bytes, 18)
  return bytes.toString('base64')
}

function successLog() {
  return [
    frame(1, 1, 'SLN-CAPABILITY-STDOUT-V1\n'),
    frame(2, 2, 'SLN-CAPABILITY-STDERR-V1\n'),
    frame(1, 3, 'SLN-CAPABILITY-NETWORK-BLOCKED-V1\nSLN-CAPABILITY-ROOTFS-READONLY-V1\n'),
    frame(7, 4, JSON.stringify({ Status: 'completed', ExitCode: 0, ElapsedMilliseconds: 12.5 })),
  ].join('\n') + '\n'
}

function exceptionLog() {
  return [
    frame(6, 1, JSON.stringify({
      TypeName: 'System.InvalidOperationException',
      Message: 'outer capability probe failure',
      StackTrace: 'at Probe.ThrowNestedException()',
      InnerException: {
        TypeName: 'System.ArgumentException',
        Message: 'inner capability probe failure',
      },
    })),
    frame(7, 2, JSON.stringify({
      Status: 'user-exception',
      ExitCode: 1,
      ElapsedMilliseconds: 9,
    })),
  ].join('\n') + '\n'
}

function createFixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-smoke-test-'))
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
  ]) {
    fs.writeFileSync(path.join(probeOutputPath, filename), 'fixture')
  }

  const profileId = 'dotnet-core-2.0-linux-x64'
  const candidateImage = 'sharplabnext/runtime-dotnet-core-2.0-linux-x64:candidate'
  const profileBytes = Buffer.from(`${JSON.stringify({
    id: profileId,
    image: candidateImage,
    family: 'coreclr',
    container: {
      isolationKind: 'standard',
      executionUser: '1654:1654',
    },
    operations: {
      run: {
        pathStyle: 'unix',
        command: {
          executable: '/opt/sharplabnext/dotnet',
          argv: ['run', '{entryAssembly}', '--', '{arguments}'],
        },
      },
    },
    securityPolicies: [{
      id: 'runtime-job-default',
      memoryBytes: 268435456,
      nanoCpus: 1000000000,
      pidsLimit: 64,
      maximumDurationSeconds: 10,
      maximumArtifactBytes: 67108864,
      maximumOutputBytes: 1048576,
      tmpfsBytes: 33554432,
    }],
  }, null, 2)}\n`)
  fs.writeFileSync(path.join(profileDirectory, `${profileId}.json`), profileBytes)
  const profileSha256 = `sha256:${crypto.createHash('sha256').update(profileBytes).digest('hex')}`
  const imageId = `sha256:${'a'.repeat(64)}`
  const resultsPath = path.join(directory, 'results.json')
  const results = {
    schemaVersion: 1,
    refreshedAt: 'inventory-time',
    rows: [{
      profileId,
      referenceSetId: 'netcoreapp2.0-ref',
      profileSha256,
      candidateImage,
      expected: { capabilities: ['run'], sourceMappingKind: 'none' },
      image: { imageId },
      verification: {
        status: 'unverified',
        smoke: { compile: 'passed', ilDecompile: 'unverified' },
      },
    }],
  }
  fs.writeFileSync(resultsPath, `${JSON.stringify(results, null, 2)}\n`)
  return {
    directory,
    imageId,
    profileDirectory,
    profileId,
    probeOutputPath,
    resultsPath,
    sandbox: {
      seccompPath: path.join(directory, 'seccomp.json'),
      seccompSha256: `sha256:${'c'.repeat(64)}`,
      openFilesSoftLimit: 256,
      openFilesHardLimit: 256,
    },
  }
}

test('frame parser accepts canonical ordered base64 lines and rejects drift', () => {
  assert.deepEqual(parseRuntimeFrameLog(successLog()).map(value => value.kind), [1, 2, 1, 7])
  assert.throws(() => parseRuntimeFrameLog('not-base64\n'), /non-canonical base64/)
  assert.throws(() => parseRuntimeFrameLog(`${frame(1, 2, 'out')}\n`), /sequence/)
  assert.throws(() => parseRuntimeFrameLog(`${frame(255, 1, 'unknown')}\n`), /not supported/)
  assert.throws(
    () => parseRuntimeFrameLog(`${frame(7, 1, '{}')}\n${frame(1, 2, 'late')}\n`),
    /after its terminal Exit/,
  )
})

test('standard CoreCLR smoke uses immutable image and writes resumable evidence', t => {
  const fixture = createFixture(t)

  const calls = []
  const spawn = (command, arguments_, options) => {
    calls.push({ command, arguments_, options })
    if (command === 'dotnet') return { status: 0, stdout: '', stderr: '' }
    assert.equal(command, 'docker')
    assert.ok(arguments_.includes(fixture.imageId))
    assert.ok(arguments_.includes('--network'))
    assert.ok(arguments_.includes('none'))
    assert.ok(arguments_.includes('--ipc'))
    assert.ok(arguments_.includes(`seccomp=${fixture.sandbox.seccompPath}`))
    assert.ok(arguments_.includes('nofile=256:256'))
    assert.equal(options.timeout, 15_000)
    const exception = arguments_.at(-1) === 'user-exception'
    return {
      status: exception ? 1 : 0,
      stdout: exception ? exceptionLog() : successLog(),
      stderr: '',
    }
  }

  const summaries = runFunctionalSmokes({
    profileIds: [fixture.profileId],
    exceptionProfileIds: [fixture.profileId],
    resultsPath: fixture.resultsPath,
    profileDirectory: fixture.profileDirectory,
    probeProjectPath: path.join(fixture.directory, 'probe.csproj'),
    probeOutputPath: fixture.probeOutputPath,
    sandbox: fixture.sandbox,
    spawn,
    now: () => new Date('2026-08-13T01:02:03.000Z'),
  })
  assert.equal(summaries[0].exceptionValidated, true)
  assert.equal(calls.length, 3)
  const resultDocument = JSON.parse(fs.readFileSync(fixture.resultsPath, 'utf8'))
  const saved = resultDocument.rows[0].verification
  assert.equal(saved.status, 'runtime-smoke-passed')
  assert.equal(saved.smoke.run, 'passed')
  assert.equal(saved.smoke.compile, 'unverified')
  assert.equal(saved.smoke.ilDecompile, 'unverified')
  assert.equal(saved.reason, 'compile-ilDecompile-pending')
  assert.equal(saved.evidence.directRun.imageId, fixture.imageId)
  assert.equal(saved.evidence.directRun.sandbox.ipcMode, 'none')
  assert.equal(saved.evidence.directRun.sandbox.seccompSha256, fixture.sandbox.seccompSha256)
  assert.equal(saved.evidence.directRun.exception.innerType, 'System.ArgumentException')
  assert.equal(resultDocument.refreshedAt, 'inventory-time')
  assert.equal(resultDocument.verificationRefreshedAt, '2026-08-13T01:02:03.000Z')
})

test('profile drift fails before running and does not overwrite prior evidence', t => {
  const fixture = createFixture(t)
  const original = fs.readFileSync(fixture.resultsPath, 'utf8')
  fs.appendFileSync(path.join(fixture.profileDirectory, `${fixture.profileId}.json`), ' ')
  let calls = 0
  assert.throws(() => runFunctionalSmokes({
    profileIds: [fixture.profileId],
    resultsPath: fixture.resultsPath,
    profileDirectory: fixture.profileDirectory,
    probeProjectPath: path.join(fixture.directory, 'probe.csproj'),
    probeOutputPath: fixture.probeOutputPath,
    sandbox: fixture.sandbox,
    spawn() { calls++; return { status: 0, stdout: '', stderr: '' } },
  }), /Refresh the inventory first/)
  assert.equal(calls, 0)
  assert.equal(fs.readFileSync(fixture.resultsPath, 'utf8'), original)
})

test('runtime timeout is bounded and force-removes the named container', t => {
  const fixture = createFixture(t)
  const calls = []
  assert.throws(() => runFunctionalSmokes({
    profileIds: [fixture.profileId],
    resultsPath: fixture.resultsPath,
    profileDirectory: fixture.profileDirectory,
    probeProjectPath: path.join(fixture.directory, 'probe.csproj'),
    probeOutputPath: fixture.probeOutputPath,
    sandbox: fixture.sandbox,
    spawn(command, arguments_, options) {
      calls.push({ command, arguments_, options })
      if (command === 'dotnet' || arguments_[0] === 'rm') {
        return { status: 0, stdout: '', stderr: '' }
      }
      const error = new Error('timed out')
      error.code = 'ETIMEDOUT'
      return { status: null, stdout: '', stderr: '', error }
    },
  }), /exceeded its 15000 ms process timeout/)
  const run = calls.find(call => call.arguments_[0] === 'run')
  const removal = calls.find(call => call.arguments_[0] === 'rm')
  assert.equal(run.options.timeout, 15_000)
  assert.deepEqual(removal.arguments_.slice(0, 2), ['rm', '--force'])
  assert.equal(removal.arguments_[2], run.arguments_[run.arguments_.indexOf('--name') + 1])
})
