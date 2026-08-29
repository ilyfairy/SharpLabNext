/** Build ignored, development-only deployment inputs for the verified runtime matrix. */
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import {
  formalRuntimeCandidateProfileIds,
  readRuntimeMatrix,
} from './runtime-candidate-environment.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const defaultResults = path.join(root, '.tmp', 'runtime-matrix-functional-results.json')
const defaultMatrix = path.join(root, 'profiles', 'runtime-matrix.json')
const defaultCatalog = path.join(root, 'profiles', 'catalog', 'catalog.json')
const defaultLock = path.join(root, 'profiles', 'lock.json')
const defaultProfiles = path.join(root, 'profiles', 'runtimes', 'candidates')
const defaultOutput = path.join(root, '.tmp', 'runtime-matrix-deployment-bridge')
const digestPattern = /^sha256:[0-9a-f]{64}$/
const idPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const maxBytes = 16 * 1024 * 1024
const transientRenameErrorCodes = new Set(['EACCES', 'EBUSY', 'EPERM'])
const renameRetryDelays = Object.freeze([25, 50, 100, 200, 400, 800])
const renameRetryWaitBuffer = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT))
const outputNames = Object.freeze({
  catalog: 'catalog.json',
  lock: 'lock.json',
  supervisor: 'runtime-supervisor-overlay.json',
  compose: 'compose.override.yaml',
  environment: 'compose.env',
})

export const runtimeMatrixDeploymentBridgeUsage = `Usage:
  node eng/runtime-matrix-deployment-bridge.mjs
    [--release-id runtime-matrix-current] [--results PATH] [--matrix PATH]
    [--catalog PATH] [--lock PATH] [--profiles PATH] [--output DIRECTORY]

Use the generated compose.env with Docker Compose so every service uses the
same development release ID.`

export class RuntimeMatrixDeploymentBridgeError extends Error {
  constructor(message, options) { super(message, options); this.name = 'RuntimeMatrixDeploymentBridgeError' }
}

function fail(message, options) { throw new RuntimeMatrixDeploymentBridgeError(message, options) }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }
function required(value, label) { if (typeof value !== 'string' || value.length === 0) fail(`${label} must be a non-empty string.`); return value }
function safeId(value, label) { const result = required(value, label); if (!idPattern.test(result)) fail(`${label} must be a safe ID.`); return result }
function digest(value, label) { if (!digestPattern.test(value ?? '')) fail(`${label} must be a sha256 identity.`); return value }
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`)
const sha256 = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const yamlString = value => JSON.stringify(String(value).replaceAll('\\', '/'))

function readRegular(filename, label) {
  const resolved = path.resolve(filename)
  let stat
  try { stat = fs.lstatSync(resolved) } catch (error) { fail(`${label} '${resolved}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maxBytes) fail(`${label} '${resolved}' must be a bounded regular non-link file.`)
  return fs.readFileSync(resolved)
}

function readJson(filename, label) {
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(readRegular(filename, label))) } catch (error) {
    if (error instanceof RuntimeMatrixDeploymentBridgeError) throw error
    fail(`${label} '${path.resolve(filename)}' is invalid JSON: ${error.message}`, { cause: error })
  }
}

