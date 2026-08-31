/**
 * Build the bounded Linux-only metadata image consumed by the shared
 * Framework matrix parent.
 *
 * Operator prefixes remain in their digest-pinned source images. The parent
 * later exposes them through named BuildKit bind mounts, so neither this
 * image nor the Windows host ever carries a raw prefix copy.
 */

import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import {
  isDigestPinnedImageReference,
  isGitCommitIdentity,
  isSha256Digest,
} from './runtime-candidate-input-validation.mjs'
import { pinnedDockerfileFrontendDirective } from './dockerfile-frontend.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const installerManifestPath = path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json')
const matrixStrategy = 'shared-framework-prefix-input-v1'
const sourceIdentityModeEnvironmentVariable = 'SHARPLABNEXT_SOURCE_IDENTITY_MODE'
const contentSourceIdentityMode = 'content'
const maximumMetadataBytes = 1024 * 1024
const maximumMetadataImageBytes = 16 * 1024 * 1024
const metadataContentKind = 'metadata-only-v1'
const safeId = /^[a-z0-9][a-z0-9._-]{0,127}$/
const versionPattern = /^\d+(?:\.\d+){1,2}$/
const imageTag = /^(?:[A-Za-z0-9][A-Za-z0-9._-]*(?::[0-9]+)?\/)?[A-Za-z0-9][A-Za-z0-9._/-]*(?::[A-Za-z0-9][A-Za-z0-9._-]*)?$/
const imageDigest = /^sha256:[0-9a-f]{64}$/
// The shared candidate validator intentionally accepts a broad Docker
// reference grammar.  A generated Dockerfile needs the narrower subset below
// so an operator-supplied reference can never introduce a parser boundary.
const safeDigestReference = /^[A-Za-z0-9][A-Za-z0-9._:/-]*@sha256:[0-9a-f]{64}$/
const requiredFrameworkRows = Object.freeze(['netfx20', 'netfx30', 'netfx35', 'netfx40', 'netfx45', 'netfx451', 'netfx452', 'netfx46', 'netfx461', 'netfx462', 'netfx47', 'netfx471', 'netfx472', 'netfx48'])

function fail(message) { throw new Error(message); }

function hasRegistryHost(reference) {
  if (typeof reference !== 'string') return false
  const at = reference.indexOf('@')
  if (at <= 0) return false
  const repository = reference.slice(0, at)
  if (!imageTag.test(repository)) return false
  const slash = repository.indexOf('/')
  if (slash <= 0) return false
  const host = repository.slice(0, slash)
  return host === 'localhost' || host.includes('.') || host.includes(':')
}

function isSafeDigestReference(value) { return isDigestPinnedImageReference(value) && safeDigestReference.test(value); }

function imageRepository(value) {
  const at = value.indexOf('@')
  const withoutDigest = at < 0 ? value : value.slice(0, at)
  const slash = withoutDigest.lastIndexOf('/')
  const colon = withoutDigest.lastIndexOf(':')
  return colon > slash ? withoutDigest.slice(0, colon) : withoutDigest
}

export function normalizeDigestPinnedImageIdentity(value) {
  if (!isSafeDigestReference(value)) return undefined
  const at = value.lastIndexOf('@')
  return `${imageRepository(value)}@${value.slice(at + 1)}`
}

function readRegularJson(filename, label) {
  let stat
  try { stat = fs.lstatSync(filename) } catch { fail(`${label} does not exist`) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maximumMetadataBytes) {
    fail(`${label} must be a bounded regular file`)
  }
  let value
  try { value = JSON.parse(fs.readFileSync(filename, 'utf8')) } catch (error) {
    fail(`${label} is invalid JSON: ${error.message}`)
  }
  return value
}

function canonicalJson(value) { return `${JSON.stringify(value)}\n`; }

function rowMetadata(row) {
  // matrix-input.json owns the document schema; each copied row also carries
  // an explicit schema marker so the parent can reject mixed-generation
  // context images before touching any prefix bytes.
  return { schemaVersion: 1, ...row }
}

