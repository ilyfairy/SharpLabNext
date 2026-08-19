import { describe, expect, it } from 'vitest'
import { createLanguageDocumentUri, createLanguageWorkspaceUri } from './languageDocumentUri'

describe('language document URIs', () => {
  it('uses the relative path-only namespace required by IL', () => {
    const workspaceUri = createLanguageWorkspaceUri('il', 'workspace-test')

    expect(workspaceUri).toBe('sharplabnext:///')
    expect(createLanguageDocumentUri(workspaceUri, 'Program.il')).toBe('sharplabnext:///Program.il')
  })

  it('keeps workspace authorities for other languages', () => {
    const workspaceUri = createLanguageWorkspaceUri('csharp', 'workspace-test')

    expect(workspaceUri).toBe('sharplabnext://workspace-test/')
    expect(createLanguageDocumentUri(workspaceUri, 'src/My File.cs')).toBe(
      'sharplabnext://workspace-test/src/My%20File.cs',
    )
  })

  it('encodes punctuation in each path segment', () => {
    expect(createLanguageDocumentUri('sharplabnext:///', 'Folder/a#b.il')).toBe(
      'sharplabnext:///Folder/a%23b.il',
    )
  })
})
