import assert from 'node:assert/strict'
import childProcess from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const helperPath = path.join(repositoryRoot, 'deploy', 'docker', 'dedupe-wine-prefixes.py')

function pythonCommand() {
  for (const command of ['python3', 'python']) {
    const probe = childProcess.spawnSync(command, ['--version'], { encoding: 'utf8' })
    if (probe.status === 0) return { command, prefix: [] }
  }
  return undefined
}

function runHelper(python, argumentsList) {
  return childProcess.spawnSync(
    python.command,
    [...python.prefix, helperPath, ...argumentsList],
    { encoding: 'utf8' },
  )
}

function writeFixture(root, relative, content, mode = 0o644) {
  const file = path.join(root, relative)
  fs.mkdirSync(path.dirname(file), { recursive: true })
  fs.writeFileSync(file, content)
  fs.chmodSync(file, mode)
  return file
}

test('Framework prefix dedupe helper is present and has a narrow immutable allow-list', () => {
  assert.equal(fs.existsSync(helperPath), true)
  const source = fs.readFileSync(helperPath, 'utf8')
  assert.match(source, /hardlink-immutable-v1/)
  assert.match(source, /Microsoft\.NET/)
  assert.match(source, /drive_c\/windows\/assembly/)
  assert.match(source, /system\.reg|user\.reg/)
  assert.match(source, /setupcache|nativeimages/i)
  assert.match(source, /--freeze/)
  assert.match(source, /--verify/)
})

test('Framework prefix dedupe links only frozen immutable files and verifies inode/content identity', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-prefix-layout-'))
  const source = path.join(root, 'clr2')
  const target = path.join(root, 'clr4')
  const manifest = path.join(root, 'layout.json')
  try {
    const duplicate = Buffer.from('identical framework payload')
    const sourceFile = writeFixture(
      source,
      'drive_c/windows/Microsoft.NET/Framework64/shared.dll',
      duplicate,
    )
    const targetFile = writeFixture(
      target,
      'drive_c/windows/Microsoft.NET/Framework64/shared.dll',
      duplicate,
    )
    const sourceAssembly = writeFixture(
      source,
      'drive_c/windows/assembly/GAC/shared.dll',
      duplicate,
    )
    const targetAssembly = writeFixture(
      target,
      'drive_c/windows/assembly/GAC/shared.dll',
      duplicate,
    )
    const mutableSource = writeFixture(source, 'system.reg', 'same registry')
    const mutableTarget = writeFixture(target, 'system.reg', 'same registry')
    const cacheSource = writeFixture(
      source,
      'drive_c/windows/Microsoft.NET/Framework64/cache/payload.dll',
      duplicate,
    )
    const cacheTarget = writeFixture(
      target,
      'drive_c/windows/Microsoft.NET/Framework64/cache/payload.dll',
      duplicate,
    )
    const nativeImagesSource = writeFixture(
      source,
      'drive_c/windows/Microsoft.NET/Framework64/NativeImages_v4.0.30319_64/payload.dll',
      duplicate,
    )
    const nativeImagesTarget = writeFixture(
      target,
      'drive_c/windows/Microsoft.NET/Framework64/NativeImages_v4.0.30319_64/payload.dll',
      duplicate,
    )

    const result = runHelper(python, [
      '--source', source,
      '--target', target,
      '--manifest', manifest,
      '--freeze',
    ])
    assert.equal(result.status, 0, result.stderr)
    const layout = JSON.parse(fs.readFileSync(manifest, 'utf8'))
    assert.equal(layout.strategy, 'hardlink-immutable-v1')
    assert.equal(layout.freeze, true)
    assert.equal(layout.linkedFileCount, 2)
    assert.equal(layout.linkedBytes, duplicate.length * 2)
    assert.equal(fs.statSync(sourceFile).ino, fs.statSync(targetFile).ino)
    assert.equal(
      fs.statSync(targetAssembly).ino,
      fs.statSync(sourceFile).ino,
      'cross-tree duplicate may point at the canonical source inode',
    )
    assert.notEqual(fs.statSync(mutableSource).ino, fs.statSync(mutableTarget).ino)
    assert.notEqual(fs.statSync(cacheSource).ino, fs.statSync(cacheTarget).ino)
    assert.notEqual(fs.statSync(nativeImagesSource).ino, fs.statSync(nativeImagesTarget).ino)
    assert.equal(fs.statSync(sourceFile).mode & 0o222, 0)
    assert.equal(fs.statSync(targetFile).mode & 0o222, 0)

    const verify = runHelper(python, [
      '--source', source,
      '--target', target,
      '--manifest', manifest,
      '--verify',
    ])
    assert.equal(verify.status, 0, verify.stderr)

    fs.unlinkSync(targetFile)
    fs.writeFileSync(targetFile, duplicate)
    const rejected = runHelper(python, [
      '--source', source,
      '--target', target,
      '--manifest', manifest,
      '--verify',
    ])
    assert.notEqual(rejected.status, 0)
    assert.match(rejected.stderr, /hard link|layout manifest/i)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})
