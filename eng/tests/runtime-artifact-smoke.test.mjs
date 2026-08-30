import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { runRuntimeArtifactSmokes, runRuntimeArtifactSmokeCli } from '../smoke/runtime-artifact-smoke.mjs'

const digest = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const fixedNow = () => new Date('2026-08-13T04:00:00.000Z')
const releaseId = 'runtime-matrix-current'
const protocol = () => ({ Major: 1, Minor: 0 })

function service(id, kind, capabilities) { return { Id: id, Kind: kind, ReleaseId: releaseId, Protocol: protocol(), Capabilities: capabilities, Status: 'ready' }; }

function capability(id, profileId) { return { Id: id, ContractVersion: 1, Available: true, ProfileIds: [profileId] }; }

function referenceSet(id) {
  return {
    Id: id,
    TargetFramework: 'netcoreapp2.0',
    Digest: 'sha512-package-content-hash',
    ContentDigest: digest(`content-${id}`),
    Provenance: { Kind: 'nuget-package', ResolvedVersion: '2.0.9', Package: 'Microsoft.NETCore.App.Ref' },
  }
}

function temporaryResults(t, profileOverrides = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-artifact-smoke-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const filename = path.join(directory, 'results.json')
  const profileDirectory = path.join(directory, 'profiles')
  const runtimeMatrixPath = path.join(directory, 'runtime-matrix.json')
  fs.mkdirSync(profileDirectory)
  const profile = {
    id: 'dotnet-core-2.0-linux-x64', family: 'coreclr', acceptedRuntimeFamilies: ['coreclr'],
    acceptedArtifactFormats: ['dotnet-managed-pe-v1'],
    acceptedFrameworks: [{ name: 'Microsoft.NETCore.App', exactVersion: '2.0.9' }],
    ...profileOverrides,
  }
  const profileBytes = Buffer.from(`${JSON.stringify(profile)}\n`)
  fs.writeFileSync(path.join(profileDirectory, `${profile.id}.json`), profileBytes)
  const matrix = { schemaVersion: 1, coreClr: [{
    id: 'dotnet-core-2.0', referenceSetId: 'netcoreapp2.0-ref',
    referencePackage: { id: 'Microsoft.NETCore.App.Ref', version: '2.0.9', packageContentHash: 'sha512-package-content-hash' },
  }] }
  const matrixBytes = Buffer.from(`${JSON.stringify(matrix, null, 2)}\n`)
  fs.writeFileSync(runtimeMatrixPath, matrixBytes)
  fs.writeFileSync(filename, `${JSON.stringify({
    schemaVersion: 1,
    runtimeMatrixSha256: digest(matrixBytes),
    rows: [{
      profileId: profile.id,
      matrixTargetId: 'dotnet-core-2.0',
      referenceSetId: 'netcoreapp2.0-ref',
      profileSha256: `sha256:${crypto.createHash('sha256').update(profileBytes).digest('hex')}`,
      image: { imageId: digest('image') },
      expected: { capabilities: ['run'], sourceMappingKind: 'none' },
      verification: { smoke: { runtimeIdentity: 'passed', run: 'passed', jit: 'not-applicable', mapping: 'not-applicable' } },
    }],
  }, null, 2)}\n`)
  return { filename, profileDirectory, runtimeMatrixPath, profileId: profile.id }
}

function response(value, status = 200, headers = {}) {
  const raw = typeof value === 'string' || Buffer.isBuffer(value) || value instanceof Uint8Array
  return new Response(raw ? value : JSON.stringify(value), {
    status,
    headers: { 'Content-Type': raw ? 'text/plain' : 'application/json', ...headers },
  })
}

