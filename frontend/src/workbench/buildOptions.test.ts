import { describe, expect, it } from 'vitest'
import { createWorkbenchBuildOptions, retainResolvedWorkbenchOutputKind } from './buildOptions'

const buildStages = [{ id: 'build', kind: 'build', providerId: 'compiler' }] as const
const runStages = [...buildStages, { id: 'run', kind: 'run', providerId: 'runtime' }] as const

describe('createWorkbenchBuildOptions', () => {
  it.each([
    'csharp',
  ] as const)('enables every feature exposed by the selected C# compiler', (languageId) => {
    expect(createWorkbenchBuildOptions(languageId, 'release', buildStages)).toEqual(
      expect.objectContaining({
        configuration: 'release',
        optimize: true,
        outputKind: 'auto',
        allowUnsafe: true,
        languageVersion: 'preview',
      }),
    )
  })

  it.each([
    'csharp',
    'visual-basic',
    'gsharp',
  ] as const)('lets the %s worker choose between a library and top-level program for non-Run output', (languageId) => {
    expect(createWorkbenchBuildOptions(languageId, 'debug', buildStages).outputKind).toBe('auto')
  })

  it.each([
    'csharp',
    'visual-basic',
    'fsharp',
    'gsharp',
    'il',
  ] as const)('requires an entry point for %s when the resolved pipeline executes the program', (languageId) => {
    expect(createWorkbenchBuildOptions(languageId, 'debug', runStages).outputKind).toBe('console')
  })

  it('keeps top-level F# source executable for non-Run artifact outputs', () => {
    expect(createWorkbenchBuildOptions('fsharp', 'debug', buildStages).outputKind).toBe('console')
  })

  it.each([
    'il',
  ] as const)('builds %s as a library when the resolved pipeline does not execute the program', (languageId) => {
    expect(createWorkbenchBuildOptions(languageId, 'debug', buildStages).outputKind).toBe('library')
  })

  it.each([
    'visual-basic',
    'fsharp',
    'il',
    'php',
    'gsharp',
    'cpp-cli',
  ] as const)('does not impose the C# language version on %s', (languageId) => {
    const options = createWorkbenchBuildOptions(languageId, 'debug', buildStages)
    expect(options).toEqual(expect.objectContaining({ configuration: 'debug', optimize: false }))
    expect(options.allowUnsafe).toBe(false)
    expect(options.outputKind).toBe(
      languageId === 'visual-basic'
        ? 'auto'
        : languageId === 'gsharp'
          ? 'auto'
          : languageId === 'il'
            ? 'library'
            : 'console',
    )
    expect(options).not.toHaveProperty('languageVersion')
  })

  it('sends only supported general options to the x64 J# compiler', () => {
    const options = createWorkbenchBuildOptions('jsharp', 'release', buildStages)

    expect(options).toEqual({
      configuration: 'release',
      optimize: true,
      outputKind: 'console',
    })
    expect(JSON.parse(JSON.stringify(options))).not.toEqual(
      expect.objectContaining({
        allowUnsafe: expect.anything(),
        nullableContext: expect.anything(),
        languageVersion: expect.anything(),
      }),
    )
  })
})

describe('retainResolvedWorkbenchOutputKind', () => {
  const selection = {
    languageId: 'csharp',
    toolchainId: 'roslyn-stable',
    referenceSetId: 'net10-ref',
    buildMode: 'release',
    selectionRevision: 4,
  } as const

  it('retains a resolved output kind only while session identity and revision stay unchanged', () => {
    const resolved = retainResolvedWorkbenchOutputKind(selection, 'auto', null)

    expect(retainResolvedWorkbenchOutputKind(selection, null, resolved.remembered)).toEqual(
      resolved,
    )
    expect(
      retainResolvedWorkbenchOutputKind(
        { ...selection, selectionRevision: 5 },
        null,
        resolved.remembered,
      ),
    ).toEqual({ outputKind: 'console', remembered: null })
    expect(
      retainResolvedWorkbenchOutputKind(
        { ...selection, toolchainId: 'roslyn-main' },
        null,
        resolved.remembered,
      ),
    ).toEqual({ outputKind: 'console', remembered: null })
  })
})
