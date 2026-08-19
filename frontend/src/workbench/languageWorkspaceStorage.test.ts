import { beforeEach, describe, expect, it } from 'vitest'
import type { LanguageManifest } from '../api/types'
import {
  languageWorkspaceStorageKey,
  readLanguageWorkspace,
  writeLanguageWorkspace,
} from './languageWorkspaceStorage'

const php: LanguageManifest = {
  id: 'php',
  displayName: 'PHP',
  monacoLanguageId: 'php',
  extensions: ['.php'],
  defaultFileName: 'index.php',
  defaultSource: '<?php echo 49;\n',
  defaultToolchainId: 'peachpie-stable',
  capabilities: ['multi-file'],
  legacyAliases: ['php'],
}

describe('language workspace storage', () => {
  beforeEach(() => localStorage.clear())

  it('round-trips a valid workspace without exposing mutable storage references', () => {
    writeLanguageWorkspace(localStorage, 'php', {
      files: [{ path: 'index.php', text: '<?php echo 7;\n' }],
      activeFile: 'index.php',
      sourceOrder: ['index.php'],
    })

    const restored = readLanguageWorkspace(localStorage, php)
    expect(restored).toEqual({
      files: [{ path: 'index.php', text: '<?php echo 7;\n' }],
      activeFile: 'index.php',
      sourceOrder: ['index.php'],
    })
    restored?.files.push({ path: 'other.php', text: '' })
    expect(readLanguageWorkspace(localStorage, php)?.files).toHaveLength(1)
  })

  it('rejects a legacy cross-language cache with the wrong file extension', () => {
    localStorage.setItem(
      languageWorkspaceStorageKey,
      JSON.stringify({
        version: 1,
        workspaces: {
          php: {
            files: [{ path: 'Program.cs', text: 'Console.WriteLine(49);' }],
            activeFile: 'Program.cs',
            sourceOrder: ['Program.cs'],
          },
        },
      }),
    )

    expect(readLanguageWorkspace(localStorage, php)).toBeNull()
  })

  it('rejects workspaces whose active file or source order is inconsistent', () => {
    localStorage.setItem(
      languageWorkspaceStorageKey,
      JSON.stringify({
        version: 1,
        workspaces: {
          php: {
            files: [{ path: 'index.php', text: '<?php' }],
            activeFile: 'missing.php',
            sourceOrder: ['index.php', 'missing.php'],
          },
        },
      }),
    )

    expect(readLanguageWorkspace(localStorage, php)).toBeNull()
  })
})
