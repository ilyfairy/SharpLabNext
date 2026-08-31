/** Validation-only Framework Supervisor one-shot smoke. No Docker orchestration. */
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const defaultResults = path.join(root, '.tmp', 'runtime-matrix-functional-results.json')
const defaultProfiles = path.join(root, 'profiles', 'runtimes', 'candidates')
const defaultOverlay = path.join(root, '.tmp', 'runtime-framework-supervisor-smoke.json')
const digestPattern = /^sha256:[0-9a-f]{64}$/
const idPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const maxBytes = 16 * 1024 * 1024
const maxResponseBytes = 4 * 1024 * 1024

export const runtimeFrameworkSupervisorSmokeUsage = `Usage:
  node eng/smoke/runtime-framework-supervisor-smoke.mjs --profile wine-netfx48-linux-x64 --prepare-only [--overlay PATH]
  node eng/smoke/runtime-framework-supervisor-smoke.mjs --profile wine-netfx48-linux-x64 --supervisor URL --roslyn-worker URL --token-file PATH [--results PATH] [--overlay PATH]`

export class RuntimeFrameworkSupervisorSmokeError extends Error {
  constructor(message, options) { super(message, options); this.name = 'RuntimeFrameworkSupervisorSmokeError' }
}

function fail(message, options) { throw new RuntimeFrameworkSupervisorSmokeError(message, options); }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }
function digest(value, label) { if (!digestPattern.test(value ?? '')) fail(`${label} must be a sha256 content identity.`); return value }
function string(value, label) { if (typeof value !== 'string' || value.length === 0) fail(`${label} must be a non-empty string.`); return value }
function sha256(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}` }
function pascal(value, label) {
  if (!object(value)) fail(`${label} must be a JSON object.`)
  for (const key of Object.keys(value)) if (!/^[A-Z]/.test(key)) fail(`${label} contains non-PascalCase property '${key}'.`)
  return value
}
function file(filename, label, maximum = maxBytes) {
  const resolved = path.resolve(filename)
  let stat
  try { stat = fs.lstatSync(resolved) } catch (error) { fail(`${label} '${resolved}' could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > maximum) fail(`${label} '${resolved}' must be a bounded regular non-link file.`)
  return fs.readFileSync(resolved)
}
function json(filename, label) {
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(file(filename, label))) } catch (error) {
    if (error instanceof RuntimeFrameworkSupervisorSmokeError) throw error
    fail(`${label} '${path.resolve(filename)}' is invalid JSON: ${error.message}`, { cause: error })
  }
}
function writeAtomic(filename, value) {
  const resolved = path.resolve(filename); fs.mkdirSync(path.dirname(resolved), { recursive: true })
  const temporary = path.join(path.dirname(resolved), `.${path.basename(resolved)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`)
  try { fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' }); fs.renameSync(temporary, resolved) } finally { fs.rmSync(temporary, { force: true }) }
}
function url(value, label) {
  let parsed; try { parsed = new URL(value) } catch { fail(`${label} must be an absolute HTTP URL.`) }
  if (!['http:', 'https:'].includes(parsed.protocol)) fail(`${label} must use HTTP(S).`)
  return parsed.toString().replace(/\/$/, '')
}
function equal(left, right, label) { if (left !== right) fail(`${label} does not match the Framework binding.`) }
function protocol(value, label) { const result = pascal(value, label); if (result.Major !== 1 || result.Minor !== 0) fail(`${label} must be protocol 1.0.`); return result }

export function readRuntimeFrameworkSupervisorTokenFile(filename) {
  const value = file(filename, 'Internal token file', 8194).toString('utf8').replace(/[\r\n]+$/, '')
  if (value.length < 32 || value.length > 8192 || [...value].some(character => character <= ' ' || character >= '\u007f')) fail('Internal token file must contain 32..8192 visible ASCII characters.')
  return value
}

function selectBinding(results, profileId, profileDirectory) {
  if (results?.schemaVersion !== 1 || !Array.isArray(results.rows)) fail('Functional results must use schema version 1 with rows.')
  const rows = results.rows.filter(row => row?.profileId === profileId)
  if (rows.length !== 1) fail(`Functional results must contain exactly one row '${profileId}'.`)
  const row = rows[0]
  const imageId = digest(row.image?.imageId, `Row '${profileId}' image.imageId`)
  if (row.family !== 'netfx-clr-wine' || row.matrixTargetId !== 'netfx48' || row.runtimeVersion !== '4.8' || row.candidateImage === undefined ||
      JSON.stringify(row.expected?.capabilities) !== JSON.stringify(['run'])) fail(`Row '${profileId}' is not the Run-only Wine Framework netfx48 binding.`)
  const artifact = row.verification?.evidence?.artifactPipeline
  if (!object(artifact) || artifact.profileSha256 !== row.profileSha256 || artifact.imageId !== imageId || artifact.referenceSetId !== row.referenceSetId ||
      !artifact.compilePassed || !artifact.ilPassed || !artifact.decompiledCSharpPassed || !object(artifact.matrix) || artifact.matrix.targetFramework !== 'net48') {
    fail(`Row '${profileId}' has no current netfx48 Framework artifact evidence.`)
  }
  // The existing artifact is intentionally only attestation input: it is a library, never a Run input.
  digest(artifact.artifactRef, `Row '${profileId}' library artifactRef`)
  const bytes = file(path.join(path.resolve(profileDirectory), `${profileId}.json`), `Runtime profile '${profileId}'`)
  let profile; try { profile = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) } catch (error) { fail(`Runtime profile '${profileId}' is invalid JSON: ${error.message}`, { cause: error }) }
  if (sha256(bytes) !== row.profileSha256 || profile?.id !== profileId || profile.family !== row.family || profile.image !== row.candidateImage ||
      JSON.stringify(profile.capabilities) !== JSON.stringify(['run']) || profile.runtimeVersion !== row.runtimeVersion ||
      !Array.isArray(profile.allowedSecurityPolicyIds) || profile.allowedSecurityPolicyIds.length !== 1 ||
      !Array.isArray(profile.securityPolicies) || profile.securityPolicies.length !== 1 || profile.securityPolicies[0]?.id !== profile.allowedSecurityPolicyIds[0] ||
      !object(profile.operations?.run) || !object(profile.container) || typeof profile.container.winePrefixPath !== 'string') {
    fail(`Runtime profile '${profileId}' does not match its Run-only Framework binding.`)
  }
  const roslyn = artifact.services?.roslyn
  if (!object(roslyn) || roslyn.id !== 'roslyn-stable-netfx48' || typeof roslyn.releaseId !== 'string' ||
      !digestPattern.test(roslyn.workerImageId ?? '') || !object(roslyn.referenceSetAttestation)) fail('Artifact evidence has no bound netfx48 Roslyn identity.')
  return { row, profile, policy: profile.securityPolicies[0], imageId, artifact, roslyn }
}

