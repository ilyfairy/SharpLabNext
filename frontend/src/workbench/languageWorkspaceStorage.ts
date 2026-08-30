import type { LanguageManifest } from '../api/types'

export const languageWorkspaceStorageKey = 'sharplabnext.language-workspaces.v1'

const maximumLanguages = 32
const maximumFiles = 64
const maximumPathLength = 260
const maximumFileLength = 2_000_000

export interface StoredLanguageWorkspace {
  files: { path: string; text: string }[]
  activeFile: string
  sourceOrder: string[]
}

interface StoredLanguageWorkspaceDocument {
  version: 1
  workspaces: Record<string, StoredLanguageWorkspace>
}

export function readLanguageWorkspace(storage: Storage | null, language: LanguageManifest): StoredLanguageWorkspace | null {
  const document = readDocument(storage)
  const workspace = document?.workspaces[language.id]
  return validateWorkspace(workspace, language) ? cloneWorkspace(workspace) : null
}

export function writeLanguageWorkspace(storage: Storage | null, languageId: string, workspace: StoredLanguageWorkspace): void {
  if (!storage || !validLanguageId(languageId) || !validateWorkspaceShape(workspace)) return

  const document = readDocument(storage) ?? { version: 1, workspaces: {} }
  const entries = Object.entries(document.workspaces).filter(([id]) => id !== languageId)
  const workspaces = Object.fromEntries(entries.slice(-(maximumLanguages - 1)))
  workspaces[languageId] = cloneWorkspace(workspace)

  try {
    storage.setItem(languageWorkspaceStorageKey, JSON.stringify({ version: 1, workspaces } satisfies StoredLanguageWorkspaceDocument));
  } catch {
    // Browsers can deny storage or reject writes after their quota is exhausted.
  }
}

export function clearLanguageWorkspaces(storage: Storage | null): void {
  try {
    storage?.removeItem(languageWorkspaceStorageKey)
  } catch {
    // Storage access is optional for the workbench.
  }
}

export function removeLanguageWorkspace(storage: Storage | null, languageId: string): void {
  if (!storage || !validLanguageId(languageId)) return
  const document = readDocument(storage)
  if (!document || !(languageId in document.workspaces)) return
  const workspaces = Object.fromEntries(Object.entries(document.workspaces).filter(([id]) => id !== languageId))
  try {
    if (Object.keys(workspaces).length === 0) {
      storage.removeItem(languageWorkspaceStorageKey)
    } else {
      storage.setItem(languageWorkspaceStorageKey, JSON.stringify({ version: 1, workspaces } satisfies StoredLanguageWorkspaceDocument));
    }
  } catch {
    // Storage access is optional for the workbench.
  }
}

function readDocument(storage: Storage | null): StoredLanguageWorkspaceDocument | null {
  if (!storage) return null
  try {
    const raw = storage.getItem(languageWorkspaceStorageKey)
    if (!raw) return null
    const parsed = JSON.parse(raw) as unknown
    if (!isRecord(parsed) || parsed.version !== 1 || !isRecord(parsed.workspaces)) return null
    return parsed as unknown as StoredLanguageWorkspaceDocument
  } catch {
    return null
  }
}

function validateWorkspace(workspace: unknown, language: LanguageManifest): workspace is StoredLanguageWorkspace {
  if (!validateWorkspaceShape(workspace)) return false
  const extensions = language.extensions.map((extension) => extension.toLocaleLowerCase())
  return workspace.files.every((file) => {
    const path = file.path.toLocaleLowerCase()
    return extensions.some((extension) => path.endsWith(extension))
  })
}

function validateWorkspaceShape(workspace: unknown): workspace is StoredLanguageWorkspace {
  if (!isRecord(workspace)) return false
  if (!Array.isArray(workspace.files) || workspace.files.length === 0) return false
  if (workspace.files.length > maximumFiles || !Array.isArray(workspace.sourceOrder)) return false
  if (typeof workspace.activeFile !== 'string') return false

  const paths = new Set<string>()
  for (const file of workspace.files) {
    if (!isRecord(file) || typeof file.path !== 'string' || typeof file.text !== 'string') {
      return false
    }
    if (file.path.length === 0 || file.path.length > maximumPathLength || file.text.length > maximumFileLength || paths.has(file.path)) {
      return false
    }
    paths.add(file.path)
  }

  if (!paths.has(workspace.activeFile) || workspace.sourceOrder.length !== paths.size) return false
  const ordered = new Set<string>()
  for (const path of workspace.sourceOrder) {
    if (typeof path !== 'string' || !paths.has(path) || ordered.has(path)) return false
    ordered.add(path)
  }
  return true
}

function validLanguageId(languageId: string): boolean {
  return /^[a-z0-9][a-z0-9-]{0,63}$/.test(languageId)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function cloneWorkspace(workspace: StoredLanguageWorkspace): StoredLanguageWorkspace {
  return {
    files: workspace.files.map((file) => ({ ...file })),
    activeFile: workspace.activeFile,
    sourceOrder: [...workspace.sourceOrder],
  }
}
