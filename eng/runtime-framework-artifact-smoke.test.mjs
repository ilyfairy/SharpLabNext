import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  runRuntimeFrameworkArtifactSmokeCli,
  runRuntimeFrameworkArtifactSmokes,
} from './runtime-framework-artifact-smoke.mjs'

const digest = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const fixedNow = () => new Date('2026-08-13T04:00:00.000Z')
const releaseId = 'runtime-matrix-current'
const protocol = () => ({ Major: 1, Minor: 0 })
const frameworkRows = [
  ['netfx20', '2.0', 'net20'], ['netfx30', '3.0', 'net30'], ['netfx35', '3.5', 'net35'],
  ['netfx40', '4.0', 'net40'], ['netfx45', '4.5', 'net45'], ['netfx451', '4.5.1', 'net451'],
  ['netfx452', '4.5.2', 'net452'], ['netfx46', '4.6', 'net46'], ['netfx461', '4.6.1', 'net461'],
  ['netfx462', '4.6.2', 'net462'], ['netfx47', '4.7', 'net47'], ['netfx471', '4.7.1', 'net471'],
  ['netfx472', '4.7.2', 'net472'], ['netfx48', '4.8', 'net48'],
]

function service(id, kind, capabilities) {
  return { Id: id, Kind: kind, ReleaseId: releaseId, Protocol: protocol(), Capabilities: capabilities, Status: 'ready' }
}

function capability(id, profileId) {
  return { Id: id, ContractVersion: 1, Available: true, ProfileIds: [profileId] }
}

function referenceSet(target) {
  const [id, _version, targetFramework] = target
  const isNet30 = id === 'netfx30'
  return {
    Id: `${id}-managed-ref`,
    TargetFramework: targetFramework,
    Digest: isNet30 ? digest('net30-composition') : `sha512-package-${id}`,
    ContentDigest: digest(`content-${id}`),
    Provenance: isNet30
      ? {
          Kind: 'nuget-package-composition', ResolvedVersion: 'net30-union-v1',
          Sources: [
            compositionSource('netfx20', 'base', 'all'),
            compositionSource('netfx35', 'extension', 'assembly-version:3.0.0.0'),
          ],
        }
      : { Kind: 'nuget-package', ResolvedVersion: '1.0.3', Package: `Microsoft.NETFramework.ReferenceAssemblies.${targetFramework}` },
  }
}

function compositionSource(id, role, selection) {
  const target = frameworkRows.find(row => row[0] === id)
  return {
    Role: role, Selection: selection, Package: `Microsoft.NETFramework.ReferenceAssemblies.${target[2]}`,
    ResolvedVersion: '1.0.3', SourceUri: `https://example.test/${id}.nupkg`,
    SourceArchiveDigest: `sha512:${id}`, PackageContentHash: `sha512-package-${id}`,
  }
}

function targetDocument(target) {
  const [id, version, targetFramework] = target
  if (id === 'netfx30') {
    return {
      id, version, targetFramework, clrGeneration: 'clr2', referenceSetId: `${id}-managed-ref`,
      referenceComposition: {
        kind: 'nuget-package-composition', resolvedVersion: 'net30-union-v1',
        sourceIdentityDigest: digest('net30-composition'),
        sources: [
          { role: 'base', targetId: 'netfx20', selection: 'all' },
          { role: 'extension', targetId: 'netfx35', selection: 'assembly-version:3.0.0.0' },
        ],
      },
    }
  }
  return {
    id, version, targetFramework, clrGeneration: id === 'netfx20' || id === 'netfx35' ? 'clr2' : 'clr4',
    referenceSetId: `${id}-managed-ref`,
    referencePackage: {
      id: `Microsoft.NETFramework.ReferenceAssemblies.${targetFramework}`, version: '1.0.3',
      url: `https://example.test/${id}.nupkg`, sha512: id, packageContentHash: `sha512-package-${id}`,
    },
  }
}

function profileFor(profileId, targetId) {
  const target = frameworkRows.find(row => row[0] === targetId)
  return {
    id: profileId,
    family: profileId === 'mono-6.12-linux-x64' ? 'mono' : 'netfx-clr-wine',
    acceptedRuntimeFamilies: profileId === 'mono-6.12-linux-x64' ? ['mono', 'netfx-clr-wine'] : ['netfx-clr-wine'],
    acceptedArtifactFormats: ['dotnet-framework-managed-pe-v1'],
    acceptedFrameworks: [{ name: '.NETFramework', exactVersion: target[1] }],
  }
}

