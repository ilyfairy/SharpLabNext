export const pinnedDockerfileFrontend =
  'docker/dockerfile:1.7@sha256:a57df69d0ea827fb7266491f2813635de6f17269be881f696fbfdf2d83dda33e'

export const pinnedDockerfileFrontendDirective = `# syntax=${pinnedDockerfileFrontend}`

export function validateDockerfileFrontend(source) {
  const hasPinnedDirective = typeof source === 'string' &&
    (source.startsWith(`${pinnedDockerfileFrontendDirective}\n`) ||
      source.startsWith(`${pinnedDockerfileFrontendDirective}\r\n`))
  if (!hasPinnedDirective) {
    return [`must start with '${pinnedDockerfileFrontendDirective}'`]
  }

  return []
}
