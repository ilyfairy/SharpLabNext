import assert from 'node:assert/strict'
import childProcess from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  buildRuntimeMatrixDeploymentBridge,
  renameSyncWithRetry,
  runRuntimeMatrixDeploymentBridgeCli,
} from '../smoke/runtime-matrix-deployment-bridge.mjs'
import {
  formalRuntimeCandidateProfileIds,
  readRuntimeMatrix,
} from '../runtime-candidate-environment.mjs'
import { validateJsonSchemaInstance } from '../release/json-schema-instance-validation.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const sourceMatrix = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
const sourceCatalog = path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json')
const sourceLock = path.join(repositoryRoot, 'profiles', 'lock.json')
const sourceProfiles = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')
const hash = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`)

function readJson(filename) { return JSON.parse(fs.readFileSync(filename, 'utf8')) }
function writeJson(filename, value) { fs.writeFileSync(filename, jsonBytes(value)) }

function matrixBindings(matrix) {
  const result = new Map()
  for (const row of matrix.coreClr) {
    result.set(`${row.id}-linux-x64`, {
      row,
      candidateTarget: 'runtime-dotnet-matrix-candidate',
      declaredCapabilities: row.linuxCapability.capabilities,
    })
    if (Number.parseInt(row.channel, 10) >= 5) {
      result.set(`wine-${row.id}-linux-x64`, {
        row,
        candidateTarget: 'runtime-wine-dotnet-matrix-candidate',
        declaredCapabilities: row.wineCapability.capabilities,
      })
    }
  }
  result.set(matrix.mono.id, {
    row: matrix.mono,
    candidateTarget: 'runtime-mono-matrix-candidate',
    declaredCapabilities: matrix.mono.capability.capabilities,
  })
  for (const row of matrix.framework.targets) {
    result.set(`wine-${row.id}-linux-x64`, {
      row,
      candidateTarget: 'runtime-wine-framework-matrix-shared-candidate',
      declaredCapabilities: row.capability.capabilities,
    })
  }
  return result
}

function createFixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-runtime-matrix-bridge-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const profileDirectory = path.join(root, 'profiles')
  const outputDirectory = path.join(root, 'output')
  fs.mkdirSync(profileDirectory)
  const matrixPath = path.join(root, 'runtime-matrix.json')
  const catalogPath = path.join(root, 'catalog.json')
  const lockPath = path.join(root, 'lock.json')
  const resultsPath = path.join(root, 'results.json')
  fs.copyFileSync(sourceMatrix, matrixPath)
  fs.copyFileSync(sourceCatalog, catalogPath)
  fs.copyFileSync(sourceLock, lockPath)
  const matrix = readRuntimeMatrix(matrixPath)
  const ids = formalRuntimeCandidateProfileIds(matrix)
  const bindings = matrixBindings(matrix)
  const rows = []
  for (const [index, profileId] of ids.entries()) {
    const profileBytes = fs.readFileSync(path.join(sourceProfiles, `${profileId}.json`))
    const profile = JSON.parse(profileBytes)
    fs.writeFileSync(path.join(profileDirectory, `${profileId}.json`), profileBytes)
    const matrixBinding = bindings.get(profileId)
    const imageId = hash(`image:${profileId}:${index}`)
    const profileSha256 = hash(profileBytes)
    const sourceMappingKind = profile.operations?.jit?.sourceMappingKind ?? 'none'
    rows.push({
      profileId,
      matrixTargetId: matrixBinding.row.id,
      candidateTarget: matrixBinding.candidateTarget,
      family: profile.family,
      runtimeVersion: matrixBinding.row.version ?? matrixBinding.row.resolvedVersion,
      referenceSetId: matrixBinding.row.referenceSetId,
      profileSha256,
      candidateImage: profile.image,
      expected: {
        capabilities: [...profile.capabilities],
        runImplementationId: profile.operations?.run?.implementationId ?? null,
        jitImplementationId: profile.operations?.jit?.implementationId ?? null,
        sourceMappingKind,
      },
      image: {
        reference: profile.image,
        imageId,
        operatingSystem: 'linux',
        architecture: 'amd64',
        labels: { 'com.sharplabnext.runtime-profile': profileId },
        inspectionError: null,
      },
      verification: {
        status: 'smoke-passed',
        reason: null,
        smoke: {
          runtimeIdentity: 'passed',
          compile: 'passed',
          run: profile.capabilities.includes('run') ? 'passed' : 'not-applicable',
          ilDecompile: 'passed',
          jit: profile.capabilities.includes('jit-asm') ? 'passed' : 'not-applicable',
          mapping: sourceMappingKind === 'none' ? 'not-applicable' : 'passed',
        },
        evidence: {
          runtimeSmoke: { profileSha256, imageId },
          artifactPipeline: {
            profileSha256,
            imageId,
            referenceSetId: matrixBinding.row.referenceSetId,
            compilePassed: true,
            ilPassed: true,
            decompiledCSharpPassed: true,
          },
        },
      },
    })
  }
  const results = {
    schemaVersion: 1,
    runtimeMatrixSha256: hash(fs.readFileSync(matrixPath)),
    rows,
  }
  writeJson(resultsPath, results)
  const options = {
    releaseId: 'runtime-matrix-current',
    resultsPath,
    matrixPath,
    catalogPath,
    lockPath,
    profileDirectory,
    outputDirectory,
  }
  const inputHashes = new Map([
    [resultsPath, hash(fs.readFileSync(resultsPath))],
    [matrixPath, hash(fs.readFileSync(matrixPath))],
    [catalogPath, hash(fs.readFileSync(catalogPath))],
    [lockPath, hash(fs.readFileSync(lockPath))],
  ])
  return { root, ids, rows, options, inputHashes }
}

function mutateResults(fixture, mutate) {
  const results = readJson(fixture.options.resultsPath)
  mutate(results)
  writeJson(fixture.options.resultsPath, results)
}

function assertNoOutput(fixture) {
  assert.equal(fs.existsSync(fixture.options.outputDirectory), false)
}

test('bridge transaction binds all canonical runtime identities into every generated input', t => {
  const fixture = createFixture(t)
  const result = buildRuntimeMatrixDeploymentBridge(fixture.options)
  assert.equal(result.manifest.profileCount, 34)
  assert.deepEqual(result.manifest.profiles.map(profile => profile.id), fixture.ids)
  assert.equal(result.overlay.RuntimeSupervisor.RequireDigestPinnedImages, true)

  const catalog = readJson(path.join(fixture.options.outputDirectory, 'catalog.json'))
  const lock = readJson(path.join(fixture.options.outputDirectory, 'lock.json'))
  const overlay = readJson(path.join(fixture.options.outputDirectory, 'runtime-supervisor-overlay.json'))
  const manifest = readJson(path.join(fixture.options.outputDirectory, 'manifest.json'))
  assert.deepEqual(validateJsonSchemaInstance(catalog, readJson(path.join(repositoryRoot, 'schemas', 'catalog.schema.json'))), [])
  assert.deepEqual(validateJsonSchemaInstance(lock, readJson(path.join(repositoryRoot, 'schemas', 'release-lock.schema.json'))), [])
  assert.equal(catalog.releaseId, 'runtime-matrix-current')
  assert.equal(lock.releaseId, 'runtime-matrix-current')
  assert.equal(overlay.RuntimeSupervisorProfileOverlay.Profiles.length, 34)
  const profiles = new Map(overlay.RuntimeSupervisorProfileOverlay.Profiles.map(profile => [profile.id, profile]))
  for (const row of fixture.rows) {
    const runtime = catalog.runtimes.find(value => value.id === row.profileId)
    const declaredCapabilities = matrixBindings(readRuntimeMatrix(fixture.options.matrixPath)).get(row.profileId).declaredCapabilities;
    assert.equal(runtime.runtimeImageId, row.image.imageId)
    assert.deepEqual(runtime.capabilities, declaredCapabilities)
    assert.equal(lock.components[row.profileId].imageId, row.image.imageId)
    assert.equal(profiles.get(row.profileId).image, row.image.imageId)
    assert.equal(profiles.get(row.profileId).runtimeImageId, row.image.imageId)
    assert.deepEqual(profiles.get(row.profileId).capabilities, declaredCapabilities)
    assert.equal(profiles.get(row.profileId).securityPolicies, undefined)
  }
  assert.deepEqual(profiles.get('dotnet-10-linux-x64').capabilities, ['run', 'jit-asm', 'inspection', 'execution-flow']);
  assert.deepEqual(profiles.get('dotnet-11-preview-linux-x64').capabilities, ['run', 'jit-asm', 'inspection', 'execution-flow']);
  for (const [filename, digest] of Object.entries(manifest.files)) {
    assert.equal(hash(fs.readFileSync(path.join(fixture.options.outputDirectory, filename))), digest)
  }
  assert.equal(fs.readFileSync(path.join(fixture.options.outputDirectory, 'compose.env'), 'utf8'), 'SHARPLABNEXT_RELEASE_ID=runtime-matrix-current\n')
  assert.deepEqual(fs.readdirSync(fixture.options.outputDirectory).sort(), [
    'catalog.json',
    'compose.env',
    'compose.override.yaml',
    'lock.json',
    'manifest.json',
    'runtime-supervisor-overlay.json',
  ])
  for (const [filename, digest] of fixture.inputHashes) assert.equal(hash(fs.readFileSync(filename)), digest)

  fs.writeFileSync(path.join(fixture.options.outputDirectory, 'stale.txt'), 'stale')
  const first = new Map(fs.readdirSync(fixture.options.outputDirectory).filter(name => name !== 'stale.txt').map(name => [name, hash(fs.readFileSync(path.join(fixture.options.outputDirectory, name)))]));
  buildRuntimeMatrixDeploymentBridge(fixture.options)
  assert.equal(fs.existsSync(path.join(fixture.options.outputDirectory, 'stale.txt')), false)
  for (const [name, digest] of first) {
    assert.equal(hash(fs.readFileSync(path.join(fixture.options.outputDirectory, name))), digest)
  }
  assert.equal(fs.readdirSync(fixture.root).some(name => name.includes('.staging') || name.includes('.backup')), false)
})

for (const [name, mutate, message] of [
  ['a missing canonical row', results => results.rows.pop(), /exactly the 34 canonical/],
  ['a duplicate canonical row', results => { results.rows[1] = structuredClone(results.rows[0]) }, /exactly the 34 canonical/],
  ['a stale runtime matrix digest', results => { results.runtimeMatrixSha256 = hash('stale') }, /current runtime matrix bytes/],
  ['a non-passing row', results => { results.rows[0].verification.status = 'unverified' }, /has not passed exact-version smoke/],
]) {
  test(`bridge rejects ${name} before creating output`, t => {
    const fixture = createFixture(t)
    mutateResults(fixture, mutate)
    assert.throws(() => buildRuntimeMatrixDeploymentBridge(fixture.options), message)
    assertNoOutput(fixture)
  })
}

for (const [name, mutate, message] of [
  ['a tag that was not the inspected reference', row => { row.image.reference = 'sharplabnext/other:candidate' }, /does not match its verified functional row/],
  ['a changed Runner implementation', row => { row.expected.runImplementationId = 'substituted-runner' }, /does not match its verified functional row/],
  ['stale image evidence', row => { row.verification.evidence.runtimeSmoke.imageId = hash('stale') }, /stale 'runtimeSmoke' evidence/],
  ['incomplete compilation evidence', row => { row.verification.evidence.artifactPipeline.ilPassed = false }, /incomplete current functional evidence/],
]) {
  test(`bridge rejects ${name}`, t => {
    const fixture = createFixture(t)
    mutateResults(fixture, results => mutate(results.rows[0]))
    assert.throws(() => buildRuntimeMatrixDeploymentBridge(fixture.options), message)
    assertNoOutput(fixture)
  })
}

test('bridge rejects duplicate Catalog rows and conflicting shared policy definitions', t => {
  const duplicateFixture = createFixture(t)
  const catalog = readJson(duplicateFixture.options.catalogPath)
  catalog.runtimes.push(structuredClone(catalog.runtimes.find(runtime => runtime.id === duplicateFixture.ids[0])))
  writeJson(duplicateFixture.options.catalogPath, catalog)
  assert.throws(() => buildRuntimeMatrixDeploymentBridge(duplicateFixture.options), /exactly one runtime/)
  assertNoOutput(duplicateFixture)

  const policyFixture = createFixture(t)
  const profileId = policyFixture.ids[1]
  const profilePath = path.join(policyFixture.options.profileDirectory, `${profileId}.json`)
  const profile = readJson(profilePath)
  profile.securityPolicies[0].memoryBytes += 1
  const bytes = jsonBytes(profile)
  fs.writeFileSync(profilePath, bytes)
  mutateResults(policyFixture, results => {
    const row = results.rows.find(value => value.profileId === profileId)
    row.profileSha256 = hash(bytes)
    for (const evidence of Object.values(row.verification.evidence)) evidence.profileSha256 = row.profileSha256
  })
  assert.throws(() => buildRuntimeMatrixDeploymentBridge(policyFixture.options), /differs between runtime profiles/)
  assertNoOutput(policyFixture)
})

test('bridge rejects output paths that overlap any source input', t => {
  const fixture = createFixture(t)
  assert.throws(
    () => buildRuntimeMatrixDeploymentBridge({ ...fixture.options, outputDirectory: fixture.root }),
    /must not overlap/,
  )
  for (const [filename, digest] of fixture.inputHashes) assert.equal(hash(fs.readFileSync(filename)), digest)
})

test('output transaction preserves a pre-existing non-directory target on failure', t => {
  const fixture = createFixture(t)
  fs.writeFileSync(fixture.options.outputDirectory, 'keep-existing-target')
  assert.throws(() => buildRuntimeMatrixDeploymentBridge(fixture.options), /must be a non-link directory/)
  assert.equal(fs.readFileSync(fixture.options.outputDirectory, 'utf8'), 'keep-existing-target')
  assert.equal(fs.readdirSync(fixture.root).some(name => name.includes('.staging') || name.includes('.backup')), false)
})

test('output transaction retries transient Windows rename failures', () => {
  const waits = []
  let attempts = 0
  renameSyncWithRetry('staging', 'output', {
    renameSync: () => {
      attempts += 1
      if (attempts < 3) throw Object.assign(new Error('sharing violation'), { code: 'EPERM' })
    },
    wait: milliseconds => waits.push(milliseconds),
  })

  assert.equal(attempts, 3)
  assert.deepEqual(waits, [25, 50])
})

test('output transaction does not retry non-transient rename failures', () => {
  let attempts = 0
  assert.throws(() => renameSyncWithRetry('staging', 'output', {
    renameSync: () => {
      attempts += 1
      throw Object.assign(new Error('destination exists'), { code: 'EEXIST' })
    },
    wait: () => assert.fail('non-transient rename failure must not wait'),
  }), /destination exists/)
  assert.equal(attempts, 1)
})

test('CLI reports help, invalid arguments and a successful 34-profile bridge', t => {
  const fixture = createFixture(t)
  const messages = []
  const output = { log: message => messages.push(['log', message]), error: message => messages.push(['error', message]) }
  assert.equal(runRuntimeMatrixDeploymentBridgeCli(['--help'], { output }), 0)
  assert.equal(runRuntimeMatrixDeploymentBridgeCli(['--unknown', 'value'], { ...fixture.options, output }), 1)
  assert.equal(runRuntimeMatrixDeploymentBridgeCli([], { ...fixture.options, output }), 0)
  assert.equal(messages.some(([, message]) => message.includes('Usage:')), true)
  assert.equal(messages.some(([kind, message]) => kind === 'error' && message.includes('Invalid or duplicate option')), true)
  assert.match(messages.at(-1)[1], /Prepared 34-profile validation runtime-matrix bridge/)
})

test('generated bridge merges with production Compose using its release environment', t => {
  const composeVersion = childProcess.spawnSync('docker', ['compose', 'version'], { encoding: 'utf8', windowsHide: true })
  if (composeVersion.error?.code === 'ENOENT' || composeVersion.status !== 0) {
    t.skip('Docker Compose is not available on this host.')
    return
  }
  const fixture = createFixture(t)
  buildRuntimeMatrixDeploymentBridge(fixture.options)
  const result = childProcess.spawnSync('docker', [
    'compose',
    '--env-file', path.join(fixture.options.outputDirectory, 'compose.env'),
    '--project-directory', repositoryRoot,
    '-f', path.join(repositoryRoot, 'deploy', 'compose.prod.yaml'),
    '-f', path.join(repositoryRoot, 'deploy', 'compose.generated.yaml'),
    '-f', path.join(fixture.options.outputDirectory, 'compose.override.yaml'),
    'config',
    '--quiet',
  ], { cwd: repositoryRoot, encoding: 'utf8', windowsHide: true })
  assert.equal(result.status, 0, `docker compose config failed:\n${result.stdout}\n${result.stderr}`)
})
