/**
 * Exercise the current-source Artifact Store, Roslyn Stable, and default
 * artifact worker without going through Gateway or a runtime container.
 */

import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const defaultResultsPath = path.join(repositoryRoot, '.tmp', 'runtime-matrix-functional-results.json')
const defaultProfileDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')
const defaultRuntimeMatrixPath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
const digestPattern = /^sha256:[0-9a-f]{64}$/
const profileIdPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const maximumResultBytes = 16 * 1024 * 1024
const maximumJsonResponseBytes = 4 * 1024 * 1024
const maximumContentResponseBytes = 16 * 1024 * 1024
const defaultRequestTimeoutMilliseconds = 15_000
const defaultPollDelayMilliseconds = 100
// Artifacts.Default permits a processor 15 seconds; retain an explicit margin.
const minimumPollWindowMilliseconds = 20_000
const minimumTokenLength = 32
const maximumTokenLength = 8192
const coreClrProfileFamilies = new Set(['coreclr', 'coreclr-wine'])

const serviceKinds = Object.freeze({
  artifactStore: 1,
  toolchainWorker: 3,
  artifactWorker: 4,
})

export const runtimeArtifactSmokeUsage = `Usage:
  node eng/runtime-artifact-smoke.mjs --profile ID [--profile ID ...]
    --artifact-store URL --roslyn-worker URL --artifact-worker URL
    [--token-file PATH] [--results PATH]`

export class RuntimeArtifactSmokeError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimeArtifactSmokeError'
  }
}

function fail(message, options) {
  throw new RuntimeArtifactSmokeError(message, options)
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function requiredString(value, label) {
  if (typeof value !== 'string' || value.length === 0) fail(`${label} must be a non-empty string.`)
  return value
}

function requireDigest(value, label) {
  if (!digestPattern.test(value ?? '')) fail(`${label} must be a sha256 content identity.`)
  return value
}

function requireProtocol(value, label) {
  const protocol = requirePascalCaseObject(value, label)
  if (protocol.Major !== 1 || protocol.Minor !== 0) fail(`${label} must be protocol 1.0.`)
  return protocol
}

function requirePascalCaseObject(value, label) {
  if (!isObject(value)) fail(`${label} must be a JSON object.`)
  for (const key of Object.keys(value)) {
    if (!/^[A-Z]/.test(key)) fail(`${label} contains non-PascalCase property '${key}'.`)
  }
  return value
}

function readBoundedFile(filename, label) {
  let metadata
  try {
    metadata = fs.lstatSync(filename)
  } catch (error) {
    fail(`${label} '${filename}' could not be inspected: ${error.message}`, { cause: error })
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() ||
      metadata.size < 1 || metadata.size > maximumResultBytes) {
    fail(`${label} '${filename}' must be a bounded regular non-link file.`)
  }
  try {
    return fs.readFileSync(filename)
  } catch (error) {
    fail(`${label} '${filename}' could not be read: ${error.message}`, { cause: error })
  }
}

function readBoundedJson(filename, label) {
  const bytes = readBoundedFile(filename, label)
  try {
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes))
  } catch (error) {
    fail(`${label} '${filename}' is invalid JSON: ${error.message}`, { cause: error })
  }
}

function validateInternalToken(token, label) {
  if (typeof token !== 'string' || token.length < minimumTokenLength || token.length > maximumTokenLength) {
    fail(`${label} must contain between ${minimumTokenLength} and ${maximumTokenLength} characters.`)
  }
  if ([...token].some(character => character <= ' ' || character >= '\u007f')) {
    fail(`${label} must contain visible ASCII characters only.`)
  }
  return token
}

function readInternalTokenFile(filename) {
  const resolved = path.resolve(filename)
  let metadata
  try {
    metadata = fs.lstatSync(resolved)
  } catch (error) {
    fail(`Internal token file '${resolved}' could not be inspected: ${error.message}`, { cause: error })
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() ||
      metadata.size < minimumTokenLength || metadata.size > maximumTokenLength + 2) {
    fail(`Internal token file '${resolved}' must be a bounded regular non-link file.`)
  }
  return validateInternalToken(fs.readFileSync(resolved, 'utf8').replace(/[\r\n]+$/, ''), 'Internal token')
}

function sha256(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}

function readProfileBinding(profileDirectory, row) {
  const filename = path.join(profileDirectory, `${row.profileId}.json`)
  let metadata
  try {
    metadata = fs.lstatSync(filename)
  } catch (error) {
    fail(`Runtime profile '${row.profileId}' could not be inspected: ${error.message}`, { cause: error })
  }
  if (!metadata.isFile() || metadata.isSymbolicLink() || metadata.size < 1 || metadata.size > maximumResultBytes) {
    fail(`Runtime profile '${row.profileId}' must be a bounded regular non-link file.`)
  }
  const bytes = fs.readFileSync(filename)
  let profile
  try {
    profile = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes))
  } catch (error) {
    fail(`Runtime profile '${row.profileId}' is invalid JSON: ${error.message}`, { cause: error })
  }
  if (profile?.id !== row.profileId || sha256(bytes) !== row.profileSha256) {
    fail(`Runtime profile '${row.profileId}' does not match its functional result SHA binding.`)
  }
  return profile
}

