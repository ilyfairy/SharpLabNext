/**
 * Refresh the local functional verification inventory for the maintained
 * runtime matrix. This tool observes candidate profiles and local image tags;
 * it does not build, run, promote, or deploy anything.
 */

import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { candidateMatrixBinding } from '../build-runtime-candidate.mjs'
import {
  formalRuntimeCandidateProfileIds,
  readRuntimeMatrix,
} from '../runtime-candidate-environment.mjs'
import { inspectDockerImage } from '../release/runtime-promotion-image-binding.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const defaultMatrixPath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
const defaultCandidateDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')
const defaultOutputPath = path.join(repositoryRoot, '.tmp', 'runtime-matrix-functional-results.json')
const maximumResultBytes = 16 * 1024 * 1024
const sha256Pattern = /^sha256:[0-9a-f]{64}$/

export const runtimeFunctionalMatrixSchemaVersion = 1

export const runtimeFunctionalMatrixUsage = `Usage:
  node eng/smoke/runtime-functional-matrix.mjs [--output PATH]`

export class RuntimeFunctionalMatrixError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimeFunctionalMatrixError'
  }
}

const candidateTargetByFamily = Object.freeze({
  coreclr: 'runtime-dotnet-matrix-candidate',
  'coreclr-wine': 'runtime-wine-dotnet-matrix-candidate',
  mono: 'runtime-mono-matrix-candidate',
  'netfx-clr-wine': 'runtime-wine-framework-matrix-shared-candidate',
})

function fail(message, options) { throw new RuntimeFunctionalMatrixError(message, options); }

function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

function requiredString(value, label) {
  if (typeof value !== 'string' || value.length === 0) fail(`${label} must be non-empty.`)
  return value
}

