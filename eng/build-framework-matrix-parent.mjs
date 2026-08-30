/**
 * Build the shared Wine/.NET Framework prefix parent.
 *
 * This is deliberately separate from build-runtime-candidate.mjs: the parent
 * is an operator-only filesystem image, not a selectable runtime profile. The
 * command validates the private matrix metadata and immutable image inputs
 * before BuildKit is allowed to resolve either Dockerfile FROM instruction.
 */

import { spawnSync } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import {
  isCandidateSourceUri,
  isDigestPinnedImageReference,
  isGitCommitIdentity,
  isSha256Digest,
} from './runtime-candidate-input-validation.mjs'
import {
  inspectOrPullOperator,
} from './build-framework-matrix-context.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const matrixInputName = 'matrix-input.json'
const matrixStrategy = 'shared-framework-prefix-input-v1'
const sourceIdentityModeEnvironmentVariable = 'SHARPLABNEXT_SOURCE_IDENTITY_MODE'
const contentSourceIdentityMode = 'content'
const maximumMatrixMetadataBytes = 1024 * 1024
const maximumMetadataImageBytes = 16 * 1024 * 1024
const safeId = /^[a-z0-9][a-z0-9._-]{0,127}$/
const imageTag = /^(?:[A-Za-z0-9][A-Za-z0-9._-]*(?::[0-9]+)?\/)?[A-Za-z0-9][A-Za-z0-9._/-]*(?::[A-Za-z0-9][A-Za-z0-9._-]*)?$/
const imageDigest = /^sha256:[0-9a-f]{64}$/
const rowMountMarker = '    # SHARPLABNEXT_FRAMEWORK_ROW_MOUNTS'
const requiredFrameworkRows = Object.freeze([
  'netfx20', 'netfx30', 'netfx35', 'netfx40', 'netfx45', 'netfx451',
  'netfx452', 'netfx46', 'netfx461', 'netfx462', 'netfx47', 'netfx471',
  'netfx472', 'netfx48',
])
const parentSourceFiles = Object.freeze([
  'deploy/docker/Dockerfile.operator-wine-framework-matrix-parent',
  'deploy/docker/assemble-framework-prefix-matrix.py',
  'deploy/docker/dedupe-wine-prefixes.py',
  'deploy/docker/wine-netfx-framework-preflight.sh',
])

function hasRegistryHost(value) {
  if (typeof value !== 'string') return false
  const withoutScheme = value.startsWith('docker://') ? value.slice('docker://'.length) : value
  const at = withoutScheme.indexOf('@')
  const reference = at < 0 ? withoutScheme : withoutScheme.slice(0, at)
  if (!imageTag.test(reference)) return false
  const slash = reference.indexOf('/')
  if (slash <= 0) return false
  const host = reference.slice(0, slash)
  return host === 'localhost' || host.includes('.') || host.includes(':')
}

function imageRepository(value) {
  const slash = value.lastIndexOf('/')
  const colon = value.lastIndexOf(':')
  return colon > slash ? value.slice(0, colon) : value
}

function immutableDockerSourceReference(value) {
  if (typeof value !== 'string' || !value.startsWith('docker://')) return undefined
  const reference = value.slice('docker://'.length)
  return isDigestPinnedImageReference(reference) ? reference : undefined
}

function fail(message) {
  throw new Error(message)
}

function realDirectory(value, label) {
  if (typeof value !== 'string' || !path.isAbsolute(value)) {
    fail(`${label} must be an absolute path`)
  }
  const lexical = path.resolve(value)
  let resolved
  try {
    resolved = fs.realpathSync.native(lexical)
  } catch {
    fail(`${label} does not exist`)
  }
  if (resolved !== lexical || !fs.statSync(resolved).isDirectory()) {
    fail(`${label} must be a real directory without symlinked path components`)
  }
  const relative = path.relative(repositoryRoot, resolved)
  if (relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative))) {
    fail(`${label} must be outside the repository`)
  }
  return resolved
}

