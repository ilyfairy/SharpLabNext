import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import {
  runtimePromotionPlanSignaturePath,
  serializeRuntimePromotionPlan,
  signRuntimePromotionPlan,
} from '../release/runtime-promotion-plan-signature.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')

const hex = character => character.repeat(64)

test('net30 composition generates its reference, presets, compatibility, and full worker allow-list', { timeout: 120_000 }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-net30-composition-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))

  const matrixPath = path.join(root, 'runtime-matrix.json')
  const catalogPath = path.join(root, 'catalog.json')
  const profileDirectory = path.join(root, 'candidates')
  const matrix = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'), 'utf8'));
  blockAllMatrixCapabilities(matrix)
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  fs.copyFileSync(path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json'), catalogPath)
  const catalogFixture = JSON.parse(fs.readFileSync(catalogPath, 'utf8'))
  catalogFixture.referenceSets.find(candidate => candidate.id === 'net5-ref').visibility = 'hidden'
  catalogFixture.runtimes.find(candidate => candidate.id === 'dotnet-5-linux-x64').visibility = 'hidden'
  catalogFixture.presets.find(candidate => candidate.id === 'csharp-roslyn-stable-dotnet-5').visibility = 'hidden'
  fs.writeFileSync(catalogPath, `${JSON.stringify(catalogFixture, null, 2)}\n`)

  const run = () => spawnSync(
    'dotnet',
    [
      'run', path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs'), '--',
      '--repository-root', repositoryRoot,
      '--matrix', matrixPath,
      '--catalog', catalogPath,
      '--profiles', profileDirectory,
      '--overwrite-profiles',
    ],
    {
      cwd: repositoryRoot,
      encoding: 'utf8',
      timeout: 110_000,
      windowsHide: true,
    },
  )
  const result = run()
  assert.equal(
    result.status,
    0,
    `generator failed\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
  )

  const generatedBytes = fs.readFileSync(catalogPath)
  assert.equal(generatedBytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf])), false)
  assert.equal(generatedBytes.includes(0x0d), false)
  const generatedText = generatedBytes.toString('utf8')
  assert.match(generatedText, /C\+\+\/CLI/)
  const generated = JSON.parse(generatedText)
  const generatedProfileBytes = fs.readFileSync(path.join(profileDirectory, 'dotnet-10-linux-x64.json'));
  assert.equal(generatedProfileBytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf])), false)
  assert.equal(generatedProfileBytes.includes(0x0d), false)
  const target = matrix.framework.targets.find(candidate => candidate.id === 'netfx30')
  const reference = generated.referenceSets.find(candidate => candidate.id === target.referenceSetId)
  assert.equal(reference.targetFramework, 'net30')
  assert.equal(reference.digest, target.referenceComposition.sourceIdentityDigest)
  assert.equal(
    generated.compatibility.filter(rule =>
      rule.kind === 'toolchain-reference-set' &&
      rule.toId === target.referenceSetId).length,
    1,
  )
  assert.equal(
    generated.presets.filter(preset => preset.referenceSetId === target.referenceSetId).length,
    2,
  )
  const toolchain = generated.toolchains.find(candidate => candidate.id === 'roslyn-stable-netfx48')
  assert.deepEqual(
    new Set(toolchain.allowedReferenceSetIds),
    new Set(matrix.framework.targets.map(candidate => candidate.referenceSetId)),
  )
  const coreReferenceSetIds = new Set(matrix.coreClr.map(candidate => candidate.referenceSetId))
  for (const toolchainId of ['roslyn-stable', 'roslyn-main']) {
    const coreToolchain = generated.toolchains.find(candidate => candidate.id === toolchainId)
    assert.deepEqual(new Set(coreToolchain.allowedReferenceSetIds), coreReferenceSetIds)
  }
  assert.equal(generated.referenceSets.find(candidate => candidate.id === 'net5-ref').visibility, 'visible')
  assert.equal(generated.runtimes.find(candidate => candidate.id === 'dotnet-5-linux-x64').visibility, 'visible')
  assert.equal(generated.presets.find(candidate => candidate.id === 'csharp-roslyn-stable-dotnet-5').visibility, 'visible')

  matrix.framework.targets.find(candidate => candidate.id === 'netfx30')
    .referenceComposition.sourceIdentityDigest = `sha256:${hex('f')}`
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  const tampered = run()
  assert.notEqual(tampered.status, 0)
  assert.match(
    `${tampered.stdout}\n${tampered.stderr}`,
    /composition source identity does not match its locked digest/,
  )
})

test('payload fallback identity follows the selected CoreCLR platform', { timeout: 120_000 }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-platform-payload-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))

  const matrixPath = path.join(root, 'runtime-matrix.json')
  const catalogPath = path.join(root, 'catalog.json')
  const profileDirectory = path.join(root, 'candidates')
  const matrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  blockAllMatrixCapabilities(matrix)
  const target = matrix.coreClr.find(candidate => candidate.id === 'dotnet-5')
  assert.ok(target, 'fixture must contain the .NET 5 row')
  delete target.runtimeCommit
  delete target.jitCommit
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  fs.copyFileSync(path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json'), catalogPath)

  const result = spawnSync(
    'dotnet',
    [
      'run',
      path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs'),
      '--',
      '--repository-root', repositoryRoot,
      '--matrix', matrixPath,
      '--catalog', catalogPath,
      '--profiles', profileDirectory,
      '--overwrite-profiles',
    ],
    {
      cwd: repositoryRoot,
      encoding: 'utf8',
      timeout: 110_000,
      windowsHide: true,
    },
  )
  assert.equal(
    result.status,
    0,
    `generator failed\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
  )

  const linuxProfile = JSON.parse(fs.readFileSync(
    path.join(profileDirectory, 'dotnet-5-linux-x64.json'),
    'utf8',
  ))
  const wineProfile = JSON.parse(fs.readFileSync(
    path.join(profileDirectory, 'wine-dotnet-5-linux-x64.json'),
    'utf8',
  ))
  assert.equal(linuxProfile.runtimeCommit, `payload-sha512:${target.linux.sha512}`)
  assert.equal(wineProfile.runtimeCommit, `payload-sha512:${target.windows.sha512}`)
  assert.notEqual(wineProfile.runtimeCommit, linuxProfile.runtimeCommit)
})

