import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const manifestPath = path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json')
const schemaPath = path.join(repositoryRoot, 'schemas', 'runtime-framework-installers.schema.json')
const dockerfilePath = path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.operator-wine-framework-matrix')
const bootstrapPath = path.join(repositoryRoot, 'deploy', 'docker', 'wine-netfx-framework-bootstrap.sh')
const candidateDockerfilePath = path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.runtime-wine-framework-matrix')
const prefixLayoutHelperPath = path.join(repositoryRoot, 'deploy', 'docker', 'dedupe-wine-prefixes.py')
const cliPath = path.join(repositoryRoot, 'eng', 'tools', 'prepare-framework-runtime.cs')
const vendoredPayloadRelativePath = 'eng/prerequisites/dotnet-framework-2.0/NetFx64.exe'
const vendoredPayloadPath = path.join(repositoryRoot, ...vendoredPayloadRelativePath.split('/'))
const expectedVendoredPayload = {
  id: 'dotnet20-x64',
  verb: 'dotnet20',
  repositoryPath: vendoredPayloadRelativePath,
  cachePath: 'dotnet20/NetFx64.exe',
  sizeBytes: 47400128,
  sha256: '7ea86dca8eeaedcaa4a17370547ca2cea9e9b6774972b8e03d2cb1fb0e798669',
}
const expectedCachedPayload = {
  id: 'dotnet35sp1-full',
  verb: 'dotnet35sp1',
  prerequisiteId: 'netfx35sp1-installer',
  cachePath: 'dotnet35sp1/dotnetfx35.exe',
  sizeBytes: 242743296,
  sha256: '0582515bde321e072f8673e829e175ed2e7a53e803127c50253af76528e66bc1',
}
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

const expectedBootstrapDirectPackages = [
  { name: 'python3', version: '3.12.3-0ubuntu2.1' },
  { name: 'cabextract', version: '1.11-2' },
  { name: 'winetricks', version: '20240105-2' },
]

const expectedBootstrapResolvedPackages = [
  'aria2=1.37.0+debian-1build3',
  'binutils-common:amd64=2.42-4ubuntu2.10',
  'binutils-x86-64-linux-gnu=2.42-4ubuntu2.10',
  'binutils=2.42-4ubuntu2.10',
  'cabextract=1.11-2',
  'libaria2-0:amd64=1.37.0+debian-1build3',
  'libbinutils:amd64=2.42-4ubuntu2.10',
  'libcares2:amd64=1.27.0-1.0ubuntu1',
  'libctf-nobfd0:amd64=2.42-4ubuntu2.10',
  'libctf0:amd64=2.42-4ubuntu2.10',
  'libgprofng0:amd64=2.42-4ubuntu2.10',
  'libjansson4:amd64=2.14-2build2',
  'libmspack0t64:amd64=0.11-1.1build1',
  'libpython3-stdlib:amd64=3.12.3-0ubuntu2.1',
  'libpython3.12-minimal:amd64=3.12.3-1ubuntu0.15',
  'libpython3.12-stdlib:amd64=3.12.3-1ubuntu0.15',
  'libreadline8t64:amd64=8.2-4build1',
  'libsframe1:amd64=2.42-4ubuntu2.10',
  'libsqlite3-0:amd64=3.45.1-1ubuntu2.7',
  'libssh2-1t64:amd64=1.11.0-4.1ubuntu0.24.04.3',
  'media-types=10.1.0',
  'netbase=6.4',
  'python3-minimal=3.12.3-0ubuntu2.1',
  'python3.12-minimal=3.12.3-1ubuntu0.15',
  'python3.12=3.12.3-1ubuntu0.15',
  'python3=3.12.3-0ubuntu2.1',
  'readline-common=8.2-4build1',
  'winetricks=20240105-2',
]

const expectedBootstrapResolvedPackageListSha256 =
  'f5fddc3a5d79452068b4633aa98e95156bca47bf8285bcab0e7b69c5a546830d'
const expectedClassicWow64DirectPackage = {
  name: 'wine32',
  architecture: 'i386',
  version: '9.0~repack-4build3',
}
const expectedClassicWow64ReplacedPackages = [
  'libc-bin=2.39-0ubuntu8.7',
  'libc6:amd64=2.39-0ubuntu8.7',
  'libssl3t64:amd64=3.0.13-0ubuntu3.11',
  'openssl=3.0.13-0ubuntu3.11',
]
const expectedClassicWow64ReplacedPackageListSha256 =
  '4a69f0e49c3ffd2cd0a5ef4001395cc5df87748ceb903a5595dea5872c3d1a45'
const expectedClassicWow64ResolvedPackageListSha256 =
  'e96dce12a7d0347874522dce2a520588fe4f3860feafd55a5736f11241b0ec8e'

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

