/** Public Gateway representative smoke for the verified Wine .NET Framework path. */
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const defaultResults = path.join(root, '.tmp', 'runtime-matrix-functional-results.json')
const digestPattern = /^sha256:[0-9a-f]{64}$/
const idPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const marker = 'SLN-FRAMEWORK-GATEWAY-V1'
const maxJsonBytes = 4 * 1024 * 1024
const maxContentBytes = 16 * 1024 * 1024

export const runtimeFrameworkGatewaySmokeUsage = `Usage:
  node eng/smoke/runtime-framework-gateway-smoke.mjs --gateway URL
    --profile wine-netfx48-linux-x64 [--results PATH]`

export class RuntimeFrameworkGatewaySmokeError extends Error {
  constructor(message, options) { super(message, options); this.name = 'RuntimeFrameworkGatewaySmokeError' }
}

function fail(message, options) { throw new RuntimeFrameworkGatewaySmokeError(message, options); }
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value) }
function string(value, label) { if (typeof value !== 'string' || value.length === 0) fail(`${label} must be a non-empty string.`); return value }
function digest(value, label) { if (!digestPattern.test(value ?? '')) fail(`${label} must be a sha256 identity.`); return value }
function pascal(value, label) { if (!object(value)) fail(`${label} must be an object.`); for (const key of Object.keys(value)) if (!/^[A-Z]/.test(key)) fail(`${label} contains non-PascalCase property '${key}'.`); return value }

function readResults(filename) {
  const resolved = path.resolve(filename); let stat
  try { stat = fs.lstatSync(resolved) } catch (error) { fail(`Functional results could not be inspected: ${error.message}`, { cause: error }) }
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 1 || stat.size > 16 * 1024 * 1024) fail('Functional results must be a bounded regular non-link file.')
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(fs.readFileSync(resolved))) } catch (error) { fail(`Functional results are invalid JSON: ${error.message}`, { cause: error }) }
}

function writeAtomic(filename, value) {
  const resolved = path.resolve(filename); const temporary = path.join(path.dirname(resolved), `.${path.basename(resolved)}.${process.pid}.${crypto.randomBytes(8).toString('hex')}.tmp`)
  try { fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, { flag: 'wx' }); fs.renameSync(temporary, resolved) } finally { fs.rmSync(temporary, { force: true }) }
}

function gatewayUrl(value) {
  let result; try { result = new URL(value) } catch { fail('Gateway URL must be an absolute HTTP URL.') }
  if (!['http:', 'https:'].includes(result.protocol) || result.username || result.password || result.search || result.hash) fail('Gateway URL must be an HTTP(S) origin without credentials, query, or fragment.')
  return result.toString().replace(/\/$/, '')
}

async function bounded(response, label, maximum = maxJsonBytes) {
  const declared = Number(response.headers.get('Content-Length')); if (Number.isFinite(declared) && declared > maximum) fail(`${label} exceeded its byte limit.`)
  const bytes = Buffer.from(await response.arrayBuffer()); if (bytes.length > maximum) fail(`${label} exceeded its byte limit.`)
  return bytes
}

async function jsonRequest(fetch, url, init, label) {
  const headers = new Headers(init?.headers); headers.set('Accept', 'application/json'); if (init?.body !== undefined) headers.set('Content-Type', 'application/json')
  let response; try { response = await fetch(url, { ...init, headers, signal: AbortSignal.timeout(15_000) }) } catch (error) { fail(`${label} request failed: ${error.message}`, { cause: error }) }
  const bytes = await bounded(response, label); const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  if (!response.ok) fail(`${label} returned HTTP ${response.status}: ${text.slice(0, 500)}`)
  try { return JSON.parse(text) } catch (error) { fail(`${label} returned invalid JSON: ${error.message}`, { cause: error }) }
}

function requestId(prefix) { return `runtime-framework-gateway-${prefix}-${crypto.randomUUID().replaceAll('-', '')}` }
function canonicalBase64(value) { if (typeof value !== 'string' || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) fail('Gateway output chunk is not canonical base64.'); const bytes = Buffer.from(value, 'base64'); if (bytes.toString('base64') !== value) fail('Gateway output chunk is not canonical base64.'); return bytes }

function parseEvents(text, operationId) {
  const events = []
  for (const line of text.replace(/\r/g, '').split('\n')) if (line.startsWith('data: ')) { let event; try { event = JSON.parse(line.slice(6)) } catch (error) { fail(`Gateway operation events are invalid JSON: ${error.message}`, { cause: error }) }; events.push(pascal(event, 'Gateway operation event')) }
  let sequence = 0
  for (const event of events) { if (event.OperationId !== operationId || !Number.isSafeInteger(event.Sequence) || event.Sequence <= sequence || !object(event.Payload)) fail('Gateway operation event identity or sequence is invalid.'); sequence = event.Sequence }
  if (events.length === 0) fail(`Gateway operation '${operationId}' returned no events.`)
  return events
}

