import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import { runRuntimeFrameworkGatewaySmoke } from './runtime-framework-gateway-smoke.mjs'

const hash = value => `sha256:${crypto.createHash('sha256').update(value).digest('hex')}`
const profileId = 'wine-netfx48-linux-x64'
const imageId = hash('runtime-image')
const marker = 'SLN-FRAMEWORK-GATEWAY-V1'

function response(value, status = 200, contentType = 'application/json') {
  return new Response(typeof value === 'string' ? value : JSON.stringify(value), { status, headers: { 'Content-Type': contentType } })
}

function fixture(t, options = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-gateway-')); t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const profileSha256 = hash('profile'); const resultsPath = path.join(directory, 'results.json')
  const results = { schemaVersion: 1, rows: [{ profileId, runtimeVersion: '4.8', profileSha256, image: { imageId }, verification: { status: 'smoke-passed', evidence: { supervisorOneShot: { profileSha256, imageId, identity: { RuntimeCommit: 'not-applicable', Rid: 'linux-x64', Architecture: 'x64' } } } } }] }
  fs.writeFileSync(resultsPath, `${JSON.stringify(results, null, 2)}\n`)
  const calls = []; const operations = new Map(); const contents = new Map()
  const complete = (id, result, output) => {
    const events = []; let sequence = 1
    if (output !== undefined) events.push({ OperationId: id, Sequence: sequence++, Payload: { Kind: 'output-chunk', Chunk: { Channel: 'stdout', Encoding: 'utf-8', Data: Buffer.from(output).toString('base64'), Truncated: false } } })
    events.push({ OperationId: id, Sequence: sequence++, Payload: { Kind: 'typed-result', Result: result } }, { OperationId: id, Sequence: sequence, Payload: { Kind: 'completed', Status: 'completed' } })
    operations.set(id, events)
  }
  const fetch = async (input, init = {}) => {
    const url = new URL(String(input)); const method = init.method ?? 'GET'; const body = init.body === undefined ? undefined : JSON.parse(init.body); calls.push({ url: url.pathname + url.search, method, body })
    if (url.pathname === '/api/v1/system') return response({ Id: 'gateway', ReleaseId: 'matrix' })
    if (url.pathname === '/api/v1/catalog') return response({ Revision: 'revision', ReleaseId: 'matrix', Runtimes: [{ Id: profileId, ResolvedVersion: '4.8', RuntimeImageId: imageId }] })
    if (url.pathname === '/api/v1/selections/resolve') {
      const outputId = body.OutputId; const terminal = outputId === 'run' ? { Id: 'run', Kind: 'run', ProviderId: profileId } : { Id: outputId, Kind: 'render', ProviderId: 'artifacts-default' }
      const selection = { LanguageId: 'csharp', ToolchainId: 'roslyn-stable-netfx48', ReferenceSetId: 'netfx48-managed-ref', OutputId: outputId }; if (body.RuntimeId !== null) selection.RuntimeId = body.RuntimeId
      return response({ EffectiveSelection: selection, PipelineResolutionId: `pipeline-${outputId}`, PipelinePlan: { Stages: [{ Id: 'build', Kind: 'build', ProviderId: 'roslyn-stable-netfx48' }, terminal], SecurityPolicyId: 'runtime-job-wine-netfx' } })
    }
    if (url.pathname === '/api/v1/builds') { const outputId = body.PipelineResolutionId.slice('pipeline-'.length); const id = `build-${outputId}`; complete(id, { ResultType: 'build', Outcome: 'succeeded', ArtifactRef: hash(`artifact-${outputId}`) }); return response({ OperationId: id }) }
    if (url.pathname === '/api/v1/artifact-renders') { const id = `render-${body.OutputId}`; const contentRef = hash(`content-${body.OutputId}`); contents.set(contentRef.slice(7), body.OutputId === 'il' ? `.method public static int32 Main() ${marker}` : `public static class Program { const string Value = "${marker}"; }`); complete(id, { ResultType: 'artifact-render', Outcome: 'succeeded', ContentRef: contentRef, Identity: { ProcessorId: 'artifacts-default' } }); return response({ OperationId: id }) }
    if (url.pathname === '/api/v1/runs') { const id = 'run'; complete(id, { ResultType: 'run', Status: 'completed', ExitCode: 0, OutputTruncated: false, Identity: { RuntimeVersion: '4.8', RuntimeCommit: 'not-applicable', RuntimeImageId: options.wrongRuntimeImageId ?? imageId, Rid: 'linux-x64', Architecture: 'x64' } }, `${marker}\nfirst\nsecond\n`); return response({ OperationId: id }) }
    const operationMatch = url.pathname.match(/^\/api\/v1\/operations\/([^/]+)$/)
    if (operationMatch) return response({ OperationId: operationMatch[1], Status: 'completed' })
    const eventMatch = url.pathname.match(/^\/api\/v1\/operations\/([^/]+)\/events$/)
    if (eventMatch) return response(operations.get(eventMatch[1]).map(event => `data: ${JSON.stringify(event)}\n\n`).join(''), 200, 'text/event-stream')
    const contentMatch = url.pathname.match(/^\/api\/v1\/operations\/[^/]+\/contents\/sha256\/([0-9a-f]{64})$/)
    if (contentMatch) return response(contents.get(contentMatch[1]), 200, 'text/plain')
    throw new Error(`Unexpected request ${method} ${url}`)
  }
  return { resultsPath, calls, fetch }
}

test('representative smoke crosses public selection, build, IL, decompile and Run contracts', async t => {
  const value = fixture(t); const evidence = await runRuntimeFrameworkGatewaySmoke({ gatewayUrl: 'http://test', profileId, resultsPath: value.resultsPath, fetch: value.fetch, sleep: async () => {} })
  assert.equal(evidence.imageId, imageId); assert.equal(evidence.run.identity.RuntimeImageId, imageId); assert.ok(evidence.il.contentRef.startsWith('sha256:')); assert.ok(evidence.decompiledCSharp.contentRef.startsWith('sha256:'))
  for (const call of value.calls.filter(call => call.method === 'POST')) assert.equal(Object.keys(call.body).every(key => /^[A-Z]/.test(key)), true)
  const stored = JSON.parse(fs.readFileSync(value.resultsPath)); assert.equal(stored.rows[0].verification.evidence.gatewayRepresentative.run.stdoutMarker, marker)
})

test('wrong public Run identity fails without replacing functional evidence', async t => {
  const value = fixture(t, { wrongRuntimeImageId: hash('wrong') }); const before = fs.readFileSync(value.resultsPath, 'utf8')
  await assert.rejects(runRuntimeFrameworkGatewaySmoke({ gatewayUrl: 'http://test', profileId, resultsPath: value.resultsPath, fetch: value.fetch, sleep: async () => {} }), /does not match/)
  assert.equal(fs.readFileSync(value.resultsPath, 'utf8'), before)
})
