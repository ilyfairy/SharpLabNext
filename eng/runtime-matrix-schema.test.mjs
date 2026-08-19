import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const schema = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'schemas', 'runtime-matrix.schema.json'),
  'utf8',
))
const matrix = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
  'utf8',
))
const releaseLock = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'profiles', 'lock.json'),
  'utf8',
))
const baseImages = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'profiles', 'base-images.json'),
  'utf8',
))

test('CoreCLR commit identities accept optional 40- and 64-character lowercase hex values', () => {
  const coreClrSchema = schema.$defs.coreClr

  assert.equal(coreClrSchema.required.includes('runtimeCommit'), false)
  assert.equal(coreClrSchema.required.includes('jitCommit'), false)
  assert.equal(coreClrSchema.properties.runtimeCommit.$ref, '#/$defs/coreClrCommit')
  assert.equal(coreClrSchema.properties.jitCommit.$ref, '#/$defs/coreClrCommit')

  const pattern = new RegExp(schema.$defs.coreClrCommit.pattern)
  assert.equal(pattern.test('a'.repeat(40)), true)
  assert.equal(pattern.test('0123456789abcdef'.repeat(4)), true)
})

test('CoreCLR commit identities reject non-lowercase-hex and unsupported lengths', () => {
  const pattern = new RegExp(schema.$defs.coreClrCommit.pattern)

  for (const value of [
    'a'.repeat(39),
    'a'.repeat(41),
    'a'.repeat(63),
    'a'.repeat(65),
    'A'.repeat(40),
    `g${'a'.repeat(39)}`,
    `sha256:${'a'.repeat(64)}`,
  ]) {
    assert.equal(pattern.test(value), false, `${value} must be rejected`)
  }
})

test('Wine capability execution users are required and closed while Linux capabilities reject them', () => {
  assert.deepEqual(
    schema.$defs.blockedWinePlatformCapability.required,
    ['executionUser', 'capabilities', 'promotionState', 'blockedReason'],
  )
  assert.deepEqual(
    schema.$defs.blockedWinePlatformCapability.properties.executionUser.enum,
    ['0:0', '1654:1654'],
  )
  assert.equal(schema.$defs.blockedPlatformCapability.properties.executionUser, undefined)
  assert.equal(schema.$defs.verifiedPlatformCapability.properties.executionUser, undefined)

  for (const row of matrix.coreClr) {
    assert.equal(typeof row.wineCapability.executionUser, 'string', row.id)
    assert.equal(row.linuxCapability.executionUser, undefined, row.id)
  }
})

test('Mono source image is the exact digest-pinned 6.12 operator image', () => {
  assert.equal(schema.$defs.mono.properties.image.$ref, '#/$defs/digestPinnedImage')
  assert.equal(
    matrix.mono.image,
    'mono:6.12@sha256:d2ae1881a608fb6401bcebbf0d444fc2fcadf0db27f07c87153a79e7b14e861a',
  )
  assert.match(matrix.mono.image, new RegExp(schema.$defs.digestPinnedImage.pattern))
})

test('Framework rows require one locked reference source and net30 uses the closed composition recipe', () => {
  assert.deepEqual(schema.$defs.frameworkTarget.oneOf, [
    { $ref: '#/$defs/frameworkPackageTarget' },
    { $ref: '#/$defs/frameworkCompositionTarget' },
  ])
  assert.equal(
    schema.$defs.frameworkPackageTarget.properties.referencePackage.$ref,
    '#/$defs/referencePackage',
  )
  assert.equal(
    schema.$defs.frameworkCompositionTarget.properties.referenceComposition.$ref,
    '#/$defs/referenceComposition',
  )

  const packageRows = matrix.framework.targets.filter(row => row.referencePackage !== undefined)
  const compositionRows = matrix.framework.targets.filter(
    row => row.referenceComposition !== undefined,
  )
  assert.equal(packageRows.length, 13)
  assert.deepEqual(compositionRows.map(row => row.id), ['netfx30'])
  assert.deepEqual(compositionRows[0].referenceComposition, {
    kind: 'nuget-package-composition',
    resolvedVersion: 'net30-union-v1',
    sourceIdentityDigest:
      'sha256:d61880a865bf41757cd61d1006f72aade7fcf574a369a7c7189aea0d60579b96',
    sources: [
      { role: 'base', targetId: 'netfx20', selection: 'all' },
      {
        role: 'extension',
        targetId: 'netfx35',
        selection: 'assembly-version:3.0.0.0',
      },
    ],
  })
})