export function createRuntimeFrameworkSupervisorOverlay(binding) {
  const profile = structuredClone(binding.profile)
  profile.runtimeImageId = binding.imageId
  delete profile.securityPolicies
  return {
    RuntimeSupervisor: { SessionReuseEnabled: false, RequireDigestPinnedImages: false },
    RuntimeSupervisorProfileOverlay: { Enabled: true, Profiles: [profile], SecurityPolicies: [structuredClone(binding.policy)] },
  }
}

export function prepareRuntimeFrameworkSupervisorSmoke(options = {}) {
  if (!idPattern.test(options.profileId ?? '')) fail('Smoke profile ID must be a safe ID.')
  const results = json(options.resultsPath ?? defaultResults, 'Functional results')
  const binding = selectBinding(results, options.profileId, options.profileDirectory ?? defaultProfiles)
  const overlay = createRuntimeFrameworkSupervisorOverlay(binding)
  if (options.overlayPath !== false) writeAtomic(options.overlayPath ?? defaultOverlay, overlay)
  return { results, binding, overlay }
}

async function text(response, label) {
  const declared = Number(response.headers.get('Content-Length'))
  if (Number.isFinite(declared) && declared > maxResponseBytes) fail(`${label} response exceeds its byte limit.`)
  const bytes = Buffer.from(await response.arrayBuffer())
  if (bytes.length > maxResponseBytes) fail(`${label} response exceeds its byte limit.`)
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes) } catch (error) { fail(`${label} response is not strict UTF-8: ${error.message}`, { cause: error }) }
}
async function requestJson(fetch, token, requestUrl, init, label) {
  const headers = new Headers(init?.headers); headers.set('Accept', 'application/json'); headers.set('Authorization', `Bearer ${token}`); if (init?.body !== undefined) headers.set('Content-Type', 'application/json')
  let response; try { response = await fetch(requestUrl, { ...init, headers, signal: AbortSignal.timeout(15_000) }) } catch (error) { fail(`${label} request failed: ${error.message}`, { cause: error }) }
  const body = await text(response, label); if (!response.ok) fail(`${label} returned HTTP ${response.status}: ${body.slice(0, 500)}`)
  try { return JSON.parse(body) } catch (error) { fail(`${label} returned invalid JSON: ${error.message}`, { cause: error }) }
}
function descriptor(binding, value) {
  const result = pascal(value, 'Roslyn worker descriptor'); const service = pascal(result.Service, 'Roslyn worker descriptor.Service')
  if (service.Id !== 'roslyn-stable-netfx48' || service.Kind !== 3 || service.Status !== 'ready') fail('Roslyn descriptor does not identify ready roslyn-stable-netfx48.')
  protocol(service.Protocol, 'Roslyn Service.Protocol'); protocol(result.NegotiatedProtocol, 'Roslyn NegotiatedProtocol')
  if (!Array.isArray(result.SupportedProtocolVersions) || !result.SupportedProtocolVersions.some(value => value?.Major === 1 && value?.Minor === 0)) fail('Roslyn descriptor does not support protocol 1.0.')
  equal(service.ReleaseId, binding.roslyn.releaseId, 'Roslyn ReleaseId'); equal(digest(result.WorkerImageId, 'Roslyn WorkerImageId'), binding.roslyn.workerImageId, 'Roslyn WorkerImageId')
  const attestation = Array.isArray(result.ReferenceSets) ? result.ReferenceSets.filter(value => value?.Id === binding.row.referenceSetId) : []
  if (attestation.length !== 1 || JSON.stringify(attestation[0]) !== JSON.stringify(binding.roslyn.referenceSetAttestation)) fail('Roslyn netfx48 reference-set attestation does not match artifact evidence.')
  return { releaseId: service.ReleaseId, workerImageId: result.WorkerImageId }
}
function requestId() { return `runtime-framework-supervisor-smoke-${crypto.randomUUID().replaceAll('-', '')}` }
function consoleBuildRequest(binding, now) {
  const id = requestId(); const buildOptions = { Configuration: 'release', Optimize: true, OutputKind: 'console', AllowUnsafe: false, EmitPortablePdb: true, NullableContext: 'enable', LanguageVersion: '14.0' }
  return {
    RequestId: id, IdempotencyKey: id, PipelineResolutionId: 'runtime-framework-supervisor-smoke-v1', ToolchainId: 'roslyn-stable-netfx48', ReferenceSetId: binding.row.referenceSetId,
    Workspace: { SchemaVersion: 1, Revision: 1, SelectionRevision: 1, LanguageId: 'csharp', Files: [{ Path: 'Program.cs', Version: 1, Text: 'using System; public static class Program { public static int Main(string[] args) { Console.WriteLine("SLN-FRAMEWORK-SUPERVISOR-V1"); foreach (var arg in args) Console.WriteLine(arg); return 0; } }' }], ActiveFile: 'Program.cs', SourceOrder: ['Program.cs'], ReferenceSetId: binding.row.referenceSetId, BuildOptions: buildOptions },
    DeadlineUtc: new Date(now().getTime() + 60_000).toISOString(), Options: buildOptions, Target: 'artifact',
  }
}
async function buildConsole(fetch, token, roslynUrl, binding, now) {
  const worker = descriptor(binding, await requestJson(fetch, token, `${roslynUrl}/api/v1/worker/describe`, { method: 'GET' }, 'Roslyn worker descriptor'))
  const request = consoleBuildRequest(binding, now)
  const response = pascal(await requestJson(fetch, token, `${roslynUrl}/api/v1/build`, { method: 'POST', body: JSON.stringify(request) }, 'Roslyn Console build'), 'Roslyn Console build')
  if (response.RequestId !== request.RequestId) fail('Roslyn Console build did not preserve RequestId.')
  const result = pascal(response.Result, 'Roslyn Console BuildResult'); const identity = pascal(result.Identity, 'Roslyn Console BuildResult.Identity')
  if (result.ResultType !== 'build' || result.Outcome !== 'succeeded' || result.WorkspaceRevision !== 1 || result.SelectionRevision !== 1 || identity.ReleaseId !== worker.releaseId || identity.ToolchainId !== 'roslyn-stable-netfx48' || identity.ReferenceSetId !== binding.row.referenceSetId || identity.WorkerImageId !== worker.workerImageId || identity.LanguageId !== 'csharp') fail('Roslyn Console BuildResult identity does not match the validated worker.')
  return { artifactRef: digest(result.ArtifactRef, 'Roslyn Console BuildResult.ArtifactRef'), identity }
}
function verifySupervisorStatus(binding, status) {
  const document = pascal(status, 'Supervisor runtime status'); const profiles = document.Profiles
  if (!Array.isArray(profiles) || profiles.length !== 1) fail('Supervisor runtime status must expose exactly one profile.')
  const actual = pascal(profiles[0], 'Supervisor runtime status profile')
  const expected = binding.profile
  for (const [key, value] of Object.entries({ Id: expected.id, Image: expected.image, RuntimeVersion: expected.runtimeVersion, RuntimeCommit: expected.runtimeCommit, RuntimeImageId: binding.imageId, Rid: expected.rid, Architecture: expected.architecture })) equal(actual[key], value, `Supervisor status ${key}`)
  const container = pascal(actual.Container, 'Supervisor runtime status profile.Container')
  const operations = pascal(actual.Operations, 'Supervisor runtime status profile.Operations')
  const run = pascal(operations.Run, 'Supervisor runtime status profile.Operations.Run')
  const command = pascal(run.Command, 'Supervisor runtime status profile.Operations.Run.Command')
  const expectedRun = expected.operations.run
  if (Object.keys(operations).length !== 1 || run.ImplementationId !== expectedRun.implementationId || run.PathStyle !== expectedRun.pathStyle ||
      command.Executable !== expectedRun.command.executable || JSON.stringify(command.Argv) !== JSON.stringify(expectedRun.command.argv) ||
      container.WinePrefixPath !== expected.container.winePrefixPath) {
    fail('Supervisor status operation, Wine prefix, or image binding does not match the candidate profile.')
  }
}
function decodeCanonicalBase64(value) {
  if (typeof value !== 'string' || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) fail('Supervisor stdout chunk is not canonical base64.')
  const decoded = Buffer.from(value, 'base64'); if (decoded.toString('base64') !== value) fail('Supervisor stdout chunk is not canonical base64.')
  return decoded
}
function parseSse(value, operationId) {
  const events = []
  for (const block of value.replace(/\r/g, '').split('\n\n')) { const data = block.split('\n').filter(line => line.startsWith('data:')).map(line => line.slice(5).replace(/^ /, '')).join('\n'); if (data) { let event; try { event = JSON.parse(data) } catch (error) { fail(`Supervisor SSE is invalid JSON: ${error.message}`, { cause: error }) }; events.push(pascal(event, 'Supervisor operation event')) } }
  let sequence = 0; for (const event of events) { if (event.OperationId !== operationId || !Number.isSafeInteger(event.Sequence) || event.Sequence <= sequence || !object(event.Payload)) fail('Supervisor operation events have invalid identities or sequence.'); sequence = event.Sequence }
  if (events.length === 0) fail('Supervisor operation returned no events.'); return events
}
async function supervisorRun(fetch, token, supervisorUrl, binding, consoleArtifact, now, sleep, maxPolls, delay) {
  verifySupervisorStatus(binding, await requestJson(fetch, token, `${supervisorUrl}/api/v1/runtime/status`, { method: 'GET' }, 'Supervisor runtime status'))
  const id = requestId(); const request = { RequestId: id, IdempotencyKey: id, PipelineResolutionId: 'runtime-framework-supervisor-smoke-v1', ArtifactRef: consoleArtifact.artifactRef, RuntimeProfileId: binding.row.profileId, Options: { Arguments: ['first', 'second'], Stdin: null, Instrumentation: 'none', SecurityPolicyId: binding.policy.id }, DeadlineUtc: new Date(now().getTime() + 60_000).toISOString() }
  const accepted = pascal(await requestJson(fetch, token, `${supervisorUrl}/internal/v1/jobs/run`, { method: 'POST', body: JSON.stringify(request) }, 'Supervisor Run start'), 'Supervisor Run start'); const operationId = string(accepted.OperationId, 'Supervisor Run OperationId')
  let state; for (let attempt = 0; attempt < maxPolls; attempt++) { state = pascal(await requestJson(fetch, token, `${supervisorUrl}/internal/v1/operations/${encodeURIComponent(operationId)}`, { method: 'GET' }, `Supervisor operation ${operationId}`), 'Supervisor operation state'); if (state.OperationId !== operationId) fail('Supervisor operation route returned a different operation ID.'); if (state.Status === 'completed') break; if (state.Status === 'failed' || state.Status === 'cancelled') fail(`Supervisor operation ended as ${state.Status}.`); await sleep(delay) }
  if (state?.Status !== 'completed') fail('Supervisor operation did not complete before polling expired.')
  let response; try { response = await fetch(`${supervisorUrl}/internal/v1/operations/${encodeURIComponent(operationId)}/events?FromSequence=0`, { headers: { Accept: 'text/event-stream', Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(15_000) }) } catch (error) { fail(`Supervisor events request failed: ${error.message}`, { cause: error }) }
  if (!response.ok) fail(`Supervisor events returned HTTP ${response.status}.`)
  const events = parseSse(await text(response, 'Supervisor events'), operationId); const typed = events.filter(event => event.Payload.Kind === 'typed-result')
  if (typed.length !== 1 || !events.some(event => event.Payload.Kind === 'completed' && event.Payload.Status === 'completed')) fail('Supervisor operation does not contain one completed Run result.')
  const result = pascal(typed[0].Payload.Result, 'Supervisor Run result'); const identity = pascal(result.Identity, 'Supervisor Run identity')
  if (result.ResultType !== 'run' || result.Status !== 'completed' || result.ExitCode !== 0 || result.OutputTruncated !== false || identity.RuntimeVersion !== binding.profile.runtimeVersion || identity.RuntimeCommit !== binding.profile.runtimeCommit || identity.RuntimeImageId !== binding.imageId || identity.Rid !== binding.profile.rid || identity.Architecture !== binding.profile.architecture) fail('Supervisor Run terminal state or identity does not match the Framework binding.')
  const stdout = Buffer.concat(events.filter(event => event.Payload.Kind === 'output-chunk' && event.Payload.Chunk?.Channel === 'stdout').map(event => { const chunk = event.Payload.Chunk; if (chunk.Encoding !== 'utf-8') fail('Supervisor stdout chunk encoding is not utf-8.'); return decodeCanonicalBase64(chunk.Data) })).toString('utf8')
  if (!stdout.includes('SLN-FRAMEWORK-SUPERVISOR-V1') || !stdout.includes('first') || !stdout.includes('second')) fail('Supervisor Run stdout did not preserve the fixed marker and arguments.')
  return { operationId, artifactRef: consoleArtifact.artifactRef, identity, stdoutMarker: 'SLN-FRAMEWORK-SUPERVISOR-V1' }
}

export async function runRuntimeFrameworkSupervisorSmoke(options = {}) {
  const prepared = prepareRuntimeFrameworkSupervisorSmoke(options)
  if (options.prepareOnly) return prepared
  if (typeof options.tokenFile !== 'string') fail('Live smoke requires an internal token file.')
  const fetch = options.fetch ?? globalThis.fetch; if (typeof fetch !== 'function') fail('A fetch implementation is required.')
  const now = options.now ?? (() => new Date()); const sleep = options.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)))
  const maxPolls = options.maxPolls ?? 120; const delay = options.pollDelayMilliseconds ?? 500
  if (!Number.isSafeInteger(maxPolls) || maxPolls < 1 || !Number.isSafeInteger(delay) || delay < 1) fail('Polling values must be positive integers.')
  const token = readRuntimeFrameworkSupervisorTokenFile(options.tokenFile); const roslynUrl = url(options.roslynWorkerUrl, 'Roslyn worker URL'); const supervisorUrl = url(options.supervisorUrl, 'Supervisor URL')
  const consoleArtifact = await buildConsole(fetch, token, roslynUrl, prepared.binding, now)
  const evidence = await supervisorRun(fetch, token, supervisorUrl, prepared.binding, consoleArtifact, now, sleep, maxPolls, delay)
  const row = prepared.results.rows.find(value => value.profileId === options.profileId)
  row.verification = { ...row.verification, evidence: { ...(object(row.verification?.evidence) ? row.verification.evidence : {}), supervisorOneShot: { ...evidence, profileSha256: row.profileSha256, imageId: prepared.binding.imageId, roslynBuildIdentity: consoleArtifact.identity, observedAt: now().toISOString() } } }
  prepared.results.verificationRefreshedAt = now().toISOString(); writeAtomic(options.resultsPath ?? defaultResults, prepared.results)
  return { ...prepared, evidence }
}