/** Validate and normalize the existing matrix-input contract. */
export function normalizeMatrixInput(document) {
  if (document?.schemaVersion !== 1 || document.strategy !== matrixStrategy ||
      !Array.isArray(document.rows) || document.rows.length < 2) {
    fail(`matrix input must use ${matrixStrategy} with at least two rows`)
  }
  const seen = new Set()
  const rows = document.rows.map((row) => {
    if (row === null || typeof row !== 'object' ||
        typeof row.id !== 'string' || !safeId.test(row.id) || seen.has(row.id)) {
      fail('matrix input contains a duplicate or unsafe row id')
    }
    seen.add(row.id)
    if (typeof row.version !== 'string' || !versionPattern.test(row.version) ||
        !['clr2', 'clr4'].includes(row.clrGeneration) ||
        row.targetPrefix !== row.clrGeneration ||
        !isSafeDigestReference(row.operatorImage)) {
      fail(`matrix input row ${row.id} has an invalid version, generation, prefix, or operator image`)
    }
    const companions = row.companionVersions
    if (companions === null || typeof companions !== 'object' ||
        versionPattern.test(String(companions.clr2)) === false ||
        versionPattern.test(String(companions.clr4)) === false ||
        companions[row.clrGeneration] !== row.version) {
      fail(`matrix input row ${row.id} has invalid companion versions`)
    }
    return {
      id: row.id,
      version: row.version,
      clrGeneration: row.clrGeneration,
      targetPrefix: row.targetPrefix,
      companionVersions: { clr2: companions.clr2, clr4: companions.clr4 },
      operatorImage: row.operatorImage,
    }
  }).sort((left, right) => left.id < right.id ? -1 : left.id > right.id ? 1 : 0)
  return { schemaVersion: 1, strategy: matrixStrategy, rows }
}

export function readMatrixInput(filename) { return normalizeMatrixInput(readRegularJson(filename, 'matrix input')); }

export function matrixInputDigest(document) { return `sha256:${crypto.createHash('sha256').update(canonicalJson(document)).digest('hex')}`; }

function expectedOperatorLabels(row) {
  return {
    'io.sharplabnext.operator-only': 'true',
    'io.sharplabnext.framework.target-id': row.id,
    'io.sharplabnext.framework.version': row.version,
    'io.sharplabnext.framework.clr-generation': row.clrGeneration,
    'io.sharplabnext.wine-prefix-layout': 'hardlink-immutable-v1',
    'io.sharplabnext.wine-prefix-layout-manifest': '/opt/sharplabnext/.wine-prefix-layout.json',
  }
}

export function validateOperatorImageInspection(row, imageInfo, expectedImages = undefined) {
  const failures = []
  if (imageInfo?.Os !== 'linux' || imageInfo?.Architecture !== 'amd64') {
    failures.push(`${row.id} operator image must be linux/amd64`)
  }
  if (!Number.isSafeInteger(imageInfo?.Size) || imageInfo.Size <= 0) {
    failures.push(`${row.id} operator image has no positive inspected size`)
  }
  const expectedDigest = row.operatorImage.slice(row.operatorImage.lastIndexOf('@') + 1)
  const repoDigests = Array.isArray(imageInfo?.RepoDigests) ? imageInfo.RepoDigests : []
  if (imageInfo?.Id !== expectedDigest && !repoDigests.includes(row.operatorImage)) {
    failures.push(`${row.id} operator image does not resolve to its supplied digest`)
  }
  const labels = imageInfo?.Config?.Labels ?? {}
  for (const [label, expected] of Object.entries(expectedOperatorLabels(row))) {
    if (labels[label] !== expected) failures.push(`${row.id} operator label ${label} must equal '${expected}'`)
  }
  const provenance = [
    ['io.sharplabnext.operator-base', 'baseImage', 'Wine/base'],
    ['io.sharplabnext.operator-root', 'rootImage', 'root'],
  ]
  for (const [label, expectedName, description] of provenance) {
    const actual = normalizeDigestPinnedImageIdentity(labels[label])
    if (actual === undefined) {
      failures.push(`${row.id} operator label ${label} must be a digest-pinned image reference`)
      continue
    }
    if (expectedImages?.[expectedName] !== undefined) {
      const expected = normalizeDigestPinnedImageIdentity(expectedImages[expectedName])
      if (expected === undefined) {
        failures.push(`expected ${description} image must be a digest-pinned image reference`)
      } else if (actual !== expected) {
        failures.push(
          `${row.id} operator ${description} identity '${actual}' must equal '${expected}'`,
        )
      }
    }
  }
  if (expectedImages?.installerManifestSha256 !== undefined &&
      labels['io.sharplabnext.framework.installer-manifest-sha256'] !==
        expectedImages.installerManifestSha256) {
    failures.push(
      `${row.id} operator installer manifest identity must equal ` +
      `'${expectedImages.installerManifestSha256}'`,
    )
  }
  if (expectedImages?.sourceRevision !== undefined &&
      labels['org.opencontainers.image.revision'] !== expectedImages.sourceRevision) {
    failures.push(
      `${row.id} operator source revision must equal '${expectedImages.sourceRevision}'`,
    )
  }
  if (expectedImages?.sourceRevision !== undefined &&
      labels['io.sharplabnext.source.revision'] !== expectedImages.sourceRevision) {
    failures.push(
      `${row.id} operator label io.sharplabnext.source.revision must equal ` +
      `'${expectedImages.sourceRevision}'`,
    )
  }
  return failures
}

