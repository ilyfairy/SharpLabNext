import { describe, expect, it } from 'vitest'
import { type DecodedShare, decodeV3, encodeV3 } from '../share'
import { createCatalogFixture } from '../test/catalogFixture'
import { createShareWorkspaceState, decodeWorkbenchShare } from './shareState'

describe('workbench share state', () => {
  it('round-trips the current multi-file selection shape', () => {
    const catalog = createCatalogFixture()
    const state = createShareWorkspaceState(catalog, {
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'ast',
      runtimeId: null,
      buildMode: 'release',
      files: [
        { path: 'Program.cs', text: 'class Program {}' },
        { path: 'Other.cs', text: 'class Other {}' },
      ],
      activeFile: 'Other.cs',
      sourceOrder: ['Program.cs', 'Other.cs'],
    })

    expect(state.runtimeId).toBe('dotnet-10-linux-x64')
    expect(state.files).toHaveLength(2)
    expect(state.activeFile).toBe('Other.cs')
  })

  it('excludes browser-local editor preference from URL state', async () => {
    const catalog = createCatalogFixture()
    const source = {
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'ast',
      runtimeId: null,
      buildMode: 'release' as const,
      files: [{ path: 'Program.cs', text: 'class Program {}' }],
      activeFile: 'Program.cs',
      sourceOrder: ['Program.cs'],
      editor: 'codemirror' as const,
    }

    const state = createShareWorkspaceState(catalog, source)
    const canonical = await encodeV3(state)
    const widenedState: typeof state & { editor: 'codemirror' } = {
      ...state,
      editor: source.editor,
    }
    const widened = await encodeV3(widenedState)

    expect(state).not.toHaveProperty('editor')
    expect(widened.fragment).toBe(canonical.fragment)
    await expect(decodeV3(widened.fragment)).resolves.not.toHaveProperty('state.editor')
  })

  it('restores a URL from an earlier release without a release notice', async () => {
    const catalog = createCatalogFixture()
    const state = createShareWorkspaceState(catalog, {
      languageId: 'csharp',
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      outputId: 'ast',
      runtimeId: null,
      buildMode: 'release',
      files: [{ path: 'Program.cs', text: 'class FromEarlierRelease {}' }],
      activeFile: 'Program.cs',
      sourceOrder: ['Program.cs'],
    })
    state.releaseVersion = 'earlier-release'
    const encoded = await encodeV3(state)
    const decoded = await decodeV3(encoded.fragment)

    const result = decodeWorkbenchShare(decoded, catalog)

    expect(result.warnings).toEqual([])
    expect(result.replacement.files[0]?.text).toBe('class FromEarlierRelease {}')
    expect(result.replacement.selection?.toolchainId).toBe('roslyn-stable')
  })

  it('resolves SharpLab branch aliases through the catalog', () => {
    const catalog = createCatalogFixture()
    const toolchain = catalog.toolchains.at(0)
    expect(toolchain).toBeDefined()
    if (!toolchain) return
    toolchain.legacyAliases = ['main']
    const decoded: DecodedShare = {
      sourceFormat: 'sharplab-v2',
      workspace: {
        languageId: 'csharp',
        activeFile: 'Program.cs',
        sourceOrder: ['Program.cs'],
        files: [{ path: 'Program.cs', text: 'test code' }],
      },
      requestedLegacyOptions: {
        branchId: 'main',
        languageKey: 'cs',
        languageId: 'csharp',
        targetKey: 'ast',
        outputId: 'ast',
        buildMode: 'release',
      },
      resolvedSelection: null,
      warnings: [],
    }

    const result = decodeWorkbenchShare(decoded, catalog)

    expect(result.replacement.selection?.toolchainId).toBe('roslyn-stable')
    expect(result.replacement.files[0]?.text).toBe('test code')
  })
})