async function startAndWait(fetch, base, path_, request, sleep, maxPolls) {
  const handle = pascal(await jsonRequest(fetch, `${base}${path_}`, { method: 'POST', body: JSON.stringify(request) }, path_), `${path_} handle`)
  const operationId = string(handle.OperationId, `${path_} OperationId`)
  let state
  for (let attempt = 0; attempt < maxPolls; attempt++) { state = pascal(await jsonRequest(fetch, `${base}/api/v1/operations/${encodeURIComponent(operationId)}`, { method: 'GET' }, `Operation ${operationId}`), 'Gateway operation state'); if (state.OperationId !== operationId) fail('Gateway operation state returned a different ID.'); if (state.Status === 'completed') break; if (state.Status === 'failed' || state.Status === 'cancelled') fail(`Gateway operation '${operationId}' ended as ${state.Status}: ${state.Error?.PublicMessage ?? 'no public error'}`); await sleep(250) }
  if (state?.Status !== 'completed') fail(`Gateway operation '${operationId}' did not complete before polling expired.`)
  let response; try { response = await fetch(`${base}/api/v1/operations/${encodeURIComponent(operationId)}/events?FromSequence=0`, { headers: { Accept: 'text/event-stream' }, signal: AbortSignal.timeout(15_000) }) } catch (error) { fail(`Gateway operation events failed: ${error.message}`, { cause: error }) }
  const eventBytes = await bounded(response, `Gateway operation ${operationId} events`); if (!response.ok) fail(`Gateway operation events returned HTTP ${response.status}.`)
  const events = parseEvents(new TextDecoder('utf-8', { fatal: true }).decode(eventBytes), operationId)
  const typed = events.filter(event => event.Payload.Kind === 'typed-result')
  if (typed.length !== 1 || !events.some(event => event.Payload.Kind === 'completed' && event.Payload.Status === 'completed')) fail(`Gateway operation '${operationId}' has no unique completed typed result.`)
  return { operationId, result: pascal(typed[0].Payload.Result, 'Gateway typed result'), events }
}

function workspace(referenceSetId) {
  const options = { Configuration: 'release', Optimize: true, OutputKind: 'console', AllowUnsafe: false, EmitPortablePdb: true, NullableContext: 'enable', LanguageVersion: '14.0', PreprocessorSymbols: [], CheckOverflow: false }
  return { SchemaVersion: 1, Revision: 1, SelectionRevision: 1, LanguageId: 'csharp', Files: [{ Path: 'Program.cs', Version: 1, Text: `using System; public static class Program { public static int Main(string[] args) { Console.WriteLine("${marker}"); foreach (var arg in args) Console.WriteLine(arg); return 0; } }` }], ActiveFile: 'Program.cs', SourceOrder: ['Program.cs'], ReferenceSetId: referenceSetId, BuildOptions: options }
}

async function resolve(fetch, base, catalog, outputId, profileId) {
  const request = { LanguageId: 'csharp', ToolchainId: 'roslyn-stable-netfx48', ReferenceSetId: 'netfx48-managed-ref', OutputId: outputId, RuntimeId: outputId === 'run' ? profileId : null, BuildMode: 'release', CatalogRevision: catalog.Revision, WorkspaceRevision: 1 }
  const response = pascal(await jsonRequest(fetch, `${base}/api/v1/selections/resolve`, { method: 'POST', body: JSON.stringify(request) }, `Resolve ${outputId}`), `Resolve ${outputId}`)
  const selection = pascal(response.EffectiveSelection, `Resolve ${outputId}.EffectiveSelection`)
  if (selection.ToolchainId !== request.ToolchainId || selection.ReferenceSetId !== request.ReferenceSetId || selection.OutputId !== outputId || (selection.RuntimeId ?? null) !== request.RuntimeId || !Array.isArray(response.PipelinePlan?.Stages)) fail(`Gateway resolved an unexpected '${outputId}' selection.`)
  return response
}

async function build(fetch, base, resolution, sleep, maxPolls) {
  const id = requestId('build'); const selection = resolution.EffectiveSelection; const source = workspace(selection.ReferenceSetId)
  const execution = await startAndWait(fetch, base, '/api/v1/builds', { RequestId: id, IdempotencyKey: id, PipelineResolutionId: resolution.PipelineResolutionId, ToolchainId: selection.ToolchainId, ReferenceSetId: selection.ReferenceSetId, Workspace: source, DeadlineUtc: new Date(Date.now() + 60_000).toISOString(), Options: source.BuildOptions, Target: 'artifact' }, sleep, maxPolls)
  if (execution.result.ResultType !== 'build' || execution.result.Outcome !== 'succeeded') fail('Gateway Framework build did not succeed.')
  return { ...execution, artifactRef: digest(execution.result.ArtifactRef, 'Gateway BuildResult.ArtifactRef') }
}