function parseArguments(argv) {
  if (argv.length === 1 && (argv[0] === '--help' || argv[0] === '-h')) return { help: true }
  const parsed = {}; const names = new Map([['--profile', 'profileId'], ['--supervisor', 'supervisorUrl'], ['--roslyn-worker', 'roslynWorkerUrl'], ['--token-file', 'tokenFile'], ['--results', 'resultsPath'], ['--overlay', 'overlayPath']])
  for (let index = 0; index < argv.length; index++) { const option = argv[index]; if (option === '--prepare-only') { if (parsed.prepareOnly) fail("Duplicate option '--prepare-only'."); parsed.prepareOnly = true; continue }; const field = names.get(option); const value = argv[++index]; if (field === undefined || value === undefined || value.length === 0 || parsed[field] !== undefined) fail(`Invalid or duplicate option '${option}'.`); parsed[field] = value }
  if (parsed.profileId === undefined) fail('Missing required profileId.')
  if (!parsed.prepareOnly) for (const field of ['supervisorUrl', 'roslynWorkerUrl']) if (parsed[field] === undefined) fail(`Missing required ${field}.`)
  return parsed
}
export async function runRuntimeFrameworkSupervisorSmokeCli(argv, options = {}) {
  const output = options.output ?? console
  try { const parsed = parseArguments(argv); if (parsed.help) { output.log(runtimeFrameworkSupervisorSmokeUsage); return 0 }; const tokenFile = parsed.prepareOnly ? undefined : parsed.tokenFile ?? process.env.SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE; if (!parsed.prepareOnly && tokenFile === undefined) fail('A token file is required; raw environment and command-line tokens are not accepted.'); const result = await runRuntimeFrameworkSupervisorSmoke({ ...options, ...parsed, tokenFile }); output.log(parsed.prepareOnly ? `Prepared Supervisor overlay for ${parsed.profileId}.` : `Supervisor one-shot smoke passed for ${parsed.profileId}.`); return result } catch (error) { output.error(`runtime Framework Supervisor smoke error: ${error.message}`); return 1 }
}
if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = (await runRuntimeFrameworkSupervisorSmokeCli(process.argv.slice(2))) === 1 ? 1 : 0
