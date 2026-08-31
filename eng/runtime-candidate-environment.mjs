/**
 * Derive one runtime candidate's row-specific environment from the maintained
 * runtime matrix. Ordinary release/base inputs remain owned by
 * run-with-bake-environment.cs and are intentionally absent here.
 */

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import {
  isCandidateSourceUri,
  isDigestPinnedImageReference,
  isGitCommitIdentity,
  isHttpsUri,
  isSha256Digest,
  isSha512HexDigest,
} from './runtime-candidate-input-validation.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const defaultRuntimeMatrixPath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json');
const buildEntryPath = path.join(repositoryRoot, 'eng', 'build-runtime-candidate.mjs');
const publishEntryPath = path.join(repositoryRoot, 'eng', 'release', 'publish-runtime-candidate.mjs');
const maximumJsonBytes = 1024 * 1024;
export const frameworkCandidateInputStrategy = 'runtime-framework-candidate-input-v1'
const safeIdPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const safeDigestPinnedImagePattern = /^[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64}$/
const wineOperatorReceiptInput = 'WINE_CORECLR_OPERATOR_RECEIPT'
const wineOperatorReceiptSignatureInput = 'WINE_CORECLR_OPERATOR_RECEIPT_SIG'
const sourceIdentityModeEnvironmentVariable = 'SHARPLABNEXT_SOURCE_IDENTITY_MODE'
const contentSourceIdentityMode = 'content'
const historicalFrameworkOverride = '--allow-historical-framework-input'
const historicalFrameworkInput = 'RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_OPT_IN'
const localOperatorWrapperInput = 'WINE_CORECLR_LOCAL_OPERATOR_OPT_IN'
const localOperatorTagInput = 'WINE_CORECLR_LOCAL_OPERATOR_TAG'
const localOperatorImageIdInput = 'WINE_CORECLR_LOCAL_OPERATOR_IMAGE_ID'
const localOperatorBakeInput = 'WINE_CORECLR_LOCAL_OPERATOR_IMAGE'
const sharedWineOperatorContentTag = 'content'
const localImageTagPattern = /^[a-z0-9][a-z0-9._/-]*(?::[a-z0-9][a-z0-9._-]{0,127})$/
const imageIdPattern = /^sha256:[0-9a-f]{64}$/

export const runtimeCandidateEnvironmentUsage = `Usage:
  node eng/runtime-candidate-environment.mjs <profile-id>
    [--runtime-matrix PATH]
    [--wine-image REPOSITORY@sha256:<64-hex>]
    [--wine-operator-receipt ABSOLUTE_PATH]
    [--wine-operator-receipt-signature ABSOLUTE_PATH]
    [--framework-input PATH]
    [--publish-to <registry-host>/<repository>:<RELEASE_ID>]
    [-- [--allow-historical-framework-input]
         [build-runtime-candidate options]]`

export class RuntimeCandidateEnvironmentError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimeCandidateEnvironmentError'
  }
}

function fail(message, options) { throw new RuntimeCandidateEnvironmentError(message, options); }

function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

function assertExactKeys(value, expected, label) {
  if (!isObject(value)) fail(`${label} must be an object.`)
  const actual = Object.keys(value).sort()
  const wanted = [...expected].sort()
  if (JSON.stringify(actual) !== JSON.stringify(wanted)) {
    fail(`${label} must contain exactly: ${wanted.join(', ')}.`)
  }
}

function requiredString(value, label) {
  if (typeof value !== 'string' || value.length === 0) fail(`${label} must be non-empty.`)
  return value
}

function requiredSafeId(value, label) {
  const result = requiredString(value, label)
  if (!safeIdPattern.test(result)) fail(`${label} must be a safe lowercase identifier.`)
  return result
}

function requiredCommit(value, label) {
  if (!isGitCommitIdentity(value)) fail(`${label} must be a lowercase Git commit identity.`)
  return value
}

function requiredSha512(value, label) {
  if (!isSha512HexDigest(value)) fail(`${label} must be a lowercase SHA-512 digest.`)
  return value
}

function requiredDigest(value, label) {
  if (!isSha256Digest(value)) fail(`${label} must be sha256:<64 lowercase hex>.`)
  return value
}