function compareVersions(left, right) {
  const parse = value => {
    const match = /^(\d+)(?:\.(\d+))?(?:\.(\d+))?/.exec(value ?? '')
    if (match === null) fail(`Version '${value}' is invalid.`)
    return [Number(match[1]), Number(match[2] ?? 0), Number(match[3] ?? 0)]
  }
  const a = parse(left)
  const b = parse(right)
  for (let index = 0; index < a.length; index++) {
    if (a[index] !== b[index]) return a[index] < b[index] ? -1 : 1
  }
  return 0
}

function targetFrameworkForCoreClr(matrixTargetId, referenceSetId) {
  const legacy = /^dotnet-core-(\d+\.\d+)$/.exec(matrixTargetId)
  const modern = /^dotnet-(\d+)(-preview)?$/.exec(matrixTargetId)
  const targetFramework = legacy === null
    ? modern === null ? null : `net${modern[1]}.0`
    : `netcoreapp${legacy[1]}`
  const expectedReferenceSetId = legacy === null
    ? modern === null ? null : `net${modern[1]}${modern[2] ?? ''}-ref`
    : `${targetFramework}-ref`
  if (targetFramework === null || referenceSetId !== expectedReferenceSetId) {
    fail(`Runtime matrix target '${matrixTargetId}' has an unsupported CoreCLR reference-set binding.`)
  }
  return targetFramework
}

function requireProfileRuntimeBinding(profile, binding, row) {
  if (!isObject(profile) || profile.id !== row.profileId || !coreClrProfileFamilies.has(profile.family) ||
      !Array.isArray(profile.acceptedRuntimeFamilies) || !profile.acceptedRuntimeFamilies.includes('coreclr') ||
      !Array.isArray(profile.acceptedArtifactFormats) || !profile.acceptedArtifactFormats.includes('dotnet-managed-pe-v1') ||
      !Array.isArray(profile.acceptedFrameworks)) {
    fail(`Runtime profile '${row.profileId}' does not accept the required CoreCLR managed artifact contract.`)
  }
  const matches = profile.acceptedFrameworks.filter(value => value?.name === binding.runtimeFramework.name)
  if (matches.length !== 1) fail(`Runtime profile '${row.profileId}' does not bind matrix runtime framework '${binding.runtimeFramework.name}'.`)
  const requirement = matches[0]
  const version = binding.runtimeFramework.minimumVersion
  if ((requirement.exactVersion !== undefined && requirement.exactVersion !== version) ||
      (requirement.minimumVersion !== undefined && compareVersions(version, requirement.minimumVersion) < 0) ||
      (requirement.maximumVersion !== undefined && compareVersions(version, requirement.maximumVersion) > 0) ||
      (requirement.exactVersion === undefined && requirement.minimumVersion === undefined)) {
    fail(`Runtime profile '${row.profileId}' does not accept matrix framework version '${version}'.`)
  }
}

function readRuntimeBinding(matrixPath, results, row, profile) {
  const matrixBytes = readBoundedFile(matrixPath, 'Runtime matrix')
  let matrix
  try {
    matrix = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(matrixBytes))
  } catch (error) {
    fail(`Runtime matrix '${matrixPath}' is invalid JSON: ${error.message}`, { cause: error })
  }
  if (!requireDigest(results.runtimeMatrixSha256, 'Functional results runtimeMatrixSha256') ||
      sha256(matrixBytes) !== results.runtimeMatrixSha256) {
    fail('Functional results does not match its runtime matrix SHA binding.')
  }
  requiredString(row.matrixTargetId, `Row '${row.profileId}' matrixTargetId`)
  if (!Array.isArray(matrix?.coreClr)) fail('Runtime matrix has no CoreCLR targets.')
  const matches = matrix.coreClr.filter(value => value?.id === row.matrixTargetId)
  if (matches.length !== 1) fail(`Runtime matrix must bind target '${row.matrixTargetId}' exactly once.`)
  const target = matches[0]
  const referenceSetId = requiredString(target.referenceSetId, `Runtime matrix target '${row.matrixTargetId}' referenceSetId`)
  const referencePackage = isObject(target.referencePackage) ? target.referencePackage : fail(`Runtime matrix target '${row.matrixTargetId}' has no reference package.`)
  const binding = {
    matrixTargetId: row.matrixTargetId,
    referenceSetId,
    targetFramework: targetFrameworkForCoreClr(row.matrixTargetId, referenceSetId),
    referencePackage: {
      id: requiredString(referencePackage.id, 'Runtime matrix reference package id'),
      version: requiredString(referencePackage.version, 'Runtime matrix reference package version'),
      packageContentHash: requiredString(referencePackage.packageContentHash, 'Runtime matrix reference package content hash'),
    },
    // The NuGet reference package proves the compilation inputs. The emitted
    // artifact binds the corresponding shared runtime framework, whose name is
    // deliberately different for Microsoft.NETCore.App.Ref packages.
    runtimeFramework: {
      name: 'Microsoft.NETCore.App',
      minimumVersion: requiredString(referencePackage.version, 'Runtime matrix runtime framework version'),
    },
  }
  if (row.referenceSetId !== binding.referenceSetId) fail(`Functional row '${row.profileId}' does not match its runtime matrix reference-set binding.`)
  requireProfileRuntimeBinding(profile, binding, row)
  return binding
}