function fixtureFetch(calls, options = {}) {
  const roslynImage = digest('roslyn-image')
  const artifactImage = digest('artifact-image')
  const artifactRef = digest('artifact')
  const ilText = options.ilText ?? '.method public static int32 RuntimeMatrixProbeMethod() cil managed'
  const csharpText = options.csharpText ?? 'public static int RuntimeMatrixProbeMethod(int value) => value + 1;'
  const ilContent = options.ilContent ?? ilText
  const csharpContent = options.csharpContent ?? csharpText
  const ilRef = digest(options.ilRefContent ?? ilText)
  const csharpRef = digest(options.csharpRefContent ?? csharpText)
  return async (url, init = {}) => {
    const request = { url: String(url), method: init.method ?? 'GET', headers: new Headers(init.headers), body: init.body }
    calls.push(request)
    if (request.url.endsWith('/roslyn/api/v1/worker/describe')) {
      const descriptor = {
        Service: service('roslyn-stable', 3, ['managed-pe']),
        InstanceId: 'roslyn-1', WorkerKind: 'toolchain', WorkerImageId: roslynImage,
        NegotiatedProtocol: protocol(), SupportedProtocolVersions: [protocol()],
        Capabilities: [capability('managed-pe', 'roslyn-stable')], ProfileIds: ['roslyn-stable'],
        StartedAtUtc: '2026-08-13T03:00:00Z', Identity: { compilerVersion: '5.6.0' },
        ReferenceSets: [referenceSet('netcoreapp2.0-ref')],
      }
      options.configureRoslynDescriptor?.(descriptor)
      return response(descriptor)
    }
    if (request.url.endsWith('/store/api/v1/artifacts/status')) {
      const identity = { ...service('artifact-store', 1, ['content-addressed-storage']), Status: 'local-sqlite-v1' }
      options.configureArtifactStoreIdentity?.(identity)
      return response(identity)
    }
    if (request.url.endsWith('/artifacts/api/v1/worker/describe')) {
      const descriptor = {
        Service: service('artifacts-default', 4, ['il', 'decompiled-csharp']),
        InstanceId: 'artifacts-1', WorkerKind: 'artifact-processor', WorkerImageId: artifactImage,
        NegotiatedProtocol: protocol(), SupportedProtocolVersions: [protocol()],
        Capabilities: [capability('il', 'artifacts-default'), capability('decompiled-csharp', 'artifacts-default')],
        ProfileIds: ['artifacts-default'], StartedAtUtc: '2026-08-13T03:00:00Z',
        Identity: { ilspyVersion: '10.1.0' },
      }
      options.configureArtifactDescriptor?.(descriptor)
      return response(descriptor)
    }
    if (request.url.endsWith('/roslyn/api/v1/build')) {
      const body = JSON.parse(request.body)
      return response({ RequestId: body.RequestId, Result: {
        ResultType: 'build', Outcome: 'succeeded', ArtifactRef: artifactRef, WorkspaceRevision: 1, SelectionRevision: 1,
        Diagnostics: [],
        Identity: { ReleaseId: releaseId, LanguageId: 'csharp', ToolchainId: 'roslyn-stable', CompilerVersion: '5.6.0', ReferenceSetId: body.ReferenceSetId, WorkerImageId: roslynImage },
      } })
    }
    if (request.url.endsWith(`/store/internal/v1/artifacts/sha256/${artifactRef.slice(7)}`)) {
      const descriptor = { Manifest: {
        ArtifactId: artifactRef, ReferenceSetId: 'netcoreapp2.0-ref', TargetFramework: 'netcoreapp2.0',
        ArtifactFormat: 'dotnet-managed-pe-v1', RuntimeRequirement: {
          Family: 'coreclr', Frameworks: [{ Name: 'Microsoft.NETCore.App', MinimumVersion: '2.0.9' }], Architecture: 'anycpu', RequiredRuntimeFeatureTags: [],
        },
      }, Entries: [] }
      options.configureManifest?.(descriptor.Manifest)
      return response(descriptor)
    }
    if (request.url.endsWith('/artifacts/api/v1/artifact-renders')) {
      const body = JSON.parse(request.body)
      return response({ OperationId: body.OutputId === 'il' ? 'il-op' : 'csharp-op' })
    }
    if (/\/artifacts\/api\/v1\/operations\/(il-op|csharp-op)$/.test(request.url)) {
      const operationId = request.url.endsWith('/il-op') ? 'il-op' : 'csharp-op'
      const callsForOperation = calls.filter(call => call.url.endsWith(`/operations/${operationId}`)).length
      const completedAfter = options.completedAfter ?? 1
      return response({ OperationId: operationId, RequestId: 'request', Kind: 'render-artifact', Status: callsForOperation >= completedAfter ? 'completed' : 'running', LastSequence: 3 })
    }
    if (request.url.endsWith('/operations/il-op/events?FromSequence=0')) return response([
      { OperationId: 'il-op', Sequence: 1, Payload: { Kind: 'content-produced', ContentRef: ilRef, MediaType: 'text/plain', Size: 80 } },
      { OperationId: 'il-op', Sequence: 2, Payload: { Kind: 'typed-result', Result: { ResultType: 'artifact-render', Outcome: 'succeeded', ContentRef: ilRef, MediaType: 'text/plain', LinkedRanges: [], Diagnostics: [], Identity: { ReleaseId: releaseId, ProcessorId: 'artifacts-default', ProcessorVersion: '10.1.0', WorkerImageId: artifactImage } } } },
      { OperationId: 'il-op', Sequence: 3, Payload: { Kind: 'completed', Status: 'completed', Elapsed: '00:00:00.0010000' } },
    ])
    if (request.url.endsWith('/operations/csharp-op/events?FromSequence=0')) return response([
      { OperationId: 'csharp-op', Sequence: 1, Payload: { Kind: 'content-produced', ContentRef: csharpRef, MediaType: 'text/plain', Size: 80 } },
      { OperationId: 'csharp-op', Sequence: 2, Payload: { Kind: 'typed-result', Result: { ResultType: 'artifact-render', Outcome: 'succeeded', ContentRef: csharpRef, MediaType: 'text/plain', LinkedRanges: [], Diagnostics: [], Identity: { ReleaseId: releaseId, ProcessorId: 'artifacts-default', ProcessorVersion: '10.1.0', WorkerImageId: artifactImage } } } },
      { OperationId: 'csharp-op', Sequence: 3, Payload: { Kind: 'completed', Status: 'completed', Elapsed: '00:00:00.0010000' } },
    ])
    if (request.url.endsWith(`/contents/sha256/${ilRef.slice(7)}`)) return response(ilContent, 200, { ETag: options.ilEtag ?? `\"${ilRef}\"` })
    if (request.url.endsWith(`/contents/sha256/${csharpRef.slice(7)}`)) return response(csharpContent, 200, { ETag: options.csharpEtag ?? `\"${csharpRef}\"` })
    throw new Error(`Unexpected request ${request.method} ${request.url}`)
  }
}