function requiredImage(value, label, options = {}) {
  if (options.allowLocalTag === true &&
      typeof value === 'string' && localImageTagPattern.test(value) && !value.includes('@')) {
    return value
  }
  if (!isDigestPinnedImageReference(value) || !safeDigestPinnedImagePattern.test(value)) {
    fail(`${label} must be a repository@sha256:<64 lowercase hex> image reference.`)
  }
  return value
}

function imageDigest(reference) { return reference.slice(reference.lastIndexOf('@') + 1); }

function requiredPayload(value, label) {
  if (!isObject(value)) fail(`${label} must be an object.`)
  const url = requiredString(value.url, `${label}.url`)
  if (!isHttpsUri(url)) fail(`${label}.url must be an absolute HTTPS URI without credentials.`)
  const sourceUri = value.sourceUri === undefined
    ? undefined
    : requiredString(value.sourceUri, `${label}.sourceUri`)
  if (sourceUri !== undefined && !isCandidateSourceUri(sourceUri)) {
    fail(`${label}.sourceUri must be an immutable HTTPS or docker source URI.`)
  }
  return {
    url,
    sha512: requiredSha512(value.sha512, `${label}.sha512`),
    sourceUri,
  }
}

function indexUniqueRows(values, label) {
  if (!Array.isArray(values) || values.length === 0) fail(`${label} must be a non-empty array.`)
  const result = new Map()
  for (const row of values) {
    const id = requiredSafeId(row?.id, `${label} row id`)
    if (result.has(id)) fail(`${label} contains duplicate row '${id}'.`)
    result.set(id, row)
  }
  return result
}

function controlImage(matrix) { return requiredImage(matrix?.controlRuntime?.image, 'runtime matrix controlRuntime.image'); }

function coreClrEnvironment(profileId, row, platform, wineImage, matrix, options = {}) {
  const version = requiredString(row.version ?? row.resolvedVersion, `CoreCLR row '${row.id}' version`)
  const runtimeCommit = requiredCommit(row.runtimeCommit, `CoreCLR row '${row.id}' runtimeCommit`)
  const jitCommit = requiredCommit(row.jitCommit, `CoreCLR row '${row.id}' jitCommit`)
  const payload = requiredPayload(
    platform === 'linux' ? row.linux : row.windows,
    `CoreCLR row '${row.id}' ${platform} payload`,
  )
  const environment = {
    RUNTIME_MATRIX_PROFILE_ID: profileId,
    RUNTIME_MATRIX_RUNTIME_VERSION: version,
    RUNTIME_MATRIX_RUNTIME_COMMIT: runtimeCommit,
    RUNTIME_MATRIX_JIT_COMMIT: jitCommit,
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI: payload.sourceUri ?? payload.url,
  }

  if (platform === 'wine') {
    environment.RUNTIME_MATRIX_WINDOWS_URL = payload.url
    environment.RUNTIME_MATRIX_WINDOWS_SHA512 = payload.sha512
    environment.RUNTIME_MATRIX_WINE_IMAGE = requiredImage(
      wineImage,
      `explicit Wine operator image for '${profileId}'`,
      { allowLocalTag: options.allowLocalTag === true },
    )
    environment.RUNTIME_MATRIX_CONTROL_IMAGE = controlImage(matrix)
    return environment
  }

  environment.RUNTIME_MATRIX_RUNTIME_URL = payload.url
  environment.RUNTIME_MATRIX_RUNTIME_SHA512 = payload.sha512
  environment.RUNTIME_MATRIX_BASE_IMAGE = requiredImage(
    row.linuxBaseImage,
    `CoreCLR row '${row.id}' linuxBaseImage`,
  )
  if (row.checkedJit !== undefined) addCheckedJitEnvironment(environment, row)
  if (row.profilerProvider !== undefined) addProfilerEnvironment(environment, row)
  return environment
}