function validateMatrixDocument(value, label = matrixInputName) {
  if (value?.schemaVersion !== 1 || value?.strategy !== matrixStrategy ||
      !Array.isArray(value.rows) || value.rows.length !== requiredFrameworkRows.length) {
    fail(`${label} must use ${matrixStrategy} with the exact ${requiredFrameworkRows.length}-row Framework set`)
  }
  const declared = new Map()
  for (const row of value.rows) {
    if (typeof row !== 'object' || row === null ||
        typeof row.id !== 'string' || !safeId.test(row.id) || declared.has(row.id)) {
      fail(`${label} contains a duplicate or unsafe row id`)
    }
    if (typeof row.version !== 'string' || row.version.length === 0 ||
        !/^\d+(?:\.\d+){1,2}$/.test(row.version) ||
        !['clr2', 'clr4'].includes(row.clrGeneration)) {
      fail(`${label} row ${row.id} has invalid version or CLR generation`)
    }
    if (row.targetPrefix !== row.clrGeneration ||
        typeof row.companionVersions !== 'object' || row.companionVersions === null ||
        row.companionVersions[row.clrGeneration] !== row.version) {
      fail(`${label} row ${row.id} does not bind its target prefix to the exact version`)
    }
    if (!isDigestPinnedImageReference(row.operatorImage)) {
      fail(`${label} row ${row.id} must bind a digest-pinned operatorImage`)
    }
    declared.set(row.id, row)
  }
  const rowIds = [...declared.keys()].sort()
  if (JSON.stringify(rowIds) !== JSON.stringify(requiredFrameworkRows)) {
    fail(`${label} must contain the exact ${requiredFrameworkRows.length}-row Framework set`)
  }
  return { declared, rowIds }
}

function readMatrixInput(context) {
  const manifest = path.join(context, matrixInputName)
  let stat
  try {
    stat = fs.lstatSync(manifest)
  } catch {
    fail(`matrix context is missing ${matrixInputName}`)
  }
  if (!stat.isFile() || stat.isSymbolicLink() ||
      stat.size < 1 || stat.size > maximumMatrixMetadataBytes) {
    fail(`${matrixInputName} must be a regular file`)
  }
  const bytes = fs.readFileSync(manifest)
  let value
  try {
    value = JSON.parse(bytes.toString('utf8'))
  } catch (error) {
    fail(`${matrixInputName} is invalid JSON: ${error.message}`)
  }
  const { declared, rowIds } = validateMatrixDocument(value)
  const rowsRoot = path.join(context, 'rows')
  const rowsStat = fs.lstatSync(rowsRoot, { throwIfNoEntry: false })
  if (!rowsStat?.isDirectory() || rowsStat.isSymbolicLink()) {
    fail('matrix context must contain a real rows directory')
  }
  const actual = fs.readdirSync(rowsRoot, { withFileTypes: true })
  const actualIds = new Set()
  for (const entry of actual) {
    if (!entry.isDirectory() || entry.isSymbolicLink() || !safeId.test(entry.name)) {
      fail(`matrix rows contains an unsafe entry '${entry.name}'`)
    }
    actualIds.add(entry.name)
    if (!declared.has(entry.name)) fail(`matrix-input.json is missing row ${entry.name}`)
    const rowJson = path.join(rowsRoot, entry.name, 'row.json')
    const rowStat = fs.lstatSync(rowJson, { throwIfNoEntry: false })
    if (!rowStat?.isFile() || rowStat.isSymbolicLink() ||
        rowStat.size < 1 || rowStat.size > maximumMatrixMetadataBytes) {
      fail(`row ${entry.name} must contain a regular row.json`)
    }
    let rowValue
    try {
      rowValue = JSON.parse(fs.readFileSync(rowJson, 'utf8'))
    } catch (error) {
      fail(`row ${entry.name} row.json is invalid JSON: ${error.message}`)
    }
    const expected = declared.get(entry.name)
    if (rowValue?.schemaVersion !== 1 || rowValue.id !== entry.name ||
        rowValue.version !== expected.version ||
        rowValue.clrGeneration !== expected.clrGeneration ||
        rowValue.targetPrefix !== expected.targetPrefix ||
        rowValue.operatorImage !== expected.operatorImage ||
        rowValue.companionVersions?.clr2 !== expected.companionVersions.clr2 ||
        rowValue.companionVersions?.clr4 !== expected.companionVersions.clr4) {
      fail(`row ${entry.name} row.json does not match matrix-input.json`)
    }
    const rowContents = fs.readdirSync(path.join(rowsRoot, entry.name)).sort()
    if (rowContents.length !== 1 || rowContents[0] !== 'row.json') {
      fail(`row ${entry.name} metadata directory must contain only row.json`)
    }
  }
  if (actualIds.size !== declared.size || [...declared.keys()].some(id => !actualIds.has(id))) {
    fail('matrix-input.json rows do not match the rows directory')
  }
  return {
    manifestPath: manifest,
    manifestSha256: crypto.createHash('sha256').update(bytes).digest('hex'),
    rowCount: declared.size,
    rowIds,
    rows: rowIds.map(id => ({ ...declared.get(id) })),
  }
}

function copyContainerMetadata(spawn, containerId, source, destination) {
  const result = spawn('docker', ['cp', `${containerId}:${source}`, destination], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
  })
  if (result.error !== undefined || result.status !== 0) {
    fail(`could not read '${source}' from the immutable Framework matrix context image`)
  }
}

