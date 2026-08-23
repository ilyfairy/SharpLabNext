import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const readJson = relativePath => JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, ...relativePath.split('/')),
  'utf8',
))

const matrix = readJson('profiles/runtime-matrix.json')
const baseImages = readJson('profiles/base-images.json')
const catalog = readJson('profiles/catalog/catalog.json')
const candidateDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')
const activeProfileDirectory = path.join(repositoryRoot, 'profiles', 'runtimes')

const requiredCoreClrIds = [
  'dotnet-core-2.0',
  'dotnet-core-2.1',
  'dotnet-core-2.2',
  'dotnet-core-3.0',
  'dotnet-core-3.1',
  'dotnet-5',
  'dotnet-6',
  'dotnet-7',
  'dotnet-8',
  'dotnet-9',
  'dotnet-10',
  'dotnet-11-preview',
]

const requiredWineCoreClrIds = [
  'dotnet-5',
  'dotnet-6',
  'dotnet-7',
  'dotnet-8',
  'dotnet-9',
  'dotnet-10',
  'dotnet-11-preview',
]

const requiredFrameworkIds = [
  'netfx20',
  'netfx30',
  'netfx35',
  'netfx40',
  'netfx45',
  'netfx451',
  'netfx452',
  'netfx46',
  'netfx461',
  'netfx462',
  'netfx47',
  'netfx471',
  'netfx472',
  'netfx48',
]

const checkedJitExpectations = new Map([
  ['dotnet-6', 'none'],
  ['dotnet-7', 'checked-jit-debug-info'],
  ['dotnet-8', 'checked-jit-debug-info'],
  ['dotnet-9', 'checked-jit-debug-info'],
])

const operationImplementations = new Map([
  ['sharplabnext-runner-v1', {
    project: 'src/RuntimeJobs/SharpLabNext.Runner/SharpLabNext.Runner.csproj',
    assembly: '/opt/sharplabnext/SharpLabNext.Runner.dll',
    mappingKinds: [],
  }],
  ['sharplabnext-jit-inspector-v1', {
    project: 'src/RuntimeJobs/SharpLabNext.JitInspector/SharpLabNext.JitInspector.csproj',
    assembly: '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
    mappingKinds: ['none', 'linux-profiler'],
  }],
  ['sharplabnext-legacy-jit-inspector-v1', {
    project: 'src/RuntimeJobs/SharpLabNext.LegacyJitInspector/SharpLabNext.LegacyJitInspector.csproj',
    assembly: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    mappingKinds: ['none'],
  }],
  ['sharplabnext-mono-jit-inspector-v1', {
    project: 'src/RuntimeJobs/SharpLabNext.MonoJitInspector/SharpLabNext.MonoJitInspector.csproj',
    assembly: '/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll',
    mappingKinds: ['none'],
  }],
  ['sharplabnext-desktop-clr-jit-inspector-v1', {
    project: 'src/RuntimeJobs/SharpLabNext.WineRunner/SharpLabNext.WineRunner.csproj',
    assembly: '/opt/sharplabnext/SharpLabNext.WineRunner.dll',
    mappingKinds: ['none'],
  }],
  ['sharplabnext-checked-jit-bridge-v1', {
    project: 'src/RuntimeJobs/SharpLabNext.CheckedJitBridge/SharpLabNext.CheckedJitBridge.csproj',
    assembly: '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
    mappingKinds: ['none', 'checked-jit-debug-info'],
  }],
  ['sharplabnext-target-runtime-runner-v1', {
    project: 'src/RuntimeJobs/SharpLabNext.TargetRuntimeRunner/SharpLabNext.TargetRuntimeRunner.csproj',
    assembly: '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
    mappingKinds: [],
  }],
])

const sorted = values => [...values].sort((left, right) => left.localeCompare(right))
const coreClrById = new Map(matrix.coreClr.map(row => [row.id, row]))
const frameworkById = new Map(matrix.framework.targets.map(row => [row.id, row]))