function addCheckedJitEnvironment(environment, row) {
  const checked = row.checkedJit
  if (!isObject(checked)) fail(`CoreCLR row '${row.id}' checkedJit must be an object.`)
  const bootstrap = checked.bootstrapSdk
  environment.RUNTIME_MATRIX_CHECKED_JIT_COMMIT = requiredCommit(
    checked.commit,
    `CoreCLR row '${row.id}' checkedJit.commit`,
  )
  environment.RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL = requiredString(
    checked.sourceArchive?.url,
    `CoreCLR row '${row.id}' checkedJit.sourceArchive.url`,
  )
  environment.RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512 = requiredSha512(
    checked.sourceArchive?.sha512,
    `CoreCLR row '${row.id}' checkedJit.sourceArchive.sha512`,
  )
  environment.RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION = bootstrap?.version ?? ''
  environment.RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL = bootstrap?.url ?? ''
  environment.RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512 = bootstrap?.sha512 ?? ''
  if (bootstrap !== undefined) {
    requiredString(bootstrap.version, `CoreCLR row '${row.id}' checkedJit.bootstrapSdk.version`)
    requiredString(bootstrap.url, `CoreCLR row '${row.id}' checkedJit.bootstrapSdk.url`)
    requiredSha512(
      bootstrap.sha512,
      `CoreCLR row '${row.id}' checkedJit.bootstrapSdk.sha512`,
    )
  }
  environment.RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE = requiredImage(
    checked.builderImage,
    `CoreCLR row '${row.id}' checkedJit.builderImage`,
  )
  for (const [name, value] of [
    ['CONFIGURATION', checked.configuration],
    ['TARGET_OS', checked.targetOs],
    ['ARCHITECTURE', checked.architecture],
    ['BUILD_COMPONENT', checked.buildComponent],
    ['PGO_MODE', checked.pgoMode],
    ['COMPILER', checked.compiler],
    ['GENERATOR', checked.generator],
    ['SOURCE_MAPPING_KIND', checked.sourceMappingKind],
  ]) {
    environment[`RUNTIME_MATRIX_CHECKED_JIT_${name}`] = requiredString(
      value,
      `CoreCLR row '${row.id}' checkedJit.${name.toLowerCase()}`,
    )
  }
  environment.RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE = checked.versionGenerationMode ?? '';
}

function addProfilerEnvironment(environment, row) {
  const provider = row.profilerProvider
  if (!isObject(provider)) fail(`CoreCLR row '${row.id}' profilerProvider must be an object.`)
  environment.RUNTIME_MATRIX_PROFILER_PROVIDER_ID = requiredSafeId(
    provider.id,
    `CoreCLR row '${row.id}' profilerProvider.id`,
  )
  environment.RUNTIME_MATRIX_PROFILER_BUILD_IMAGE = requiredImage(
    provider.builderImage,
    `CoreCLR row '${row.id}' profilerProvider.builderImage`,
  )
  environment.RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT = requiredCommit(
    provider.scaffold?.commit,
    `CoreCLR row '${row.id}' profilerProvider.scaffold.commit`,
  )
  environment.RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI = requiredString(
    provider.scaffold?.sourceUri,
    `CoreCLR row '${row.id}' profilerProvider.scaffold.sourceUri`,
  )
  environment.RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT = requiredCommit(
    provider.runtimeHeaders?.commit,
    `CoreCLR row '${row.id}' profilerProvider.runtimeHeaders.commit`,
  )
  environment.RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI = requiredString(
    provider.runtimeHeaders?.sourceUri,
    `CoreCLR row '${row.id}' profilerProvider.runtimeHeaders.sourceUri`,
  )
  environment.RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND = requiredString(
    provider.sourceMappingKind,
    `CoreCLR row '${row.id}' profilerProvider.sourceMappingKind`,
  )
}