function writeJsonAtomically(filename, value) {
  fs.mkdirSync(path.dirname(filename), { recursive: true })
  const temporary = path.join(
    path.dirname(filename),
    `.${path.basename(filename)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`,
  )
  try {
    fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' })
    fs.renameSync(temporary, filename)
  } finally {
    fs.rmSync(temporary, { force: true })
  }
}

function endpoint(value, label) {
  let url
  try {
    url = new URL(value)
  } catch {
    fail(`${label} must be an absolute HTTP URL.`)
  }
  if (!['http:', 'https:'].includes(url.protocol)) fail(`${label} must use HTTP(S).`)
  return url.toString().replace(/\/$/, '')
}

function joinUrl(base, pathname) {
  return `${base}${pathname}`
}

function newId(prefix) {
  return `${prefix}-${crypto.randomUUID().replaceAll('-', '')}`
}

function artifactDigest(value) {
  return requireDigest(value, 'ContentRef').slice('sha256:'.length)
}

async function readBoundedResponseBytes(response, label, maximumBytes) {
  const declaredLength = Number(response.headers.get('Content-Length'))
  if (Number.isFinite(declaredLength) && declaredLength > maximumBytes) {
    fail(`${label} response exceeds the ${maximumBytes}-byte limit.`)
  }
  if (response.body === null) return ''
  const reader = response.body.getReader()
  const chunks = []
  let total = 0
  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      total += value.byteLength
      if (total > maximumBytes) {
        await reader.cancel()
        fail(`${label} response exceeds the ${maximumBytes}-byte limit.`)
      }
      chunks.push(Buffer.from(value))
    }
  } catch (error) {
    if (error instanceof RuntimeArtifactSmokeError) throw error
    fail(`${label} response body could not be read: ${error.message}`, { cause: error })
  }
  return Buffer.concat(chunks, total)
}

async function readBoundedResponseText(response, label, maximumBytes) {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(
      await readBoundedResponseBytes(response, label, maximumBytes),
    )
  } catch (error) {
    if (error instanceof RuntimeArtifactSmokeError) throw error
    fail(`${label} response is not strict UTF-8: ${error.message}`, { cause: error })
  }
}

async function readJson(response, label) {
  const text = await readBoundedResponseText(response, label, maximumJsonResponseBytes)
  if (!response.ok) fail(`${label} returned HTTP ${response.status}: ${text.slice(0, 500)}`)
  try {
    return JSON.parse(text)
  } catch (error) {
    fail(`${label} returned invalid JSON: ${error.message}`, { cause: error })
  }
}

function makeRequest(fetch, token, requestTimeoutMilliseconds, url, init = {}) {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body !== undefined) headers.set('Content-Type', 'application/json')
  if (token !== undefined) headers.set('Authorization', `Bearer ${token}`)
  const timeoutSignal = AbortSignal.timeout(requestTimeoutMilliseconds)
  const signal = init.signal === undefined
    ? timeoutSignal
    : AbortSignal.any([init.signal, timeoutSignal])
  return fetch(url, { ...init, headers, signal })
}