const matrixRows = [
  ...matrix.coreClr.flatMap(row => [
    {
      id: `${row.id}-linux-x64`,
      sourceId: row.id,
      platform: 'linux',
      version: row.version,
      lifecycle: row,
      capability: row.linuxCapability,
    },
    {
      id: `wine-${row.id}-linux-x64`,
      sourceId: row.id,
      platform: 'wine',
      version: row.version,
      lifecycle: row,
      capability: row.wineCapability,
    },
  ]),
  {
    id: matrix.mono.id,
    sourceId: matrix.mono.id,
    platform: 'mono',
    version: matrix.mono.version,
    lifecycle: matrix.mono,
    capability: matrix.mono.capability,
  },
  ...matrix.framework.targets.map(row => ({
    id: `wine-${row.id}-linux-x64`,
    sourceId: row.id,
    platform: 'framework',
    version: row.version,
    lifecycle: row,
    capability: row.capability,
  })),
]

function assertExactSet(actual, expected, message) {
  assert.deepEqual(sorted(actual), sorted(expected), message)
}

function candidateProfile(id) {
  return readJson(`profiles/runtimes/candidates/${id}.json`)
}

function assertCapabilityState(row) {
  const { capability } = row
  assert.ok(Array.isArray(capability.capabilities), `${row.id} must declare capabilities`)
  assert.equal(
    new Set(capability.capabilities).size,
    capability.capabilities.length,
    `${row.id} must not repeat capabilities`,
  )

  if (capability.promotionState === 'blocked') {
    assert.equal(typeof capability.blockedReason, 'string', `${row.id} must explain why it is blocked`)
    assert.notEqual(capability.blockedReason.trim(), '', `${row.id} blocked reason must not be empty`)
    assert.equal(capability.promotionReceipt, undefined, `${row.id} blocked row cannot cite a promotion receipt`)
    return
  }

  assert.equal(capability.promotionState, 'verified', `${row.id} has an unknown promotion state`)
  assert.equal(capability.blockedReason, undefined, `${row.id} verified row cannot retain a blocked reason`)
  assert.equal(typeof capability.promotionReceipt?.path, 'string', `${row.id} verified row needs a receipt`)
  assert.match(capability.promotionReceipt?.sha256 ?? '', /^sha256:[0-9a-f]{64}$/)
}

test('source lock contains the complete requested runtime row sets', () => {
  assertExactSet(matrix.coreClr.map(row => row.id), requiredCoreClrIds, 'CoreCLR source rows changed')
  assertExactSet(
    matrix.framework.targets.map(row => row.id),
    requiredFrameworkIds,
    '.NET Framework source rows changed',
  )
  assert.equal(matrix.mono.id, 'mono-6.12-linux-x64')
  assert.equal(matrix.mono.version, '6.12.0.182')
  assert.equal(
    matrix.mono.image,
    'mono:6.12@sha256:d2ae1881a608fb6401bcebbf0d444fc2fcadf0db27f07c87153a79e7b14e861a',
  )

  for (const id of requiredWineCoreClrIds) {
    assert.ok(coreClrById.get(id)?.wineCapability, `${id} must explicitly declare Wine feasibility`)
  }
})

test('every Framework row has a generated reference set and the Roslyn worker allows the full matrix', () => {
  const expectedReferenceSetIds = matrix.framework.targets.map(row => row.referenceSetId)
  assert.equal(new Set(expectedReferenceSetIds).size, requiredFrameworkIds.length)
  for (const id of expectedReferenceSetIds) {
    const reference = catalog.referenceSets.find(candidate => candidate.id === id)
    assert.ok(reference, `${id} is missing`)
    assert.deepEqual(
      reference.requiredRuntimeFeatureTags,
      [],
      `${id} managed references cannot require a Wine-only runtime tag`,
    )
  }

  const toolchain = catalog.toolchains.find(candidate => candidate.id === 'roslyn-stable-netfx48')
  assert.ok(toolchain)
  assertExactSet(
    toolchain.allowedReferenceSetIds,
    expectedReferenceSetIds,
    'Roslyn Framework reference allow-list changed',
  )
  assert.equal(toolchain.defaultReferenceSetId, 'netfx48-managed-ref')

  const net30 = frameworkById.get('netfx30')
  const reference = catalog.referenceSets.find(candidate => candidate.id === net30.referenceSetId)
  assert.equal(reference.targetFramework, 'net30')
  assert.equal(reference.digest, net30.referenceComposition.sourceIdentityDigest)
  assert.ok(catalog.compatibility.some(rule =>
    rule.kind === 'toolchain-reference-set' &&
    rule.fromId === 'roslyn-stable-netfx48' &&
    rule.toId === net30.referenceSetId,
  ))
  assert.ok(catalog.presets.some(preset =>
    preset.referenceSetId === net30.referenceSetId &&
    preset.defaultRuntimeId === 'wine-netfx30-linux-x64',
  ))
})

