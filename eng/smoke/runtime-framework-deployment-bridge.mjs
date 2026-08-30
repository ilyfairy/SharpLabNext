/** Build ignored, development-only Catalog/Compose inputs from verified Framework evidence. */
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { prepareRuntimeFrameworkSupervisorSmoke } from './runtime-framework-supervisor-smoke.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const defaultResults = path.join(root, '.tmp', 'runtime-matrix-functional-results.json')
const defaultCatalog = path.join(root, 'profiles', 'catalog', 'catalog.json')
const defaultLock = path.join(root, 'profiles', 'lock.json')
const defaultProfiles = path.join(root, 'profiles', 'runtimes', 'candidates')
const defaultOutput = path.join(root, '.tmp', 'runtime-framework-deployment-bridge')
const digestPattern = /^sha256:[0-9a-f]{64}$/
const idPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const maxBytes = 16 * 1024 * 1024

export const runtimeFrameworkDeploymentBridgeUsage = `Usage:
  node eng/smoke/runtime-framework-deployment-bridge.mjs --profile wine-netfx48-linux-x64
    [--release-id runtime-matrix-current] [--results PATH] [--catalog PATH]
    [--lock PATH] [--profiles PATH] [--output DIRECTORY]`

export class RuntimeFrameworkDeploymentBridgeError extends Error {
  constructor(message, options) { super(message, options); this.name = 'RuntimeFrameworkDeploymentBridgeError' }
}

function fail(message, options) { throw new RuntimeFrameworkDeploymentBridgeError(message, options); }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }
function required(value, label) { if (typeof value !== 'string' || value.length === 0) fail(`${label} must be a non-empty string.`); return value }
function safeId(value, label) { const result = required(value, label); if (!idPattern.test(result)) fail(`${label} must be a safe ID.`); return result }
function digest(value, label) { if (!digestPattern.test(value ?? '')) fail(`${label} must be a sha256 identity.`); return value }

