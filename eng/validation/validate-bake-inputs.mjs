import fs from 'node:fs'
import crypto from 'node:crypto'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { findDockerfileStageArgumentScopeViolations } from '../dockerfile-stage-arguments.mjs'
import { validateDockerfileFrontend } from '../dockerfile-frontend.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const manifestPath = path.join(repositoryRoot, 'profiles', 'base-images.json')
const bakePath = path.join(repositoryRoot, 'eng', 'bake.hcl')
const candidateBakePath = path.join(repositoryRoot, 'eng', 'bake.runtime-candidates.hcl')
const dockerDirectory = path.join(repositoryRoot, 'deploy', 'docker')
const winePackageManifestPath = path.join(repositoryRoot, 'profiles', 'runtime-wine-packages.json')

const expectedBaseImages = new Map([
  ['node-builder', 'BASE_NODE_IMAGE'],
  ['dotnet-sdk', 'BASE_DOTNET_SDK_IMAGE'],
  ['dotnet-aspnet', 'BASE_DOTNET_ASPNET_IMAGE'],
  ['const-generics-aspnet', 'BASE_CONST_GENERICS_ASPNET_IMAGE'],
  ['dotnet-runtime-deps', 'BASE_DOTNET_RUNTIME_DEPS_IMAGE'],
  ['dotnet-runtime-build', 'BASE_DOTNET_RUNTIME_BUILD_IMAGE'],
  ['mono-jsil', 'BASE_MONO_JSIL_IMAGE'],
])

const jitProfilerSourceLabels = [
  'jit-profiler-clr-samples.commit',
  'jit-profiler-clr-samples.source-uri',
  'jit-profiler-runtime-headers.commit',
  'jit-profiler-runtime-headers.source-uri',
]

const frameworkManagedReferenceSetIds = [
  'netfx20-managed-ref',
  'netfx30-managed-ref',
  'netfx35-managed-ref',
  'netfx40-managed-ref',
  'netfx45-managed-ref',
  'netfx451-managed-ref',
  'netfx452-managed-ref',
  'netfx46-managed-ref',
  'netfx461-managed-ref',
  'netfx462-managed-ref',
  'netfx47-managed-ref',
  'netfx471-managed-ref',
  'netfx472-managed-ref',
  'netfx48-managed-ref',
]

const coreClrReferenceSets = [
  ['netcoreapp2.0-ref', 'NETCOREAPP20_REFERENCE_VERSION', 'NETCOREAPP20', 'netcoreapp20', 'NETCOREAPP20_REFERENCE_SOURCE_URI'],
  ['netcoreapp2.1-ref', 'NETCOREAPP21_REFERENCE_VERSION', 'NETCOREAPP21', 'netcoreapp21', 'NETCOREAPP21_REFERENCE_SOURCE_URI'],
  ['netcoreapp2.2-ref', 'NETCOREAPP22_REFERENCE_VERSION', 'NETCOREAPP22', 'netcoreapp22', 'NETCOREAPP22_REFERENCE_SOURCE_URI'],
  ['netcoreapp3.0-ref', 'NETCOREAPP30_REFERENCE_VERSION', 'NETCOREAPP30', 'netcoreapp30', 'NETCOREAPP30_REFERENCE_SOURCE_URI'],
  ['netcoreapp3.1-ref', 'NETCOREAPP31_REFERENCE_VERSION', 'NETCOREAPP31', 'netcoreapp31', 'NETCOREAPP31_REFERENCE_SOURCE_URI'],
  ['net5-ref', 'NET5_REFERENCE_VERSION', 'NET5', 'net5', 'NET5_REFERENCE_SOURCE_URI'],
  ['net6-ref', 'NET6_REFERENCE_VERSION', 'NET6', 'net6', 'NET6_REFERENCE_SOURCE_URI'],
  ['net7-ref', 'NET7_REFERENCE_VERSION', 'NET7', 'net7', 'NET7_REFERENCE_SOURCE_URI'],
  ['net8-ref', 'NET8_REFERENCE_VERSION', 'NET8', 'net8', 'NET8_REFERENCE_SOURCE_URI'],
  ['net9-ref', 'NET9_REFERENCE_VERSION', 'NET9', 'net9', 'NET9_REFERENCE_SOURCE_URI'],
  ['net10-ref', 'NET10_REFERENCE_PACK_VERSION', 'NET10', 'net10', 'NET10_REFERENCE_URL'],
  ['net11-preview-ref', 'NET11_REFERENCE_VERSION', 'NET11', 'net11', 'NET11_REFERENCE_URL'],
]

const expectedComponentLabels = new Map([
  ['operator-wine-coreclr', [
    'wine-coreclr-userspace.version',
    'wine-coreclr-userspace.digest',
    'wine-coreclr-userspace.source-uri',
  ]],
  ['runtime-dotnet10', [
    'dotnet-10-linux-x64.version',
    'dotnet-10-linux-x64.commit',
    'dotnet-10-linux-x64.source-uri',
    ...jitProfilerSourceLabels,
  ]],
  ['runtime-dotnet11', [
    'dotnet-11-preview-linux-x64.version',
    'dotnet-11-preview-linux-x64.commit',
    'dotnet-11-preview-linux-x64.source-uri',
    ...jitProfilerSourceLabels,
  ]],
  ['runtime-dotnet-matrix-candidate', [
    'runtime-matrix.profile-id',
    'runtime-matrix.version',
    'runtime-matrix.commit',
    'runtime-matrix.source-uri',
  ]],
  ['runtime-mono-matrix-candidate', [
    'runtime-matrix.profile-id',
    'runtime-matrix.version',
    'runtime-matrix.digest',
    'runtime-matrix.source-uri',
  ]],
  ['runtime-wine-dotnet-matrix-candidate', [
    'runtime-matrix.profile-id',
    'runtime-matrix.version',
    'runtime-matrix.commit',
    'runtime-matrix.jit-commit',
    'runtime-matrix.source-uri',
  ]],
  ['runtime-const-generics', [
    'const-generics-linux-x64.version',
    'const-generics-linux-x64.commit',
    'const-generics-linux-x64.source-uri',
    'const-generics-runtime-source.version',
    'const-generics-runtime-source.commit',
    'const-generics-runtime-source.digest',
    'const-generics-runtime-source.source-uri',
    'const-generics-ref.version',
    'const-generics-ref.commit',
    'const-generics-ref.digest',
    'const-generics-ref.source-uri',
    'const-generics-versiontools.version',
    'const-generics-versiontools.digest',
    'const-generics-versiontools.source-uri',
    ...jitProfilerSourceLabels,
  ]],
  ['runtime-wine-netfx48', [
    'wine-netfx48-linux-x64.version',
    'wine-netfx48-linux-x64.digest',
    'wine-netfx48-linux-x64.source-uri',
    'msvc-cppcli-netfx48.version',
    'msvc-cppcli-netfx48.digest',
    'msvc-cppcli-netfx48.source-uri',
    'msvc-wine-source.version',
    'msvc-wine-source.commit',
    'msvc-wine-source.digest',
    'msvc-wine-source.source-uri',
    'netfx48-ref.version',
    'netfx48-ref.digest',
    'netfx48-ref.source-uri',
  ]],
  ['jsharp-wine-base', [
    'jsharp20.version',
    'jsharp20.digest',
    'jsharp20.source-uri',
    'vjc-jsharp20.version',
  ]],
  ['runtime-wine-jsharp20', [
    'jsharp20.version',
    'jsharp20.digest',
    'jsharp20.source-uri',
    'vjc-jsharp20.version',
    'wine-jsharp20-linux-x64.version',
    'wine-jsharp20-linux-x64.digest',
    'wine-jsharp20-linux-x64.source-uri',
  ]],
  ['worker-roslyn-stable', ['roslyn-stable.version', 'roslyn-stable.source-uri']],
  ['worker-roslyn-netfx48', [
    'roslyn-stable-netfx48.version',
    'roslyn-stable-netfx48.source-uri',
    'roslyn-stable.version',
    'roslyn-stable.source-uri',
    'netfx48-managed-ref.version',
    'netfx48-managed-ref.source-uri',
  ]],
  ['worker-roslyn-main', [
    'roslyn-main.version',
    'roslyn-main.commit',
    'roslyn-main.digest',
    'roslyn-main.source-uri',
  ]],
  ['worker-roslyn-const-generics', [
    'roslyn-const-generics.version',
    'roslyn-const-generics.commit',
    'roslyn-const-generics.digest',
    'roslyn-const-generics.source-uri',
    'const-generics-roslyn-source.version',
    'const-generics-roslyn-source.commit',
    'const-generics-roslyn-source.digest',
    'const-generics-roslyn-source.source-uri',
  ]],
  ['worker-fsharp', [
    'fsharp-stable.version',
    'fsharp-stable.source-uri',
    'fsharp-core.version',
    'fsharp-core.source-uri',
  ]],
  ['worker-gsharp', [
    'gsharp-stable.version',
    'gsharp-stable.commit',
    'gsharp-stable.digest',
    'gsharp-stable.source-uri',
    'gsharp-source.version',
    'gsharp-source.commit',
    'gsharp-source.digest',
    'gsharp-source.source-uri',
    'gsharp-legacy-0.3.8.version',
    'gsharp-legacy-0.3.8.commit',
    'gsharp-legacy-0.3.8.digest',
    'gsharp-legacy-0.3.8.source-uri',
    'gsharp-legacy-0.3.8-source.version',
    'gsharp-legacy-0.3.8-source.commit',
    'gsharp-legacy-0.3.8-source.digest',
    'gsharp-legacy-0.3.8-source.source-uri',
  ]],
  ['worker-peachpie', [
    'peachpie-stable.version',
    'peachpie-stable.commit',
    'peachpie-stable.package-content-hash',
    'peachpie-stable.source-uri',
    'peachpie-runtime.version',
    'peachpie-runtime.commit',
    'peachpie-runtime.package-content-hash',
    'peachpie-runtime.source-uri',
    'peachpie-library.version',
    'peachpie-library.commit',
    'peachpie-library.package-content-hash',
    'peachpie-library.source-uri',
  ]],
  ['worker-cppcli', [
    'msvc-cppcli-netfx48.version',
    'msvc-cppcli-netfx48.digest',
    'msvc-cppcli-netfx48.source-uri',
    'msvc-wine-source.version',
    'msvc-wine-source.commit',
    'msvc-wine-source.digest',
    'msvc-wine-source.source-uri',
    'netfx48-ref.version',
    'netfx48-ref.digest',
    'netfx48-ref.source-uri',
  ]],
  ['worker-jsharp', [
    'jsharp20.version',
    'jsharp20.digest',
    'jsharp20.source-uri',
    'vjc-jsharp20.version',
    'jsharp20-ref.version',
    'jsharp20-ref.digest',
    'jsharp20-ref.source-uri',
  ]],
  ['worker-il', [
    'mobius-ilasm-stable.version',
    'mobius-ilasm-stable.source-uri',
    'ilsense.version',
    'ilsense.commit',
    'ilsense.digest',
    'ilsense.source-uri',
    'ilsense-source.version',
    'ilsense-source.commit',
    'ilsense-source.digest',
    'ilsense-source.source-uri',
  ]],
  ['worker-minilang', ['minilang-stable.version']],
  ['worker-artifacts-default', [
    'artifacts-default.version',
    'ilspy.version',
    'ilspy.source-uri',
    'dotnet-ilverify.version',
    'dotnet-ilverify.source-uri',
    'netfx48-managed-ref.version',
    'netfx48-managed-ref.source-uri',
    'jsharp20-ref.version',
    'jsharp20-ref.digest',
    'jsharp20-ref.source-uri',
  ]],
  ['worker-artifacts-const-generics', [
    'artifacts-const-generics.version',
    'artifacts-const-generics.commit',
    'artifacts-const-generics.digest',
    'artifacts-const-generics.source-uri',
    'const-generics-ilspy-source.version',
    'const-generics-ilspy-source.commit',
    'const-generics-ilspy-source.digest',
    'const-generics-ilspy-source.source-uri',
    'const-generics-versiontools.version',
    'const-generics-versiontools.digest',
    'const-generics-versiontools.source-uri',
  ]],
  ['worker-artifacts-il-assembler', ['il-assembler.version']],
])