async function content(fetch, base, execution) {
  const reference = digest(execution.result.ContentRef, 'Gateway RenderArtifactResult.ContentRef'); const digestValue = reference.slice('sha256:'.length)
  let response; try { response = await fetch(`${base}/api/v1/operations/${encodeURIComponent(execution.operationId)}/contents/sha256/${digestValue}`, { signal: AbortSignal.timeout(15_000) }) } catch (error) { fail(`Gateway result content request failed: ${error.message}`, { cause: error }) }
  const bytes = await bounded(response, 'Gateway result content', maxContentBytes); if (!response.ok) fail(`Gateway result content returned HTTP ${response.status}.`)
  return { reference, text: new TextDecoder('utf-8', { fatal: true }).decode(bytes) }
}

async function renderPipeline(fetch, base, catalog, outputId, profileId, sleep, maxPolls) {
  const resolution = await resolve(fetch, base, catalog, outputId, profileId); const stages = resolution.PipelinePlan.Stages
  if (stages.length !== 2 || stages[0].Kind !== 'build' || stages[1].Kind !== 'render' || stages[1].Id !== outputId) fail(`Gateway '${outputId}' pipeline has an unexpected plan.`)
  const compiled = await build(fetch, base, resolution, sleep, maxPolls); const id = requestId('render')
  const rendered = await startAndWait(fetch, base, '/api/v1/artifact-renders', { RequestId: id, IdempotencyKey: id, PipelineResolutionId: resolution.PipelineResolutionId, ArtifactRef: compiled.artifactRef, ProcessorId: stages[1].ProviderId, OutputId: outputId, Options: { IncludeSequencePoints: true, IncludeCompilerGeneratedMembers: true, MaxCharacters: 1_000_000 }, DeadlineUtc: new Date(Date.now() + 60_000).toISOString() }, sleep, maxPolls)
  if (rendered.result.ResultType !== 'artifact-render' || rendered.result.Outcome !== 'succeeded' || rendered.result.Identity?.ProcessorId !== 'artifacts-default') fail(`Gateway '${outputId}' render did not succeed through artifacts-default.`)
  const renderedContent = await content(fetch, base, rendered)
  if (!renderedContent.text.includes(marker) || (outputId === 'il' ? !renderedContent.text.includes('.method') : !renderedContent.text.includes('class Program'))) fail(`Gateway '${outputId}' content does not contain the expected Framework program.`)
  return { operationId: rendered.operationId, artifactRef: compiled.artifactRef, contentRef: renderedContent.reference }
}

async function runPipeline(fetch, base, catalog, binding, sleep, maxPolls) {
  const resolution = await resolve(fetch, base, catalog, 'run', binding.profileId); const stages = resolution.PipelinePlan.Stages
  if (stages.length !== 2 || stages[0].Kind !== 'build' || stages[1].Kind !== 'run') fail("Gateway 'run' pipeline has an unexpected plan.")
  const compiled = await build(fetch, base, resolution, sleep, maxPolls); const id = requestId('run')
  const execution = await startAndWait(fetch, base, '/api/v1/runs', { RequestId: id, IdempotencyKey: id, PipelineResolutionId: resolution.PipelineResolutionId, ArtifactRef: compiled.artifactRef, RuntimeProfileId: binding.profileId, Options: { Arguments: ['first', 'second'], Stdin: null, Instrumentation: 'none', SecurityPolicyId: resolution.PipelinePlan.SecurityPolicyId }, DeadlineUtc: new Date(Date.now() + 60_000).toISOString() }, sleep, maxPolls)
  const result = execution.result; const identity = pascal(result.Identity, 'Gateway RunResult.Identity')
  if (result.ResultType !== 'run' || result.Status !== 'completed' || result.ExitCode !== 0 || result.OutputTruncated !== false || identity.RuntimeImageId !== binding.imageId || identity.RuntimeVersion !== binding.runtimeVersion || identity.RuntimeCommit !== binding.runtimeCommit || identity.Rid !== binding.rid || identity.Architecture !== binding.architecture) fail('Gateway RunResult does not match the verified Framework runtime.')
  const stdout = Buffer.concat(execution.events.filter(event => event.Payload.Kind === 'output-chunk' && event.Payload.Chunk?.Channel === 'stdout').map(event => { if (event.Payload.Chunk.Encoding !== 'utf-8') fail('Gateway stdout encoding is not utf-8.'); return canonicalBase64(event.Payload.Chunk.Data) })).toString('utf8')
  if (!stdout.includes(marker) || !stdout.includes('first') || !stdout.includes('second')) fail('Gateway Run stdout did not preserve the marker and arguments.')
  return { operationId: execution.operationId, artifactRef: compiled.artifactRef, identity, stdoutMarker: marker }
}