test('Checked JIT locks close commit, canonical archive, builder, and mapping identity', () => {
  assert.deepEqual(
    schema.$defs.coreClr.dependentRequired.checkedJit,
    ['runtimeCommit', 'jitCommit'],
  )
  assert.equal(schema.$defs.checkedJit.properties.compiler.const, 'gcc')
  assert.equal(schema.$defs.checkedJit.properties.generator.const, 'make')
  assert.equal(
    schema.$defs.checkedJit.properties.versionGenerationMode.const,
    'skip-by-upstream-flag',
  )
  assert.deepEqual(schema.$defs.checkedJitBootstrapSdk.required, ['version', 'url', 'sha512'])
  const sourcePattern = new RegExp(
    schema.$defs.checkedJitSourceArchive.properties.url.pattern,
  )
  const bootstrapVersionPattern = new RegExp(
    schema.$defs.checkedJitBootstrapSdk.properties.version.pattern,
  )
  const bootstrapUrlPattern = new RegExp(
    schema.$defs.checkedJitBootstrapSdk.properties.url.pattern,
  )
  const builderPattern = new RegExp(schema.$defs.digestPinnedImage.pattern)
  const checkedRows = matrix.coreClr.filter(runtime => runtime.checkedJit !== undefined)
  assert.deepEqual(checkedRows.map(runtime => runtime.id), [
    'dotnet-6',
    'dotnet-7',
    'dotnet-8',
    'dotnet-9',
  ])

  for (const runtime of checkedRows) {
    const checkedJit = runtime.checkedJit
    assert.equal(checkedJit.commit, runtime.runtimeCommit, runtime.id)
    assert.equal(checkedJit.commit, runtime.jitCommit, runtime.id)
    assert.equal(
      checkedJit.sourceArchive.url,
      `https://github.com/dotnet/runtime/archive/${checkedJit.commit}.tar.gz`,
      runtime.id,
    )
    assert.equal(sourcePattern.test(checkedJit.sourceArchive.url), true, runtime.id)
    assert.equal(builderPattern.test(checkedJit.builderImage), true, runtime.id)
    assert.match(checkedJit.sourceArchive.sha512, /^[0-9a-f]{128}$/, runtime.id)
    assert.equal(checkedJit.compiler, 'gcc', runtime.id)
    assert.equal(checkedJit.generator, 'make', runtime.id)
  }

  const net6BootstrapSdk = checkedRows
    .find(runtime => runtime.id === 'dotnet-6')
    .checkedJit.bootstrapSdk
  assert.deepEqual(net6BootstrapSdk, {
    version: '6.0.135',
    url: 'https://builds.dotnet.microsoft.com/dotnet/Sdk/6.0.135/dotnet-sdk-6.0.135-linux-x64.tar.gz',
    sha512: 'f990fa0636385a3a4ea6b0e1ccaa45613fef442d3610015236fc2474895f2c2446559f2fb942c901171bb847cd825fcc575fb82d120cc5d1cf175d5c0ae01cff',
  })
  assert.equal(bootstrapVersionPattern.test(net6BootstrapSdk.version), true)
  assert.equal(bootstrapUrlPattern.test(net6BootstrapSdk.url), true)
  assert.match(net6BootstrapSdk.sha512, /^[0-9a-f]{128}$/)
  assert.equal(
    checkedRows.find(runtime => runtime.id === 'dotnet-6').checkedJit.versionGenerationMode,
    'skip-by-upstream-flag',
  )
  for (const id of ['dotnet-7', 'dotnet-8', 'dotnet-9']) {
    assert.equal(
      checkedRows.find(runtime => runtime.id === id).checkedJit.bootstrapSdk,
      undefined,
      `${id} must not inherit the .NET 6 bootstrap SDK`,
    )
    assert.equal(
      checkedRows.find(runtime => runtime.id === id).checkedJit.versionGenerationMode,
      undefined,
      `${id} must retain upstream native version generation`,
    )
  }

  assert.equal(
    checkedRows.find(runtime => runtime.id === 'dotnet-6').checkedJit.sourceMappingKind,
    'none',
  )
  for (const id of ['dotnet-7', 'dotnet-8', 'dotnet-9']) {
    assert.equal(
      checkedRows.find(runtime => runtime.id === id).checkedJit.sourceMappingKind,
      'checked-jit-debug-info',
      id,
    )
  }
})

test('modern Linux profiler providers close VMR, builder, and vendored source identity', () => {
  assert.deepEqual(
    schema.$defs.coreClr.dependentRequired.profilerProvider,
    ['runtimeCommit', 'jitCommit'],
  )
  assert.equal(
    schema.$defs.coreClr.dependentSchemas.checkedJit.propertyNames.not.const,
    'profilerProvider',
    'a CoreCLR row cannot select Checked JIT and the Release-runtime profiler together',
  )

  const providerRows = matrix.coreClr.filter(runtime => runtime.profilerProvider !== undefined)
  assert.deepEqual(providerRows.map(runtime => runtime.id), [
    'dotnet-10',
    'dotnet-11-preview',
  ])

  const expectedRuntimeCommits = new Map([
    ['dotnet-10', 'f7d90799ce4ef09a0bb257852a57248d2a8fb8dd'],
    ['dotnet-11-preview', 'ba53d0ed335bed4ab7bfd01988c8e3953ee5ffbe'],
  ])
  const profilerBuilder = baseImages.images.find(image => image.id === 'dotnet-runtime-build')
  const scaffold = releaseLock.components['jit-profiler-clr-samples']
  const runtimeHeaders = releaseLock.components['jit-profiler-runtime-headers']

  for (const runtime of providerRows) {
    const provider = runtime.profilerProvider
    assert.equal(runtime.runtimeCommit, expectedRuntimeCommits.get(runtime.id), runtime.id)
    assert.equal(runtime.jitCommit, runtime.runtimeCommit, runtime.id)
    assert.equal(runtime.checkedJit, undefined, `${runtime.id} cannot reinterpret a VMR commit as dotnet/runtime source`)
    assert.equal(provider.id, 'sharplabnext-linux-profiler-v1', runtime.id)
    assert.equal(provider.sourceMappingKind, 'linux-profiler', runtime.id)
    assert.equal(provider.builderImage, profilerBuilder.reference, runtime.id)
    assert.deepEqual(provider.scaffold, {
      commit: scaffold.commit,
      sourceUri: scaffold.sourceUri,
    }, runtime.id)
    assert.deepEqual(provider.runtimeHeaders, {
      commit: runtimeHeaders.commit,
      sourceUri: runtimeHeaders.sourceUri,
    }, runtime.id)
  }
})