const versionToolsConsumers = new Map([
  ['runtime-const-generics', 'Dockerfile.runtime-const-generics'],
  ['worker-artifacts-const-generics', 'Dockerfile.worker-artifacts-const-generics'],
])

const failures = []
const manifest = readJson(manifestPath)
const winePackageManifest = readJson(winePackageManifestPath)
const bake = fs.readFileSync(bakePath, 'utf8')
const candidateBake = fs.readFileSync(candidateBakePath, 'utf8')

validateManifest(manifest)
validateWinePackageManifest(winePackageManifest)
validateBake(bake, candidateBake)
validateDockerfiles()

if (failures.length > 0) {
  for (const failure of failures) console.error(`bake input error: ${failure}`)
  process.exit(1);
}

console.log(`Validated ${manifest.images.length} digest-pinned base images, ${variableNames(bake).length} production Bake variables, ${variableNames(candidateBake).length} isolated candidate variables, and Dockerfile build inputs.`)

function validateManifest(document) {
  if (document.schemaVersion !== 1 || !Array.isArray(document.images)) return;

  const ids = new Set();
  const variables = new Set();
  const imageReference = /^[^@\s]+@sha256:[0-9a-f]{64}$/
  for (const image of document.images) {
    if (ids.has(image.id)) failures.push(`profiles/base-images.json has duplicate id '${image.id}'`)
    if (variables.has(image.bakeVariable)) {
      failures.push(`profiles/base-images.json has duplicate bakeVariable '${image.bakeVariable}'`)
    }
    ids.add(image.id);
    variables.add(image.bakeVariable);
    if (!imageReference.test(image.reference)) {
      failures.push(`base image '${image.id}' must use repository@sha256:<64 lowercase hex>`)
    }
  }

  if (ids.size !== expectedBaseImages.size) {
    failures.push(`profiles/base-images.json must contain exactly ${expectedBaseImages.size} base image roles`)
  }
  for (const [id, bakeVariable] of expectedBaseImages) {
    const image = document.images.find(candidate => candidate.id === id);
    if (image === undefined) failures.push(`profiles/base-images.json is missing '${id}'`)
    else if (image.bakeVariable !== bakeVariable) {
      failures.push(`base image '${id}' must export '${bakeVariable}'`)
    }
  }
}

function validateWinePackageManifest(document) {
  const expectedResolvedHash = 'sha256:fa83c245764fc09102029b249f5149a48baeda53a40c0432de973ebe09e39dee'
  const expectedDirectPackages = new Map([
    ['wine', ['9.0~repack-4build3', 'all']],
    ['wine64', ['9.0~repack-4build3', 'amd64']],
    ['fonts-wine', ['9.0~repack-4build3', 'all']],
    ['xvfb', ['2:21.1.12-1ubuntu1.6', 'amd64']],
  ])
  if (document.schemaVersion !== 1 ||
      document.platform !== 'linux/amd64' ||
      document.baseImageId !== 'dotnet-runtime-deps' ||
      document.component?.id !== 'wine-coreclr-userspace' ||
      document.component?.kind !== 'runtime-dependency' ||
      document.component?.sourceUri !== 'https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/') {
    failures.push('profiles/runtime-wine-packages.json must identify the source-controlled Linux Wine userspace')
    return
  }
  const expectedSuites = new Map([
    ['20260810T000000Z\0noble', ['cdb2f31d809f589719a53c6ad15f255b27569c4059542ada282aaa21b8e164b0', 255850]],
    ['20260810T000000Z\0noble-updates', ['ef81441269d3a8bdd8cdfe9095de7deb7f1af70d42191f61f1af3c8fb72cfb32', 126125]],
    ['20260810T000000Z\0noble-security', ['3cfb1c8d7499c0bac1bfbe1e32675d200f0ca74b18afc4248c45325a073d0fd0', 126127]],
    ['20260610T000000Z\0noble-updates', ['f51355c88d0b337b45cede930d215a56f806b7c9339e95487b6600ea02c728ce', 126125]],
  ])
  const archiveIndexes = new Set()
  let archiveSuiteCount = 0
  if (!Array.isArray(document.archiveSnapshots) || document.archiveSnapshots.length !== 2) {
    failures.push('Wine userspace must pin its exact Ubuntu installation and base-image evidence snapshot origins')
  }
  else {
    for (const snapshot of document.archiveSnapshots) {
      if (snapshot.uri !== `https://snapshot.ubuntu.com/ubuntu/${snapshot.id}/` || !Array.isArray(snapshot.suites)) {
        failures.push(`Wine archive snapshot '${snapshot.id}' has an invalid immutable URI or suite list`)
        continue
      }
      for (const suite of snapshot.suites) {
        archiveSuiteCount++
        const expected = expectedSuites.get(`${snapshot.id}\0${suite.name}`)
        if (expected === undefined || suite.inReleaseSha256 !== expected[0] ||
            suite.inReleaseSizeBytes !== expected[1] ||
            suite.signingKeyFingerprint !== 'F6ECB3762474EDA9D21B7022871920D1991BC93C' ||
            !Array.isArray(suite.indexes) || suite.indexes.length !== 8) {
          failures.push(`Wine archive suite '${snapshot.id}/${suite.name}' has invalid signed metadata`)
          continue
        }
        for (const index of suite.indexes) {
          const identity = `${snapshot.id}\0${suite.name}\0${index.kind}\0${index.component}\0${index.path}`
          if (archiveIndexes.has(identity) || !/^[0-9a-f]{64}$/.test(index.sha256) ||
              !Number.isSafeInteger(index.sizeBytes) || index.sizeBytes <= 0) {
            failures.push(`Wine archive index '${identity}' has an invalid or duplicate identity`)
          }
          archiveIndexes.add(identity)
        }
      }
    }
    if (archiveSuiteCount !== 4 || archiveIndexes.size !== 32) {
      failures.push('Wine userspace must retain exactly four signed suites and 32 binary/source indexes')
    }
  }
  if (!Array.isArray(document.directPackages) || document.directPackages.length !== 4) {
    failures.push('profiles/runtime-wine-packages.json must lock exactly four direct Wine/Xvfb packages')
  }
  else {
    const actualDirectPackages = new Set()
    for (const item of document.directPackages) {
      actualDirectPackages.add(item.name)
      const expected = expectedDirectPackages.get(item.name)
      if (expected === undefined || item.version !== expected[0] || item.architecture !== expected[1]) {
        failures.push(`profiles/runtime-wine-packages.json has an unexpected direct package '${item.name}@${item.version}'`)
      }
      if (!/^[0-9a-f]{64}$/.test(item.sha256) || !/^pool\//.test(item.path)) {
        failures.push(`Wine direct package '${item.name}' must have a source-controlled pool path and SHA-256`)
      }
      if (!['LGPL-2.1+', 'MIT'].includes(item.license) ||
          !/^[0-9a-f]{64}$/.test(item.sourceSha256) ||
          !Number.isInteger(item.sourceSizeBytes) || item.sourceSizeBytes <= 0) {
        failures.push(`Wine direct package '${item.name}' must retain its source license and identity`)
      }
    }
    for (const id of expectedDirectPackages.keys()) {
      if (!actualDirectPackages.has(id)) failures.push(`Wine direct package '${id}' is missing`)
    }
    const xvfb = document.directPackages.find(item => item.name === 'xvfb')
    if (xvfb?.path !== 'pool/universe/x/xorg-server/xvfb_21.1.12-1ubuntu1.6_amd64.deb' ||
        xvfb.sourceUri !== 'https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/pool/main/x/xorg-server/xorg-server_21.1.12-1ubuntu1.6.dsc') {
      failures.push('Xvfb must retain its archive binary path and separate Xorg source-package URI')
    }
  }
  const resolved = document.resolvedPackages
  if (!Array.isArray(resolved) || resolved.length !== 228) {
    failures.push('profiles/runtime-wine-packages.json must lock exactly 228 resolved packages')
  }
  else {
    const rendered = resolved.map(item => `${item.name}=${item.version}\n`).join('')
    const hash = `sha256:${crypto.createHash('sha256').update(rendered, 'utf8').digest('hex')}`
    if (hash !== expectedResolvedHash || document.resolvedPackageListSha256 !== expectedResolvedHash) {
      failures.push('Wine resolved package closure must match its canonical 228-line SHA-256')
    }
    const names = resolved.map(item => item.name)
    if (names.some((name, index) => index > 0 && names[index - 1] >= name)) {
      failures.push('Wine resolved packages must be sorted ordinal by package name')
    }
    if (names.some(name => name.endsWith(':i386'))) {
      failures.push('Wine resolved package closure must not contain i386 packages')
    }
    const sources = new Set((document.sourcePackages ?? []).map(item => `${item.name}\0${item.version}`))
    const copyrightPaths = new Set()
    for (const item of resolved) {
      const binaryIndex = `${item.archiveSnapshotId}\0${item.archiveSuite}\0binary\0${item.archiveComponent}\0${item.archiveIndexPath}`
      if (!archiveIndexes.has(binaryIndex) || !sources.has(`${item.sourcePackage}\0${item.sourceVersion}`) ||
          !/^[0-9a-f]{64}$/.test(item.sha256) || !/^[0-9a-f]{64}$/.test(item.copyrightSha256) ||
          !/^\/usr\/share\/doc\/[A-Za-z0-9][A-Za-z0-9.+_-]*\/copyright$/.test(item.copyrightPath)) {
        failures.push(`Wine binary package '${item.name}' lacks signed binary/source/notice evidence`)
      }
      copyrightPaths.add(item.copyrightPath)
    }
    if (copyrightPaths.size !== 225 ||
        resolved.find(item => item.name === 'openssl')?.copyrightPath !== '/usr/share/doc/openssl/copyright') {
      failures.push('Wine copyright closure must contain the canonical 225-entry set including OpenSSL')
    }
  }
  const sourcePackages = document.sourcePackages
  if (!Array.isArray(sourcePackages) || sourcePackages.length !== 162) {
    failures.push('Wine userspace must retain exactly 162 source packages')
  }
  else {
    const sourceFiles = sourcePackages.flatMap(source => source.files ?? [])
    const sourceBytes = sourceFiles.reduce((total, file) => total + file.sizeBytes, 0)
    if (sourceFiles.length !== 526 || sourceBytes !== 840446201 ||
        sourcePackages.some(source => !source.files.some(file => file.path.endsWith('.dsc')))) {
      failures.push('Wine userspace source closure must contain 526 files, one descriptor per source, and 840446201 bytes')
    }
  }
  const offer = document.sourceOffer
  if (offer?.package !== 'wine' ||
      offer.baseUri !== 'https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/pool/universe/w/wine/' ||
      offer.version !== '9.0~repack-4build3' ||
      offer.license !== 'LGPL-2.1+' ||
      !Array.isArray(offer.files) || offer.files.length !== 3 ||
      offer.files.some(file => !/^[0-9a-f]{64}$/.test(file.sha256) || !Number.isInteger(file.sizeBytes))) {
    failures.push('Wine LGPL source offer must retain its three exact source files')
  }
  if (document.noticeArchive?.imagePath !== '/usr/local/share/sharplabnext/wine-coreclr-copyright-notices.tar' ||
      document.noticeArchive?.sha256 !== '3fcd04a992da99ac8fb08d3b7aa5a3ac29f28d9de221f7bc37c455c431e27f8d' ||
      document.noticeArchive?.sizeBytes !== 2887680 || document.noticeArchive?.entryCount !== 225) {
    failures.push('Wine notice archive must bind the deterministic 225-entry USTAR identity')
  }
  if (/archive\.ubuntu\.com|blob\/main/.test(JSON.stringify(document))) {
    failures.push('Wine userspace must not contain mutable Ubuntu or Git branch URLs')
  }
}

