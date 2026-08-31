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

function operatorInputFiles(definitions) {
  const files = new Set(['.dockerignore', '.gitattributes', 'eng/release-prerequisites.json'])
  for (const definition of definitions) {
    const operator = definition.operator
    for (const filename of [operator.script, ...(operator.inputFiles ?? [])]) {
      if (typeof filename === 'string' && filename.length > 0) files.add(filename)
    }
  }
  return [...files]
}

function defaultCapabilityDefinitions(repositoryRoot) {
  try {
    return JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'deploy', 'images.json'), 'utf8')).capabilityDefinitions ?? []
  } catch {
    return []
  }
}

function validateOperatorDefinition(definition) {
  const operator = definition?.operator
  if (!isObject(operator) || typeof operator.imageId !== 'string' || typeof operator.buildKind !== 'string' || typeof operator.script !== 'string' || operator.frameworkSeedGeneration !== undefined && typeof operator.frameworkSeedGeneration !== 'string') {
    fail(`Capability '${definition?.id ?? '<unknown>'}' has an incomplete operator definition`)
  }
  const inputFiles = operator.inputFiles ?? []
  const downloadArguments = operator.downloadArguments ?? []
  const licenseArguments = operator.licenseArguments ?? []
  if (!Array.isArray(inputFiles) || !Array.isArray(downloadArguments) || !Array.isArray(licenseArguments)) fail(`Capability '${definition.id}' operator metadata arrays are invalid`)
  if ([operator.script, ...inputFiles].some(filename => typeof filename !== 'string' || filename.length === 0 || path.isAbsolute(filename) || filename.includes('\\') || filename.split('/').some(segment => segment.length === 0 || segment === '.' || segment === '..'))) {
    fail(`Capability '${definition.id}' operator input paths are invalid`)
  }
  if (operator.environmentVariable !== undefined && !/^[A-Z][A-Z0-9_]*$/.test(operator.environmentVariable)) fail(`Capability '${definition.id}' operator environment variable is invalid`)
  if (downloadArguments.some(argument => !isObject(argument) || typeof argument.option !== 'string' || !/^--[A-Za-z0-9][A-Za-z0-9-]*$/.test(argument.option) || typeof argument.downloadId !== 'string')) fail(`Capability '${definition.id}' operator download arguments are invalid`)
  if (licenseArguments.some(argument => typeof argument !== 'string' || !/^--[A-Za-z0-9][A-Za-z0-9-]*$/.test(argument))) fail(`Capability '${definition.id}' operator license arguments are invalid`)
}

export async function createOperatorImageBuildSpec(repositoryRoot, prerequisiteManifest, frameworkSeeds, capabilityDefinitions = defaultCapabilityDefinitions(repositoryRoot)) {
  const manifest = prerequisiteManifest?.value
  if (!isObject(manifest) || !sha256Pattern.test(prerequisiteManifest?.sha256 ?? '') ||
      !Array.isArray(manifest.generatedImages)) {
    fail('Operator image build requires one validated prerequisite manifest')
  }
  if (!Array.isArray(capabilityDefinitions)) fail('Operator image build capability definitions must be an array')
  const definitions = capabilityDefinitions.filter(definition => definition?.operator !== undefined)
  definitions.forEach(validateOperatorDefinition)
  const downloadIds = new Set((manifest.downloads ?? []).map(item => item?.id).filter(value => typeof value === 'string'))
  for (const definition of definitions) {
    for (const argument of definition.operator.downloadArguments ?? []) {
      if (!downloadIds.has(argument.downloadId)) fail(`Capability '${definition.id}' references missing prerequisite download '${argument.downloadId}'`)
    }
  }
  const seeds = Object.freeze(Object.fromEntries(Object.entries(frameworkSeeds ?? {}).map(([name, reference]) => [name, requireSeedReference(reference, name)])))
  const allImages = manifest.generatedImages.map(item => Object.freeze({
    id: item.id,
    reference: item.reference,
    buildKind: item.buildKind,
  }))
  const imageById = new Map(allImages.map(image => [image.id, image]))
  const selectedImageIds = new Set()
  const images = []
  for (const definition of definitions) {
    const image = imageById.get(definition.operator.imageId)
    if (image === undefined || !selectedImageIds.add(image.id) || image.buildKind !== definition.operator.buildKind || definition.operator.frameworkSeedGeneration !== undefined && seeds[definition.operator.frameworkSeedGeneration] === undefined) {
      fail(`Capability '${definition.id}' does not match a unique generated operator image and framework seed`)
    }
    images.push(image)
  }
  const files = await collectInputFiles(
    repositoryRoot,
    operatorInputFiles(definitions),
    4 * 1024 * 1024,
    'Operator image build input',
  )
  const descriptor = {
    schemaVersion: 1,
    strategy: operatorImageBuildStrategy,
    prerequisiteManifestSha256: prerequisiteManifest.sha256,
    files,
    frameworkSeeds: seeds,
    operators: definitions,
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