function temporaryResults(t, selected = [['wine-netfx20-linux-x64', 'netfx20']]) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-artifact-smoke-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const filename = path.join(directory, 'results.json')
  const profileDirectory = path.join(directory, 'profiles')
  const runtimeMatrixPath = path.join(directory, 'runtime-matrix.json')
  fs.mkdirSync(profileDirectory)
  const profiles = selected.map(([profileId, targetId]) => {
    const bytes = Buffer.from(`${JSON.stringify(profileFor(profileId, targetId))}\n`)
    fs.writeFileSync(path.join(profileDirectory, `${profileId}.json`), bytes)
    return [profileId, targetId, bytes]
  })
  const matrix = {
    schemaVersion: 1,
    mono: { id: 'mono-6.12-linux-x64', referenceSetId: 'netfx48-managed-ref' },
    framework: { targets: frameworkRows.map(targetDocument) },
  }
  const matrixBytes = Buffer.from(`${JSON.stringify(matrix, null, 2)}\n`)
  fs.writeFileSync(runtimeMatrixPath, matrixBytes)
  fs.writeFileSync(filename, `${JSON.stringify({
    schemaVersion: 1,
    runtimeMatrixSha256: digest(matrixBytes),
    rows: profiles.map(([profileId, targetId, profileBytes]) => {
      const target = frameworkRows.find(row => row[0] === targetId)
      return {
        profileId, matrixTargetId: profileId === 'mono-6.12-linux-x64' ? profileId : targetId,
        family: profileId === 'mono-6.12-linux-x64' ? 'mono' : 'netfx-clr-wine',
        referenceSetId: `${targetId}-managed-ref`, profileSha256: digest(profileBytes), image: { imageId: digest(`image-${profileId}`) },
        expected: { capabilities: profileId === 'mono-6.12-linux-x64' ? ['run', 'jit-asm'] : ['run'], sourceMappingKind: 'none' },
        verification: { smoke: { runtimeIdentity: 'passed', run: 'passed', jit: profileId === 'mono-6.12-linux-x64' ? 'unverified' : 'not-applicable', mapping: 'not-applicable' } },
      }
    }),
  }, null, 2)}\n`)
  return { filename, profileDirectory, runtimeMatrixPath }
}

function response(value, status = 200, headers = {}) {
  const raw = typeof value === 'string' || Buffer.isBuffer(value) || value instanceof Uint8Array
  return new Response(raw ? value : JSON.stringify(value), {
    status, headers: { 'Content-Type': raw ? 'text/plain' : 'application/json', ...headers },
  })
}