export function inspectMetadataImage(
  reference,
  expectedDigest,
  expectedRowCount,
  expectedRevision,
  spawn = spawnSync,
) {
  const result = spawn('docker', ['image', 'inspect', reference], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
  })
  if (result.error !== undefined || result.status !== 0) {
    fail(`immutable Framework matrix metadata image '${reference}' is unavailable`)
  }
  let parsed
  try { parsed = JSON.parse(result.stdout) } catch { fail('metadata image inspect returned invalid JSON') }
  if (!Array.isArray(parsed) || parsed.length !== 1) {
    fail('metadata image inspect did not return exactly one image')
  }
  const info = parsed[0]
  const suppliedDigest = reference.slice(reference.lastIndexOf('@') + 1)
  const repoDigests = Array.isArray(info.RepoDigests) ? info.RepoDigests : []
  if (info.Id !== suppliedDigest && !repoDigests.includes(reference)) {
    fail('metadata image does not resolve to its supplied immutable digest')
  }
  const labels = info.Config?.Labels ?? {}
  for (const [label, expected] of Object.entries({
    'io.sharplabnext.framework.matrix-context': 'true',
    'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
    'io.sharplabnext.framework.matrix-strategy': matrixStrategy,
    'io.sharplabnext.framework.matrix-input-sha256': expectedDigest,
    'io.sharplabnext.framework.matrix-row-count': String(expectedRowCount),
    'org.opencontainers.image.revision': expectedRevision,
    'io.sharplabnext.source.revision': expectedRevision,
  })) {
    if (labels[label] !== expected) fail(`metadata image label ${label} does not match the matrix input`)
  }
  if (info.Os !== 'linux' || info.Architecture !== 'amd64' ||
      !Number.isSafeInteger(info.Size) || info.Size <= 0 ||
      info.Size > maximumMetadataImageBytes) {
    fail(`metadata image must be linux/amd64 and no larger than ${maximumMetadataImageBytes} bytes`)
  }
}

function inspectDockerMatrixInput(
  reference,
  expectedDigest,
  expectedRevision,
  spawn = spawnSync,
) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-metadata-'))
  let containerId
  let primaryError
  try {
    inspectMetadataImage(
      reference,
      expectedDigest,
      requiredFrameworkRows.length,
      expectedRevision,
      spawn,
    )
    const created = spawn(
      'docker',
      ['create', '--platform', 'linux/amd64', '--entrypoint', '/bin/false', reference],
      {
        cwd: repositoryRoot,
        encoding: 'utf8',
        shell: false,
      },
    )
    if (created.error !== undefined || created.status !== 0) {
      fail(`immutable Framework matrix context image '${reference}' is unavailable`)
    }
    containerId = String(created.stdout ?? '').trim()
    if (!/^[0-9a-f]{12,64}$/.test(containerId)) {
      fail('Docker did not return a valid stopped context container identity')
    }

    const manifestPath = path.join(root, matrixInputName)
    copyContainerMetadata(spawn, containerId, `/${matrixInputName}`, manifestPath)
    const manifestStat = fs.lstatSync(manifestPath, { throwIfNoEntry: false })
    if (!manifestStat?.isFile() || manifestStat.isSymbolicLink() ||
        manifestStat.size < 1 || manifestStat.size > maximumMatrixMetadataBytes) {
      fail(`${matrixInputName} in the immutable context image must be a bounded regular file`)
    }
    let manifest
    try {
      manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'))
    } catch (error) {
      fail(`${matrixInputName} in the immutable context image is invalid JSON: ${error.message}`)
    }
    validateMatrixDocument(manifest, `${matrixInputName} in the immutable context image`)
    const actualManifestDigest = `sha256:${crypto.createHash('sha256')
      .update(fs.readFileSync(manifestPath)).digest('hex')}`
    if (actualManifestDigest !== expectedDigest) {
      fail(`FRAMEWORK_MATRIX_INPUT_SHA256 does not match immutable context matrix-input.json (${actualManifestDigest})`)
    }
    for (const row of manifest.rows) {
      const rowRoot = path.join(root, 'rows', row.id)
      fs.mkdirSync(rowRoot, { recursive: true })
      copyContainerMetadata(
        spawn,
        containerId,
        `/rows/${row.id}/row.json`,
        path.join(rowRoot, 'row.json'),
      )
    }
    const input = readMatrixInput(root)
    const actualDigest = `sha256:${input.manifestSha256}`
    if (actualDigest !== expectedDigest) {
      fail(`FRAMEWORK_MATRIX_INPUT_SHA256 does not match immutable context matrix-input.json (${actualDigest})`)
    }
    return input
  } catch (error) {
    primaryError = error
    throw error
  } finally {
    if (containerId !== undefined) {
      const removed = spawn('docker', ['rm', containerId], {
        cwd: repositoryRoot,
        encoding: 'utf8',
        shell: false,
      })
      if (primaryError === undefined && (removed.error !== undefined || removed.status !== 0)) {
        fail(`could not remove stopped Framework matrix context container '${containerId}'`)
      }
    }
    fs.rmSync(root, { recursive: true, force: true })
  }
}

