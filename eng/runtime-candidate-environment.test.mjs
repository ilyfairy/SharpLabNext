import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import { validateCandidateBuildInputs } from './build-runtime-candidate.mjs'
import {
  canonicalFrameworkCandidateInput,
  deriveRuntimeCandidateEnvironment,
  frameworkCandidateInputStrategy,
  readFrameworkCandidateInput,
  readRuntimeMatrix,
  runRuntimeCandidateEnvironment,
} from './runtime-candidate-environment.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const matrixPath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
const matrix = readRuntimeMatrix(matrixPath)
const fakeWineImage = pinnedImage('wine-operator', '9')

function pinnedImage(name, character) {
  return `registry.example/${name}@sha256:${character.repeat(64)}`
}

function commonEnvironment() {
  return {
    IMAGE_PREFIX: 'registry.example/sharplabnext',
    RELEASE_ID: 'candidate-test',
    SOURCE_DATE_EPOCH: '1',
    SOURCE_REVISION: 'f'.repeat(40),
    BASE_DOTNET_SDK_IMAGE: pinnedImage('dotnet-sdk', 'a'),
    WINE_CONTROL_TFM: matrix.controlRuntime.targetFramework,
  }
}

function frameworkInput() {
  return {
    schemaVersion: 1,
    strategy: frameworkCandidateInputStrategy,
    parentImage: pinnedImage('framework-parent', 'b'),
    metadataImage: pinnedImage('framework-metadata', 'c'),
    matrixInputSha256: `sha256:${'d'.repeat(64)}`,
    rows: matrix.framework.targets.map((row, index) => ({
      id: row.id,
      operatorImage: pinnedImage(`operator-${row.id}`, ((index % 8) + 1).toString()),
      rowDigest: `sha256:${((index % 6) + 4).toString().repeat(64)}`,
    })),
  }
}

function derive(profileId) {
  return deriveRuntimeCandidateEnvironment(profileId, matrix, {
    wineImage: profileId.startsWith('wine-') ? fakeWineImage : undefined,
    frameworkInput: profileId.startsWith('wine-netfx') ? frameworkInput() : undefined,
  })
}

test('all 34 formal runtime rows derive complete candidate inputs accepted by the build boundary', () => {
  const rows = [
    ...matrix.coreClr.map(row => `${row.id}-linux-x64`),
    ...matrix.coreClr
      .filter(row => Number.parseInt(row.channel, 10) >= 5)
      .map(row => `wine-${row.id}-linux-x64`),
    matrix.mono.id,
    ...matrix.framework.targets.map(row => `wine-${row.id}-linux-x64`),
  ]
  assert.equal(rows.length, 34)
  const targetCounts = new Map()
  for (const profileId of rows) {
    const result = derive(profileId)
    targetCounts.set(result.target, (targetCounts.get(result.target) ?? 0) + 1)
    assert.equal(result.environment.RUNTIME_MATRIX_PROFILE_ID, profileId)
    assert.equal(
      Object.keys(result.environment).every(name => name.startsWith('RUNTIME_MATRIX_')),
      true,
      profileId,
    )
    assert.equal('IMAGE_PREFIX' in result.environment, false, profileId)
    assert.deepEqual(
      validateCandidateBuildInputs(result.target, {
        ...commonEnvironment(),
        ...result.environment,
      }),
      [],
      profileId,
    )
  }
  assert.deepEqual(Object.fromEntries(targetCounts), {
    'runtime-dotnet-matrix-candidate': 12,
    'runtime-wine-dotnet-matrix-candidate': 7,
    'runtime-mono-matrix-candidate': 1,
    'runtime-wine-framework-matrix-shared-candidate': 14,
  })
})

test('the five Wine CoreCLR 2.x and 3.x profiles are explicitly excluded', () => {
  const excluded = matrix.coreClr
    .filter(row => Number.parseInt(row.channel, 10) < 5)
    .map(row => `wine-${row.id}-linux-x64`)
  assert.equal(excluded.length, 5)
  for (const profileId of excluded) {
    assert.throws(
      () => deriveRuntimeCandidateEnvironment(profileId, matrix, { wineImage: fakeWineImage }),
      /excluded.*CoreCLR 2\.x\/3\.x/s,
      profileId,
    )
  }
})

test('control image is matrix-owned while each Wine row requires an explicit digest-pinned operator', () => {
  const profileId = 'wine-dotnet-9-linux-x64'
  assert.throws(() => deriveRuntimeCandidateEnvironment(profileId, matrix), /explicit Wine operator/)
  assert.throws(
    () => deriveRuntimeCandidateEnvironment(profileId, matrix, { wineImage: 'registry.example/wine:latest' }),
    /repository@sha256/,
  )
  const result = deriveRuntimeCandidateEnvironment(profileId, matrix, { wineImage: fakeWineImage })
  assert.equal(result.environment.RUNTIME_MATRIX_WINE_IMAGE, fakeWineImage)
  assert.equal(result.environment.RUNTIME_MATRIX_CONTROL_IMAGE, matrix.controlRuntime.image)
})

test('matrix drift and missing identities fail before a candidate command starts', () => {
  const cases = [
    [
      value => { value.coreClr[0].linux.sha512 = 'not-a-digest' },
      'dotnet-core-2.0-linux-x64',
      /SHA-512/,
    ],
    [
      value => { delete value.coreClr[0].runtimeCommit },
      'dotnet-core-2.0-linux-x64',
      /runtimeCommit/,
    ],
    [
      value => { value.controlRuntime.image = 'mcr.microsoft.com/dotnet/aspnet:latest' },
      matrix.mono.id,
      /controlRuntime\.image.*repository@sha256/,
    ],
  ]
  for (const [mutate, profileId, pattern] of cases) {
    const changed = structuredClone(matrix)
    mutate(changed)
    assert.throws(() => deriveRuntimeCandidateEnvironment(profileId, changed), pattern)
  }
})