async function getWorkerDescriptor(request, baseUrl, expected) {
  const { id: expectedId, serviceKind, workerKind, label } = expected
  const descriptor = requirePascalCaseObject(await readJson(
    await request(joinUrl(baseUrl, '/api/v1/worker/describe')),
    `${label} descriptor`,
  ), `${label} descriptor`)
  const service = requirePascalCaseObject(descriptor.Service, `${label} descriptor.Service`)
  if (service.Id !== expectedId || service.Kind !== serviceKind || service.Status !== 'ready' ||
      descriptor.WorkerKind !== workerKind) {
    fail(`${label} descriptor does not identify a ready '${expectedId}' ${workerKind}.`)
  }
  const releaseId = requiredString(service.ReleaseId, `${label} descriptor.Service.ReleaseId`)
  requireProtocol(service.Protocol, `${label} descriptor.Service.Protocol`)
  requireProtocol(descriptor.NegotiatedProtocol, `${label} descriptor.NegotiatedProtocol`)
  if (!Array.isArray(descriptor.SupportedProtocolVersions) ||
      !descriptor.SupportedProtocolVersions.some(value => value?.Major === 1 && value?.Minor === 0)) {
    fail(`${label} descriptor does not support protocol 1.0.`)
  }
  const workerImageId = requireDigest(descriptor.WorkerImageId, `${label} descriptor.WorkerImageId`)
  if (!Array.isArray(descriptor.ProfileIds) || !descriptor.ProfileIds.includes(expectedId)) {
    fail(`${label} descriptor does not bind profile '${expectedId}'.`)
  }
  if (!Array.isArray(descriptor.Capabilities)) fail(`${label} descriptor has no capabilities.`)
  return {
    id: service.Id,
    releaseId,
    workerImageId,
    instanceId: requiredString(descriptor.InstanceId, `${label} descriptor.InstanceId`),
    serviceCapabilities: Array.isArray(service.Capabilities) ? service.Capabilities : [],
    capabilities: descriptor.Capabilities,
    profileIds: descriptor.ProfileIds,
    referenceSets: descriptor.ReferenceSets,
  }
}

async function getArtifactStoreIdentity(request, baseUrl) {
  const identity = requirePascalCaseObject(await readJson(
    await request(joinUrl(baseUrl, '/api/v1/artifacts/status')),
    'Artifact Store status',
  ), 'Artifact Store status')
  if (identity.Id !== 'artifact-store' || identity.Kind !== serviceKinds.artifactStore) {
    fail("Artifact Store does not identify as 'artifact-store'.")
  }
  requiredString(identity.Status, 'Artifact Store Status')
  requireProtocol(identity.Protocol, 'Artifact Store Protocol')
  return {
    id: identity.Id,
    releaseId: requiredString(identity.ReleaseId, 'Artifact Store ReleaseId'),
    protocol: identity.Protocol ?? null,
  }
}

function requireCapability(worker, capability, label) {
  const matches = worker.capabilities.filter(value => value?.Id === capability)
  if (matches.length !== 1) fail(`${label} must declare '${capability}' exactly once.`)
  const descriptor = requirePascalCaseObject(matches[0], `${label} '${capability}' capability`)
  if (descriptor.ContractVersion !== 1 || descriptor.Available !== true ||
      !Array.isArray(descriptor.ProfileIds) || !descriptor.ProfileIds.includes(worker.id) ||
      !worker.serviceCapabilities.includes(capability)) {
    fail(`${label} capability '${capability}' is not available for profile '${worker.id}'.`)
  }
}

function requireReferenceSet(worker, binding, label) {
  if (!Array.isArray(worker.referenceSets)) fail(`${label} has no reference-set attestations.`)
  const matches = worker.referenceSets.filter(value => value?.Id === binding.referenceSetId)
  if (matches.length !== 1) fail(`${label} must attest reference set '${binding.referenceSetId}' exactly once.`)
  const attestation = requirePascalCaseObject(matches[0], `${label} reference set '${binding.referenceSetId}'`)
  if (attestation.TargetFramework !== binding.targetFramework || attestation.Digest !== binding.referencePackage.packageContentHash) {
    fail(`${label} reference-set attestation does not match the runtime matrix binding.`)
  }
  requireDigest(attestation.ContentDigest, `${label} reference set ContentDigest`)
  const provenance = requirePascalCaseObject(attestation.Provenance, `${label} reference set Provenance`)
  if (provenance.Kind !== 'nuget-package' || provenance.ResolvedVersion !== binding.referencePackage.version ||
      provenance.Package !== binding.referencePackage.id) {
    fail(`${label} reference-set provenance does not match the runtime matrix binding.`)
  }
  return attestation
}

function requireManifestRuntimeRequirement(runtimeRequirement, binding) {
  const requirement = requirePascalCaseObject(runtimeRequirement, 'Artifact manifest RuntimeRequirement')
  if (requirement.Family !== 'coreclr' || !Array.isArray(requirement.Frameworks) || requirement.Frameworks.length !== 1) {
    fail('Artifact manifest runtime requirement is not the required CoreCLR framework contract.')
  }
  const framework = requirePascalCaseObject(requirement.Frameworks[0], 'Artifact manifest runtime framework')
  if (framework.Name !== binding.runtimeFramework.name ||
      framework.MinimumVersion !== binding.runtimeFramework.minimumVersion) {
    fail('Artifact manifest runtime framework does not match the runtime matrix binding.')
  }
  return requirement
}