export function validateParentInputs(values, matrixInput = undefined) {
  const failures = []
  const required = [
    ['ROOT_IMAGE', 'digest-pinned root image'],
    ['WINE_IMAGE', 'digest-pinned Wine image'],
  ]
  for (const [name, description] of required) {
    if (!isDigestPinnedImageReference(values?.[name])) {
      failures.push(`${name} must be a ${description} repository@sha256:<64 lowercase hex> reference`)
    }
  }
  if (!isCandidateSourceUri(values?.FRAMEWORK_MATRIX_SOURCE_URI)) {
    failures.push('FRAMEWORK_MATRIX_SOURCE_URI must be an HTTPS or immutable docker:// source URI')
  }
  if (values?.push === true && immutableDockerSourceReference(values?.FRAMEWORK_MATRIX_SOURCE_URI) === undefined) {
    failures.push('FRAMEWORK_MATRIX_SOURCE_URI must be an immutable docker:// image when --push is used')
  }
  if (values?.push === true &&
      immutableDockerSourceReference(values?.FRAMEWORK_MATRIX_SOURCE_URI) !== undefined &&
      !hasRegistryHost(values.FRAMEWORK_MATRIX_SOURCE_URI)) {
    failures.push('FRAMEWORK_MATRIX_SOURCE_URI must include an explicit registry host when --push is used')
  }
  const developmentRevision = values?.SOURCE_REVISION === 'development'
  const developmentOverride = values?.allowDirty === true
  if (!isGitCommitIdentity(values?.SOURCE_REVISION) &&
      !(developmentRevision && developmentOverride)) {
    failures.push(
      'SOURCE_REVISION must be a lowercase 40- or 64-character Git commit ' +
      '(or development with --allow-uncommitted-source-for-development)',
    )
  }
  if (typeof values?.IMAGE !== 'string' || !imageTag.test(values.IMAGE)) {
    failures.push('IMAGE must be a safe local repository tag')
  }
  if (values?.push === true && !hasRegistryHost(values.IMAGE)) {
    failures.push('IMAGE must include an explicit registry host when --push is used')
  }
  for (const name of ['ROOT_IMAGE', 'WINE_IMAGE']) {
    if (values?.push === true && isDigestPinnedImageReference(values?.[name]) &&
        !hasRegistryHost(values[name])) {
      failures.push(`${name} must include an explicit registry host when --push is used`)
    }
  }
  if (values?.push === true && !isGitCommitIdentity(values?.SOURCE_REVISION)) {
    failures.push('SOURCE_REVISION must be a full Git commit when --push is used')
  }
  if (!isSha256Digest(values?.FRAMEWORK_MATRIX_INPUT_SHA256)) {
    failures.push('FRAMEWORK_MATRIX_INPUT_SHA256 must be sha256:<64 lowercase hex>')
  }
  let validatedMatrixInput = matrixInput
  const hasLocalContext = typeof values?.CONTEXT === 'string' && values.CONTEXT.length > 0
  if (!hasLocalContext && immutableDockerSourceReference(values?.FRAMEWORK_MATRIX_SOURCE_URI) === undefined) {
    failures.push('CONTEXT is required unless FRAMEWORK_MATRIX_SOURCE_URI is an immutable docker:// image')
  } else if (hasLocalContext && !path.isAbsolute(values.CONTEXT)) {
    failures.push('CONTEXT must be an absolute external matrix context path')
  } else if (hasLocalContext) {
    try {
      const context = realDirectory(values.CONTEXT, 'CONTEXT')
      const input = readMatrixInput(context)
      validatedMatrixInput = input
      const expected = values.FRAMEWORK_MATRIX_INPUT_SHA256
      const actual = `sha256:${input.manifestSha256}`
      if (!isSha256Digest(expected) || expected !== actual) {
        failures.push(`FRAMEWORK_MATRIX_INPUT_SHA256 does not match matrix-input.json (${actual})`)
      }
    } catch (error) {
      failures.push(error.message)
    }
  }
  if (values?.push === true && Array.isArray(validatedMatrixInput?.rows) &&
      validatedMatrixInput.rows.some(row => !hasRegistryHost(row.operatorImage))) {
    failures.push('every Framework row operator image must include an explicit registry host when --push is used')
  }
  return failures
}

