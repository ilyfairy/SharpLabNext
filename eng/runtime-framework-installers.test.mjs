import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const manifestPath = path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json')
const schemaPath = path.join(repositoryRoot, 'schemas', 'runtime-framework-installers.schema.json')
const dockerfilePath = path.join(
  repositoryRoot,
  'deploy',
  'docker',
  'Dockerfile.operator-wine-framework-matrix',
)
const candidateDockerfilePath = path.join(
  repositoryRoot,
  'deploy',
  'docker',
  'Dockerfile.runtime-wine-framework-matrix',
)
const prefixLayoutHelperPath = path.join(
  repositoryRoot,
  'deploy',
  'docker',
  'dedupe-wine-prefixes.py',
)
const cliPath = path.join(repositoryRoot, 'eng', 'prepare-framework-runtime.cs')
const certificateDirectory = path.join(repositoryRoot, 'deploy', 'docker', 'certificates')
const certificateFiles = new Map([
  ['microsoft-tls-rsa-root-g2-xsign.crt', 'cc80b386dca2922d3f341ab2595004a063f75dbbc5d96c5e026614f1555f0ef9'],
  ['microsoft-tls-g2-rsa-ca-ocsp-04.crt', '9a2573b478d0086cbe44a8036dc2c333bd9d6d765d3d5b5a7ba5fedce0f80609'],
])

