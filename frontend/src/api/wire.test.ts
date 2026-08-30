import { describe, expect, it } from 'vitest'
import { decodeWire, encodeWire, stringifyWire } from './wire'

describe('application wire naming', () => {
  it('encodes nested camelCase members as PascalCase', () => {
    expect(
      encodeWire({
        operationId: 'op-1',
        request: { pipelineResolutionId: 'pipeline-1', workspaceRevision: 4 },
        payload: [{ createdAtUtc: 'now', isExisting: false }],
      }),
    ).toEqual({
      OperationId: 'op-1',
      Request: { PipelineResolutionId: 'pipeline-1', WorkspaceRevision: 4 },
      Payload: [{ CreatedAtUtc: 'now', IsExisting: false }],
    })
  })

  it('keeps enum/discriminator values and dynamic syntax property keys unchanged', () => {
    expect(
      encodeWire({
        kind: 'typed-result',
        resultType: 'ast',
        properties: { IsStatic: 'true', customName: null },
      }),
    ).toEqual({
      Kind: 'typed-result',
      ResultType: 'ast',
      Properties: { IsStatic: 'true', customName: null },
    })
  })

  it('does not rewrite keys inside contract dictionary members', () => {
    expect(
      encodeWire({
        metadata: { compilerVersion: '1.0', 'System.Console': 'reference' },
        fileContentsBase64: { 'Program.cs': 'AA==' },
      }),
    ).toEqual({
      Metadata: { compilerVersion: '1.0', 'System.Console': 'reference' },
      FileContentsBase64: { 'Program.cs': 'AA==' },
    })

    expect(
      decodeWire({
        Metadata: { compilerVersion: '1.0' },
        FileContentsBase64: { 'Program.cs': 'AA==' },
      }),
    ).toEqual({
      metadata: { compilerVersion: '1.0' },
      fileContentsBase64: { 'Program.cs': 'AA==' },
    })
  })

  it('preserves keyed identity and readiness maps while converting their values', () => {
    expect(
      decodeWire({
        Identity: { customProviderKey: 'value' },
        Dependencies: {
          'language:roslyn-stable': {
            Ready: true,
            RuntimeProfileIds: ['net10'],
          },
        },
      }),
    ).toEqual({
      identity: { customProviderKey: 'value' },
      dependencies: {
        'language:roslyn-stable': { ready: true, runtimeProfileIds: ['net10'] },
      },
    })

    expect(
      encodeWire({
        identity: { customProviderKey: 'value' },
        dependencies: { 'language:roslyn-stable': { ready: true } },
      }),
    ).toEqual({
      Identity: { customProviderKey: 'value' },
      Dependencies: { 'language:roslyn-stable': { Ready: true } },
    })
  })

  it('decodes the canonical PascalCase response shape into the app model', () => {
    expect(
      decodeWire<{
        operationId: string
        payload: {
          resultType: string
          properties: Record<string, string | null>
        }
      }>({
        OperationId: 'op-1',
        Payload: { ResultType: 'ast', Properties: { IsStatic: 'true' } },
      }),
    ).toEqual({
      operationId: 'op-1',
      payload: { resultType: 'ast', properties: { IsStatic: 'true' } },
    })
  })

  it('rejects lower-camel members instead of silently accepting a legacy response', () => {
    expect(() => decodeWire({ operationId: 'legacy' })).toThrow("Invalid SharpLabNext wire member 'operationId'")
    expect(() =>
      decodeWire({
        OperationId: 'op-1',
        Request: { pipelineResolutionId: 'legacy' },
      }),
    ).toThrow("Invalid SharpLabNext wire member 'pipelineResolutionId'")
  })

  it('serializes only the PascalCase application representation', () => {
    expect(stringifyWire({ requestId: 'r-1', outputId: 'il' })).toBe('{"RequestId":"r-1","OutputId":"il"}')
  })

  it('converts fixed operation identities while preserving provider identity maps', () => {
    expect(
      decodeWire({
        Identity: {
          RuntimeVersion: '11.0.0',
          RuntimeImageId: 'sha256:image',
          JitVersion: '11.0.0-jit',
          JitProvider: 'coreclr-jitdisasm',
        },
      }),
    ).toEqual({
      identity: {
        runtimeVersion: '11.0.0',
        runtimeImageId: 'sha256:image',
        jitVersion: '11.0.0-jit',
        jitProvider: 'coreclr-jitdisasm',
      },
    })

    expect(
      encodeWire({
        identity: {
          runtimeVersion: '11.0.0',
          runtimeImageId: 'sha256:image',
          jitVersion: '11.0.0-jit',
          jitProvider: 'coreclr-jitdisasm',
        },
      }),
    ).toEqual({
      Identity: {
        RuntimeVersion: '11.0.0',
        RuntimeImageId: 'sha256:image',
        JitVersion: '11.0.0-jit',
        JitProvider: 'coreclr-jitdisasm',
      },
    })

    expect(
      encodeWire({
        identity: { runtimeVersion: 'provider-value', customKey: 'keep-as-is' },
      }),
    ).toEqual({
      Identity: { runtimeVersion: 'provider-value', customKey: 'keep-as-is' },
    })
  })
})