export function createParentDockerfile(template, rows) {
  if (typeof template !== 'string' || template.split(rowMountMarker).length !== 2) {
    fail('parent Dockerfile template must contain exactly one row mount marker')
  }
  if (!Array.isArray(rows) || rows.length !== requiredFrameworkRows.length) {
    fail(`parent Dockerfile requires exactly ${requiredFrameworkRows.length} Framework rows`)
  }
  const rowIds = rows.map(row => row.id)
  if (JSON.stringify(rowIds) !== JSON.stringify(requiredFrameworkRows)) {
    fail('parent Dockerfile rows are not the canonical Framework set')
  }
  const mounts = rows.map(row =>
    `    --mount=type=bind,from=framework-row-${row.id},` +
    `source=/opt/wine-netfx-${row.targetPrefix},` +
    `target=/run/sharplabnext-framework-rows/${row.id}/${row.targetPrefix},ro \\`,
  ).join('\n')
  return template.replace(rowMountMarker, mounts)
}

export function createParentBuildArguments(values, matrixInput, dockerfilePath) {
  const failures = validateParentInputs(values, matrixInput)
  if (failures.length > 0) throw new Error(failures.join('; '))
  if (!matrixInput || !Array.isArray(matrixInput.rows)) {
    throw new Error('validated matrix input rows are required')
  }
  if (typeof dockerfilePath !== 'string' || !path.isAbsolute(dockerfilePath)) {
    throw new Error('generated parent Dockerfile path must be absolute')
  }
  const output = values.push === true ? '--push' : '--load'
  const metadata = values.metadataFile === undefined
    ? []
    : ['--metadata-file', values.metadataFile]
  const sourceReference = immutableDockerSourceReference(values.FRAMEWORK_MATRIX_SOURCE_URI)
  const hasLocalContext = typeof values.CONTEXT === 'string' && values.CONTEXT.length > 0
  const frameworkMetadata = values.push === true || !hasLocalContext
    ? `docker-image://${sourceReference}`
    : values.CONTEXT
  const rowContexts = matrixInput.rows.flatMap(row => [
    '--build-context',
    `framework-row-${row.id}=docker-image://${row.operatorImage}`,
  ])
  return [
    'buildx', 'build',
    '--platform', 'linux/amd64',
    '--file', dockerfilePath,
    '--build-context', `framework-matrix-metadata=${frameworkMetadata}`,
    ...rowContexts,
    '--build-arg', `ROOT_IMAGE=${values.ROOT_IMAGE}`,
    '--build-arg', `WINE_IMAGE=${values.WINE_IMAGE}`,
    '--build-arg', `FRAMEWORK_MATRIX_INPUT_SHA256=${values.FRAMEWORK_MATRIX_INPUT_SHA256}`,
    '--build-arg', `FRAMEWORK_MATRIX_SOURCE_URI=${values.FRAMEWORK_MATRIX_SOURCE_URI}`,
    '--build-arg', `VERSION=${values.VERSION ?? 'development'}`,
    '--build-arg', `SOURCE_REVISION=${values.SOURCE_REVISION}`,
    '--tag', values.IMAGE,
    output, ...metadata, '--provenance=false', '.',
  ]
}

function parseArguments(arguments_) {
  const values = { VERSION: 'development' }
  let allowDirty = false
  let push = false
  for (let index = 0; index < arguments_.length; index++) {
    const argument = arguments_[index]
    if (argument === '--allow-uncommitted-source-for-development') {
      allowDirty = true
      continue
    }
    if (argument === '--push') {
      if (push) fail('--push may be specified only once')
      push = true
      continue
    }
    if (argument === '--help') return { help: true }
    if (!argument.startsWith('--') || index + 1 >= arguments_.length) {
      fail(`unknown or incomplete argument '${argument}'`)
    }
    const name = argument.slice(2).replaceAll('-', '_').toUpperCase()
    const allowed = new Set([
      'CONTEXT', 'ROOT_IMAGE', 'WINE_IMAGE', 'FRAMEWORK_MATRIX_SOURCE_URI',
      'FRAMEWORK_MATRIX_INPUT_SHA256', 'SOURCE_REVISION', 'IMAGE', 'VERSION',
    ])
    if (!allowed.has(name)) fail(`unknown argument '${argument}'`)
    values[name] = arguments_[++index]
  }
  values.allowDirty = allowDirty
  values.push = push
  return values
}

function usage() {
  return `Usage: node eng/build-framework-matrix-parent.mjs \\
  [--context <external-matrix-directory>] \\
  --root-image <repository@sha256:...> \\
  --wine-image <repository@sha256:...> \\
  --framework-matrix-source-uri <https://...|docker://...@sha256:...> \\
  --framework-matrix-input-sha256 sha256:<64-hex> \\
  --source-revision <40/64-hex> --image <registry/repository:tag> [--version <id>] [--push]\n\n` +
  `A digest-pinned docker:// matrix source is consumed directly and does not require --context. ` +
  `A host context is accepted only for local development.`
}