function normalizeFrameworkInput(value, frameworkRows) {
  assertExactKeys(value, [
    'schemaVersion',
    'strategy',
    'parentImage',
    'metadataImage',
    'matrixInputSha256',
    'sourceRevision',
    'rows',
  ], 'Framework candidate input')
  if (value.schemaVersion !== 1 || value.strategy !== frameworkCandidateInputStrategy) {
    fail(`Framework candidate input must use ${frameworkCandidateInputStrategy} schema version 1.`)
  }
  if (!Array.isArray(value.rows) || value.rows.length !== frameworkRows.size) {
    fail(`Framework candidate input must contain exactly ${frameworkRows.size} rows.`)
  }
  const seen = new Set()
  const rows = value.rows.map((row, index) => {
    assertExactKeys(row, ['id', 'operatorImage', 'rowDigest'], `Framework candidate row ${index}`)
    const id = requiredSafeId(row.id, `Framework candidate row ${index} id`)
    if (seen.has(id)) fail(`Framework candidate input contains duplicate row '${id}'.`)
    seen.add(id)
    if (!frameworkRows.has(id)) fail(`Framework candidate input contains unknown row '${id}'.`)
    return {
      id,
      operatorImage: requiredImage(row.operatorImage, `Framework candidate row '${id}' operatorImage`),
      rowDigest: requiredDigest(row.rowDigest, `Framework candidate row '${id}' rowDigest`),
    }
  })
  const expectedIds = [...frameworkRows.keys()]
  if (JSON.stringify(rows.map(row => row.id)) !== JSON.stringify(expectedIds)) {
    fail('Framework candidate rows must use the runtime matrix order without omissions.')
  }
  return {
    schemaVersion: 1,
    strategy: frameworkCandidateInputStrategy,
    parentImage: requiredImage(value.parentImage, 'Framework candidate parentImage'),
    metadataImage: requiredImage(value.metadataImage, 'Framework candidate metadataImage'),
    matrixInputSha256: requiredDigest(
      value.matrixInputSha256,
      'Framework candidate matrixInputSha256',
    ),
    sourceRevision: requiredCommit(
      value.sourceRevision,
      'Framework candidate sourceRevision',
    ),
    rows,
  }
}

export function canonicalFrameworkCandidateInput(value, matrix) {
  const frameworkRows = indexUniqueRows(matrix?.framework?.targets, 'runtime matrix Framework targets')
  return `${JSON.stringify(normalizeFrameworkInput(value, frameworkRows))}\n`
}

function readBoundedRegularFile(filename, label, maximumBytes = maximumJsonBytes) {
  let before
  try {
    before = fs.lstatSync(filename)
  } catch (error) {
    fail(`${label} does not exist.`, { cause: error })
  }
  if (!before.isFile() || before.isSymbolicLink() || before.size < 1 || before.size > maximumBytes) {
    fail(`${label} must be a 1..${maximumBytes} byte regular non-link file.`)
  }
  const descriptor = fs.openSync(filename, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0))
  try {
    const opened = fs.fstatSync(descriptor)
    if (!opened.isFile() || opened.size !== before.size ||
        (before.dev !== undefined && opened.dev !== before.dev) ||
        (before.ino !== undefined && opened.ino !== before.ino)) {
      fail(`${label} changed while it was opened.`)
    }
    const bytes = fs.readFileSync(descriptor)
    const after = fs.fstatSync(descriptor)
    if (bytes.length !== opened.size || after.size !== opened.size ||
        after.mtimeMs !== opened.mtimeMs) {
      fail(`${label} changed while it was read.`)
    }
    return bytes
  } finally {
    fs.closeSync(descriptor)
  }
}

function parseJson(bytes, label) {
  try {
    return JSON.parse(bytes.toString('utf8'))
  } catch (error) {
    fail(`${label} is invalid JSON: ${error.message}`, { cause: error })
  }
}

export function readRuntimeMatrix(filename = defaultRuntimeMatrixPath) { return parseJson(readBoundedRegularFile(path.resolve(filename), 'runtime matrix'), 'runtime matrix'); }

export function readFrameworkCandidateInput(filename, matrix) {
  const absolute = path.resolve(requiredString(filename, 'Framework candidate input path'))
  const bytes = readBoundedRegularFile(absolute, 'Framework candidate input')
  const normalized = normalizeFrameworkInput(
    parseJson(bytes, 'Framework candidate input'),
    indexUniqueRows(matrix?.framework?.targets, 'runtime matrix Framework targets'),
  )
  const canonical = Buffer.from(`${JSON.stringify(normalized)}\n`)
  if (!bytes.equals(canonical)) {
    fail('Framework candidate input must use canonical JSON bytes.')
  }
  return Object.freeze({
    ...normalized,
    rows: Object.freeze(normalized.rows.map(row => Object.freeze(row))),
  })
}

