import { spawn, spawnSync } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import http from 'node:http'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import {
  createFrameworkSeedBuildSpec,
  createOperatorImageBuildSpec,
} from './image-build-inputs.mjs'
import {
  computeBuildCacheInputFingerprintSync,
} from './build-cache-inputs.mjs'
import {
  wineCoreClrOperatorExpectedLabels,
} from './build-runtime-candidate.mjs'
import {
  readPrerequisiteManifest,
  runPrerequisiteCache,
} from './prerequisite-cache.mjs'
import {
  wineCoreClrUserspaceEnvironment,
} from './runtime-wine-userspace-lock.mjs'

const defaultRepositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const sourceRevisionPattern = /^[0-9a-f]{40}(?:[0-9a-f]{24})?$/
const imageIdPattern = /^sha256:[0-9a-f]{64}$/
const digestReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const developmentInputsLabel = 'io.sharplabnext.development-image-inputs'
const sourceRevisionLabel = 'io.sharplabnext.source.revision'
const versionLabel = 'org.opencontainers.image.version'
const bakeEnvironmentJsonPrefix = 'SHARPLABNEXT_BAKE_ENVIRONMENT_JSON='
const developmentSourceGrant = 'SHARPLABNEXT_BAKE_ALLOW_UNCOMMITTED_SOURCE_FOR_DEVELOPMENT'
const developmentImageInputsGrant = 'SHARPLABNEXT_BAKE_ALLOW_DEVELOPMENT_IMAGE_INPUTS'
const buildCacheStateSchemaVersion = 1
const buildCacheStateFilename = 'build-images-state.json'
const imageCacheProbePrefix = 'SHARPLABNEXT_IMAGE_CACHE='
// A retry is for transient Docker/restore failures only.  BuildKit reuses
// completed layers, so one bounded retry is enough without hiding real errors.
const buildRetryAttempts = 2
const buildRetryDelayMilliseconds = 3_000
const frameworkIds = Object.freeze([
  'netfx20', 'netfx30', 'netfx35', 'netfx40', 'netfx45', 'netfx451', 'netfx452',
  'netfx46', 'netfx461', 'netfx462', 'netfx47', 'netfx471', 'netfx472', 'netfx48',
])
const jsharpBuildTargets = new Set([
  'runtime-wine-jsharp20', 'worker-jsharp', 'worker-artifacts-default',
])
const cppcliBuildTargets = new Set(['runtime-wine-netfx48', 'worker-cppcli'])

// These are external build capabilities, not language-specific cache paths.
// A target asks for a capability; the dependency closure below supplies the
// shared operators only when a missing output actually needs them.
const buildCapabilityRules = Object.freeze([
  Object.freeze({
    id: 'wine',
    matches: image => image.producer.kind === 'runtime-candidate' &&
      /^(?:wine-dotnet|wine-netfx)/.test(image.producer.id),
  }),
  Object.freeze({
    id: 'framework',
    matches: image => image.producer.kind === 'runtime-candidate' &&
      /^wine-netfx/.test(image.producer.id),
  }),
  Object.freeze({
    id: 'jsharp',
    matches: image => image.producer.kind === 'bake' &&
      jsharpBuildTargets.has(image.producer.id),
  }),
  Object.freeze({
    id: 'cppcli',
    matches: image => image.producer.kind === 'bake' &&
      cppcliBuildTargets.has(image.producer.id),
  }),
])

export function resolveBuildCapabilities(images) {
  const capabilities = new Set()
  for (const image of images ?? []) {
    if (image?.producer === undefined) continue
    for (const rule of buildCapabilityRules) {
      if (rule.matches(image)) capabilities.add(rule.id)
    }
  }
  if (capabilities.has('framework') || capabilities.has('jsharp') || capabilities.has('cppcli')) {
    capabilities.add('framework')
    capabilities.add('wine')
  }
  return Object.freeze(capabilities)
}

export class BuildImagesError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'BuildImagesError'
  }
}

function fail(message, options) {
  throw new BuildImagesError(message, options)
}

function run(command, arguments_, options = {}) {
  const result = spawnSync(command, arguments_, {
    cwd: options.cwd,
    env: options.env,
    encoding: options.capture ? 'utf8' : undefined,
    shell: false,
    stdio: options.capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
  })
  if (result.error !== undefined) fail(`Could not start '${command}': ${result.error.message}`, { cause: result.error })
  if (result.status !== 0) {
    const detail = options.capture ? String(result.stderr ?? '').trim() : ''
    fail(`'${command}' exited ${result.status ?? 1}${detail.length > 0 ? `: ${detail}` : ''}`)
  }
  return options.capture ? String(result.stdout ?? '') : ''
}

function start(command, arguments_, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, arguments_, {
      cwd: options.cwd,
      env: options.env,
      shell: false,
      stdio: 'inherit',
    })
    child.once('error', error => reject(new BuildImagesError(`Could not start '${command}': ${error.message}`, { cause: error })))
    child.once('exit', (code, signal) => {
      if (code === 0) resolve()
      else reject(new BuildImagesError(`'${command}' exited ${code ?? signal ?? 1}`))
    })
  })
}

function waitBeforeRetry(attempt) {
  const delay = buildRetryDelayMilliseconds * attempt
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, delay)
}

function runWithRetry(command, arguments_, options = {}) {
  let lastError
  for (let attempt = 1; attempt <= buildRetryAttempts; attempt++) {
    try {
      return run(command, arguments_, options)
    } catch (error) {
      lastError = error
      if (attempt === buildRetryAttempts) throw error
      console.warn(`Build command failed (attempt ${attempt}/${buildRetryAttempts}); retrying.`)
      waitBeforeRetry(attempt)
    }
  }
  throw lastError
}

async function startWithRetry(command, arguments_, options = {}) {
  let lastError
  for (let attempt = 1; attempt <= buildRetryAttempts; attempt++) {
    try {
      await start(command, arguments_, options)
      return
    } catch (error) {
      lastError = error
      if (attempt === buildRetryAttempts) throw error
      console.warn(`Build command failed (attempt ${attempt}/${buildRetryAttempts}); retrying.`)
      await new Promise(resolve => setTimeout(resolve, buildRetryDelayMilliseconds * attempt))
    }
  }
  throw lastError
}

export function validateLocalImageBuildDriverInspection(inspection) {
  const driver = /^Driver:\s*(\S+)\s*$/m.exec(inspection)?.[1]
  if (driver !== 'docker') {
    fail(
      `The complete development build requires the Docker Buildx driver so it can ` +
      `consume source-built operator images from the host image store; ` +
      `observed '${driver ?? '<unknown>'}'. ` +
      'Select the Docker default builder and retry.',
    )
  }
}

function verifyLocalImageBuildDriver(repositoryRoot) {
  validateLocalImageBuildDriverInspection(run(
    'docker',
    ['buildx', 'inspect', '--bootstrap'],
    { cwd: repositoryRoot, capture: true },
  ))
}

export async function runParallel(tasks, maximumParallel) {
  let next = 0
  let firstFailure
  async function worker() {
    while (firstFailure === undefined) {
      const index = next++
      if (index >= tasks.length) return
      const task = tasks[index]
      try {
        await task.run()
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error)
        firstFailure ??= new BuildImagesError(`${task.label} failed: ${detail}`, { cause: error })
      }
    }
  }
  await Promise.all(Array.from({ length: Math.min(maximumParallel, tasks.length) }, worker))
  if (firstFailure !== undefined) throw firstFailure
}

function readJson(filename, label) {
  try { return JSON.parse(fs.readFileSync(filename, 'utf8')) } catch (error) {
    fail(`Could not read ${label} '${filename}': ${error.message}`, { cause: error })
  }
}

function atomicWrite(filename, bytes) {
  fs.mkdirSync(path.dirname(filename), { recursive: true })
  const temporary = path.join(path.dirname(filename), `.${path.basename(filename)}.${process.pid}.${crypto.randomUUID()}.tmp`)
  try {
    fs.writeFileSync(temporary, bytes, { flag: 'wx' })
    fs.rmSync(filename, { force: true })
    fs.renameSync(temporary, filename)
  } finally {
    fs.rmSync(temporary, { force: true })
  }
}