test('the unavailable dotnet20 x64 payload is an exact hydrated Git LFS input', async () => {
  const manifest = requireJson(manifestPath)
  assert.deepEqual(manifest.vendoredWinetricksPayloads, [expectedVendoredPayload])

  const attributes = fs.readFileSync(path.join(repositoryRoot, '.gitattributes'), 'utf8')
  assert.match(
    attributes,
    /^eng\/prerequisites\/dotnet-framework-2\.0\/NetFx64\.exe filter=lfs diff=lfs merge=lfs -text$/m,
  )
  const dockerIgnore = fs.readFileSync(path.join(repositoryRoot, '.dockerignore'), 'utf8')
  assert.match(
    dockerIgnore,
    /^eng\/prerequisites\/dotnet-framework-2\.0\/NetFx64\.exe$/m,
  )

  const stat = fs.statSync(vendoredPayloadPath)
  assert.equal(stat.isFile(), true)
  assert.equal(stat.size, expectedVendoredPayload.sizeBytes)
  const hash = crypto.createHash('sha256')
  for await (const chunk of fs.createReadStream(vendoredPayloadPath)) hash.update(chunk)
  assert.equal(hash.digest('hex'), expectedVendoredPayload.sha256)
})

test('dotnet35sp1 is a hash-locked prerequisite-cache input', () => {
  const manifest = requireJson(manifestPath)
  const prerequisites = requireJson(path.join(repositoryRoot, 'eng', 'release-prerequisites.json'))
  assert.deepEqual(manifest.cachedWinetricksPayloads, [expectedCachedPayload])
  assert.deepEqual(
    prerequisites.downloads.find(download => download.id === expectedCachedPayload.prerequisiteId),
    {
      kind: 'file',
      id: 'netfx35sp1-installer',
      path: 'downloads/dotnetfx35.exe',
      url: 'https://download.microsoft.com/download/2/0/e/20e90413-712f-438c-988e-fdaa79a8ac3d/dotnetfx35.exe',
      sizeBytes: expectedCachedPayload.sizeBytes,
      sha256: expectedCachedPayload.sha256,
      license: 'Microsoft .NET Framework Redistributable EULA',
    },
  )
})

test('Framework bootstrap tools lock the complete signed Docker-only package delta', () => {
  const manifest = requireJson(manifestPath)
  const bootstrap = manifest.bootstrapTools

  assert.equal(bootstrap.archiveSnapshotId, '20260810T000000Z')
  assert.deepEqual(bootstrap.directPackages, expectedBootstrapDirectPackages)
  assert.deepEqual(bootstrap.resolvedPackages, expectedBootstrapResolvedPackages)
  assert.deepEqual(
    bootstrap.resolvedPackages,
    [...bootstrap.resolvedPackages].sort(),
  )
  assert.equal(new Set(bootstrap.resolvedPackages).size, 28)
  const canonical = `${bootstrap.resolvedPackages.join('\n')}\n`
  assert.equal(
    crypto.createHash('sha256').update(canonical, 'utf8').digest('hex'),
    expectedBootstrapResolvedPackageListSha256,
  )
  assert.equal(
    bootstrap.resolvedPackageListSha256,
    expectedBootstrapResolvedPackageListSha256,
  )
})