test('execution users are explicit and preserved across every generated runtime row', () => {
  const nonRootWineIds = new Set(requiredWineCoreClrIds)

  for (const row of matrix.coreClr) {
    const expectedWineUser = nonRootWineIds.has(row.id) ? '1654:1654' : '0:0'
    assert.equal(row.wineCapability.executionUser, expectedWineUser, row.id)
    assert.equal(
      candidateProfile(`${row.id}-linux-x64`).container.executionUser,
      '1654:1654',
      `${row.id} Linux candidate`,
    )
    assert.equal(
      candidateProfile(`wine-${row.id}-linux-x64`).container.executionUser,
      expectedWineUser,
      `${row.id} Wine candidate`,
    )
  }

  assert.equal(
    candidateProfile(matrix.mono.id).container.executionUser,
    '1654:1654',
    'Mono candidate',
  )
  for (const row of matrix.framework.targets) {
    assert.equal(
      candidateProfile(`wine-${row.id}-linux-x64`).container.executionUser,
      '0:0',
      `${row.id} Framework candidate`,
    )
  }
})

test('every source row has one canonical candidate profile ID', () => {
  const expectedIds = matrixRows.map(row => row.id)
  assert.equal(new Set(expectedIds).size, expectedIds.length, 'matrix output IDs must be unique')

  const actualIds = fs.readdirSync(candidateDirectory)
    .filter(file => file.endsWith('.json'))
    .map(file => path.basename(file, '.json'))
  assertExactSet(actualIds, expectedIds, 'candidate profile files must exactly match the source matrix')

  for (const id of actualIds) {
    assert.equal(candidateProfile(id).id, id, `${id} file and profile IDs must match`)
  }
})

test('every candidate operation resolves to one maintained helper contract', () => {
  for (const fileName of fs.readdirSync(candidateDirectory).filter(name => name.endsWith('.json'))) {
    const profile = readJson(`profiles/runtimes/candidates/${fileName}`)
    for (const [operationName, operation] of Object.entries(profile.operations ?? {})) {
      const implementation = operationImplementations.get(operation.implementationId)
      assert.ok(
        implementation,
        `${profile.id} ${operationName} has unknown implementation '${operation.implementationId}'`,
      )
      assert.ok(
        fs.existsSync(path.join(repositoryRoot, ...implementation.project.split('/'))),
        `${profile.id} ${operationName} implementation project is missing`,
      )
      assert.ok(
        operation.command?.argv?.includes(implementation.assembly) ||
          operation.command?.argv?.includes(`Z:${implementation.assembly.replaceAll('/', '\\')}`),
        `${profile.id} ${operationName} does not invoke its maintained helper`,
      )
      if (operationName === 'jit') {
        assert.ok(
          implementation.mappingKinds.includes(operation.sourceMappingKind),
          `${profile.id} JIT implementation cannot provide '${operation.sourceMappingKind}' mapping`,
        )
      }
    }
  }
})

test('Legacy CoreCLR operations pin the runtime version inside the helper contract', () => {
  for (const fileName of fs.readdirSync(candidateDirectory).filter(name => name.endsWith('.json'))) {
    const profile = readJson(`profiles/runtimes/candidates/${fileName}`)
    for (const [operationName, operation] of Object.entries(profile.operations ?? {})) {
      if (operation.implementationId !== 'sharplabnext-legacy-jit-inspector-v1')
        continue

      const argv = operation.command?.argv ?? []
      const helperIndex = argv.findIndex(token =>
        token === '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll' ||
        token === 'Z:\\opt\\sharplabnext\\SharpLabNext.LegacyJitInspector.dll',
      )
      assert.ok(helperIndex >= 0, `${profile.id} ${operationName} must invoke the Legacy helper`)
      assert.equal(argv[helperIndex + 1], '--runtime-version', `${profile.id} ${operationName} must pass the guard switch`)
      assert.equal(argv[helperIndex + 2], profile.runtimeVersion, `${profile.id} ${operationName} must pass its exact runtime version`)
    }
  }
})

test('Catalog runtime IDs are unique', () => {
  const ids = catalog.runtimes.map(runtime => runtime.id)
  assert.equal(new Set(ids).size, ids.length, 'Catalog cannot contain duplicate runtime IDs')
})

