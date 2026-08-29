import assert from 'node:assert/strict'
import childProcess from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const assembler = path.join(repositoryRoot, 'deploy', 'docker', 'assemble-framework-prefix-matrix.py')
const dedupe = path.join(repositoryRoot, 'deploy', 'docker', 'dedupe-wine-prefixes.py')
const parentDockerfile = path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.operator-wine-framework-matrix-parent')
const candidateDockerfile = path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.runtime-wine-framework-matrix-shared')
const testParentImage = `registry.example/framework-parent@sha256:${'b'.repeat(64)}`

function pythonCommand() {
  for (const command of ['python3', 'python']) {
    const result = childProcess.spawnSync(command, ['--version'], { encoding: 'utf8' })
    if (result.status === 0) return command
  }
  return undefined
}

function run(python, args) {
  return childProcess.spawnSync(python, [assembler, ...args], {
    encoding: 'utf8',
    cwd: repositoryRoot,
  })
}

function prefix(root, sharedText, uniqueText) {
  const framework = path.join(root, 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319')
  const gac = path.join(root, 'drive_c', 'windows', 'assembly', 'GAC')
  const system32 = path.join(root, 'drive_c', 'windows', 'system32')
  const config = path.join(system32, 'config')
  const drivers = path.join(system32, 'drivers')
  const winsxs = path.join(root, 'drive_c', 'windows', 'winsxs', 'component')
  const winsxsTemp = path.join(root, 'drive_c', 'windows', 'winsxs', 'Temp')
  const resources = path.join(root, 'drive_c', 'windows', 'resources', 'theme')
  const references = path.join(root, 'drive_c', 'Program Files', 'Reference Assemblies', 'Example')
  const cache = path.join(root, 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'cache')
  const nativeImages = path.join(root, 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'NativeImages_v4.0.30319_64')
  fs.mkdirSync(framework, { recursive: true })
  fs.mkdirSync(gac, { recursive: true })
  fs.mkdirSync(config, { recursive: true })
  for (const directory of [drivers, winsxs, winsxsTemp, resources, references]) {
    fs.mkdirSync(directory, { recursive: true })
  }
  fs.mkdirSync(cache, { recursive: true })
  fs.mkdirSync(nativeImages, { recursive: true })
  fs.writeFileSync(path.join(framework, 'mscorlib.dll'), sharedText)
  fs.writeFileSync(path.join(gac, 'shared.dll'), sharedText)
  fs.writeFileSync(path.join(system32, 'kernel32.dll'), `static-${sharedText}`)
  fs.writeFileSync(path.join(config, 'state.dll'), `state-${sharedText}`)
  fs.writeFileSync(path.join(system32, 'notes.txt'), `notes-${sharedText}`)
  fs.writeFileSync(path.join(drivers, 'driver.sys'), `driver-${sharedText}`)
  fs.writeFileSync(path.join(drivers, 'helper.dll'), `driver-helper-${sharedText}`)
  fs.writeFileSync(path.join(winsxs, 'payload.bin'), `winsxs-${sharedText}`)
  fs.writeFileSync(path.join(winsxsTemp, 'state.bin'), `winsxs-temp-${sharedText}`)
  fs.writeFileSync(path.join(resources, 'style.msstyles'), `resource-${sharedText}`)
  fs.writeFileSync(path.join(references, 'Reference.dll'), `reference-${sharedText}`)
  fs.writeFileSync(path.join(framework, 'unique.dll'), uniqueText)
  fs.writeFileSync(path.join(cache, 'mutable.dll'), sharedText)
  fs.writeFileSync(path.join(nativeImages, 'mutable.dll'), sharedText)
  fs.writeFileSync(path.join(root, 'system.reg'), 'WINE REGISTRY Version 2\n#arch=win64\n')
  fs.writeFileSync(path.join(root, 'user.reg'), 'row-local\n')
}

function makeRow(input, id, version, sharedText, clrGeneration = 'clr4') {
  const row = path.join(input, 'rows', id)
  prefix(path.join(row, 'clr2'), sharedText, `${id}-clr2`)
  prefix(path.join(row, 'clr4'), sharedText, `${id}-clr4`)
  fs.writeFileSync(path.join(row, 'row.json'), JSON.stringify({
    schemaVersion: 1,
    id,
    version,
    clrGeneration,
    targetPrefix: clrGeneration,
    companionVersions: {
      clr2: clrGeneration === 'clr2' ? version : '3.5',
      clr4: clrGeneration === 'clr4' ? version : '4.8',
    },
    operatorImage: `registry.example/${id}@sha256:${'a'.repeat(64)}`,
  }))
}

function writeInputManifest(input, rows) {
  fs.writeFileSync(path.join(input, 'matrix-input.json'), JSON.stringify({
    schemaVersion: 1,
    strategy: 'shared-framework-prefix-input-v1',
    rows: rows.map(row => ({
      ...row,
      operatorImage: `registry.example/${row.id}@sha256:${'a'.repeat(64)}`,
    })),
  }))
}

function assembleTwoRowMatrix(python, root) {
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
  makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
  writeInputManifest(input, [
    { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
    { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
  ])
  const assembled = run(python, [
    'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
  ])
  assert.equal(assembled.status, 0, assembled.stderr)
  return {
    input,
    output,
    manifest: JSON.parse(fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8')),
  }
}

function selectTwoRowMatrix(python, root, output, manifest, parentImage = testParentImage) {
  const selectedRow = manifest.rows.find(row => row.id === 'netfx47')
  return childProcess.spawnSync(python, [assembler, 'select',
    '--root', path.join(output, 'framework-prefixes'),
    '--target-id', 'netfx47',
    '--canonical-prefix', path.join(root, 'wine-netfx-clr4'),
    '--receipt', path.join(root, 'selector.json'),
    '--dedupe-helper', dedupe,
    '--expected-input-manifest-sha256', manifest.inputManifestSha256,
    '--expected-parent-image', parentImage,
    '--expected-operator-image', selectedRow.operatorImage,
    '--expected-row-digest', `sha256:${selectedRow.rowDigest}`,
  ], { encoding: 'utf8', cwd: repositoryRoot })
}

test('shared Framework matrix assembler deduplicates static files and preserves mutable row state', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-matrix-'))
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    const result = run(python, [
      'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
    ])
    assert.equal(result.status, 0, result.stderr)
    const manifest = JSON.parse(fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8'))
    assert.equal(manifest.strategy, 'shared-framework-target-prefix-matrix-v1')
    assert.deepEqual(manifest.rows.map(row => row.id), ['netfx451', 'netfx47'])
    assert.equal(manifest.layout.strategy, 'hardlink-static-runtime-matrix-v1')
    assert.equal(manifest.dedupePolicy.id, 'wine-static-runtime-payload-v1')
    assert.deepEqual(manifest.layout.policy, manifest.dedupePolicy)
    assert.ok(manifest.layout.linkedFileCount >= 3)

    const sharedA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4', 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'mscorlib.dll')
    const sharedB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'mscorlib.dll')
    assert.equal(fs.statSync(sharedA).ino, fs.statSync(sharedB).ino)
    assert.equal(fs.statSync(sharedA).mode & 0o222, 0)
    const staticA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4', 'drive_c', 'windows', 'system32', 'kernel32.dll')
    const staticB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'drive_c', 'windows', 'system32', 'kernel32.dll')
    assert.equal(fs.statSync(staticA).ino, fs.statSync(staticB).ino)
    const protectedA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4', 'drive_c', 'windows', 'system32', 'config', 'state.dll')
    const protectedB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'drive_c', 'windows', 'system32', 'config', 'state.dll')
    assert.notEqual(fs.statSync(protectedA).ino, fs.statSync(protectedB).ino)
    const rowA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4')
    const rowB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4')
    for (const relative of [
      'drive_c/windows/system32/drivers/driver.sys',
      'drive_c/windows/winsxs/component/payload.bin',
      'drive_c/windows/resources/theme/style.msstyles',
      'drive_c/Program Files/Reference Assemblies/Example/Reference.dll',
    ]) {
      assert.equal(fs.statSync(path.join(rowA, relative)).ino, fs.statSync(path.join(rowB, relative)).ino)
    }
    for (const relative of [
      'drive_c/windows/system32/notes.txt',
      'drive_c/windows/system32/drivers/helper.dll',
      'drive_c/windows/winsxs/Temp/state.bin',
    ]) {
      assert.notEqual(fs.statSync(path.join(rowA, relative)).ino, fs.statSync(path.join(rowB, relative)).ino)
    }
    const mutableA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4', 'system.reg')
    const mutableB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'system.reg')
    assert.notEqual(fs.statSync(mutableA).ino, fs.statSync(mutableB).ino)
    const cacheA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4', 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'cache', 'mutable.dll')
    const cacheB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'cache', 'mutable.dll')
    assert.notEqual(fs.statSync(cacheA).ino, fs.statSync(cacheB).ino)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451', 'clr2')), false)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix assembler accepts metadata plus read-only mounted prefix roots', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-mounted-'))
  const raw = path.join(root, 'raw')
  const metadata = path.join(root, 'metadata')
  const mounted = path.join(root, 'mounted')
  const output = path.join(root, 'output')
  try {
    makeRow(raw, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(raw, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(raw, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    fs.mkdirSync(path.join(metadata, 'rows'), { recursive: true })
    fs.copyFileSync(path.join(raw, 'matrix-input.json'), path.join(metadata, 'matrix-input.json'))
    for (const row of ['netfx451', 'netfx47']) {
      fs.mkdirSync(path.join(metadata, 'rows', row), { recursive: true })
      fs.copyFileSync(
        path.join(raw, 'rows', row, 'row.json'),
        path.join(metadata, 'rows', row, 'row.json'),
      )
      fs.mkdirSync(path.join(mounted, row), { recursive: true })
      fs.cpSync(
        path.join(raw, 'rows', row, 'clr4'),
        path.join(mounted, row, 'clr4'),
        { recursive: true, errorOnExist: true },
      )
    }
    const result = run(python, [
      'assemble', '--input', metadata,
      '--row-prefix-root', mounted,
      '--output', output,
      '--dedupe-helper', dedupe,
    ])
    assert.equal(result.status, 0, result.stderr)
    const manifest = JSON.parse(fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8'))
    assert.deepEqual(manifest.rows.map(row => row.id), ['netfx451', 'netfx47'])
    assert.ok(manifest.layout.linkedFileCount >= 3)
    assert.equal(fs.existsSync(path.join(metadata, 'rows', 'netfx451', 'clr2')), false)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451', 'clr2')), false)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix stores only each row target generation', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-target-only-'))
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  try {
    makeRow(input, 'netfx35', '3.5', 'same-runtime-payload', 'clr2')
    makeRow(input, 'netfx47', '4.7', 'same-runtime-payload')
    writeInputManifest(input, [
      { id: 'netfx35', version: '3.5', clrGeneration: 'clr2', targetPrefix: 'clr2', companionVersions: { clr2: '3.5', clr4: '4.8' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    const result = run(python, [
      'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
    ])
    assert.equal(result.status, 0, result.stderr)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx35', 'clr2')), true)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx35', 'clr4')), false)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx47', 'clr4')), true)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx47', 'clr2')), false)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects a mismatched canonical generation before mutation', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-canonical-'))
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-runtime-payload')
    makeRow(input, 'netfx47', '4.7', 'same-runtime-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    assert.equal(run(python, [
      'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
    ]).status, 0)
    const manifest = JSON.parse(fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8'))
    const selectedRow = manifest.rows.find(row => row.id === 'netfx47')
    const result = childProcess.spawnSync(python, [assembler, 'select',
      '--root', path.join(output, 'framework-prefixes'),
      '--target-id', 'netfx47',
      '--canonical-prefix', path.join(root, 'wine-netfx-clr2'),
      '--receipt', path.join(root, 'selector.json'),
      '--dedupe-helper', dedupe,
      '--expected-input-manifest-sha256', manifest.inputManifestSha256,
      '--expected-parent-image', testParentImage,
      '--expected-operator-image', selectedRow.operatorImage,
      '--expected-row-digest', `sha256:${selectedRow.rowDigest}`,
    ], { encoding: 'utf8', cwd: repositoryRoot })
    assert.equal(result.status, 1)
    assert.match(result.stderr, /does not match the selected CLR generation/)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx47')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector whiteouts unselected rows and emits a receipt', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-selector-'))
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    assert.equal(run(python, [
      'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
    ]).status, 0)
    const matrixRoot = path.join(output, 'framework-prefixes')
    const matrixInputSha256 = JSON.parse(
      fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8'),
    ).inputManifestSha256
    const selectedRow = JSON.parse(
      fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8'),
    ).rows.find(row => row.id === 'netfx47')
    const receipt = path.join(output, 'selector.json')
    const selected = childProcess.spawnSync(python, [assembler, 'select',
      '--root', matrixRoot,
      '--target-id', 'netfx47',
      '--canonical-prefix', path.join(root, 'wine-netfx-clr4'),
      '--receipt', receipt,
      '--dedupe-helper', dedupe,
      '--expected-input-manifest-sha256', matrixInputSha256,
      '--expected-parent-image', testParentImage,
      '--expected-operator-image', selectedRow.operatorImage,
      '--expected-row-digest', `sha256:${selectedRow.rowDigest}`,
    ], { encoding: 'utf8', cwd: repositoryRoot })
    assert.equal(selected.status, 0, selected.stderr)
    assert.equal(fs.existsSync(path.join(matrixRoot, 'netfx451')), false)
    assert.deepEqual(fs.readdirSync(matrixRoot), ['netfx47'])
    assert.deepEqual(fs.readdirSync(path.join(matrixRoot, 'netfx47')), ['clr4'])
    assert.equal(fs.realpathSync(path.join(root, 'wine-netfx-clr4')), fs.realpathSync(path.join(matrixRoot, 'netfx47', 'clr4')))
    const selector = JSON.parse(fs.readFileSync(receipt, 'utf8'))
    assert.equal(selector.strategy, 'shared-framework-target-prefix-selector-v1')
    assert.equal(selector.targetId, 'netfx47')
    assert.equal(selector.parentImage, testParentImage)
    assert.equal(
      selector.parentManifestSha256,
      crypto.createHash('sha256').update(fs.readFileSync(path.join(output, 'framework-matrix.json'))).digest('hex'),
    )
    assert.equal(
      selector.layoutManifestSha256,
      crypto.createHash('sha256').update(fs.readFileSync(path.join(output, '.wine-prefix-layout.json'))).digest('hex'),
    )
    assert.equal(selector.targetPrefix, 'clr4')
    assert.equal(selector.canonicalPrefix, path.join(root, 'wine-netfx-clr4'))
    assert.deepEqual(selector.hiddenRows, ['netfx451'])
    assert.equal(selector.whiteoutMode, 'directory')
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects row content drift before mutation', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-selector-drift-'))
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    assert.equal(run(python, [
      'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
    ]).status, 0)
    const drifted = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'system.reg')
    fs.appendFileSync(drifted, 'drift\n')
    const matrixRoot = path.join(output, 'framework-prefixes')
    const matrixInputSha256 = JSON.parse(
      fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8'),
    ).inputManifestSha256
    const selectedRow = JSON.parse(
      fs.readFileSync(path.join(output, 'framework-matrix.json'), 'utf8'),
    ).rows.find(row => row.id === 'netfx47')
    const selected = childProcess.spawnSync(python, [assembler, 'select',
      '--root', matrixRoot,
      '--target-id', 'netfx47',
      '--canonical-prefix', path.join(root, 'wine-netfx-clr4'),
      '--receipt', path.join(root, 'selector.json'),
      '--dedupe-helper', dedupe,
      '--expected-input-manifest-sha256', matrixInputSha256,
      '--expected-parent-image', testParentImage,
      '--expected-operator-image', selectedRow.operatorImage,
      '--expected-row-digest', `sha256:${selectedRow.rowDigest}`,
    ], { encoding: 'utf8', cwd: repositoryRoot })
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /content does not match its recorded digest/)
    assert.equal(fs.existsSync(path.join(matrixRoot, 'netfx451')), true)
    assert.equal(fs.existsSync(path.join(matrixRoot, 'netfx47')), true)
    assert.equal(fs.existsSync(path.join(root, 'wine-netfx-clr4')), false)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects manifest identity and layout drift', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-selector-manifest-'))
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    assert.equal(run(python, [
      'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
    ]).status, 0)
    const manifestPath = path.join(output, 'framework-matrix.json')
    const original = JSON.parse(fs.readFileSync(manifestPath, 'utf8'))
    const selectedRow = original.rows.find(row => row.id === 'netfx47')
    fs.writeFileSync(manifestPath, JSON.stringify({
      ...original,
      inputManifestSha256: 'sha256:' + '0'.repeat(64),
    }))
    const invoke = () => childProcess.spawnSync(python, [assembler, 'select',
      '--root', path.join(output, 'framework-prefixes'),
      '--target-id', 'netfx47',
      '--canonical-prefix', path.join(root, 'wine-netfx-clr4'),
      '--receipt', path.join(root, 'selector.json'),
      '--dedupe-helper', dedupe,
      '--expected-input-manifest-sha256', original.inputManifestSha256,
      '--expected-parent-image', testParentImage,
      '--expected-operator-image', selectedRow.operatorImage,
      '--expected-row-digest', `sha256:${selectedRow.rowDigest}`,
    ], { encoding: 'utf8', cwd: repositoryRoot })
    let selected = invoke()
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /inputManifestSha256|input manifest/i)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)

    // Restore the exact assembled manifest, then drift only the summary so
    // layout verification must reject it.
    fs.writeFileSync(manifestPath, JSON.stringify({
      ...original,
      layout: { ...original.layout, linkedBytes: original.layout.linkedBytes + 1 },
    }))
    selected = invoke()
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /layout summary|linked byte/i)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx47')), true)

    fs.writeFileSync(manifestPath, JSON.stringify(original))
    const layoutPath = path.join(output, '.wine-prefix-layout.json')
    const originalLayout = JSON.parse(fs.readFileSync(layoutPath, 'utf8'))
    fs.writeFileSync(layoutPath, JSON.stringify({
      ...originalLayout,
      policy: { ...originalLayout.policy, sha256: 'sha256:' + '0'.repeat(64) },
    }))
    selected = invoke()
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /static runtime policy/i)

    const protectedTarget = 'drive_c/windows/system32/config/state.dll'
    fs.writeFileSync(layoutPath, JSON.stringify({
      ...originalLayout,
      links: originalLayout.links.map((link, index) => (
        index === 0 ? { ...link, target: protectedTarget } : link
      )),
    }))
    selected = invoke()
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /escapes its declared policy/i)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects extra rows and empty companion prefixes before mutation', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-shape-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const matrixRoot = path.join(output, 'framework-prefixes')
    fs.mkdirSync(path.join(matrixRoot, 'unexpected-row'))
    let selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /root rows do not exactly match its manifest/)
    assert.equal(fs.existsSync(path.join(matrixRoot, 'netfx451')), true)

    fs.rmSync(path.join(matrixRoot, 'unexpected-row'), { recursive: true })
    fs.mkdirSync(path.join(matrixRoot, 'netfx47', 'clr2'))
    selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /must contain only clr4/)
    assert.equal(fs.existsSync(path.join(matrixRoot, 'netfx451')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects undeclared mutable cross-row hard links', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-mutable-link-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const rowA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4', 'system.reg')
    const rowB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'system.reg')
    fs.unlinkSync(rowB)
    fs.linkSync(rowA, rowB)
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /undeclared cross-prefix hard link/)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects undeclared cross-row hard-linked symlinks', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-symlink-inode-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const rowA = path.join(output, 'framework-prefixes', 'netfx451', 'clr4', 'row-state-link')
    const rowB = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'row-state-link')
    fs.symlinkSync('system.reg', rowA)
    fs.linkSync(rowA, rowB)
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /undeclared cross-prefix hard link/)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector requires a digest-pinned physical parent identity', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-parent-identity-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const selected = selectTwoRowMatrix(
      python,
      root,
      output,
      manifest,
      'registry.example/framework-parent:mutable',
    )
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /expected parent image must be repository@sha256/)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx47')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects an old dedupe strategy before mutation', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-old-strategy-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const layoutPath = path.join(output, '.wine-prefix-layout.json')
    const layout = JSON.parse(fs.readFileSync(layoutPath, 'utf8'))
    fs.writeFileSync(layoutPath, JSON.stringify({ ...layout, strategy: 'hardlink-immutable-matrix-v1' }))
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /schema or strategy is unsupported/)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)

    fs.writeFileSync(layoutPath, JSON.stringify(layout))
    const manifestPath = path.join(output, 'framework-matrix.json')
    fs.writeFileSync(manifestPath, JSON.stringify({
      ...manifest,
      treeFingerprintPolicy: {
        ...manifest.treeFingerprintPolicy,
        id: 'regular-files-only-v0',
      },
    }))
    const legacyFingerprint = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(legacyFingerprint.status, 1)
    assert.match(legacyFingerprint.stderr, /tree fingerprint policy is invalid/)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx47')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix selector rejects intermediate symlinks in manifest paths', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-path-link-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const frameworkRoot = path.join(
      output, 'framework-prefixes', 'netfx47', 'clr4',
      'drive_c', 'windows', 'Microsoft.NET',
    )
    fs.renameSync(path.join(frameworkRoot, 'Framework64'), path.join(frameworkRoot, 'Framework64-real'))
    fs.symlinkSync('Framework64-real', path.join(frameworkRoot, 'Framework64'), 'dir')
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /symlinked component/)
    assert.equal(fs.existsSync(path.join(output, 'framework-prefixes', 'netfx451')), true)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework row fingerprint rejects directory mode drift', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-directory-mode-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const directory = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'drive_c')
    const currentMode = fs.statSync(directory).mode & 0o7777
    fs.chmodSync(directory, currentMode === 0o700 ? 0o755 : 0o700)
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /content does not match its recorded digest/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework row fingerprint rejects owner drift', {
  skip: pythonCommand() === undefined || typeof process.getuid !== 'function' || process.getuid() !== 0,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-owner-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    fs.chownSync(path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'user.reg'), 1234, 1234)
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /content does not match its recorded digest/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework row fingerprint rejects special nodes', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-special-node-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const fifo = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'unexpected.fifo')
    const created = childProcess.spawnSync(python, ['-c', 'import os,sys; os.mkfifo(sys.argv[1])', fifo], { encoding: 'utf8' })
    assert.equal(created.status, 0, created.stderr)
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /unsupported special node/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework row fingerprint rejects non-empty extended attributes when supported', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, t => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-xattr-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const target = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'user.reg')
    const changed = childProcess.spawnSync(
      python,
      ['-c', 'import os,sys; os.setxattr(sys.argv[1], b"user.sharplabnext-test", b"1")', target],
      { encoding: 'utf8' },
    )
    if (changed.status !== 0) {
      t.skip('test filesystem does not support user xattrs')
      return
    }
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /unsupported extended attributes/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix accepts and normalizes DOSATTRIB before output identity', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, t => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-dosattrib-'))
  const input = path.join(root, 'input')
  const output = path.join(root, 'output')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    const relative = path.join('clr4', 'drive_c', 'windows', 'system32', 'kernel32.dll')
    for (const [row, value] of [['netfx451', '0x2'], ['netfx47', '0x4']]) {
      const target = path.join(input, 'rows', row, relative)
      const changed = childProcess.spawnSync(
        python,
        ['-c', 'import os,sys; os.setxattr(sys.argv[1], b"user.DOSATTRIB", sys.argv[2].encode())', target, value],
        { encoding: 'utf8' },
      )
      if (changed.status !== 0) {
        t.skip('test filesystem does not support user.DOSATTRIB xattrs')
        return
      }
    }
    const assembled = run(python, [
      'assemble', '--input', input, '--output', output, '--dedupe-helper', dedupe,
    ])
    assert.equal(assembled.status, 0, assembled.stderr)
    const layout = JSON.parse(fs.readFileSync(path.join(output, '.wine-prefix-layout.json'), 'utf8'))
    const link = layout.links.find(entry => entry.target.endsWith('drive_c/windows/system32/kernel32.dll'))
    assert.ok(link)
    assert.deepEqual(link.xattrs, [])
    const first = path.join(output, 'framework-prefixes', 'netfx451', relative)
    const second = path.join(output, 'framework-prefixes', 'netfx47', relative)
    assert.equal(fs.statSync(first).ino, fs.statSync(second).ino)
    const attributes = childProcess.spawnSync(
      python,
      ['-c', 'import os,sys; print(os.listxattr(sys.argv[1], follow_symlinks=False))', second],
      { encoding: 'utf8' },
    )
    assert.equal(attributes.status, 0, attributes.stderr)
    assert.equal(attributes.stdout.trim(), '[]')
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework selector rejects a valid DOSATTRIB introduced after assembly', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, t => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-dosattrib-drift-'))
  try {
    const { output, manifest } = assembleTwoRowMatrix(python, root)
    const target = path.join(output, 'framework-prefixes', 'netfx47', 'clr4', 'user.reg')
    const changed = childProcess.spawnSync(
      python,
      ['-c', 'import os,sys; os.setxattr(sys.argv[1], b"user.DOSATTRIB", b"0x2")', target],
      { encoding: 'utf8' },
    )
    if (changed.status !== 0) {
      t.skip('test filesystem does not support user.DOSATTRIB xattrs')
      return
    }
    const selected = selectTwoRowMatrix(python, root, output, manifest)
    assert.equal(selected.status, 1)
    assert.match(selected.stderr, /content does not match its recorded digest/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix rejects malformed DOSATTRIB values', {
  skip: pythonCommand() === undefined || process.platform === 'win32',
}, t => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-dosattrib-invalid-'))
  const input = path.join(root, 'input')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    const target = path.join(input, 'rows', 'netfx47', 'clr4', 'drive_c', 'windows', 'system32', 'kernel32.dll')
    const changed = childProcess.spawnSync(
      python,
      ['-c', 'import os,sys; os.setxattr(sys.argv[1], b"user.DOSATTRIB", b"not-a-mask")', target],
      { encoding: 'utf8' },
    )
    if (changed.status !== 0) {
      t.skip('test filesystem does not support user.DOSATTRIB xattrs')
      return
    }
    const assembled = run(python, [
      'assemble', '--input', input, '--output', path.join(root, 'output'), '--dedupe-helper', dedupe,
    ])
    assert.equal(assembled.status, 1)
    assert.match(assembled.stderr, /invalid value/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework matrix assembler rejects a row whose registry is not x64', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-invalid-'))
  const input = path.join(root, 'input')
  try {
    makeRow(input, 'netfx451', '4.5.1', 'same-framework-payload')
    makeRow(input, 'netfx47', '4.7', 'same-framework-payload')
    writeInputManifest(input, [
      { id: 'netfx451', version: '4.5.1', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.5.1' } },
      { id: 'netfx47', version: '4.7', clrGeneration: 'clr4', targetPrefix: 'clr4', companionVersions: { clr2: '3.5', clr4: '4.7' } },
    ])
    const registry = path.join(input, 'rows', 'netfx47', 'clr4', 'system.reg')
    fs.writeFileSync(registry, 'WINE REGISTRY Version 2\n#arch=win32\n')
    const result = run(python, [
      'assemble', '--input', input, '--output', path.join(root, 'output'), '--dedupe-helper', dedupe,
    ])
    assert.notEqual(result.status, 0)
    assert.match(result.stderr, /#arch=win64|win64/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('shared Framework Dockerfiles keep the parent layer and selector contracts explicit', () => {
  const assemblerSource = fs.readFileSync(assembler, 'utf8')
  const selectorSource = assemblerSource.slice(assemblerSource.indexOf('def select('))
  const parent = fs.readFileSync(parentDockerfile, 'utf8')
  const candidate = fs.readFileSync(candidateDockerfile, 'utf8')
  assert.match(parent, /FROM \$\{ROOT_IMAGE\} AS final/)
  assert.match(parent, /from=framework-matrix-metadata/)
  assert.match(parent, /SHARPLABNEXT_FRAMEWORK_ROW_MOUNTS/)
  assert.match(parent, /--row-prefix-root \/run\/sharplabnext-framework-rows/)
  assert.match(parent, /--output \/opt\/sharplabnext/)
  assert.match(parent, /assemble-framework-prefix-matrix verify/)
  assert.match(parent, /test ! -e \/run\/sharplabnext-framework-matrix-metadata/)
  assert.match(parent, /test ! -e \/run\/sharplabnext-framework-rows/)
  assert.doesNotMatch(parent, /FROM \$\{ROOT_IMAGE\} AS assembler/)
  assert.doesNotMatch(parent, /COPY --from=framework-matrix|COPY --from=assembler/)
  assert.doesNotMatch(parent, /matrix_output=|cp -a "\$\{matrix_output\}"/)
  assert.match(parent, /shared-framework-target-prefix-matrix-v1/)
  assert.match(parent, /framework-matrix\.json/)
  assert.match(parent, /--preflight-command/)
  assert.match(parent, /hardlink-static-runtime-matrix-v1/)
  assert.match(parent, /wine-static-runtime-payload-v1/)
  assert.match(parent, /\.operator-wine-image/)
  assert.match(parent, /\.framework-matrix-input-sha256/)
  assert.match(parent, /\.framework-matrix-source-uri/)
  assert.match(assemblerSource, /MatrixManifestBuilder\(freeze=True\)/)
  assert.match(assemblerSource, /layout_builder\.add_prefix/)
  assert.doesNotMatch(parent, /COPY --from=wine-source \/opt\/wine-dotnet/)
  assert.match(candidate, /FROM \$\{PARENT_IMAGE\} AS matrix-parent/)
  assert.match(candidate, /assemble-framework-prefix-matrix select/s)
  assert.match(candidate, /--canonical-prefix "\$\{canonical_prefix\}"/)
  assert.match(candidate, /--expected-input-manifest-sha256 "\$\{FRAMEWORK_MATRIX_INPUT_SHA256\}"/)
  assert.match(candidate, /--expected-parent-image "\$\{PARENT_IMAGE\}"/)
  assert.match(candidate, /--expected-operator-image "\$\{FRAMEWORK_ROW_OPERATOR_IMAGE\}"/)
  assert.match(candidate, /--expected-row-digest "\$\{FRAMEWORK_ROW_DIGEST\}"/)
  assert.match(candidate, /operator_digest="\$\{FRAMEWORK_ROW_OPERATOR_IMAGE##\*@\}"/)
  assert.match(candidate, /RUNTIME_COMPONENT_DIGEST.*operator_digest/s)
  assert.match(candidate, /\.framework-selector\.json/)
  assert.match(candidate, /ARG WINE_IMAGE/)
  assert.match(candidate, /SHARPLABNEXT_CAPTURE_DIRECTORY="Z:\\\\tmp"/)
  assert.match(candidate, /\.operator-wine-image/)
  assert.match(candidate, /\.framework-matrix-input-sha256/)
  assert.match(candidate, /\.framework-matrix-source-uri/)
  assert.match(candidate, /test "\$\(cat \/opt\/sharplabnext\/\.operator-wine-image\)" = "\$\{WINE_IMAGE\}"/)
  assert.match(candidate, /test -L "\$\{canonical_prefix\}"/)
  assert.match(candidate, /test ! -e "\$\{other_prefix\}"/)
  assert.match(candidate, /io\.sharplabnext\.framework\.matrix-parent/)
  assert.match(assemblerSource, /parent_manifest_sha256 = file_sha256\(manifest_path/)
  assert.match(assemblerSource, /layout_manifest_sha256 = file_sha256\(layout_path/)
  assert.match(selectorSource, /"parentImage": parent_image/)
  assert.match(selectorSource, /"layoutManifestSha256": layout_digest/)
})
