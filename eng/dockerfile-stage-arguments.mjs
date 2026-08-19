export function findDockerfileStageArgumentScopeViolations(source, argumentName) {
  if (!/^[A-Z_][A-Z0-9_]*$/.test(argumentName)) {
    throw new Error(`Invalid Dockerfile argument name '${argumentName}'.`)
  }

  const declaration = new RegExp(`^ARG\\s+${argumentName}(?:\\s*=.*)?$`, 'i')
  const reference = new RegExp(`\\$\\{${argumentName}\\}`)
  const violations = []
  let stage
  let declaredInStage = false

  for (const [index, line] of source.split(/\r?\n/).entries()) {
    const trimmed = line.trim()
    const from = /^FROM(?:\s+--[^\s]+)*\s+[^\s]+(?:\s+AS\s+([^\s]+))?/i.exec(trimmed)
    if (from) {
      stage = from[1] ?? `stage starting at line ${index + 1}`
      declaredInStage = false
      continue
    }
    if (stage === undefined || trimmed.startsWith('#')) continue
    if (declaration.test(trimmed)) {
      declaredInStage = true
      continue
    }
    if (reference.test(line) && !declaredInStage) {
      violations.push({ line: index + 1, stage })
    }
  }

  return violations
}