function buildCacheStatePath(options) {
  return path.join(options.repositoryRoot, 'artifacts', buildCacheStateFilename)
}

function readBuildCacheState(options) {
  try {
    const state = JSON.parse(fs.readFileSync(buildCacheStatePath(options), 'utf8'))
    if (state?.schemaVersion !== buildCacheStateSchemaVersion ||
        typeof state.sourceInputDigest !== 'string' ||
        typeof state.imageContentDigest !== 'string' ||
        typeof state.sourceRevision !== 'string' ||
        typeof state.imagePrefix !== 'string') {
      return undefined
    }
    return state
  } catch {
    return undefined
  }
}

function imagePlanContentDigest(imagePlan) {
  const images = imagePlan.plan.images
    .map(image => ({
      id: image.id,
      runtimeId: image.runtimeId ?? null,
      producer: image.producer,
    }))
    .sort((left, right) => left.id < right.id ? -1 : left.id > right.id ? 1 : 0)
  return `sha256:${crypto.createHash('sha256').update(JSON.stringify({ images })).digest('hex')}`
}

function createBuildCacheIdentity(options, imagePlan, sourceRevision, sourceInputDigest) {
  if (sourceInputDigest === undefined) return undefined
  return Object.freeze({
    schemaVersion: buildCacheStateSchemaVersion,
    sourceInputDigest,
    imageContentDigest: imagePlanContentDigest(imagePlan),
    sourceRevision,
    imagePrefix: options.imagePrefix,
  })
}

function buildCacheStateMatches(state, identity) {
  return state !== undefined && identity !== undefined &&
    state.schemaVersion === identity.schemaVersion &&
    state.sourceInputDigest === identity.sourceInputDigest &&
    state.imageContentDigest === identity.imageContentDigest &&
    state.sourceRevision === identity.sourceRevision &&
    state.imagePrefix === identity.imagePrefix
}

function writeBuildCacheState(options, identity) {
  if (identity === undefined) return
  atomicWrite(
    buildCacheStatePath(options),
    `${JSON.stringify(identity, null, 2)}\n`,
  )
}

function recordBuildCacheState(options, identity, sourceInputDigest) {
  if (identity === undefined || sourceInputDigest === undefined) return false
  const currentDigest = resolveBuildCacheInputDigest(options)
  if (currentDigest !== sourceInputDigest) {
    console.warn('Source inputs changed during image build; cache state was not recorded.')
    return false
  }
  writeBuildCacheState(options, identity)
  return true
}

function resolveBuildCacheInputDigest(options) {
  try {
    return computeBuildCacheInputFingerprintSync(options.repositoryRoot)
  } catch (error) {
    console.warn(`Build cache input fingerprint unavailable; using image labels only: ${error.message}`)
    return undefined
  }
}

function resolveSourceRevision(options) {
  const arguments_ = [
    'run', path.join(options.repositoryRoot, 'eng', 'resolve-source-provenance.cs'), '--',
    '--repository-root', options.repositoryRoot,
  ]
  if (options.sourceRevision !== undefined) arguments_.push('--source-revision', options.sourceRevision)
  if (options.allowUncommittedSourceForDevelopment) arguments_.push('--allow-uncommitted-source-for-development')
  const output = run('dotnet', arguments_, { cwd: options.repositoryRoot, capture: true })
  const revision = output.split(/\r?\n/)
    .filter(line => line.startsWith('SHARPLABNEXT_SOURCE_REVISION='))
    .at(-1)?.slice('SHARPLABNEXT_SOURCE_REVISION='.length)
  if (!sourceRevisionPattern.test(revision ?? '')) fail('Source provenance resolver did not return a full revision')
  return revision
}

function generateImagePlan(options, sourceRevision) {
  const output = path.join(options.repositoryRoot, 'artifacts', 'release-image-plan.json')
  run('dotnet', [
    'run', '--project', path.join(options.repositoryRoot, 'src', 'Tools', 'SharpLabNext.BundleBuilder'),
    '--configuration', 'Release', '--',
    '--repository-root', options.repositoryRoot,
    '--write-image-plan', output,
    '--image-prefix', options.imagePrefix,
    '--source-revision', sourceRevision,
  ], { cwd: options.repositoryRoot })
  const plan = readJson(output, 'release image plan')
  if (plan?.schemaVersion !== 1 || typeof plan.releaseId !== 'string' ||
      !Array.isArray(plan.images) || plan.images.length === 0) fail('Release image plan is invalid')
  const ids = new Set()
  const references = new Set()
  for (const image of plan.images) {
    if (typeof image?.id !== 'string' || ids.has(image.id) ||
        typeof image?.reference !== 'string' || references.has(image.reference) ||
        !['bake', 'runtime-candidate', 'pull'].includes(image?.producer?.kind) ||
        typeof image?.producer?.id !== 'string') fail('Release image plan contains an invalid or duplicate entry')
    ids.add(image.id)
    references.add(image.reference)
  }
  const digest = `sha256:${crypto.createHash('sha256').update(JSON.stringify(plan)).digest('hex')}`
  return { plan, path: output, digest }
}

function bakeEnvironmentArguments(options, sourceRevision, operatorImages) {
  const arguments_ = [
    'run', path.join(options.repositoryRoot, 'eng', 'run-with-bake-environment.cs'), '--',
    '--lock', path.join(options.repositoryRoot, 'profiles', 'lock.json'),
    '--base-images', path.join(options.repositoryRoot, 'profiles', 'base-images.json'),
    '--runtime-matrix', path.join(options.repositoryRoot, 'profiles', 'runtime-matrix.json'),
    '--source-revision', sourceRevision,
    '--repository-root', options.repositoryRoot,
    '--image-prefix', options.imagePrefix,
    '--allow-development-image-inputs',
  ]
  if (operatorImages !== undefined) {
    arguments_.push(
      '--development-image-input',
      `CPPCLI_PREPARED_BASE_IMAGE=${operatorImages['cppcli-prepared-base']}`,
      '--development-image-input',
      `JSHARP_TOOLCHAIN_IMAGE=${operatorImages['jsharp20-development-base']}`,
    )
  }
  if (options.allowUncommittedSourceForDevelopment) arguments_.push('--allow-uncommitted-source-for-development')
  return arguments_
}

function runInBakeEnvironment(
  options,
  sourceRevision,
  operatorImages,
  command,
  arguments_,
  snapshot = options.bakeEnvironmentSnapshot,
) {
  if (snapshot !== undefined) {
    return runWithRetry(command, arguments_, {
      cwd: options.repositoryRoot,
      env: createBakeChildEnvironment(snapshot, options, process.env, operatorImages),
    })
  }
  return runWithRetry('dotnet', [
    ...bakeEnvironmentArguments(options, sourceRevision, operatorImages),
    '--', command, ...arguments_,
  ], { cwd: options.repositoryRoot })
}

export function parseBakeEnvironmentSnapshot(output) {
  const payloads = String(output)
    .split(/\r?\n/)
    .filter(line => line.startsWith(bakeEnvironmentJsonPrefix))
    .map(line => line.slice(bakeEnvironmentJsonPrefix.length))
  if (payloads.length !== 1) {
    fail('Bake environment resolver did not emit exactly one JSON snapshot')
  }

  let document
  try { document = JSON.parse(payloads[0]) } catch (error) {
    fail(`Bake environment resolver emitted invalid JSON: ${error.message}`, { cause: error })
  }
  if (document === null || typeof document !== 'object' || Array.isArray(document)) {
    fail('Bake environment resolver JSON must be an object')
  }

  const entries = Object.entries(document)
  if (entries.length === 0) fail('Bake environment resolver emitted an empty snapshot')
  for (const [name, value] of entries) {
    if (!/^[A-Z][A-Z0-9_]*$/.test(name) || typeof value !== 'string') {
      fail('Bake environment resolver JSON contains an invalid environment entry')
    }
    if (name === developmentSourceGrant || name === developmentImageInputsGrant) {
      fail(`Bake environment resolver JSON must not contain grant '${name}'`)
    }
  }
  return Object.freeze({ ...document })
}

function resolveBakeEnvironmentSnapshot(options, sourceRevision, operatorImages) {
  const output = runWithRetry('dotnet', [
    ...bakeEnvironmentArguments(options, sourceRevision, operatorImages),
    '--emit-environment-json',
  ], { cwd: options.repositoryRoot, capture: true })
  return parseBakeEnvironmentSnapshot(output)
}