function frameworkEnvironment(profileId, row, wineImage, matrix, frameworkInput) {
  if (frameworkInput === undefined) {
    fail(`Framework profile '${profileId}' requires --framework-input.`)
  }
  const normalized = normalizeFrameworkInput(
    frameworkInput,
    indexUniqueRows(matrix.framework.targets, 'runtime matrix Framework targets'),
  )
  const externalRow = normalized.rows.find(candidate => candidate.id === row.id)
  if (externalRow === undefined) fail(`Framework candidate input is missing row '${row.id}'.`)
  return {
    RUNTIME_MATRIX_PROFILE_ID: profileId,
    RUNTIME_MATRIX_RUNTIME_VERSION: requiredString(row.version, `Framework row '${row.id}' version`),
    RUNTIME_MATRIX_RUNTIME_DIGEST: imageDigest(externalRow.operatorImage),
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI: `docker://${externalRow.operatorImage}`,
    RUNTIME_MATRIX_WINE_IMAGE: requiredImage(
      wineImage,
      `explicit Wine operator image for '${profileId}'`,
    ),
    RUNTIME_MATRIX_CONTROL_IMAGE: controlImage(matrix),
    RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE: normalized.parentImage,
    RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: normalized.sourceRevision,
    RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256: normalized.matrixInputSha256,
    RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI: `docker://${normalized.metadataImage}`,
    RUNTIME_MATRIX_FRAMEWORK_TARGET_ID: row.id,
    RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION: requiredString(
      row.clrGeneration,
      `Framework row '${row.id}' clrGeneration`,
    ),
    RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE: externalRow.operatorImage,
    RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST: externalRow.rowDigest,
  }
}

/** Return only the row-specific overlay plus its reviewed Bake target. */
export function deriveRuntimeCandidateEnvironment(profileId, matrix, options = {}) {
  requiredSafeId(profileId, 'candidate profile ID')
  if (!isObject(matrix)) fail('runtime matrix must be an object.')
  const coreRows = indexUniqueRows(matrix.coreClr, 'runtime matrix CoreCLR rows')
  const frameworkRows = indexUniqueRows(matrix.framework?.targets, 'runtime matrix Framework targets')
  const linuxSuffix = '-linux-x64'
  let target
  let environment

  if (profileId === matrix.mono?.id) {
    const image = requiredImage(matrix.mono.image, 'runtime matrix Mono image')
    target = 'runtime-mono-matrix-candidate'
    environment = {
      RUNTIME_MATRIX_PROFILE_ID: profileId,
      RUNTIME_MATRIX_RUNTIME_VERSION: requiredString(matrix.mono.version, 'runtime matrix Mono version'),
      RUNTIME_MATRIX_RUNTIME_DIGEST: imageDigest(image),
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: `docker://${image}`,
      RUNTIME_MATRIX_MONO_IMAGE: image,
      RUNTIME_MATRIX_CONTROL_IMAGE: controlImage(matrix),
    }
  } else if (profileId.startsWith('wine-') && profileId.endsWith(linuxSuffix)) {
    const matrixId = profileId.slice('wine-'.length, -linuxSuffix.length)
    const coreRow = coreRows.get(matrixId)
    const frameworkRow = frameworkRows.get(matrixId)
    if (coreRow !== undefined) {
      const major = Number.parseInt(requiredString(coreRow.channel, `CoreCLR row '${matrixId}' channel`), 10)
      if (!Number.isSafeInteger(major) || major < 5) {
        fail(`Wine CoreCLR profile '${profileId}' is excluded; CoreCLR 2.x/3.x is not supported under Wine.`)
      }
      target = 'runtime-wine-dotnet-matrix-candidate'
      environment = coreClrEnvironment(profileId, coreRow, 'wine', options.wineImage, matrix, {
        allowLocalTag: options.allowLocalWineOperator === true,
      })
    } else if (frameworkRow !== undefined) {
      target = 'runtime-wine-framework-matrix-shared-candidate'
      environment = frameworkEnvironment(
        profileId,
        frameworkRow,
        options.wineImage,
        matrix,
        options.frameworkInput,
      )
    }
  } else if (profileId.endsWith(linuxSuffix)) {
    const matrixId = profileId.slice(0, -linuxSuffix.length)
    const coreRow = coreRows.get(matrixId)
    if (coreRow !== undefined) {
      target = 'runtime-dotnet-matrix-candidate'
      environment = coreClrEnvironment(profileId, coreRow, 'linux', undefined, matrix)
    }
  }

  if (target === undefined || environment === undefined) {
    fail(`candidate profile '${profileId}' is not a maintained runtime matrix row.`)
  }
  if (options.wineImage !== undefined && environment.RUNTIME_MATRIX_WINE_IMAGE === undefined) {
    fail(`--wine-image is not applicable to '${profileId}'.`)
  }
  if (options.frameworkInput !== undefined &&
      target !== 'runtime-wine-framework-matrix-shared-candidate') {
    fail(`--framework-input is not applicable to '${profileId}'.`)
  }
  for (const key of Object.keys(environment)) {
    if (!key.startsWith('RUNTIME_MATRIX_')) {
      fail(`internal error: derived environment contains non-row input '${key}'.`)
    }
  }
  return Object.freeze({
    target,
    environment: Object.freeze({ ...environment }),
  })
}

