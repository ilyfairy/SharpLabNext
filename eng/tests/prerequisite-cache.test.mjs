import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  readPrerequisiteManifest,
  validateRepositoryFiles,
} from '../prerequisite-cache.mjs'

const repositoryRoot = path.resolve(import.meta.dirname, '../..')
const gitEnvironment = () => {
  const environment = { ...process.env }
  delete environment.GIT_DIR
  delete environment.GIT_WORK_TREE
  delete environment.GIT_INDEX_FILE
  return environment
}
const gitAvailable = spawnSync('git', ['--version'], { env: gitEnvironment(), windowsHide: true }).status === 0

function writeFixture(root) {
  const manifestValue = {
    schemaVersion: 3,
    localRegistry: {
      image: `registry@sha256:${'a'.repeat(64)}`,
      imageId: `sha256:${'a'.repeat(64)}`,
      containerName: 'sharplabnext-release-registry',
      host: '127.0.0.1',
      port: 5000,
    },
    downloads: [{
      kind: 'file',
      id: 'framework-installer',
      path: 'downloads/framework.exe',
      url: 'https://download.microsoft.com/framework.exe',
      sizeBytes: 1,
      sha256: 'b'.repeat(64),
      license: 'Microsoft test license',
    }],
    repositoryFiles: [{
      id: 'jsharp-installer',
      path: 'eng/prerequisites/jsharp/installer.exe',
      sizeBytes: 1,
      sha256: 'c'.repeat(64),
      gitLfs: true,
      license: 'Microsoft test license',
    }],
    generatedImages: [
      {
        id: 'jsharp20-development-base',
        reference: 'example/jsharp:test',
        buildKind: 'jsharp20',
        license: 'Private test license',
      },
      {
        id: 'cppcli-prepared-base',
        reference: 'example/cppcli:test',
        buildKind: 'cppcli',
        license: 'Private test license',
      },
    ],
  }
  const manifestPath = path.join(root, 'release-prerequisites.json')
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifestValue, null, 2)}\n`)
  const manifest = readPrerequisiteManifest(manifestPath)
  return { manifest, manifestPath }
}

test('repository prerequisites require expanded Git LFS bytes', { skip: !gitAvailable }, async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-repository-prerequisite-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const { manifest } = writeFixture(root)
  const installer = path.join(root, 'eng', 'prerequisites', 'jsharp', 'installer.exe')
  fs.mkdirSync(path.dirname(installer), { recursive: true })
  fs.writeFileSync(path.join(root, '.gitattributes'),
    'eng/prerequisites/jsharp/installer.exe filter=lfs diff=lfs merge=lfs -text\n')
  assert.equal(spawnSync('git', ['init', '--quiet'], { cwd: root, env: gitEnvironment() }).status, 0)
  fs.writeFileSync(installer, Buffer.from([0]))

  await assert.rejects(
    validateRepositoryFiles(root, manifest.value.repositoryFiles),
    /SHA-256 is invalid/,
  )

  fs.writeFileSync(installer,
    'version https://git-lfs.github.com/spec/v1\n' +
    `oid sha256:${'c'.repeat(64)}\nsize 1\n`)
  await assert.rejects(
    validateRepositoryFiles(root, manifest.value.repositoryFiles),
    /unexpanded Git LFS pointer/,
  )
})

test('content identity validates repository bytes without Git metadata', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-content-prerequisite-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const { manifest } = writeFixture(root)
  const installer = path.join(root, 'eng', 'prerequisites', 'jsharp', 'installer.exe')
  fs.mkdirSync(path.dirname(installer), { recursive: true })
  const bytes = Buffer.from([0])
  fs.writeFileSync(installer, bytes)
  const item = manifest.value.repositoryFiles[0]
  item.sizeBytes = bytes.length
  item.sha256 = crypto.createHash('sha256').update(bytes).digest('hex')

  const previous = process.env.SHARPLABNEXT_SOURCE_IDENTITY_MODE
  process.env.SHARPLABNEXT_SOURCE_IDENTITY_MODE = 'content'
  try {
    const files = await validateRepositoryFiles(root, manifest.value.repositoryFiles)
    assert.equal(files['jsharp-installer'], installer)
  } finally {
    if (previous === undefined) delete process.env.SHARPLABNEXT_SOURCE_IDENTITY_MODE
    else process.env.SHARPLABNEXT_SOURCE_IDENTITY_MODE = previous
  }
})

test('prerequisite manifest requires the pinned local registry image ID', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-prerequisite-manifest-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const { manifestPath } = writeFixture(root)
  const value = JSON.parse(fs.readFileSync(manifestPath, 'utf8'))
  delete value.localRegistry.imageId
  fs.writeFileSync(manifestPath, `${JSON.stringify(value)}\n`)

  assert.throws(
    () => readPrerequisiteManifest(manifestPath),
    /localRegistry must contain exactly/,
  )
})

test('release prerequisites lock the two const-generics fork packages', () => {
  const manifest = readPrerequisiteManifest(path.join(repositoryRoot, 'eng', 'release-prerequisites.json'));
  const packages = manifest.value.downloads.filter(item => item.kind === 'nuget-package')
    .map(item => ({
      id: item.id,
      path: item.path,
      package: item.package,
      version: item.version,
      sizeBytes: item.sizeBytes,
      sha256: item.sha256,
      license: item.license,
    }))

  assert.deepEqual(packages, [
    {
      id: 'const-generics-system-collections-immutable',
      path: 'downloads/const-generics-fork-packages/system.collections.immutable.8.0.0-dev.nupkg',
      package: 'System.Collections.Immutable',
      version: '8.0.0-dev',
      sizeBytes: 705507,
      sha256: '204d96f613cb1e19a063ccefebb3e58a8ca2a7cdfef9a8bd52e956bab17d5341',
      license: 'MIT',
    },
    {
      id: 'const-generics-system-reflection-metadata',
      path: 'downloads/const-generics-fork-packages/system.reflection.metadata.8.0.0-dev.nupkg',
      package: 'System.Reflection.Metadata',
      version: '8.0.0-dev',
      sizeBytes: 1248015,
      sha256: 'f5faf1a6f2a65c68be3eb1bcf4deeeea43a5af9f31fa661be06c4a9786884904',
      license: 'MIT',
    },
  ])
})

test('const-generics package URL must match its locked package identity', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-const-package-url-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const value = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'eng', 'release-prerequisites.json'), 'utf8'));
  const item = value.downloads.find(entry =>
    entry.id === 'const-generics-system-collections-immutable')
  item.url = item.url.replace('system.collections.immutable', 'system.reflection.metadata')
  const manifestPath = path.join(root, 'release-prerequisites.json')
  fs.writeFileSync(manifestPath, `${JSON.stringify(value)}\n`)

  assert.throws(
    () => readPrerequisiteManifest(manifestPath),
    /approved immutable HTTPS source/,
  )
})