export function createBakeChildEnvironment(
  snapshot,
  options,
  parentEnvironment = process.env,
  operatorImages = undefined,
) {
  const environment = { ...parentEnvironment, ...snapshot }
  for (const name of Object.keys(environment)) {
    const normalized = name.toUpperCase()
    if (normalized === developmentSourceGrant || normalized === developmentImageInputsGrant) {
      delete environment[name]
    }
  }
  if (options.allowUncommittedSourceForDevelopment) {
    environment[developmentSourceGrant] = 'true'
  }
  environment[developmentImageInputsGrant] = 'true'
  if (operatorImages !== undefined) {
    environment.CPPCLI_PREPARED_BASE_IMAGE = operatorImages['cppcli-prepared-base'] ?? ''
    environment.JSHARP_TOOLCHAIN_IMAGE = operatorImages['jsharp20-development-base'] ?? ''
  }
  return environment
}

function registryResponds() {
  return new Promise(resolve => {
    const request = http.get('http://127.0.0.1:5000/v2/', response => {
      response.resume()
      resolve(response.statusCode === 200)
    })
    request.setTimeout(2_000, () => { request.destroy(); resolve(false) })
    request.on('error', () => resolve(false))
  })
}

function inspectContainer(name, repositoryRoot) {
  const result = spawnSync('docker', ['container', 'inspect', name], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
    stdio: ['ignore', 'pipe', 'ignore'],
  })
  if (result.error !== undefined || result.status !== 0) return undefined
  let document
  try { document = JSON.parse(String(result.stdout ?? '')) } catch {
    fail(`Docker returned invalid container inspection JSON for '${name}'`)
  }
  if (!Array.isArray(document) || document.length !== 1) {
    fail(`Docker did not resolve exactly one container for '${name}'`)
  }
  return document[0]
}

export function validateRegistryContainer(
  container,
  configuration,
  requireManagedRestartPolicy = true,
) {
  if (container?.Image !== configuration.imageId ||
      container?.Config?.Image !== configuration.image ||
      (requireManagedRestartPolicy &&
       container?.HostConfig?.RestartPolicy?.Name !== 'unless-stopped')) {
    fail(
      `Container '${configuration.containerName}' does not match the pinned release ` +
      'registry image and restart policy',
    )
  }
  const bindings = container?.HostConfig?.PortBindings?.['5000/tcp']
  if (!Array.isArray(bindings) || bindings.length !== 1 ||
      bindings[0]?.HostIp !== configuration.host ||
      bindings[0]?.HostPort !== String(configuration.port)) {
    fail(
      `Container '${configuration.containerName}' must bind only ` +
      `${configuration.host}:${configuration.port} to registry port 5000`,
    )
  }
}

function containersPublishingRegistryPort(configuration, repositoryRoot) {
  const result = spawnSync('docker', [
    'container', 'ls', '--all',
    '--filter', `publish=${configuration.port}`,
    '--format', '{{.ID}}',
  ], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
    stdio: ['ignore', 'pipe', 'ignore'],
  })
  if (result.error !== undefined || result.status !== 0) {
    fail('Could not inspect containers publishing the local registry port')
  }
  return String(result.stdout ?? '')
    .split(/\r?\n/)
    .map(value => value.trim())
    .filter(value => value.length > 0)
    .map(id => inspectContainer(id, repositoryRoot))
    .filter(container => container !== undefined)
    .filter(container => {
      const bindings = container?.HostConfig?.PortBindings?.['5000/tcp']
      return Array.isArray(bindings) && bindings.some(binding =>
        binding?.HostIp === configuration.host &&
        binding?.HostPort === String(configuration.port))
    })
}

export async function ensureLocalRegistry(configuration, repositoryRoot) {
  let container = inspectContainer(configuration.containerName, repositoryRoot)
  let managed = container !== undefined
  const responding = await registryResponds()
  if (container === undefined) {
    const compatible = containersPublishingRegistryPort(configuration, repositoryRoot)
    if (compatible.length > 1) {
      fail(`More than one container claims ${configuration.host}:${configuration.port}`)
    }
    if (compatible.length === 1) {
      container = compatible[0]
      validateRegistryContainer(container, configuration, false)
    } else if (responding) {
      fail(
        `${configuration.host}:${configuration.port} is occupied by a service that is not ` +
        'the pinned release registry container',
      )
    }
  }
  if (container !== undefined) validateRegistryContainer(container, configuration, managed)
  if (container?.State?.Running !== true && container !== undefined) {
    if (responding) fail('The managed release registry is stopped while its loopback port is occupied')
    run('docker', ['container', 'start', container.Id], { cwd: repositoryRoot })
  } else if (container === undefined) {
    run('docker', [
      'container', 'run', '--detach', '--restart', 'unless-stopped',
      '--name', configuration.containerName,
      '--publish', `${configuration.host}:${configuration.port}:5000`,
      configuration.image,
    ], { cwd: repositoryRoot })
    managed = true
    container = inspectContainer(configuration.containerName, repositoryRoot)
  }
  if (container === undefined) fail('Docker did not retain the managed release registry container')
  container = inspectContainer(container.Id, repositoryRoot)
  if (container === undefined) fail('Docker lost the selected release registry container')
  validateRegistryContainer(container, configuration, managed)
  if (container.State?.Running !== true) fail('The managed release registry container is not running')
  for (let attempt = 0; attempt < 20; attempt++) {
    if (await registryResponds()) return
    await new Promise(resolve => setTimeout(resolve, 250))
  }
  fail('Local release registry did not become ready on 127.0.0.1:5000')
}

function inspectImage(reference, repositoryRoot) {
  const output = run('docker', ['image', 'inspect', reference], { cwd: repositoryRoot, capture: true })
  let document
  try { document = JSON.parse(output) } catch { fail(`Docker returned invalid inspection JSON for '${reference}'`) }
  if (!Array.isArray(document) || document.length !== 1) fail(`Docker did not resolve exactly one image for '${reference}'`)
  return document[0]
}

function imageRepository(reference) {
  const digest = reference.indexOf('@')
  if (digest > 0) return reference.slice(0, digest)
  const tag = reference.lastIndexOf(':')
  if (tag <= reference.lastIndexOf('/')) fail(`Image reference '${reference}' has no tag`)
  return reference.slice(0, tag)
}

function validateImageInspection(image, reference, expectedLabels, description) {
  if (!imageIdPattern.test(image?.Id ?? '') || image?.Os !== 'linux' || image?.Architecture !== 'amd64') {
    fail(`${description} '${reference}' is not one immutable linux/amd64 image`)
  }
  const labels = image.Config?.Labels ?? {}
  for (const [name, expected] of Object.entries(expectedLabels)) {
    if (labels[name] !== expected) {
      fail(
        `${description} '${reference}' label '${name}' is ` +
        `'${labels[name] ?? '<missing>'}', expected '${expected}'`,
      )
    }
  }
  return image
}

export function validateReusableImageInspection(image, reference, expectedLabels) {
  validateImageInspection(image, reference, expectedLabels, 'Cached prerequisite image')
  const repository = imageRepository(reference)
  const digests = (image.RepoDigests ?? [])
    .filter(value => value.startsWith(`${repository}@sha256:`))
  const digest = digests.find(value => digestReferencePattern.test(value))
  if (digest === undefined) {
    fail(`Cached prerequisite image '${reference}' has no unique immutable RepoDigest`)
  }
  return digest
}

function tryInspectImage(reference, repositoryRoot) {
  const result = spawnSync('docker', ['image', 'inspect', reference], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
  })
  if (result.error !== undefined) return undefined
  if (result.status !== 0) return undefined
  try { return inspectImage(reference, repositoryRoot) } catch { return undefined }
}

// BuildKit owns layer reuse. This probe only looks in Docker's local image
// store; it never turns a cache miss into a registry pull or a second cache
// repository. A stale local tag is treated as a miss and rebuilt by BuildKit.
function tryReuseLocalImage(reference, expectedLabels, repositoryRoot, enabled = true) {
  if (!enabled) return undefined
  const image = tryInspectImage(reference, repositoryRoot)
  if (image === undefined) return undefined
  try {
    validateImageInspection(image, reference, expectedLabels, 'Local cached image')
    console.log(`Build cache hit: ${reference} -> ${image.Id}`)
    return image
  } catch {
    return undefined
  }
}

