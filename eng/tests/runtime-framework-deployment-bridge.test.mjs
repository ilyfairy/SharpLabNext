import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { buildRuntimeFrameworkDeploymentBridge, runRuntimeFrameworkDeploymentBridgeCli } from '../smoke/runtime-framework-deployment-bridge.mjs'

const hash = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const profileId = 'wine-netfx48-linux-x64'

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-bridge-')); t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const profiles = path.join(root, 'profiles'); const output = path.join(root, 'output'); fs.mkdirSync(profiles)
  const profile = { schemaVersion: 1, id: profileId, image: 'sharplabnext/runtime-wine-netfx48-linux-x64:candidate', family: 'netfx-clr-wine', acceptedRuntimeFamilies: ['netfx-clr-wine'], acceptedFrameworks: [{ name: '.NETFramework', exactVersion: '4.8' }], runtimeVersion: '4.8', runtimeCommit: 'not-applicable', jitVersion: 'not-applicable', jitCommit: 'not-applicable', runtimeImageId: 'candidate', rid: 'linux-x64', architecture: 'x64', acceptedArtifactFormats: ['dotnet-framework-managed-pe-v1'], capabilities: ['run'], providedRuntimeFeatureTags: ['runtime.netfx48-wine'], providedMetadataFeatureTags: [], allowedSecurityPolicyIds: ['runtime-job-wine-netfx'], container: { isolationKind: 'wine', environmentKind: 'wine', executionUser: '0:0', winePrefixPath: '/opt/wine-netfx-clr4' }, operations: { run: { implementationId: 'runner', pathStyle: 'wine-z', command: { executable: 'wine64', argv: ['runner', '{entryAssembly}'] } } }, securityPolicies: [{ id: 'runtime-job-wine-netfx', memoryBytes: 1, nanoCpus: 1, pidsLimit: 1, maximumDurationSeconds: 1, maximumArtifactBytes: 1, maximumOutputBytes: 1, tmpfsBytes: 1 }] }
  const profileBytes = Buffer.from(`${JSON.stringify(profile)}\n`); fs.writeFileSync(path.join(profiles, `${profileId}.json`), profileBytes)
  const imageId = hash('image'); const profileSha256 = hash(profileBytes)
  const results = { schemaVersion: 1, rows: [{ profileId, matrixTargetId: 'netfx48', family: 'netfx-clr-wine', runtimeVersion: '4.8', candidateImage: profile.image, referenceSetId: 'netfx48-managed-ref', profileSha256, image: { imageId }, expected: { capabilities: ['run'] }, verification: { status: 'smoke-passed', evidence: { artifactPipeline: { profileSha256, imageId, referenceSetId: 'netfx48-managed-ref', artifactRef: hash('library'), compilePassed: true, ilPassed: true, decompiledCSharpPassed: true, matrix: { targetFramework: 'net48' }, services: { roslyn: { id: 'roslyn-stable-netfx48', releaseId: 'old', workerImageId: hash('roslyn'), referenceSetAttestation: {} } } }, supervisorOneShot: { profileSha256, imageId, identity: { RuntimeVersion: '4.8', RuntimeCommit: 'not-applicable', RuntimeImageId: imageId, Rid: 'linux-x64', Architecture: 'x64' }, stdoutMarker: 'SLN-FRAMEWORK-SUPERVISOR-V1' } } } }] }
  const catalog = { schemaVersion: 1, revision: 'old', releaseId: 'old', runtimes: [{ id: profileId, displayName: 'Framework', family: 'old', resolvedVersion: '4.8', rid: 'linux-x64', architecture: 'x64', acceptedArtifactFormats: [], capabilities: [], runtimeImageId: hash('old'), availability: { installed: true, health: 'healthy' } }] }
  const lock = { schemaVersion: 1, releaseId: 'old', resolvedAt: '2026-01-01T00:00:00Z', components: { [profileId]: { kind: 'runtime', resolvedVersion: '4.8' } } }
  for (const [name, value] of [['results.json', results], ['catalog.json', catalog], ['lock.json', lock]]) fs.writeFileSync(path.join(root, name), `${JSON.stringify(value)}\n`)
  return { root, profiles, output, imageId, profileSha256, options: { profileId, releaseId: 'runtime-matrix-current', resultsPath: path.join(root, 'results.json'), catalogPath: path.join(root, 'catalog.json'), lockPath: path.join(root, 'lock.json'), profileDirectory: profiles, outputDirectory: output } }
}

test('bridge binds verified Framework identity into ignored Catalog, lock, Supervisor and Compose inputs', t => {
  const value = fixture(t); const result = buildRuntimeFrameworkDeploymentBridge(value.options)
  const catalog = JSON.parse(fs.readFileSync(path.join(value.output, 'catalog.json'))); const runtime = catalog.runtimes[0]
  assert.equal(catalog.releaseId, 'runtime-matrix-current'); assert.match(catalog.revision, /^runtime-framework-functional-[0-9a-f]{12}$/)
  assert.equal(runtime.runtimeImageId, value.imageId); assert.deepEqual(runtime.capabilities, ['run']); assert.equal(runtime.containerEnvironmentKind, 'wine')
  const lock = JSON.parse(fs.readFileSync(path.join(value.output, 'lock.json'))); assert.equal(lock.releaseId, 'runtime-matrix-current'); assert.equal(lock.components[profileId].imageId, value.imageId)
  const overlay = JSON.parse(fs.readFileSync(path.join(value.output, 'runtime-supervisor-overlay.json'))); assert.equal(overlay.RuntimeSupervisorProfileOverlay.Profiles[0].runtimeImageId, value.imageId); assert.equal(overlay.RuntimeSupervisorProfileOverlay.Profiles[0].securityPolicies, undefined)
  const compose = fs.readFileSync(path.join(value.output, 'compose.override.yaml'), 'utf8'); assert.match(compose, /appsettings\.RuntimeFramework\.json/); assert.match(compose, /DependencyHealth__Enabled: "false"/)
  assert.equal(result.manifest.validationOnly, true); assert.equal(Object.keys(result.manifest.files).length, 4)
})

test('stale Supervisor evidence rejects before any bridge file is written', t => {
  const value = fixture(t); const results = JSON.parse(fs.readFileSync(value.options.resultsPath)); results.rows[0].verification.evidence.supervisorOneShot.imageId = hash('stale'); fs.writeFileSync(value.options.resultsPath, JSON.stringify(results))
  assert.throws(() => buildRuntimeFrameworkDeploymentBridge(value.options), /no current real Supervisor/); assert.equal(fs.existsSync(value.output), false)
})

test('CLI requires a profile and reports a successful bridge without mutating source inputs', t => {
  const value = fixture(t); const messages = []; const output = { log: message => messages.push(message), error: message => messages.push(message) }
  assert.equal(runRuntimeFrameworkDeploymentBridgeCli([], { output }), 1)
  assert.equal(runRuntimeFrameworkDeploymentBridgeCli(['--profile', profileId], { ...value.options, output }), 0)
  assert.match(messages.at(-1), /Prepared validation Framework deployment bridge/)
})