async function getArtifactManifest(request, artifactStoreUrl, artifactRef, binding) {
  const descriptor = requirePascalCaseObject(await readJson(
    await request(joinUrl(artifactStoreUrl, `/internal/v1/artifacts/sha256/${artifactDigest(artifactRef)}`)),
    `Artifact Store manifest ${artifactRef}`,
  ), `Artifact Store manifest ${artifactRef}`)
  const manifest = requirePascalCaseObject(descriptor.Manifest, `Artifact Store manifest ${artifactRef}.Manifest`)
  if (manifest.ArtifactId !== artifactRef || manifest.ReferenceSetId !== binding.referenceSetId ||
      manifest.TargetFramework !== binding.targetFramework || manifest.ArtifactFormat !== 'dotnet-managed-pe-v1') {
    fail('Artifact Store manifest does not match the built artifact and runtime matrix binding.')
  }
  const runtimeRequirement = requireManifestRuntimeRequirement(manifest.RuntimeRequirement, binding)
  return {
    artifactId: manifest.ArtifactId,
    referenceSetId: manifest.ReferenceSetId,
    targetFramework: manifest.TargetFramework,
    artifactFormat: manifest.ArtifactFormat,
    runtimeRequirement,
  }
}

function buildRequest(referenceSetId, now) {
  const requestId = newId('runtime-artifact-build')
  const options = {
    Configuration: 'release',
    Optimize: true,
    OutputKind: 'library',
    AllowUnsafe: false,
    EmitPortablePdb: true,
    NullableContext: 'enable',
    LanguageVersion: '14.0',
  }
  return {
    RequestId: requestId,
    IdempotencyKey: requestId,
    PipelineResolutionId: 'runtime-artifact-smoke-v1',
    ToolchainId: 'roslyn-stable',
    ReferenceSetId: referenceSetId,
    Workspace: {
      SchemaVersion: 1,
      Revision: 1,
      SelectionRevision: 1,
      LanguageId: 'csharp',
      Files: [{
        Path: 'Program.cs',
        Version: 1,
        Text: 'public static class RuntimeArtifactProbe { public static int RuntimeMatrixProbeMethod(int value) => value + 1; }',
      }],
      ActiveFile: 'Program.cs',
      SourceOrder: ['Program.cs'],
      ReferenceSetId: referenceSetId,
      BuildOptions: options,
    },
    DeadlineUtc: new Date(now().getTime() + 60_000).toISOString(),
    Options: options,
    Target: 'artifact',
  }
}

async function buildArtifact(send, roslynUrl, descriptor, referenceSetId, now) {
  const build = buildRequest(referenceSetId, now)
  const response = requirePascalCaseObject(await readJson(
    await send(joinUrl(roslynUrl, '/api/v1/build'), {
      method: 'POST', body: JSON.stringify(build),
    }),
    'Roslyn build',
  ), 'Roslyn build')
  if (response.RequestId !== build.RequestId) fail('Roslyn build did not preserve RequestId.')
  const result = requirePascalCaseObject(response.Result, 'Roslyn build Result')
  if (result.ResultType !== 'build' || result.Outcome !== 'succeeded') {
    fail('Roslyn build did not return a succeeded BuildResult.')
  }
  const artifactRef = requireDigest(result.ArtifactRef, 'BuildResult.ArtifactRef')
  const identity = requirePascalCaseObject(result.Identity, 'BuildResult.Identity')
  if (identity.ReleaseId !== descriptor.releaseId || identity.LanguageId !== 'csharp' ||
      identity.ToolchainId !== 'roslyn-stable' ||
      identity.ReferenceSetId !== referenceSetId || identity.WorkerImageId !== descriptor.workerImageId ||
      typeof identity.CompilerVersion !== 'string' || identity.CompilerVersion.length === 0 ||
      result.WorkspaceRevision !== 1 || result.SelectionRevision !== 1) {
    fail('BuildResult identity does not match the requested Roslyn worker and reference set.')
  }
  return { artifactRef, identity }
}

async function waitForOperation(request, artifactUrl, operationId, maxPolls, sleep, pollDelayMilliseconds) {
  let state
  for (let attempt = 0; attempt < maxPolls; attempt++) {
    state = requirePascalCaseObject(await readJson(
      await request(joinUrl(artifactUrl, `/api/v1/operations/${encodeURIComponent(operationId)}`)),
      `Artifact operation ${operationId}`,
    ), `Artifact operation ${operationId}`)
    if (state.OperationId !== operationId) fail(`Artifact operation route returned a different operation identity.`)
    if (state.Status === 'completed') break
    if (state.Status === 'failed' || state.Status === 'cancelled') {
      fail(`Artifact operation ${operationId} ended as ${state.Status}.`)
    }
    await sleep(pollDelayMilliseconds)
  }
  if (state?.Status !== 'completed') fail(`Artifact operation ${operationId} did not complete within ${maxPolls} polls.`)
  const events = await readJson(
    await request(joinUrl(
      artifactUrl,
      `/api/v1/operations/${encodeURIComponent(operationId)}/events?FromSequence=0`,
    )),
    `Artifact operation ${operationId} events`,
  )
  if (!Array.isArray(events) || events.length === 0) fail(`Artifact operation ${operationId} returned no events.`)
  let previousSequence = 0
  for (const event of events) {
    requirePascalCaseObject(event, `Artifact operation ${operationId} event`)
    requirePascalCaseObject(event.Payload, `Artifact operation ${operationId} event payload`)
    if (event.OperationId !== operationId || !Number.isSafeInteger(event.Sequence) || event.Sequence <= previousSequence) {
      fail(`Artifact operation ${operationId} events have an invalid sequence.`)
    }
    previousSequence = event.Sequence
  }
  if (events.at(-1)?.Payload?.Kind !== 'completed' || events.at(-1)?.Payload?.Status !== 'completed') {
    fail(`Artifact operation ${operationId} events do not end in completion.`)
  }
  return events
}