function registryImageTag(options, name) {
  const prefix = String(options.imagePrefix).startsWith('localhost:5000/')
    ? String(options.imagePrefix)
    : `localhost:5000/${options.imagePrefix}`
  return `${prefix}/${name}:${options.releaseId}`
}

// Publish only the immutable identity required by a digest-pinned named
// context. This is transport, not a build cache: the source remains in the
// Docker image store and BuildKit still decides which layers are rebuilt.
function publishImmutableImage(source, destination, expectedLabels, repositoryRoot) {
  const sourceImage = inspectImage(source, repositoryRoot)
  validateImageInspection(sourceImage, source, expectedLabels, 'Built image')
  const existing = tryInspectImage(destination, repositoryRoot)
  if (existing?.Id === sourceImage.Id) {
    try { return validateReusableImageInspection(existing, destination, expectedLabels) } catch { /* republish */ }
  }
  pushAsLocalDigest(source, destination, repositoryRoot)
  const published = inspectImage(destination, repositoryRoot)
  return validateReusableImageInspection(published, destination, expectedLabels)
}

function pushAsLocalDigest(source, destination, repositoryRoot) {
  if (source !== destination) runWithRetry('docker', ['image', 'tag', source, destination], { cwd: repositoryRoot })
  runWithRetry('docker', ['image', 'push', destination], { cwd: repositoryRoot })
  const image = inspectImage(destination, repositoryRoot)
  const repository = destination.slice(0, destination.lastIndexOf(':'))
  const digest = (image.RepoDigests ?? []).find(value => value.startsWith(`${repository}@sha256:`))
  if (!digestReferencePattern.test(digest ?? '')) fail(`Pushed local image '${destination}' has no immutable RepoDigest`)
  return digest
}

function buildWineOperator(
  options,
  sourceRevision,
  snapshot = undefined,
  requireImmutableReference = false,
) {
  const bakeSnapshot = snapshot ?? resolveBakeEnvironmentSnapshot(options, sourceRevision, undefined)
  const values = {
    ...bakeSnapshot,
    ...wineCoreClrUserspaceEnvironment(bakeSnapshot, options.repositoryRoot),
    SOURCE_REVISION: sourceRevision,
  }
  const sourceBinding = Object.freeze(options.allowUncommittedSourceForDevelopment
    ? {
        context: 'working-tree-development',
        promotionEligible: false,
      }
    : {
        context: 'committed',
        promotionEligible: true,
      })
  const expectedLabels = {
    ...wineCoreClrOperatorExpectedLabels(values, sourceBinding),
    'org.opencontainers.image.revision': sourceRevision,
    [sourceRevisionLabel]: sourceRevision,
    'io.sharplabnext.base-image.dotnet-runtime-deps': values.BASE_DOTNET_RUNTIME_DEPS_IMAGE,
    [developmentInputsLabel]: 'true',
  }
  // Keep one content tag across development and release identities. The
  // release-scoped tag is applied by the operator wrapper and remains only a
  // user-facing alias.
  const localTag = `${options.imagePrefix}/operator-wine-coreclr:content`
  const releaseTag = `${options.imagePrefix}/operator-wine-coreclr:${options.releaseId}`
  const cached = tryReuseLocalImage(
    localTag,
    expectedLabels,
    options.repositoryRoot,
    options.reuseExisting,
  )
  if (cached !== undefined) {
    const digest = requireImmutableReference
      ? publishImmutableImage(
        localTag,
        registryImageTag(options, 'operator-wine-coreclr'),
        expectedLabels,
        options.repositoryRoot,
      )
      : cached.Id
    return { localTag, digest }
  }

  const arguments_ = [path.join(options.repositoryRoot, 'eng', 'build-wine-coreclr-operator.mjs')]
  if (options.allowUncommittedSourceForDevelopment) arguments_.push('--allow-uncommitted-source-for-development')
  runInBakeEnvironment(options, sourceRevision, undefined, process.execPath, arguments_, bakeSnapshot)
  const built = inspectImage(releaseTag, options.repositoryRoot)
  validateImageInspection(built, releaseTag, expectedLabels, 'Built image')
  runWithRetry('docker', ['image', 'tag', releaseTag, localTag], { cwd: options.repositoryRoot })
  const image = inspectImage(localTag, options.repositoryRoot)
  validateImageInspection(image, localTag, expectedLabels, 'Built image')
  const digest = requireImmutableReference
    ? publishImmutableImage(
      localTag,
      registryImageTag(options, 'operator-wine-coreclr'),
      expectedLabels,
      options.repositoryRoot,
    )
    : image.Id
  return { localTag, digest }
}

