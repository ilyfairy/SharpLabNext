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
const releaseLock = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'profiles', 'lock.json'), 'utf8'))
const baseImages = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'profiles', 'base-images.json'), 'utf8'))
const wineUserspace = releaseLock.components['wine-coreclr-userspace']
const runtimeDepsImage = baseImages.images.find(image => image.id === 'dotnet-runtime-deps')
assert.ok(wineUserspace)
assert.ok(runtimeDepsImage)
const fakeWineImage = pinnedImage('wine-operator', '9')
const localDevelopmentWineImage = 'registry.example/sharplabnext/operator-wine-coreclr:candidate-test'
const localDevelopmentWineImageId = `sha256:${'7'.repeat(64)}`
const outerDevelopmentGrantInput = 'SHARPLABNEXT_BAKE_ALLOW_UNCOMMITTED_SOURCE_FOR_DEVELOPMENT'
const historicalFrameworkDevelopmentInput = 'RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_DEVELOPMENT_OPT_IN'

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
    BASE_DOTNET_RUNTIME_DEPS_IMAGE: runtimeDepsImage.reference,
    WINE_CONTROL_TFM: matrix.controlRuntime.targetFramework,
    WINE_CORECLR_USERSPACE_VERSION: wineUserspace.resolvedVersion,
    WINE_CORECLR_USERSPACE_DIGEST: wineUserspace.digest,
    WINE_CORECLR_USERSPACE_SOURCE_URI: wineUserspace.sourceUri,
  }
}

test('outer Bake launcher sanitizes and emits only its explicit development grant', () => {
  const source = fs.readFileSync(path.join(repositoryRoot, 'eng', 'run-with-bake-environment.cs'), 'utf8')
  const marker = 'SHARPLABNEXT_BAKE_ALLOW_UNCOMMITTED_SOURCE_FOR_DEVELOPMENT'
  assert.ok(source.includes(marker))
  assert.match(source, /startInfo\.Environment\.Remove\(developmentGrantEnvironmentVariable\)/)
  assert.match(
    source,
    /if \(allowUncommittedSourceForDevelopment\)\s+startInfo\.Environment\[developmentGrantEnvironmentVariable\] = "true"/,
  )
})

