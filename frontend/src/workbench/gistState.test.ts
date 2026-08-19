import { describe, expect, it } from 'vitest'
import type { GistDocument } from '../api/types'
import { createCatalogFixture } from '../test/catalogFixture'
import { createGistWorkspaceState, decodeWorkbenchGist } from './gistState'

describe('workbench Gist state', () => {
  it('creates versioned resolved multi-file state', () => {
    const catalog = createCatalogFixture()
    const state = createGistWorkspaceState(catalog, {
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

    expect(state).toMatchObject({
      schemaVersion: 1,
      toolchainId: 'roslyn-stable',
      referenceSetId: 'net10-ref',
      activeFile: 'Other.cs',
    })
  })

  it('excludes browser-local editor preference from Gist serialization', () => {
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
      editor: 'monaco' as const,
    }

    const state = createGistWorkspaceState(catalog, source)

    expect(state).not.toHaveProperty('editor')
    expect(JSON.stringify({ workspace: state })).not.toContain('"editor"')
  })

  it('restores a Gist from an earlier release without synthesizing a release notice', () => {
    const catalog = createCatalogFixture()
    const workspace = createGistWorkspaceState(catalog, {
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
    workspace.releaseId = 'earlier-release'
    const document: GistDocument = {
      id: 'earlier-release-gist',
      htmlUrl: 'https://gist.github.com/earlier-release-gist',
      ownerLogin: 'owner',
      isPublic: true,
      canUpdate: false,
      description: 'Earlier release',
      sourceFormat: 'sharplabnext-v1',
      workspace,
      warnings: ['Server-provided notice.'],
    }

    const decoded = decodeWorkbenchGist(document, catalog)

    expect(decoded.warnings).toEqual(['Server-provided notice.'])
    expect(decoded.replacement.files[0]?.text).toBe('class FromEarlierRelease {}')
  })

  it('normalizes a legacy branch through catalog aliases without dropping source', () => {
    const catalog = createCatalogFixture()
    const toolchain = catalog.toolchains[0]
    if (!toolchain) throw new Error('Missing fixture toolchain.')
    toolchain.legacyAliases = ['main']
    const document: GistDocument = {
      id: 'abcdef',
      htmlUrl: 'https://gist.github.com/abcdef',
      ownerLogin: 'owner',
      isPublic: true,
      canUpdate: false,
      description: 'legacy',
      sourceFormat: 'sharplab-v1',
      workspace: {
        schemaVersion: 1,
        languageId: 'csharp',
        outputId: 'il',
        buildMode: 'release',
        activeFile: 'Program.cs',
        sourceOrder: ['Program.cs'],
        files: [{ path: 'Program.cs', text: 'class Program {}' }],
        legacyBranchId: 'main',
      },
      warnings: ['legacy import'],
    }

    const decoded = decodeWorkbenchGist(document, catalog)

    expect(decoded.replacement.selection?.toolchainId).toBe('roslyn-stable')
    expect(decoded.replacement.files[0]?.text).toBe('class Program {}')
    expect(decoded.warnings).toContain('legacy import')
  })
})