function frameworkManifest(repositoryRoot) {
  const document = readJson(path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json'), 'Framework installer manifest')
  if (document?.schemaVersion !== 1 || !Array.isArray(document.targets) ||
      JSON.stringify(document.targets.map(target => target.id)) !== JSON.stringify(frameworkIds)) {
    fail('Framework installer manifest does not contain the canonical 14 rows')
  }
  return document
}

function baseImage(repositoryRoot, id) {
  const document = readJson(path.join(repositoryRoot, 'profiles', 'base-images.json'), 'base image manifest')
  const image = document?.images?.find(candidate => candidate.id === id)
  if (!digestReferencePattern.test(image?.reference ?? '')) fail(`Base image '${id}' is missing or not digest-pinned`)
  return image.reference
}

async function buildFrameworkOperators(
  options,
  sourceRevision,
  wineDigest,
  downloads,
  targetIds = frameworkIds,
  seedGenerations = undefined,
) {
  const manifest = frameworkManifest(options.repositoryRoot)
  const selectedTargetIds = new Set(targetIds)
  const selectedTargets = manifest.targets.filter(candidate => selectedTargetIds.has(candidate.id))
  const requiredSeedGenerations = new Set(seedGenerations ?? selectedTargets.map(target =>
    target.clrGeneration === 'clr2' ? 'clr4' : 'clr2'))
  const rootImage = baseImage(options.repositoryRoot, 'dotnet-runtime-deps')
  if (requiredSeedGenerations.size === 0) {
    return {
      manifest,
      rootImage,
      references: new Map(),
      seedInputSha256: undefined,
      seedReferences: new Map(),
    }
  }
  const preparationScript = path.join(options.repositoryRoot, 'eng', 'prepare-framework-runtime.cs')
  runWithRetry('dotnet', ['build', preparationScript, '--nologo'], { cwd: options.repositoryRoot })
  const seedSpec = await createFrameworkSeedBuildSpec(
    options.repositoryRoot,
    wineDigest,
    rootImage,
  )
  const commonTag = `${options.imagePrefix}/framework-wow64-base:content`
  const commonRegistryTag = registryImageTag(options, 'framework-wow64-base')
  const commonArguments = [
    'run', preparationScript, '--no-build', '--',
    '--build-kind', 'wow64-base',
    '--repository-root', options.repositoryRoot,
    '--base-image', wineDigest,
    '--root-image', rootImage,
    '--output-image', commonTag,
    '--source-revision', sourceRevision,
    '--seed-input-sha256', seedSpec.inputSha256,
    '--accept-microsoft-dotnet-framework-eula',
  ]
  if (options.allowUncommittedSourceForDevelopment) {
    commonArguments.push('--allow-uncommitted-source-for-development')
  }
  let commonImage = tryReuseLocalImage(
    commonTag,
    {
      'io.sharplabnext.framework.build-role': 'wow64-base',
      'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    },
    options.repositoryRoot,
    options.reuseExisting,
  )
  if (commonImage === undefined) {
    runWithRetry('dotnet', commonArguments, { cwd: options.repositoryRoot })
    commonImage = inspectImage(commonTag, options.repositoryRoot)
    validateImageInspection(
      commonImage,
      commonTag,
      {
        'io.sharplabnext.framework.build-role': 'wow64-base',
        'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
        'io.sharplabnext.operator-only': 'true',
        'io.sharplabnext.redistribution': 'operator-supplied-only',
      },
      'Built image',
    )
  }
  const commonDigest = publishImmutableImage(
    commonTag,
    commonRegistryTag,
    {
      'io.sharplabnext.framework.build-role': 'wow64-base',
      'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    },
    options.repositoryRoot,
  )

  const seedReferences = new Map()
  const missingSeeds = []
  for (const seed of seedSpec.images.filter(candidate => requiredSeedGenerations.has(candidate.generation))) {
    const localTag = `${options.imagePrefix}/framework-companion-seed-${seed.generation}:content`
    const registryTag = registryImageTag(options, `framework-companion-seed-${seed.generation}`)
    const expectedLabels = {
      'io.sharplabnext.framework.build-role': 'companion-seed',
      'io.sharplabnext.framework.seed-schema': 'framework-companion-seed-v1',
      'io.sharplabnext.framework.seed-generation': seed.generation,
      'io.sharplabnext.framework.seed-version': seed.version,
      'io.sharplabnext.framework.seed-prefix': seed.prefix,
      'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.framework.installer-manifest-sha256': seedSpec.manifestSha256,
      'io.sharplabnext.framework.wow64-base-image': commonDigest,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    }
    const cached = tryReuseLocalImage(
      localTag,
      expectedLabels,
      options.repositoryRoot,
      options.reuseExisting,
    )
    if (cached !== undefined) {
      seedReferences.set(seed.generation, {
        ...seed,
        reference: localTag,
        digest: publishImmutableImage(
          localTag,
          registryTag,
          expectedLabels,
          options.repositoryRoot,
        ),
      })
      continue
    }
    missingSeeds.push({ seed, localTag, registryTag, expectedLabels })
  }

  const seedTasks = missingSeeds.map(({ seed, localTag }) => ({
    label: `Framework companion seed '${seed.id}'`,
    run: async () => {
      const arguments_ = [
        'run', preparationScript, '--no-build', '--',
        '--build-kind', 'companion-seed',
        '--repository-root', options.repositoryRoot,
        '--seed-generation', seed.generation,
        '--framework-wow64-base-image', commonDigest,
        '--base-image', wineDigest,
        '--root-image', rootImage,
        '--output-image', localTag,
        '--source-revision', sourceRevision,
        '--seed-input-sha256', seedSpec.inputSha256,
        '--accept-microsoft-dotnet-framework-eula',
      ]
      if (seed.generation === 'clr2') {
        arguments_.push(
          '--cached-winetricks-payload-file',
          downloads['netfx35sp1-installer'],
        )
      }
      if (options.allowUncommittedSourceForDevelopment) {
        arguments_.push('--allow-uncommitted-source-for-development')
      }
      await startWithRetry('dotnet', arguments_, { cwd: options.repositoryRoot })
    },
  }))
  await runParallel(seedTasks, 2)

  for (const { seed, localTag, registryTag, expectedLabels } of missingSeeds) {
    seedReferences.set(
      seed.generation,
      {
        ...seed,
        reference: localTag,
        digest: publishImmutableImage(
          localTag,
          registryTag,
          expectedLabels,
          options.repositoryRoot,
        ),
      },
    )
  }

  const references = new Map()
  const missingTargets = []
  for (const target of selectedTargets) {
    const tag = `${options.imagePrefix}/operator-${target.id}:content`
    const registryTag = registryImageTag(options, `operator-${target.id}`)
    const seed = seedReferences.get(target.clrGeneration === 'clr2' ? 'clr4' : 'clr2')
    if (seed === undefined) fail(`Framework target '${target.id}' has no companion seed`)
    const expectedLabels = {
      'org.opencontainers.image.title': 'SharpLabNext Operator Wine .NET Framework Matrix',
      'org.opencontainers.image.version': target.version,
      'org.opencontainers.image.revision': sourceRevision,
      [sourceRevisionLabel]: sourceRevision,
      'io.sharplabnext.runtime.framework': `.NETFramework,Version=v${target.version}`,
      'io.sharplabnext.runtime.framework-version': target.version,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
      'io.sharplabnext.framework.target-id': target.id,
      'io.sharplabnext.framework.version': target.version,
      'io.sharplabnext.framework.clr-generation': target.clrGeneration,
      'io.sharplabnext.framework.companion-seed-image': seed.digest,
      'io.sharplabnext.framework.companion-seed-generation': seed.generation,
      'io.sharplabnext.framework.companion-seed-version': seed.version,
      'io.sharplabnext.framework.companion-seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.framework.installer-manifest-sha256': seedSpec.manifestSha256,
      'io.sharplabnext.wine-prefix-layout': 'hardlink-immutable-v1',
      'io.sharplabnext.wine-prefix-layout-manifest': '/opt/sharplabnext/.wine-prefix-layout.json',
      'io.sharplabnext.operator-base': wineDigest,
      'io.sharplabnext.operator-root': rootImage,
    }
    const cached = tryReuseLocalImage(
      tag,
      expectedLabels,
      options.repositoryRoot,
      options.reuseExisting,
    )
    if (cached !== undefined) {
      references.set(target.id, publishImmutableImage(
        tag,
        registryTag,
        expectedLabels,
        options.repositoryRoot,
      ))
    } else {
      missingTargets.push({ target, tag, registryTag, seed, expectedLabels })
    }
  }

  const tasks = missingTargets.map(({ target, tag, registryTag, seed, expectedLabels }) => ({
    label: `Framework operator '${target.id}'`,
    run: async () => {
      const arguments_ = [
        'run', preparationScript, '--no-build', '--',
        '--repository-root', options.repositoryRoot,
        '--target-id', target.id,
        '--base-image', wineDigest,
        '--root-image', rootImage,
        '--framework-seed-image', seed.digest,
        '--seed-input-sha256', seedSpec.inputSha256,
        '--output-image', tag,
        '--source-revision', sourceRevision,
        '--accept-microsoft-dotnet-framework-eula',
      ]
      if (options.allowUncommittedSourceForDevelopment) arguments_.push('--allow-uncommitted-source-for-development')
      if (target.id === 'netfx451') arguments_.push('--installer-secret-file', downloads['netfx451-installer'])
      if (target.id === 'netfx47') arguments_.push('--installer-secret-file', downloads['netfx47-installer'])
      if (target.recipe.kind === 'winetricks' && target.recipe.verb === 'dotnet35sp1') {
        arguments_.push(
          '--cached-winetricks-payload-file',
          downloads['netfx35sp1-installer'],
        )
      }
      await startWithRetry('dotnet', arguments_, { cwd: options.repositoryRoot })
      references.set(target.id, publishImmutableImage(
        tag,
        registryTag,
        expectedLabels,
        options.repositoryRoot,
      ))
    },
  }))
  await runParallel(tasks, Math.min(options.maximumParallel, 2))
  return {
    manifest,
    rootImage,
    references,
    seedInputSha256: seedSpec.inputSha256,
    seedReferences,
  }
}

async function buildOperatorImages(
  options,
  prerequisiteState,
  framework,
  requiredIds,
) {
  const manifest = readPrerequisiteManifest(
    path.join(options.repositoryRoot, 'eng', 'release-prerequisites.json'),
  )
  const frameworkSeeds = {
    clr2: framework.seedReferences.get('clr2')?.digest,
    clr4: framework.seedReferences.get('clr4')?.digest,
  }
  const spec = await createOperatorImageBuildSpec(
    options.repositoryRoot,
    manifest,
    frameworkSeeds,
  )
  const jsharpScript = path.join(
    options.repositoryRoot,
    'eng',
    'prepare-jsharp-toolchain.cs',
  )
  const cppcliScript = path.join(
    options.repositoryRoot,
    'eng',
    'prepare-cppcli-toolchain.cs',
  )
  const jsharp = prerequisiteState.generatedImages['jsharp20-development-base']
  const cppcli = prerequisiteState.generatedImages['cppcli-prepared-base']
  if (jsharp?.buildKind !== 'jsharp20' || cppcli?.buildKind !== 'cppcli') {
    fail('Prerequisite state does not contain the canonical generated images')
  }
  const tasks = [
    {
      id: 'jsharp20-development-base',
      script: jsharpScript,
      label: "Source-built operator image 'jsharp20'",
      run: () => startWithRetry('dotnet', [
        'run', jsharpScript, '--no-build', '--',
        '--repository-root', options.repositoryRoot,
        '--framework-seed-image', frameworkSeeds.clr2,
        '--output-image', jsharp.reference,
        '--operator-build-input-sha256', spec.inputSha256,
        '--accept-microsoft-dotnet-eula',
        '--accept-microsoft-jsharp-eula',
      ], { cwd: options.repositoryRoot }),
    },
    {
      id: 'cppcli-prepared-base',
      script: cppcliScript,
      label: "Source-built operator image 'cppcli'",
      run: () => startWithRetry('dotnet', [
        'run', cppcliScript, '--no-build', '--',
        '--repository-root', options.repositoryRoot,
        '--framework-seed-image', frameworkSeeds.clr4,
        '--output-image', cppcli.reference,
        '--msvc-wine-source',
        prerequisiteState.downloads['msvc-wine-source'],
        '--visual-studio-manifest',
        prerequisiteState.downloads['visual-studio-manifest'],
        '--netfx48-developer-pack',
        prerequisiteState.downloads['netfx48-developer-pack'],
        '--operator-build-input-sha256', spec.inputSha256,
        '--accept-microsoft-cpp-build-tools-license',
        '--accept-microsoft-dotnet-eula',
      ], { cwd: options.repositoryRoot }),
    },
  ]

  const result = {}
  const required = new Set(requiredIds)
  const missingTasks = []
  const imageMetadata = new Map()
  for (const image of spec.images.filter(candidate => required.has(candidate.id))) {
    const seed = image.buildKind === 'jsharp20'
      ? frameworkSeeds.clr2
      : frameworkSeeds.clr4
    const expectedLabels = {
      'io.sharplabnext.operator-build.strategy': 'source-built-operator-image-v1',
      'io.sharplabnext.operator-build.input-sha256': spec.inputSha256,
      'io.sharplabnext.operator-build.image-id': image.id,
      'io.sharplabnext.operator-build.build-kind': image.buildKind,
      'io.sharplabnext.operator-build.framework-seed-image': seed,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    }
    const registryTag = registryImageTag(options, image.id)
    imageMetadata.set(image.id, { localTag: image.reference, registryTag, expectedLabels })
    const cached = tryReuseLocalImage(
      image.reference,
      expectedLabels,
      options.repositoryRoot,
      options.reuseExisting,
    )
    if (cached !== undefined) {
      result[image.id] = publishImmutableImage(
        image.reference,
        registryTag,
        expectedLabels,
        options.repositoryRoot,
      )
      continue
    }
    const task = tasks.find(candidate => candidate.id === image.id)
    if (task === undefined) fail(`Operator image '${image.id}' has no build task`)
    missingTasks.push(task)
  }
  for (const script of [...new Set(missingTasks.map(task => task.script))]) {
    runWithRetry('dotnet', ['build', script, '--nologo'], { cwd: options.repositoryRoot })
  }
  await runParallel(missingTasks, 2)

  for (const task of missingTasks) {
    const image = spec.images.find(candidate => candidate.id === task.id)
    if (image === undefined) fail(`Operator image '${task.id}' has no build specification`)
    const metadata = imageMetadata.get(image.id)
    if (metadata === undefined) fail(`Operator image '${image.id}' has no cache metadata`)
    result[image.id] = publishImmutableImage(
      image.reference,
      metadata.registryTag,
      metadata.expectedLabels,
      options.repositoryRoot,
    )
  }
  return result
}

function createFrameworkMatrixInput(options, built) {
  const rows = built.manifest.targets.map(target => ({
    id: target.id,
    version: target.version,
    clrGeneration: target.clrGeneration,
    targetPrefix: target.clrGeneration,
    companionVersions: target.clrGeneration === 'clr2'
      ? { clr2: target.version, clr4: '4.8' }
      : { clr2: '3.5', clr4: target.version },
    operatorImage: built.references.get(target.id),
  }))
  const value = { schemaVersion: 1, strategy: 'shared-framework-prefix-input-v1', rows }
  const bytes = `${JSON.stringify(value)}\n`
  const filename = path.join(options.repositoryRoot, 'artifacts', 'prerequisites', 'generated', 'framework-matrix-input.json')
  atomicWrite(filename, bytes)
  return { filename, sha256: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}` }
}

function buildFrameworkControlImages(options, sourceRevision, wineDigest, built, matrixInput) {
  const generatedRoot = path.join(options.repositoryRoot, 'artifacts', 'prerequisites', 'generated')
  const metadataTag = `${options.imagePrefix}/operator-framework-metadata:content`
  const metadataRegistryTag = registryImageTag(options, 'operator-framework-metadata')
  const metadataLabels = {
    'io.sharplabnext.framework.matrix-context': 'true',
    'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
    'io.sharplabnext.framework.matrix-input-sha256': matrixInput.sha256,
    'io.sharplabnext.framework.matrix-row-count': '14',
    'org.opencontainers.image.revision': sourceRevision,
    'io.sharplabnext.source.revision': sourceRevision,
  }
  let metadataImage = tryReuseLocalImage(
    metadataTag,
    metadataLabels,
    options.repositoryRoot,
    options.reuseExisting,
  )
  if (metadataImage === undefined) {
    const contextArguments = [
      path.join(options.repositoryRoot, 'eng', 'build-framework-matrix-context.mjs'),
      '--matrix-input', matrixInput.filename,
      '--source-revision', sourceRevision,
      '--image', metadataTag,
      '--version', options.releaseId,
    ]
    if (options.allowUncommittedSourceForDevelopment) contextArguments.push('--allow-uncommitted-source-for-development')
    runWithRetry(process.execPath, contextArguments, { cwd: options.repositoryRoot })
    metadataImage = inspectImage(metadataTag, options.repositoryRoot)
    validateImageInspection(metadataImage, metadataTag, metadataLabels, 'Built image')
  }
  const metadataDigest = publishImmutableImage(
    metadataTag,
    metadataRegistryTag,
    metadataLabels,
    options.repositoryRoot,
  )

  const parentTag = `${options.imagePrefix}/operator-framework-parent:content`
  const parentRegistryTag = registryImageTag(options, 'operator-framework-parent')
  const parentLabels = {
    'io.sharplabnext.framework.matrix': 'true',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-target-prefix-matrix-v1',
    'io.sharplabnext.framework.dedupe-policy': 'wine-static-runtime-payload-v1',
    'org.opencontainers.image.revision': sourceRevision,
    'io.sharplabnext.source.revision': sourceRevision,
    'io.sharplabnext.framework.matrix-input-sha256': matrixInput.sha256,
    'io.sharplabnext.framework.matrix-source-uri': `docker://${metadataDigest}`,
    'io.sharplabnext.operator-image.wine': wineDigest,
    'io.sharplabnext.operator-root': built.rootImage,
  }
  let parentImage = tryReuseLocalImage(
    parentTag,
    parentLabels,
    options.repositoryRoot,
    options.reuseExisting,
  )
  if (parentImage === undefined) {
    const parentArguments = [
      path.join(options.repositoryRoot, 'eng', 'build-framework-matrix-parent.mjs'),
      '--root-image', built.rootImage,
      '--wine-image', wineDigest,
      '--framework-matrix-source-uri', `docker://${metadataDigest}`,
      '--framework-matrix-input-sha256', matrixInput.sha256,
      '--source-revision', sourceRevision,
      '--image', parentTag,
      '--version', options.releaseId,
    ]
    if (options.allowUncommittedSourceForDevelopment) parentArguments.push('--allow-uncommitted-source-for-development')
    runWithRetry(process.execPath, parentArguments, { cwd: options.repositoryRoot })
    parentImage = inspectImage(parentTag, options.repositoryRoot)
    validateImageInspection(parentImage, parentTag, parentLabels, 'Built image')
  }
  const parentDigest = publishImmutableImage(
    parentTag,
    parentRegistryTag,
    parentLabels,
    options.repositoryRoot,
  )

  const candidateInput = path.join(generatedRoot, 'runtime-framework-candidates.json')
  fs.rmSync(candidateInput, { force: true })
  run(process.execPath, [
    path.join(options.repositoryRoot, 'eng', 'create-runtime-framework-candidate-input.mjs'),
    '--parent-image', parentDigest,
    '--metadata-image', metadataDigest,
    '--matrix-input', matrixInput.filename,
    '--source-revision', sourceRevision,
    '--output', candidateInput,
    ...(options.allowUncommittedSourceForDevelopment
      ? ['--allow-uncommitted-source-for-development']
      : []),
  ], { cwd: options.repositoryRoot })
  return { candidateInput, metadataDigest, parentDigest }
}

function buildBakeTargets(options, sourceRevision, operatorImages, targets, environmentSnapshot = undefined) {
  if (targets.length === 0) return
  runInBakeEnvironment(options, sourceRevision, operatorImages, 'docker', [
    'buildx', 'bake', '--file', path.join(options.repositoryRoot, 'eng', 'bake.hcl'), ...targets,
  ], environmentSnapshot)
}

export async function buildRuntimeCandidates(
  options,
  sourceRevision,
  operatorImages,
  images,
  wine,
  framework,
  operations = {},
) {
  const resolveEnvironment = operations.resolveBakeEnvironmentSnapshot ??
    resolveBakeEnvironmentSnapshot
  const startCandidate = operations.start ?? startWithRetry
  const snapshot = operations.environmentSnapshot ??
    resolveEnvironment(options, sourceRevision, operatorImages)
  const environment = createBakeChildEnvironment(
    snapshot,
    options,
    operations.parentEnvironment ?? process.env,
    operatorImages,
  )
  const tasks = images.map(image => ({
    label: `Runtime candidate '${image.producer.id}'`,
    run: async () => {
      const profileId = image.producer.id
      const arguments_ = [path.join(options.repositoryRoot, 'eng', 'runtime-candidate-environment.mjs'), profileId]
      if (profileId.startsWith('wine-dotnet-')) {
        arguments_.push('--wine-image', wine.localTag)
      } else if (profileId.startsWith('wine-netfx')) {
        arguments_.push('--wine-image', wine.digest, '--framework-input', framework.candidateInput)
      }
      arguments_.push('--', '--allow-development-image-inputs')
      if (options.allowUncommittedSourceForDevelopment) arguments_.push('--allow-uncommitted-source-for-development')
      await startCandidate(process.execPath, arguments_, {
        cwd: options.repositoryRoot,
        env: environment,
      })
    },
  }))
  await runParallel(tasks, options.maximumParallel)
}

function validateFinalImageInspection(planned, image, plan, sourceRevision) {
  const labels = image.Config?.Labels ?? {}
  if (!imageIdPattern.test(image.Id ?? '') || image.Os !== 'linux' || image.Architecture !== 'amd64') {
    fail(`Final image '${planned.id}' is not one immutable linux/amd64 image`)
  }
  if (labels[versionLabel] !== plan.releaseId) {
    fail(`Final image '${planned.id}' does not carry release label '${plan.releaseId}'`)
  }
  if (planned.producer.kind !== 'pull') {
    if (labels[sourceRevisionLabel] !== sourceRevision) fail(`Final image '${planned.id}' does not carry source revision '${sourceRevision}'`)
    if (labels[developmentInputsLabel] !== 'true') fail(`Final image '${planned.id}' is missing the development image-input marker`)
  }
  return labels
}

// A failed bundle must not invalidate images that were already built.  Keep
// this probe deliberately local: BuildKit owns layer reuse and a cache check
// must never turn into an implicit registry pull (especially on SSH sessions).
function finalImageAliases(planned) {
  if (planned.producer.kind === 'pull') return [planned.reference]
  const repository = imageRepository(planned.reference)
  const aliases = [planned.reference]
  if (planned.producer.kind === 'runtime-candidate') aliases.push(`${repository}:candidate`)
  aliases.push(`${repository}:content`)
  return aliases
}

function tryReuseFinalImage(planned, plan, sourceRevision, repositoryRoot) {
  for (const reference of finalImageAliases(planned)) {
    const image = tryInspectImage(reference, repositoryRoot)
    if (image === undefined) continue
    try {
      validateFinalImageInspection(planned, image, plan, sourceRevision)
    } catch {
      continue
    }
    if (reference !== planned.reference) {
      runWithRetry('docker', ['image', 'tag', reference, planned.reference], { cwd: repositoryRoot })
    }
    return image
  }
  return undefined
}

function partitionPlannedImages(options, plan, sourceRevision, forceRebuild = false) {
  const cached = new Map()
  const missing = []
  for (const planned of plan.images) {
    // Pull-only entries are independent of the repository source. Keep them
    // reusable even when a source-input fingerprint invalidates build outputs.
    if (planned.producer.kind === 'pull' && options.reuseExisting !== false) {
      const image = tryInspectImage(planned.reference, options.repositoryRoot)
      if (image !== undefined) {
        try {
          validateFinalImageInspection(planned, image, plan, sourceRevision)
          cached.set(planned.id, image)
        } catch {
          missing.push(planned)
        }
      } else missing.push(planned)
      continue
    }
    if (options.reuseExisting === false || forceRebuild) {
      missing.push(planned)
      continue
    }
    const image = tryReuseFinalImage(planned, plan, sourceRevision, options.repositoryRoot)
    if (image === undefined) missing.push(planned)
    else cached.set(planned.id, image)
  }
  return { cached, missing }
}

function verifyFinalImages(options, sourceRevision, plan, imagePlanDigest) {
  const result = []
  for (const planned of plan.images) {
    const image = inspectImage(planned.reference, options.repositoryRoot)
    validateFinalImageInspection(planned, image, plan, sourceRevision)
    if (planned.producer.kind !== 'pull') {
      const contentAlias = `${imageRepository(planned.reference)}:content`
      if (contentAlias !== planned.reference) {
        runWithRetry('docker', [
          'image', 'tag', planned.reference, contentAlias,
        ], { cwd: options.repositoryRoot })
      }
    }
    result.push({ id: planned.id, reference: planned.reference, imageId: image.Id, producer: planned.producer })
  }
  const output = path.join(options.repositoryRoot, 'artifacts', 'release-images.json')
  atomicWrite(output, `${JSON.stringify({
    schemaVersion: 2,
    releaseId: plan.releaseId,
    sourceRevision,
    imagePlanDigest: imagePlanDigest,
    developmentImageInputs: true,
    images: result,
  }, null, 2)}\n`)
  return output
}

function parseArguments(argv) {
  const result = {
    repositoryRoot: defaultRepositoryRoot,
    imagePrefix: 'sharplabnext',
    sourceRevision: undefined,
    maximumParallel: Math.max(1, Math.min(4, Math.floor((Number(process.env.NUMBER_OF_PROCESSORS) || 8) / 2))),
    allowUncommittedSourceForDevelopment: false,
    acceptMicrosoftLicenses: false,
    offline: false,
    planOnly: false,
    cacheProbe: false,
    reuseExisting: true,
  }
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index]
    if (argument === '--allow-uncommitted-source-for-development') { result.allowUncommittedSourceForDevelopment = true; continue }
    if (argument === '--accept-microsoft-licenses') { result.acceptMicrosoftLicenses = true; continue }
    if (argument === '--offline') { result.offline = true; continue }
    if (argument === '--plan-only') { result.planOnly = true; continue }
    if (argument === '--cache-probe') { result.cacheProbe = true; continue }
    if (argument === '--no-reuse-existing') { result.reuseExisting = false; continue }
    if (argument === '--help' || argument === '-h') return { help: true }
    const field = {
      '--repository-root': 'repositoryRoot',
      '--image-prefix': 'imagePrefix',
      '--source-revision': 'sourceRevision',
      '--max-parallel': 'maximumParallel',
    }[argument]
    if (field === undefined) fail(`Unknown build-images argument '${argument}'`)
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail(`${argument} requires a value`)
    result[field] = field === 'maximumParallel' ? Number(value) : value
  }
  result.repositoryRoot = path.resolve(result.repositoryRoot)
  if (!fs.existsSync(path.join(result.repositoryRoot, 'SharpLabNext.slnx'))) fail('Repository root does not contain SharpLabNext.slnx')
  if (!/^[a-z0-9][a-z0-9._/-]{0,255}$/.test(result.imagePrefix) || result.imagePrefix.endsWith('/')) fail('--image-prefix is invalid')
  if (result.sourceRevision !== undefined && !sourceRevisionPattern.test(result.sourceRevision)) fail('--source-revision must be a full Git revision')
  if (!Number.isSafeInteger(result.maximumParallel) || result.maximumParallel < 1 || result.maximumParallel > 8) fail('--max-parallel must be an integer from 1 through 8')
  return result
}

