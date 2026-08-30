import assert from 'node:assert/strict'
import childProcess from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const extractor = path.join(repositoryRoot, 'deploy', 'docker', 'extract-netfx48-sdk.py')
const dockerfile = path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.operator-cppcli-base')

function pythonCommand() {
  for (const command of ['python3', 'python']) {
    const result = childProcess.spawnSync(command, ['--version'], { encoding: 'utf8' })
    if (result.status === 0) return command
  }
  return undefined
}

function sha1(value) { return crypto.createHash('sha1').update(value).digest('hex').toUpperCase(); }

function writeFixture(root, { corruptCab = false } = {}) {
  const bundle = path.join(root, 'bundle')
  const output = path.join(root, 'output')
  fs.mkdirSync(bundle, { recursive: true })
  const msi = Buffer.from('opaque SDK MSI fixture')
  const cab = Buffer.from('opaque SDK CAB fixture')
  fs.writeFileSync(path.join(bundle, 'opaque-u7'), msi)
  fs.writeFileSync(path.join(bundle, 'opaque-a42'), corruptCab ? Buffer.from('corrupt') : cab)
  fs.writeFileSync(path.join(bundle, 'unrelated-a1'), 'unrelated payload')
  fs.writeFileSync(path.join(bundle, 'manifest-zero'), `<?xml version="1.0" encoding="utf-8"?>
<BurnManifest xmlns="http://schemas.microsoft.com/wix/2008/Burn">
  <Payload Id="sdk-msi" FilePath="packages\\netfxsdk\\sdk_tools48.msi"
    FileSize="${msi.length}" Hash="${sha1(msi)}" Packaging="embedded"
    SourcePath="opaque-u7" Container="WixAttachedContainer" />
  <Payload Id="sdk-cab" FilePath="packages\\netfxsdk\\sdk_tools48.cab"
    FileSize="${cab.length}" Hash="${sha1(cab)}" Packaging="embedded"
    SourcePath="opaque-a42" Container="WixAttachedContainer" />
  <Payload Id="unrelated" FilePath="packages\\other\\other.msi"
    FileSize="17" Hash="${sha1('unrelated payload')}" Packaging="embedded"
    SourcePath="unrelated-a1" Container="WixAttachedContainer" />
  <Chain>
    <MsiPackage Id="netfxsdk" ProductCode="{949C0535-171C-480F-9CF4-D25C9E60FE88}"
      Version="4.8.03928" Language="1033">
      <PayloadRef Id="sdk-msi" />
      <PayloadRef Id="sdk-cab" />
    </MsiPackage>
  </Chain>
</BurnManifest>`)
  return { bundle, output, msi, cab }
}

function runExtractor(python, fixture) {
  return childProcess.spawnSync(python, [
    extractor,
    '--bundle-root', fixture.bundle,
    '--output', fixture.output,
  ], { encoding: 'utf8', cwd: repositoryRoot })
}

test('C++/CLI SDK extractor resolves opaque Burn payload names by exact package identity', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-netfxsdk-'))
  try {
    const fixture = writeFixture(root)
    const result = runExtractor(python, fixture)
    assert.equal(result.status, 0, result.stderr)
    assert.deepEqual(fs.readFileSync(path.join(fixture.output, 'sdk_tools48.msi')), fixture.msi)
    assert.deepEqual(fs.readFileSync(path.join(fixture.output, 'sdk_tools48.cab')), fixture.cab)
    assert.match(result.stdout, /product=\{949C0535-171C-480F-9CF4-D25C9E60FE88\}/)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('C++/CLI SDK extractor rejects an opaque payload that drifts from the Burn manifest', {
  skip: pythonCommand() === undefined,
}, () => {
  const python = pythonCommand()
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-netfxsdk-invalid-'))
  try {
    const fixture = writeFixture(root, { corruptCab: true })
    const result = runExtractor(python, fixture)
    assert.notEqual(result.status, 0)
    assert.match(result.stderr, /size is invalid|hash does not match/)
    assert.equal(fs.existsSync(path.join(fixture.output, 'sdk_tools48.msi')), false)
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('C++/CLI Dockerfile uses the manifest extractor and verifies exact SDK MSI metadata', () => {
  const source = fs.readFileSync(dockerfile, 'utf8')
  assert.match(source, /COPY --chmod=0555 deploy\/docker\/extract-netfx48-sdk\.py/)
  assert.match(source, /sharplabnext-extract-netfx48-sdk/)
  assert.match(source, /ProductCode.*949C0535-171C-480F-9CF4-D25C9E60FE88/s)
  assert.match(source, /ProductVersion.*4\.8\.03928/s)
  assert.match(source, /sdk_tools48\.cab/)
  assert.doesNotMatch(source, /find .*sdk_tools48\.msi/)
  assert.doesNotMatch(source, /developer_pack_root.*\/a3/s)
})
