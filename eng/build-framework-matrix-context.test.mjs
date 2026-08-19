import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  createContextBuildArguments,
  createContextDockerfile,
  matrixInputDigest,
  normalizeMatrixInput,
  validateContextInputs,
  validateOperatorImageInspection,
  runContextBuild,
} from './build-framework-matrix-context.mjs'

const rowDefinitions = [
  ['netfx20', '2.0', 'clr2'], ['netfx30', '3.0', 'clr2'],
  ['netfx35', '3.5', 'clr2'], ['netfx40', '4.0', 'clr4'],
  ['netfx45', '4.5', 'clr4'], ['netfx451', '4.5.1', 'clr4'],
  ['netfx452', '4.5.2', 'clr4'], ['netfx46', '4.6', 'clr4'],
  ['netfx461', '4.6.1', 'clr4'], ['netfx462', '4.6.2', 'clr4'],
  ['netfx47', '4.7', 'clr4'], ['netfx471', '4.7.1', 'clr4'],
  ['netfx472', '4.7.2', 'clr4'], ['netfx48', '4.8', 'clr4'],
]
const operatorBase = `registry.example/wine:development@sha256:${'b'.repeat(64)}`
const operatorRoot = `registry.example/root:stable@sha256:${'a'.repeat(64)}`

function matrix() {
  return {
    schemaVersion: 1,
    strategy: 'shared-framework-prefix-input-v1',
    rows: rowDefinitions.toReversed().map(([id, version, clrGeneration], index) => ({
      id, version, clrGeneration, targetPrefix: clrGeneration,
      companionVersions: {
        clr2: clrGeneration === 'clr2' ? version : '3.5',
        clr4: clrGeneration === 'clr4' ? version : '4.8',
      },
      operatorImage: `registry.example/operator-${id}@sha256:${String(index + 1).padStart(64, '0')}`,
    })),
  }
}

function labels(row) {
  return {
    'io.sharplabnext.operator-only': 'true',
    'io.sharplabnext.framework.target-id': row.id,
    'io.sharplabnext.framework.version': row.version,
    'io.sharplabnext.framework.clr-generation': row.clrGeneration,
    'io.sharplabnext.wine-prefix-layout': 'hardlink-immutable-v1',
    'io.sharplabnext.wine-prefix-layout-manifest': '/opt/sharplabnext/.wine-prefix-layout.json',
    'io.sharplabnext.operator-base': operatorBase,
    'io.sharplabnext.operator-root': operatorRoot,
  }
}

test('normalizes matrix rows deterministically and binds the exact target prefix', () => {
  const normalized = normalizeMatrixInput(matrix())
  assert.deepEqual(normalized.rows.map(row => row.id), rowDefinitions.map(row => row[0]))
  assert.equal(normalized.rows[0].targetPrefix, 'clr2')
  assert.match(matrixInputDigest(normalized), /^sha256:[0-9a-f]{64}$/)
  assert.throws(() => normalizeMatrixInput({ ...matrix(), rows: [matrix().rows[0], matrix().rows[0]] }), /duplicate|unsafe/)
  assert.throws(() => normalizeMatrixInput({ ...matrix(), rows: matrix().rows.map((row, index) => index === 0 ? { ...row, targetPrefix: row.clrGeneration === 'clr2' ? 'clr4' : 'clr2' } : row) }), /invalid version|generation|prefix/)
  assert.throws(() => normalizeMatrixInput({ ...matrix(), rows: matrix().rows.map((row, index) => index === 0 ? { ...row, operatorImage: 'registry.example/x"\nFROM scratch@sha256:' + 'c'.repeat(64) } : row) }), /invalid version|generation|prefix|operator image/)
})

test('generated Dockerfile contains bounded metadata and no operator prefix stage', () => {
  const document = normalizeMatrixInput(matrix())
  const digest = matrixInputDigest(document)
  const source = createContextDockerfile(document, digest, 'a'.repeat(40), 'development')
  assert.match(source, /FROM scratch AS final/)
  assert.match(source, /COPY rows\/netfx20\/row\.json \/rows\/netfx20\/row\.json/)
  assert.match(source, /COPY rows\/netfx48\/row\.json \/rows\/netfx48\/row\.json/)
  assert.doesNotMatch(source, /FROM registry\.example|COPY --from|wine-netfx-clr[24]|wine-prefixes|--mount/)
  assert.match(source, /io\.sharplabnext\.framework\.matrix-content="metadata-only-v1"/)
  assert.match(source, new RegExp(`io.sharplabnext.framework.matrix-input-sha256="${digest}"`))
})

test('operator image identity requires the expected labels, platform, and digest', () => {
  const row = normalizeMatrixInput(matrix()).rows.find(candidate => candidate.id === 'netfx48')
  const valid = {
    Id: row.operatorImage.slice(row.operatorImage.lastIndexOf('@') + 1),
    Size: 100,
    Os: 'linux', Architecture: 'amd64',
    Config: { Labels: labels(row) },
  }
  assert.deepEqual(validateOperatorImageInspection(row, valid, {
    baseImage: `registry.example/wine@sha256:${'b'.repeat(64)}`,
    rootImage: `registry.example/root@sha256:${'a'.repeat(64)}`,
  }), [])
  assert.match(validateOperatorImageInspection(row, { ...valid, Os: 'windows' }).join('\n'), /linux\/amd64/)
  assert.match(validateOperatorImageInspection(row, { ...valid, Id: `sha256:${'f'.repeat(64)}` }).join('\n'), /supplied digest/)
  assert.match(validateOperatorImageInspection(row, { ...valid, Config: { Labels: { ...labels(row), 'io.sharplabnext.framework.version': '4.7' } } }).join('\n'), /framework.version/)
  assert.match(validateOperatorImageInspection(row, {
    ...valid,
    Config: { Labels: { ...labels(row), 'io.sharplabnext.operator-root': undefined } },
  }).join('\n'), /operator-root.*digest-pinned/)
  assert.match(validateOperatorImageInspection(row, valid, {
    baseImage: `registry.example/other-wine@sha256:${'b'.repeat(64)}`,
    rootImage: `registry.example/root@sha256:${'a'.repeat(64)}`,
  }).join('\n'), /Wine\/base identity.*must equal/)
})