test('smoke writes binding evidence only after exact build, operation, event, and CAS checks', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const calls = []
  const result = await runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, fetch: fixtureFetch(calls), now: fixedNow,
    sleep: async () => {}, internalToken: 'x'.repeat(32), profileDirectory, runtimeMatrixPath, artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  })
  const row = result.rows[0]
  assert.equal(row.verification.status, 'smoke-passed')
  assert.equal(row.verification.smoke.compile, 'passed')
  assert.equal(row.verification.smoke.ilDecompile, 'passed')
  assert.equal(row.verification.evidence.artifactPipeline.referenceSetId, 'netcoreapp2.0-ref')
  assert.equal(row.verification.evidence.artifactPipeline.services.roslyn.id, 'roslyn-stable')
  assert.equal(row.verification.evidence.artifactPipeline.services.artifactStore.id, 'artifact-store')
  assert.equal(row.verification.evidence.artifactPipeline.services.artifactsDefault.id, 'artifacts-default')
  assert.equal(row.verification.evidence.artifactPipeline.services.roslyn.referenceSetAttestation.Id, 'netcoreapp2.0-ref')
  assert.equal(calls.every(call => call.headers.get('Authorization') === `Bearer ${'x'.repeat(32)}`), true)
  assert.equal(JSON.parse(fs.readFileSync(resultsPath, 'utf8')).rows[0].verification.smoke.compile, 'passed')
})