test('Framework installer locks classic WoW64 to an isolated build-only package transition', () => {
  const manifest = requireJson(manifestPath)
  const installer = manifest.classicWow64Installer

  assert.equal(installer.archiveSnapshotId, '20260810T000000Z')
  assert.equal(installer.foreignArchitecture, 'i386')
  assert.deepEqual(installer.directPackage, expectedClassicWow64DirectPackage)
  assert.deepEqual(installer.replacedPackages, expectedClassicWow64ReplacedPackages)
  assert.equal(installer.resolvedPackages.length, 109)
  assert.deepEqual(installer.resolvedPackages, [...installer.resolvedPackages].sort())
  assert.equal(new Set(installer.resolvedPackages).size, 109)
  assert.ok(installer.resolvedPackages.includes('wine32:i386=9.0~repack-4build3'))

  for (const [packages, expectedDigest, declaredDigest] of [
    [
      installer.replacedPackages,
      expectedClassicWow64ReplacedPackageListSha256,
      installer.replacedPackageListSha256,
    ],
    [
      installer.resolvedPackages,
      expectedClassicWow64ResolvedPackageListSha256,
      installer.resolvedPackageListSha256,
    ],
  ]) {
    const canonical = `${packages.join('\n')}\n`
    assert.equal(crypto.createHash('sha256').update(canonical, 'utf8').digest('hex'), expectedDigest)
    assert.equal(declaredDigest, expectedDigest)
  }
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
        sha256: 'd9690c83d7ce56b2804ea34aef79ce34b242d60b9cec16385bce1340cfe00883',
        prerequisiteVerb: 'dotnet462',
        arguments: ['/q', '/norestart'],
      },
    },
  ])

  const winetricks = new Map(manifest.targets.filter(target => target.recipe.kind === 'winetricks').map(target => [target.id, target.recipe.verb]));
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
    'vendoredWinetricksPayloads',
    'cachedWinetricksPayloads',
    'bootstrapTools',
    'classicWow64Installer',
    'companionPrefixes',
    'targets',
  ])
  assert.equal(schema.$defs.bootstrapTools.additionalProperties, false)
  assert.equal(schema.$defs.vendoredWinetricksPayload.additionalProperties, false)
  assert.equal(schema.$defs.cachedWinetricksPayload.additionalProperties, false)
  assert.equal(schema.$defs.vendoredWinetricksPayload.properties.sizeBytes.const, 47400128)
  assert.equal(
    schema.$defs.vendoredWinetricksPayload.properties.sha256.const,
    expectedVendoredPayload.sha256,
  )
  assert.equal(
    schema.$defs.cachedWinetricksPayload.properties.sha256.const,
    expectedCachedPayload.sha256,
  )
  assert.equal(schema.$defs.bootstrapTools.properties.resolvedPackages.minItems, 28)
  assert.equal(schema.$defs.bootstrapTools.properties.resolvedPackages.maxItems, 28)
  assert.equal(schema.$defs.classicWow64Installer.additionalProperties, false)
  assert.equal(schema.$defs.classicWow64Installer.properties.replacedPackages.minItems, 4)
  assert.equal(schema.$defs.classicWow64Installer.properties.resolvedPackages.minItems, 109)
  assert.equal(schema.$defs.classicWow64Installer.properties.resolvedPackages.maxItems, 109)
  assert.equal(schema.$defs.target.additionalProperties, false)
  assert.equal(schema.$defs.winetricksRecipe.additionalProperties, false)
  assert.equal(schema.$defs.operatorInstallerRecipe.additionalProperties, false)
  assert.equal(schema.$defs.operatorInstallerRecipe.properties.kind.const, 'operator-installer')
  assert.equal(schema.$defs.winetricksRecipe.properties.kind.const, 'winetricks')
  assert.match(schema.$defs.sha256.pattern, /\{64\}/)
})