async function renderArtifact(request, artifactUrl, descriptor, artifactRef, outputId, now, maxPolls, sleep, pollDelayMilliseconds) {
  const requestId = newId(`runtime-artifact-${outputId}`)
  const response = requirePascalCaseObject(await readJson(
    await request(joinUrl(artifactUrl, '/api/v1/artifact-renders'), {
      method: 'POST',
      body: JSON.stringify({
        RequestId: requestId,
        IdempotencyKey: requestId,
        PipelineResolutionId: 'runtime-artifact-smoke-v1',
        ArtifactRef: artifactRef,
        ProcessorId: 'artifacts-default',
        OutputId: outputId,
        Options: { IncludeSequencePoints: true, IncludeCompilerGeneratedMembers: true, MaxCharacters: 1_000_000 },
        DeadlineUtc: new Date(now().getTime() + 60_000).toISOString(),
      }),
    }),
    `Artifact ${outputId} start`,
  ), `Artifact ${outputId} start`)
  const operationId = requiredString(response.OperationId, `Artifact ${outputId} OperationId`)
  const events = await waitForOperation(
    request, artifactUrl, operationId, maxPolls, sleep, pollDelayMilliseconds,
  )
  const typed = events.filter(event => event.Payload?.Kind === 'typed-result')
  if (typed.length !== 1) fail(`Artifact ${outputId} operation returned ${typed.length} typed results.`)
  const result = requirePascalCaseObject(typed[0].Payload.Result, `Artifact ${outputId} result`)
  const identity = requirePascalCaseObject(result.Identity, `Artifact ${outputId} identity`)
  if (result.ResultType !== 'artifact-render' || result.Outcome !== 'succeeded' ||
      identity.ReleaseId !== descriptor.releaseId ||
      identity.ProcessorId !== descriptor.id || identity.WorkerImageId !== descriptor.workerImageId) {
    fail(`Artifact ${outputId} result identity does not match artifacts-default.`)
  }
  requiredString(identity.ProcessorVersion, `Artifact ${outputId} identity.ProcessorVersion`)
  const contentRef = requireDigest(result.ContentRef, `Artifact ${outputId} ContentRef`)
  if (!events.some(event => event.Payload?.Kind === 'content-produced' &&
      event.Payload.ContentRef === contentRef)) {
    fail(`Artifact ${outputId} result has no matching content-produced event.`)
  }
  return { contentRef, identity, operationId }
}

async function readContent(request, artifactStoreUrl, contentRef) {
  const response = await request(joinUrl(
    artifactStoreUrl,
    `/internal/v1/contents/sha256/${artifactDigest(contentRef)}`,
  ), { headers: { Accept: 'text/plain' } })
  if (!response.ok) fail(`Artifact Store content ${contentRef} returned HTTP ${response.status}.`)
  if (response.headers.get('ETag') !== `\"${contentRef}\"`) {
    fail(`Artifact Store content ${contentRef} did not return its canonical ETag.`)
  }
  const bytes = await readBoundedResponseBytes(response, `Artifact Store content ${contentRef}`, maximumContentResponseBytes)
  if (sha256(bytes) !== contentRef) fail(`Artifact Store content ${contentRef} did not match its ContentRef.`)
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch (error) {
    fail(`Artifact Store content ${contentRef} is not strict UTF-8: ${error.message}`, { cause: error })
  }
}