test('the explicitly enabled full runtime matrix remains visible without erasing lifecycle status', () => {
  const lifecycleRows = [...matrix.coreClr, matrix.mono, ...matrix.framework.targets]

  for (const row of lifecycleRows) {
    assert.equal(row.visibility, 'visible', `${row.id} must remain selectable in the full matrix`)
    assert.ok(
      ['active', 'maintenance', 'preview', 'legacy', 'experimental'].includes(row.supportStatus),
      `${row.id} must retain an explicit lifecycle status`,
    )
  }
})

test('blocked and verified capability states are fail-closed', () => {
  for (const row of matrixRows) {
    assertCapabilityState(row)

    if (row.capability.promotionState !== 'blocked')
      continue

    const exactCatalogRows = catalog.runtimes.filter(runtime =>
      runtime.id === row.id && runtime.resolvedVersion === row.version)
    for (const runtime of exactCatalogRows) {
      assert.deepEqual(runtime.capabilities, [], `${row.id} blocked version cannot advertise capabilities`)
      assert.equal(runtime.availability?.installed, false, `${row.id} blocked version cannot be installed`)
      assert.notEqual(runtime.availability?.health, 'healthy', `${row.id} blocked version cannot be healthy`)
    }
  }
})

test('out-of-scope Wine CoreCLR 2.x and 3.x rows stay explicitly disabled', () => {
  for (const id of requiredCoreClrIds.filter(candidate => candidate.startsWith('dotnet-core-'))) {
    const row = coreClrById.get(id)
    assert.deepEqual(row.wineCapability.capabilities, [], `${id} Wine row must not expose a capability`)
    assert.equal(row.wineCapability.promotionState, 'blocked')
    assert.equal(row.visibility, 'visible', `${id} Linux/reference target must remain selectable`)
    assert.equal(
      catalog.runtimes.find(runtime => runtime.id === `wine-${id}-linux-x64`)?.visibility,
      'hidden',
      `${id} out-of-scope Wine runtime must remain hidden`,
    )
    assert.match(row.wineCapability.blockedReason, /outside the requested and tested matrix/i)
  }
})

test('Wine CoreCLR 5-11 records independently verified Run and JIT capabilities', () => {
  for (const id of requiredWineCoreClrIds) {
    const row = coreClrById.get(id)
    const capabilities = row.wineCapability.capabilities
    assert.match(row.runtimeCommit, /^[0-9a-f]{40}$|^[0-9a-f]{64}$/, `${id} must lock its CoreCLR commit`)
    assert.equal(row.jitCommit, row.runtimeCommit, `${id} Wine runtime and JIT commits must match`)
    assert.ok(capabilities.includes('run'), `${id} Wine row must retain the audited Run capability`)
    assert.equal(row.wineCapability.promotionState, 'verified', `${id} Wine row must have an exact promotion receipt`)
    assert.equal(
      typeof row.wineCapability.promotionReceipt?.path,
      'string',
      `${id} Wine row must bind its immutable receipt`,
    )

    const major = Number.parseInt(row.channel, 10)
    assert.equal(
      capabilities.includes('jit-asm'),
      major >= 7,
      `${id} Wine JIT feasibility must reflect the retail JitDisasm audit`,
    )

    const profile = candidateProfile(`wine-${id}-linux-x64`)
    assert.equal(profile.runtimeCommit, row.runtimeCommit)
    assert.equal(profile.jitCommit, row.jitCommit)
    if (major >= 7) {
      assert.equal(profile.operations.jit?.implementationId, 'sharplabnext-legacy-jit-inspector-v1')
      assert.equal(profile.operations.jit?.sourceMappingKind, 'none')
    }
    else {
      assert.equal(profile.operations.jit, undefined, `${id} Wine profile cannot advertise JIT ASM`)
    }
  }
})