const expectedTargets = [
  ['netfx20', '2.0', 'clr2', '/opt/wine-netfx-clr2'],
  ['netfx30', '3.0', 'clr2', '/opt/wine-netfx-clr2'],
  ['netfx35', '3.5', 'clr2', '/opt/wine-netfx-clr2'],
  ['netfx40', '4.0', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx45', '4.5', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx451', '4.5.1', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx452', '4.5.2', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx46', '4.6', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx461', '4.6.1', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx462', '4.6.2', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx47', '4.7', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx471', '4.7.1', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx472', '4.7.2', 'clr4', '/opt/wine-netfx-clr4'],
  ['netfx48', '4.8', 'clr4', '/opt/wine-netfx-clr4'],
]

function requireJson(filePath) {
  assert.equal(fs.existsSync(filePath), true, `${path.relative(repositoryRoot, filePath)} is required`)
  return JSON.parse(fs.readFileSync(filePath, 'utf8'))
}

test('shared Framework operator artifacts exist', () => {
  for (const filePath of [
    manifestPath,
    schemaPath,
    dockerfilePath,
    candidateDockerfilePath,
    cliPath,
    prefixLayoutHelperPath,
  ]) {
    assert.equal(fs.existsSync(filePath), true, `${path.relative(repositoryRoot, filePath)} is required`)
  }
})

test('Microsoft download certificate chain is exact and public-only', () => {
  for (const [fileName, expectedSha256] of certificateFiles) {
    const source = fs.readFileSync(path.join(certificateDirectory, fileName))
    assert.equal(crypto.createHash('sha256').update(source).digest('hex'), expectedSha256)
    assert.match(source.toString('ascii'), /^-----BEGIN CERTIFICATE-----\n/)
  }
})

test('installer manifest covers the runtime matrix exactly once', () => {
  const manifest = requireJson(manifestPath)
  const matrix = requireJson(path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'))

  assert.equal(manifest.schemaVersion, 1)
  assert.equal(manifest.winetricksVersion, '20240105')
  assert.deepEqual(
    manifest.targets.map(target => [target.id, target.version, target.clrGeneration, target.prefix]),
    expectedTargets,
  )
  assert.deepEqual(
    manifest.targets.map(target => [target.id, target.version, target.clrGeneration, target.prefix]),
    matrix.framework.targets.map(target => [target.id, target.version, target.clrGeneration, target.prefix]),
  )
  assert.equal(new Set(manifest.targets.map(target => target.id)).size, expectedTargets.length)
  assert.equal(new Set(manifest.targets.map(target => target.version)).size, expectedTargets.length)
})

test('only unavailable Winetricks versions use locked operator installers', () => {
  const manifest = requireJson(manifestPath)
  const manual = manifest.targets.filter(target => target.recipe.kind === 'operator-installer')
  assert.deepEqual(manual, [
    {
      id: 'netfx451',
      version: '4.5.1',
      clrGeneration: 'clr4',
      prefix: '/opt/wine-netfx-clr4',
      recipe: {
        kind: 'operator-installer',
        fileName: 'NDP451-KB2858728-x86-x64-AllOS-ENU.exe',
        sha256: '5ded8628ce233a5afa8e0efc19ad34690f05e9bb492f2ed0413508546af890fe',
        prerequisiteVerb: 'dotnet40',
        arguments: ['/q', '/norestart'],
      },
    },
    {
      id: 'netfx47',
      version: '4.7',
      clrGeneration: 'clr4',
      prefix: '/opt/wine-netfx-clr4',
      recipe: {
        kind: 'operator-installer',
        fileName: 'NDP47-KB3186497-x86-x64-AllOS-ENU.exe',
        sha256: '24762159579ec9763baec8c23555464360bd31677ee8894a58bdb67262e7e470',
        prerequisiteVerb: 'dotnet462',
        arguments: ['/q', '/norestart'],
      },
    },
  ])

  const winetricks = new Map(
    manifest.targets
      .filter(target => target.recipe.kind === 'winetricks')
      .map(target => [target.id, target.recipe.verb]),
  )
  assert.deepEqual([...winetricks], [
    ['netfx20', 'dotnet20'],
    ['netfx30', 'dotnet35sp1'],
    ['netfx35', 'dotnet35sp1'],
    ['netfx40', 'dotnet40'],
    ['netfx45', 'dotnet45'],
    ['netfx452', 'dotnet452'],
    ['netfx46', 'dotnet46'],
    ['netfx461', 'dotnet461'],
    ['netfx462', 'dotnet462'],
    ['netfx471', 'dotnet471'],
    ['netfx472', 'dotnet472'],
    ['netfx48', 'dotnet48'],
  ])
  assert.equal(manifest.targets.find(target => target.id === 'netfx30').recipe.sharedClr2FeaturePack, true)
  assert.doesNotMatch(JSON.stringify(manifest), /https?:\/\//i)
})

test('installer schema is strict and separates Winetricks from private installers', () => {
  const schema = requireJson(schemaPath)

  assert.equal(schema.$schema, 'https://json-schema.org/draft/2020-12/schema')
  assert.match(schema.$id, /^https:\/\//)
  assert.equal(schema.type, 'object')
  assert.equal(schema.additionalProperties, false)
  assert.deepEqual(schema.required, [
    'schemaVersion',
    'winetricksVersion',
    'companionPrefixes',
    'targets',
  ])
  assert.equal(schema.$defs.target.additionalProperties, false)
  assert.equal(schema.$defs.winetricksRecipe.additionalProperties, false)
  assert.equal(schema.$defs.operatorInstallerRecipe.additionalProperties, false)
  assert.equal(schema.$defs.operatorInstallerRecipe.properties.kind.const, 'operator-installer')
  assert.equal(schema.$defs.winetricksRecipe.properties.kind.const, 'winetricks')
  assert.match(schema.$defs.sha256.pattern, /\{64\}/)
})

test('operator Dockerfile keeps installers private, bounded, and preflights the final layer', () => {
  assert.equal(fs.existsSync(dockerfilePath), true, 'operator Dockerfile is required')
  const source = fs.readFileSync(dockerfilePath, 'utf8')

  assert.match(source, /ARG BASE_IMAGE/)
  assert.match(source, /ARG ROOT_IMAGE/)
  assert.match(source, /ARG SOURCE_REVISION/)
  assert.match(source, /FROM \$\{BASE_IMAGE\}/)
  assert.match(source, /FROM \$\{ROOT_IMAGE\} AS final/)
  assert.match(source, /COPY --from=wine-source \/usr\//)
  assert.match(source, /COPY --from=wine-source \/etc\/fonts\//)
  assert.match(source, /operator-root/)
  assert.doesNotMatch(source, /COPY --from=wine-source \/opt\/wine-dotnet/)
  assert.match(source, /@sha256:\[0-9a-f\]\{64\}/)
  assert.match(source, /ACCEPT_MICROSOFT_DOTNET_FRAMEWORK_EULA/)
  assert.match(source, /id=framework-installer-url/)
  assert.match(source, /from=framework-installer-context/)
  assert.doesNotMatch(source, /id=framework-installer(?:,|\s)/)
  assert.match(source, /sha256sum --check --status/)
  assert.match(source, /timeout --signal=KILL/)
  assert.match(source, /tail -c 16384/)
  assert.match(source, /tail -n 80/)
  assert.match(source, /<redacted-url>/)
  assert.match(source, /winetricks --optout --unattended/)
  assert.match(source, /WINE=\/usr\/lib\/wine\/wine/)
  assert.match(source, /WINELOADER=\/usr\/lib\/wine\/wine64/)
  assert.match(source, /WINESERVER=\/usr\/lib\/wine\/wineserver64/)
  assert.match(source, /update-ca-certificates/)
  assert.match(source, /ac8ea9f2874fd368a3e778b1a0b165ee898db9b9687c17edcdc76908ab58c82c/)
  assert.doesNotMatch(source, /(?:^|[\s"])wineserver -w/m)
  assert.match(source, /sharplabnext-wine-netfx-preflight/)
  assert.match(source, /sharplabnext-dedupe-wine-prefixes/)
  assert.match(source, /hardlink-immutable-v1/)
  assert.match(source, /\.wine-prefix-layout\.json/)
  assert.match(source, /--freeze/)
  assert.match(source, /--verify/)
  assert.match(source, /\/opt\/wine-netfx-clr2/)
  assert.match(source, /\/opt\/wine-netfx-clr4/)
  assert.match(source, /stage cleanup-private-assets/)
  assert.match(source, /rm -rf \/usr\/lib\/x86_64-linux-gnu\/wine\/i386-windows/)
  assert.match(source, /operator-only="true"/)
  assert.match(source, /org\.opencontainers\.image\.revision="\$\{SOURCE_REVISION\}"/)
  assert.match(source, /io\.sharplabnext\.source\.revision="\$\{SOURCE_REVISION\}"/)
  assert.match(source, /redistribution="operator-supplied-only"/)
  assert.doesNotMatch(source, /COPY[^\r\n]*\.(?:exe|msi|cab)\b/i)
  assert.doesNotMatch(source, /https?:\/\//i)

  const installEnd = source.indexOf("\nSH\n")
  const finalVerification = source.indexOf("RUN <<'SH'\nset -euo pipefail", installEnd + 1)
  assert.ok(installEnd >= 0 && finalVerification > installEnd)
  const verificationSource = source.slice(finalVerification, source.indexOf('\nLABEL ', finalVerification))
  assert.doesNotMatch(verificationSource, /--mount=/)
  assert.doesNotMatch(verificationSource, /framework-installer-url/)
})

test('Framework candidate pins its helper while retaining the operator manifest', () => {
  const source = fs.readFileSync(candidateDockerfilePath, 'utf8')
  assert.match(
    source,
    /COPY deploy\/docker\/dedupe-wine-prefixes\.py \/usr\/local\/bin\/sharplabnext-dedupe-wine-prefixes/,
  )
  assert.match(source, /chmod 0555 \/usr\/local\/bin\/sharplabnext-dedupe-wine-prefixes/)
  assert.match(source, /test -s \/opt\/sharplabnext\/\.wine-prefix-layout\.json/)
  assert.doesNotMatch(source, /COPY[^\r\n]*\.wine-prefix-layout\.json/)
})