function selectBinding(results, profileId) {
  if (results?.schemaVersion !== 1 || !Array.isArray(results.rows)) fail('Functional results must use schema version 1 with rows.')
  const matches = results.rows.filter(row => row?.profileId === profileId); if (matches.length !== 1) fail(`Functional results must contain exactly one row '${profileId}'.`)
  const row = matches[0]; const imageId = digest(row.image?.imageId, `Row '${profileId}' image ID`); const supervisor = row.verification?.evidence?.supervisorOneShot
  if (row.verification?.status !== 'smoke-passed' || !object(supervisor) || supervisor.imageId !== imageId || supervisor.profileSha256 !== row.profileSha256) fail(`Row '${profileId}' has no current Supervisor evidence.`)
  return { row, profileId, imageId, runtimeVersion: row.runtimeVersion, runtimeCommit: supervisor.identity?.RuntimeCommit, rid: supervisor.identity?.Rid, architecture: supervisor.identity?.Architecture }
}

export async function runRuntimeFrameworkGatewaySmoke(options = {}) {
  if (!idPattern.test(options.profileId ?? '')) fail('Gateway smoke profile ID must be a safe ID.')
  const resultsPath = options.resultsPath ?? defaultResults; const results = readResults(resultsPath); const binding = selectBinding(results, options.profileId)
  const fetch = options.fetch ?? globalThis.fetch; if (typeof fetch !== 'function') fail('A fetch implementation is required.'); const base = gatewayUrl(options.gatewayUrl); const sleep = options.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))); const maxPolls = options.maxPolls ?? 240
  const started = Date.now(); const system = pascal(await jsonRequest(fetch, `${base}/api/v1/system`, { method: 'GET' }, 'Gateway system'), 'Gateway system'); const catalog = pascal(await jsonRequest(fetch, `${base}/api/v1/catalog`, { method: 'GET' }, 'Gateway catalog'), 'Gateway catalog')
  const runtimes = Array.isArray(catalog.Runtimes) ? catalog.Runtimes.filter(runtime => runtime?.Id === binding.profileId) : []
  if (system.Id !== 'gateway' || system.ReleaseId !== catalog.ReleaseId || runtimes.length !== 1 || runtimes[0].RuntimeImageId !== binding.imageId || runtimes[0].ResolvedVersion !== binding.runtimeVersion) fail('Gateway system/Catalog does not expose the verified Framework binding.')
  const il = await renderPipeline(fetch, base, catalog, 'il', binding.profileId, sleep, maxPolls)
  const decompiledCSharp = await renderPipeline(fetch, base, catalog, 'decompiled-csharp', binding.profileId, sleep, maxPolls)
  const run = await runPipeline(fetch, base, catalog, binding, sleep, maxPolls)
  const evidence = { observedAt: new Date().toISOString(), releaseId: catalog.ReleaseId, catalogRevision: catalog.Revision, profileSha256: binding.row.profileSha256, imageId: binding.imageId, wallElapsedMilliseconds: Date.now() - started, il, decompiledCSharp, run }
  binding.row.verification = { ...binding.row.verification, evidence: { ...binding.row.verification.evidence, gatewayRepresentative: evidence } }; results.verificationRefreshedAt = evidence.observedAt; writeAtomic(resultsPath, results)
  return evidence
}

function parseArguments(argv) { if (argv.length === 1 && (argv[0] === '--help' || argv[0] === '-h')) return { help: true }; const result = {}; const names = new Map([['--gateway', 'gatewayUrl'], ['--profile', 'profileId'], ['--results', 'resultsPath']]); for (let index = 0; index < argv.length; index++) { const option = argv[index]; const field = names.get(option); const value = argv[++index]; if (field === undefined || value === undefined || value.length === 0 || result[field] !== undefined) fail(`Invalid or duplicate option '${option}'.`); result[field] = value }; for (const field of ['gatewayUrl', 'profileId']) if (result[field] === undefined) fail(`Missing required ${field}.`); return result }
export async function runRuntimeFrameworkGatewaySmokeCli(argv, options = {}) { const output = options.output ?? console; try { const parsed = parseArguments(argv); if (parsed.help) { output.log(runtimeFrameworkGatewaySmokeUsage); return 0 }; await runRuntimeFrameworkGatewaySmoke({ ...options, ...parsed }); output.log(`Gateway Framework representative smoke passed for ${parsed.profileId}.`); return 0 } catch (error) { output.error(`runtime Framework Gateway smoke error: ${error.message}`); return 1 } }

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = await runRuntimeFrameworkGatewaySmokeCli(process.argv.slice(2))
