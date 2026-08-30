import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';

const frameworkSeedBuildStrategy = 'framework-companion-seed-build-v1';
const operatorImageBuildStrategy = 'source-built-operator-image-v1';
const sha256Pattern = /^[0-9a-f]{64}$/;
const digestReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/;

const frameworkInputFiles = Object.freeze([
  '.dockerignore',
  '.gitattributes',
  'deploy/docker/Dockerfile.operator-wine-framework-matrix',
  'deploy/docker/wine-netfx-framework-bootstrap.sh',
  'deploy/docker/wine-netfx-framework-preflight.sh',
  'deploy/docker/dedupe-wine-prefixes.py',
  'deploy/docker/certificates/microsoft-tls-rsa-root-g2-xsign.crt',
  'deploy/docker/certificates/microsoft-tls-g2-rsa-ca-ocsp-04.crt',
  'profiles/runtime-framework-installers.json',
  'eng/tools/prepare-framework-runtime.cs',
  'eng/release-prerequisites.json',
  'eng/prerequisites/dotnet-framework-2.0/NetFx64.exe',
])

const operatorInputFiles = Object.freeze([
  '.dockerignore',
  '.gitattributes',
  'deploy/docker/Dockerfile.operator-jsharp20',
  'deploy/docker/Dockerfile.operator-cppcli-base',
  'deploy/docker/cppcli-netfx-env.sh',
  'deploy/docker/extract-netfx48-sdk.py',
  'eng/tools/prepare-jsharp-toolchain.cs',
  'eng/tools/prepare-cppcli-toolchain.cs',
  'eng/release-prerequisites.json',
])

export const frameworkSeedDefinitions = Object.freeze([
  Object.freeze({
    id: 'clr2-3.5',
    generation: 'clr2',
    version: '3.5',
    prefix: '/opt/wine-netfx-clr2',
    reference: 'sharplabnext/framework-companion-seed-clr2-35:source-v1',
  }),
  Object.freeze({
    id: 'clr4-4.8',
    generation: 'clr4',
    version: '4.8',
    prefix: '/opt/wine-netfx-clr4',
    reference: 'sharplabnext/framework-companion-seed-clr4-48:source-v1',
  }),
])

export class ImageBuildInputError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'ImageBuildInputError'
  }
}

function fail(message, options) { throw new ImageBuildInputError(message, options); }

function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

async function fileSha256(filename) {
  const hash = crypto.createHash('sha256')
  await pipeline(fs.createReadStream(filename), hash)
  return hash.digest('hex')
}

async function collectInputFiles(repositoryRoot, relativePaths, maximumBytes, label) {
  const root = fs.realpathSync(repositoryRoot)
  const files = []
  for (const relativePath of relativePaths) {
    const filename = path.resolve(root, ...relativePath.split('/'))
    if (!filename.startsWith(`${root}${path.sep}`)) {
      fail(`${label} '${relativePath}' escapes the repository root`)
    }
    let info
    try { info = fs.lstatSync(filename) } catch (error) {
      fail(`${label} '${relativePath}' is missing`, { cause: error })
    }
    if (!info.isFile() || info.isSymbolicLink() || info.size < 1 ||
        info.size > maximumBytes) {
      fail(`${label} '${relativePath}' must be one bounded regular file`)
    }
    files.push({
      path: relativePath,
      sizeBytes: info.size,
      sha256: await fileSha256(filename),
    })
  }
  return files
}

export async function createFrameworkSeedBuildSpec(repositoryRoot, wineImage, rootImage) {
  if (!digestReferencePattern.test(wineImage) || !digestReferencePattern.test(rootImage)) {
    fail('Framework seed inputs must use digest-pinned Wine and root images')
  }
  const files = await collectInputFiles(
    repositoryRoot,
    frameworkInputFiles,
    2 * 1024 * 1024 * 1024,
    'Framework seed build input',
  )
  const manifestSha256 = files.find(file => file.path === 'profiles/runtime-framework-installers.json')?.sha256;
  if (!sha256Pattern.test(manifestSha256 ?? '')) {
    fail('Framework installer manifest digest is missing from the seed build input closure')
  }
  const descriptor = {
    schemaVersion: 1,
    strategy: frameworkSeedBuildStrategy,
    wineImage,
    rootImage,
    files,
    seeds: frameworkSeedDefinitions.map(({ id, generation, version, prefix }) => ({
      id, generation, version, prefix,
    })),
  }
  const inputSha256 = crypto.createHash('sha256').update(JSON.stringify(descriptor)).digest('hex')
  return Object.freeze({
    inputSha256,
    manifestSha256,
    wineImage,
    rootImage,
    descriptor: Object.freeze(descriptor),
    images: frameworkSeedDefinitions,
  })
}

function requireSeedReference(value, name) {
  if (!digestReferencePattern.test(value ?? '')) {
    fail(`Framework ${name} seed must use one digest-pinned image reference`)
  }
  return value
}

export async function createOperatorImageBuildSpec(repositoryRoot, prerequisiteManifest, frameworkSeeds) {
  const manifest = prerequisiteManifest?.value
  if (!isObject(manifest) || !sha256Pattern.test(prerequisiteManifest?.sha256 ?? '') ||
      !Array.isArray(manifest.generatedImages)) {
    fail('Operator image build requires one validated prerequisite manifest')
  }
  const seeds = Object.freeze({
    clr2: requireSeedReference(frameworkSeeds?.clr2, 'CLR2'),
    clr4: requireSeedReference(frameworkSeeds?.clr4, 'CLR4'),
  })
  const images = manifest.generatedImages.map(item => Object.freeze({
    id: item.id,
    reference: item.reference,
    buildKind: item.buildKind,
  }))
  if (JSON.stringify(images.map(({ id, buildKind }) => ({ id, buildKind }))) !==
      JSON.stringify([
        { id: 'jsharp20-development-base', buildKind: 'jsharp20' },
        { id: 'cppcli-prepared-base', buildKind: 'cppcli' },
      ])) {
    fail('Operator image manifest does not contain the canonical generated images')
  }

  const files = await collectInputFiles(
    repositoryRoot,
    operatorInputFiles,
    4 * 1024 * 1024,
    'Operator image build input',
  )
  const descriptor = {
    schemaVersion: 1,
    strategy: operatorImageBuildStrategy,
    prerequisiteManifestSha256: prerequisiteManifest.sha256,
    files,
    frameworkSeeds: seeds,
    images,
  }
  const inputSha256 = crypto.createHash('sha256').update(JSON.stringify(descriptor)).digest('hex')
  return Object.freeze({
    inputSha256,
    descriptor: Object.freeze(descriptor),
    frameworkSeeds: seeds,
    images: Object.freeze(images),
  })
}