/** Canonical promotion order for the maintained 34-row runtime matrix. */
export function formalRuntimeCandidateProfileIds(matrix) {
  if (!isObject(matrix)) fail('runtime matrix must be an object.')
  const coreRows = indexUniqueRows(matrix.coreClr, 'runtime matrix CoreCLR rows')
  const frameworkRows = indexUniqueRows(matrix.framework?.targets, 'runtime matrix Framework targets')
  const linux = [...coreRows.keys()].map(id => `${id}-linux-x64`)
  const wine = [...coreRows.values()]
    .filter(row => {
      const major = Number.parseInt(requiredString(row.channel, `CoreCLR row '${row.id}' channel`), 10)
      return Number.isSafeInteger(major) && major >= 5
    })
    .map(row => `wine-${row.id}-linux-x64`)
  const mono = [requiredSafeId(matrix.mono?.id, 'runtime matrix Mono id')]
  const framework = [...frameworkRows.keys()].map(id => `wine-${id}-linux-x64`)
  const result = [...linux, ...wine, ...mono, ...framework]
  if (result.length !== 34 || new Set(result).size !== result.length) {
    fail(`formal runtime candidate scope must contain exactly 34 unique rows; observed ${result.length}.`)
  }
  return Object.freeze(result)
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  const separator = argv.indexOf('--')
  const own = separator < 0 ? argv : argv.slice(0, separator)
  const buildArguments = separator < 0 ? undefined : argv.slice(separator + 1)
  const profileId = own[0]
  if (profileId === undefined || profileId.startsWith('-')) fail('candidate profile ID is required.')
  const values = { profileId, buildArguments }
  const seen = new Set()
  for (let index = 1; index < own.length; index++) {
    const option = own[index]
    const field = {
      '--runtime-matrix': 'runtimeMatrixPath',
      '--wine-image': 'wineImage',
      '--wine-operator-receipt': 'wineOperatorReceiptPath',
      '--wine-operator-receipt-signature': 'wineOperatorReceiptSignaturePath',
      '--framework-input': 'frameworkInputPath',
      '--publish-to': 'publishDestination',
    }[option]
    if (field === undefined) fail(`unknown option '${option}'.`)
    if (seen.has(option)) fail(`duplicate option '${option}'.`)
    seen.add(option)
    const value = own[++index]
    if (value === undefined || value.length === 0) fail(`${option} requires a value.`)
    values[field] = value
  }
  return values
}

function isRealLocalBuild(buildArguments) {
  if (buildArguments === undefined) return false
  return !buildArguments.some((argument, index) =>
    argument === '--check' || argument === '--print' || argument === '--call' ||
    argument.startsWith('--call=') ||
    (index > 0 && buildArguments[index - 1] === '--call'))
}