test('Framework external input is bounded, exact, canonical and closes all row identities', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-candidate-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const input = frameworkInput()
  const filename = path.join(root, 'framework-candidates.json')
  fs.writeFileSync(filename, canonicalFrameworkCandidateInput(input, matrix))
  const parsed = readFrameworkCandidateInput(filename, matrix)
  assert.deepEqual(parsed, input)

  const selected = deriveRuntimeCandidateEnvironment('wine-netfx48-linux-x64', matrix, {
    wineImage: fakeWineImage,
    frameworkInput: parsed,
  })
  const row = input.rows.find(value => value.id === 'netfx48')
  assert.equal(selected.environment.RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE, input.parentImage)
  assert.equal(
    selected.environment.RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI,
    `docker://${input.metadataImage}`,
  )
  assert.equal(selected.environment.RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE, row.operatorImage)
  assert.equal(selected.environment.RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST, row.rowDigest)
  assert.equal(selected.environment.RUNTIME_MATRIX_RUNTIME_DIGEST, row.operatorImage.split('@')[1])
})

test('Framework external input rejects drift, wrong digests, malicious fields and noncanonical bytes', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-candidate-bad-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const filename = path.join(root, 'input.json')
  const cases = [
    [
      { ...frameworkInput(), unexpected: 'FROM scratch\n' },
      /contain exactly/,
    ],
    [
      { ...frameworkInput(), parentImage: pinnedImage('parent', 'A') },
      /repository@sha256/,
    ],
    [
      {
        ...frameworkInput(),
        metadataImage: `registry.example/metadata\"escape@sha256:${'a'.repeat(64)}`,
      },
      /repository@sha256/,
    ],
    [
      {
        ...frameworkInput(),
        rows: frameworkInput().rows.map((row, index) => index === 0
          ? { ...row, rowDigest: `sha256:${'A'.repeat(64)}` }
          : row),
      },
      /rowDigest.*sha256/,
    ],
    [
      { ...frameworkInput(), rows: [...frameworkInput().rows].reverse() },
      /runtime matrix order/,
    ],
  ]
  for (const [value, pattern] of cases) {
    fs.writeFileSync(filename, `${JSON.stringify(value)}\n`)
    assert.throws(() => readFrameworkCandidateInput(filename, matrix), pattern)
  }

  fs.writeFileSync(filename, `${JSON.stringify(frameworkInput(), null, 2)}\n`)
  assert.throws(() => readFrameworkCandidateInput(filename, matrix), /canonical JSON bytes/)
  fs.writeFileSync(filename, Buffer.alloc(1024 * 1024 + 1, 0x20))
  assert.throws(() => readFrameworkCandidateInput(filename, matrix), /1\.\.1048576 byte/)
})

test('CLI output is a row-only overlay and command mode invokes the reviewed build entry', () => {
  const output = {
    logs: [],
    errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  assert.equal(runRuntimeCandidateEnvironment([
    'dotnet-5-linux-x64',
    '--runtime-matrix', matrixPath,
  ], { output }), 0)
  const printed = JSON.parse(output.logs[0])
  assert.equal(printed.target, 'runtime-dotnet-matrix-candidate')
  assert.equal(Object.keys(printed.environment).every(key => key.startsWith('RUNTIME_MATRIX_')), true)

  const calls = []
  assert.equal(runRuntimeCandidateEnvironment([
    'dotnet-5-linux-x64',
    '--runtime-matrix', matrixPath,
    '--', '--progress', 'plain',
  ], {
    output,
    values: { ORDINARY_BASE_INPUT: 'retained' },
    spawn(command, arguments_, options) {
      calls.push({ command, arguments_, options })
      return { status: 0 }
    },
  }), 0)
  assert.equal(calls.length, 1)
  assert.equal(path.basename(calls[0].arguments_[0]), 'build-runtime-candidate.mjs')
  assert.equal(calls[0].arguments_[1], 'runtime-dotnet-matrix-candidate')
  assert.deepEqual(calls[0].arguments_.slice(-2), ['--progress', 'plain'])
  assert.equal(calls[0].options.env.ORDINARY_BASE_INPUT, 'retained')
  assert.equal(calls[0].options.env.RUNTIME_MATRIX_PROFILE_ID, 'dotnet-5-linux-x64')

  assert.equal(runRuntimeCandidateEnvironment([
    'dotnet-5-linux-x64',
    '--runtime-matrix', matrixPath,
    '--publish-to', 'registry.example/sharplabnext/runtime-dotnet-5-linux-x64:candidate-test',
  ], {
    output,
    values: { ORDINARY_BASE_INPUT: 'retained' },
    spawn(command, arguments_, options) {
      calls.push({ command, arguments_, options })
      return { status: 0 }
    },
  }), 0)
  assert.equal(path.basename(calls[1].arguments_[0]), 'publish-runtime-candidate.mjs')
  assert.deepEqual(calls[1].arguments_.slice(1), [
    'runtime-dotnet-matrix-candidate',
    '--destination',
    'registry.example/sharplabnext/runtime-dotnet-5-linux-x64:candidate-test',
  ])
  assert.equal(calls[1].options.env.ORDINARY_BASE_INPUT, 'retained')
})