function updateVerification(row, evidence, now) {
  const previous = isObject(row.verification) ? row.verification : {}
  const priorSmoke = isObject(previous.smoke) ? previous.smoke : {}
  const allArtifactChecksPassed = evidence.compilePassed && evidence.ilPassed && evidence.decompiledCSharpPassed
  const smoke = {
    runtimeIdentity: priorSmoke.runtimeIdentity ?? 'unverified',
    compile: allArtifactChecksPassed ? 'passed' : 'unverified',
    run: priorSmoke.run ?? (row.expected?.capabilities?.includes('run') ? 'unverified' : 'not-applicable'),
    ilDecompile: allArtifactChecksPassed ? 'passed' : 'unverified',
    jit: priorSmoke.jit ?? (row.expected?.capabilities?.includes('jit-asm') ? 'unverified' : 'not-applicable'),
    mapping: priorSmoke.mapping ?? (row.expected?.sourceMappingKind === 'none' ? 'not-applicable' : 'unverified'),
  }
  const pending = Object.entries(smoke)
    .filter(([, status]) => status !== 'passed' && status !== 'not-applicable')
    .map(([name]) => name)
  row.verification = {
    ...previous,
    status: pending.length === 0 ? 'smoke-passed' : 'runtime-smoke-passed',
    reason: pending.length === 0 ? null : `${pending.join('-')}-pending`,
    smoke,
    evidence: {
      ...(isObject(previous.evidence) ? previous.evidence : {}),
      artifactPipeline: { ...evidence, observedAt: now().toISOString() },
    },
  }
}

function readSelectedRows(results, profileIds) {
  if (results?.schemaVersion !== 1 || !Array.isArray(results.rows)) {
    fail('Functional results must use schema version 1 with rows.')
  }
  const byId = new Map(results.rows.map(row => [row?.profileId, row]))
  return profileIds.map(profileId => {
    const row = byId.get(profileId)
    if (!isObject(row)) fail(`Functional results has no row '${profileId}'.`)
    requireDigest(row.profileSha256, `Row '${profileId}' profileSha256`)
    requireDigest(row.image?.imageId, `Row '${profileId}' image.imageId`)
    requiredString(row.referenceSetId, `Row '${profileId}' referenceSetId`)
    return row
  })
}

export async function runRuntimeArtifactSmokes(options = {}) {
  const profileIds = options.profileIds
  if (!Array.isArray(profileIds) || profileIds.length === 0 || new Set(profileIds).size !== profileIds.length ||
      profileIds.some(id => !profileIdPattern.test(id))) {
    fail('Smoke profile IDs must be a non-empty unique list of safe IDs.')
  }
  const fetch = options.fetch ?? globalThis.fetch
  if (typeof fetch !== 'function') fail('A fetch implementation is required.')
  const now = options.now ?? (() => new Date())
  const sleep = options.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)))
  if (typeof sleep !== 'function') fail('Sleep must be a function.')
  const resultsPath = path.resolve(options.resultsPath ?? defaultResultsPath)
  const profileDirectory = path.resolve(options.profileDirectory ?? defaultProfileDirectory)
  const runtimeMatrixPath = path.resolve(options.runtimeMatrixPath ?? defaultRuntimeMatrixPath)
  const artifactStoreUrl = endpoint(options.artifactStoreUrl, 'Artifact Store URL')
  const roslynWorkerUrl = endpoint(options.roslynWorkerUrl, 'Roslyn worker URL')
  const artifactWorkerUrl = endpoint(options.artifactWorkerUrl, 'Artifact worker URL')
  const token = options.internalToken === undefined
    ? undefined
    : validateInternalToken(options.internalToken, 'Internal token')
  const requestTimeoutMilliseconds = options.requestTimeoutMilliseconds ?? defaultRequestTimeoutMilliseconds
  if (!Number.isSafeInteger(requestTimeoutMilliseconds) || requestTimeoutMilliseconds < 1) {
    fail('Request timeout must be a positive integer number of milliseconds.')
  }
  const pollDelayMilliseconds = options.pollDelayMilliseconds ?? defaultPollDelayMilliseconds
  const maxPolls = options.maxPolls ?? Math.ceil(minimumPollWindowMilliseconds / pollDelayMilliseconds)
  if (!Number.isSafeInteger(pollDelayMilliseconds) || pollDelayMilliseconds < 1 ||
      !Number.isSafeInteger(maxPolls) || maxPolls < 1 ||
      maxPolls * pollDelayMilliseconds < minimumPollWindowMilliseconds) {
    fail(`Artifact polling must cover at least ${minimumPollWindowMilliseconds} milliseconds.`)
  }
  const request = (url, init) => makeRequest(fetch, token, requestTimeoutMilliseconds, url, init)
  const results = readBoundedJson(resultsPath, 'Functional result')
  const rows = readSelectedRows(results, profileIds)
  const bindings = new Map()
  for (const row of rows) {
    const profile = readProfileBinding(profileDirectory, row)
    bindings.set(row.profileId, readRuntimeBinding(runtimeMatrixPath, results, row, profile))
  }
  const artifactStore = await getArtifactStoreIdentity(request, artifactStoreUrl)
  const roslyn = await getWorkerDescriptor(request, roslynWorkerUrl, {
    id: 'roslyn-stable', serviceKind: serviceKinds.toolchainWorker,
    workerKind: 'toolchain', label: 'Roslyn worker',
  })
  const artifactWorker = await getWorkerDescriptor(request, artifactWorkerUrl, {
    id: 'artifacts-default', serviceKind: serviceKinds.artifactWorker,
    workerKind: 'artifact-processor', label: 'Artifact worker',
  })
  requireCapability(roslyn, 'managed-pe', 'Roslyn worker')
  requireCapability(artifactWorker, 'il', 'Artifact worker')
  requireCapability(artifactWorker, 'decompiled-csharp', 'Artifact worker')
  if (artifactStore.releaseId !== roslyn.releaseId || roslyn.releaseId !== artifactWorker.releaseId) {
    fail('Artifact Store, Roslyn worker, and artifacts-default must have the same ReleaseId.')
  }

  for (const row of rows) {
    const binding = bindings.get(row.profileId)
    const referenceSetAttestation = requireReferenceSet(roslyn, binding, 'Roslyn worker')
    const build = await buildArtifact(request, roslynWorkerUrl, roslyn, binding.referenceSetId, now)
    const manifest = await getArtifactManifest(request, artifactStoreUrl, build.artifactRef, binding)
    const il = await renderArtifact(
      request, artifactWorkerUrl, artifactWorker, build.artifactRef, 'il', now,
      maxPolls, sleep, pollDelayMilliseconds,
    )
    const decompiled = await renderArtifact(
      request, artifactWorkerUrl, artifactWorker, build.artifactRef, 'decompiled-csharp', now,
      maxPolls, sleep, pollDelayMilliseconds,
    )
    const ilText = await readContent(request, artifactStoreUrl, il.contentRef)
    const csharpText = await readContent(request, artifactStoreUrl, decompiled.contentRef)
    const evidence = {
      profileSha256: row.profileSha256,
      imageId: row.image.imageId,
      matrix: binding,
      referenceSetId: binding.referenceSetId,
      compilePassed: true,
      ilPassed: /\.method/i.test(ilText) && ilText.includes('RuntimeMatrixProbeMethod'),
      decompiledCSharpPassed: csharpText.includes('RuntimeMatrixProbeMethod'),
      services: {
        artifactStore,
        roslyn: { ...roslyn, referenceSets: undefined, referenceSetAttestation, buildIdentity: build.identity },
        artifactsDefault: { ...artifactWorker, ilIdentity: il.identity, decompiledCSharpIdentity: decompiled.identity },
      },
      artifactRef: build.artifactRef,
      artifactManifest: manifest,
      ilContentRef: il.contentRef,
      decompiledCSharpContentRef: decompiled.contentRef,
    }
    if (!evidence.ilPassed || !evidence.decompiledCSharpPassed) fail(`Profile '${row.profileId}' artifact output lost the probe method.`)
    updateVerification(row, evidence, now)
  }
  results.verificationRefreshedAt = now().toISOString()
  writeJsonAtomically(resultsPath, results)
  return results
}