function fixtureFetch(calls, options = {}) {
  const roslynImage = digest('roslyn-image')
  const artifactImage = digest('artifact-image')
  const artifactRef = digest('artifact')
  const ilText = options.ilText ?? '.method public static int32 RuntimeMatrixProbeMethod() cil managed'
  const csharpText = options.csharpText ?? 'public static int RuntimeMatrixProbeMethod(int value) => value + 1;'
  const ilRef = digest(options.ilRefContent ?? ilText)
  const csharpRef = digest(options.csharpRefContent ?? csharpText)
  return async (url, init = {}) => {
    const request = { url: String(url), method: init.method ?? 'GET', headers: new Headers(init.headers), body: init.body }
    calls.push(request)
    if (request.url.endsWith('/roslyn/api/v1/worker/describe')) {
      const descriptor = {
        Service: service('roslyn-stable-netfx48', 3, ['managed-pe']), InstanceId: 'roslyn-netfx-1',
        WorkerKind: 'toolchain', WorkerImageId: roslynImage, NegotiatedProtocol: protocol(), SupportedProtocolVersions: [protocol()],
        Capabilities: [capability('managed-pe', 'roslyn-stable-netfx48')], ProfileIds: ['roslyn-stable-netfx48'],
        StartedAtUtc: '2026-08-13T03:00:00Z', Identity: { compilerVersion: '5.6.0' }, ReferenceSets: frameworkRows.map(referenceSet),
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
        Service: service('artifacts-default', 4, ['il', 'decompiled-csharp']), InstanceId: 'artifacts-1',
        WorkerKind: 'artifact-processor', WorkerImageId: artifactImage, NegotiatedProtocol: protocol(), SupportedProtocolVersions: [protocol()],
        Capabilities: [capability('il', 'artifacts-default'), capability('decompiled-csharp', 'artifacts-default')],
        ProfileIds: ['artifacts-default'], StartedAtUtc: '2026-08-13T03:00:00Z', Identity: { ilspyVersion: '10.1.0' },
      }
      options.configureArtifactDescriptor?.(descriptor)
      return response(descriptor)
    }
    if (request.url.endsWith('/roslyn/api/v1/build')) {
      const body = JSON.parse(request.body)
      const target = frameworkRows.find(row => `${row[0]}-managed-ref` === body.ReferenceSetId)
      return response({ RequestId: body.RequestId, Result: {
        ResultType: 'build', Outcome: 'succeeded', ArtifactRef: artifactRef, WorkspaceRevision: 1, SelectionRevision: 1, Diagnostics: [],
        Identity: { ReleaseId: releaseId, LanguageId: 'csharp', ToolchainId: 'roslyn-stable-netfx48', CompilerVersion: '5.6.0', ReferenceSetId: body.ReferenceSetId, WorkerImageId: roslynImage },
      } })
    }
    if (request.url.endsWith(`/store/internal/v1/artifacts/sha256/${artifactRef.slice(7)}`)) {
      const build = calls.findLast(call => call.url.endsWith('/roslyn/api/v1/build'))
      const referenceSetId = JSON.parse(build.body).ReferenceSetId
      const target = frameworkRows.find(row => `${row[0]}-managed-ref` === referenceSetId)
      const descriptor = { Manifest: {
        ArtifactId: artifactRef, ReferenceSetId: referenceSetId, TargetFramework: target[2], ArtifactFormat: 'dotnet-framework-managed-pe-v1',
        RuntimeRequirement: { Family: 'netfx-clr-wine', Frameworks: [{ Name: '.NETFramework', MinimumVersion: target[1] }], Architecture: 'anycpu', RequiredRuntimeFeatureTags: [] },
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
      const count = calls.filter(call => call.url.endsWith(`/operations/${operationId}`)).length
      return response({ OperationId: operationId, RequestId: 'request', Kind: 'render-artifact', Status: count >= (options.completedAfter ?? 1) ? 'completed' : 'running', LastSequence: 3 })
    }
    if (request.url.endsWith('/operations/il-op/events?FromSequence=0')) return response([
      { OperationId: 'il-op', Sequence: 1, Payload: { Kind: 'content-produced', ContentRef: ilRef, MediaType: 'text/plain', Size: 80 } },
      { OperationId: 'il-op', Sequence: 2, Payload: { Kind: 'typed-result', Result: { ResultType: 'artifact-render', Outcome: 'succeeded', ContentRef: ilRef, MediaType: 'text/plain', LinkedRanges: [], Diagnostics: [], Identity: { ReleaseId: releaseId, ProcessorId: 'artifacts-default', ProcessorVersion: '10.1.0', WorkerImageId: artifactImage } } } },
      { OperationId: 'il-op', Sequence: 3, Payload: { Kind: 'completed', Status: 'completed' } },
    ])
    if (request.url.endsWith('/operations/csharp-op/events?FromSequence=0')) return response([
      { OperationId: 'csharp-op', Sequence: 1, Payload: { Kind: 'content-produced', ContentRef: csharpRef, MediaType: 'text/plain', Size: 80 } },
      { OperationId: 'csharp-op', Sequence: 2, Payload: { Kind: 'typed-result', Result: { ResultType: 'artifact-render', Outcome: 'succeeded', ContentRef: csharpRef, MediaType: 'text/plain', LinkedRanges: [], Diagnostics: [], Identity: { ReleaseId: releaseId, ProcessorId: 'artifacts-default', ProcessorVersion: '10.1.0', WorkerImageId: artifactImage } } } },
      { OperationId: 'csharp-op', Sequence: 3, Payload: { Kind: 'completed', Status: 'completed' } },
    ])
    if (request.url.endsWith(`/contents/sha256/${ilRef.slice(7)}`)) return response(options.ilContent ?? ilText, 200, { ETag: options.ilEtag ?? `"${ilRef}"` })
    if (request.url.endsWith(`/contents/sha256/${csharpRef.slice(7)}`)) return response(options.csharpContent ?? csharpText, 200, { ETag: options.csharpEtag ?? `"${csharpRef}"` })
    throw new Error(`Unexpected request ${request.method} ${request.url}`)
  }
}

function run(options) {
  return runRuntimeFrameworkArtifactSmokes({
    ...options, now: fixedNow, sleep: async () => {}, internalToken: 'x'.repeat(32),
    artifactStoreUrl: 'http://test/store', roslynWorkerUrl: 'http://test/roslyn', artifactWorkerUrl: 'http://test/artifacts',
  })
}

test('Wine Framework exact row writes artifact evidence after Framework contract and CAS checks', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const calls = []
  const result = await run({ profileIds: ['wine-netfx20-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath, fetch: fixtureFetch(calls) })
  const row = result.rows[0]
  assert.equal(row.verification.smoke.compile, 'passed')
  assert.equal(row.verification.smoke.ilDecompile, 'passed')
  assert.equal(row.verification.evidence.artifactPipeline.referenceSetId, 'netfx20-managed-ref')
  assert.equal(row.verification.evidence.artifactPipeline.matrix.runtimeFramework.minimumVersion, '2.0')
  assert.equal(row.verification.evidence.artifactPipeline.services.roslyn.id, 'roslyn-stable-netfx48')
  assert.equal(JSON.parse(calls.find(call => call.url.endsWith('/api/v1/build')).body).ToolchainId, 'roslyn-stable-netfx48')
  assert.equal(calls.every(call => call.headers.get('Authorization') === `Bearer ${'x'.repeat(32)}`), true)
})

test('all 14 Wine Framework reference sets and Mono netfx48 use their exact matrix binding', async t => {
  const selected = [
    ...frameworkRows.map(([id]) => [`wine-${id.replace('netfx', 'netfx')}-linux-x64`, id]),
    ['mono-6.12-linux-x64', 'netfx48'],
  ]
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t, selected)
  const result = await run({ profileIds: selected.map(([id]) => id), resultsPath, profileDirectory, runtimeMatrixPath, fetch: fixtureFetch([]) })
  for (const [profileId, targetId] of selected) {
    const row = result.rows.find(value => value.profileId === profileId)
    assert.equal(row.verification.evidence.artifactPipeline.referenceSetId, `${targetId}-managed-ref`)
    assert.equal(row.verification.evidence.artifactPipeline.matrix.targetFramework, frameworkRows.find(value => value[0] === targetId)[2])
  }
  assert.equal(result.rows.find(row => row.profileId === 'mono-6.12-linux-x64').verification.evidence.artifactPipeline.matrix.runtimeFramework.minimumVersion, '4.8')
})

test('netfx30 composition identity and Framework manifest drift reject before rendering', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t, [['wine-netfx30-linux-x64', 'netfx30']])
  const calls = []
  await assert.rejects(run({
    profileIds: ['wine-netfx30-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch(calls, { configureManifest: manifest => { manifest.RuntimeRequirement.Frameworks[0].MinimumVersion = '3.5' } }),
  }), /runtime framework does not match/)
  assert.equal(calls.some(call => call.url.endsWith('/api/v1/artifact-renders')), false)
})

test('netfx30 composition source provenance drift rejects before compilation', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t, [['wine-netfx30-linux-x64', 'netfx30']])
  const calls = []
  await assert.rejects(run({
    profileIds: ['wine-netfx30-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch(calls, { configureRoslynDescriptor: descriptor => {
      descriptor.ReferenceSets.find(value => value.Id === 'netfx30-managed-ref').Provenance.Sources[1].Selection = 'all'
    } }),
  }), /composition source 1 does not match/)
  assert.equal(calls.some(call => call.url.endsWith('/api/v1/build')), false)
})

