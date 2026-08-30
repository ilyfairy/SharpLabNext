import { ShareUrlError } from './errors'
import type { ShareBuildMode, ShareFile, ShareWorkspaceState, UrlCodecLimits } from './types'

const textEncoder = new TextEncoder()

const payloadKeys = ['v', 'l', 'tc', 'rs', 'o', 'rt', 'm', 'rv', 'af', 'so', 'f'] as const

interface V3Payload {
  v: 3
  l: string
  tc: string
  rs: string
  o: string
  rt: string
  m: ShareBuildMode
  rv: string
  af: string
  so: string[]
  f: [string, string][]
}

const fail = (message: string): never => {
  throw new ShareUrlError('invalid-workspace', message)
}

const assertPlainObject = (value: unknown, name: string): Record<string, unknown> => {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new ShareUrlError('invalid-payload', `${name} must be an object.`)
  }
  return value as Record<string, unknown>
}

const assertExactKeys = (value: Record<string, unknown>): void => {
  const actual = Object.keys(value)
  if (actual.length !== payloadKeys.length || payloadKeys.some((key) => !actual.includes(key))) {
    throw new ShareUrlError('invalid-payload', 'The v3 payload has unknown or missing fields.')
  }
}

const validateSelectionId = (value: unknown, name: string, limits: UrlCodecLimits): string => {
  if (typeof value !== 'string' || value.length === 0 || value.length > limits.maxSelectionIdLength) {
    return fail(`${name} must be a non-empty string within the configured length limit.`)
  }
  if (/\p{Cc}/u.test(value)) return fail(`${name} cannot contain control characters.`)
  return value
}

export const validateRelativePath = (value: unknown, limits: UrlCodecLimits): string => {
  if (typeof value !== 'string' || value.length === 0 || value.length > limits.maxPathLength || textEncoder.encode(value).length > limits.maxPathLength) {
    return fail('Workspace paths must be non-empty and within the configured length limit.')
  }
  if (value.includes('\0') || value.includes('\\') || value.startsWith('/') || value.includes(':')) {
    return fail(`Workspace path '${value}' is not a normalized relative path.`)
  }

  const segments = value.split('/')
  if (segments.some((segment) => segment.length === 0 || segment === '.' || segment === '..')) {
    return fail(`Workspace path '${value}' contains an invalid segment.`)
  }
  return value
}

const validateFiles = (value: unknown, limits: UrlCodecLimits): ShareFile[] => {
  if (!Array.isArray(value) || value.length === 0 || value.length > limits.maxFiles) {
    return fail(`A workspace must contain between 1 and ${limits.maxFiles} files.`)
  }

  const paths = new Set<string>()
  let totalBytes = 0
  return value.map((item) => {
    if (!Array.isArray(item) || item.length !== 2) return fail('Each v3 file must be a [path,text] tuple.')
    const path = validateRelativePath(item[0], limits)
    if (paths.has(path)) return fail(`Workspace path '${path}' is duplicated.`)
    paths.add(path)
    if (typeof item[1] !== 'string') return fail(`Workspace file '${path}' must contain text.`)

    const fileBytes = textEncoder.encode(item[1]).length
    if (fileBytes > limits.maxFileBytes) {
      return fail(`Workspace file '${path}' exceeds the ${limits.maxFileBytes} byte limit.`)
    }
    totalBytes += fileBytes
    if (totalBytes > limits.maxTotalFileBytes) {
      return fail(`Workspace source exceeds the ${limits.maxTotalFileBytes} byte limit.`)
    }
    return { path, text: item[1] }
  })
}

const validateSourceOrder = (value: unknown, files: readonly ShareFile[], limits: UrlCodecLimits): string[] => {
  if (!Array.isArray(value) || value.length !== files.length) {
    return fail('Source order must contain every workspace file exactly once.')
  }

  const paths = new Set(files.map((file) => file.path))
  const seen = new Set<string>()
  return value.map((item) => {
    const path = validateRelativePath(item, limits)
    if (!paths.has(path) || seen.has(path)) {
      return fail('Source order must contain every workspace file exactly once.')
    }
    seen.add(path)
    return path
  })
}

export const payloadToState = (value: unknown, limits: UrlCodecLimits): ShareWorkspaceState => {
  const payload = assertPlainObject(value, 'The v3 payload')
  assertExactKeys(payload)
  if (payload.v !== 3) throw new ShareUrlError('invalid-payload', 'Unsupported v3 payload schema.')

  const files = validateFiles(payload.f, limits)
  const sourceOrder = validateSourceOrder(payload.so, files, limits)
  const activeFile = validateRelativePath(payload.af, limits)
  if (!files.some((file) => file.path === activeFile)) {
    return fail('The active file must identify a workspace file.')
  }
  if (payload.m !== 'debug' && payload.m !== 'release') {
    return fail("Build mode must be either 'debug' or 'release'.")
  }

  return {
    languageId: validateSelectionId(payload.l, 'Language ID', limits),
    toolchainId: validateSelectionId(payload.tc, 'Toolchain ID', limits),
    referenceSetId: validateSelectionId(payload.rs, 'Reference set ID', limits),
    outputId: validateSelectionId(payload.o, 'Output ID', limits),
    runtimeId: validateSelectionId(payload.rt, 'Runtime ID', limits),
    buildMode: payload.m,
    releaseVersion: validateSelectionId(payload.rv, 'Release identity', limits),
    activeFile,
    sourceOrder,
    files,
  }
}

export const stateToPayload = (state: ShareWorkspaceState, limits: UrlCodecLimits): V3Payload => {
  const filesAsTuples: [string, string][] = state.files.map((file) => [file.path, file.text])
  const validated = payloadToState(
    {
      v: 3,
      l: state.languageId,
      tc: state.toolchainId,
      rs: state.referenceSetId,
      o: state.outputId,
      rt: state.runtimeId,
      m: state.buildMode,
      rv: state.releaseVersion,
      af: state.activeFile,
      so: [...state.sourceOrder],
      f: filesAsTuples,
    },
    limits,
  )

  return {
    v: 3,
    l: validated.languageId,
    tc: validated.toolchainId,
    rs: validated.referenceSetId,
    o: validated.outputId,
    rt: validated.runtimeId,
    m: validated.buildMode,
    rv: validated.releaseVersion,
    af: validated.activeFile,
    so: validated.sourceOrder,
    f: validated.files.map((file) => [file.path, file.text]),
  }
}

export const encodeCanonicalPayload = (state: ShareWorkspaceState, limits: UrlCodecLimits): Uint8Array => {
  const bytes = textEncoder.encode(JSON.stringify(stateToPayload(state, limits)))
  if (bytes.length > limits.maxUncompressedBytes) {
    throw new ShareUrlError('payload-too-large', `The v3 payload exceeds the ${limits.maxUncompressedBytes} byte limit.`)
  }
  return bytes
}

export const decodeCanonicalPayload = (bytes: Uint8Array, limits: UrlCodecLimits): ShareWorkspaceState => {
  let json: string
  try {
    json = new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch (error) {
    throw new ShareUrlError('invalid-payload', 'The v3 payload is not valid UTF-8.', {
      cause: error,
    })
  }

  try {
    return payloadToState(JSON.parse(json), limits)
  } catch (error) {
    if (error instanceof ShareUrlError) throw error
    throw new ShareUrlError('invalid-payload', 'The v3 payload is not valid JSON.', {
      cause: error,
    })
  }
}
