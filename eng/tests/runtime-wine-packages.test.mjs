import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const manifestPath = path.join(repositoryRoot, 'profiles', 'runtime-wine-packages.json')
const lockPath = path.join(repositoryRoot, 'profiles', 'lock.json')
const expectedPackageListSha256 =
  'sha256:fa83c245764fc09102029b249f5149a48baeda53a40c0432de973ebe09e39dee'
const signingFingerprint = 'F6ECB3762474EDA9D21B7022871920D1991BC93C'

function loadManifest() { return JSON.parse(fs.readFileSync(manifestPath, 'utf8')); }

function indexIdentity(snapshotId, suite, kind, component, path_) { return `${snapshotId}\0${suite}\0${kind}\0${component}\0${path_}`; }

test('Wine package manifest locks the complete signed no-i386 closure', () => {
  const manifest = loadManifest()

  assert.equal(manifest.schemaVersion, 1)
  assert.equal(manifest.platform, 'linux/amd64')
  assert.equal(manifest.baseImageId, 'dotnet-runtime-deps')
  assert.deepEqual(manifest.component, {
    id: 'wine-coreclr-userspace',
    kind: 'runtime-dependency',
    resolvedVersion: 'wine-9.0~repack-4build3+xvfb-2:21.1.12-1ubuntu1.6',
    license: 'LGPL-2.1+',
    sourceUri: 'https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/',
  })

  const expectedSuites = new Map([
    ['20260810T000000Z\0noble', ['cdb2f31d809f589719a53c6ad15f255b27569c4059542ada282aaa21b8e164b0', 255850]],
    ['20260810T000000Z\0noble-updates', ['ef81441269d3a8bdd8cdfe9095de7deb7f1af70d42191f61f1af3c8fb72cfb32', 126125]],
    ['20260810T000000Z\0noble-security', ['3cfb1c8d7499c0bac1bfbe1e32675d200f0ca74b18afc4248c45325a073d0fd0', 126127]],
    ['20260610T000000Z\0noble-updates', ['f51355c88d0b337b45cede930d215a56f806b7c9339e95487b6600ea02c728ce', 126125]],
  ])
  assert.equal(manifest.archiveSnapshots.length, 2)
  const indexes = new Set()
  let suiteCount = 0
  for (const snapshot of manifest.archiveSnapshots) {
    assert.equal(snapshot.uri, `https://snapshot.ubuntu.com/ubuntu/${snapshot.id}/`)
    for (const suite of snapshot.suites) {
      suiteCount++
      const expected = expectedSuites.get(`${snapshot.id}\0${suite.name}`)
      assert.ok(expected, `unexpected archive suite ${snapshot.id}/${suite.name}`)
      assert.equal(suite.inReleaseSha256, expected[0])
      assert.equal(suite.inReleaseSizeBytes, expected[1])
      assert.equal(suite.signingKeyFingerprint, signingFingerprint)
      assert.equal(suite.indexes.length, 8)
      for (const component of ['main', 'universe', 'restricted', 'multiverse']) {
        const binaryPath = `${component}/binary-amd64/Packages.gz`
        const sourcePath = `${component}/source/Sources.gz`
        assert.ok(suite.indexes.some(index =>
          index.kind === 'binary' && index.component === component &&
          index.architecture === 'amd64' && index.path === binaryPath))
        assert.ok(suite.indexes.some(index =>
          index.kind === 'source' && index.component === component &&
          index.architecture === undefined && index.path === sourcePath))
      }
      for (const index of suite.indexes) {
        assert.match(index.sha256, /^[0-9a-f]{64}$/)
        assert.ok(Number.isSafeInteger(index.sizeBytes) && index.sizeBytes > 0)
        const identity = indexIdentity(
          snapshot.id, suite.name, index.kind, index.component, index.path)
        assert.equal(indexes.has(identity), false)
        indexes.add(identity)
      }
    }
  }
  assert.equal(suiteCount, 4)
  assert.equal(indexes.size, 32)

  assert.equal(manifest.resolvedPackages.length, 228)
  const names = manifest.resolvedPackages.map(item => item.name)
  assert.deepEqual(names, [...names].sort())
  assert.ok(names.every(name => !name.endsWith(':i386')))
  const canonical = manifest.resolvedPackages.map(item => `${item.name}=${item.version}\n`).join('')
  assert.equal(
    `sha256:${crypto.createHash('sha256').update(canonical, 'utf8').digest('hex')}`,
    expectedPackageListSha256)
  assert.equal(manifest.resolvedPackageListSha256, expectedPackageListSha256)

  const sources = new Map(manifest.sourcePackages.map(source => [
    `${source.name}\0${source.version}`,
    source,
  ]))
  assert.equal(sources.size, 162)
  let sourceFileCount = 0
  let sourceBytes = 0
  for (const source of manifest.sourcePackages) {
    assert.ok(indexes.has(indexIdentity(
      source.archiveSnapshotId,
      source.archiveSuite,
      'source',
      source.archiveComponent,
      source.archiveIndexPath)))
    assert.ok(source.files.some(file => file.path.endsWith('.dsc')))
    for (const file of source.files) {
      sourceFileCount++
      sourceBytes += file.sizeBytes
      assert.match(file.path, /^pool\//)
      assert.match(file.sha256, /^[0-9a-f]{64}$/)
      assert.ok(Number.isSafeInteger(file.sizeBytes) && file.sizeBytes > 0)
    }
  }
  assert.equal(sourceFileCount, 526)
  assert.equal(sourceBytes, 840446201)

  const copyrightPaths = new Set()
  for (const package_ of manifest.resolvedPackages) {
    assert.ok(indexes.has(indexIdentity(
      package_.archiveSnapshotId,
      package_.archiveSuite,
      'binary',
      package_.archiveComponent,
      package_.archiveIndexPath)))
    assert.ok(sources.has(`${package_.sourcePackage}\0${package_.sourceVersion}`))
    assert.match(package_.path, /^pool\//)
    assert.match(package_.sha256, /^[0-9a-f]{64}$/)
    assert.match(package_.copyrightPath, /^\/usr\/share\/doc\/[A-Za-z0-9][A-Za-z0-9.+_-]*\/copyright$/)
    assert.match(package_.copyrightSha256, /^[0-9a-f]{64}$/)
    assert.ok(Number.isSafeInteger(package_.copyrightSizeBytes) && package_.copyrightSizeBytes > 0)
    copyrightPaths.add(package_.copyrightPath)
  }
  assert.equal(copyrightPaths.size, 225)
  assert.equal(
    manifest.resolvedPackages.find(package_ => package_.name === 'openssl').copyrightPath,
    '/usr/share/doc/openssl/copyright')
  assert.deepEqual(manifest.noticeArchive, {
    imagePath: '/usr/local/share/sharplabnext/wine-coreclr-copyright-notices.tar',
    sha256: '3fcd04a992da99ac8fb08d3b7aa5a3ac29f28d9de221f7bc37c455c431e27f8d',
    sizeBytes: 2887680,
    entryCount: 225,
  })
  assert.doesNotMatch(JSON.stringify(manifest), /archive\.ubuntu\.com|blob\/main/)
})

test('Wine direct packages and source offer use only immutable snapshot material', () => {
  const manifest = loadManifest()
  const directNames = manifest.directPackages.map(package_ => package_.name)
  assert.deepEqual(directNames, ['wine', 'wine64', 'fonts-wine', 'xvfb'])
  assert.ok(manifest.directPackages.every(package_ =>
    package_.sourceUri.startsWith('https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/pool/')))
  assert.deepEqual(manifest.sourceOffer, {
    baseUri: 'https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/pool/universe/w/wine/',
    package: 'wine',
    version: '9.0~repack-4build3',
    license: 'LGPL-2.1+',
    files: [
      {
        path: 'wine_9.0~repack-4build3.debian.tar.xz',
        sha256: '0e1ac34c2272c560df213602495e2792de8a1c31bf27a6b6fbea39289dfc145a',
        sizeBytes: 58753032,
      },
      {
        path: 'wine_9.0~repack-4build3.dsc',
        sha256: '5d720edb86a3069749efe89c3a9d886c7faa19aa3f55f1e9c4a8e0abda8bda85',
        sizeBytes: 3826,
      },
      {
        path: 'wine_9.0~repack.orig.tar.xz',
        sha256: 'b956a23e00a5083f46c5c5ce0fbb3428460548a55ec1414cc20c6c21c7c8d0a7',
        sizeBytes: 26988196,
      },
    ],
  })
})

test('Wine userspace release-lock component binds the exact manifest bytes', () => {
  const manifestBytes = fs.readFileSync(manifestPath)
  const manifest = JSON.parse(manifestBytes.toString('utf8'))
  const releaseLock = JSON.parse(fs.readFileSync(lockPath, 'utf8'))
  assert.deepEqual(releaseLock.components['wine-coreclr-userspace'], {
    kind: 'runtime-dependency',
    resolvedVersion: manifest.component.resolvedVersion,
    digest: `sha256:${crypto.createHash('sha256').update(manifestBytes).digest('hex')}`,
    sourceUri: manifest.component.sourceUri,
  })
})
