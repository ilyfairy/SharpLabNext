import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  createFrameworkCandidateInput,
  createFrameworkCandidateInputFromImages,
  runCreateFrameworkCandidateInput,
} from '../create-runtime-framework-candidate-input.mjs'
import { matrixInputDigest } from '../build-framework-matrix-context.mjs'
import { readRuntimeMatrix } from '../runtime-candidate-environment.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const runtimeMatrixPath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
const runtimeMatrix = readRuntimeMatrix(runtimeMatrixPath)
const installerManifestSha256 = crypto.createHash('sha256').update(fs.readFileSync(path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json'))).digest('hex');
const revision = 'a'.repeat(40)

function image(name, value) { return `registry.example/sharplabnext/${name}@sha256:${value.repeat(64)}`; }

function inputRows() {
  return runtimeMatrix.framework.targets.map((target, index) => ({
    id: target.id,
    version: target.version,
    clrGeneration: target.clrGeneration,
    targetPrefix: target.clrGeneration,
    companionVersions: {
      clr2: target.clrGeneration === 'clr2' ? target.version : '3.5',
      clr4: target.clrGeneration === 'clr4' ? target.version : '4.8',
    },
    operatorImage: image(`operator-${target.id}`, ((index % 9) + 1).toString()),
  }))
}

function prepare(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-input-generator-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const matrixInput = { schemaVersion: 1, strategy: 'shared-framework-prefix-input-v1', rows: inputRows() }
  const matrixInputPath = path.join(root, 'matrix-input.json')
  fs.writeFileSync(matrixInputPath, `${JSON.stringify(matrixInput)}\n`)
  const parentImage = image('framework-parent', 'b')
  const metadataImage = image('framework-metadata', 'c')
  const wineImage = image('wine-operator', 'd')
  const rootImage = image('root', 'e')
  const digest = matrixInputDigest(matrixInput)
  const rows = matrixInput.rows.map((row, index) => ({
    schemaVersion: 1,
    id: row.id,
    version: row.version,
    clrGeneration: row.clrGeneration,
    targetPrefix: row.targetPrefix,
    companionVersions: row.companionVersions,
    operatorImage: row.operatorImage,
    prefixes: { [row.targetPrefix]: `${row.id}/${row.targetPrefix}` },
    rowDigest: ((index % 9) + 1).toString().repeat(64),
  }))
  const manifest = { schemaVersion: 1, strategy: 'shared-framework-target-prefix-matrix-v1', inputManifestSha256: digest, rows }
  return {
    root, matrixInput, matrixInputPath, parentImage, metadataImage,
    wineImage, rootImage, digest, manifest,
  }
}

function fakeSpawn(state) {
  const info = (reference, labels) => ({
    Id: reference.slice(reference.lastIndexOf('@') + 1), RepoDigests: [reference],
    Os: 'linux', Architecture: 'amd64', Size: 1024, Config: { Labels: labels },
  })
  return (command, args) => {
    if (command === 'git') throw new Error(`unexpected Git invocation: ${args.join(' ')}`)
    if (command !== 'docker') throw new Error(`unexpected command ${command}`)
    if (args[0] === 'image' && args[1] === 'inspect') {
      const reference = args[2]
      if (reference === state.metadataImage) return { status: 0, stdout: JSON.stringify([info(reference, {
        'io.sharplabnext.framework.matrix-context': 'true',
        'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
        'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
        'io.sharplabnext.framework.matrix-input-sha256': state.digest,
        'io.sharplabnext.framework.matrix-row-count': '14',
        'org.opencontainers.image.revision': state.metadataRevision ?? revision,
        'io.sharplabnext.source.revision': state.metadataSourceRevision ?? state.metadataRevision ?? revision,
      })]) }
      if (reference === state.parentImage) return { status: 0, stdout: JSON.stringify([info(reference, {
        'io.sharplabnext.operator-only': 'true',
        'io.sharplabnext.framework.matrix': 'true',
        'io.sharplabnext.framework.matrix-strategy': 'shared-framework-target-prefix-matrix-v1',
        'io.sharplabnext.framework.dedupe-policy': 'wine-static-runtime-payload-v1',
        'io.sharplabnext.framework.matrix-input-sha256': state.digest,
        'io.sharplabnext.framework.matrix-source-uri': `docker://${state.metadataImage}`,
        'io.sharplabnext.operator-image.wine': state.wineImage,
        'io.sharplabnext.operator-root': state.rootImage,
        'org.opencontainers.image.revision': state.parentRevision ?? revision,
        'io.sharplabnext.source.revision': state.parentSourceRevision ?? state.parentRevision ?? revision,
      })]) }
      const row = state.matrixInput.rows.find(candidate => candidate.operatorImage === reference)
      if (row === undefined) throw new Error(`unexpected image ${reference}`)
      return { status: 0, stdout: JSON.stringify([info(reference, {
        'io.sharplabnext.operator-only': 'true',
        'io.sharplabnext.framework.target-id': row.id,
        'io.sharplabnext.framework.version': row.version,
        'io.sharplabnext.framework.clr-generation': row.clrGeneration,
        'io.sharplabnext.wine-prefix-layout': 'hardlink-immutable-v1',
        'io.sharplabnext.wine-prefix-layout-manifest': '/opt/sharplabnext/.wine-prefix-layout.json',
        'io.sharplabnext.framework.installer-manifest-sha256':
          state.installerManifestSha256 ?? installerManifestSha256,
        'io.sharplabnext.operator-base': state.operatorWineImage ?? state.wineImage,
        'io.sharplabnext.operator-root': state.rootImage,
        'org.opencontainers.image.revision': state.operatorRevision ?? revision,
        'io.sharplabnext.source.revision': state.operatorSourceRevision ?? state.operatorRevision ?? revision,
      })]) }
    }
    if (args[0] === 'create') return { status: 0, stdout: `${'d'.repeat(64)}\n` }
    if (args[0] === 'cp') {
      const value = args[1].endsWith(':/matrix-input.json')
        ? state.metadataMatrixInput ?? state.matrixInput
        : state.manifest
      fs.writeFileSync(args[2], `${JSON.stringify(value)}\n`)
      return { status: 0, stdout: '' }
    }
    if (args[0] === 'rm') return { status: 0, stdout: '' }
    throw new Error(`unexpected docker ${args.join(' ')}`)
  }
}

function options(state) {
  return {
    parentImage: state.parentImage, metadataImage: state.metadataImage,
    matrixInput: state.matrixInputPath, runtimeMatrix: runtimeMatrixPath,
    sourceRevision: revision, spawn: fakeSpawn(state),
  }
}

test('derives canonical Framework candidate input from all immutable identities', t => {
  const state = prepare(t)
  const result = createFrameworkCandidateInput(options(state))
  const parsed = JSON.parse(result.bytes)
  assert.equal(parsed.strategy, 'runtime-framework-candidate-input-v1')
  assert.equal(parsed.parentImage, state.parentImage)
  assert.equal(parsed.metadataImage, state.metadataImage)
  assert.equal(parsed.matrixInputSha256, state.digest)
  assert.equal(parsed.sourceRevision, revision)
  assert.deepEqual(parsed.rows.map(row => row.id), runtimeMatrix.framework.targets.map(row => row.id))
  assert.equal(parsed.rows.every(row => !Object.hasOwn(row, 'imageId')), true)
  assert.match(result.sha256, /^sha256:[0-9a-f]{64}$/)

  const reconstructed = createFrameworkCandidateInputFromImages(options(state))
  assert.deepEqual(reconstructed.value, result.value)
})

test('rejects a host matrix input that differs from immutable metadata', t => {
  const state = prepare(t)
  state.metadataMatrixInput = structuredClone(state.matrixInput)
  const changed = structuredClone(state.matrixInput)
  changed.rows[0].operatorImage = image('different-operator', 'f')
  fs.writeFileSync(state.matrixInputPath, `${JSON.stringify(changed)}\n`)

  assert.throws(
    () => createFrameworkCandidateInput(options(state)),
    /supplied matrix input digest.*does not match immutable metadata/,
  )
})

test('rejects reordering, duplicate rows, floating references, and runtime identity mismatches', t => {
  const cases = [
    ['matrix input out of order', state => { [state.matrixInput.rows[0], state.matrixInput.rows[1]] = [state.matrixInput.rows[1], state.matrixInput.rows[0]]; fs.writeFileSync(state.matrixInputPath, `${JSON.stringify(state.matrixInput)}\n`) }, /exact ordered/],
    ['out of order', state => { [state.manifest.rows[0], state.manifest.rows[1]] = [state.manifest.rows[1], state.manifest.rows[0]] }, /canonical order/],
    ['duplicate', state => { state.manifest.rows[1].id = state.manifest.rows[0].id }, /canonical order/],
    ['floating', state => { state.matrixInput.rows[0].operatorImage = 'registry.example/operator:latest'; fs.writeFileSync(state.matrixInputPath, `${JSON.stringify(state.matrixInput)}\n`) }, /operator image/],
    ['identity mismatch', state => { state.manifest.rows[0].version = '9.9' }, /runtime identity/],
  ]
  for (const [name, mutate, pattern] of cases) {
    const state = prepare(t)
    mutate(state)
    assert.throws(() => createFrameworkCandidateInput(options(state)), pattern, name)
  }
})

test('derivation is independent of the current Git HEAD and worktree state', t => {
  const state = prepare(t)
  state.dirty = true
  state.head = 'f'.repeat(40)
  const result = createFrameworkCandidateInput(options(state))
  assert.equal(result.value.sourceRevision, revision)
})

test('rejects stale revisions and operator provenance drift', t => {
  const cases = [
    ['metadata revision', state => { state.metadataRevision = 'f'.repeat(40) }, /metadata image label org.opencontainers.image.revision/],
    ['parent revision', state => { state.parentRevision = 'f'.repeat(40) }, /parent image label org.opencontainers.image.revision/],
    ['operator revision', state => { state.operatorRevision = 'f'.repeat(40) }, /operator 'netfx20' label org.opencontainers.image.revision/],
    ['metadata source revision', state => { state.metadataSourceRevision = 'f'.repeat(40) }, /metadata image label io.sharplabnext.source.revision/],
    ['parent source revision', state => { state.parentSourceRevision = 'f'.repeat(40) }, /parent image label io.sharplabnext.source.revision/],
    ['operator source revision', state => { state.operatorSourceRevision = 'f'.repeat(40) }, /operator 'netfx20' label io.sharplabnext.source.revision/],
    ['operator Wine input', state => { state.operatorWineImage = image('wrong-wine', 'f') }, /operator 'netfx20' label io.sharplabnext.operator-base/],
  ]
  for (const [name, mutate, pattern] of cases) {
    const state = prepare(t)
    mutate(state)
    assert.throws(() => createFrameworkCandidateInput(options(state)), pattern, name)
  }
})

test('CLI writes deterministic bytes atomically and refuses overwrite', t => {
  const state = prepare(t)
  const outputPath = path.join(state.root, 'candidate.json')
  const messages = { logs: [], errors: [], log(value) { this.logs.push(value) }, error(value) { this.errors.push(value) } }
  const arguments_ = [
    '--parent-image', state.parentImage, '--metadata-image', state.metadataImage,
    '--matrix-input', state.matrixInputPath, '--runtime-matrix', runtimeMatrixPath,
    '--source-revision', revision, '--output', outputPath,
  ]
  const cliOptions = {
    spawn: fakeSpawn(state),
    output: messages,
    createCommittedSourceContext: () => ({
      directory: repositoryRoot,
      dispose() {},
    }),
  }
  assert.equal(runCreateFrameworkCandidateInput(arguments_, cliOptions), 0)
  const expected = createFrameworkCandidateInput(options(state)).bytes
  assert.deepEqual(fs.readFileSync(outputPath), expected)
  assert.match(messages.logs[0], /"sha256":"sha256:[0-9a-f]{64}"/)
  assert.equal(runCreateFrameworkCandidateInput(arguments_, cliOptions), 1)
  assert.match(messages.errors.at(-1), /refusing to overwrite/)
})

test('CLI validates matrix and installer provenance from the requested committed source', t => {
  const state = prepare(t)
  const historicalRoot = path.join(state.root, 'historical-source')
  const profiles = path.join(historicalRoot, 'profiles')
  fs.mkdirSync(profiles, { recursive: true })

  const historicalMatrix = structuredClone(runtimeMatrix)
  historicalMatrix.metadataSource = 'https://historical.example.invalid/releases-index.json'
  const historicalMatrixPath = path.join(profiles, 'runtime-matrix.json')
  fs.writeFileSync(historicalMatrixPath, `${JSON.stringify(historicalMatrix)}\n`)

  const historicalInstaller = Buffer.from('{"historical":true}\n')
  state.installerManifestSha256 = crypto.createHash('sha256').update(historicalInstaller).digest('hex')
  fs.writeFileSync(path.join(profiles, 'runtime-framework-installers.json'), historicalInstaller)

  const messages = { logs: [], errors: [], log(value) { this.logs.push(value) }, error(value) { this.errors.push(value) } }
  const arguments_ = [
    '--parent-image', state.parentImage, '--metadata-image', state.metadataImage,
    '--matrix-input', state.matrixInputPath, '--runtime-matrix', historicalMatrixPath,
    '--source-revision', revision,
    '--output', path.join(state.root, 'historical-candidate.json'),
  ]
  let contextOptions
  const cliOptions = {
    spawn: fakeSpawn(state),
    output: messages,
    createCommittedSourceContext(options) {
      contextOptions = options
      return { directory: historicalRoot, dispose() {} }
    },
  }

  assert.equal(runCreateFrameworkCandidateInput(arguments_, cliOptions), 0, messages.errors.join('\n'))
  assert.equal(contextOptions.revision, revision)
  assert.deepEqual(contextOptions.requiredFiles, [
    'profiles/runtime-matrix.json',
    'profiles/runtime-framework-installers.json',
  ])

  const mismatched = prepare(t)
  const mismatchProfiles = path.join(mismatched.root, 'historical-source', 'profiles')
  fs.mkdirSync(mismatchProfiles, { recursive: true })
  fs.writeFileSync(path.join(mismatchProfiles, 'runtime-matrix.json'), `${JSON.stringify(runtimeMatrix)}\n`)
  fs.writeFileSync(path.join(mismatchProfiles, 'runtime-framework-installers.json'), historicalInstaller)
  const mismatchMessages = { logs: [], errors: [], log(value) { this.logs.push(value) }, error(value) { this.errors.push(value) } }
  assert.equal(runCreateFrameworkCandidateInput([
    '--parent-image', mismatched.parentImage, '--metadata-image', mismatched.metadataImage,
    '--matrix-input', mismatched.matrixInputPath, '--source-revision', revision,
    '--output', path.join(mismatched.root, 'candidate.json'),
  ], {
    spawn: fakeSpawn(mismatched),
    output: mismatchMessages,
    createCommittedSourceContext: () => ({ directory: path.dirname(mismatchProfiles), dispose() {} }),
  }), 1)
  assert.match(mismatchMessages.errors.join('\n'), /installer-manifest-sha256/)
})

test('CLI development override uses the current worktree only after binding Git HEAD', t => {
  const state = prepare(t)
  const outputPath = path.join(state.root, 'development-candidate.json')
  const messages = {
    logs: [], errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  const dockerSpawn = fakeSpawn(state)
  const spawn = (command, arguments_, options) => command === 'git'
    ? { status: 0, stdout: `${revision}\n`, stderr: '' }
    : dockerSpawn(command, arguments_, options)

  assert.equal(runCreateFrameworkCandidateInput([
    '--parent-image', state.parentImage,
    '--metadata-image', state.metadataImage,
    '--matrix-input', state.matrixInputPath,
    '--runtime-matrix', runtimeMatrixPath,
    '--source-revision', revision,
    '--output', outputPath,
    '--allow-uncommitted-source-for-development',
  ], { spawn, output: messages }), 0, messages.errors.join('\n'))
  assert.equal(JSON.parse(fs.readFileSync(outputPath, 'utf8')).sourceRevision, revision)

  messages.errors.length = 0
  const wrongHead = (command, arguments_, options) => command === 'git'
    ? { status: 0, stdout: `${'f'.repeat(40)}\n`, stderr: '' }
    : dockerSpawn(command, arguments_, options)
  assert.equal(runCreateFrameworkCandidateInput([
    '--parent-image', state.parentImage,
    '--metadata-image', state.metadataImage,
    '--matrix-input', state.matrixInputPath,
    '--source-revision', revision,
    '--output', path.join(state.root, 'wrong-head.json'),
    '--allow-uncommitted-source-for-development',
  ], { spawn: wrongHead, output: messages }), 1)
  assert.match(messages.errors.join('\n'), /must match Git HEAD/)
})
