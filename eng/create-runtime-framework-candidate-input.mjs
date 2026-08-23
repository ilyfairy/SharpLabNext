/**
 * Materialize the narrowly-scoped Framework candidate input consumed by
 * runtime-candidate-environment.mjs. The input is derived from immutable
 * images and the parent's assembled matrix manifest; it is never hand-written.
 */

import { spawnSync } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { isDigestPinnedImageReference, isGitCommitIdentity } from './runtime-candidate-input-validation.mjs'
import { matrixInputDigest, normalizeMatrixInput } from './build-framework-matrix-context.mjs'
import { canonicalFrameworkCandidateInput, frameworkCandidateInputStrategy, readRuntimeMatrix } from './runtime-candidate-environment.mjs'
import { createCommittedSourceContext } from './committed-source-context.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const maximumJsonBytes = 1024 * 1024
const installerManifestPath = path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json')
const requiredFrameworkRows = Object.freeze([
  'netfx20', 'netfx30', 'netfx35', 'netfx40', 'netfx45', 'netfx451',
  'netfx452', 'netfx46', 'netfx461', 'netfx462', 'netfx47', 'netfx471',
  'netfx472', 'netfx48',
])

function fail(message) { throw new Error(message) }
function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }

function readRegularBytes(filename, label) {
  if (typeof filename !== 'string' || filename.length === 0) fail(`${label} path is required`)
  const absolute = path.resolve(filename)
  let before
  try { before = fs.lstatSync(absolute) } catch { fail(`${label} does not exist`) }
  if (!before.isFile() || before.isSymbolicLink() || before.size < 1 || before.size > maximumJsonBytes) {
    fail(`${label} must be a 1..${maximumJsonBytes} byte regular non-link file`)
  }
  const descriptor = fs.openSync(absolute, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0))
  try {
    const opened = fs.fstatSync(descriptor)
    if (!opened.isFile() || opened.size !== before.size ||
        (before.dev !== undefined && opened.dev !== before.dev) ||
        (before.ino !== undefined && opened.ino !== before.ino)) {
      fail(`${label} changed while it was opened`)
    }
    const bytes = fs.readFileSync(descriptor)
    const after = fs.fstatSync(descriptor)
    if (bytes.length !== opened.size || after.size !== opened.size ||
        after.mtimeMs !== opened.mtimeMs) {
      fail(`${label} changed while it was read`)
    }
    return bytes
  } finally {
    fs.closeSync(descriptor)
  }
}

function readRegularJson(filename, label) {
  try { return JSON.parse(readRegularBytes(filename, label).toString('utf8')) } catch (error) {
    fail(`${label} is invalid JSON: ${error.message}`)
  }
}

function requireDigestReference(value, label) {
  if (!isDigestPinnedImageReference(value)) fail(`${label} must be a repository@sha256:<64 lowercase hex> reference`)
  return value
}

function imageDigest(reference) { return reference.slice(reference.lastIndexOf('@') + 1) }

function exactKeys(value, keys, label) {
  if (!isObject(value)) fail(`${label} must be an object`)
  const actual = Object.keys(value).sort()
  const expected = [...keys].sort()
  if (JSON.stringify(actual) !== JSON.stringify(expected)) fail(`${label} must contain exactly: ${expected.join(', ')}`)
}

