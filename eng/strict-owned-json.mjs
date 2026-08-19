export function parseOwnedJson(bytes, label, failures) {
  let value
  try {
    value = JSON.parse(bytes.toString('utf8'))
  } catch (error) {
    failures.push(`${label} is invalid JSON (${error.message})`)
    return undefined
  }

  if (containsExplicitJsonNull(value)) {
    failures.push(
      `${label} cannot contain explicit JSON null values; optional properties must be omitted`,
    )
    return undefined
  }
  return value
}

export function containsExplicitJsonNull(value) {
  if (value === null) return true
  if (Array.isArray(value)) return value.some(containsExplicitJsonNull)
  return typeof value === 'object' &&
    Object.values(value).some(containsExplicitJsonNull)
}