export function runRuntimeCandidateEnvironment(argv, options = {}) {
  const {
    spawn = spawnSync,
    values = process.env,
    output = console,
  } = options
  let parsed
  try {
    parsed = parseArguments(argv)
    if (parsed.help) {
      output.log(runtimeCandidateEnvironmentUsage)
      return 0
    }
    const contentSourceIdentity = String(values[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() === contentSourceIdentityMode
    const historicalFrameworkOverrideCount = (parsed.buildArguments ?? []).filter(argument => argument === historicalFrameworkOverride).length
    if (historicalFrameworkOverrideCount > 1) {
      fail(`${historicalFrameworkOverride} may be supplied once after --.`)
    }
    const historicalFrameworkMode = historicalFrameworkOverrideCount === 1
    const matrix = readRuntimeMatrix(parsed.runtimeMatrixPath ?? defaultRuntimeMatrixPath)
    const frameworkInput = parsed.frameworkInputPath === undefined
      ? undefined
      : readFrameworkCandidateInput(parsed.frameworkInputPath, matrix)
    const derived = deriveRuntimeCandidateEnvironment(parsed.profileId, matrix, {
      wineImage: parsed.wineImage,
      frameworkInput,
      allowLocalWineOperator: contentSourceIdentity,
    })
    if (parsed.publishDestination !== undefined && parsed.buildArguments !== undefined) {
      fail('--publish-to cannot be combined with candidate build options after --.')
    }
    const commandMode = parsed.publishDestination !== undefined || parsed.buildArguments !== undefined
    const wineCandidate = derived.environment.RUNTIME_MATRIX_WINE_IMAGE !== undefined
    const wineCoreClrCandidate = derived.target === 'runtime-wine-dotnet-matrix-candidate'
    const sharedFrameworkCandidate =
      derived.target === 'runtime-wine-framework-matrix-shared-candidate'
    const localWineOperatorMode = wineCoreClrCandidate &&
      contentSourceIdentity
    const localFrameworkMode = sharedFrameworkCandidate &&
      (historicalFrameworkMode || contentSourceIdentity)
    const hasWineOperatorReceipt = parsed.wineOperatorReceiptPath !== undefined
    const hasWineOperatorReceiptSignature = parsed.wineOperatorReceiptSignaturePath !== undefined
    if (hasWineOperatorReceipt !== hasWineOperatorReceiptSignature) {
      fail('--wine-operator-receipt and --wine-operator-receipt-signature must be supplied together.')
    }
    if (localWineOperatorMode) {
      const expectedTag = `${values.IMAGE_PREFIX}/operator-wine-coreclr:${values.RELEASE_ID}`
      const contentTag = `${values.IMAGE_PREFIX}/operator-wine-coreclr:${sharedWineOperatorContentTag}`
      if (parsed.wineImage !== expectedTag && parsed.wineImage !== contentTag) {
        fail(
          `Local inputs require the exact Wine operator tag ` +
          `'${expectedTag}' or shared content tag '${contentTag}'.`,
        )
      }
    }
    if (historicalFrameworkMode &&
        (!isRealLocalBuild(parsed.buildArguments) || parsed.publishDestination !== undefined)) {
      fail(`${historicalFrameworkOverride} is accepted only for real local candidate builds.`)
    }
    if (historicalFrameworkMode && !sharedFrameworkCandidate) {
      fail(`${historicalFrameworkOverride} is supported only for shared Wine Framework candidates.`)
    }
    if (historicalFrameworkMode && !contentSourceIdentity) fail(`${historicalFrameworkOverride} requires content source identity.`)
    if (historicalFrameworkMode &&
        (!isGitCommitIdentity(values.SOURCE_REVISION) ||
         !isGitCommitIdentity(frameworkInput?.sourceRevision) ||
         frameworkInput.sourceRevision === values.SOURCE_REVISION)) {
      fail(
        `${historicalFrameworkOverride} requires distinct valid Framework input and ` +
        'candidate source revisions.',
      )
    }
    if (historicalFrameworkMode && hasWineOperatorReceipt) {
      fail('Historical Framework candidates must not receive formal operator receipts.')
    }
    if (contentSourceIdentity && parsed.publishDestination !== undefined) fail('Content source identity is accepted only for local candidate builds.')
    if (contentSourceIdentity && wineCandidate && hasWineOperatorReceipt) fail('Content source candidates must not receive formal operator receipts.')
    if (localFrameworkMode &&
        (!isGitCommitIdentity(values.SOURCE_REVISION) ||
         !isGitCommitIdentity(frameworkInput?.sourceRevision))) {
      fail('Local Framework inputs require valid Framework and candidate source revisions.')
    }
    if (hasWineOperatorReceipt && !commandMode) {
      fail('Wine operator receipt options are only accepted for build or publish commands.')
    }
    if ((hasWineOperatorReceipt || hasWineOperatorReceiptSignature) && !wineCandidate) {
      fail(`Wine operator receipt options are not applicable to '${parsed.profileId}'.`)
    }
    if (commandMode && wineCandidate && !localWineOperatorMode &&
        !localFrameworkMode && !hasWineOperatorReceipt) {
      fail('Wine candidate build and publish commands require --wine-operator-receipt and --wine-operator-receipt-signature.')
    }
    if (hasWineOperatorReceipt &&
        (!path.isAbsolute(parsed.wineOperatorReceiptPath) ||
         !path.isAbsolute(parsed.wineOperatorReceiptSignaturePath))) {
      fail('Wine operator receipt paths must be absolute.')
    }
    if (parsed.publishDestination === undefined && parsed.buildArguments === undefined) {
      output.log(JSON.stringify(derived))
      return 0
    }
    const entryPath = parsed.publishDestination === undefined ? buildEntryPath : publishEntryPath
    const entryArguments = parsed.publishDestination === undefined
      ? [
          derived.target,
          ...parsed.buildArguments,
        ]
      : [derived.target, '--destination', parsed.publishDestination]
    const childEnvironment = { ...values, ...derived.environment }
    delete childEnvironment[wineOperatorReceiptInput]
    delete childEnvironment[wineOperatorReceiptSignatureInput]
    delete childEnvironment.WINE_CORECLR_OPERATOR_RECEIPT_SHA256
    delete childEnvironment.WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID
    delete childEnvironment.WINE_CORECLR_OPERATOR_REFERENCE
    delete childEnvironment[localOperatorWrapperInput]
    delete childEnvironment[localOperatorTagInput]
    delete childEnvironment[localOperatorImageIdInput]
    delete childEnvironment[localOperatorBakeInput]
    delete childEnvironment[historicalFrameworkInput]
    if (hasWineOperatorReceipt) {
      childEnvironment[wineOperatorReceiptInput] = parsed.wineOperatorReceiptPath
      childEnvironment[wineOperatorReceiptSignatureInput] = parsed.wineOperatorReceiptSignaturePath
    }
    if (historicalFrameworkMode) {
      childEnvironment[historicalFrameworkInput] = 'true'
    }
    if (localWineOperatorMode) {
      const localTag = derived.environment.RUNTIME_MATRIX_WINE_IMAGE
      const inspect = options.inspectDockerImage ?? ((reference) => {
        const result = spawn('docker', ['image', 'inspect', reference], {
          cwd: repositoryRoot,
          env: childEnvironment,
          encoding: 'utf8',
          shell: false,
        })
        if (result?.error !== undefined || result?.status !== 0) {
          fail(`Could not inspect local Wine operator '${reference}'.`)
        }
        let document
        try { document = JSON.parse(String(result.stdout ?? '')) } catch {
          fail(`Local Wine operator '${reference}' returned invalid inspection JSON.`)
        }
        if (!Array.isArray(document) || document.length !== 1) {
          fail(`Local Wine operator '${reference}' must resolve to exactly one image.`)
        }
        return { imageId: document[0]?.Id }
      })
      const inspection = inspect(localTag)
      if (!imageIdPattern.test(inspection?.imageId ?? '')) {
        fail(`Local Wine operator '${localTag}' has no immutable local image ID.`)
      }
      childEnvironment.RUNTIME_MATRIX_WINE_IMAGE = inspection.imageId
      childEnvironment[localOperatorWrapperInput] = 'true'
      childEnvironment[localOperatorTagInput] = localTag
      childEnvironment[localOperatorImageIdInput] = inspection.imageId
    }
    const result = spawn(
      process.execPath,
      [entryPath, ...entryArguments],
      {
        cwd: repositoryRoot,
        env: childEnvironment,
        stdio: 'inherit',
        shell: false,
      },
    )
    if (result.error !== undefined) {
      output.error(`Could not start runtime candidate operation: ${result.error.message}`)
      return 1
    }
    return result.status ?? 1
  } catch (error) {
    output.error(`runtime candidate environment error: ${error.message}`)
    return 1;
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimeCandidateEnvironment(process.argv.slice(2))
}