function inspectImage(reference, spawn) {
  const result = spawn('docker', ['image', 'inspect', reference], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
  if (result.error !== undefined || result.status !== 0) fail(`Docker image '${reference}' is unavailable`)
  let images
  try { images = JSON.parse(result.stdout) } catch { fail(`Docker image '${reference}' inspection is invalid JSON`) }
  if (!Array.isArray(images) || images.length !== 1 || !isObject(images[0])) fail(`Docker image '${reference}' inspection must return exactly one image`)
  const image = images[0]
  const repoDigests = Array.isArray(image.RepoDigests) ? image.RepoDigests : []
  if (image.Id !== imageDigest(reference) && !repoDigests.includes(reference)) fail(`Docker image '${reference}' does not resolve to its immutable digest`)
  if (image.Os !== 'linux' || image.Architecture !== 'amd64') fail(`Docker image '${reference}' must be linux/amd64`)
  if (!Number.isSafeInteger(image.Size) || image.Size <= 0) fail(`Docker image '${reference}' must have a positive inspected size`)
  return image
}

function validateLabels(image, expected, label) {
  const labels = image.Config?.Labels
  if (!isObject(labels)) fail(`${label} has no labels`)
  for (const [name, value] of Object.entries(expected)) {
    if (labels[name] !== value) fail(`${label} label ${name} must equal '${value}'`)
  }
}

function readImageJson(image, sourcePath, label, spawn) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-candidate-'))
  let containerId
  try {
    const created = spawn('docker', ['create', '--platform', 'linux/amd64', '--entrypoint', '/bin/false', image], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
    if (created.error !== undefined || created.status !== 0) fail(`could not create stopped ${label} container '${image}'`)
    containerId = String(created.stdout ?? '').trim()
    if (!/^[0-9a-f]{12,64}$/.test(containerId)) fail(`Docker returned an invalid ${label} container identity`)
    const destination = path.join(root, path.basename(sourcePath))
    const copied = spawn('docker', ['cp', `${containerId}:${sourcePath}`, destination], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
    if (copied.error !== undefined || copied.status !== 0) fail(`could not read ${sourcePath} from the immutable ${label} image`)
    return readRegularJson(destination, `${label} ${path.basename(sourcePath)}`)
  } finally {
    if (containerId !== undefined) spawn('docker', ['rm', containerId], { cwd: repositoryRoot, encoding: 'utf8', shell: false })
    fs.rmSync(root, { recursive: true, force: true })
  }
}

function readParentManifest(parentImage, spawn) {
  return readImageJson(
    parentImage,
    '/opt/sharplabnext/framework-matrix.json',
    'parent',
    spawn,
  )
}

function readMetadataMatrixInput(metadataImage, spawn) {
  return readImageJson(metadataImage, '/matrix-input.json', 'metadata', spawn)
}

function normalizeCanonicalMatrixInput(rawMatrixInput) {
  if (!Array.isArray(rawMatrixInput?.rows) ||
      JSON.stringify(rawMatrixInput.rows.map(row => row?.id)) !== JSON.stringify(requiredFrameworkRows)) {
    fail(`matrix input must contain the exact ordered ${requiredFrameworkRows.length}-row Framework set`)
  }
  const matrixInput = normalizeMatrixInput(rawMatrixInput)
  if (JSON.stringify(matrixInput.rows.map(row => row.id)) !== JSON.stringify(requiredFrameworkRows)) {
    fail(`matrix input must contain the exact ordered ${requiredFrameworkRows.length}-row Framework set`)
  }
  return matrixInput
}

function normalizeParentRows(document, matrixInput, runtimeMatrix) {
  if (!isObject(document) || document.schemaVersion !== 1 || document.strategy !== 'shared-framework-target-prefix-matrix-v1' || !Array.isArray(document.rows)) {
    fail('parent framework-matrix.json has an unsupported schema or strategy')
  }
  if (document.inputManifestSha256 !== matrixInputDigest(matrixInput)) fail('parent framework-matrix.json does not bind the supplied matrix input')
  if (document.rows.length !== requiredFrameworkRows.length) fail(`parent framework-matrix.json must contain exactly ${requiredFrameworkRows.length} rows`)
  const targets = runtimeMatrix?.framework?.targets
  if (!Array.isArray(targets) || JSON.stringify(targets.map(row => row?.id)) !== JSON.stringify(requiredFrameworkRows)) {
    fail('runtime matrix must contain the canonical ordered Framework target set')
  }
  const inputRows = new Map(matrixInput.rows.map(row => [row.id, row]))
  return document.rows.map((row, index) => {
    exactKeys(row, ['schemaVersion', 'id', 'version', 'clrGeneration', 'targetPrefix', 'companionVersions', 'operatorImage', 'prefixes', 'rowDigest'], `parent Framework row ${index}`)
    const target = targets[index]
    const source = matrixInput.rows[index]
    if (row.id !== requiredFrameworkRows[index] || row.id !== target.id || row.id !== source.id) fail(`parent Framework row ${index} does not use the canonical order/profile identity`)
    if (row.version !== target.version || row.clrGeneration !== target.clrGeneration || row.targetPrefix !== row.clrGeneration || row.operatorImage !== inputRows.get(row.id)?.operatorImage) {
      fail(`parent Framework row '${row.id}' does not match the matrix input/runtime identity`)
    }
    requireDigestReference(row.operatorImage, `parent Framework row '${row.id}' operatorImage`)
    if (typeof row.rowDigest !== 'string' || !/^[0-9a-f]{64}$/.test(row.rowDigest)) fail(`parent Framework row '${row.id}' rowDigest must be 64 lowercase hexadecimal characters`)
    return Object.freeze({ id: row.id, operatorImage: row.operatorImage, rowDigest: `sha256:${row.rowDigest}`, version: row.version, clrGeneration: row.clrGeneration })
  })
}

function validateOperatorImages(rows, sourceRevision, parentInputs, installerManifestSha256, spawn) {
  for (const row of rows) {
    const image = inspectImage(row.operatorImage, spawn)
    validateLabels(image, {
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.framework.target-id': row.id,
      'io.sharplabnext.framework.version': row.version,
      'io.sharplabnext.framework.clr-generation': row.clrGeneration,
      'io.sharplabnext.wine-prefix-layout': 'hardlink-immutable-v1',
      'io.sharplabnext.wine-prefix-layout-manifest': '/opt/sharplabnext/.wine-prefix-layout.json',
      'io.sharplabnext.framework.installer-manifest-sha256': installerManifestSha256,
      'io.sharplabnext.operator-base': parentInputs.wineImage,
      'io.sharplabnext.operator-root': parentInputs.rootImage,
      'org.opencontainers.image.revision': sourceRevision,
      'io.sharplabnext.source.revision': sourceRevision,
    }, `Framework operator '${row.id}'`)
  }
}

function validateControlImages(parentImage, metadataImage, matrixInput, sourceRevision, spawn) {
  const digest = matrixInputDigest(matrixInput)
  const metadata = inspectImage(metadataImage, spawn)
  validateLabels(metadata, {
    'io.sharplabnext.framework.matrix-context': 'true',
    'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
    'io.sharplabnext.framework.matrix-input-sha256': digest,
    'io.sharplabnext.framework.matrix-row-count': String(requiredFrameworkRows.length),
    'org.opencontainers.image.revision': sourceRevision,
    'io.sharplabnext.source.revision': sourceRevision,
  }, 'Framework metadata image')
  const parent = inspectImage(parentImage, spawn)
  validateLabels(parent, {
    'io.sharplabnext.operator-only': 'true',
    'io.sharplabnext.framework.matrix': 'true',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-target-prefix-matrix-v1',
    'io.sharplabnext.framework.dedupe-policy': 'wine-static-runtime-payload-v1',
    'io.sharplabnext.framework.matrix-input-sha256': digest,
    'io.sharplabnext.framework.matrix-source-uri': `docker://${metadataImage}`,
    'org.opencontainers.image.revision': sourceRevision,
    'io.sharplabnext.source.revision': sourceRevision,
  }, 'Framework parent image')
  const parentLabels = parent.Config?.Labels
  return Object.freeze({
    wineImage: requireDigestReference(
      parentLabels?.['io.sharplabnext.operator-image.wine'],
      'Framework parent Wine operator label',
    ),
    rootImage: requireDigestReference(
      parentLabels?.['io.sharplabnext.operator-root'],
      'Framework parent root image label',
    ),
  })
}

export function createFrameworkCandidateInputFromImages(options) {
  const parentImage = requireDigestReference(options?.parentImage, 'parentImage')
  const metadataImage = requireDigestReference(options?.metadataImage, 'metadataImage')
  if (!isGitCommitIdentity(options?.sourceRevision)) fail('sourceRevision must be a full lowercase Git commit identity')
  const runtimeMatrix = readRuntimeMatrix(options?.runtimeMatrix)
  const spawn = options.spawn ?? spawnSync
  const matrixInput = normalizeCanonicalMatrixInput(
    readMetadataMatrixInput(metadataImage, spawn),
  )
  const parentInputs = validateControlImages(
    parentImage,
    metadataImage,
    matrixInput,
    options.sourceRevision,
    spawn,
  )
  const rows = normalizeParentRows(readParentManifest(parentImage, spawn), matrixInput, runtimeMatrix)
  const manifestPath = options?.installerManifest ?? installerManifestPath
  const installerManifestSha256 = crypto.createHash('sha256')
    .update(readRegularBytes(manifestPath, 'Framework installer manifest'))
    .digest('hex')
  validateOperatorImages(
    rows,
    options.sourceRevision,
    parentInputs,
    installerManifestSha256,
    spawn,
  )
  const value = {
    schemaVersion: 1,
    strategy: frameworkCandidateInputStrategy,
    parentImage,
    metadataImage,
    matrixInputSha256: matrixInputDigest(matrixInput),
    sourceRevision: options.sourceRevision,
    rows: rows.map(({ id, operatorImage, rowDigest }) => ({ id, operatorImage, rowDigest })),
  }
  const bytes = Buffer.from(canonicalFrameworkCandidateInput(value, runtimeMatrix))
  return Object.freeze({
    value: Object.freeze(value),
    operatorInputs: parentInputs,
    bytes,
    sha256: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`,
  })
}

export function createFrameworkCandidateInput(options) {
  const suppliedMatrixInput = normalizeCanonicalMatrixInput(
    readRegularJson(options?.matrixInput, 'matrix input'),
  )
  const result = createFrameworkCandidateInputFromImages(options)
  const suppliedDigest = matrixInputDigest(suppliedMatrixInput)
  if (suppliedDigest !== result.value.matrixInputSha256) {
    fail(
      `supplied matrix input digest '${suppliedDigest}' does not match ` +
      `immutable metadata '${result.value.matrixInputSha256}'`,
    )
  }
  return result
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  const values = {}
  const fields = { '--parent-image': 'parentImage', '--metadata-image': 'metadataImage', '--matrix-input': 'matrixInput', '--runtime-matrix': 'runtimeMatrix', '--source-revision': 'sourceRevision', '--output': 'output' }
  for (let index = 0; index < argv.length; index++) {
    const field = fields[argv[index]]
    if (field === undefined || values[field] !== undefined) fail(`unknown or duplicate argument '${argv[index]}'`)
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail(`${argv[index - 1]} requires a value`)
    values[field] = value
  }
  for (const [argument, field] of Object.entries(fields)) {
    if (field !== 'runtimeMatrix' && values[field] === undefined) fail(`${argument} is required`)
  }
  return values
}

function usage() {
  return `Usage: node eng/create-runtime-framework-candidate-input.mjs \\
  --parent-image <repository@sha256:...> --metadata-image <repository@sha256:...> \\
  --matrix-input <matrix-input.json> --source-revision <40/64-hex> \\
  --output <candidate-input.json> [--runtime-matrix <runtime-matrix.json>]`
}

function writeAtomically(filename, bytes) {
  const output = path.resolve(filename)
  if (fs.existsSync(output)) fail(`output '${output}' already exists; refusing to overwrite it`)
  const directory = path.dirname(output)
  const directoryInfo = fs.lstatSync(directory)
  if (!directoryInfo.isDirectory() || directoryInfo.isSymbolicLink()) {
    fail(`output directory '${directory}' must be a regular non-link directory`)
  }
  const temporary = path.join(path.dirname(output), `.${path.basename(output)}.${process.pid}.${crypto.randomUUID()}.tmp`)
  try {
    fs.writeFileSync(temporary, bytes, { flag: 'wx' })
    fs.linkSync(temporary, output)
  } finally {
    fs.rmSync(temporary, { force: true })
  }
}

export function runCreateFrameworkCandidateInput(argv, options = {}) {
  const output = options.output ?? console
  let sourceContext
  let exitCode = 1
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) { output.log(usage()); return 0 }
    const createContext = options.createCommittedSourceContext ??
      createCommittedSourceContext
    sourceContext = createContext({
      repositoryRoot,
      revision: parsed.sourceRevision,
      requiredFiles: [
        'profiles/runtime-matrix.json',
        'profiles/runtime-framework-installers.json',
      ],
      spawn: options.spawn ?? spawnSync,
    })
    const committedRuntimeMatrix = path.join(
      sourceContext.directory,
      'profiles',
      'runtime-matrix.json',
    )
    if (parsed.runtimeMatrix !== undefined &&
        !readRegularBytes(parsed.runtimeMatrix, 'runtime matrix').equals(
          readRegularBytes(committedRuntimeMatrix, 'committed runtime matrix'),
        )) {
      fail('supplied runtime matrix does not match the committed source revision')
    }
    const result = createFrameworkCandidateInput({
      ...parsed,
      runtimeMatrix: committedRuntimeMatrix,
      installerManifest: path.join(
        sourceContext.directory,
        'profiles',
        'runtime-framework-installers.json',
      ),
      spawn: options.spawn ?? spawnSync,
    })
    writeAtomically(parsed.output, result.bytes)
    output.log(JSON.stringify({ output: path.resolve(parsed.output), sha256: result.sha256, rowCount: requiredFrameworkRows.length }))
    exitCode = 0
  } catch (error) {
    output.error(`Framework candidate input error: ${error.message}`)
  } finally {
    try {
      sourceContext?.dispose()
    } catch (error) {
      output.error(`Framework candidate input error: ${error.message}`)
      exitCode = 1
    }
  }
  return exitCode
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = runCreateFrameworkCandidateInput(process.argv.slice(2))
