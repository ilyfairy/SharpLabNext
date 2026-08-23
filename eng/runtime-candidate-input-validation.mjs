/**
 * Fail-closed validation for operator-provided runtime candidate inputs.
 *
 * Candidate images are deliberately outside profiles/base-images.json, but
 * they still need the same immutable reference contract before BuildKit is
 * allowed to resolve a Dockerfile FROM. Keep this helper dependency-free so
 * release automation can run it before invoking `docker buildx bake`.
 */

import { pathToFileURL } from 'node:url'

const digestPinnedImageReference = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const sha256Digest = /^sha256:[0-9a-f]{64}$/
const sha512HexDigest = /^[0-9a-f]{128}$/
const gitCommitIdentity = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const dotNetSdkVersion = /^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/

export const candidateImageInputNames = Object.freeze([
  'BASE_DOTNET_SDK_IMAGE',
  'RUNTIME_MATRIX_BASE_IMAGE',
  'RUNTIME_MATRIX_CONTROL_IMAGE',
  'RUNTIME_MATRIX_MONO_IMAGE',
  'RUNTIME_MATRIX_MONO_WINE_IMAGE',
  'RUNTIME_MATRIX_WINE_IMAGE',
  'RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
])

// Labels are part of the candidate identity closure. A candidate image may
// only be promoted when the label value is byte-for-byte the input used for
// its corresponding FROM stage.
export const candidateImageLabelBindings = Object.freeze({
  'io.sharplabnext.base-image.dotnet-sdk': 'BASE_DOTNET_SDK_IMAGE',
  'io.sharplabnext.base-image.dotnet-runtime-deps': 'RUNTIME_MATRIX_BASE_IMAGE',
  'io.sharplabnext.control-image': 'RUNTIME_MATRIX_CONTROL_IMAGE',
  'io.sharplabnext.operator-image.mono': 'RUNTIME_MATRIX_MONO_IMAGE',
  'io.sharplabnext.operator-image.mono-wine': 'RUNTIME_MATRIX_MONO_WINE_IMAGE',
  'io.sharplabnext.operator-image.wine': 'RUNTIME_MATRIX_WINE_IMAGE',
  'io.sharplabnext.framework.matrix-parent': 'RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
  'io.sharplabnext.framework.row-operator-image': 'RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
  'io.sharplabnext.framework.row-digest': 'RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST',
})

export const wineCoreClrUserspaceInputNames = Object.freeze({
  version: 'WINE_CORECLR_USERSPACE_VERSION',
  digest: 'WINE_CORECLR_USERSPACE_DIGEST',
  sourceUri: 'WINE_CORECLR_USERSPACE_SOURCE_URI',
})

/**
 * The shared Wine userspace is a release component, not an untracked base
 * image detail. Keep its three lock-derived inputs strict before candidate
 * image resolution so a private or development operator cannot be relabelled
 * into a promotion closure.
 */
export function validateWineCoreClrUserspaceInputs(values) {
  const failures = []
  const version = values?.[wineCoreClrUserspaceInputNames.version]
  if (typeof version !== 'string' || version.trim().length === 0 || /\s/.test(version)) {
    failures.push(`${wineCoreClrUserspaceInputNames.version} must be a non-empty whitespace-free version`)
  }
  const digest = values?.[wineCoreClrUserspaceInputNames.digest]
  if (!isSha256Digest(digest)) {
    failures.push(`${wineCoreClrUserspaceInputNames.digest} must be sha256:<64 lowercase hex>`)
  }
  const sourceUri = values?.[wineCoreClrUserspaceInputNames.sourceUri]
  if (!isHttpsUri(sourceUri)) {
    failures.push(`${wineCoreClrUserspaceInputNames.sourceUri} must be an absolute HTTPS URI without credentials`)
  }
  return failures
}

/**
 * Return true only for a Docker repository reference pinned to a lowercase
 * SHA-256 digest. Tags, bare digests, whitespace and alternate algorithms are
 * intentionally rejected.
 */
export function isDigestPinnedImageReference(value) {
  return typeof value === 'string' && digestPinnedImageReference.test(value)
}