test('Checked-JIT source locks are exact for Linux .NET 6-9', () => {
  assertExactSet(
    matrix.coreClr.filter(row => row.checkedJit).map(row => row.id),
    checkedJitExpectations.keys(),
    'Checked-JIT source-lock coverage changed',
  )

  for (const [id, expectedMappingKind] of checkedJitExpectations) {
    const row = coreClrById.get(id)
    assert.ok(row.linuxCapability.capabilities.includes('jit-asm'), `${id} must declare Linux JIT ASM`)
    assert.equal(row.checkedJit.commit, row.runtimeCommit, `${id} Checked JIT must match the runtime commit`)
    assert.equal(row.jitCommit, row.runtimeCommit, `${id} runtime and JIT commits must match`)
    assert.match(row.checkedJit.sourceArchive.url, new RegExp(`${row.runtimeCommit}\\.tar\\.gz$`))
    assert.match(row.checkedJit.sourceArchive.sha512, /^[0-9a-f]{128}$/)
    assert.match(row.checkedJit.builderImage, /@sha256:[0-9a-f]{64}$/)
    assert.equal(row.checkedJit.configuration, 'Checked')
    assert.equal(row.checkedJit.architecture, 'x64')
    assert.equal(row.checkedJit.buildComponent, 'jit')
    assert.equal(row.checkedJit.compiler, 'gcc')
    assert.equal(row.checkedJit.generator, 'make')
    assert.equal(row.checkedJit.sourceMappingKind, expectedMappingKind)
  }

  assert.deepEqual(coreClrById.get('dotnet-6').checkedJit.bootstrapSdk, {
    version: '6.0.135',
    url: 'https://builds.dotnet.microsoft.com/dotnet/Sdk/6.0.135/dotnet-sdk-6.0.135-linux-x64.tar.gz',
    sha512: 'f990fa0636385a3a4ea6b0e1ccaa45613fef442d3610015236fc2474895f2c2446559f2fb942c901171bb847cd825fcc575fb82d120cc5d1cf175d5c0ae01cff',
  })
  assert.equal(
    coreClrById.get('dotnet-6').checkedJit.versionGenerationMode,
    'skip-by-upstream-flag',
  )
  for (const id of ['dotnet-7', 'dotnet-8', 'dotnet-9']) {
    assert.equal(
      coreClrById.get(id).checkedJit.bootstrapSdk,
      undefined,
      `${id} must use its builder toolset without the .NET 6 bootstrap SDK`,
    )
    assert.equal(
      coreClrById.get(id).checkedJit.versionGenerationMode,
      undefined,
      `${id} must retain upstream native version generation`,
    )
  }

  const maintainedRuntimeBuildImage = baseImages.images.find(
    image => image.id === 'dotnet-runtime-build',
  )?.reference
  assert.ok(maintainedRuntimeBuildImage, 'base image manifest must contain dotnet-runtime-build')
  for (const id of ['dotnet-8', 'dotnet-9']) {
    assert.equal(
      coreClrById.get(id).checkedJit.builderImage,
      maintainedRuntimeBuildImage,
      `${id} requires the maintained CMake 3.20+ runtime build image`,
    )
  }
})

for (const [id, expectedMappingKind] of checkedJitExpectations) {
  test(`${id} candidate consumes its Checked-JIT implementation and mapping contract`, () => {
    const profile = candidateProfile(`${id}-linux-x64`)
    assert.ok(profile.capabilities.includes('jit-asm'), `${id} candidate must retain JIT ASM`)
    assert.equal(profile.operations.jit?.implementationId, 'sharplabnext-checked-jit-bridge-v1')
    assert.equal(profile.operations.jit?.sourceMappingKind, expectedMappingKind)
    assert.ok(
      profile.operations.jit?.command?.argv?.includes(
        '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
      ),
      `${id} must invoke the Checked-JIT bridge`,
    )
  })
}

test('Framework 4.0-4.7.2 retain independent exact-runtime promotion receipts', () => {
  const compatibilityOnlyVersions = [
    '4.0', '4.5', '4.5.1', '4.5.2', '4.6', '4.6.1', '4.6.2', '4.7', '4.7.1', '4.7.2',
  ]

  const receiptPaths = new Set()
  for (const version of compatibilityOnlyVersions) {
    const row = matrix.framework.targets.find(candidate => candidate.version === version)
    assert.ok(row, `missing Framework ${version}`)
    assert.equal(row.capability.promotionState, 'verified', `Framework ${version} must be exact-runtime verified`)
    assert.equal(typeof row.capability.promotionReceipt?.path, 'string')
    assert.equal(receiptPaths.has(row.capability.promotionReceipt.path), false)
    receiptPaths.add(row.capability.promotionReceipt.path)

    const exactCatalogRows = catalog.runtimes.filter(runtime =>
      runtime.id === `wine-${row.id}-linux-x64` && runtime.resolvedVersion === version)
    for (const runtime of exactCatalogRows) {
      assert.equal(runtime.availability?.installed, true)
      assert.ok(runtime.capabilities.includes('run'))
    }
  }
})