test('Wine profile generation rejects execution users outside the closed identities', { timeout: 120_000 }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-wine-user-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))

  const matrixPath = path.join(root, 'runtime-matrix.json')
  const catalogPath = path.join(root, 'catalog.json')
  const profileDirectory = path.join(root, 'candidates')
  const matrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  blockAllMatrixCapabilities(matrix)
  const target = matrix.coreClr.find(candidate => candidate.id === 'dotnet-5')
  assert.ok(target, 'fixture must contain the .NET 5 row')
  target.wineCapability.executionUser = '1000:1000'
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  fs.copyFileSync(path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json'), catalogPath)

  const result = spawnSync(
    'dotnet',
    [
      'run',
      path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs'),
      '--',
      '--repository-root', repositoryRoot,
      '--matrix', matrixPath,
      '--catalog', catalogPath,
      '--profiles', profileDirectory,
      '--overwrite-profiles',
    ],
    {
      cwd: repositoryRoot,
      encoding: 'utf8',
      timeout: 110_000,
      windowsHide: true,
    },
  )

  assert.notEqual(result.status, 0)
  assert.match(
    `${result.stdout}\n${result.stderr}`,
    /Wine execution user '1000:1000' is not one of the closed runtime identities/,
  )
})

test('blocked runtime refresh revokes a stale allowed artifact edge', { timeout: 120_000 }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-matrix-generator-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))

  const matrixPath = path.join(root, 'runtime-matrix.json')
  const catalogPath = path.join(root, 'catalog.json')
  const profileDirectory = path.join(root, 'candidates')
  const matrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  blockAllMatrixCapabilities(matrix)
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  const catalog = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json'),
    'utf8',
  ))
  const fromId = 'dotnet-managed-pe-v1'
  const toId = 'dotnet-core-2.0-linux-x64'
  const matching = catalog.compatibility.filter(rule =>
    rule.kind === 'artifact-runtime' && rule.fromId === fromId && rule.toId === toId)
  assert.equal(matching.length, 1, 'fixture must begin with one semantic edge')
  matching[0].allowed = true
  delete matching[0].reason
  const runtime = catalog.runtimes.find(candidate => candidate.id === toId)
  assert.ok(runtime, 'fixture must contain the blocked runtime')
  runtime.availability = {
    installed: false,
    health: 'not-installed',
    reason: 'stale test candidate',
  }
  fs.writeFileSync(catalogPath, `${JSON.stringify(catalog, null, 2)}\n`)

  const result = spawnSync(
    'dotnet',
    [
      'run',
      path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs'),
      '--',
      '--repository-root', repositoryRoot,
      '--matrix', matrixPath,
      '--catalog', catalogPath,
      '--profiles', profileDirectory,
      '--overwrite-profiles',
    ],
    {
      cwd: repositoryRoot,
      encoding: 'utf8',
      timeout: 110_000,
      windowsHide: true,
    },
  )
  assert.equal(
    result.status,
    0,
    `generator failed\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
  )

  const generated = JSON.parse(fs.readFileSync(catalogPath, 'utf8'))
  assert.equal(
    generated.runtimes.some(runtime => runtime.id === 'mono-6.8-linux-x64'),
    false,
    'stale unpromoted Mono runtime must be removed when the source row identity changes',
  )
  assert.equal(
    generated.runtimes.filter(runtime => runtime.id === 'mono-6.12-linux-x64').length,
    1,
    'the current Mono runtime must be generated exactly once',
  )
  assert.equal(
    generated.compatibility.some(rule =>
      rule.fromId === 'mono-6.8-linux-x64' || rule.toId === 'mono-6.8-linux-x64'),
    false,
    'stale Mono compatibility edges must be removed with the runtime',
  )
  assert.equal(
    generated.presets.some(preset => preset.defaultRuntimeId === 'mono-6.8-linux-x64'),
    false,
    'stale Mono presets must be removed with the runtime',
  )
  assert.equal(
    fs.existsSync(path.join(profileDirectory, 'mono-6.8-linux-x64.json')),
    false,
    'the stale unpromoted Mono candidate profile must not survive generation',
  )
  assert.equal(
    fs.existsSync(path.join(profileDirectory, 'mono-6.12-linux-x64.json')),
    true,
    'the current Mono candidate profile must be generated',
  )
  const generatedEdges = generated.compatibility.filter(rule =>
    rule.kind === 'artifact-runtime' && rule.fromId === fromId && rule.toId === toId)
  assert.equal(generatedEdges.length, 1, 'generator must retain one semantic edge')
  assert.equal(generatedEdges[0].allowed, false)
  assert.match(generatedEdges[0].reason, /not selectable|preflight/i)
})

test('checked JIT source lock selects the isolated bridge only for Linux', { timeout: 120_000 }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-checked-jit-generator-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))

  const matrixPath = path.join(root, 'runtime-matrix.json')
  const catalogPath = path.join(root, 'catalog.json')
  const profileDirectory = path.join(root, 'candidates')
  const matrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  blockAllMatrixCapabilities(matrix)
  const target = matrix.coreClr.find(candidate => candidate.id === 'dotnet-7')
  assert.ok(target, 'fixture must contain the .NET 7 row')
  target.checkedJit = {
    sourceMappingKind: 'checked-jit-debug-info',
  }
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  fs.copyFileSync(path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json'), catalogPath)

  const result = spawnSync(
    'dotnet',
    [
      'run',
      path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs'),
      '--',
      '--repository-root', repositoryRoot,
      '--matrix', matrixPath,
      '--catalog', catalogPath,
      '--profiles', profileDirectory,
      '--overwrite-profiles',
    ],
    {
      cwd: repositoryRoot,
      encoding: 'utf8',
      timeout: 110_000,
      windowsHide: true,
    },
  )
  assert.equal(
    result.status,
    0,
    `generator failed\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
  )

  const linux = JSON.parse(fs.readFileSync(
    path.join(profileDirectory, 'dotnet-7-linux-x64.json'),
    'utf8',
  ))
  assert.equal(linux.operations.run.implementationId, 'sharplabnext-legacy-jit-inspector-v1')
  assert.equal(linux.operations.jit.implementationId, 'sharplabnext-checked-jit-bridge-v1')
  assert.equal(linux.operations.jit.sourceMappingKind, 'checked-jit-debug-info')
  assert.deepEqual(linux.operations.jit.command.argv, [
    '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
    'jit',
    '{entryAssembly}',
    '{methodFilter}',
  ])
  assert.equal(
    linux.layout.jitInspectorAssemblyPath,
    '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
  )

  const wine = JSON.parse(fs.readFileSync(
    path.join(profileDirectory, 'wine-dotnet-7-linux-x64.json'),
    'utf8',
  ))
  assert.equal(wine.operations.jit.implementationId, 'sharplabnext-legacy-jit-inspector-v1')
  assert.equal(wine.operations.jit.sourceMappingKind, 'none')
})