export function isSha256Digest(value) {
  return typeof value === 'string' && sha256Digest.test(value)
}

export function isSha512HexDigest(value) {
  return typeof value === 'string' && sha512HexDigest.test(value)
}

export function isGitCommitIdentity(value) {
  return typeof value === 'string' && gitCommitIdentity.test(value)
}

export function isDotNetSdkVersion(value) {
  return typeof value === 'string' && dotNetSdkVersion.test(value)
}

export function isHttpsUri(value) {
  if (typeof value !== 'string' || value.length === 0 || value !== value.trim()) return false
  try {
    const uri = new URL(value)
    return uri.protocol === 'https:' &&
      uri.hostname.length > 0 &&
      uri.username.length === 0 &&
      uri.password.length === 0
  } catch {
    return false
  }
}

export function isCandidateSourceUri(value) {
  if (isHttpsUri(value)) return true
  if (typeof value !== 'string' || !value.startsWith('docker://')) return false
  return isDigestPinnedImageReference(value.slice('docker://'.length))
}

/**
 * Validate the supplied candidate image environment values.
 *
 * `names` is optional for targets that use only a subset of the matrix inputs;
 * the default validates every candidate-capable input.
 */
export function validateCandidateImageInputs(values, names = candidateImageInputNames) {
  const failures = []
  for (const name of names) {
    const value = values?.[name]
    if (typeof value !== 'string' || value.length === 0) {
      failures.push(`${name} must be a non-empty repository@sha256:<64 lowercase hex> reference`)
      continue
    }
    if (!isDigestPinnedImageReference(value)) {
      failures.push(`${name} must use repository@sha256:<64 lowercase hex>; received '${value}'`)
    }
  }
  return failures
}

/**
 * Validate labels returned by `docker image inspect` against the exact input
 * references. Missing labels are failures; silently accepting a missing label
 * would allow a candidate to lose its provenance while retaining its tag.
 */
export function validateCandidateImageLabels(labels, values, bindings = candidateImageLabelBindings) {
  const failures = []
  const actual = labels ?? {}
  for (const [label, inputName] of Object.entries(bindings)) {
    const expected = values?.[inputName]
    if (expected === undefined || expected === '') continue
    if (actual[label] !== expected) {
      const observed = actual[label] === undefined ? '<missing>' : `'${actual[label]}'`
      failures.push(`${label} must equal ${inputName} (${expected}); observed ${observed}`)
    }
  }
  return failures
}

/** Validate labels whose expected value is a fixed part of the candidate contract. */
export function validateCandidateExpectedLabels(labels, expectedLabels) {
  const failures = []
  const actual = labels ?? {}
  for (const [label, expected] of Object.entries(expectedLabels)) {
    if (actual[label] !== expected) {
      const observed = actual[label] === undefined ? '<missing>' : `'${actual[label]}'`
      failures.push(`${label} must equal '${expected}'; observed ${observed}`)
    }
  }
  return failures
}

/**
 * Validate both the build inputs and the inspected image labels in one call.
 * This is the preferred gate for candidate materialization and remote image
 * verification.
 */
export function validateCandidateImageIdentity(
  values,
  labels,
  names = candidateImageInputNames,
  bindings = candidateImageLabelBindings,
) {
  const inputFailures = validateCandidateImageInputs(values, names)
  const selectedBindings = Object.fromEntries(
    Object.entries(bindings)
      .filter(([, inputName]) => names.includes(inputName)),
  )
  const labelFailures = validateCandidateImageLabels(labels, values, selectedBindings)
  return [...inputFailures, ...labelFailures]
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const names = process.argv.slice(2)
  const selectedNames = names.length === 0 ? candidateImageInputNames : names
  const values = Object.fromEntries(selectedNames.map(name => [name, process.env[name] ?? '']))
  const failures = validateCandidateImageInputs(values, selectedNames)
  if (failures.length > 0) {
    for (const failure of failures) console.error(`runtime candidate input error: ${failure}`)
    process.exitCode = 1
  } else {
    console.log(`Validated ${selectedNames.length} digest-pinned runtime candidate image inputs.`)
  }
}
