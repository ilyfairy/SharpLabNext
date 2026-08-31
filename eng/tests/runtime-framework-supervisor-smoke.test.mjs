import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  runRuntimeFrameworkSupervisorSmoke,
  runRuntimeFrameworkSupervisorSmokeCli,
} from '../smoke/runtime-framework-supervisor-smoke.mjs'

const hash = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const profileId = 'wine-netfx48-linux-x64'
const imageId = hash('runtime-image')
const roslynImageId = hash('roslyn-image')
const libraryRef = hash('library-artifact')
const consoleRef = hash('console-artifact')
const fixedNow = () => new Date('2026-08-13T08:00:00.000Z')
const protocol = { Major: 1, Minor: 0 }

function response(value, status = 200, headers = {}) { return new Response(typeof value === 'string' ? value : JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json', ...headers } }); }
function profile() {
  return {
    schemaVersion: 1, id: profileId, image: 'sharplabnext/runtime-wine-netfx48-linux-x64:candidate', family: 'netfx-clr-wine', runtimeVersion: '4.8', runtimeCommit: 'not-applicable', jitVersion: 'not-applicable', jitCommit: 'not-applicable', runtimeImageId: 'sharplabnext/runtime-wine-netfx48-linux-x64:candidate', rid: 'linux-x64', architecture: 'x64', cpuFeatureProfile: 'x64-v2', acceptedRuntimeFamilies: ['netfx-clr-wine'], acceptedFrameworks: [{ name: '.NETFramework', exactVersion: '4.8' }], acceptedArtifactFormats: ['dotnet-framework-managed-pe-v1'], capabilities: ['run'], providedRuntimeFeatureTags: ['runtime.netfx48-wine'], providedMetadataFeatureTags: [], allowedSecurityPolicyIds: ['runtime-job-wine-netfx'], container: { isolationKind: 'wine', environmentKind: 'wine', executionUser: '0:0', winePrefixPath: '/opt/wine-netfx-clr4' }, operations: { run: { implementationId: 'sharplabnext-target-runtime-runner-v1', pathStyle: 'wine-z', command: { executable: '/usr/lib/wine/wine64', argv: ['runner', 'run', '{entryAssembly}', '--', '{arguments}'] } } }, securityPolicies: [{ id: 'runtime-job-wine-netfx', memoryBytes: 1, nanoCpus: 1, pidsLimit: 1, maximumDurationSeconds: 30, maximumArtifactBytes: 1, maximumOutputBytes: 1, tmpfsBytes: 1 }],
  }
}
function setup(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-supervisor-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const profiles = path.join(directory, 'profiles'); fs.mkdirSync(profiles)
  const candidate = profile(); const profileBytes = Buffer.from(`${JSON.stringify(candidate)}\n`); fs.writeFileSync(path.join(profiles, `${profileId}.json`), profileBytes)
  const attestation = { Id: 'netfx48-managed-ref', TargetFramework: 'net48', Digest: 'sha512-net48', ContentDigest: hash('net48-content'), Provenance: { Kind: 'nuget-package', ResolvedVersion: '1.0.3', Package: 'Microsoft.NETFramework.ReferenceAssemblies.net48' } }
  const results = { schemaVersion: 1, rows: [{ profileId, matrixTargetId: 'netfx48', candidateImage: candidate.image, family: 'netfx-clr-wine', runtimeVersion: '4.8', referenceSetId: 'netfx48-managed-ref', profileSha256: hash(profileBytes), image: { imageId }, expected: { capabilities: ['run'] }, verification: { evidence: { artifactPipeline: { profileSha256: hash(profileBytes), imageId, referenceSetId: 'netfx48-managed-ref', artifactRef: libraryRef, compilePassed: true, ilPassed: true, decompiledCSharpPassed: true, matrix: { targetFramework: 'net48' }, services: { roslyn: { id: 'roslyn-stable-netfx48', releaseId: 'matrix-release', workerImageId: roslynImageId, referenceSetAttestation: attestation } } } } } }] }
  const resultsPath = path.join(directory, 'results.json'); fs.writeFileSync(resultsPath, `${JSON.stringify(results, null, 2)}\n`)
  const tokenFile = path.join(directory, 'token'); fs.writeFileSync(tokenFile, 't'.repeat(32))
  return { directory, profiles, resultsPath, tokenFile, candidate, attestation }
}
function sse(events) { return events.map(event => `data: ${JSON.stringify(event)}\n\n`).join('') }
function fetchFixture(context, options = {}) {
  const calls = []
  const output = Buffer.from(options.stdout ?? 'SLN-FRAMEWORK-SUPERVISOR-V1\nfirst\nsecond\n').toString('base64')
  const worker = { Service: { Id: 'roslyn-stable-netfx48', Kind: 3, Status: 'ready', ReleaseId: 'matrix-release', Protocol: protocol, Capabilities: ['managed-pe'] }, InstanceId: 'roslyn-1', WorkerKind: 'toolchain', WorkerImageId: options.roslynImageId ?? roslynImageId, NegotiatedProtocol: protocol, SupportedProtocolVersions: [protocol], ProfileIds: ['roslyn-stable-netfx48'], Capabilities: [], ReferenceSets: [options.attestation ?? context.attestation] }
  const expectedRun = context.candidate.operations.run
  const statusProfile = options.statusProfile ?? { Id: profileId, Image: context.candidate.image, RuntimeVersion: '4.8', RuntimeCommit: 'not-applicable', RuntimeImageId: imageId, Rid: 'linux-x64', Architecture: 'x64', Container: { WinePrefixPath: '/opt/wine-netfx-clr4' }, Operations: { Run: { ImplementationId: expectedRun.implementationId, PathStyle: expectedRun.pathStyle, Command: { Executable: expectedRun.command.executable, Argv: expectedRun.command.argv } } } }
  return { calls, fetch: async (value, init = {}) => {
    const request = { url: String(value), method: init.method ?? 'GET', headers: new Headers(init.headers), body: init.body }; calls.push(request)
    if (request.url.endsWith('/roslyn/api/v1/worker/describe')) return response(worker)
    if (request.url.endsWith('/roslyn/api/v1/build')) return response({ RequestId: JSON.parse(request.body).RequestId, Result: { ResultType: 'build', Outcome: 'succeeded', ArtifactRef: consoleRef, WorkspaceRevision: 1, SelectionRevision: 1, Identity: { ReleaseId: 'matrix-release', LanguageId: 'csharp', ToolchainId: 'roslyn-stable-netfx48', CompilerVersion: '5.6.0', ReferenceSetId: 'netfx48-managed-ref', WorkerImageId: options.roslynImageId ?? roslynImageId } } })
    if (request.url.endsWith('/supervisor/api/v1/runtime/status')) return response({ Service: { Id: 'runtime-supervisor' }, Profiles: [statusProfile] })
    if (request.url.endsWith('/supervisor/internal/v1/jobs/run')) return response({ OperationId: 'operation-1', RequestId: JSON.parse(request.body).RequestId })
    if (request.url.endsWith('/supervisor/internal/v1/operations/operation-1')) return response({ OperationId: 'operation-1', Status: options.operationStatus ?? 'completed' })
    if (request.url.endsWith('/supervisor/internal/v1/operations/operation-1/events?FromSequence=0')) return response(sse([
      { OperationId: 'operation-1', Sequence: 1, Payload: { Kind: 'output-chunk', Chunk: { Channel: 'stdout', Encoding: 'utf-8', Data: output, Truncated: false } } },
      { OperationId: 'operation-1', Sequence: 2, Payload: { Kind: 'typed-result', Result: { ResultType: 'run', Status: 'completed', ExitCode: 0, OutputTruncated: false, Identity: options.identity ?? { RuntimeVersion: '4.8', RuntimeCommit: 'not-applicable', RuntimeImageId: imageId, Rid: 'linux-x64', Architecture: 'x64' } } } },
      { OperationId: 'operation-1', Sequence: 3, Payload: { Kind: 'completed', Status: 'completed' } },
    ]), 200, { 'Content-Type': 'text/event-stream' })
    throw new Error(`Unexpected request ${request.method} ${request.url}`)
  } }
}
function live(context, fixture, extra = {}) { return runRuntimeFrameworkSupervisorSmoke({ profileId, resultsPath: context.resultsPath, profileDirectory: context.profiles, overlayPath: path.join(context.directory, 'overlay.json'), tokenFile: context.tokenFile, supervisorUrl: 'http://test/supervisor', roslynWorkerUrl: 'http://test/roslyn', fetch: fixture.fetch, now: fixedNow, sleep: async () => {}, ...extra }) }

test('prepare-only writes a one-profile validation overlay without credentials or live URLs', t => {
  const context = setup(t)
  return runRuntimeFrameworkSupervisorSmoke({ profileId, resultsPath: context.resultsPath, profileDirectory: context.profiles, overlayPath: path.join(context.directory, 'overlay.json'), prepareOnly: true }).then(result => {
    assert.equal(result.overlay.RuntimeSupervisor.SessionReuseEnabled, false); assert.equal(result.overlay.RuntimeSupervisor.RequireDigestPinnedImages, false)
    assert.equal(result.overlay.RuntimeSupervisorProfileOverlay.Profiles.length, 1); assert.equal(result.overlay.RuntimeSupervisorProfileOverlay.Profiles[0].image, context.candidate.image); assert.equal(result.overlay.RuntimeSupervisorProfileOverlay.Profiles[0].runtimeImageId, imageId)
    assert.equal(result.overlay.RuntimeSupervisorProfileOverlay.SecurityPolicies.length, 1); assert.equal(Object.hasOwn(result.overlay.RuntimeSupervisorProfileOverlay.Profiles[0], 'securityPolicies'), false)
  })
})

test('live smoke builds a Console EXE and sends one PascalCase one-shot Supervisor Run', async t => {
  const context = setup(t); const fixture = fetchFixture(context); const result = await live(context, fixture)
  const build = fixture.calls.find(call => call.url.endsWith('/roslyn/api/v1/build')); const buildBody = JSON.parse(build.body)
  assert.equal(buildBody.Options.OutputKind, 'console'); assert.match(buildBody.Workspace.Files[0].Text, /SLN-FRAMEWORK-SUPERVISOR-V1/); assert.equal(Object.keys(buildBody).every(key => /^[A-Z]/.test(key)), true)
  const run = fixture.calls.find(call => call.url.endsWith('/supervisor/internal/v1/jobs/run')); const runBody = JSON.parse(run.body)
  assert.equal(runBody.ArtifactRef, consoleRef); assert.notEqual(runBody.ArtifactRef, libraryRef); assert.deepEqual(runBody.Options.Arguments, ['first', 'second']); assert.equal(Object.keys(runBody).every(key => /^[A-Z]/.test(key)), true); assert.equal(run.headers.has('X-SharpLabNext-Runtime-Session-Id'), false)
  assert.equal(result.evidence.stdoutMarker, 'SLN-FRAMEWORK-SUPERVISOR-V1'); assert.equal(JSON.parse(fs.readFileSync(context.resultsPath)).rows[0].verification.evidence.supervisorOneShot.artifactRef, consoleRef)
})

test('stale profile or Roslyn attestation fails before a live Run and preserves results', async t => {
  const context = setup(t); const before = fs.readFileSync(context.resultsPath, 'utf8'); fs.appendFileSync(path.join(context.profiles, `${profileId}.json`), ' ')
  await assert.rejects(live(context, fetchFixture(context)), /does not match/); assert.equal(fs.readFileSync(context.resultsPath, 'utf8'), before)
  const clean = setup(t); const cleanBefore = fs.readFileSync(clean.resultsPath, 'utf8'); const stale = structuredClone(clean.attestation); stale.Digest = 'sha512-stale'; const fixture = fetchFixture(clean, { attestation: stale })
  await assert.rejects(live(clean, fixture), /attestation/); assert.equal(fixture.calls.some(call => call.url.endsWith('/build')), false); assert.equal(fs.readFileSync(clean.resultsPath, 'utf8'), cleanBefore)
})

test('wrong Supervisor identity or failed terminal state leaves results untouched', async t => {
  const context = setup(t); const before = fs.readFileSync(context.resultsPath, 'utf8'); const fixture = fetchFixture(context, { identity: { RuntimeVersion: '4.8', RuntimeCommit: 'not-applicable', RuntimeImageId: hash('wrong'), Rid: 'linux-x64', Architecture: 'x64' } })
  await assert.rejects(live(context, fixture), /identity/); assert.equal(fs.readFileSync(context.resultsPath, 'utf8'), before)
  const second = setup(t); const secondBefore = fs.readFileSync(second.resultsPath, 'utf8'); await assert.rejects(live(second, fetchFixture(second, { operationStatus: 'failed' })), /ended as failed/); assert.equal(fs.readFileSync(second.resultsPath, 'utf8'), secondBefore)
})

test('CLI permits prepare-only but rejects live invocation without URLs or a token file', async t => {
  const context = setup(t); const output = { log() {}, error() {} }
  assert.notEqual(await runRuntimeFrameworkSupervisorSmokeCli(['--profile', profileId, '--prepare-only', '--results', context.resultsPath, '--overlay', path.join(context.directory, 'overlay.json')], { output, profileDirectory: context.profiles }), 1)
  assert.equal(await runRuntimeFrameworkSupervisorSmokeCli(['--profile', profileId], { output }), 1)
  assert.equal(await runRuntimeFrameworkSupervisorSmokeCli(['--profile', profileId, '--supervisor', 'http://test', '--roslyn-worker', 'http://test'], { output }), 1)
})