function installerManifestSha256() { return crypto.createHash('sha256').update(fs.readFileSync(installerManifestPath)).digest('hex'); }

function inspectImage(reference, spawn = spawnSync) {
  const result = spawn('docker', ['image', 'inspect', reference], {
    cwd: repositoryRoot, encoding: 'utf8', shell: false,
  })
  if (result.error !== undefined || result.status !== 0) return undefined
  try {
    const parsed = JSON.parse(result.stdout)
    return Array.isArray(parsed) && parsed.length === 1 ? parsed[0] : undefined
  } catch { return undefined }
}

export function inspectOrPullOperator(row, spawn = spawnSync, expectedImages = undefined) {
  // Operators are produced by the current build and are already in the local
  // Docker store.  A best-effort cache probe must never turn into an implicit
  // registry login/pull (which is unreliable over SSH and defeats BuildKit's
  // own layer cache).
  const info = inspectImage(row.operatorImage, spawn)
  if (info === undefined) fail(`Docker operator image '${row.operatorImage}' is unavailable`)
  const failures = validateOperatorImageInspection(row, info, expectedImages)
  if (failures.length > 0) fail(failures.join('; '))
  return { id: info.Id, sizeBytes: info.Size }
}

function dockerfileLabelValue(value) {
  // All values passed here have already been constrained to safe IDs, versions,
  // digests, or Git identities. Keep this guard to prevent future callers from
  // accidentally introducing a Dockerfile instruction boundary.
  if (typeof value !== 'string' || /[\r\n"\\]/.test(value)) fail('unsafe Dockerfile label value')
  return value
}

export function createContextDockerfile(document, inputDigest, sourceRevision, version) {
  // This image is an identity document, not a transport for Wine prefixes.
  // The parent builder mounts each digest-pinned operator image directly.
  const lines = [pinnedDockerfileFrontendDirective, '', 'FROM scratch AS final']
  lines.push('COPY matrix-input.json /matrix-input.json')
  document.rows.forEach((row) => lines.push(`COPY rows/${row.id}/row.json /rows/${row.id}/row.json`));
  const labels = {
    'io.sharplabnext.framework.matrix-context': 'true',
    'io.sharplabnext.framework.matrix-content': metadataContentKind,
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
    'io.sharplabnext.framework.matrix-input-sha256': inputDigest,
    'io.sharplabnext.framework.matrix-row-count': String(document.rows.length),
    'org.opencontainers.image.revision': sourceRevision,
    'io.sharplabnext.source.revision': sourceRevision,
    'org.opencontainers.image.version': version,
  }
  lines.push('LABEL ' + Object.entries(labels)
    .map(([key, value]) => `${key}="${dockerfileLabelValue(value)}"`).join(' '))
  lines.push('')
  return `${lines.join('\n')}\n`
}

export function validateContextInputs(values, document) {
  const failures = []
  if (JSON.stringify(document?.rows?.map(row => row.id)) !== JSON.stringify(requiredFrameworkRows)) {
    failures.push(`matrix input must contain the exact ${requiredFrameworkRows.length}-row Framework set`)
  }
  if (!isSha256Digest(values?.MATRIX_INPUT_SHA256)) failures.push('MATRIX_INPUT_SHA256 must be sha256:<64 lowercase hex>')
  if (values?.MATRIX_INPUT_SHA256 !== matrixInputDigest(document)) failures.push('MATRIX_INPUT_SHA256 does not match normalized matrix input')
  if (typeof values?.SOURCE_REVISION !== 'string' ||
      (!isGitCommitIdentity(values.SOURCE_REVISION) && values.SOURCE_REVISION !== 'development')) {
    failures.push('SOURCE_REVISION must be a Git commit identity or development')
  }
  if (typeof values?.IMAGE !== 'string' || !imageTag.test(values.IMAGE)) failures.push('IMAGE must be a safe image tag')
  if (values?.push === true && !hasRegistryHost(`${values.IMAGE}@${'0'.repeat(64)}`)) failures.push('IMAGE must include an explicit registry host when --push is used')
  if (values?.push === true && values.SOURCE_REVISION === 'development') failures.push('--push requires a committed SOURCE_REVISION')
  if (values?.push === true && document.rows.some(row => !hasRegistryHost(row.operatorImage))) failures.push('--push requires registry-hosted operator image references')
  if (typeof values?.VERSION !== 'string' || values.VERSION.length === 0 || /[\r\n"\\]/.test(values.VERSION)) failures.push('VERSION must be a non-empty safe value')
  return failures
}

export function createContextBuildArguments(values, buildRoot, dockerfilePath, metadataFile) {
  const args = [
    'buildx', 'build', '--platform', 'linux/amd64', '--file', dockerfilePath,
    '--tag', values.IMAGE,
  ]
  if (metadataFile !== undefined) args.push('--metadata-file', metadataFile)
  args.push(values.push === true ? '--push' : '--load', '--provenance=false', buildRoot)
  return args
}

function inspectGitSource(spawn = spawnSync, fallbackRevision = undefined, environment = process.env) {
  if (String(environment?.[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() === contentSourceIdentityMode &&
      isGitCommitIdentity(fallbackRevision)) {
    return { headRevision: fallbackRevision, isDirty: true }
  }
  try {
    const revision = spawn('git', ['rev-parse', '--verify', 'HEAD'], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
    if (revision.error !== undefined || revision.status !== 0) fail('could not resolve Git HEAD')
    const headRevision = String(revision.stdout ?? '').trim()
    if (!isGitCommitIdentity(headRevision)) fail('Git HEAD is not a full commit identity')
    const status = spawn('git', ['status', '--porcelain=v1', '--untracked-files=normal'], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
    if (status.error !== undefined || status.status !== 0) fail('could not inspect Git source state')
    return { headRevision, isDirty: String(status.stdout ?? '').length > 0 }
  } catch (error) {
    if (isGitCommitIdentity(fallbackRevision)) {
      return { headRevision: fallbackRevision, isDirty: true }
    }
    throw error
  }
}

function copyMetadata(spawn, containerId, source, destination) {
  const result = spawn('docker', ['cp', `${containerId}:${source}`, destination], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
  if (result.error !== undefined || result.status !== 0) fail(`could not inspect built matrix metadata '${source}'`)
}

function verifyBuiltMetadata(reference, document, expectedDigest, spawn = spawnSync) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-context-check-'))
  let containerId
  try {
    const created = spawn('docker', ['create', '--platform', 'linux/amd64', '--entrypoint', '/bin/false', reference], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
    if (created.error !== undefined || created.status !== 0) fail(`could not create built matrix context '${reference}'`)
    containerId = String(created.stdout ?? '').trim()
    if (!/^[0-9a-f]{12,64}$/.test(containerId)) fail('Docker returned an invalid context container identity')
    const matrixPath = path.join(root, 'matrix-input.json')
    copyMetadata(spawn, containerId, '/matrix-input.json', matrixPath)
    const observed = readMatrixInput(matrixPath)
    if (matrixInputDigest(observed) !== expectedDigest) fail('built matrix context metadata digest does not match')
    for (const row of document.rows) {
      const rowRoot = path.join(root, 'rows', row.id)
      fs.mkdirSync(rowRoot, { recursive: true })
      const rowPath = path.join(rowRoot, 'row.json')
      copyMetadata(spawn, containerId, `/rows/${row.id}/row.json`, rowPath)
      const actual = JSON.parse(fs.readFileSync(rowPath, 'utf8'))
      if (JSON.stringify(actual) !== JSON.stringify(rowMetadata(row))) fail(`built matrix context row '${row.id}' metadata drifted`)
    }
  } finally {
    if (containerId !== undefined) spawn('docker', ['rm', containerId], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
    fs.rmSync(root, { recursive: true, force: true })
  }
}

function readBuildDigest(filename) {
  let stat
  try { stat = fs.lstatSync(filename) } catch { fail('BuildKit metadata file is missing') }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maximumMetadataBytes) {
    fail('BuildKit metadata must be a bounded regular file')
  }
  let value
  try { value = JSON.parse(fs.readFileSync(filename, 'utf8')) } catch (error) { fail(`BuildKit metadata is invalid JSON: ${error.message}`) }
  if (!imageDigest.test(value?.['containerimage.digest'] ?? '')) fail('BuildKit metadata has no valid image digest')
  return value['containerimage.digest']
}

function parseArguments(arguments_) {
  const values = { VERSION: 'development', push: false, allowDirty: false }
  for (let index = 0; index < arguments_.length; index++) {
    const argument = arguments_[index]
    if (argument === '--push') { values.push = true; continue }
    if (argument === '--help') return { help: true }
    if (!argument.startsWith('--') || index + 1 >= arguments_.length) fail(`unknown or incomplete argument '${argument}'`)
    const name = argument.slice(2).replaceAll('-', '_').toUpperCase()
    const allowed = new Set(['MATRIX_INPUT', 'SOURCE_REVISION', 'IMAGE', 'VERSION'])
    if (!allowed.has(name)) fail(`unknown argument '${argument}'`)
    values[name] = arguments_[++index]
  }
  return values
}

function usage() {
  return `Usage: node eng/build-framework-matrix-context.mjs \\
  --matrix-input <metadata-json> \\
  --source-revision <40/64-hex|development> \\
  --image <repository:tag> [--version <id>] [--push]\n\n` +
    'The output is a bounded metadata-only image. Prefixes remain in their digest-pinned operator images.'
}

export function runContextBuild(argv, environment = process.env, spawn = spawnSync, output = console) {
  let values
  try { values = { ...environment, ...parseArguments(argv) } } catch (error) { output.error(`framework context input error: ${error.message}`); return 64 }
  if (values.help) { output.log(usage()); return 0 }
  if (String(values[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() ===
      contentSourceIdentityMode) {
    values.allowDirty = true
  }
  let document
  try { document = readMatrixInput(values.MATRIX_INPUT) } catch (error) { output.error(`framework context input error: ${error.message}`); return 1 }
  values.MATRIX_INPUT_SHA256 = matrixInputDigest(document)
  const inputFailures = validateContextInputs(values, document)
  if (inputFailures.length > 0) { inputFailures.forEach(failure => output.error(`framework context input error: ${failure}`)); return 1 }
  let before
  try { before = inspectGitSource(spawn, values.SOURCE_REVISION, values) } catch (error) { output.error(`framework context source error: ${error.message}`); return 1 }
  if (values.SOURCE_REVISION !== 'development' && values.SOURCE_REVISION !== before.headRevision) { output.error('framework context source error: SOURCE_REVISION does not match Git HEAD'); return 1 }
  if (before.isDirty && !values.allowDirty) { output.error('framework context source error: worktree is dirty'); return 1 }
  if (values.push && before.isDirty) { output.error('framework context source error: --push requires a clean worktree'); return 1 }
  const operatorExpectations = {
    installerManifestSha256: installerManifestSha256(),
    ...(values.SOURCE_REVISION === 'development'
      ? {}
      : { sourceRevision: values.SOURCE_REVISION }),
  }
  try {
    document.rows.forEach(row => inspectOrPullOperator(row, spawn, operatorExpectations))
  } catch (error) {
    output.error(`framework context operator error: ${error.message}`)
    return 1
  }

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-context-'))
  const metadataRoot = values.push ? fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-context-meta-')) : undefined
  try {
    fs.mkdirSync(path.join(root, 'rows'), { recursive: true })
    const inputPath = path.join(root, 'matrix-input.json')
    fs.writeFileSync(inputPath, canonicalJson(document), { encoding: 'utf8', flag: 'wx' })
    document.rows.forEach(row => {
      const rowRoot = path.join(root, 'rows', row.id)
      fs.mkdirSync(rowRoot, { recursive: true })
      fs.writeFileSync(path.join(rowRoot, 'row.json'), canonicalJson(rowMetadata(row)), { encoding: 'utf8', flag: 'wx' })
    })
    const dockerfilePath = path.join(root, 'Dockerfile')
    fs.writeFileSync(dockerfilePath, createContextDockerfile(document, values.MATRIX_INPUT_SHA256, values.SOURCE_REVISION, values.VERSION), { encoding: 'utf8', flag: 'wx' })
    const metadataFile = metadataRoot === undefined ? undefined : path.join(metadataRoot, 'build-metadata.json')
    const build = spawn('docker', createContextBuildArguments(values, root, dockerfilePath, metadataFile), { cwd: repositoryRoot, encoding: 'utf8', shell: false, stdio: 'inherit' })
    if (build.error !== undefined || build.status !== 0) { output.error(`framework context Docker build failed with exit code ${build.status ?? 1}`); return 1 }
    const after = inspectGitSource(spawn, values.SOURCE_REVISION, values)
    if (after.headRevision !== before.headRevision || after.isDirty !== before.isDirty || (values.push && after.isDirty)) { output.error('framework context source error: Git source changed during the build'); return 1 }
    let reference = values.IMAGE
    let pushedDigest
    if (values.push) {
      pushedDigest = readBuildDigest(metadataFile)
      reference = `${imageRepository(values.IMAGE)}@${pushedDigest}`
      const pull = spawn('docker', ['pull', reference], { cwd: repositoryRoot, encoding: 'utf8', shell: false, stdio: 'inherit' })
      if (pull.error !== undefined || pull.status !== 0) fail(`could not pull pushed context '${reference}'`)
    }
    const info = inspectImage(reference, spawn)
    if (info === undefined) fail(`Docker context image '${reference}' is unavailable after build`)
    if (values.push && (!Array.isArray(info.RepoDigests) || !info.RepoDigests.includes(reference))) {
      fail(`pushed context does not expose RepoDigest '${reference}'`)
    }
    const labels = info.Config?.Labels ?? {}
    for (const [label, expected] of Object.entries({
      'io.sharplabnext.framework.matrix-context': 'true',
      'io.sharplabnext.framework.matrix-content': metadataContentKind,
      'io.sharplabnext.framework.matrix-strategy': matrixStrategy,
      'io.sharplabnext.framework.matrix-input-sha256': values.MATRIX_INPUT_SHA256,
      'io.sharplabnext.framework.matrix-row-count': String(document.rows.length),
      'org.opencontainers.image.revision': values.SOURCE_REVISION,
      'io.sharplabnext.source.revision': values.SOURCE_REVISION,
      'org.opencontainers.image.version': values.VERSION,
    })) if (labels[label] !== expected) fail(`built context label ${label} does not match supplied input`)
    if (info.Os !== 'linux' || info.Architecture !== 'amd64' ||
        !Number.isSafeInteger(info.Size) || info.Size <= 0 ||
        info.Size > maximumMetadataImageBytes) {
      fail(`built context must be a bounded linux/amd64 metadata image no larger than ${maximumMetadataImageBytes} bytes`)
    }
    verifyBuiltMetadata(reference, document, values.MATRIX_INPUT_SHA256, spawn)
    output.log(JSON.stringify({ image: values.IMAGE, imageId: info.Id, sizeBytes: info.Size, matrixInputSha256: values.MATRIX_INPUT_SHA256, rowCount: document.rows.length, rowIds: document.rows.map(row => row.id), promotionEligible: values.SOURCE_REVISION !== 'development' && !before.isDirty, ...(pushedDigest === undefined ? {} : { registryReference: reference }) }, null, 2))
    return 0
  } catch (error) {
    output.error(`framework context identity error: ${error.message}`)
    return 1
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
    if (metadataRoot !== undefined) fs.rmSync(metadataRoot, { recursive: true, force: true })
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = runContextBuild(process.argv.slice(2))