function sha256(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`; }

function sortedStringMap(value, label) {
  if (!isObject(value)) fail(`${label} must be an object.`)
  const result = {}
  for (const [name, item] of Object.entries(value).sort(([left], [right]) =>
    left.localeCompare(right, 'en'))) {
    if (typeof item !== 'string') fail(`${label}.${name} must be a string.`)
    result[name] = item
  }
  return result
}

function readBoundedJson(filename, label, maximumBytes = maximumResultBytes) {
  let metadata
  try {
    metadata = fs.lstatSync(filename)
  } catch (error) {
    fail(`${label} '${filename}' could not be inspected: ${error.message}`, { cause: error })
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() ||
      metadata.size < 1 || metadata.size > maximumBytes) {
    fail(`${label} '${filename}' must be a 1..${maximumBytes} byte regular non-link file.`)
  }
  let bytes
  try {
    bytes = fs.readFileSync(filename)
  } catch (error) {
    fail(`${label} '${filename}' could not be read: ${error.message}`, { cause: error })
  }
  try {
    return { bytes, value: JSON.parse(bytes.toString('utf8')) }
  } catch (error) {
    fail(`${label} '${filename}' is invalid JSON: ${error.message}`, { cause: error })
  }
}

function readPreviousResults(filename) {
  if (!fs.existsSync(filename)) return new Map()
  const { value } = readBoundedJson(filename, 'Previous functional result')
  if (!isObject(value) || value.schemaVersion !== runtimeFunctionalMatrixSchemaVersion ||
      !Array.isArray(value.rows)) {
    fail(
      `Previous functional result '${filename}' must use schema version ` +
      `${runtimeFunctionalMatrixSchemaVersion} with a rows array.`,
    )
  }
  const result = new Map()
  for (const [index, row] of value.rows.entries()) {
    if (!isObject(row)) fail(`Previous functional result row ${index} must be an object.`)
    const profileId = requiredString(row.profileId, `Previous functional result row ${index}.profileId`)
    if (result.has(profileId)) fail(`Previous functional result contains duplicate row '${profileId}'.`)
    if (!sha256Pattern.test(row.profileSha256 ?? '')) {
      fail(`Previous functional result row '${profileId}' has an invalid profileSha256.`)
    }
    if (!isObject(row.image) ||
        !(row.image.imageId === null || sha256Pattern.test(row.image.imageId ?? ''))) {
      fail(`Previous functional result row '${profileId}' has an invalid image identity.`)
    }
    if (!isObject(row.verification) || typeof row.verification.status !== 'string') {
      fail(`Previous functional result row '${profileId}' has no valid verification state.`)
    }
    result.set(profileId, row)
  }
  return result
}

function validateProfile(profile, profileId, candidateTarget, matrix) {
  if (!isObject(profile) || profile.schemaVersion !== 1) {
    fail(`Candidate profile '${profileId}' must use schema version 1.`)
  }
  if (profile.id !== profileId) {
    fail(`Candidate profile '${profileId}' declares mismatched id '${profile.id ?? '<missing>'}'.`)
  }
  const image = requiredString(profile.image, `Candidate profile '${profileId}'.image`)
  if (profile.runtimeImageId !== image) {
    fail(`Candidate profile '${profileId}' runtimeImageId must equal its candidate image tag.`)
  }
  const family = requiredString(profile.family, `Candidate profile '${profileId}'.family`)
  if (candidateTargetByFamily[family] !== candidateTarget) {
    fail(`Candidate profile '${profileId}' family '${family}' does not match '${candidateTarget}'.`)
  }
  const binding = candidateMatrixBinding(candidateTarget, profileId, matrix)
  if (binding.family !== family) {
    fail(
      `Candidate profile '${profileId}' family '${family}' disagrees with matrix family ` +
      `'${binding.family}'.`,
    )
  }
  const runtimeVersion = requiredString(profile.runtimeVersion, `Candidate profile '${profileId}'.runtimeVersion`)
  const matrixVersion = binding.row.version ?? binding.row.resolvedVersion
  if (runtimeVersion !== matrixVersion) {
    fail(
      `Candidate profile '${profileId}' runtime version '${runtimeVersion}' disagrees with ` +
      `matrix version '${matrixVersion ?? '<missing>'}'.`,
    )
  }
  if (!Array.isArray(profile.capabilities) ||
      profile.capabilities.some(value => typeof value !== 'string' || value.length === 0) ||
      new Set(profile.capabilities).size !== profile.capabilities.length) {
    fail(`Candidate profile '${profileId}' capabilities must be unique non-empty strings.`)
  }
  const operations = isObject(profile.operations) ? profile.operations : {}
  const runImplementationId = operations.run?.implementationId ?? null
  const jitImplementationId = operations.jit?.implementationId ?? null
  const sourceMappingKind = operations.jit?.sourceMappingKind ?? 'none'
  for (const [name, value] of [
    ['run implementation ID', runImplementationId],
    ['JIT implementation ID', jitImplementationId],
    ['source mapping kind', sourceMappingKind],
  ]) {
    if (!(value === null || (typeof value === 'string' && value.length > 0))) {
      fail(`Candidate profile '${profileId}' has an invalid ${name}.`)
    }
  }
  if (profile.capabilities.includes('run') && runImplementationId === null) {
    fail(`Candidate profile '${profileId}' declares Run without a Run implementation.`)
  }
  if (profile.capabilities.includes('jit-asm') && jitImplementationId === null) {
    fail(`Candidate profile '${profileId}' declares JIT ASM without a JIT implementation.`)
  }
  return Object.freeze({
    binding,
    image,
    family,
    runtimeVersion,
    capabilities: Object.freeze([...profile.capabilities]),
    runImplementationId,
    jitImplementationId,
    sourceMappingKind,
  })
}

function normalizeInspection(reference, inspection) {
  if (!sha256Pattern.test(inspection?.imageId ?? '')) {
    fail(`Docker inspection for '${reference}' returned an invalid image ID.`)
  }
  if (!Number.isSafeInteger(inspection.sizeBytes) || inspection.sizeBytes <= 0) {
    fail(`Docker inspection for '${reference}' returned an invalid image size.`)
  }
  return {
    reference,
    imageId: inspection.imageId,
    sizeBytes: inspection.sizeBytes,
    operatingSystem: requiredString(inspection.operatingSystem, `Docker inspection for '${reference}'.operatingSystem`),
    architecture: requiredString(inspection.architecture, `Docker inspection for '${reference}'.architecture`),
    repoDigests: [...inspection.repoDigests],
    labels: sortedStringMap(inspection.labels, `Docker inspection for '${reference}'.labels`),
    inspectionError: null,
  }
}

function conciseError(error) {
  const message = String(error?.message ?? error).replace(/\s+/g, ' ').trim()
  return message.length <= 500 ? message : `${message.slice(0, 497)}...`
}

function inspectCandidate(reference, inspect, inspectOptions) {
  try {
    return normalizeInspection(reference, inspect(reference, inspectOptions))
  } catch (error) {
    return {
      reference,
      imageId: null,
      sizeBytes: null,
      operatingSystem: null,
      architecture: null,
      repoDigests: [],
      labels: {},
      inspectionError: conciseError(error),
    }
  }
}

function defaultVerification(profile, image, reason) {
  const capabilities = new Set(profile.capabilities)
  return {
    status: 'unverified',
    reason,
    smoke: {
      runtimeIdentity: image.imageId === null ? 'unavailable' : 'unverified',
      compile: 'unverified',
      run: capabilities.has('run') ? 'unverified' : 'not-applicable',
      ilDecompile: 'unverified',
      jit: capabilities.has('jit-asm') ? 'unverified' : 'not-applicable',
      mapping: profile.sourceMappingKind === 'none' ? 'not-applicable' : 'unverified',
    },
  }
}

function mergeVerification(previous, current) {
  const sameProfile = previous?.profileSha256 === current.profileSha256
  const sameImage = current.image.imageId !== null &&
    previous?.image?.imageId !== null &&
    previous?.image?.imageId === current.image.imageId
  if (sameProfile && sameImage) return structuredClone(previous.verification)

  let reason = 'new-row'
  if (current.image.imageId === null) reason = 'candidate-image-unavailable'
  else if (previous !== undefined && !sameProfile) reason = 'profile-changed'
  else if (previous !== undefined && !sameImage) reason = 'candidate-image-changed'
  return defaultVerification(current.expected, current.image, reason)
}

function targetForFamily(family, profileId) {
  const target = candidateTargetByFamily[family]
  if (target === undefined) fail(`Candidate profile '${profileId}' has unsupported family '${family}'.`)
  return target
}

function writeJsonAtomically(filename, value) {
  const directory = path.dirname(filename)
  fs.mkdirSync(directory, { recursive: true })
  const temporary = path.join(directory, `.${path.basename(filename)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`)
  try {
    fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' })
    fs.renameSync(temporary, filename)
  } finally {
    fs.rmSync(temporary, { force: true })
  }
}

export function refreshRuntimeFunctionalMatrix(options = {}) {
  const matrixPath = path.resolve(options.matrixPath ?? defaultMatrixPath)
  const candidateDirectory = path.resolve(options.candidateDirectory ?? defaultCandidateDirectory)
  const outputPath = path.resolve(options.outputPath ?? defaultOutputPath)
  const inspect = options.inspect ?? inspectDockerImage
  const previous = readPreviousResults(outputPath)
  const matrixBytes = fs.readFileSync(matrixPath)
  const matrix = readRuntimeMatrix(matrixPath)
  const profileIds = formalRuntimeCandidateProfileIds(matrix)
  const rows = []

  for (const profileId of profileIds) {
    const profilePath = path.join(candidateDirectory, `${profileId}.json`)
    const { bytes, value: profile } = readBoundedJson(profilePath, `Candidate profile '${profileId}'`)
    const family = requiredString(profile?.family, `Candidate profile '${profileId}'.family`)
    const candidateTarget = targetForFamily(family, profileId)
    const validated = validateProfile(profile, profileId, candidateTarget, matrix)
    const image = inspectCandidate(validated.image, inspect, options.inspectOptions)
    const row = {
      profileId,
      matrixTargetId: validated.binding.matrixTargetId,
      candidateTarget,
      family: validated.family,
      runtimeVersion: validated.runtimeVersion,
      referenceSetId: validated.binding.row.referenceSetId ?? null,
      profileSha256: sha256(bytes),
      candidateImage: validated.image,
      expected: {
        capabilities: [...validated.capabilities],
        runImplementationId: validated.runImplementationId,
        jitImplementationId: validated.jitImplementationId,
        sourceMappingKind: validated.sourceMappingKind,
      },
      image,
    }
    row.verification = mergeVerification(previous.get(profileId), row)
    rows.push(row)
  }

  const result = {
    schemaVersion: runtimeFunctionalMatrixSchemaVersion,
    runtimeMatrixSha256: sha256(matrixBytes),
    refreshedAt: (options.now ?? (() => new Date()))().toISOString(),
    rows,
  }
  writeJsonAtomically(outputPath, result)
  return result
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  const result = {}
  for (let index = 0; index < argv.length; index++) {
    const option = argv[index]
    if (option !== '--output') fail(`Unknown option '${option}'.`)
    if (result.outputPath !== undefined) fail("Duplicate option '--output'.")
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail("Option '--output' requires a path.")
    result.outputPath = value
  }
  return result
}

export function runRuntimeFunctionalMatrix(argv, options = {}) {
  const output = options.output ?? console
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) {
      output.log(runtimeFunctionalMatrixUsage)
      return 0
    }
    const result = refreshRuntimeFunctionalMatrix({
      ...options,
      outputPath: parsed.outputPath ?? options.outputPath,
      output: undefined,
    })
    const available = result.rows.filter(row => row.image.imageId !== null).length
    const preserved = result.rows.filter(row => row.verification.status !== 'unverified').length
    output.log(
      `Refreshed ${result.rows.length} runtime rows: ${available} local images, ` +
      `${preserved} preserved verified results.`,
    )
    return 0
  } catch (error) {
    output.error(`runtime functional matrix error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimeFunctionalMatrix(process.argv.slice(2))
}