test('failed artifact or profile binding leaves result evidence untouched', async t => {
  const { filename: resultsPath, profileDirectory, runtimeMatrixPath } = temporaryResults(t)
  const before = fs.readFileSync(resultsPath, 'utf8')
  await assert.rejects(run({
    profileIds: ['wine-netfx20-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: fixtureFetch([], { csharpContent: 'missing', csharpRefContent: 'missing' }),
  }), /lost the probe method/)
  assert.equal(fs.readFileSync(resultsPath, 'utf8'), before)

  fs.appendFileSync(path.join(profileDirectory, 'wine-netfx20-linux-x64.json'), ' ')
  await assert.rejects(run({
    profileIds: ['wine-netfx20-linux-x64'], resultsPath, profileDirectory, runtimeMatrixPath,
    fetch: async () => { throw new Error('services must not be contacted') },
  }), /SHA binding/)
  assert.equal(fs.readFileSync(resultsPath, 'utf8'), before)
})

test('CLI rejects incomplete endpoints and command-line secrets', async () => {
  const output = { log() {}, error() {} }
  assert.equal(await runRuntimeFrameworkArtifactSmokeCli(['--profile', 'wine-netfx20-linux-x64'], { output }), 1)
  assert.equal(await runRuntimeFrameworkArtifactSmokeCli(['--internal-token', 'secret'], { output }), 1)
})