function parseArguments(argv) {
  if (argv.includes('--help') || argv.includes('-h')) return { help: true }
  const result = { profileIds: [] }
  const names = new Map([
    ['--artifact-store', 'artifactStoreUrl'],
    ['--roslyn-worker', 'roslynWorkerUrl'],
    ['--artifact-worker', 'artifactWorkerUrl'],
    ['--token-file', 'tokenFile'],
    ['--results', 'resultsPath'],
  ])
  for (let index = 0; index < argv.length; index++) {
    const option = argv[index]
    if (option === '--profile') {
      const value = argv[++index]
      if (value === undefined) fail("Option '--profile' requires a value.")
      result.profileIds.push(value)
      continue
    }
    const name = names.get(option)
    if (name === undefined) fail(`Unknown option '${option}'.`)
    if (result[name] !== undefined) fail(`Duplicate option '${option}'.`)
    const value = argv[++index]
    if (value === undefined || value.length === 0) fail(`Option '${option}' requires a value.`)
    result[name] = value
  }
  for (const [option, name] of names) {
    if (name !== 'tokenFile' && name !== 'resultsPath' && result[name] === undefined) {
      fail(`Option '${option}' is required.`)
    }
  }
  return result
}

export async function runRuntimeArtifactSmokeCli(argv, options = {}) {
  const output = options.output ?? console
  try {
    const parsed = parseArguments(argv)
    if (parsed.help) {
      output.log(runtimeArtifactSmokeUsage)
      return 0
    }
    await runRuntimeArtifactSmokes({
      ...options,
      ...parsed,
      internalToken: options.internalToken ?? (parsed.tokenFile !== undefined
        ? readInternalTokenFile(parsed.tokenFile)
        : process.env.SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE !== undefined
          ? readInternalTokenFile(process.env.SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE)
          : process.env.SHARPLABNEXT_INTERNAL_SERVICE_TOKEN),
    })
    output.log(`Artifact smoke passed for ${parsed.profileIds.length} runtime profile(s).`)
    return 0
  } catch (error) {
    output.error(`runtime artifact smoke error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await runRuntimeArtifactSmokeCli(process.argv.slice(2))
}