function pathContains(parent, child) {
  const relative = path.relative(parent, child)
  return relative === '' ||
    (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`))
}

function requireSeparatedOutput(outputDirectory, inputs) {
  const output = path.resolve(outputDirectory)
  if (path.parse(output).root === output) fail('Output directory cannot be a filesystem root.')
  for (const [label, value] of inputs) {
    const input = path.resolve(value)
    if (pathContains(output, input) || pathContains(input, output)) {
      fail(`Output directory must not overlap ${label} '${input}'.`)
    }
  }
  return output
}

export function renameSyncWithRetry(source, destination, options = {}) {
  const renameSync = options.renameSync ?? fs.renameSync
  const wait = options.wait ?? (milliseconds => Atomics.wait(renameRetryWaitBuffer, 0, 0, milliseconds))
  for (let attempt = 0; ; attempt += 1) {
    try {
      renameSync(source, destination)
      return
    } catch (error) {
      if (!transientRenameErrorCodes.has(error?.code) || attempt >= renameRetryDelays.length) throw error
      wait(renameRetryDelays[attempt])
    }
  }
}

function commitOutputDirectory(outputDirectory, files) {
  const parent = path.dirname(outputDirectory)
  const name = path.basename(outputDirectory)
  const nonce = `${process.pid}.${crypto.randomBytes(8).toString('hex')}`
  const staging = path.join(parent, `.${name}.${nonce}.staging`)
  const backup = path.join(parent, `.${name}.${nonce}.backup`)
  fs.mkdirSync(parent, { recursive: true })
  fs.mkdirSync(staging)
  let previousMoved = false
  let committed = false
  try {
    for (const [filename, bytes] of Object.entries(files)) {
      fs.writeFileSync(path.join(staging, filename), bytes, { flag: 'wx' })
    }
    if (fs.existsSync(outputDirectory)) {
      const stat = fs.lstatSync(outputDirectory)
      if (!stat.isDirectory() || stat.isSymbolicLink()) {
        fail(`Output '${outputDirectory}' must be a non-link directory when it already exists.`)
      }
      renameSyncWithRetry(outputDirectory, backup)
      previousMoved = true
    }
    renameSyncWithRetry(staging, outputDirectory)
    committed = true
  } catch (error) {
    if (previousMoved && !fs.existsSync(outputDirectory) && fs.existsSync(backup)) {
      try { renameSyncWithRetry(backup, outputDirectory) } catch (rollbackError) {
        fail(`Output transaction failed and rollback also failed: ${rollbackError.message}`, { cause: error })
      }
    }
    throw error
  } finally {
    fs.rmSync(staging, { recursive: true, force: true })
    if (committed) fs.rmSync(backup, { recursive: true, force: true })
  }
}

function canonicalMatrixBindings(matrix) {
  const bindings = new Map()
  const add = (profileId, row, candidateTarget, declaredCapabilities) => {
    if (bindings.has(profileId)) fail(`Runtime matrix produces duplicate profile '${profileId}'.`)
    if (!Array.isArray(declaredCapabilities) || declaredCapabilities.length === 0 ||
        new Set(declaredCapabilities).size !== declaredCapabilities.length ||
        declaredCapabilities.some(capability => typeof capability !== 'string' || capability.length === 0)) {
      fail(`Runtime matrix capabilities for '${profileId}' must be a non-empty unique string array.`)
    }
    bindings.set(profileId, {
      matrixTargetId: required(row?.id, `Runtime matrix target for '${profileId}'`),
      runtimeVersion: required(row?.version ?? row?.resolvedVersion, `Runtime matrix version for '${profileId}'`),
      referenceSetId: required(row?.referenceSetId, `Runtime matrix reference set for '${profileId}'`),
      candidateTarget,
      declaredCapabilities: [...declaredCapabilities],
    })
  }
  for (const row of matrix.coreClr ?? []) {
    add(`${row.id}-linux-x64`, row, 'runtime-dotnet-matrix-candidate', row.linuxCapability?.capabilities)
    const major = Number.parseInt(row.channel, 10)
    if (Number.isSafeInteger(major) && major >= 5) {
      add(`wine-${row.id}-linux-x64`, row, 'runtime-wine-dotnet-matrix-candidate', row.wineCapability?.capabilities)
    }
  }
  add(matrix.mono?.id, matrix.mono, 'runtime-mono-matrix-candidate', matrix.mono?.capability?.capabilities)
  for (const row of matrix.framework?.targets ?? []) {
    add(`wine-${row.id}-linux-x64`, row, 'runtime-wine-framework-matrix-shared-candidate', row.capability?.capabilities)
  }
  return bindings
}

function requireCanonicalRows(results, matrix, matrixSha256) {
  if (results?.schemaVersion !== 1 || !Array.isArray(results.rows)) fail('Functional results must use schema version 1 with rows.')
  if (results.runtimeMatrixSha256 !== matrixSha256) fail('Functional results do not bind the current runtime matrix bytes.')
  const expected = formalRuntimeCandidateProfileIds(matrix)
  const matrixBindings = canonicalMatrixBindings(matrix)
  const actual = results.rows.map(row => safeId(row?.profileId, 'Functional result profile ID'))
  if (new Set(actual).size !== actual.length || JSON.stringify([...actual].sort()) !== JSON.stringify([...expected].sort())) {
    fail(`Functional results must contain exactly the ${expected.length} canonical runtime profiles.`)
  }
  const rows = new Map(results.rows.map(row => [row.profileId, row]))
  for (const profileId of expected) {
    const row = rows.get(profileId)
    const binding = matrixBindings.get(profileId)
    if (binding === undefined || row.matrixTargetId !== binding.matrixTargetId ||
        row.runtimeVersion !== binding.runtimeVersion || row.referenceSetId !== binding.referenceSetId ||
        row.candidateTarget !== binding.candidateTarget) {
      fail(`Functional result '${profileId}' does not match its current runtime-matrix binding.`)
    }
  }
  return rows
}

function bindProfile(row, profileDirectory, matrixBinding) {
  const profileId = row.profileId
  if (row.verification?.status !== 'smoke-passed') fail(`Runtime '${profileId}' has not passed exact-version smoke.`)
  const imageId = digest(row.image?.imageId, `Runtime '${profileId}' image identity`)
  const profileSha256 = digest(row.profileSha256, `Runtime '${profileId}' profile identity`)
  const bytes = readRegular(path.join(path.resolve(profileDirectory), `${profileId}.json`), `Runtime profile '${profileId}'`)
  let profile
  try { profile = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) } catch (error) { fail(`Runtime profile '${profileId}' is invalid JSON: ${error.message}`, { cause: error }) }
  const runImplementationId = profile?.operations?.run?.implementationId ?? null
  const jitImplementationId = profile?.operations?.jit?.implementationId ?? null
  const sourceMappingKind = profile?.operations?.jit?.sourceMappingKind ?? 'none'
  if (sha256(bytes) !== profileSha256 || profile?.schemaVersion !== 1 || profile.id !== profileId ||
      profile.image !== row.candidateImage || row.image?.reference !== row.candidateImage ||
      row.image?.inspectionError !== null || row.image?.operatingSystem !== 'linux' ||
      row.image?.architecture !== 'amd64' || row.image?.labels?.['com.sharplabnext.runtime-profile'] !== profileId ||
      profile.family !== row.family || profile.runtimeVersion !== row.runtimeVersion ||
      JSON.stringify(profile.capabilities) !== JSON.stringify(row.expected?.capabilities) ||
      runImplementationId !== (row.expected?.runImplementationId ?? null) ||
      jitImplementationId !== (row.expected?.jitImplementationId ?? null) ||
      sourceMappingKind !== row.expected?.sourceMappingKind ||
      !Array.isArray(profile.allowedSecurityPolicyIds) || profile.allowedSecurityPolicyIds.length === 0 ||
      !Array.isArray(profile.securityPolicies) || profile.securityPolicies.length === 0) {
    fail(`Runtime profile '${profileId}' does not match its verified functional row.`)
  }
  const declaredCapabilities = matrixBinding?.declaredCapabilities
  const candidateCapabilities = new Set(profile.capabilities)
  if (!Array.isArray(declaredCapabilities) ||
      profile.capabilities.some(capability => !declaredCapabilities.includes(capability)) ||
      declaredCapabilities.some(capability =>
        !candidateCapabilities.has(capability) && capability !== 'inspection' && capability !== 'execution-flow')) {
    fail(`Runtime profile '${profileId}' cannot be expanded to its declared deployment capabilities.`)
  }
  profile.capabilities = [...declaredCapabilities]
  const policyIds = new Set(profile.securityPolicies.map(policy => safeId(policy?.id, `Runtime '${profileId}' security policy ID`)))
  if (policyIds.size !== profile.securityPolicies.length ||
      profile.allowedSecurityPolicyIds.some(policyId => !policyIds.has(policyId))) {
    fail(`Runtime profile '${profileId}' does not close its security-policy allow-list.`)
  }
  const smoke = row.verification.smoke
  const requiredSmoke = ['runtimeIdentity', 'compile', 'ilDecompile']
  if (profile.capabilities.includes('run')) requiredSmoke.push('run')
  if (profile.capabilities.includes('jit-asm')) requiredSmoke.push('jit')
  if (sourceMappingKind !== 'none') requiredSmoke.push('mapping')
  if (!object(smoke) || requiredSmoke.some(name => smoke[name] !== 'passed')) {
    fail(`Runtime '${profileId}' has incomplete exact-version smoke evidence.`)
  }
  const evidence = row.verification.evidence
  if (!object(evidence) || !object(evidence.artifactPipeline)) {
    fail(`Runtime '${profileId}' has no current artifact-pipeline evidence.`)
  }
  let runtimeEvidence = false
  for (const [name, value] of Object.entries(evidence)) {
    if (!object(value) || value.profileSha256 !== profileSha256 || value.imageId !== imageId) {
      fail(`Runtime '${profileId}' has stale '${name}' evidence.`)
    }
    if (name !== 'artifactPipeline') runtimeEvidence = true
  }
  if (!runtimeEvidence || evidence.artifactPipeline.referenceSetId !== row.referenceSetId ||
      evidence.artifactPipeline.compilePassed !== true || evidence.artifactPipeline.ilPassed !== true ||
      evidence.artifactPipeline.decompiledCSharpPassed !== true) {
    fail(`Runtime '${profileId}' has incomplete current functional evidence.`)
  }
  return { row, profile, profileSha256, imageId }
}

function patchCatalog(catalog, bindings, releaseId) {
  if (catalog?.schemaVersion !== 1 || !Array.isArray(catalog.runtimes)) fail('Catalog must use schema version 1 with runtimes.')
  for (const binding of bindings) {
    const matches = catalog.runtimes.filter(runtime => runtime?.id === binding.profile.id)
    if (matches.length !== 1) fail(`Catalog must contain exactly one runtime '${binding.profile.id}'.`)
    const runtime = matches[0]
    if (!object(runtime) || runtime.availability?.installed !== true || runtime.availability?.health !== 'healthy') {
      fail(`Catalog runtime '${binding.profile.id}' must exist and be selectable.`)
    }
    const profile = binding.profile
    Object.assign(runtime, {
      family: profile.family,
      resolvedVersion: profile.runtimeVersion,
      rid: profile.rid,
      architecture: profile.architecture,
      acceptedArtifactFormats: structuredClone(profile.acceptedArtifactFormats),
      capabilities: structuredClone(profile.capabilities),
      runtimeCommit: profile.runtimeCommit,
      jitVersion: profile.jitVersion,
      jitCommit: profile.jitCommit,
      runtimeImageId: binding.imageId,
      acceptedRuntimeFamilies: structuredClone(profile.acceptedRuntimeFamilies),
      acceptedFrameworks: structuredClone(profile.acceptedFrameworks),
      containerIsolationKind: profile.container?.isolationKind,
      containerEnvironmentKind: profile.container?.environmentKind,
      providedRuntimeFeatureTags: structuredClone(profile.providedRuntimeFeatureTags),
      providedMetadataFeatureTags: structuredClone(profile.providedMetadataFeatureTags),
    })
    const mapping = profile.operations?.jit?.sourceMappingKind
    if (mapping === undefined) delete runtime.jitSourceMappingKind
    else runtime.jitSourceMappingKind = mapping
  }
  catalog.releaseId = releaseId
  catalog.revision = `runtime-matrix-functional-${sha256(jsonBytes(bindings.map(binding => ({
    profileId: binding.profile.id,
    profileSha256: binding.profileSha256,
    imageId: binding.imageId,
  })))).slice(7, 19)}`
  return catalog
}

function patchReleaseLock(releaseLock, bindings, releaseId) {
  if (releaseLock?.schemaVersion !== 1 || !object(releaseLock.components)) fail('Release lock must use schema version 1 with components.')
  for (const binding of bindings) {
    const component = releaseLock.components[binding.profile.id]
    if (!object(component) || component.kind !== 'runtime' || component.resolvedVersion !== binding.profile.runtimeVersion) {
      fail(`Release lock has no matching runtime component '${binding.profile.id}'.`)
    }
    component.imageId = binding.imageId
  }
  releaseLock.releaseId = releaseId
  return releaseLock
}

function createSupervisorOverlay(bindings) {
  const policies = new Map()
  const profiles = []
  for (const binding of bindings) {
    for (const policy of binding.profile.securityPolicies) {
      const existing = policies.get(policy.id)
      if (existing !== undefined && JSON.stringify(existing) !== JSON.stringify(policy)) {
        fail(`Security policy '${policy.id}' differs between runtime profiles.`)
      }
      policies.set(policy.id, structuredClone(policy))
    }
    const profile = structuredClone(binding.profile)
    profile.image = binding.imageId
    profile.runtimeImageId = binding.imageId
    delete profile.securityPolicies
    profiles.push(profile)
  }
  return {
    RuntimeSupervisor: { SessionReuseEnabled: false, RequireDigestPinnedImages: true },
    RuntimeSupervisorProfileOverlay: {
      Enabled: true,
      Profiles: profiles,
      SecurityPolicies: [...policies.values()].sort((left, right) => left.id.localeCompare(right.id)),
    },
  }
}

function composeOverride(outputDirectory) {
  const mount = path.resolve(outputDirectory)
  const supervisor = path.join(mount, 'runtime-supervisor-overlay.json')
  return `# Generated development-only runtime-matrix bridge. Do not deploy as a release.\n# Apply with --env-file ${yamlString(path.join(mount, outputNames.environment))}.\nservices:\n  gateway:\n    environment:\n      Catalog__Path: /app/runtime-matrix/catalog.json\n      Catalog__LockPath: /app/runtime-matrix/lock.json\n      DependencyHealth__Enabled: "false"\n    volumes:\n      - type: bind\n        source: ${yamlString(mount)}\n        target: /app/runtime-matrix\n        read_only: true\n  runtime-supervisor:\n    environment:\n      ASPNETCORE_ENVIRONMENT: RuntimeMatrix\n      RuntimeSupervisor__SessionReuseEnabled: "false"\n    volumes:\n      - type: bind\n        source: ${yamlString(supervisor)}\n        target: /app/appsettings.RuntimeMatrix.json\n        read_only: true\n`
}

const composeEnvironment = releaseId => Buffer.from(`SHARPLABNEXT_RELEASE_ID=${releaseId}\n`)

export function buildRuntimeMatrixDeploymentBridge(options = {}) {
  const releaseId = safeId(options.releaseId ?? 'runtime-matrix-current', 'Development release ID')
  const matrixPath = options.matrixPath ?? defaultMatrix
  const resultsPath = options.resultsPath ?? defaultResults
  const catalogPath = options.catalogPath ?? defaultCatalog
  const lockPath = options.lockPath ?? defaultLock
  const matrixBytes = readRegular(matrixPath, 'Runtime matrix')
  const matrix = readRuntimeMatrix(matrixPath)
  const results = readJson(resultsPath, 'Functional results')
  const rows = requireCanonicalRows(results, matrix, sha256(matrixBytes))
  const matrixBindings = canonicalMatrixBindings(matrix)
  const profileDirectory = options.profileDirectory ?? defaultProfiles
  const bindings = formalRuntimeCandidateProfileIds(matrix).map(profileId =>
    bindProfile(rows.get(profileId), profileDirectory, matrixBindings.get(profileId)))
  const catalog = patchCatalog(readJson(catalogPath, 'Catalog'), bindings, releaseId)
  const releaseLock = patchReleaseLock(readJson(lockPath, 'Release lock'), bindings, releaseId)
  const overlay = createSupervisorOverlay(bindings)
  const outputDirectory = requireSeparatedOutput(options.outputDirectory ?? defaultOutput, [
    ['functional results', resultsPath],
    ['runtime matrix', matrixPath],
    ['Catalog', catalogPath],
    ['release lock', lockPath],
    ['runtime profile directory', profileDirectory],
  ])
  const outputs = {
    catalog: jsonBytes(catalog),
    lock: jsonBytes(releaseLock),
    supervisor: jsonBytes(overlay),
    compose: Buffer.from(composeOverride(outputDirectory)),
    environment: composeEnvironment(releaseId),
  }
  const manifest = {
    schemaVersion: 1,
    developmentOnly: true,
    releaseId,
    profileCount: bindings.length,
    profiles: bindings.map(binding => ({
      id: binding.profile.id,
      profileSha256: binding.profileSha256,
      runtimeImageId: binding.imageId,
    })),
    files: Object.fromEntries(Object.entries(outputs).map(([name, bytes]) => [outputNames[name], sha256(bytes)])),
  }
  const files = Object.fromEntries(Object.entries(outputs).map(([name, bytes]) => [outputNames[name], bytes]))
  files['manifest.json'] = jsonBytes(manifest)
  commitOutputDirectory(outputDirectory, files)
  return { outputDirectory, catalog, releaseLock, overlay, manifest }
}

function parseArguments(argv) {
  if (argv.length === 1 && (argv[0] === '--help' || argv[0] === '-h')) return { help: true }
  const result = {}
  const names = new Map([
    ['--release-id', 'releaseId'], ['--results', 'resultsPath'], ['--matrix', 'matrixPath'],
    ['--catalog', 'catalogPath'], ['--lock', 'lockPath'], ['--profiles', 'profileDirectory'],
    ['--output', 'outputDirectory'],
  ])
  for (let index = 0; index < argv.length; index++) {
    const option = argv[index]
    const field = names.get(option)
    const value = argv[++index]
    if (field === undefined || value === undefined || value.length === 0 || result[field] !== undefined) fail(`Invalid or duplicate option '${option}'.`)
    result[field] = value
  }
  return result
}

export function runRuntimeMatrixDeploymentBridgeCli(argv, options = {}) {
  const output = options.output ?? console
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) { output.log(runtimeMatrixDeploymentBridgeUsage); return 0 }
    const result = buildRuntimeMatrixDeploymentBridge({ ...options, ...parsed })
    output.log(`Prepared ${result.manifest.profileCount}-profile development runtime-matrix bridge at ${result.outputDirectory}.`)
    return 0
  } catch (error) {
    output.error(`runtime matrix deployment bridge error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimeMatrixDeploymentBridgeCli(process.argv.slice(2))
}
