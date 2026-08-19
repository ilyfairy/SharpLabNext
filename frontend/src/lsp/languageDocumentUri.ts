/**
 * Builds the URI namespace used by a browser language-service session.
 *
 * Most workers use the editor-local workspace id as the URI authority so
 * multiple browser sessions can keep their document names distinct. ILSense
 * intentionally defines a stricter protocol: document URIs must be relative
 * paths below `sharplabnext:///` and must not contain an authority. Keep that
 * exception here so both editor adapters make the same protocol decision.
 */
export function createLanguageWorkspaceUri(languageId: string, workspaceId: string): string {
  return languageId === 'il' ? 'sharplabnext:///' : `sharplabnext://${workspaceId}/`
}

/**
 * Appends a workspace-relative file path to an LSP workspace URI.
 * Paths are encoded segment-by-segment so spaces and other file-name
 * punctuation cannot change the URI structure.
 */
export function createLanguageDocumentUri(workspaceUri: string, path: string): string {
  const root = workspaceUri.endsWith('/') ? workspaceUri : `${workspaceUri}/`
  const encodedPath = path
    .split('/')
    .map((segment) => encodeURIComponent(segment))
    .join('/')
  return `${root}${encodedPath}`
}