function readRegular(filename, label) {
  const resolved = path.resolve(filename)
  let stat
  try { stat = fs.lstatSync(resolved) } catch (error) { fail(`${label} '${resolved}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maxBytes) fail(`${label} '${resolved}' must be a bounded regular non-link file.`)
  return fs.readFileSync(resolved)
}

function readJson(filename, label) {
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(readRegular(filename, label))) } catch (error) {
    if (error instanceof RuntimeFrameworkDeploymentBridgeError) throw error
    fail(`${label} '${path.resolve(filename)}' is invalid JSON: ${error.message}`, { cause: error })
  }
}

function writeAtomic(filename, bytes) {
  const resolved = path.resolve(filename)
  fs.mkdirSync(path.dirname(resolved), { recursive: true })
  const temporary = path.join(path.dirname(resolved), `.${path.basename(resolved)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`)
  try { fs.writeFileSync(temporary, bytes, { flag: 'wx' }); fs.renameSync(temporary, resolved) } finally { fs.rmSync(temporary, { force: true }) }
}

const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`)
const sha256 = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const yamlString = value => JSON.stringify(String(value).replaceAll('\\', '/'))

function patchCatalog(catalog, binding, releaseId) {
  if (catalog?.schemaVersion !== 1 || !Array.isArray(catalog.runtimes)) fail('Catalog must use schema version 1 with runtimes.')
  const matches = catalog.runtimes.filter(runtime => runtime?.id === binding.profile.id)
  if (matches.length !== 1) fail(`Catalog must contain exactly one runtime '${binding.profile.id}'.`)
  const runtime = matches[0]
  if (runtime.availability?.installed !== true || runtime.availability?.health !== 'healthy') fail(`Catalog runtime '${runtime.id}' must already be selectable.`)
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
    containerIsolationKind: profile.container.isolationKind,
    containerEnvironmentKind: profile.container.environmentKind,
    providedRuntimeFeatureTags: structuredClone(profile.providedRuntimeFeatureTags),
    providedMetadataFeatureTags: structuredClone(profile.providedMetadataFeatureTags),
  })
  const mapping = profile.operations?.jit?.sourceMappingKind
  if (mapping === undefined) delete runtime.jitSourceMappingKind
  else runtime.jitSourceMappingKind = mapping
  catalog.releaseId = releaseId
  catalog.revision = `runtime-framework-functional-${sha256(jsonBytes({ profileId: profile.id, imageId: binding.imageId, profileSha256: binding.row.profileSha256 })).slice(7, 19)}`
  return catalog
}

function patchReleaseLock(releaseLock, binding, releaseId) {
  if (releaseLock?.schemaVersion !== 1 || !object(releaseLock.components)) fail('Release lock must use schema version 1 with components.')
  const component = releaseLock.components[binding.profile.id]
  if (!object(component) || component.kind !== 'runtime' || component.resolvedVersion !== binding.profile.runtimeVersion) fail(`Release lock has no matching runtime component '${binding.profile.id}'.`)
  component.imageId = binding.imageId
  releaseLock.releaseId = releaseId
  return releaseLock
}

function requireSupervisorEvidence(binding) {
  const evidence = binding.row.verification?.evidence?.supervisorOneShot
  if (!object(evidence) || evidence.profileSha256 !== binding.row.profileSha256 || evidence.imageId !== binding.imageId ||
      evidence.identity?.RuntimeImageId !== binding.imageId || evidence.identity?.RuntimeVersion !== binding.profile.runtimeVersion ||
      evidence.identity?.RuntimeCommit !== binding.profile.runtimeCommit || evidence.identity?.Rid !== binding.profile.rid ||
      evidence.identity?.Architecture !== binding.profile.architecture || evidence.stdoutMarker !== 'SLN-FRAMEWORK-SUPERVISOR-V1') {
    fail(`Runtime '${binding.profile.id}' has no current real Supervisor one-shot evidence.`)
  }
}

function composeOverride(outputDirectory) {
  const catalogMount = path.resolve(outputDirectory)
  const supervisorMount = path.join(catalogMount, 'runtime-supervisor-overlay.json')
  return `# Generated development-only Framework validation bridge. Do not deploy as a release.\nservices:\n  gateway:\n    environment:\n      Catalog__Path: /app/runtime-framework/catalog.json\n      Catalog__LockPath: /app/runtime-framework/lock.json\n      DependencyHealth__Enabled: \"false\"\n    volumes:\n      - type: bind\n        source: ${yamlString(catalogMount)}\n        target: /app/runtime-framework\n        read_only: true\n  runtime-supervisor:\n    environment:\n      ASPNETCORE_ENVIRONMENT: RuntimeFramework\n      RuntimeSupervisor__SessionReuseEnabled: \"false\"\n    volumes:\n      - type: bind\n        source: ${yamlString(supervisorMount)}\n        target: /app/appsettings.RuntimeFramework.json\n        read_only: true\n`
}

export function buildRuntimeFrameworkDeploymentBridge(options = {}) {
  const profileId = safeId(options.profileId, 'Framework profile ID')
  const releaseId = safeId(options.releaseId ?? 'runtime-matrix-current', 'Development release ID')
  const resultsPath = options.resultsPath ?? defaultResults
  const profiles = options.profileDirectory ?? defaultProfiles
  const prepared = prepareRuntimeFrameworkSupervisorSmoke({ profileId, resultsPath, profileDirectory: profiles, overlayPath: false, prepareOnly: true })
  requireSupervisorEvidence(prepared.binding)
  const catalog = patchCatalog(readJson(options.catalogPath ?? defaultCatalog, 'Catalog'), prepared.binding, releaseId)
  const releaseLock = patchReleaseLock(readJson(options.lockPath ?? defaultLock, 'Release lock'), prepared.binding, releaseId)
  const outputDirectory = path.resolve(options.outputDirectory ?? defaultOutput)
  const outputs = {
    catalog: jsonBytes(catalog),
    lock: jsonBytes(releaseLock),
    supervisor: jsonBytes(prepared.overlay),
    compose: Buffer.from(composeOverride(outputDirectory)),
  }
  writeAtomic(path.join(outputDirectory, 'catalog.json'), outputs.catalog)
  writeAtomic(path.join(outputDirectory, 'lock.json'), outputs.lock)
  writeAtomic(path.join(outputDirectory, 'runtime-supervisor-overlay.json'), outputs.supervisor)
  writeAtomic(path.join(outputDirectory, 'compose.override.yaml'), outputs.compose)
  const manifest = {
    schemaVersion: 1,
    developmentOnly: true,
    releaseId,
    profileId,
    profileSha256: prepared.binding.row.profileSha256,
    runtimeImageId: prepared.binding.imageId,
    files: Object.fromEntries(Object.entries(outputs).map(([name, bytes]) => [name, sha256(bytes)])),
  }
  writeAtomic(path.join(outputDirectory, 'manifest.json'), jsonBytes(manifest))
  return { outputDirectory, catalog, releaseLock, overlay: prepared.overlay, manifest }
}

function parseArguments(argv) {
  if (argv.length === 1 && (argv[0] === '--help' || argv[0] === '-h')) return { help: true }
  const result = {}; const names = new Map([['--profile', 'profileId'], ['--release-id', 'releaseId'], ['--results', 'resultsPath'], ['--catalog', 'catalogPath'], ['--lock', 'lockPath'], ['--profiles', 'profileDirectory'], ['--output', 'outputDirectory']])
  for (let index = 0; index < argv.length; index++) { const option = argv[index]; const field = names.get(option); const value = argv[++index]; if (field === undefined || value === undefined || value.length === 0 || result[field] !== undefined) fail(`Invalid or duplicate option '${option}'.`); result[field] = value }
  if (result.profileId === undefined) fail('Missing required --profile.')
  return result
}

export function runRuntimeFrameworkDeploymentBridgeCli(argv, options = {}) {
  const output = options.output ?? console
  try { const parsed = parseArguments(argv); if (parsed.help) { output.log(runtimeFrameworkDeploymentBridgeUsage); return 0 }; const result = buildRuntimeFrameworkDeploymentBridge({ ...options, ...parsed }); output.log(`Prepared development Framework deployment bridge at ${result.outputDirectory}.`); return 0 } catch (error) { output.error(`runtime Framework deployment bridge error: ${error.message}`); return 1 }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = runRuntimeFrameworkDeploymentBridgeCli(process.argv.slice(2))