test('operator Dockerfile keeps installers private, bounded, and preflights the final layer', () => {
  assert.equal(fs.existsSync(dockerfilePath), true, 'operator Dockerfile is required')
  assert.equal(fs.existsSync(bootstrapPath), true, 'Framework bootstrap script is required')
  const dockerfile = fs.readFileSync(dockerfilePath, 'utf8')
  const bootstrap = fs.readFileSync(bootstrapPath, 'utf8')
  const source = `${dockerfile}\n${bootstrap}`

  assert.match(source, /ARG BASE_IMAGE/)
  assert.match(source, /ARG ROOT_IMAGE/)
  assert.match(source, /ARG SOURCE_REVISION/)
  assert.match(source, /FROM \$\{BASE_IMAGE\}/)
  assert.match(dockerfile, /FROM \$\{ROOT_IMAGE\} AS framework-wow64-base/)
  assert.match(dockerfile, /FROM \$\{FRAMEWORK_WOW64_BASE_IMAGE\} AS framework-companion-seed/)
  assert.match(dockerfile, /FROM \$\{FRAMEWORK_SEED_IMAGE\} AS framework-installer/)
  assert.doesNotMatch(dockerfile, /^ARG (?:FRAMEWORK_WOW64_BASE_IMAGE|FRAMEWORK_SEED_IMAGE)=/m)
  assert.match(source, /FROM \$\{ROOT_IMAGE\} AS final/)
  assert.match(source, /COPY --from=wine-source \/usr\//)
  assert.match(source, /COPY --from=wine-source \/etc\/fonts\//)
  assert.match(source, /sharplabnext-snapshot\.sources/)
  assert.match(source, /python3=3\.12\.3-0ubuntu2\.1/)
  assert.match(source, /cabextract=1\.11-2/)
  assert.match(source, /winetricks=20240105-2/)
  assert.match(source, /comm -23/)
  assert.match(source, /comm -13/)
  assert.match(source, /resolvedPackageListSha256/)
  assert.match(source, /dpkg --add-architecture i386/)
  assert.match(source, /wine32:i386=9\.0~repack-4build3/)
  assert.match(source, /WINE=\/usr\/lib\/wine\/wine \\/)
  assert.match(source, /COPY --from=framework-installer \/opt\/ \/opt\//)
  assert.match(source, /operator-root/)
  assert.doesNotMatch(source, /COPY --from=wine-source \/opt\/wine-dotnet/)
  assert.match(source, /@sha256:\[0-9a-f\]\{64\}/)
  assert.match(source, /ACCEPT_MICROSOFT_DOTNET_FRAMEWORK_EULA/)
  assert.match(source, /id=framework-installer-url/)
  assert.match(source, /from=framework-vendored-context/)
  assert.match(source, /from=framework-cached-context/)
  assert.match(source, /from=framework-installer-context/)
  assert.match(source, /framework_vendored_root=\/run\/operator-assets\/framework-vendored/)
  assert.match(source, /framework_cached_root=\/run\/operator-assets\/framework-cached/)
  assert.match(source, /framework_installer_root=\/run\/operator-assets\/framework-installer/)
  assert.doesNotMatch(source, /installer\.bin/)
  assert.doesNotMatch(source, /staged-context-file-set|staged_context_files/)
  assert.equal(
    dockerfile.match(/RUN --network=none/g)?.length,
    2,
  )
  assert.match(
    dockerfile,
    /FROM framework-companion-seed-\$\{FRAMEWORK_INSTALLER_NETWORK\} AS framework-companion-seed/,
  )
  assert.match(
    dockerfile,
    /FROM framework-installer-\$\{FRAMEWORK_INSTALLER_NETWORK\} AS framework-installer/,
  )
  assert.match(bootstrap, /expected_installer_network=default/)
  assert.match(bootstrap, /expected_installer_network=none/)
  assert.match(bootstrap, /fail_bootstrap installer-network/)
  assert.match(source, /vendoredWinetricksPayloads/)
  assert.match(source, /dotnet20\/NetFx64\.exe/)
  assert.match(source, /dotnet35sp1\/dotnetfx35\.exe/)
  assert.match(source, /47400128/)
  assert.match(source, new RegExp(expectedVendoredPayload.sha256))
  assert.match(source, /cp "\$\{vendored_payload_source\}" "\$\{vendored_destination\}"/)
  assert.match(source, /cp "\$\{cached_payload_source\}" "\$\{cached_destination\}"/)
  assert.match(source, /W_CACHE="\$\{cache\}"/)
  assert.doesNotMatch(source, /id=framework-installer(?:,|\s)/)
  assert.match(source, /sha256sum --check --status/)
  assert.match(source, /timeout --signal=KILL/)
  assert.match(source, /tail -c 16384/)
  assert.match(source, /tail -n 80/)
  assert.match(source, /<redacted-url>/)
  assert.match(source, /winetricks --optout --unattended/)
  assert.match(source, /WINE=\/usr\/lib\/wine\/wine64/)
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
  assert.match(source, /stage disable-runtime-ngen-services/)
  assert.match(source, /clr_optimization_\$\{service_version\}_\$\{architecture\}/)
  assert.match(source, /reg\.exe add "\$\{key\}" \/v Start \/t REG_DWORD \/d 4 \/f/)
  assert.match(source, /rm -rf \/usr\/lib\/x86_64-linux-gnu\/wine\/i386-windows/)
  assert.match(source, /test ! -e \/usr\/lib\/i386-linux-gnu\/wine/)
  assert.match(source, /test ! -e \/usr\/lib\/wine\/wine/)
  assert.match(source, /dpkg --print-foreign-architectures/)
  assert.match(source, /grep ':i386\$'/)
  assert.match(source, /operator-only="true"/)
  assert.match(source, /org\.opencontainers\.image\.revision="\$\{SOURCE_REVISION\}"/)
  assert.match(source, /io\.sharplabnext\.source\.revision="\$\{SOURCE_REVISION\}"/)
  assert.match(source, /redistribution="operator-supplied-only"/)
  assert.match(source, /framework-companion-seed-v1/)
  assert.match(source, /framework-companion-binding-v1/)
  assert.match(source, /stage "install-shared-\$\{target_generation\}-companion"/)
  assert.match(source, /stage "install-target-\$\{target_generation\}"/)
  assert.doesNotMatch(bootstrap, /stage install-companion-clr[24]/)
  assert.doesNotMatch(source, /COPY[^\r\n]*\.(?:exe|msi|cab)\b/i)
  assert.doesNotMatch(source, /https?:\/\//i)

  const wow64Stage = dockerfile.slice(
    dockerfile.indexOf('FROM ${ROOT_IMAGE} AS framework-wow64-base'),
    dockerfile.indexOf('FROM ${FRAMEWORK_WOW64_BASE_IMAGE} AS framework-companion-seed'),
  )
  assert.doesNotMatch(wow64Stage, /FRAMEWORK_TARGET_ID|FRAMEWORK_VERSION|CLR_GENERATION/)
  const finalStage = dockerfile.slice(dockerfile.indexOf('FROM ${ROOT_IMAGE} AS final'))
  assert.doesNotMatch(finalStage, /--mount=/)
  assert.doesNotMatch(finalStage, /framework-installer-url/)
  assert.doesNotMatch(finalStage, /NetFx64\.exe/)
  assert.doesNotMatch(finalStage, /wine32:i386/)
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