function validateBake(source, candidateSource) {
  const blocks = variableBlocks(source)
  const deferredImageInputs = new Map([
    ['CPPCLI_PREPARED_BASE_IMAGE', 2],
    ['JSHARP_TOOLCHAIN_IMAGE', 2],
  ])
  for (const [name, body] of blocks) {
    if (!/^\s*default\s*=\s*""\s*$/m.test(body)) {
      failures.push(`Bake variable '${name}' must have an empty default`)
    }
    if (deferredImageInputs.has(name)) {
      const uses = source.match(new RegExp(`deferred_image\\(${name}\\)`, 'g')) ?? []
      const expectedUses = deferredImageInputs.get(name)
      if (uses.length !== expectedUses || source.includes(`required(${name})`)) {
        failures.push(
          `Bake-generated image input '${name}' must have exactly ${expectedUses} deferred consumers`,
        )
      }
    } else if (!source.includes(`required(${name})`)) {
      failures.push(`Bake variable '${name}' is not consumed through required()`)
    }
  }

  for (const bakeVariable of expectedBaseImages.values()) {
    if (!blocks.has(bakeVariable)) failures.push(`eng/bake.hcl is missing variable '${bakeVariable}'`)
  }

  if (!source.includes('function "required"')) {
    failures.push('eng/bake.hcl must define the required() fail-closed helper')
  }
  if (!source.includes(
    'function "deferred_image" {\n  params = [value]\n  result = value != "" ? value : "scratch"\n}',
  )) {
    failures.push('eng/bake.hcl must define the exact deferred generated-image helper')
  }
  if (/\b[0-9a-f]{40}\b/.test(source) || /@sha256:[0-9a-f]{64}/.test(source)) {
    failures.push('eng/bake.hcl must not contain source commits or base image digests')
  }
  if (/CONST_GENERICS_[A-Z0-9_]*TREE|CONST_GENERICS_ILSPY_LEGACY_METADATA_SHA256/.test(source)) {
    failures.push('eng/bake.hcl must not expose observed tree or legacy DLL hashes as maintained inputs')
  }

  const productionTargets = namedBlocks(source, 'target')
  const candidateTargets = namedBlocks(candidateSource, 'target')
  const targets = new Map([...productionTargets, ...candidateTargets])
  const defaultGroup = namedBlocks(source, 'group').get('default')
  const wineOperator = productionTargets.get('operator-wine-coreclr')
  if (wineOperator === undefined) {
    failures.push("eng/bake.hcl is missing target 'operator-wine-coreclr'")
  }
  else {
    for (const requiredText of [
      'context = "."',
      'platforms = ["linux/amd64"]',
      'output = ["type=docker,rewrite-timestamp=true,unpack=false"]',
      'attest = ["type=provenance,disabled=true"]',
      'dockerfile = "deploy/docker/Dockerfile.operator-wine-coreclr"',
      'target = "final"',
      'SOURCE_DATE_EPOCH = unix_seconds(required(SOURCE_DATE_EPOCH))',
      'RUNTIME_DEPS_IMAGE = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)',
      'SOURCE_REVISION = required(SOURCE_REVISION)',
      'WINE_CORECLR_USERSPACE_VERSION = required(WINE_CORECLR_USERSPACE_VERSION)',
      'WINE_CORECLR_USERSPACE_DIGEST = required(WINE_CORECLR_USERSPACE_DIGEST)',
      'WINE_CORECLR_USERSPACE_SOURCE_URI = required(WINE_CORECLR_USERSPACE_SOURCE_URI)',
      'OPERATOR_SOURCE_CONTEXT = required(OPERATOR_SOURCE_CONTEXT)',
      'OPERATOR_PROMOTION_ELIGIBLE = required(OPERATOR_PROMOTION_ELIGIBLE)',
      'OPERATOR_DEVELOPMENT_ONLY = required(OPERATOR_DEVELOPMENT_ONLY)',
      '"org.opencontainers.image.version" = "wine-9.0-noble-amd64"',
      '"org.opencontainers.image.revision" = required(SOURCE_REVISION)',
      '"org.opencontainers.image.source" = "https://github.com/sharplabnext/SharpLabNext"',
      '"io.sharplabnext.source.revision" = required(SOURCE_REVISION)',
      '"io.sharplabnext.base-image.dotnet-runtime-deps" = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)',
      '"io.sharplabnext.source.context" = required(OPERATOR_SOURCE_CONTEXT)',
      '"io.sharplabnext.development-only" = required(OPERATOR_DEVELOPMENT_ONLY)',
      '"com.sharplabnext.operator.promotion-eligible" = required(OPERATOR_PROMOTION_ELIGIBLE)',
      'operator-wine-coreclr:${required(RELEASE_ID)}',
    ]) {
      if (!wineOperator.includes(requiredText)) {
        failures.push(`operator-wine-coreclr is missing '${requiredText}'`)
      }
    }
    if (wineOperator.includes('inherits = ["common"]')) {
      failures.push('operator-wine-coreclr must not inherit unrelated common base-image inputs')
    }
  }
  if (defaultGroup?.includes('operator-wine-coreclr')) {
    failures.push('operator-wine-coreclr must not be a default Bake target')
  }
  const common = productionTargets.get('common')
  if (common === undefined) {
    failures.push("eng/bake.hcl is missing target 'common'")
  }
  else {
    if (!common.includes('output = ["type=docker,rewrite-timestamp=true,unpack=false"]')) {
      failures.push('Bake common target must use the timestamp-rewriting Docker exporter without eager unpack')
    }
    if (!common.includes('SOURCE_DATE_EPOCH = unix_seconds(required(SOURCE_DATE_EPOCH))')) {
      failures.push('Bake common target must pass SOURCE_DATE_EPOCH as a fail-closed BuildKit build argument')
    }
    if (!common.includes('attest = ["type=provenance,disabled=true"]')) {
      failures.push('Bake common target must disable BuildKit invocation provenance')
    }
  }
  if (source.includes('unpack=true')) {
    failures.push('eng/bake.hcl must not enable Docker exporter unpack mode')
  }
  if (!source.includes('function "unix_seconds"') || !source.includes('regex("^[0-9]+$", value)')) {
    failures.push('eng/bake.hcl must validate SOURCE_DATE_EPOCH as Unix seconds')
  }
  validateCandidateBakeBoundary(source, candidateSource, candidateTargets)
  for (const [target, labels] of expectedComponentLabels) {
    const body = targets.get(target)
    if (body === undefined) {
      failures.push(`eng/bake.hcl is missing target '${target}'`)
      continue
    }
    for (const label of labels) {
      const key = `io.sharplabnext.component.${label}`
      if (!body.includes(`"${key}"`)) failures.push(`Bake target '${target}' is missing label '${key}'`)
    }
  }

  for (const target of ['worker-roslyn-netfx48', 'service-with-framework-reference-sets']) {
    const body = productionTargets.get(target)
    if (body === undefined) {
      failures.push(`eng/bake.hcl is missing target '${target}'`)
      continue
    }
    for (const referenceSetId of frameworkManagedReferenceSetIds) {
      const label = `"io.sharplabnext.reference-set.${referenceSetId}"`
      if (!body.includes(label)) {
        failures.push(`Bake target '${target}' is missing reference-set label '${referenceSetId}'`)
      }
      const versionLabel = `"io.sharplabnext.component.${referenceSetId}.version"`
      if (!body.includes(versionLabel)) {
        failures.push(`Bake target '${target}' is missing component version label '${referenceSetId}'`)
      }
      const identityLabel = referenceSetId === 'netfx30-managed-ref'
        ? `"io.sharplabnext.component.${referenceSetId}.digest"`
        : `"io.sharplabnext.component.${referenceSetId}.source-uri"`
      if (!body.includes(identityLabel)) {
        failures.push(`Bake target '${target}' is missing component identity label '${referenceSetId}'`)
      }
    }
  }

  validateCoreClrReferenceSetBakeWiring(productionTargets)

  const matrixCandidate = targets.get('runtime-dotnet-matrix-candidate')
  if (matrixCandidate !== undefined) {
    for (const requiredText of [
      'dockerfile = "deploy/docker/Dockerfile.runtime-dotnet-matrix"',
      'SDK_IMAGE = BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_DEPS_IMAGE = RUNTIME_MATRIX_BASE_IMAGE',
      'DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_VERSION = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION',
      'DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_URL = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL',
      'DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_SHA512 = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512',
      'DOTNET_CHECKED_JIT_VERSION_GENERATION_MODE = RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE',
      '"com.sharplabnext.runtime-candidate" = "true"',
      '"io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version"',
      '"io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.commit"',
      '"io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri"',
      '"io.sharplabnext.runtime.commit"',
      '"io.sharplabnext.jit.commit"',
      '"io.sharplabnext.jit.checked.bootstrap-sdk.version"',
      '"io.sharplabnext.jit.checked.bootstrap-sdk.source-uri"',
      '"io.sharplabnext.jit.checked.bootstrap-sdk.source-sha512"',
      '"io.sharplabnext.jit.checked.version-generation-mode"',
      'runtime-${RUNTIME_MATRIX_PROFILE_ID}:candidate',
      'runtime-${RUNTIME_MATRIX_PROFILE_ID}:${required(RELEASE_ID)}',
    ]) {
      if (!matrixCandidate.includes(requiredText)) {
        failures.push(`runtime-dotnet-matrix-candidate is missing '${requiredText}'`)
      }
    }
  }
  else {
    failures.push("eng/bake.runtime-candidates.hcl is missing target 'runtime-dotnet-matrix-candidate'")
  }

  for (const input of [
    'WINE_CORECLR_OPERATOR_RECEIPT_SHA256',
    'WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID',
    'WINE_CORECLR_OPERATOR_REFERENCE',
    'WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE',
    'WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG',
  ]) {
    if (candidateSource.includes(`required(${input})`)) {
      failures.push(
        `eng/bake.runtime-candidates.hcl must not eagerly require '${input}'; ` +
        'Buildx evaluates unselected Wine targets while building Linux candidates',
      )
    }
  }

  for (const [targetName, dockerfile, markers] of [
    ['runtime-mono-matrix-candidate', 'deploy/docker/Dockerfile.runtime-mono-matrix', [
      'SDK_IMAGE = BASE_DOTNET_SDK_IMAGE',
      'MONO_IMAGE = RUNTIME_MATRIX_MONO_IMAGE',
      'CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE',
      'RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST',
      'RUNTIME_COMPONENT_SOURCE_URI = RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    ]],
    ['runtime-wine-dotnet-matrix-candidate', 'deploy/docker/Dockerfile.runtime-wine-dotnet-matrix', [
      'SDK_IMAGE = BASE_DOTNET_SDK_IMAGE',
      'WINE_IMAGE = WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG != "" ? WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG : RUNTIME_MATRIX_WINE_IMAGE',
      'WINE_IDENTITY = RUNTIME_MATRIX_WINE_IMAGE',
      'CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE',
    ]],
    ['runtime-wine-framework-matrix-candidate', 'deploy/docker/Dockerfile.runtime-wine-framework-matrix', [
      'SDK_IMAGE = BASE_DOTNET_SDK_IMAGE',
      'WINE_IMAGE = RUNTIME_MATRIX_WINE_IMAGE',
      'CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE',
      'RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST',
      'RUNTIME_COMPONENT_SOURCE_URI = RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    ]],
    ['runtime-wine-framework-matrix-shared-candidate', 'deploy/docker/Dockerfile.runtime-wine-framework-matrix-shared', [
      'SDK_IMAGE = BASE_DOTNET_SDK_IMAGE',
      'PARENT_IMAGE = RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
      'FRAMEWORK_SOURCE_REVISION = RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION',
      'WINE_IMAGE = RUNTIME_MATRIX_WINE_IMAGE',
      'CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE',
      'FRAMEWORK_MATRIX_INPUT_SHA256 = RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256',
      'FRAMEWORK_MATRIX_SOURCE_URI = RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI',
      'FRAMEWORK_ROW_OPERATOR_IMAGE = RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
      'FRAMEWORK_ROW_DIGEST = RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST',
    ]],
    ['runtime-mono-wine-matrix-candidate', 'deploy/docker/Dockerfile.runtime-mono-wine-matrix', [
      'SDK_IMAGE = BASE_DOTNET_SDK_IMAGE',
      'MONO_WINE_IMAGE = RUNTIME_MATRIX_MONO_WINE_IMAGE',
      'CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE',
      'RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST',
      'RUNTIME_COMPONENT_SOURCE_URI = RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    ]],
  ]) {
    const candidate = targets.get(targetName)
    if (candidate === undefined) {
      failures.push(`eng/bake.runtime-candidates.hcl is missing target '${targetName}'`)
      continue
    }
    const isCombined = targetName === 'runtime-mono-wine-matrix-candidate'
    const requiredTexts = isCombined
      ? [
          `dockerfile = "${dockerfile}"`,
          ...markers,
          '"com.sharplabnext.runtime-candidate" = "true"',
          '"io.sharplabnext.runtime.matrix.profile-group" = "mono-wine-matrix"',
          '"io.sharplabnext.runtime.matrix.digest"',
          '"io.sharplabnext.runtime.matrix.source-uri"',
          '"io.sharplabnext.control-image"',
          '"io.sharplabnext.operator-image.mono-wine"',
          '"io.sharplabnext.base-image.dotnet-sdk"',
          'runtime-mono-wine-matrix:candidate',
          'runtime-mono-wine-matrix:${required(RELEASE_ID)}',
        ]
      : [
          `dockerfile = "${dockerfile}"`,
          ...markers,
          '"com.sharplabnext.runtime-candidate" = "true"',
          '"io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version"',
          '"io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri"',
          'runtime-${RUNTIME_MATRIX_PROFILE_ID}:candidate',
          'runtime-${RUNTIME_MATRIX_PROFILE_ID}:${required(RELEASE_ID)}',
        ]
    for (const requiredText of requiredTexts) {
      if (!candidate.includes(requiredText)) {
        failures.push(`${targetName} is missing '${requiredText}'`)
      }
    }
    if (targetName === 'runtime-mono-matrix-candidate' ||
        targetName === 'runtime-wine-framework-matrix-candidate' ||
        targetName === 'runtime-wine-framework-matrix-shared-candidate') {
      for (const requiredText of [
        '"io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.digest"',
        '"io.sharplabnext.control-image"',
          targetName === 'runtime-mono-matrix-candidate'
            ? '"io.sharplabnext.operator-image.mono"'
            : '"io.sharplabnext.operator-image.wine"',
        '"io.sharplabnext.base-image.dotnet-sdk"',
      ]) {
        if (!candidate.includes(requiredText)) {
          failures.push(`${targetName} is missing '${requiredText}'`)
        }
      }
      if (targetName === 'runtime-wine-framework-matrix-shared-candidate') {
        for (const requiredText of [
          'PARENT_IMAGE = RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
          'FRAMEWORK_SOURCE_REVISION = RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION',
          'FRAMEWORK_MATRIX_INPUT_SHA256 = RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256',
          'FRAMEWORK_MATRIX_SOURCE_URI = RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI',
          'FRAMEWORK_ROW_OPERATOR_IMAGE = RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
          'FRAMEWORK_ROW_DIGEST = RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST',
          'HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT = RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT',
          '"io.sharplabnext.framework.matrix-parent"',
          '"io.sharplabnext.framework.source-revision"',
          '"io.sharplabnext.framework.matrix-selector"',
          'RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT != "true" ? {',
          '"io.sharplabnext.operator.receipt-sha256" = WINE_CORECLR_OPERATOR_RECEIPT_SHA256',
          '"io.sharplabnext.operator.receipt-key-id" = WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID',
          '"io.sharplabnext.operator.userspace-reference" = WINE_CORECLR_OPERATOR_REFERENCE',
          '"io.sharplabnext.component.wine-coreclr-userspace.version" = WINE_CORECLR_USERSPACE_VERSION',
          '"io.sharplabnext.component.wine-coreclr-userspace.digest" = WINE_CORECLR_USERSPACE_DIGEST',
          '"io.sharplabnext.component.wine-coreclr-userspace.source-uri" = WINE_CORECLR_USERSPACE_SOURCE_URI',
        ]) {
          if (!candidate.includes(requiredText)) {
            failures.push(`${targetName} is missing '${requiredText}'`)
          }
        }
      }
    }
    else if (!isCombined) {
      for (const requiredText of [
        '"io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.commit"',
        '"io.sharplabnext.runtime.commit"',
        '"io.sharplabnext.jit.commit"',
        '"io.sharplabnext.control-image"',
        '"io.sharplabnext.operator-image.wine"',
        '"io.sharplabnext.base-image.dotnet-sdk"',
      ]) {
        if (!candidate.includes(requiredText)) {
          failures.push(`${targetName} is missing '${requiredText}'`)
        }
      }
    }
    for (const requiredText of [
      'CANDIDATE_SOURCE_CONTEXT = RUNTIME_CANDIDATE_SOURCE_CONTEXT',
      'CANDIDATE_PROMOTION_ELIGIBLE = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE',
      '"io.sharplabnext.source.context" = RUNTIME_CANDIDATE_SOURCE_CONTEXT',
      '"com.sharplabnext.runtime-candidate.promotion-eligible" = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE',
    ]) {
      if (!candidate.includes(requiredText)) {
        failures.push(`${targetName} is missing source-context binding '${requiredText}'`)
      }
    }
  }

  for (const target of versionToolsConsumers.keys()) {
    const body = targets.get(target)
    if (body !== undefined &&
        !body.includes('CONST_GENERICS_VERSIONTOOLS_SOURCE_URI = required(CONST_GENERICS_VERSIONTOOLS_SOURCE_URI)')) {
      failures.push(`Bake target '${target}' does not inject the locked VersionTools package source URI`)
    }
  }
}

function validateCoreClrReferenceSetBakeWiring(productionTargets) {
  const fullCoreTarget = productionTargets.get('service-with-roslyn-coreclr-reference-sets')
  const stableTarget = productionTargets.get('worker-roslyn-stable')
  const mainTarget = productionTargets.get('worker-roslyn-main')
  if (fullCoreTarget === undefined) {
    failures.push("eng/bake.hcl is missing target 'service-with-roslyn-coreclr-reference-sets'")
    return
  }
  if (!fullCoreTarget.includes('target = "final-with-roslyn-coreclr-reference-sets"')) {
    failures.push('Roslyn CoreCLR reference target must select final-with-roslyn-coreclr-reference-sets')
  }
  if (stableTarget === undefined ||
      !stableTarget.includes('inherits = ["service-with-roslyn-coreclr-reference-sets"]')) {
    failures.push('worker-roslyn-stable must use the complete Roslyn CoreCLR reference target')
  }
  if (mainTarget === undefined) {
    failures.push("eng/bake.hcl is missing target 'worker-roslyn-main'")
    return
  }

  for (const [referenceSetId, versionVariable, prefix, optionPrefix, urlVariable] of coreClrReferenceSets) {
    for (const target of [fullCoreTarget, mainTarget]) {
      for (const marker of [
        `"io.sharplabnext.component.${referenceSetId}.version" = required(${versionVariable})`,
        `"io.sharplabnext.component.${referenceSetId}.source-uri" = required(${urlVariable})`,
        `"io.sharplabnext.component.${referenceSetId}.source-sha512" = required(${prefix}_REFERENCE_SHA512)`,
        `"io.sharplabnext.reference-set.${referenceSetId}" = required(${prefix}_REFERENCE_PACKAGE_CONTENT_HASH)`,
      ]) {
        if (!target.includes(marker)) {
          failures.push(`Roslyn CoreCLR reference target is missing '${marker}'`)
        }
      }
      for (const marker of [
        `${versionVariable} = required(${versionVariable})`,
        `${urlVariable} = required(${urlVariable})`,
        `${prefix}_REFERENCE_SHA512 = required(${prefix}_REFERENCE_SHA512)`,
        `${prefix}_REFERENCE_PACKAGE_CONTENT_HASH = required(${prefix}_REFERENCE_PACKAGE_CONTENT_HASH)`,
      ]) {
        if (!target.includes(marker)) {
          failures.push(`Roslyn CoreCLR materializer build arguments are missing '${marker}'`)
        }
      }
    }
    for (const [dockerfile, source] of [
      ['Dockerfile.worker', fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.worker'), 'utf8')],
      ['Dockerfile.worker-roslyn-main', fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.worker-roslyn-main'), 'utf8')],
    ]) {
      for (const marker of [
        `ARG ${versionVariable}`,
        `ARG ${urlVariable}`,
        `ARG ${prefix}_REFERENCE_SHA512`,
        `ARG ${prefix}_REFERENCE_PACKAGE_CONTENT_HASH`,
        `--${optionPrefix}-version`,
        `--${optionPrefix}-url`,
        `--${optionPrefix}-sha512`,
        `--${optionPrefix}-content-hash`,
      ]) {
        if (!source.includes(marker)) {
          failures.push(`${dockerfile} is missing full CoreCLR identity binding '${marker}'`)
        }
      }
    }
  }

  for (const targetName of ['worker-fsharp', 'worker-il', 'worker-minilang', 'worker-artifacts-jsil']) {
    const target = productionTargets.get(targetName)
    if (target?.includes('service-with-roslyn-coreclr-reference-sets')) {
      failures.push(`${targetName} must not inherit the Roslyn-only CoreCLR reference closure`)
    }
  }

  for (const [dockerfile, source] of [
    ['Dockerfile.worker', fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.worker'), 'utf8')],
    ['Dockerfile.worker-roslyn-main', fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.worker-roslyn-main'), 'utf8')],
  ]) {
    for (const marker of [
      'src/RuntimeApi/SharpLabNext.Runtime/SharpLabNext.Runtime.csproj',
      '--framework netstandard2.1',
      '/app/sharplab-runtime-netstandard21/SharpLab.Runtime.dll',
    ]) {
      if (!source.includes(marker)) {
        failures.push(`${dockerfile} must materialize and use the netstandard2.1 SharpLab.Runtime product`)
      }
    }
  }
}

function validateCandidateBakeBoundary(productionSource, candidateSource, candidateTargets) {
  const expectedTargets = new Set([
    'runtime-dotnet-matrix-candidate',
    'runtime-mono-matrix-candidate',
    'runtime-mono-wine-matrix-candidate',
    'runtime-wine-dotnet-matrix-candidate',
    'runtime-wine-framework-matrix-candidate',
    'runtime-wine-framework-matrix-shared-candidate',
  ])
  const expectedVariables = new Set([
    'RUNTIME_MATRIX_PROFILE_ID',
    'RUNTIME_MATRIX_RUNTIME_VERSION',
    'RUNTIME_MATRIX_RUNTIME_COMMIT',
    'RUNTIME_MATRIX_JIT_COMMIT',
    'RUNTIME_MATRIX_RUNTIME_URL',
    'RUNTIME_MATRIX_RUNTIME_SHA512',
    'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    'RUNTIME_MATRIX_RUNTIME_DIGEST',
    'RUNTIME_MATRIX_BASE_IMAGE',
    'RUNTIME_MATRIX_MONO_IMAGE',
    'RUNTIME_MATRIX_MONO_WINE_IMAGE',
    'RUNTIME_MATRIX_CONTROL_IMAGE',
    'RUNTIME_MATRIX_WINE_IMAGE',
    'RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
    'RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION',
    'RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256',
    'RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI',
    'RUNTIME_MATRIX_FRAMEWORK_TARGET_ID',
    'RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION',
    'RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
    'RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST',
    'RUNTIME_CANDIDATE_SOURCE_CONTEXT',
    'RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE',
    'RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT',
    'WINE_CORECLR_OPERATOR_RECEIPT_SHA256',
    'WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID',
    'WINE_CORECLR_OPERATOR_REFERENCE',
    'WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE',
    'WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG',
    'RUNTIME_MATRIX_WINDOWS_URL',
    'RUNTIME_MATRIX_WINDOWS_SHA512',
    'RUNTIME_MATRIX_CHECKED_JIT_COMMIT',
    'RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL',
    'RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512',
    'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION',
    'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL',
    'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512',
    'RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE',
    'RUNTIME_MATRIX_CHECKED_JIT_CONFIGURATION',
    'RUNTIME_MATRIX_CHECKED_JIT_TARGET_OS',
    'RUNTIME_MATRIX_CHECKED_JIT_ARCHITECTURE',
    'RUNTIME_MATRIX_CHECKED_JIT_BUILD_COMPONENT',
    'RUNTIME_MATRIX_CHECKED_JIT_PGO_MODE',
    'RUNTIME_MATRIX_CHECKED_JIT_COMPILER',
    'RUNTIME_MATRIX_CHECKED_JIT_GENERATOR',
    'RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE',
    'RUNTIME_MATRIX_CHECKED_JIT_SOURCE_MAPPING_KIND',
    'RUNTIME_MATRIX_PROFILER_PROVIDER_ID',
    'RUNTIME_MATRIX_PROFILER_BUILD_IMAGE',
    'RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT',
    'RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI',
    'RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT',
    'RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI',
    'RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND',
  ])

  if (productionSource.includes('RUNTIME_MATRIX_')) {
    failures.push('eng/bake.hcl must not declare or consume candidate-only RUNTIME_MATRIX_* inputs')
  }
  for (const target of expectedTargets) {
    if (namedBlocks(productionSource, 'target').has(target)) {
      failures.push(`eng/bake.hcl must not contain candidate target '${target}'`)
    }
  }
  if (/group\s+"default"/.test(candidateSource)) {
    failures.push('eng/bake.runtime-candidates.hcl must not define a default group')
  }
  if (/required\(RUNTIME_MATRIX_/.test(candidateSource)) {
    failures.push('candidate variables must be validated by the selected-target entry, not eagerly by Bake')
  }

  const candidateVariables = variableBlocks(candidateSource)
  for (const [name, body] of candidateVariables) {
    if (!expectedVariables.has(name)) {
      failures.push(`eng/bake.runtime-candidates.hcl declares unexpected variable '${name}'`)
    }
    if (!/^\s*default\s*=\s*""\s*$/m.test(body)) {
      failures.push(`candidate Bake variable '${name}' must have an empty default`)
    }
  }
  for (const name of expectedVariables) {
    if (!candidateVariables.has(name)) {
      failures.push(`eng/bake.runtime-candidates.hcl is missing variable '${name}'`)
    }
  }
  for (const name of candidateTargets.keys()) {
    if (!expectedTargets.has(name)) {
      failures.push(`eng/bake.runtime-candidates.hcl declares unexpected target '${name}'`)
    }
  }
  for (const name of expectedTargets) {
    if (!candidateTargets.has(name)) {
      failures.push(`eng/bake.runtime-candidates.hcl is missing target '${name}'`)
    }
  }

  const entryPath = path.join(repositoryRoot, 'eng', 'build-runtime-candidate.mjs')
  const entry = fs.readFileSync(entryPath, 'utf8')
  for (const marker of [
    "from './runtime-candidate-input-validation.mjs'",
    "from './committed-source-context.mjs'",
    'const failures = validateCandidateBuildInputs(target, values, effectiveRepositoryRoot, {',
    'candidateCommittedSourceFiles(target, values)',
    'validateCandidateBuildInputs(target, dockerEnvironment, sourceRoot, {',
    '`${target}.context=${sourceRoot}`',
    'cwd: sourceRoot',
    'Git source state changed during Bake',
    'sourceContext.dispose()',
    "'eng/bake.hcl'",
    "'eng/bake.runtime-candidates.hcl'",
    'inspectDockerImage(',
    'validateCandidateImageIdentity(',
  ]) {
    if (!entry.includes(marker)) {
      failures.push(`eng/build-runtime-candidate.mjs is missing '${marker}'`)
    }
  }
  const validationIndex = entry.indexOf(
    'const failures = validateCandidateBuildInputs(target, values, effectiveRepositoryRoot, {',
  )
  const dockerIndex = entry.indexOf("spawn('docker'")
  if (validationIndex < 0 || dockerIndex < 0 || validationIndex > dockerIndex) {
    failures.push('candidate entry must validate selected-target inputs before starting Docker')
  }
  const inspectIndex = entry.indexOf('inspect: reference => inspectDockerImage(')
  const identityIndex = entry.lastIndexOf('validateCandidateImageIdentity(')
  if (inspectIndex < dockerIndex || identityIndex < inspectIndex) {
    failures.push('candidate entry must verify built-image labels after Docker returns successfully')
  }

  const committedContext = fs.readFileSync(path.join(repositoryRoot, 'eng', 'committed-source-context.mjs'), 'utf8')
  for (const marker of [
    "['archive', '--format=tar', '--output', archive, revision]",
    "['--extract', '--file', archive, '--directory', directory]",
    'requiredFiles.length === 0',
    'info.isSymbolicLink()',
    'removeRoot(root, true)',
  ]) {
    if (!committedContext.includes(marker)) {
      failures.push(`eng/committed-source-context.mjs is missing '${marker}'`)
    }
  }

  const wineOperatorEntry = fs.readFileSync(path.join(repositoryRoot, 'eng', 'build-wine-coreclr-operator.mjs'), 'utf8')
  for (const marker of [
    "from './committed-source-context.mjs'",
    "from './release/runtime-promotion-image-binding.mjs'",
    'wineCoreClrOperatorCommittedFiles',
    'committedSourceFiles()',
    'createCommittedSourceContext',
    'validateGitSourceState',
    'Git source state changed during Bake',
    'OPERATOR_SOURCE_CONTEXT',
    'OPERATOR_PROMOTION_ELIGIBLE',
    'OPERATOR_DEVELOPMENT_ONLY',
    'wineCoreClrOperatorExpectedLabels',
    'bindRuntimeCandidateImage',
  ]) {
    if (!wineOperatorEntry.includes(marker)) {
      failures.push(`eng/build-wine-coreclr-operator.mjs is missing '${marker}'`)
    }
  }
}

function validateDockerfiles() {
  const allowedDefaults = new Set(['CONFIGURATION=Release', 'SERVICE_TITLE="SharpLabNext Worker"'])
  const files = discoverDockerfiles(repositoryRoot)

  for (const relativePath of files) {
    const fileName = path.basename(relativePath)
    const source = fs.readFileSync(path.join(repositoryRoot, relativePath), 'utf8')
    const sourceWithoutFrontendDirective = source.replace(/^# syntax=[^\r\n]*(?:\r?\n)?/, '')
    for (const violation of validateDockerfileFrontend(source)) {
      failures.push(`${relativePath} ${violation}`)
    }
    if (!relativePath.startsWith('deploy/docker/')) continue
    for (const violation of findDockerfileStageArgumentScopeViolations(source, 'CONTROL_TFM')) {
      failures.push(
        `${fileName}:${violation.line} uses CONTROL_TFM in stage '${violation.stage}' ` +
        'without redeclaring ARG CONTROL_TFM in that stage')
    }
    for (const match of source.matchAll(/^ARG\s+([^\s]+=[^\r\n]+)$/gm)) {
      if (!allowedDefaults.has(match[1])) {
        failures.push(`${fileName} has a maintained ARG default: ${match[1]}`)
      }
    }
    for (const match of source.matchAll(/^FROM\s+([^\s]+)/gm)) {
      const value = match[1]
      if (!value.startsWith('${') && /[\/:@]/.test(value)) {
        failures.push(`${fileName} uses an external FROM without an injected build argument: ${value}`)
      }
    }
    if (/@sha256:[0-9a-f]{64}/.test(sourceWithoutFrontendDirective) ||
        /\b[0-9a-f]{40}\b/.test(sourceWithoutFrontendDirective)) {
      failures.push(`${fileName} contains a maintained image digest or source commit`)
    }
    if (/CONST_GENERICS_[A-Z0-9_]*TREE|CONST_GENERICS_ILSPY_LEGACY_METADATA_SHA256/.test(source)) {
      failures.push(`${fileName} exposes observed tree or legacy DLL hashes as maintained inputs`)
    }

    if ([...versionToolsConsumers.values()].includes(fileName)) {
      if (!source.includes('ARG CONST_GENERICS_VERSIONTOOLS_SOURCE_URI')) {
        failures.push(`${fileName} does not accept the locked VersionTools package source URI`)
      }
      if (!source.includes('"${CONST_GENERICS_VERSIONTOOLS_SOURCE_URI}"')) {
        failures.push(`${fileName} does not download VersionTools from the locked source URI`)
      }
      if (!source.includes('${CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256}  /tmp/microsoft.dotnet.versiontools.tasks.nupkg')) {
        failures.push(`${fileName} does not verify the downloaded VersionTools package from /tmp`)
      }
      if (/\/root\/\.nuget\/packages\/microsoft\.dotnet\.versiontools\.tasks\/[^\s"]+\.nupkg/.test(source)) {
        failures.push(`${fileName} relies on NuGet global-package cache .nupkg retention`)
      }
    }
  }

  const wineOperator = fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.operator-wine-coreclr'), 'utf8')
  const wineOperatorRelativePath = 'deploy/docker/Dockerfile.operator-wine-coreclr'
  if (!files.includes(wineOperatorRelativePath)) {
    for (const violation of validateDockerfileFrontend(wineOperator)) {
      failures.push(`${wineOperatorRelativePath} ${violation}`)
    }
  }
  for (const requiredText of [
    'ARG RUNTIME_DEPS_IMAGE',
    'FROM ${RUNTIME_DEPS_IMAGE} AS final',
    'COPY profiles/runtime-wine-packages.json',
    'https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/',
    "find /etc/apt \\( -name '*.list' -o -name '*.sources' \\) -delete",
    "test -z \"$(find /etc/apt \\( -name '*.list' -o -name '*.sources' \\) -print -quit)\"",
    "test \"$(find /etc/apt \\( -name '*.list' -o -name '*.sources' \\) -print)\" = '/etc/apt/sources.list.d/sharplabnext-snapshot.sources'",
    "test -z \"$(find /var/lib/apt/lists",
    "snapshot.ubuntu.com_ubuntu_20260810T000000Z_dists_noble_InRelease",
    "snapshot.ubuntu.com_ubuntu_20260810T000000Z_dists_noble-updates_InRelease",
    "snapshot.ubuntu.com_ubuntu_20260810T000000Z_dists_noble-security_InRelease",
    'apt-get download',
    'apt-get install --yes --no-install-recommends',
    'wine=9.0~repack-4build3',
    'wine64=9.0~repack-4build3',
    'fonts-wine=9.0~repack-4build3',
    'xvfb=2:21.1.12-1ubuntu1.6',
    'mv xvfb_2%3a21.1.12-1ubuntu1.6_amd64.deb xvfb_21.1.12-1ubuntu1.6_amd64.deb',
    'sha256sum --check --strict',
    'dpkg-query -W -f=\'${binary:Package}\\t${Version}\\n\'',
    "sort -t $'\\t' -k1,1",
    'sha256:fa83c245764fc09102029b249f5149a48baeda53a40c0432de973ebe09e39dee',
    'test -z "$(dpkg --print-foreign-architectures)"',
    'xvfb-run -a /usr/bin/wineboot-stable --init',
    'WINEARCH=win64',
    'Wine prefix system.reg does not declare win64.',
    'Wine loader is not an ELF64 executable.',
    'org.opencontainers.image.version="wine-9.0-noble-amd64"',
    'io.sharplabnext.operator.wine-version="9.0"',
    'io.sharplabnext.operator.root="${RUNTIME_DEPS_IMAGE}"',
    'io.sharplabnext.component.wine-coreclr-userspace.version="${WINE_CORECLR_USERSPACE_VERSION}"',
    'io.sharplabnext.component.wine-coreclr-userspace.digest="${WINE_CORECLR_USERSPACE_DIGEST}"',
    'io.sharplabnext.component.wine-coreclr-userspace.source-uri="${WINE_CORECLR_USERSPACE_SOURCE_URI}"',
    'ARG OPERATOR_SOURCE_CONTEXT',
    'ARG OPERATOR_PROMOTION_ELIGIBLE',
    'ARG OPERATOR_DEVELOPMENT_ONLY',
    '"${OPERATOR_SOURCE_CONTEXT}:${OPERATOR_PROMOTION_ELIGIBLE}:${OPERATOR_DEVELOPMENT_ONLY}" = "committed:true:false"',
    '"${OPERATOR_SOURCE_CONTEXT}:${OPERATOR_PROMOTION_ELIGIBLE}:${OPERATOR_DEVELOPMENT_ONLY}" = "working-tree-development:false:true"',
    'io.sharplabnext.source.context="${OPERATOR_SOURCE_CONTEXT}"',
    'io.sharplabnext.development-only="${OPERATOR_DEVELOPMENT_ONLY}"',
    'com.sharplabnext.operator.promotion-eligible="${OPERATOR_PROMOTION_ELIGIBLE}"',
    '/usr/share/doc/wine/copyright',
    '/usr/share/doc/xvfb/copyright',
    '/usr/share/doc/openssl/copyright',
    '../libssl3t64/copyright',
    'copyrightSizeBytes',
    'wine-coreclr-copyright-notices.tar',
    'tar --format=ustar',
    '--dereference --hard-dereference',
    'test "$(wc -l < "${copyright_paths}")" -eq 225',
    '/var/cache/apt/archives/*',
  ]) {
    if (!wineOperator.includes(requiredText)) {
      failures.push(`Dockerfile.operator-wine-coreclr is missing '${requiredText}'`)
    }
  }
  if (/WINE_SOURCE_IMAGE|COPY --from=.*wine-source|operator\.wine-source/.test(wineOperator)) {
    failures.push('Dockerfile.operator-wine-coreclr must not consume a historical Wine source image')
  }

  const sourceStatusDockerfiles = new Set([
    'Dockerfile.runtime-dotnet-matrix',
    'Dockerfile.runtime-mono-matrix',
    'Dockerfile.runtime-mono-wine-matrix',
    'Dockerfile.runtime-wine-dotnet-matrix',
    'Dockerfile.runtime-wine-framework-matrix',
    'Dockerfile.runtime-wine-framework-matrix-shared',
  ])
  const candidateRuntimeChecks = new Map([
    ['Dockerfile.operator-wine-framework-matrix', [
      'ARG SOURCE_REVISION',
      '[[ "${SOURCE_REVISION}" =~ ^([0-9a-f]{40}|[0-9a-f]{64})$ ]]',
      'org.opencontainers.image.revision="${SOURCE_REVISION}"',
      'io.sharplabnext.source.revision="${SOURCE_REVISION}"',
      'io.sharplabnext.framework.installer-manifest-sha256="${INSTALLER_MANIFEST_SHA256}"',
      'io.sharplabnext.operator-base="${BASE_IMAGE}"',
      'io.sharplabnext.operator-root="${ROOT_IMAGE}"',
    ]],
    ['Dockerfile.operator-wine-framework-matrix-parent', [
      'FROM framework-row-netfx20 AS framework-tool-source',
      'FROM ${ROOT_IMAGE} AS final',
      'COPY --from=framework-tool-source /usr/ /usr/',
      'ARG VERSION',
      'ARG SOURCE_REVISION',
      'from=framework-matrix-metadata',
      'SHARPLABNEXT_FRAMEWORK_ROW_MOUNTS',
      'matrix-input.json',
      'FRAMEWORK_MATRIX_INPUT_SHA256',
      'assemble-framework-prefix-matrix assemble',
      '--row-prefix-root /run/sharplabnext-framework-rows',
      '--output /opt/sharplabnext',
      'assemble-framework-prefix-matrix verify',
      'test ! -e /run/sharplabnext-framework-matrix-metadata',
      'test ! -e /run/sharplabnext-framework-rows',
      'framework-matrix.json',
      '.wine-prefix-layout.json',
      '.operator-wine-image',
      '.framework-matrix-input-sha256',
      '.framework-matrix-source-uri',
      'io.sharplabnext.framework.matrix-strategy="shared-framework-target-prefix-matrix-v1"',
      'org.opencontainers.image.revision="${SOURCE_REVISION}"',
      'io.sharplabnext.source.revision="${SOURCE_REVISION}"',
      'org.opencontainers.image.version="${VERSION}"',
      'io.sharplabnext.framework.layout-strategy="hardlink-static-runtime-matrix-v1"',
      'io.sharplabnext.framework.dedupe-policy="wine-static-runtime-payload-v1"',
    ]],
    ['Dockerfile.runtime-mono-matrix', [
      'ARG CONTROL_IMAGE',
      'FROM ${CONTROL_IMAGE} AS control-image',
      'FROM mono-source AS mono-runtime-check',
      'src/RuntimeJobs/SharpLabNext.TargetRuntimeRunner/SharpLabNext.TargetRuntimeRunner.csproj',
      '--output /target-runtime-runner',
      'mono --version | grep --fixed-strings --quiet',
      'ldd /usr/bin/mono-sgen',
      'FROM mono-runtime-check AS final',
      'command -v stat >/dev/null',
      'command -v cp >/dev/null',
      'command -v cmp >/dev/null',
      'mono_source=/usr/bin/mono-sgen',
      'mono_destination=/usr/bin/mono',
      'test -f "${mono_source}"',
      'test ! -L "${mono_source}"',
      'test -L "${mono_destination}"',
      'cp --preserve=mode,ownership,timestamps -- "${mono_source}" "${mono_destination}"',
      'test -f "${mono_destination}"',
      'test ! -L "${mono_destination}"',
      'test "$(stat --format=%a "${mono_destination}")" = "${mono_mode}"',
      'test "$(stat --format=%u:%g "${mono_destination}")" = "${mono_owner}"',
      'test "$(stat --format=%y "${mono_destination}")" = "${mono_timestamp}"',
      'test "$(stat --format=%h "${mono_destination}")" -eq 1',
      'cmp --silent "${mono_source}" "${mono_destination}"',
      'COPY --from=control-image /usr/share/dotnet/ /usr/share/dotnet/',
      'COPY --from=publish /target-runtime-runner/ /opt/sharplabnext/',
      'test -s /opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
      'test -s /opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe.config',
      'TMPDIR=/tmp /usr/bin/mono',
      '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe self-test',
      'io.sharplabnext.control-image="${CONTROL_IMAGE}"',
    ]],
    ['Dockerfile.runtime-wine-dotnet-matrix', [
      'ARG CONTROL_IMAGE',
      'FROM ${CONTROL_IMAGE} AS control-image',
      'FROM wine-source AS runtime-base',
      'FROM runtime-base AS preflight',
      'FROM runtime-base AS final',
      'src/RuntimeJobs/SharpLabNext.LegacyJitInspector/SharpLabNext.LegacyJitInspector.csproj',
      '--output /legacy-jit-helper',
      'COPY --from=publish /legacy-jit-helper/ /opt/sharplabnext/',
      'COPY --from=control-image /control-image-preflight /opt/sharplabnext/.control-image-preflight',
      'test -s /opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
      'test -s /opt/sharplabnext/SharpLabNext.LegacyJitInspector.runtimeconfig.json',
      'test -x /usr/lib/wine/wine64',
      'WINELOADER=/usr/lib/wine/wine64',
      'WINESERVER=/usr/lib/wine/wineserver64',
      'CMD ["/usr/lib/wine/wine64", "--version"]',
      'test -r /opt/wine-dotnet/system.reg',
      'index($0, "#arch=") == 1',
      '= win64',
      'od -An -t x1 -j 4 -N 1 /usr/lib/wine/wine64',
      'dpkg --print-foreign-architectures',
      "grep ':i386$'",
      "'Z:\\\\opt\\\\wine-dotnet\\\\drive_c\\\\dotnet\\\\dotnet.exe' --list-runtimes",
      'test "$(find /runtime/shared/Microsoft.NETCore.App -mindepth 1 -maxdepth 1 -type d | wc -l)" -eq 1',
      'io.sharplabnext.control-image="${CONTROL_IMAGE}"',
      'io.sharplabnext.operator-image.wine="${WINE_IDENTITY}"',
    ]],
    ['Dockerfile.runtime-wine-framework-matrix', [
      'ARG CONTROL_IMAGE',
      'FROM ${CONTROL_IMAGE} AS control-image',
      'FROM wine-source AS runtime-base',
      'FROM runtime-base AS preflight',
      'FROM runtime-base AS final',
      'test -x /usr/lib/wine/wine64',
      'WINELOADER=/usr/lib/wine/wine64',
      'WINESERVER=/usr/lib/wine/wineserver64',
      'CMD ["/usr/lib/wine/wine64", "--version"]',
      'test -x /usr/lib/wine/wineserver64',
      'test -d /opt/wine-netfx-clr2/drive_c/windows/Microsoft.NET/Framework64/v2.0.50727',
      'test -d /opt/wine-netfx-clr4/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319',
      'COPY deploy/docker/wine-netfx-framework-preflight.sh',
      'COPY deploy/docker/dedupe-wine-prefixes.py /usr/local/bin/sharplabnext-dedupe-wine-prefixes',
      'chmod 0555 /usr/local/bin/sharplabnext-dedupe-wine-prefixes',
      'test -x /usr/local/bin/sharplabnext-dedupe-wine-prefixes',
      'sharplabnext-wine-netfx-preflight /opt/wine-netfx-clr2',
      'sharplabnext-wine-netfx-preflight /opt/wine-netfx-clr4',
      'sharplabnext-dedupe-wine-prefixes',
      '.wine-prefix-layout.json',
      '--verify',
      'io.sharplabnext.wine-prefix-layout="hardlink-immutable-v1"',
      'test ! -e /usr/lib/x86_64-linux-gnu/wine/i386-windows',
      'src/RuntimeJobs/SharpLabNext.TargetRuntimeRunner/SharpLabNext.TargetRuntimeRunner.csproj',
      '--output /target-runtime-runner',
      'src/RuntimeJobs/SharpLabNext.DesktopClrJitInspector/SharpLabNext.DesktopClrJitInspector.csproj',
      'src/RuntimeJobs/SharpLabNext.WineRunner/SharpLabNext.WineRunner.csproj',
      '--output /desktop-clr-jit-inspector',
      '--output /wine-runner',
      'COPY --from=control-image /usr/share/dotnet/ /usr/share/dotnet/',
      'COPY --from=publish /target-runtime-runner/ /opt/sharplabnext/',
      'COPY --from=publish /desktop-clr-jit-inspector/ /opt/sharplabnext/',
      'COPY --from=publish /wine-runner/ /opt/sharplabnext/',
      '/usr/share/dotnet/dotnet --info',
      'test -s /opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
      'test -s /opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe.config',
      "'Z:\\\\opt\\\\sharplabnext\\\\SharpLabNext.TargetRuntimeRunner.exe' self-test",
      '/opt/sharplabnext/SharpLabNext.DesktopClrJitInspector.exe',
      '/opt/sharplabnext/SharpLabNext.WineRunner.dll',
      'install -d -m 0755 /workspace',
      'desktop-jit',
      'WindowsAbi',
      'wine-framework-desktop-clr-jit-preflight-v1',
      'io.sharplabnext.control-image="${CONTROL_IMAGE}"',
    ]],
    ['Dockerfile.runtime-wine-framework-matrix-shared', [
      'ARG PARENT_IMAGE',
      'ARG WINE_IMAGE',
      'ARG FRAMEWORK_SOURCE_REVISION',
      'ARG HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT',
      'ARG FRAMEWORK_MATRIX_INPUT_SHA256',
      'ARG FRAMEWORK_MATRIX_SOURCE_URI',
      'FROM ${PARENT_IMAGE} AS matrix-parent',
      'FROM matrix-parent AS runtime-base',
      'assemble-framework-prefix-matrix select',
      '--root /opt/sharplabnext/framework-prefixes',
      '--canonical-prefix "${canonical_prefix}"',
      '--receipt /opt/sharplabnext/.framework-selector.json',
      '--expected-input-manifest-sha256 "${FRAMEWORK_MATRIX_INPUT_SHA256}"',
      '.operator-wine-image',
      '.framework-matrix-input-sha256',
      '.framework-matrix-source-uri',
      'io.sharplabnext.operator-image.wine="${WINE_IMAGE}"',
      'io.sharplabnext.framework.source-revision="${FRAMEWORK_SOURCE_REVISION}"',
      'test "${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = true || test "${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = false',
      'test "${FRAMEWORK_SOURCE_REVISION}" != "${SOURCE_REVISION}"',
      'test "${FRAMEWORK_SOURCE_REVISION}" = "${SOURCE_REVISION}"',
      '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "committed:true:false"',
      '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "working-tree-development:false:false"',
      '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "committed-historical-framework-input-development:false:true"',
      '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "working-tree-historical-framework-input-development:false:true"',
      'test -L "${canonical_prefix}"',
      'test ! -e "${other_prefix}"',
      'FROM runtime-base AS preflight',
      'sharplabnext-wine-netfx-preflight',
      'src/RuntimeJobs/SharpLabNext.DesktopClrJitInspector/SharpLabNext.DesktopClrJitInspector.csproj',
      'src/RuntimeJobs/SharpLabNext.WineRunner/SharpLabNext.WineRunner.csproj',
      '--output /desktop-clr-jit-inspector',
      '--output /wine-runner',
      'COPY --from=publish /desktop-clr-jit-inspector/ /opt/sharplabnext/',
      'COPY --from=publish /wine-runner/ /opt/sharplabnext/',
      '/opt/sharplabnext/SharpLabNext.DesktopClrJitInspector.exe',
      '/opt/sharplabnext/SharpLabNext.WineRunner.dll',
      'install -d -m 0755 /workspace',
      'desktop-jit',
      'WindowsAbi',
      'wine-framework-desktop-clr-jit-preflight-v1',
      'io.sharplabnext.framework.matrix-parent',
      'io.sharplabnext.framework.selector',
      'io.sharplabnext.framework.matrix-selector="true"',
    ]],
    ['Dockerfile.runtime-mono-wine-matrix', [
      'ARG MONO_WINE_IMAGE',
      'FROM ${MONO_WINE_IMAGE} AS runtime-source',
      'FROM runtime-source AS runtime-base',
      'FROM runtime-base AS preflight',
      'FROM runtime-base AS final',
      'test -x /usr/bin/mono-sgen',
      'test -x /usr/lib/wine/wine64',
      'WINELOADER=/usr/lib/wine/wine64',
      'WINESERVER=/usr/lib/wine/wineserver64',
      'CMD ["/usr/lib/wine/wine64", "--version"]',
      'test -d /opt/wine-dotnet/drive_c/windows',
      'test -d /opt/wine-netfx-clr2/drive_c/windows/Microsoft.NET/Framework64/v2.0.50727',
      'test -d /opt/wine-netfx-clr4/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319',
      'for prefix in /opt/wine-netfx-clr2 /opt/wine-netfx-clr4 /opt/wine-dotnet',
      'test -r "${prefix}/system.reg"',
      'index($0, "#arch=") == 1',
      '= win64',
      'test ! -e /usr/lib/x86_64-linux-gnu/wine/i386-windows',
      'COPY --from=control-image /usr/share/dotnet/ /usr/share/dotnet/',
      'COPY --from=publish /legacy-jit-helper/ /opt/sharplabnext/',
      'COPY --from=publish /target-runtime-runner/ /opt/sharplabnext/',
      '/usr/share/dotnet/dotnet --info',
      'test -s /opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
      'test -s /opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe.config',
      'target_frames="$(/usr/bin/mono',
      'clr2_frames="$(WINEPREFIX=/opt/wine-netfx-clr2 /usr/lib/wine/wine64',
      'clr4_frames="$(WINEPREFIX=/opt/wine-netfx-clr4 /usr/lib/wine/wine64',
      'io.sharplabnext.operator-image.mono-wine="${MONO_WINE_IMAGE}"',
    ]],
  ])
  for (const [fileName, requiredText] of candidateRuntimeChecks) {
    const source = fs.readFileSync(path.join(dockerDirectory, fileName), 'utf8')
    for (const text of requiredText) {
      if (!source.includes(text)) {
        failures.push(`${fileName} is missing candidate preflight check '${text}'`)
      }
    }
    if (sourceStatusDockerfiles.has(fileName)) {
      const sourceContextChecks = fileName === 'Dockerfile.runtime-wine-framework-matrix-shared'
        ? [
            'ARG CANDIDATE_SOURCE_CONTEXT',
            'ARG CANDIDATE_PROMOTION_ELIGIBLE',
            'ARG HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT',
            '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "committed:true:false"',
            '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "working-tree-development:false:false"',
            '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "committed-historical-framework-input-development:false:true"',
            '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}:${HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT}" = "working-tree-historical-framework-input-development:false:true"',
            'io.sharplabnext.source.context="${CANDIDATE_SOURCE_CONTEXT}"',
            'com.sharplabnext.runtime-candidate.promotion-eligible="${CANDIDATE_PROMOTION_ELIGIBLE}"',
          ]
        : [
            'ARG CANDIDATE_SOURCE_CONTEXT',
            'ARG CANDIDATE_PROMOTION_ELIGIBLE',
            '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}" = "committed:true"',
            '"${CANDIDATE_SOURCE_CONTEXT}:${CANDIDATE_PROMOTION_ELIGIBLE}" = "working-tree-development:false"',
            'io.sharplabnext.source.context="${CANDIDATE_SOURCE_CONTEXT}"',
            'com.sharplabnext.runtime-candidate.promotion-eligible="${CANDIDATE_PROMOTION_ELIGIBLE}"',
          ]
      for (const text of sourceContextChecks) {
        if (!source.includes(text)) {
          failures.push(`${fileName} is missing source-context check '${text}'`)
        }
      }
    }
    if ((fileName === 'Dockerfile.runtime-wine-dotnet-matrix' ||
         fileName === 'Dockerfile.runtime-mono-wine-matrix') &&
        /test ! -e .*syswow64/.test(source)) {
      failures.push(`${fileName} incorrectly treats a syswow64 directory as evidence of a 32-bit prefix`)
    }
    if (fileName.startsWith('Dockerfile.runtime-wine-') ||
        fileName === 'Dockerfile.runtime-mono-wine-matrix') {
      if (/COPY --from=(?:wine-source|runtime-source) \/usr\/ \/usr\//.test(source)) {
        failures.push(`${fileName} copies the operator userspace instead of retaining it as the runtime base`)
      }
      if (/(?:wine|mono-wine|control)-(?:os|libc)-id|cmp --silent/.test(source)) {
        failures.push(`${fileName} relies on distro/libc text equality instead of an executable compatibility preflight`)
      }
    }
    if (fileName === 'Dockerfile.runtime-wine-framework-matrix' &&
        /COPY[^\r\n]*\.wine-prefix-layout\.json/.test(source)) {
      failures.push(`${fileName} must retain the operator-provided prefix layout manifest rather than copying a source-tree replacement`)
    }
  }
  const matrixDockerfile = fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.runtime-dotnet-matrix'), 'utf8')
  for (const requiredText of [
    'FROM ${RUNTIME_DEPS_IMAGE} AS final',
    'test -d "/runtime/shared/Microsoft.NETCore.App/${DOTNET_RUNTIME_VERSION}"',
    'test -f "/runtime/shared/Microsoft.NETCore.App/${DOTNET_RUNTIME_VERSION}/System.Private.CoreLib.dll"',
    'test -d "/opt/sharplabnext/target-dotnet/shared/Microsoft.NETCore.App/${DOTNET_RUNTIME_VERSION}"',
    'test -f "/opt/sharplabnext/target-dotnet/shared/Microsoft.NETCore.App/${DOTNET_RUNTIME_VERSION}/System.Private.CoreLib.dll"',
    'ARG DOTNET_CHECKED_JIT_VERSION_GENERATION_MODE',
    'case "${DOTNET_CHECKED_JIT_VERSION_GENERATION_MODE}" in',
    'skip-by-upstream-flag) set -- "$@" -skipgenerateversion ;;',
    'SharpLabNext.CheckedJitBridge.dll',
    '--verify-runtime-version',
    '"${DOTNET_RUNTIME_VERSION}";',
  ]) {
    if (!matrixDockerfile.includes(requiredText)) {
      failures.push(`Dockerfile.runtime-dotnet-matrix is missing '${requiredText}'`)
    }
  }

  const jsharpRuntime = fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.runtime-wine-jsharp20'), 'utf8')
  const netfxRuntime = fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.runtime-wine-netfx48'), 'utf8')
  const cppCliWorker = fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.worker-cppcli'), 'utf8')
  const jsharpWorker = fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.worker-jsharp'), 'utf8')
  for (const requiredText of [
    'AS built-jsharp-wine-base',
    'FROM jsharp-wine-base-context AS final',
    'find /opt/sharplabnext -mindepth 1 -maxdepth 1 ! -name jsharp20',
    'WINEARCH=win64',
    'WINELOADER=/usr/lib/wine/wine64',
    'WINESERVER=/usr/lib/wine/wineserver64',
    'test ! -e /opt/msvc',
    'test ! -e /usr/lib/wine/wine',
    'test ! -e /opt/wine-jsharp20/drive_c/windows/syswow64',
  ]) {
    if (!jsharpRuntime.includes(requiredText)) {
      failures.push(`Dockerfile.runtime-wine-jsharp20 is missing '${requiredText}'`)
    }
  }
  for (const [fileName, source] of [
    ['Dockerfile.runtime-wine-netfx48', netfxRuntime],
    ['Dockerfile.runtime-wine-jsharp20', jsharpRuntime],
  ]) {
    for (const requiredText of [
      'ARG CONTROL_TFM',
      '--framework ${CONTROL_TFM}',
      'mkdir -p /opt/sharplabnext/control-dotnet',
      'ln -s /usr/share/dotnet/dotnet /opt/sharplabnext/control-dotnet/dotnet',
    ]) {
      if (!source.includes(requiredText)) {
        failures.push(`${fileName} is missing shared control-plane wiring '${requiredText}'`)
      }
    }
  }
  for (const requiredText of [
    'src/RuntimeJobs/SharpLabNext.DesktopClrJitInspector/SharpLabNext.DesktopClrJitInspector.csproj',
    'src/RuntimeJobs/SharpLabNext.WineRunner/SharpLabNext.WineRunner.csproj',
    '--output /desktop-clr-jit-inspector',
    'COPY --from=publish /desktop-clr-jit-inspector/ /opt/sharplabnext/',
    '/opt/sharplabnext/SharpLabNext.DesktopClrJitInspector.exe',
    '/opt/sharplabnext/SharpLabNext.WineRunner.dll',
    'install -d -m 0755 /workspace',
    'desktop-jit',
    'WindowsAbi',
  ]) {
    if (!netfxRuntime.includes(requiredText)) {
      failures.push(`Dockerfile.runtime-wine-netfx48 is missing Desktop CLR JIT wiring '${requiredText}'`)
    }
  }
  if (!jsharpWorker.includes('FROM jsharp-wine-base AS final')) {
    failures.push('Dockerfile.worker-jsharp must inherit the shared J# Wine base target')
  }
  if (!netfxRuntime.includes('FROM cppcli-prepared-base-context AS wine-source') ||
      !cppCliWorker.includes('FROM cppcli-prepared-base AS final')) {
    failures.push('C++/CLI runtime and worker must consume the immutable prepared base input')
  }
  if (!cppCliWorker.includes('rm -rf /app /usr/share/dotnet')) {
    failures.push('C++/CLI worker must remove historical app/control-runtime payloads from the prepared base')
  }
  if (!/RUN rm -rf\s*\\\r?\n\s*\/usr\/share\/dotnet\s*\\\r?\n\s*\/usr\/local\/bin\/sharplabnext-service/.test(netfxRuntime)) {
    failures.push('C++/CLI runtime source must remove historical worker control-plane payloads')
  }
  if (!bake.includes('"cppcli-prepared-base-context" = "docker-image://${deferred_image(CPPCLI_PREPARED_BASE_IMAGE)}"') ||
      !bake.includes('"cppcli-prepared-base" = "docker-image://${deferred_image(CPPCLI_PREPARED_BASE_IMAGE)}"')) {
    failures.push('C++/CLI runtime and worker must use digest-pinned prepared-base contexts')
  }
  if (netfxRuntime.includes('CPPCLI_TOOLCHAIN_IMAGE') || cppCliWorker.includes('CPPCLI_TOOLCHAIN_IMAGE')) {
    failures.push('C++/CLI product Dockerfiles must not consume the raw private operator directly')
  }
  if (!bake.includes('"jsharp-wine-base-context" = "target:jsharp-wine-base"') ||
      !bake.includes('"jsharp-wine-base" = "target:jsharp-wine-base"') ||
      bake.includes('JSHARP_WINE_BASE_IMAGE')) {
    failures.push('J# runtime and worker must consume the shared prepared Wine base target')
  }
  if (!jsharpWorker.includes('JSharp__CompilerHostPath=/usr/lib/wine/wine64') ||
      !jsharpWorker.includes('JSharp__CompilerPath=/opt/sharplabnext/jsharp20/vjc.exe')) {
    failures.push('Dockerfile.worker-jsharp must use the verified x64 Wine/compiler paths')
  }
  for (const requiredText of [
    "find \"${reference_root}\" -maxdepth 1 -type f -printf '%f\\n'",
    '| LC_ALL=C sort',
    "printf 'sha256:%s  %s  %s\\n' \"${file_digest}\" \"${file_size}\" \"${file}\"",
    'reference_content_digest="sha256:$(sha256sum "${reference_manifest}" | cut -d\' \' -f1)"',
    'test "${reference_content_digest}" = "${JSHARP_REFERENCE_DIGEST}"',
    'rm -f "${reference_manifest}"',
  ]) {
    if (!jsharpWorker.includes(requiredText)) {
      failures.push(`Dockerfile.worker-jsharp is missing reference-content verification '${requiredText}'`)
    }
  }
  if (jsharpRuntime.includes('COPY --from=operator-toolchain /usr/ /usr/') ||
      jsharpRuntime.includes('COPY --from=operator-toolchain /opt/ /opt/')) {
    failures.push('J# product images must not copy the complete operator userspace or /opt tree')
  }

  const serviceWorker = fs.readFileSync(path.join(dockerDirectory, 'Dockerfile.worker'), 'utf8')
  const measurementHelperPath = path.join(dockerDirectory, 'runtime-measurement-helper.sh')
  const measurementHelperSha256 = crypto.createHash('sha256').update(fs.readFileSync(measurementHelperPath)).digest('hex')
  for (const requiredText of [
    'FROM publish AS framework-reference-sets',
    'COPY profiles/runtime-matrix.json /inputs/runtime-matrix.json',
    'COPY eng/tools/materialize-framework-reference-sets.cs /tools/materialize-framework-reference-sets.cs',
    'dotnet run materialize-framework-reference-sets.cs',
    'test "$(find /reference-sets -mindepth 1 -maxdepth 1 -type d | wc -l)" -eq 14',
    'COPY --from=framework-reference-sets --chown=1654:1654',
    'AS final-with-framework-and-jsharp-reference-sets',
    'COPY --from=jsharp-reference-source --chown=1654:1654',
    '/reference-sets/jsharp20-ref/ /reference-sets/jsharp20-ref/',
    'test "${reference_content_digest}" = "${JSHARP_REFERENCE_DIGEST}"',
    `"${measurementHelperSha256}"`,
    'sha256sum --check --status -',
  ]) {
    if (!serviceWorker.includes(requiredText)) {
      failures.push(`Dockerfile.worker is missing artifact reference wiring '${requiredText}'`)
    }
  }
  if (serviceWorker.includes('reference-sets-with-netfx') ||
      serviceWorker.includes('microsoft.netframework.referenceassemblies.net48.${NETFX48_MANAGED_REFERENCE_VERSION}.nupkg')) {
    failures.push('Dockerfile.worker must use the shared Framework reference-set materializer, not a net48-only extractor')
  }
  if (!bake.includes('"jsharp-reference-source" = "target:worker-jsharp"') ||
      !bake.includes('target = "final-with-framework-and-jsharp-reference-sets"')) {
    failures.push('worker-artifacts-default must consume the verified J# reference target')
  }
}

function discoverDockerfiles(root) {
  const excludedDirectories = new Set(['.git', '.tmp', '_tmp', '.vs', '.idea', '.vscode', 'artifacts', 'bin', 'obj', 'node_modules', 'dist', 'coverage', 'TestResults', 'third_party'])
  const files = []
  const visit = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.isSymbolicLink()) continue
      const filename = path.join(directory, entry.name)
      if (entry.isDirectory()) {
        if (!excludedDirectories.has(entry.name)) visit(filename)
      } else if (entry.isFile() && entry.name.startsWith('Dockerfile')) {
        files.push(path.relative(root, filename).replaceAll(path.sep, '/'))
      }
    }
  }
  visit(root)
  return files.sort()
}

function readJson(filePath) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'))
  } catch (error) {
    failures.push(`${path.relative(repositoryRoot, filePath)} is invalid JSON (${error.message})`)
    return { images: [] }
  }
}

function variableNames(source) { return [...variableBlocks(source).keys()]; }

function variableBlocks(source) {
  const result = new Map()
  for (const match of source.matchAll(/variable\s+"([^"]+)"\s*\{([\s\S]*?)\}/g)) {
    result.set(match[1], match[2])
  }
  return result
}

function namedBlocks(source, kind) {
  const result = new Map()
  const expression = new RegExp(`${kind}\\s+"([^"]+)"\\s*\\{`, 'g')
  for (const match of source.matchAll(expression)) {
    let depth = 1
    let index = match.index + match[0].length
    while (index < source.length && depth > 0) {
      if (source[index] === '{') depth += 1
      else if (source[index] === '}') depth -= 1
      index += 1
    }
    result.set(match[1], source.slice(match.index, index))
  }
  return result
}