function usage() {
  return `Usage: node eng/build-images.mjs [--repository-root PATH] [--image-prefix PREFIX]\n` +
    `  [--source-revision COMMIT] [--max-parallel 1..8] [--offline]\n` +
    `  [--allow-uncommitted-source-for-development] [--plan-only] [--cache-probe]\n` +
    `  [--no-reuse-existing]\n` +
    `  --accept-microsoft-licenses`
}

export async function runBuildImages(argv, output = console) {
  try {
    const options = parseArguments(argv)
    if (options.help) { output.log(usage()); return 0 }
    const sourceRevision = resolveSourceRevision(options)
    const imagePlan = generateImagePlan(options, sourceRevision)
    options.releaseId = imagePlan.plan.releaseId
    const sourceInputDigest = resolveBuildCacheInputDigest(options)
    const cacheIdentity = createBuildCacheIdentity(
      options,
      imagePlan,
      sourceRevision,
      sourceInputDigest,
    )
    const previousCacheState = readBuildCacheState(options)
    // A clean commit already identifies the source input. Only a dirty
    // development tree needs the extra fingerprint gate; otherwise an
    // unrelated README/test change would disable every local image shortcut.
    const cacheStateMismatch = options.allowUncommittedSourceForDevelopment &&
      !buildCacheStateMatches(previousCacheState, cacheIdentity)
    const counts = Object.fromEntries(['bake', 'runtime-candidate', 'pull'].map(kind => [kind, imagePlan.plan.images.filter(image => image.producer.kind === kind).length]))
    output.log(`Release image plan: ${imagePlan.plan.images.length} images (${counts.bake} Bake, ${counts['runtime-candidate']} runtime candidates, ${counts.pull} immutable pulls).`)
    if (options.planOnly) return 0
    if (!options.acceptMicrosoftLicenses) fail('--accept-microsoft-licenses is required because the selected Catalog includes Microsoft proprietary runtime/toolchain inputs')

    const imageState = partitionPlannedImages(
      options,
      imagePlan.plan,
      sourceRevision,
      cacheStateMismatch,
    )
    output.log(`Image cache: ${imageState.cached.size} hit, ${imageState.missing.length} to build.`)
    if (options.cacheProbe) {
      const hit = imageState.missing.length === 0
      output.log(`${imageCacheProbePrefix}${hit ? 'hit' : 'miss'}`)
      if (hit) recordBuildCacheState(options, cacheIdentity, sourceInputDigest)
      return 0
    }
    if (imageState.missing.length === 0) {
      const validationPath = verifyFinalImages(options, sourceRevision, imagePlan.plan, imagePlan.digest)
      recordBuildCacheState(options, cacheIdentity, sourceInputDigest)
      output.log(`All planned release images were already present and validated. Identity record: ${validationPath}`)
      return 0
    }

    const missingIds = new Set(imageState.missing.map(image => image.id))
    const bakeTargets = [...new Set(imagePlan.plan.images
      .filter(image => image.producer.kind === 'bake' && missingIds.has(image.id))
      .map(image => image.producer.id))].sort()
    const candidates = imagePlan.plan.images.filter(
      image => image.producer.kind === 'runtime-candidate' && missingIds.has(image.id),
    )
    const capabilities = resolveBuildCapabilities(imageState.missing)
    output.log(`Build capabilities: ${capabilities.size === 0 ? 'none' : [...capabilities].join(', ')}`)

    runWithRetry('dotnet', [
      'run', path.join(options.repositoryRoot, 'eng', 'verify-buildkit.cs'),
    ], { cwd: options.repositoryRoot })
    verifyLocalImageBuildDriver(options.repositoryRoot)

    for (const image of imageState.missing.filter(image => image.producer.kind === 'pull')) {
      runWithRetry('docker', ['image', 'pull', image.reference], { cwd: options.repositoryRoot })
    }
    if (bakeTargets.length === 0 && candidates.length === 0) {
      const validationPath = verifyFinalImages(options, sourceRevision, imagePlan.plan, imagePlan.digest)
      recordBuildCacheState(options, cacheIdentity, sourceInputDigest)
      output.log(`Fetched and validated every planned release image. Identity record: ${validationPath}`)
      return 0
    }

    let prerequisiteState
    const needsOperatorInputs = capabilities.has('framework') ||
      capabilities.has('jsharp') || capabilities.has('cppcli')
    const prerequisiteManifest = needsOperatorInputs
      ? readPrerequisiteManifest(path.join(options.repositoryRoot, 'eng', 'release-prerequisites.json'))
      : undefined
    if (needsOperatorInputs) {
      const prerequisiteOutput = {
        logs: [],
        log(value) { this.logs.push(String(value)); output.log(value) },
        error(value) { output.error(value) },
      }
      const prerequisiteArguments = [
        'prepare', '--repository-root', options.repositoryRoot, '--accept-microsoft-licenses',
      ]
      if (options.offline) prerequisiteArguments.push('--offline')
      const prerequisiteStatus = await runPrerequisiteCache(prerequisiteArguments, prerequisiteOutput)
      if (prerequisiteStatus !== 0) fail('Prerequisite preparation failed')
      prerequisiteState = JSON.parse(prerequisiteOutput.logs.at(-1))
    }
    const frameworkCandidates = candidates.filter(image =>
      /^wine-netfx/.test(image.producer.id))
    const requiredOperatorIds = []
    if (capabilities.has('jsharp')) requiredOperatorIds.push('jsharp20-development-base')
    if (capabilities.has('cppcli')) requiredOperatorIds.push('cppcli-prepared-base')
    const needsFrameworkSeeds = frameworkCandidates.length > 0 || requiredOperatorIds.length > 0
    if (needsFrameworkSeeds) {
      await ensureLocalRegistry(
        prerequisiteState?.localRegistry ?? prerequisiteManifest?.value.localRegistry,
        options.repositoryRoot,
      )
    }

    // Resolve the lock/catalog environment once. Every later build receives
    // this immutable snapshot, avoiding repeated file-app compilation and temp
    // directory races between parallel candidates.
    const bakeEnvironmentSnapshot = resolveBakeEnvironmentSnapshot(
      options,
      sourceRevision,
      undefined,
    )
    let wine = {}
    let operators = { seedReferences: new Map(), references: new Map() }
    let operatorImages = {}
    let framework = {}
    if (capabilities.has('wine')) {
      wine = buildWineOperator(
        options,
        sourceRevision,
        bakeEnvironmentSnapshot,
        needsFrameworkSeeds,
      )
    }
    if (needsFrameworkSeeds) {
      operators = await buildFrameworkOperators(
        options,
        sourceRevision,
        wine.digest,
        prerequisiteState?.downloads,
        frameworkCandidates.length > 0 ? frameworkIds : [],
        requiredOperatorIds.length > 0 ? ['clr2', 'clr4'] : undefined,
      )
    }
    if (requiredOperatorIds.length > 0) {
      operatorImages = await buildOperatorImages(
        options,
        prerequisiteState,
        operators,
        requiredOperatorIds,
      )
    }
    if (frameworkCandidates.length > 0) {
      const matrixInput = createFrameworkMatrixInput(options, operators)
      framework = buildFrameworkControlImages(
        options,
        sourceRevision,
        wine.digest,
        operators,
        matrixInput,
      )
    }

    buildBakeTargets(
      options,
      sourceRevision,
      operatorImages,
      bakeTargets,
      bakeEnvironmentSnapshot,
    )

    await buildRuntimeCandidates(
      options,
      sourceRevision,
      operatorImages,
      candidates,
      wine,
      framework,
      { environmentSnapshot: bakeEnvironmentSnapshot },
    )
    const validationPath = verifyFinalImages(options, sourceRevision, imagePlan.plan, imagePlan.digest)
    recordBuildCacheState(options, cacheIdentity, sourceInputDigest)
    output.log(`Built and validated every planned release image. Identity record: ${validationPath}`)
    return 0
  } catch (error) {
    output.error(`Build images failed: ${error.message}`)
    return error instanceof BuildImagesError ? 1 : 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await runBuildImages(process.argv.slice(2))
}