test('blocked modern profiler rows retain one closed Runner and JIT provider contract', { timeout: 120_000 }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-profiler-generator-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))

  const matrixPath = path.join(root, 'runtime-matrix.json')
  const catalogPath = path.join(root, 'catalog.json')
  const profileDirectory = path.join(root, 'candidates')
  const matrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  blockAllMatrixCapabilities(matrix)
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  const catalog = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json'),
    'utf8',
  ))
  const modernProfileIds = new Set(['dotnet-10-linux-x64', 'dotnet-11-preview-linux-x64'])
  catalog.runtimes = catalog.runtimes.filter(runtime => !modernProfileIds.has(runtime.id))
  fs.writeFileSync(catalogPath, `${JSON.stringify(catalog, null, 2)}\n`)

  const result = spawnSync(
    'dotnet',
    [
      'run',
      path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs'),
      '--',
      '--repository-root', repositoryRoot,
      '--matrix', matrixPath,
      '--catalog', catalogPath,
      '--profiles', profileDirectory,
      '--overwrite-profiles',
    ],
    {
      cwd: repositoryRoot,
      encoding: 'utf8',
      timeout: 110_000,
      windowsHide: true,
    },
  )
  assert.equal(
    result.status,
    0,
    `generator failed\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
  )

  const generatedCatalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'))
  for (const matrixId of ['dotnet-10', 'dotnet-11-preview']) {
    const row = matrix.coreClr.find(candidate => candidate.id === matrixId)
    assert.ok(row?.profilerProvider, `${matrixId} must retain its profiler provider lock`)
    assert.equal(row.linuxCapability.promotionState, 'blocked')

    const profileId = `${matrixId}-linux-x64`
    const profile = JSON.parse(fs.readFileSync(
      path.join(profileDirectory, `${profileId}.json`),
      'utf8',
    ))
    assert.equal(profile.runtimeCommit, row.runtimeCommit)
    assert.equal(profile.jitCommit, row.jitCommit)
    assert.deepEqual(profile.capabilities, ['run', 'jit-asm'])
    assert.equal(profile.operations.run.implementationId, 'sharplabnext-runner-v1')
    assert.deepEqual(profile.operations.run.command.argv, [
      '/opt/sharplabnext/SharpLabNext.Runner.dll',
      '{entryAssembly}',
      '--',
      '{arguments}',
    ])
    assert.equal(profile.operations.jit.implementationId, 'sharplabnext-jit-inspector-v1')
    assert.equal(profile.operations.jit.sourceMappingKind, 'linux-profiler')
    assert.equal(
      profile.operations.jit.profilerPath,
      '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
    )
    assert.equal(
      profile.layout.runnerAssemblyPath,
      '/opt/sharplabnext/SharpLabNext.Runner.dll',
    )
    assert.equal(
      profile.layout.jitInspectorAssemblyPath,
      '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
    )

    const catalogRuntime = generatedCatalog.runtimes.find(candidate => candidate.id === profileId)
    assert.ok(catalogRuntime)
    assert.equal(catalogRuntime.runtimeCommit, row.runtimeCommit)
    assert.equal(catalogRuntime.jitCommit, row.jitCommit)
    assert.equal(catalogRuntime.jitSourceMappingKind, 'linux-profiler')
    assert.deepEqual(catalogRuntime.capabilities, [])
    assert.equal(catalogRuntime.availability.installed, false)
  }
})

test('verified runtime generation consumes immutable receipt identities and JIT mapping', { timeout: 120_000 }, t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-matrix-promotion-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))

  const matrixPath = path.join(root, 'profiles', 'runtime-matrix.json')
  const catalogPath = path.join(root, 'profiles', 'catalog', 'catalog.json')
  const profileDirectory = path.join(root, 'profiles', 'runtimes', 'candidates')
  const validatorDirectory = path.join(root, 'eng', 'release')
  fs.mkdirSync(path.dirname(matrixPath), { recursive: true })
  fs.mkdirSync(path.dirname(catalogPath), { recursive: true })
  fs.mkdirSync(validatorDirectory, { recursive: true })
  const planKeys = crypto.generateKeyPairSync('ed25519')
  const planPublicKey = planKeys.publicKey.export({ type: 'spki', format: 'pem' })
  const planKeyId = `sha256:${crypto.createHash('sha256').update(planKeys.publicKey.export({ type: 'spki', format: 'der' })).digest('hex')}`;
  for (const fileName of [
    'json-schema-formats.mjs',
    'json-schema-instance-validation.mjs',
    'runtime-promotion-receipt-validation.mjs',
    'runtime-performance-evidence-validation.mjs',
    'runtime-capability-evidence-validation.mjs',
    'strict-owned-json.mjs',
    'runtime-promotion-plan-signature.mjs',
    'runtime-wine-operator-binding.mjs',
    'wine-coreclr-operator-receipt.mjs',
  ]) {
    const source = path.join(repositoryRoot, 'eng', 'release', fileName)
    const targetPath = path.join(validatorDirectory, fileName === 'runtime-promotion-receipt-validation.mjs' ? 'runtime-promotion-receipt-validation-impl.mjs' : fileName);
    fs.copyFileSync(source, targetPath)
  }
  const schemaDirectory = path.join(root, 'schemas')
  fs.mkdirSync(schemaDirectory, { recursive: true })
  for (const fileName of [
    'runtime-promotion-plan.schema.json',
    'runtime-promotion-receipt.schema.json',
  ]) {
    fs.copyFileSync(path.join(repositoryRoot, 'schemas', fileName), path.join(schemaDirectory, fileName))
  }
  // The production validator deliberately has no environment key override.
  // This test-local executable is the only place that injects its ephemeral
  // public key, keeping the generated-repository test self-contained.
  fs.writeFileSync(path.join(validatorDirectory, 'runtime-promotion-receipt-validation.mjs'), `
import fs from 'node:fs'
import path from 'node:path'
import { validateRuntimePromotionReceipts } from './runtime-promotion-receipt-validation-impl.mjs'

const publicKey = ${JSON.stringify(planPublicKey)}
const keyId = ${JSON.stringify(planKeyId)}
let root = process.cwd()
let matrixPath
for (let index = 2; index < process.argv.length; index += 2) {
  const option = process.argv[index]
  const value = process.argv[index + 1]
  if ((option !== '--repository-root' && option !== '--matrix') || value === undefined) process.exit(64)
  if (option === '--repository-root') root = path.resolve(value)
  else matrixPath = path.resolve(value)
}
matrixPath ??= path.join(root, 'profiles', 'runtime-matrix.json')
const failures = validateRuntimePromotionReceipts(
  JSON.parse(fs.readFileSync(matrixPath, 'utf8')),
  root,
  fs.readFileSync,
  { planSignaturePublicKey: publicKey, planSignatureKeyId: keyId },
)
if (failures.length > 0) {
  for (const failure of failures) console.error('promotion receipt error: ' + failure)
  process.exitCode = 1
}
`)

  const matrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  blockAllMatrixCapabilities(matrix)
  const target = matrix.coreClr.find(candidate => candidate.id === 'dotnet-core-2.1')
  assert.ok(target, 'fixture must contain the .NET Core 2.1 row')
  target.runtimeCommit = '1'.repeat(40)
  target.jitCommit = '2'.repeat(40)

  const profileId = 'dotnet-core-2.1-linux-x64'
  const writeFixtureCatalog = () => {
    const catalog = JSON.parse(fs.readFileSync(
      path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json'),
      'utf8',
    ))
    catalog.runtimes = catalog.runtimes.filter(candidate => candidate.id !== profileId)
    fs.writeFileSync(catalogPath, `${JSON.stringify(catalog, null, 2)}\n`)
  }
  const receipt = {
    schemaVersion: 2,
    planSha256: `sha256:${hex('0')}`,
    profileId,
    matrixTargetId: target.id,
    platform: 'linux',
    family: 'coreclr',
    resolvedVersion: target.version,
    image: {
      reference: `registry.example/sharplabnext/runtime@sha256:${hex('a')}`,
      imageId: `sha256:${hex('b')}`,
      sizeBytes: 536870912,
    },
    componentIdentity: {
      sourceUri: target.linux.url,
      sourceDigest: `sha512:${target.linux.sha512}`,
    },
    runtimeIdentity: {
      runtimeCommit: target.runtimeCommit,
      jitVersion: target.version,
      jitCommit: target.jitCommit,
    },
    operations: {
      run: {
        implementation: 'sharplabnext-legacy-jit-inspector-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
        assemblySha256: `sha256:${hex('c')}`,
      },
      jit: {
        implementation: 'sharplabnext-legacy-jit-inspector-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
        assemblySha256: `sha256:${hex('c')}`,
      },
    },
    sourceRevision: 'd'.repeat(40),
    checks: [
      {
        capability: 'run',
        result: 'passed',
        networkDisabled: true,
        supervisorSandbox: true,
        outputLimitValidated: true,
        sourceMappingKind: 'not-applicable',
        mappingSource: 'not-applicable',
        evidenceSha256: `sha256:${hex('e')}`,
      },
      {
        capability: 'jit-asm',
        result: 'passed',
        networkDisabled: true,
        supervisorSandbox: true,
        outputLimitValidated: true,
        sourceMappingKind: 'none',
        mappingSource: 'method',
        evidenceSha256: `sha256:${hex('f')}`,
      },
    ],
  }
  const preflightProfile = JSON.parse(fs.readFileSync(
    path.join(
      repositoryRoot,
      'profiles',
      'runtimes',
      'candidates',
      `${profileId}.json`,
    ),
    'utf8',
  ))
  preflightProfile.capabilities = ['run', 'jit-asm']
  preflightProfile.operations.jit = {
    implementationId: 'sharplabnext-legacy-jit-inspector-v1',
    pathStyle: 'unix',
    command: {
      executable: '/opt/sharplabnext/target-dotnet/dotnet',
      argv: [
        'exec',
        '--fx-version',
        target.version,
        '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
        '--runtime-version',
        target.version,
        'jit',
        '{entryAssembly}',
        '{methodFilter}',
      ],
    },
    sourceMappingKind: 'none',
  }
  fs.mkdirSync(profileDirectory, { recursive: true })
  fs.writeFileSync(
    path.join(profileDirectory, `${profileId}.json`),
    `${JSON.stringify(preflightProfile, null, 2)}\n`,
  )
  const securityPolicy = preflightProfile.securityPolicies[0]
  const performancePolicyRelativePath = 'profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json';
  const performancePolicyPath = path.join(root, ...performancePolicyRelativePath.split('/'))
  fs.mkdirSync(path.dirname(performancePolicyPath), { recursive: true })
  const performancePolicyBytes = fs.readFileSync(path.join(repositoryRoot, ...performancePolicyRelativePath.split('/')));
  fs.writeFileSync(performancePolicyPath, performancePolicyBytes)
  const performancePolicyDigest = `sha256:${crypto.createHash('sha256').update(performancePolicyBytes).digest('hex')}`;
  const writePerformanceEvidence = () => {
    const capabilities = receipt.checks.map(check => check.capability).sort()
    const jitCheck = receipt.checks.find(check => check.capability === 'jit-asm')
    const scenarios = { run: performanceScenario() }
    if (jitCheck !== undefined) scenarios.jit = performanceScenario()
    if (jitCheck !== undefined && !['none', 'not-applicable'].includes(jitCheck.sourceMappingKind)) {
      scenarios.mapping = performanceScenario()
    }
    const performanceEvidenceRelativePath = `profiles/runtime-promotion-evidence/${profileId}/performance.json`;
    const performanceEvidencePath = path.join(root, ...performanceEvidenceRelativePath.split('/'))
    const performanceEvidence = {
      schemaVersion: 1,
      planSha256: receipt.planSha256,
      profileId,
      image: { ...receipt.image },
      measurementHelper: {
        implementation: 'sharplabnext-runtime-cgroup-sidecar-v1',
        image: {
          reference: `registry.example/runtime-supervisor@sha256:${'7'.repeat(64)}`,
          imageId: `sha256:${'8'.repeat(64)}`,
          sizeBytes: 536870912,
        },
        entrypoint: '/usr/local/bin/sharplabnext-runtime-measurement',
        sourceRevision: receipt.sourceRevision,
        contentSha256:
          'sha256:f7645af4191d024c86769f3e39fd76ad237f537572c752fdfec3ff529aea9e4c',
      },
      sourceRevision: receipt.sourceRevision,
      policy: {
        id: 'runtime-image-linux-x64-v1',
        sha256: performancePolicyDigest,
      },
      capabilities,
      sourceMappingKind: jitCheck?.sourceMappingKind ?? 'not-applicable',
      environment: {
        runnerId: 'runtime-preflight-linux-x64-v2',
        operatingSystem: 'linux',
        architecture: 'x64',
        nanoCpus: 1000000000,
        memoryLimitBytes: 268435456,
      },
      completedAtUtc: '2026-07-22T00:00:00Z',
      result: 'passed',
      scenarios,
    }
    const bytes = Buffer.from(`${JSON.stringify(performanceEvidence, null, 2)}\n`)
    fs.mkdirSync(path.dirname(performanceEvidencePath), { recursive: true })
    fs.writeFileSync(performanceEvidencePath, bytes)
    receipt.performance = {
      result: 'passed',
      policyId: 'runtime-image-linux-x64-v1',
      policyPath: performancePolicyRelativePath,
      policySha256: performancePolicyDigest,
      evidencePath: performanceEvidenceRelativePath,
      evidenceSha256: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`,
    }
  }
  const writeEvidence = () => {
    for (const check of receipt.checks) {
      const relativePath = `profiles/runtime-promotion-evidence/${profileId}/${check.capability}.json`;
      const absolutePath = path.join(root, ...relativePath.split('/'))
      const helper = {
        role: 'helper',
        path: receipt.operations[check.capability === 'jit-asm' ? 'jit' : 'run'].assemblyPath,
        sha256: receipt.operations[check.capability === 'jit-asm' ? 'jit' : 'run'].assemblySha256,
        sizeBytes: 1048576,
        format: 'managed-pe',
        architecture: 'anycpu',
      }
      const runtimeHost = {
        role: 'runtime-host',
        path: '/opt/sharplabnext/target-dotnet/dotnet',
        sha256: `sha256:${hex('6')}`,
        sizeBytes: 1052672,
        format: 'elf',
        architecture: 'x64',
      }
      const entryAssembly = {
        path: '/workspace/app.dll',
        sha256: `sha256:${hex('8')}`,
      }
      const isJit = check.capability === 'jit-asm'
      const isMappedJit = isJit && check.sourceMappingKind !== 'none'
      const methodFilter = 'Program:Main'
      const currentProfile = JSON.parse(fs.readFileSync(
        path.join(profileDirectory, `${profileId}.json`),
        'utf8',
      ))
      const profileOperation = currentProfile.operations[isJit ? 'jit' : 'run']
      const command = [profileOperation.command.executable]
      for (const token of profileOperation.command.argv) {
        if (token === '{entryAssembly}') command.push(entryAssembly.path)
        else if (token === '{methodFilter}') command.push(methodFilter)
        else if (token === '{arguments}') {
          const runProbeArgument = {
            run: 'success-security',
            inspection: 'inspection',
            'execution-flow': 'execution-flow',
          }[check.capability]
          if (runProbeArgument === undefined) {
            throw new Error(`Unsupported Run capability '${check.capability}'.`)
          }
          command.push(runProbeArgument)
        } else command.push(token)
      }
      const lifecycleProbe = terminalStatus => ({
        result: 'passed',
        terminalStatus,
        containerRemoved: true,
        processTreeRemoved: true,
      })
      const evidence = {
        schemaVersion: 1,
        profileId,
        capability: check.capability,
        result: check.result,
        sourceRevision: receipt.sourceRevision,
        completedAtUtc: '2026-07-22T00:00:00Z',
        image: {
          reference: receipt.image.reference,
          imageId: receipt.image.imageId,
        },
        producer: {
          id: 'sharplabnext-runtime-preflight-v1',
          sourceRevision: receipt.sourceRevision,
          planSha256: receipt.planSha256,
        },
        artifacts: [
          helper,
          runtimeHost,
          ...(isJit ? [{
            role: 'jit-library',
            path: '/usr/share/dotnet/shared/Microsoft.NETCore.App/2.1.30/libclrjit.so',
            sha256: `sha256:${hex('9')}`,
            sizeBytes: 2097152,
            format: 'elf',
            architecture: 'x64',
          }] : []),
          ...(isJit && check.sourceMappingKind === 'linux-profiler' ? [{
            role: 'profiler',
            path: receipt.operations.jit.profilerPath,
            sha256: receipt.operations.jit.profilerSha256,
            sizeBytes: 524288,
            format: 'elf',
            architecture: 'x64',
          }] : []),
        ],
        invocation: {
          implementation: receipt.operations[isJit ? 'jit' : 'run'].implementation,
          command,
          entryAssembly,
          ...(isJit ? { methodFilter } : {}),
          outcome: 'succeeded',
          exitCode: 0,
          runtimeFrameCount: 2,
          terminalFrameKind: 'Exit',
          terminalStatus: 'completed',
          stdoutBytes: 32,
          stderrBytes: 16,
        },
        sandbox: {
          supervisorPolicyId: 'runtime-supervisor-v1',
          securityPolicyId: 'runtime-job-default',
          seccompSha256: `sha256:${hex('a')}`,
          containerId: hex('b'),
          networkMode: 'none',
          networkProbeBlocked: true,
          readOnlyRootFilesystem: true,
          readOnlyProbeBlocked: true,
          capDrop: ['ALL'],
          noNewPrivileges: true,
          user: '1654:1654',
          nanoCpus: securityPolicy.nanoCpus,
          memoryBytes: securityPolicy.memoryBytes,
          pidsLimit: securityPolicy.pidsLimit,
          deadlineMilliseconds: securityPolicy.maximumDurationSeconds * 1000,
          outputLimitBytes: securityPolicy.maximumOutputBytes,
          tmpfsBytes: securityPolicy.tmpfsBytes,
        },
        lifecycle: {
          outputOverflow: lifecycleProbe('output-limit-exceeded'),
          timeout: lifecycleProbe('timeout'),
          cancellation: lifecycleProbe('cancelled'),
          processTreeCleanup: lifecycleProbe('completed'),
        },
        ...(check.capability === 'jit-asm'
          ? {
              jit: {
                runtimeVersion: receipt.resolvedVersion,
                jitVersion: receipt.runtimeIdentity.jitVersion,
                ...(isMappedJit ? {
                  pdb: {
                    path: '/workspace/app.pdb',
                    sha256: `sha256:${hex('d')}`,
                    contentId: 'e'.repeat(40),
                    sequencePointCount: 2,
                  },
                } : {}),
                methods: [{
                  metadataToken: '0x06000001',
                  displayName: methodFilter,
                  nativeCodeBytes: 64,
                  instructionCount: 8,
                  sourceRanges: isMappedJit ? [
                    {
                      ilOffset: 0,
                      nativeStartOffset: 0,
                      nativeEndOffset: 8,
                      document: '/workspace/Program.cs',
                      startLine: 3,
                      startColumn: 5,
                      endLine: 3,
                      endColumn: 20,
                    },
                    {
                      ilOffset: 4,
                      nativeStartOffset: 8,
                      nativeEndOffset: 16,
                      document: '/workspace/Program.cs',
                      startLine: 4,
                      startColumn: 5,
                      endLine: 4,
                      endColumn: 20,
                    },
                  ] : [],
                }],
                mapping: {
                  kind: check.sourceMappingKind,
                  source: check.mappingSource,
                  rangeCount: isMappedJit ? 2 : 0,
                  distinctSourceRangeCount: isMappedJit ? 2 : 0,
                  allRangesMatchPdb: isMappedJit,
                },
              },
            }
          : check.capability === 'run' ? {
              run: {
                expectedStdoutMarker: 'runtime-preflight-stdout',
                observedStdoutMarker: 'runtime-preflight-stdout',
                expectedStderrMarker: 'runtime-preflight-stderr',
                observedStderrMarker: 'runtime-preflight-stderr',
                exceptionFrameValidated: true,
              },
            } : {
              inspection: {
                recordCount: 2,
                kinds: ['Value', 'MemoryGraph'],
                valueProbePassed: true,
                memoryGraphProbePassed: true,
              },
            }),
      }
      const bytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`)
      fs.mkdirSync(path.dirname(absolutePath), { recursive: true })
      fs.writeFileSync(absolutePath, bytes)
      check.evidencePath = relativePath
      check.evidenceSha256 = `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`;
    }
    writePerformanceEvidence()
  }
  const writePlanBinding = () => {
    const candidatePath = path.join(profileDirectory, `${profileId}.json`)
    const candidateBytes = fs.readFileSync(candidatePath)
    const boundPreflight = JSON.parse(candidateBytes)
    boundPreflight.image = receipt.image.reference
    boundPreflight.runtimeImageId = receipt.image.imageId
    boundPreflight.capabilities = receipt.checks.map(check => check.capability).sort()
    delete boundPreflight.promotionReceipt
    const planRoot = path.join(root, 'profiles', 'runtime-promotion-plans')
    fs.mkdirSync(planRoot, { recursive: true })
    const preflightRelativePath = `profiles/runtime-promotion-plans/${profileId}.profile.json`;
    const preflightBytes = Buffer.from(`${JSON.stringify(boundPreflight, null, 2)}\n`)
    fs.writeFileSync(path.join(root, ...preflightRelativePath.split('/')), preflightBytes)
    const plan = {
      schemaVersion: 1,
      candidateTarget: 'runtime-dotnet-matrix-candidate',
      profileId,
      profileSha256:
        `sha256:${crypto.createHash('sha256').update(candidateBytes).digest('hex')}`,
      matrixTargetId: target.id,
      platform: 'linux',
      family: 'coreclr',
      resolvedVersion: target.version,
      sourceRevision: receipt.sourceRevision,
      sourceTree: 'f'.repeat(40),
      image: receipt.image,
      componentIdentity: receipt.componentIdentity,
      runtimeIdentity: receipt.runtimeIdentity,
      buildInputs: { FIXTURE: 'runtime-matrix-generator' },
      buildInputsSha256: `sha256:${crypto.createHash('sha256').update(
        serializeRuntimePromotionPlan({ FIXTURE: 'runtime-matrix-generator' }),
      ).digest('hex')}`,
      producer: {
        id: 'sharplabnext-runtime-preflight-v1',
        sourceRevision: receipt.sourceRevision,
      },
      securityPolicyId: securityPolicy.id,
      capabilities: receipt.checks.map(check => check.capability).sort(),
      sourceMappingKind: receipt.checks.find(check => check.capability === 'jit-asm')?.sourceMappingKind ??
        'not-applicable',
      operations: receipt.operations,
      preflightProfile: {
        path: preflightRelativePath,
        sha256: `sha256:${crypto.createHash('sha256').update(preflightBytes).digest('hex')}`,
      },
      performance: {
        policyId: 'runtime-image-linux-x64-v1',
        policyPath: performancePolicyRelativePath,
        policySha256: performancePolicyDigest,
        evidencePath: `profiles/runtime-promotion-evidence/${profileId}/performance.json`,
      },
    }
    const planBytes = serializeRuntimePromotionPlan(plan)
    fs.writeFileSync(path.join(planRoot, `${profileId}.json`), planBytes)
    receipt.planSha256 = `sha256:${crypto.createHash('sha256').update(planBytes).digest('hex')}`;
    const signatureRelativePath = runtimePromotionPlanSignaturePath(profileId)
    const signatureBytes = Buffer.from(`${signRuntimePromotionPlan(planBytes, planKeys.privateKey)}\n`)
    fs.writeFileSync(path.join(root, ...signatureRelativePath.split('/')), signatureBytes)
    receipt.planSignature = {
      path: signatureRelativePath,
      sha256: `sha256:${crypto.createHash('sha256').update(signatureBytes).digest('hex')}`,
      keyId: planKeyId,
    }
  }
  const receiptRelativePath = `profiles/runtime-promotion-receipts/${profileId}.json`
  const receiptPath = path.join(root, ...receiptRelativePath.split('/'))
  fs.mkdirSync(path.dirname(receiptPath), { recursive: true })
  writePlanBinding()
  writeEvidence()
  const receiptBytes = Buffer.from(`${JSON.stringify(receipt, null, 2)}\n`)
  fs.writeFileSync(receiptPath, receiptBytes)
  target.linuxCapability = {
    capabilities: ['run', 'jit-asm'],
    promotionState: 'verified',
    promotionReceipt: {
      path: receiptRelativePath,
      sha256: `sha256:${crypto.createHash('sha256').update(receiptBytes).digest('hex')}`,
    },
  }
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  writeFixtureCatalog()

  const runGenerator = () => spawnSync(
      'dotnet',
      [
        'run',
        path.join(repositoryRoot, 'eng', 'tools', 'generate-runtime-matrix.cs'),
        '--',
        '--repository-root', root,
        '--matrix', matrixPath,
        '--catalog', catalogPath,
        '--profiles', profileDirectory,
        '--overwrite-profiles',
      ],
      {
        cwd: repositoryRoot,
        encoding: 'utf8',
        timeout: 110_000,
        windowsHide: true,
      },
    )
  const result = runGenerator()
  assert.equal(
    result.status,
    0,
    `generator failed\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
  )

  const profile = JSON.parse(fs.readFileSync(path.join(profileDirectory, `${profileId}.json`), 'utf8'))
  assert.equal(profile.image, receipt.image.reference)
  assert.equal(profile.runtimeImageId, receipt.image.imageId)
  assert.equal(profile.runtimeCommit, target.runtimeCommit)
  assert.equal(profile.jitVersion, target.version)
  assert.equal(profile.jitCommit, target.jitCommit)
  assert.deepEqual(profile.promotionReceipt, target.linuxCapability.promotionReceipt)
  assert.equal(profile.operations.run.implementationId, receipt.operations.run.implementation)
  assert.equal(profile.operations.jit.implementationId, receipt.operations.jit.implementation)
  assert.equal(profile.operations.jit.sourceMappingKind, 'none')

  const generated = JSON.parse(fs.readFileSync(catalogPath, 'utf8'))
  const runtime = generated.runtimes.find(candidate => candidate.id === profileId)
  assert.ok(runtime)
  assert.equal(runtime.runtimeImageId, receipt.image.imageId)
  assert.equal(runtime.runtimeCommit, target.runtimeCommit)
  assert.equal(runtime.jitVersion, target.version)
  assert.equal(runtime.jitCommit, target.jitCommit)
  assert.equal(runtime.jitSourceMappingKind, 'none')
  assert.deepEqual(runtime.capabilities, ['run', 'jit-asm'])

  receipt.operations.run = {
    implementation: 'sharplabnext-runner-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.Runner.dll',
    assemblySha256: `sha256:${hex('1')}`,
  }
  receipt.operations.jit = {
    implementation: 'sharplabnext-jit-inspector-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
    assemblySha256: `sha256:${hex('2')}`,
    profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
    profilerSha256: `sha256:${hex('3')}`,
  }
  receipt.checks[1].sourceMappingKind = 'linux-profiler'
  receipt.checks[1].mappingSource = 'ordinary'
  profile.operations.run = {
    implementationId: 'sharplabnext-runner-v1',
    pathStyle: 'unix',
    command: {
      executable: '/opt/sharplabnext/target-dotnet/dotnet',
      argv: [
        '/opt/sharplabnext/SharpLabNext.Runner.dll',
        '{entryAssembly}',
        '--',
        '{arguments}',
      ],
    },
  }
  profile.operations.jit = {
    implementationId: 'sharplabnext-jit-inspector-v1',
    pathStyle: 'unix',
    command: {
      executable: '/opt/sharplabnext/target-dotnet/dotnet',
      argv: [
        '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
        '{entryAssembly}',
        '{methodFilter}',
      ],
    },
    sourceMappingKind: 'linux-profiler',
    profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
  }
  profile.layout.runnerAssemblyPath = '/opt/sharplabnext/SharpLabNext.Runner.dll'
  profile.layout.jitInspectorAssemblyPath = '/opt/sharplabnext/SharpLabNext.JitInspector.dll'
  fs.writeFileSync(
    path.join(profileDirectory, `${profileId}.json`),
    `${JSON.stringify(profile, null, 2)}\n`,
  )
  writePlanBinding()
  writeEvidence()
  let updatedReceiptBytes = Buffer.from(`${JSON.stringify(receipt, null, 2)}\n`)
  fs.writeFileSync(receiptPath, updatedReceiptBytes)
  target.linuxCapability.promotionReceipt.sha256 = `sha256:${crypto.createHash('sha256').update(updatedReceiptBytes).digest('hex')}`;
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)
  writeFixtureCatalog()

  const profilerResult = runGenerator()
  assert.equal(
    profilerResult.status,
    0,
    `profiler generator failed\nstdout:\n${profilerResult.stdout}\nstderr:\n${profilerResult.stderr}`,
  )
  const profilerProfile = JSON.parse(fs.readFileSync(path.join(profileDirectory, `${profileId}.json`), 'utf8'))
  assert.equal(profilerProfile.operations.run.implementationId, 'sharplabnext-runner-v1')
  assert.equal(profilerProfile.operations.jit.implementationId, receipt.operations.jit.implementation)
  assert.equal(profilerProfile.operations.jit.sourceMappingKind, 'linux-profiler')
  assert.equal(
    profilerProfile.operations.jit.profilerPath,
    '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
  )
  const profilerCatalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'))
  assert.equal(
    profilerCatalog.runtimes.find(candidate => candidate.id === profileId).jitSourceMappingKind,
    'linux-profiler',
  )

  delete target.linuxCapability.promotionReceipt
  target.linuxCapability = {
    capabilities: ['run', 'inspection'],
    instrumentationCapabilities: ['inspection'],
    promotionState: 'verified',
  }
  receipt.operations = {
    run: {
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
      assemblySha256: `sha256:${hex('c')}`,
    },
  }
  receipt.checks = [receipt.checks[0], {
    ...receipt.checks[0],
    capability: 'inspection',
  }]
  writePlanBinding()
  writeEvidence()
  updatedReceiptBytes = Buffer.from(`${JSON.stringify(receipt, null, 2)}\n`)
  fs.writeFileSync(receiptPath, updatedReceiptBytes)
  target.linuxCapability.promotionReceipt = {
    path: receiptRelativePath,
    sha256: `sha256:${crypto.createHash('sha256').update(updatedReceiptBytes).digest('hex')}`,
  }
  fs.writeFileSync(matrixPath, `${JSON.stringify(matrix, null, 2)}\n`)

  const invalidInstrumentationResult = runGenerator()
  assert.notEqual(invalidInstrumentationResult.status, 0)
  assert.match(
    `${invalidInstrumentationResult.stdout}\n${invalidInstrumentationResult.stderr}`,
    /operations\.run\.implementation must equal "sharplabnext-runner-v1"/,
  )
})

let performanceSampleSequence = 0

function blockAllMatrixCapabilities(matrix) {
  const block = capability => {
    if (capability.promotionState !== 'verified') return
    capability.promotionState = 'blocked'
    capability.blockedReason = 'Fixture requires an explicit promotion receipt for this row.'
    delete capability.promotionReceipt
  }

  for (const row of matrix.coreClr) {
    block(row.linuxCapability)
    block(row.wineCapability)
  }
  block(matrix.mono.capability)
  for (const row of matrix.framework.targets) block(row.capability)
}

function performanceScenario() {
  const sample = latencyMilliseconds => ({
    latencyMilliseconds,
    peakMemoryBytes: 134217728,
    completionPeakMemoryBytes: 134217728,
    operationId: `op_${(++performanceSampleSequence).toString(16).padStart(32, '0')}`,
    resourceSampleCount: 1,
    postCompletionResourceSampleCount: 1,
    completedAtUtc: '2026-07-22T00:00:00.0000000Z',
  })
  return {
    cold: Array.from({ length: 3 }, () => sample(100)),
    warm: Array.from({ length: 10 }, () => sample(50)),
  }
}