function frameworkInput() {
  return {
    schemaVersion: 1,
    strategy: frameworkCandidateInputStrategy,
    parentImage: pinnedImage('framework-parent', 'b'),
    metadataImage: pinnedImage('framework-metadata', 'c'),
    matrixInputSha256: `sha256:${'d'.repeat(64)}`,
    sourceRevision: 'f'.repeat(40),
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
  assert.equal(selected.environment.RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION, input.sourceRevision)
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
    [
      (() => { const value = frameworkInput(); delete value.sourceRevision; return value })(),
      /contain exactly/,
    ],
    [
      { ...frameworkInput(), sourceRevision: 'A'.repeat(40) },
      /sourceRevision.*Git commit identity/,
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

test('Wine command mode requires and forwards an explicit signed operator receipt pair', () => {
  const output = {
    logs: [],
    errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-wine-receipt-cli-'))
  const receipt = path.join(root, 'operator-receipt.json')
  const signature = path.join(root, 'operator-receipt.json.sig')
  const calls = []
  const spawn = (command, arguments_, options) => {
    calls.push({ command, arguments_, options })
    return { status: 0 }
  }
  try {
    assert.equal(runRuntimeCandidateEnvironment([
      'wine-dotnet-9-linux-x64',
      '--wine-image', fakeWineImage,
      '--', '--progress', 'plain',
    ], { output, spawn }), 1)
    assert.match(output.errors.at(-1), /require --wine-operator-receipt/)
    assert.equal(calls.length, 0)

    assert.equal(runRuntimeCandidateEnvironment([
      'wine-dotnet-9-linux-x64',
      '--wine-image', fakeWineImage,
      '--wine-operator-receipt', receipt,
      '--wine-operator-receipt-signature', signature,
      '--', '--progress', 'plain',
    ], {
      output,
      values: {
        WINE_CORECLR_OPERATOR_RECEIPT: 'C:\\stale\\receipt.json',
        WINE_CORECLR_OPERATOR_RECEIPT_SIG: 'C:\\stale\\receipt.json.sig',
      },
      spawn,
    }), 0)
    assert.equal(calls.length, 1)
    assert.equal(calls[0].options.env.WINE_CORECLR_OPERATOR_RECEIPT, receipt)
    assert.equal(calls[0].options.env.WINE_CORECLR_OPERATOR_RECEIPT_SIG, signature)

    assert.equal(runRuntimeCandidateEnvironment([
      'wine-dotnet-9-linux-x64',
      '--wine-image', fakeWineImage,
      '--wine-operator-receipt', receipt,
      '--', '--progress', 'plain',
    ], { output, spawn }), 1)
    assert.match(output.errors.at(-1), /must be supplied together/)

    assert.equal(runRuntimeCandidateEnvironment([
      'dotnet-9-linux-x64',
      '--wine-operator-receipt', receipt,
      '--wine-operator-receipt-signature', signature,
      '--', '--progress', 'plain',
    ], { output, spawn }), 1)
    assert.match(output.errors.at(-1), /not applicable/)

    assert.equal(runRuntimeCandidateEnvironment([
      'wine-dotnet-9-linux-x64',
      '--wine-image', fakeWineImage,
      '--wine-operator-receipt', receipt,
      '--wine-operator-receipt-signature', signature,
    ], { output, spawn }), 1)
    assert.match(output.errors.at(-1), /only accepted for build or publish/)
    assert.equal(calls.length, 1)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('Wine CoreCLR development command requires explicit outer and candidate opt-ins', () => {
  const output = {
    logs: [], errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  const calls = []
  const baseOptions = {
    output,
    inspectDockerImage(reference) {
      assert.equal(reference, localDevelopmentWineImage)
      return { imageId: localDevelopmentWineImageId }
    },
    spawn(command, arguments_, invocation) {
      calls.push({ command, arguments_, invocation })
      return { status: 0 }
    },
  }

  // The command-scoped outer grant cannot select development mode by itself.
  assert.equal(runRuntimeCandidateEnvironment([
    'wine-dotnet-9-linux-x64',
    '--wine-image', localDevelopmentWineImage,
    '--', '--progress', 'plain',
  ], {
    ...baseOptions,
    values: {
      ...commonEnvironment(),
      [outerDevelopmentGrantInput]: 'true',
    },
  }), 1)
  assert.equal(calls.length, 0)

  // The candidate opt-in cannot manufacture the outer grant.
  output.errors.length = 0
  assert.equal(runRuntimeCandidateEnvironment([
    'wine-dotnet-9-linux-x64',
    '--wine-image', localDevelopmentWineImage,
    '--', '--allow-uncommitted-source-for-development', '--progress', 'plain',
  ], {
    ...baseOptions,
    values: commonEnvironment(),
  }), 1)
  assert.match(output.errors.at(-1), /outer run-with-bake-environment development grant/)
  assert.equal(calls.length, 0)

  output.errors.length = 0
  assert.equal(runRuntimeCandidateEnvironment([
    'wine-dotnet-9-linux-x64',
    '--wine-image', localDevelopmentWineImage,
    '--', '--allow-uncommitted-source-for-development', '--progress', 'plain',
  ], {
    ...baseOptions,
    values: {
      ...commonEnvironment(),
      [outerDevelopmentGrantInput]: 'true',
      WINE_CORECLR_OPERATOR_RECEIPT: 'stale-receipt.json',
      WINE_CORECLR_OPERATOR_RECEIPT_SIG: 'stale-receipt.json.sig',
      WINE_CORECLR_DEVELOPMENT_WRAPPER_OPT_IN: 'stale',
      WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG: 'stale',
      WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE_ID: 'stale',
      WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE: 'true',
    },
  }), 0)
  assert.equal(calls.length, 1)
  assert.deepEqual(calls[0].arguments_.slice(1), [
    'runtime-wine-dotnet-matrix-candidate',
    '--allow-uncommitted-source-for-development',
    '--progress', 'plain',
  ])
  assert.equal(
    calls[0].arguments_.filter(argument => argument === '--allow-uncommitted-source-for-development').length,
    1,
  )
  assert.equal(calls[0].invocation.env.RUNTIME_MATRIX_WINE_IMAGE, localDevelopmentWineImageId)
  assert.equal(calls[0].invocation.env[outerDevelopmentGrantInput], undefined)
  assert.equal(calls[0].invocation.env.WINE_CORECLR_DEVELOPMENT_WRAPPER_OPT_IN, 'true')
  assert.equal(calls[0].invocation.env.WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG, localDevelopmentWineImage)
  assert.equal(calls[0].invocation.env.WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE_ID, localDevelopmentWineImageId)
  assert.equal(calls[0].invocation.env.WINE_CORECLR_OPERATOR_RECEIPT, undefined)
  assert.equal(calls[0].invocation.env.WINE_CORECLR_OPERATOR_RECEIPT_SIG, undefined)
  assert.equal(calls[0].invocation.env.WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE, undefined)

  for (const argv of [
    [
      'wine-dotnet-9-linux-x64', '--wine-image', fakeWineImage,
      '--', '--allow-uncommitted-source-for-development', '--progress', 'plain',
    ],
    [
      'wine-dotnet-9-linux-x64', '--wine-image', localDevelopmentWineImage,
      '--', '--allow-uncommitted-source-for-development',
      '--allow-uncommitted-source-for-development', '--progress', 'plain',
    ],
    [
      'wine-dotnet-9-linux-x64', '--wine-image', localDevelopmentWineImage,
      '--publish-to',
      'registry.example/sharplabnext/runtime-wine-dotnet-9-linux-x64:candidate-test',
      '--', '--allow-uncommitted-source-for-development',
    ],
    [
      'wine-dotnet-9-linux-x64', '--wine-image', localDevelopmentWineImage,
      '--allow-uncommitted-source-for-development',
    ],
  ]) {
    output.errors.length = 0
    assert.equal(runRuntimeCandidateEnvironment(argv, {
      ...baseOptions,
      values: {
        ...commonEnvironment(),
        [outerDevelopmentGrantInput]: 'true',
      },
    }), 1)
  }
  assert.equal(calls.length, 1)
})

test('historical Framework development mode is explicit, local-only, and non-receipted', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-historical-framework-cli-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const input = { ...frameworkInput(), sourceRevision: 'e'.repeat(40) }
  const inputPath = path.join(root, 'framework-input.json')
  fs.writeFileSync(inputPath, canonicalFrameworkCandidateInput(input, matrix))
  const output = { logs: [], errors: [], log(value) { this.logs.push(value) }, error(value) { this.errors.push(value) } }
  const calls = []
  const baseValues = {
    ...commonEnvironment(),
    [outerDevelopmentGrantInput]: 'true',
    [historicalFrameworkDevelopmentInput]: 'forged',
    WINE_CORECLR_OPERATOR_RECEIPT: 'stale-receipt.json',
    WINE_CORECLR_OPERATOR_RECEIPT_SIG: 'stale-receipt.json.sig',
  }
  const spawn = (command, arguments_, options) => {
    calls.push({ command, arguments_, options })
    return { status: 0 }
  }
  const commonArguments = [
    'wine-netfx48-linux-x64', '--wine-image', fakeWineImage,
    '--framework-input', inputPath,
  ]

  assert.equal(runRuntimeCandidateEnvironment([
    ...commonArguments,
    '--', '--allow-historical-framework-input-for-development', '--progress', 'plain',
  ], { output, values: baseValues, spawn }), 0, output.errors.join('\n'))
  assert.equal(calls.length, 1)
  assert.deepEqual(calls[0].arguments_.slice(1), [
    'runtime-wine-framework-matrix-shared-candidate',
    '--allow-historical-framework-input-for-development', '--progress', 'plain',
  ])
  assert.equal(calls[0].options.env[historicalFrameworkDevelopmentInput], 'true')
  assert.equal(calls[0].options.env[outerDevelopmentGrantInput], undefined)
  assert.equal(calls[0].options.env.WINE_CORECLR_OPERATOR_RECEIPT, undefined)
  assert.equal(calls[0].options.env.WINE_CORECLR_OPERATOR_RECEIPT_SIG, undefined)
  assert.equal(calls[0].options.env.WINE_CORECLR_DEVELOPMENT_WRAPPER_OPT_IN, undefined)

  assert.equal(runRuntimeCandidateEnvironment([
    ...commonArguments,
    '--', '--allow-historical-framework-input-for-development',
    '--allow-uncommitted-source-for-development', '--progress', 'plain',
  ], { output, values: baseValues, spawn }), 0, output.errors.join('\n'))
  assert.equal(calls.length, 2)
  assert.equal(calls[1].options.env[historicalFrameworkDevelopmentInput], 'true')
  assert.equal(calls[1].options.env.WINE_CORECLR_DEVELOPMENT_WRAPPER_OPT_IN, undefined)

  const cases = [
    {
      name: 'non-Framework target',
      argv: [
        'wine-dotnet-9-linux-x64', '--wine-image', fakeWineImage,
        '--', '--allow-historical-framework-input-for-development', '--progress', 'plain',
      ],
      values: baseValues,
      error: /only for shared Wine Framework candidates/,
    },
    {
      name: 'no outer grant',
      argv: [...commonArguments, '--', '--allow-historical-framework-input-for-development'],
      values: commonEnvironment(),
      error: /outer run-with-bake-environment development grant/,
    },
    {
      name: 'same source revision',
      argv: [...commonArguments, '--', '--allow-historical-framework-input-for-development'],
      values: { ...baseValues, SOURCE_REVISION: input.sourceRevision },
      error: /distinct valid Framework input and candidate source revisions/,
    },
    {
      name: 'invalid candidate source revision',
      argv: [...commonArguments, '--', '--allow-historical-framework-input-for-development'],
      values: { ...baseValues, SOURCE_REVISION: 'not-a-commit' },
      error: /distinct valid Framework input and candidate source revisions/,
    },
    {
      name: 'check invocation',
      argv: [...commonArguments, '--', '--allow-historical-framework-input-for-development', '--check'],
      values: baseValues,
      error: /only for real local candidate builds/,
    },
    {
      name: 'print invocation',
      argv: [...commonArguments, '--', '--allow-historical-framework-input-for-development', '--print'],
      values: baseValues,
      error: /only for real local candidate builds/,
    },
    {
      name: 'call invocation',
      argv: [...commonArguments, '--', '--allow-historical-framework-input-for-development', '--call', 'outline'],
      values: baseValues,
      error: /only for real local candidate builds/,
    },
    {
      name: 'duplicate flag',
      argv: [
        ...commonArguments,
        '--', '--allow-historical-framework-input-for-development',
        '--allow-historical-framework-input-for-development',
      ],
      values: baseValues,
      error: /may be supplied once/,
    },
    {
      name: 'publish invocation',
      argv: [
        ...commonArguments,
        '--publish-to', 'registry.example/sharplabnext/runtime-wine-netfx48-linux-x64:candidate-test',
        '--', '--allow-historical-framework-input-for-development',
      ],
      values: baseValues,
      error: /cannot be combined with candidate build options|only for real local candidate builds/,
    },
    {
      name: 'receipt supplied',
      argv: [
        ...commonArguments,
        '--wine-operator-receipt', 'C:\\operator\receipt.json',
        '--wine-operator-receipt-signature', 'C:\\operator\receipt.json.sig',
        '--', '--allow-historical-framework-input-for-development',
      ],
      values: baseValues,
      error: /must not receive formal operator receipts/,
    },
  ]
  for (const { name, argv, values, error } of cases) {
    output.errors.length = 0
    assert.equal(runRuntimeCandidateEnvironment(argv, { output, values, spawn }), 1, name)
    assert.match(output.errors.join('\n'), error, name)
  }
  assert.equal(calls.length, 2)
})