function inspectGitSource(
  spawn = spawnSync,
  fallbackRevision = undefined,
  environment = process.env,
) {
  if (String(environment?.[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() === contentSourceIdentityMode &&
      isGitCommitIdentity(fallbackRevision)) {
    return { headRevision: fallbackRevision, isDirty: true }
  }
  try {
    const revision = spawn('git', ['rev-parse', '--verify', 'HEAD'], {
      cwd: repositoryRoot,
      encoding: 'utf8',
      shell: false,
    })
    if (revision.error !== undefined || revision.status !== 0) fail('could not resolve Git HEAD')
    const headRevision = String(revision.stdout ?? '').trim()
    if (!isGitCommitIdentity(headRevision)) fail('Git HEAD is not a full commit identity')
    const status = spawn('git', ['status', '--porcelain=v1', '--untracked-files=normal'], {
      cwd: repositoryRoot,
      encoding: 'utf8',
      shell: false,
    })
    if (status.error !== undefined || status.status !== 0) fail('could not inspect Git source state')
    return { headRevision, isDirty: String(status.stdout ?? '').length > 0 }
  } catch (error) {
    if (isGitCommitIdentity(fallbackRevision)) {
      return { headRevision: fallbackRevision, isDirty: true }
    }
    throw error
  }
}

function createCommittedSourceContext(revision, spawn = spawnSync) {
  const context = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-source-'))
  try {
    for (const relative of parentSourceFiles) {
      const result = spawn('git', ['show', `${revision}:${relative}`], {
        cwd: repositoryRoot,
        encoding: null,
        shell: false,
        maxBuffer: 32 * 1024 * 1024,
      })
      if (result.error !== undefined || result.status !== 0) {
        fail(`could not read committed parent source '${relative}'`)
      }
      const bytes = Buffer.isBuffer(result.stdout)
        ? result.stdout
        : Buffer.from(result.stdout ?? '')
      if (bytes.length === 0) fail(`committed parent source '${relative}' is empty`)
      const destination = path.join(context, ...relative.split('/'))
      fs.mkdirSync(path.dirname(destination), { recursive: true })
      fs.writeFileSync(destination, bytes)
    }
    return context
  } catch (error) {
    fs.rmSync(context, { recursive: true, force: true })
    throw error
  }
}

function expectedParentLabels(expectedValues) {
  return {
    'io.sharplabnext.framework.matrix': 'true',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-target-prefix-matrix-v1',
    'io.sharplabnext.framework.dedupe-policy': 'wine-static-runtime-payload-v1',
    'org.opencontainers.image.revision': expectedValues.SOURCE_REVISION,
    'io.sharplabnext.source.revision': expectedValues.SOURCE_REVISION,
    'org.opencontainers.image.version': expectedValues.VERSION ?? 'development',
    'io.sharplabnext.framework.matrix-input-sha256': expectedValues.FRAMEWORK_MATRIX_INPUT_SHA256,
    'io.sharplabnext.framework.matrix-source-uri': expectedValues.FRAMEWORK_MATRIX_SOURCE_URI,
    'io.sharplabnext.operator-image.wine': expectedValues.WINE_IMAGE,
    'io.sharplabnext.operator-root': expectedValues.ROOT_IMAGE,
  }
}

function validateParentImageIdentity(imageInfo, expectedValues) {
  const labels = imageInfo.Config?.Labels ?? {}
  for (const [label, expected] of Object.entries(expectedParentLabels(expectedValues))) {
    if (expected !== undefined && labels[label] !== expected) {
      fail(`parent image label ${label} does not match the supplied input`)
    }
  }
  if (imageInfo.Os !== 'linux' || imageInfo.Architecture !== 'amd64') {
    fail(`parent image platform must be linux/amd64, observed ${imageInfo.Os ?? '<missing>'}/${imageInfo.Architecture ?? '<missing>'}`)
  }
  return labels
}

function inspectImage(image, expectedValues, spawn = spawnSync) {
  const result = spawn('docker', ['image', 'inspect', image], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
  })
  if (result.error !== undefined || result.status !== 0) fail(`Docker image '${image}' is unavailable`)
  let parsed
  try { parsed = JSON.parse(result.stdout) } catch { fail('Docker image inspect returned invalid JSON') }
  if (!Array.isArray(parsed) || parsed.length !== 1) fail('Docker image inspect did not return exactly one image')
  const imageInfo = parsed[0]
  const labels = validateParentImageIdentity(imageInfo, expectedValues)
  if (!Number.isSafeInteger(imageInfo.Size) || imageInfo.Size <= 0) fail('parent image has no positive inspected size')
  const repoDigests = Array.isArray(imageInfo.RepoDigests)
    ? imageInfo.RepoDigests.filter(value => typeof value === 'string')
    : []
  return { id: imageInfo.Id, sizeBytes: imageInfo.Size, labels, repoDigests }
}

function inspectPushedImage(reference, expectedDigest, expectedValues, spawn = spawnSync) {
  const formatted = spawn(
    'docker',
    ['buildx', 'imagetools', 'inspect', '--format', '{{json .}}', reference],
    { cwd: repositoryRoot, encoding: 'utf8', shell: false },
  )
  if (formatted.error !== undefined || formatted.status !== 0) {
    fail(`could not inspect pushed parent image '${reference}'`)
  }
  let report
  try { report = JSON.parse(formatted.stdout) } catch { fail('pushed parent inspection returned invalid JSON') }
  if (report?.manifest?.digest !== expectedDigest) {
    fail('pushed parent manifest digest does not match BuildKit metadata')
  }
  const image = report?.image
  const labels = validateParentImageIdentity({
    Os: image?.os,
    Architecture: image?.architecture,
    Config: image?.config,
  }, expectedValues)

  const raw = spawn(
    'docker',
    ['buildx', 'imagetools', 'inspect', '--raw', reference],
    { cwd: repositoryRoot, encoding: 'utf8', shell: false },
  )
  if (raw.error !== undefined || raw.status !== 0) {
    fail(`could not read pushed parent manifest '${reference}'`)
  }
  let manifest
  try { manifest = JSON.parse(raw.stdout) } catch { fail('pushed parent manifest is invalid JSON') }
  if (!Array.isArray(manifest?.layers) || manifest.layers.length === 0 ||
      manifest.layers.some(layer => !Number.isSafeInteger(layer?.size) || layer.size <= 0)) {
    fail('pushed parent manifest has invalid layers')
  }
  const sizeBytes = manifest.layers.reduce((total, layer) => total + layer.size, 0)
  return { id: expectedDigest, sizeBytes, labels, repoDigests: [reference] }
}

function readPushedDigest(filename) {
  let document
  try {
    document = JSON.parse(fs.readFileSync(filename, 'utf8'))
  } catch (error) {
    fail(`BuildKit metadata is invalid JSON: ${error.message}`)
  }
  const digest = document?.['containerimage.digest']
  if (!imageDigest.test(digest ?? '')) fail('BuildKit metadata does not contain a valid image digest')
  return digest
}

export function runParentBuild(argv, values = process.env, spawn = spawnSync, output = console) {
  let parsed
  try { parsed = parseArguments(argv) } catch (error) {
    output.error(`framework parent input error: ${error.message}`)
    return 64
  }
  if (parsed.help) { output.log(usage()); return 0 }
  const merged = { ...values, ...parsed }
  if (String(merged[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() ===
      contentSourceIdentityMode) {
    merged.allowDirty = true
  }
  const failures = validateParentInputs(merged)
  if (failures.length > 0) {
    for (const failure of failures) output.error(`framework parent input error: ${failure}`)
    return 1
  }
  let matrixInput
  try {
    const sourceReference = immutableDockerSourceReference(merged.FRAMEWORK_MATRIX_SOURCE_URI)
    const hasLocalContext = typeof merged.CONTEXT === 'string' && merged.CONTEXT.length > 0
    matrixInput = parsed.push || !hasLocalContext
      ? inspectDockerMatrixInput(
        sourceReference,
        merged.FRAMEWORK_MATRIX_INPUT_SHA256,
        merged.SOURCE_REVISION,
        spawn,
      )
      : readMatrixInput(realDirectory(merged.CONTEXT, 'CONTEXT'))
  } catch (error) {
    output.error(`framework parent context error: ${error.message}`)
    return 1
  }
  const matrixFailures = validateParentInputs(merged, matrixInput)
  if (matrixFailures.length > 0) {
    for (const failure of matrixFailures) output.error(`framework parent input error: ${failure}`)
    return 1
  }
  try {
    for (const row of matrixInput.rows) {
      inspectOrPullOperator(row, spawn, {
        baseImage: merged.WINE_IMAGE,
        rootImage: merged.ROOT_IMAGE,
        installerManifestSha256: crypto.createHash('sha256').update(fs.readFileSync(
          path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json'),
        )).digest('hex'),
        ...(merged.SOURCE_REVISION === 'development'
          ? {}
          : { sourceRevision: merged.SOURCE_REVISION }),
      })
    }
  } catch (error) {
    output.error(`framework parent operator error: ${error.message}`)
    return 1
  }
  let sourceBefore
  try { sourceBefore = inspectGitSource(spawn, merged.SOURCE_REVISION, merged) } catch (error) {
    output.error(`framework parent source error: ${error.message}`)
    return 1
  }
  const dirty = sourceBefore.isDirty
  if (merged.SOURCE_REVISION !== 'development' &&
      sourceBefore.headRevision !== merged.SOURCE_REVISION) {
    output.error(
      `framework parent source error: SOURCE_REVISION '${merged.SOURCE_REVISION}' ` +
      `does not match Git HEAD '${sourceBefore.headRevision}'`,
    )
    return 1
  }
  if (dirty && !merged.allowDirty) {
    output.error('framework parent source error: worktree is dirty; use the explicit development override')
    return 1
  }
  if (parsed.push && dirty) {
    output.error('framework parent source error: --push requires a clean worktree')
    return 1
  }
  const dockerEnvironment = { ...values, ...parsed }
  let sourceContext
  if (parsed.push) {
    try {
      sourceContext = createCommittedSourceContext(merged.SOURCE_REVISION, spawn)
    } catch (error) {
      output.error(`framework parent source error: ${error.message}`)
      return 1
    }
  }
  let metadataDirectory
  try {
    metadataDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-parent-'))
  } catch (error) {
    if (sourceContext !== undefined) fs.rmSync(sourceContext, { recursive: true, force: true })
    output.error(`framework parent source error: could not create temporary build metadata: ${error.message}`)
    return 1
  }
  const metadataFile = parsed.push
    ? path.join(metadataDirectory, 'build-metadata.json')
    : undefined
  let dockerArguments
  try {
    const sourceRoot = sourceContext ?? repositoryRoot
    const templatePath = path.join(
      sourceRoot,
      'deploy', 'docker', 'Dockerfile.operator-wine-framework-matrix-parent',
    )
    const template = fs.readFileSync(templatePath, 'utf8')
    const generatedDockerfile = path.join(metadataDirectory, 'Dockerfile.framework-parent.generated')
    fs.writeFileSync(
      generatedDockerfile,
      createParentDockerfile(template, matrixInput.rows),
      { encoding: 'utf8', flag: 'wx' },
    )
    dockerArguments = createParentBuildArguments(
      metadataFile === undefined ? merged : { ...merged, metadataFile },
      matrixInput,
      generatedDockerfile,
    )
  } catch (error) {
    fs.rmSync(metadataDirectory, { recursive: true, force: true })
    if (sourceContext !== undefined) fs.rmSync(sourceContext, { recursive: true, force: true })
    output.error(`framework parent input error: ${error.message}`)
    return 1
  }
  let result
  try {
    result = spawn('docker', dockerArguments, {
      cwd: sourceContext ?? repositoryRoot,
      env: dockerEnvironment,
      encoding: 'utf8',
      shell: false,
      stdio: 'inherit',
    })
  } catch (error) {
    fs.rmSync(metadataDirectory, { recursive: true, force: true })
    if (sourceContext !== undefined) fs.rmSync(sourceContext, { recursive: true, force: true })
    output.error(`framework parent Docker build failed: ${error.message}`)
    return 1
  }
  if (result.error !== undefined || result.status !== 0) {
    fs.rmSync(metadataDirectory, { recursive: true, force: true })
    if (sourceContext !== undefined) fs.rmSync(sourceContext, { recursive: true, force: true })
    output.error(`framework parent Docker build failed with exit code ${result.status ?? 1}`)
    return 1
  }
  try {
    const sourceAfter = inspectGitSource(spawn, merged.SOURCE_REVISION, merged)
    if (sourceAfter.headRevision !== sourceBefore.headRevision ||
        sourceAfter.isDirty !== sourceBefore.isDirty ||
        (parsed.push && sourceAfter.isDirty)) {
      fail('Git source state changed while the parent image was being built')
    }
    let inspectedReference = merged.IMAGE
    let pushedDigest
    let observed
    if (parsed.push) {
      pushedDigest = readPushedDigest(metadataFile)
      inspectedReference = `${imageRepository(merged.IMAGE)}@${pushedDigest}`
      observed = inspectPushedImage(inspectedReference, pushedDigest, merged, spawn)
    } else {
      observed = inspectImage(inspectedReference, merged, spawn)
    }
    if (parsed.push && !observed.repoDigests.includes(inspectedReference)) {
      fail(`pushed parent image does not expose RepoDigest '${inspectedReference}'`)
    }
    output.log(JSON.stringify({
      image: merged.IMAGE,
      imageId: observed.id,
      sizeBytes: observed.sizeBytes,
      rowCount: matrixInput.rowCount,
      promotionEligible: !dirty,
      ...(pushedDigest === undefined ? {} : { registryReference: inspectedReference }),
    }, null, 2))
    return 0
  } catch (error) {
    output.error(`framework parent identity error: ${error.message}`)
    return 1
  } finally {
    fs.rmSync(metadataDirectory, { recursive: true, force: true })
    if (sourceContext !== undefined) fs.rmSync(sourceContext, { recursive: true, force: true })
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runParentBuild(process.argv.slice(2))
}