test('coreclr-wine profile passes the compile, IL, and decompiled C# artifact pipeline', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath, profileId } = temporaryResults(t, {
    id: 'wine-dotnet-core-2.0-x64', family: 'coreclr-wine',
  })
  const calls = []
  const result = await runRuntimeArtifactSmokes({
    profileIds: [profileId], resultsPath, profileDirectory, runtimeMatrixPath, fetch: fixtureFetch(calls), now: fixedNow,
    sleep: async () => {}, internalToken: 'x'.repeat(32),
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  })
  const verification = result.rows[0].verification
  assert.equal(verification.status, 'smoke-passed')
  assert.equal(verification.smoke.compile, 'passed')
  assert.equal(verification.smoke.ilDecompile, 'passed')
  assert.equal(calls.some(call => call.url.endsWith('/roslyn/api/v1/build')), true)
  assert.equal(calls.filter(call => call.url.endsWith('/artifacts/api/v1/artifact-renders')).length, 2)
  assert.equal(JSON.parse(fs.readFileSync(resultsPath, 'utf8')).rows[0].verification.status, 'smoke-passed')
})

test('coreclr-wine profile without the CoreCLR runtime-family contract is rejected before contacting services', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath, profileId } = temporaryResults(t, {
    id: 'wine-dotnet-core-2.0-x64', family: 'coreclr-wine', acceptedRuntimeFamilies: [],
  })
  const before = fs.readFileSync(resultsPath, 'utf8')
  await assert.rejects(runRuntimeArtifactSmokes({
    profileIds: [profileId], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: async () => { throw new Error('services must not be contacted') },
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  }), /does not accept the required CoreCLR managed artifact contract/)
  assert.equal(fs.readFileSync(resultsPath, 'utf8'), before)
})

test('failed artifact assertion leaves the original result file untouched', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const before = fs.readFileSync(resultsPath, 'utf8')
  await assert.rejects(
    runRuntimeArtifactSmokes({
      profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, fetch: fixtureFetch([], { csharpText: 'missing' }), now: fixedNow,
      sleep: async () => {}, profileDirectory, runtimeMatrixPath, artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
    }),
    /lost the probe method/,
  )
  assert.equal(fs.readFileSync(resultsPath, 'utf8'), before)
})

test('profile SHA drift rejects the result row before contacting services', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const profilePath = path.join(profileDirectory, 'dotnet-core-2.0-linux-x64.json')
  fs.appendFileSync(profilePath, ' ')
  const before = fs.readFileSync(resultsPath, 'utf8')
  await assert.rejects(
    runRuntimeArtifactSmokes({
      profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath,
      fetch: async () => { throw new Error('services must not be contacted') }, now: fixedNow,
      profileDirectory, runtimeMatrixPath, artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
    }),
    /SHA binding/,
  )
  assert.equal(fs.readFileSync(resultsPath, 'utf8'), before)
})

test('matrix drift rejects the result row before contacting services', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const matrix = JSON.parse(fs.readFileSync(runtimeMatrixPath, 'utf8'))
  matrix.coreClr[0].referencePackage.packageContentHash = 'sha512-drifted'
  fs.writeFileSync(runtimeMatrixPath, `${JSON.stringify(matrix)}\n`)
  await assert.rejects(runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: async () => { throw new Error('services must not be contacted') },
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  }), /runtime matrix SHA binding/)
})

test('Roslyn attestation drift rejects before a build is submitted', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const calls = []
  await assert.rejects(runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch(calls, { configureRoslynDescriptor: value => { value.ReferenceSets[0].Digest = 'sha512-drifted' } }),
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  }), /attestation does not match/)
  assert.equal(calls.some(call => call.url.endsWith('/api/v1/build')), false)
})