test('every installed JIT advertisement resolves to a maintained implementation', () => {
  const installedJitRuntimes = catalog.runtimes.filter(runtime =>
    runtime.availability?.installed === true &&
    runtime.availability?.health === 'healthy' &&
    runtime.capabilities?.includes('jit-asm'))

  assert.ok(installedJitRuntimes.length > 0, 'fixture must include an installed JIT runtime')
  for (const runtime of installedJitRuntimes) {
    const profilePath = path.join(activeProfileDirectory, `${runtime.id}.json`)
    assert.ok(fs.existsSync(profilePath), `${runtime.id} must have an active runtime profile`)
    const profile = JSON.parse(fs.readFileSync(profilePath, 'utf8'))
    const operation = profile.operations?.jit
    assert.ok(operation, `${runtime.id} advertises JIT ASM without a JIT operation`)

    const implementation = operationImplementations.get(operation.implementationId)
    assert.ok(implementation, `${runtime.id} has an unknown JIT implementation`)
    assert.ok(
      fs.existsSync(path.join(repositoryRoot, ...implementation.project.split('/'))),
      `${runtime.id} JIT implementation project is missing`,
    )
    assert.ok(
      operation.command?.argv?.includes(implementation.assembly) ||
        operation.command?.argv?.includes(`Z:${implementation.assembly.replaceAll('/', '\\')}`),
      `${runtime.id} JIT command does not invoke its maintained implementation`,
    )
    assert.ok(
      implementation.mappingKinds.includes(operation.sourceMappingKind),
      `${runtime.id} JIT implementation and source mapping kind disagree`,
    )
  }
})

test('released .NET 10 and .NET 11 profiles agree with their promoted matrix rows', () => {
  for (const id of ['dotnet-10-linux-x64', 'dotnet-11-preview-linux-x64']) {
    const runtime = catalog.runtimes.find(candidate => candidate.id === id)
    assert.ok(runtime, `${id} must remain in the active Catalog`)
    assert.equal(runtime.availability?.installed, true, `${id} must remain installed`)
    assert.equal(runtime.availability?.health, 'healthy', `${id} must remain healthy`)
    assert.ok(runtime.capabilities.includes('run'))
    assert.ok(runtime.capabilities.includes('jit-asm'))

    const profile = readJson(`profiles/runtimes/${id}.json`)
    assert.equal(profile.runtimeVersion, runtime.resolvedVersion)
    assert.equal(profile.operations.jit?.implementationId, 'sharplabnext-jit-inspector-v1')
    assert.equal(profile.operations.jit?.sourceMappingKind, 'linux-profiler')

    const matrixId = id.replace(/-linux-x64$/, '')
    const promoted = coreClrById.get(matrixId)
    assert.ok(promoted, `${id} must retain its matrix row`)
    assert.equal(promoted.linuxCapability.promotionState, 'verified')
    assert.equal(typeof promoted.linuxCapability.promotionReceipt?.path, 'string')
    assert.equal(promoted.version, runtime.resolvedVersion)
  }
})

for (const id of ['dotnet-10-linux-x64', 'dotnet-11-preview-linux-x64']) {
  test(`${id} candidate does not regress the released JIT source mapping`, () => {
    const active = readJson(`profiles/runtimes/${id}.json`)
    const candidate = candidateProfile(id)
    assert.equal(active.operations.jit?.sourceMappingKind, 'linux-profiler')
    assert.ok(candidate.capabilities.includes('jit-asm'))
    assert.ok(candidate.operations.jit, `${id} candidate must retain a JIT operation`)
    assert.ok(
      ['linux-profiler', 'checked-jit-debug-info'].includes(
        candidate.operations.jit.sourceMappingKind,
      ),
      `${id} candidate cannot regress source mapping to method-only or none`,
    )

    const implementation = operationImplementations.get(
      candidate.operations.jit.implementationId,
    )
    assert.ok(implementation, `${id} candidate has an unknown JIT implementation`)
    assert.ok(
      implementation.mappingKinds.includes(candidate.operations.jit.sourceMappingKind),
      `${id} candidate JIT implementation cannot provide its declared mapping`,
    )
  })
}