test('context build arguments keep BuildKit on linux/amd64 and never mount a host prefix', () => {
  const args = createContextBuildArguments({ IMAGE: 'localhost:5000/sharplabnext/framework-context:dev', push: true }, 'C:\\metadata-only-context', 'C:\\metadata-only-context\\Dockerfile', 'C:\\metadata\\build.json')
  assert.deepEqual(args.slice(0, 6), ['buildx', 'build', '--platform', 'linux/amd64', '--file', 'C:\\metadata-only-context\\Dockerfile'])
  assert.ok(args.includes('--push'))
  assert.ok(args.includes('--provenance=false'))
  assert.equal(args.at(-1), 'C:\\metadata-only-context')
})

test('push validation rejects local-only operator references and mutable source identity', () => {
  const document = normalizeMatrixInput(matrix())
  const failures = validateContextInputs({
    MATRIX_INPUT_SHA256: matrixInputDigest(document),
    SOURCE_REVISION: 'development',
    IMAGE: 'localhost:5000/sharplabnext/framework-context:release',
    VERSION: 'release', push: true,
  }, document)
  assert.match(failures.join('\n'), /committed SOURCE_REVISION/)
  const localDocument = normalizeMatrixInput({
    ...matrix(), rows: matrix().rows.map(row => ({ ...row, operatorImage: row.operatorImage.replace('registry.example/', 'sharplabnext/') })),
  })
  const localFailures = validateContextInputs({
    MATRIX_INPUT_SHA256: matrixInputDigest(localDocument), SOURCE_REVISION: 'a'.repeat(40),
    IMAGE: 'localhost:5000/sharplabnext/framework-context:release', VERSION: 'release', push: true,
  }, localDocument)
  assert.match(localFailures.join('\n'), /registry-hosted operator/)
})

test('development build emits only the mocked metadata boundary', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-context-test-'))
  const input = path.join(root, 'matrix-input.json')
  const document = normalizeMatrixInput(matrix())
  fs.writeFileSync(input, JSON.stringify(document), 'utf8')
  const rowInfo = new Map(document.rows.map(row => [row.operatorImage, {
    Id: row.operatorImage.slice(row.operatorImage.lastIndexOf('@') + 1),
    Size: 100, Os: 'linux', Architecture: 'amd64', Config: { Labels: labels(row) },
  }]))
  let buildRoot
  const calls = []
  const output = { logs: [], errors: [], log(value) { this.logs.push(value) }, error(value) { this.errors.push(value) } }
  const spawn = (command, args) => {
    calls.push([command, args])
    if (command === 'git' && args[0] === 'rev-parse') return { status: 0, stdout: `${'d'.repeat(40)}\n` }
    if (command === 'git' && args[0] === 'status') return { status: 0, stdout: '' }
    if (command === 'docker' && args[0] === 'image' && args[1] === 'inspect') {
      if (rowInfo.has(args[2])) return { status: 0, stdout: JSON.stringify([rowInfo.get(args[2])]) }
      return { status: 0, stdout: JSON.stringify([{
        Id: `sha256:${'e'.repeat(64)}`, Size: 1024, Os: 'linux', Architecture: 'amd64',
        Config: { Labels: {
          'io.sharplabnext.framework.matrix-context': 'true',
          'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
          'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
          'io.sharplabnext.framework.matrix-input-sha256': matrixInputDigest(document),
          'io.sharplabnext.framework.matrix-row-count': '14',
          'org.opencontainers.image.revision': 'development',
          'org.opencontainers.image.version': 'development',
        } },
      }]) }
    }
    if (command === 'docker' && args[0] === 'buildx') {
      buildRoot = args.at(-1)
      assert.equal(fs.existsSync(path.join(buildRoot, 'matrix-input.json')), true)
      assert.equal(fs.existsSync(path.join(buildRoot, 'rows', 'netfx20', 'row.json')), true)
      assert.equal(fs.existsSync(path.join(buildRoot, 'rows', 'netfx48', 'row.json')), true)
      return { status: 0, stdout: '' }
    }
    if (command === 'docker' && args[0] === 'create') return { status: 0, stdout: `${'f'.repeat(64)}\n` }
    if (command === 'docker' && args[0] === 'cp') {
      const source = args[1].slice(args[1].indexOf(':/') + 2)
      const destination = args[2]
      fs.copyFileSync(path.join(buildRoot, source), destination)
      return { status: 0, stdout: '' }
    }
    if (command === 'docker' && args[0] === 'rm') return { status: 0, stdout: '' }
    throw new Error(`unexpected mocked command: ${command} ${args.join(' ')}`)
  }
  try {
    const status = runContextBuild([
      '--matrix-input', input,
      '--source-revision', 'development',
      '--image', 'sharplabnext/framework-context:development',
      '--allow-uncommitted-source-for-development',
    ], {}, spawn, output)
    assert.equal(status, 0, output.errors.join('\n'))
    assert.deepEqual(output.errors, [])
    assert.match(output.logs.at(-1), /matrixInputSha256/)
    assert.match(output.logs.at(-1), /"promotionEligible": false/)
    assert.equal(calls.some(([, args]) => args[0] === 'buildx' && args.includes('--load')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})