test('release divergence rejects before a build is submitted', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const calls = []
  await assert.rejects(runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch(calls, { configureArtifactDescriptor: value => { value.Service.ReleaseId = 'other-release' } }),
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  }), /same ReleaseId/)
  assert.equal(calls.some(call => call.url.endsWith('/api/v1/build')), false)
})

test('manifest artifact, reference set, TFM, format, and runtime binding mismatches reject before rendering', async t => {
  const mutations = [
    value => { value.ArtifactId = digest('other') },
    value => { value.ReferenceSetId = 'other-ref' },
    value => { value.TargetFramework = 'netcoreapp3.1' },
    value => { value.ArtifactFormat = 'other-format' },
    value => { value.RuntimeRequirement.Family = 'other' },
    value => { value.RuntimeRequirement.Frameworks[0].Name = 'Microsoft.NETCore.App.Ref' },
    value => { value.RuntimeRequirement.Frameworks[0].MinimumVersion = '9.9.9' },
  ]
  for (const mutate of mutations) {
    const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
    const calls = []
    await assert.rejects(runRuntimeArtifactSmokes({
      profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
      fetch: fixtureFetch(calls, { configureManifest: mutate }),
      artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
    }), /manifest|runtime requirement/)
    assert.equal(calls.some(call => call.url.endsWith('/api/v1/artifact-renders')), false)
  }
})

test('CAS digest, ETag, and strict UTF-8 are verified before evidence is written', async t => {
  const verifyFailure = async options => {
    const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
    const before = fs.readFileSync(resultsPath, 'utf8')
    await assert.rejects(runRuntimeArtifactSmokes({
      profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
      fetch: fixtureFetch([], options), artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
    }), /ContentRef|ETag|strict UTF-8/)
    assert.equal(fs.readFileSync(resultsPath, 'utf8'), before)
  }
  await verifyFailure({ ilContent: 'tampered' })
  await verifyFailure({ ilEtag: '"wrong"' })
  await verifyFailure({ ilContent: Uint8Array.of(0xc3, 0x28), ilRefContent: Uint8Array.of(0xc3, 0x28) })
})

test('default poll window covers completion after the former 100-poll limit', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const calls = []
  await runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch(calls, { completedAfter: 151 }), sleep: async () => {},
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  })
  assert.equal(calls.filter(call => call.url.endsWith('/operations/il-op')).length, 151)
})

test('CLI rejects an incomplete endpoint contract before making requests', async () => {
  const output = { log() {}, error() {} }
  assert.equal(await runRuntimeArtifactSmokeCli(['--profile', 'dotnet-core-2.0-linux-x64'], { output }), 1)
})

test('unavailable capability is rejected before a build is submitted', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const calls = []
  await assert.rejects(runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch(calls, { configureRoslynDescriptor: value => { value.Capabilities[0].Available = false } }),
    internalToken: 'x'.repeat(32), artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  }), /not available/)
  assert.equal(calls.some(call => call.url.endsWith('/api/v1/build')), false)
})

test('missing exact reference-set attestation is rejected before a build is submitted', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const calls = []
  await assert.rejects(runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch(calls, { configureRoslynDescriptor: value => { value.ReferenceSets = [] } }),
    internalToken: 'x'.repeat(32), artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  }), /must attest reference set/)
  assert.equal(calls.some(call => call.url.endsWith('/api/v1/build')), false)
})

test('a stalled HTTP request is aborted by the hard request timeout', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const stalledFetch = (_url, init) => new Promise((resolve, reject) => {
    init.signal.addEventListener('abort', () => reject(init.signal.reason), { once: true })
  })
  await assert.rejects(runRuntimeArtifactSmokes({
    profileIds: ['dotnet-core-2.0-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: stalledFetch, requestTimeoutMilliseconds: 5,
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  }), /timed out|timeout/i)
})

test('CLI rejects secrets supplied as command-line arguments', async () => {
  const output = { log() {}, error() {} }
  assert.equal(await runRuntimeArtifactSmokeCli(['--internal-token', 'secret'], { output }), 1)
})
