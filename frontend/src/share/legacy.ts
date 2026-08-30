import { assertStandardBase64 } from './base64'
import { ShareUrlError } from './errors'
import { legacyPredecompress } from './legacyPrecompressor'
import { resolveUrlCodecLimits } from './limits'
import type { ImportedWorkspace, LegacyImportResult, LegacyRequestedOptions, UrlCodecLimits } from './types'

const languageIds: Readonly<Record<string, string>> = {
  cs: 'csharp',
  vb: 'visual-basic',
  fs: 'fsharp',
  php: 'php',
  il: 'il',
}

const outputIds: Readonly<Record<string, string>> = {
  cs: 'decompiled-csharp',
  vb: 'decompiled-visual-basic',
  il: 'il',
  asm: 'jit-asm',
  ast: 'ast',
  run: 'run',
  'run-il': 'run-il',
  verify: 'il-verify',
  explain: 'explain',
}

const defaultFileNames: Readonly<Record<string, string>> = {
  csharp: 'Program.cs',
  'visual-basic': 'Program.vb',
  fsharp: 'Program.fs',
  php: 'index.php',
  il: 'Program.il',
}

interface LzStringApi {
  decompressFromBase64(value: string): string | null
}

const loadLzString = async (): Promise<LzStringApi> => {
  const module = await import('lz-string')
  return module.default
}

const decodeHashText = (fragment: string, limits: UrlCodecLimits): string => {
  if (fragment.length > limits.maxLegacyEncodedLength) {
    throw new ShareUrlError('legacy-too-large', 'The legacy URL exceeds its encoded length limit.')
  }

  const hash = fragment.startsWith('#') ? fragment.slice(1) : fragment
  try {
    const decoded = decodeURIComponent(hash)
    if (decoded.length > limits.maxLegacyEncodedLength) {
      throw new ShareUrlError('legacy-too-large', 'The decoded legacy URL exceeds its length limit.')
    }
    return decoded
  } catch (error) {
    if (error instanceof ShareUrlError) throw error
    throw new ShareUrlError('legacy-invalid', 'The legacy URL contains invalid percent encoding.', {
      cause: error,
    })
  }
}

const assertLegacyDecodedLength = (value: string, limits: UrlCodecLimits, description: string): void => {
  if (value.length > limits.maxLegacyDecodedCharacters || new TextEncoder().encode(value).length > limits.maxUncompressedBytes) {
    throw new ShareUrlError('legacy-too-large', `${description} exceeds the legacy decode limit.`)
  }
}

const createRequestedOptions = (branchId: string | undefined, languageKey: string, targetKey: string, release: boolean): LegacyRequestedOptions => ({
  branchId,
  languageKey,
  languageId: languageIds[languageKey],
  targetKey,
  outputId: outputIds[targetKey],
  buildMode: release ? 'release' : 'debug',
})

const createWorkspace = (code: string, languageId: string): ImportedWorkspace => {
  const path = defaultFileNames[languageId] ?? 'Program.txt'
  return {
    languageId,
    activeFile: path,
    sourceOrder: [path],
    files: [{ path, text: code }],
  }
}

const createResult = (sourceFormat: LegacyImportResult['sourceFormat'], code: string, requestedLegacyOptions: LegacyRequestedOptions): LegacyImportResult => {
  if (!requestedLegacyOptions.languageId) {
    throw new ShareUrlError('legacy-invalid', `SharpLab language '${requestedLegacyOptions.languageKey}' is not recognized.`)
  }
  if (!requestedLegacyOptions.outputId) {
    throw new ShareUrlError('legacy-invalid', `SharpLab target '${requestedLegacyOptions.targetKey}' is not recognized.`)
  }

  const warnings = ['Legacy selections must be resolved against the current catalog before execution.']
  if (requestedLegacyOptions.branchId) {
    warnings.push(`Legacy branch '${requestedLegacyOptions.branchId}' must be resolved through profile aliases.`)
  }
  return {
    sourceFormat,
    workspace: createWorkspace(code, requestedLegacyOptions.languageId),
    requestedLegacyOptions,
    resolvedSelection: null,
    warnings,
  }
}

const parsePackedOptions = (value: string): Record<string, string | undefined> => {
  const result: Record<string, string | undefined> = {}
  for (const part of value.split(',')) {
    const separator = part.indexOf(':')
    const key = separator < 0 ? part : part.slice(0, separator)
    result[key] = separator < 0 ? undefined : part.slice(separator + 1).split(':', 1)[0]
  }
  return result
}

const importV2 = async (hash: string, limits: UrlCodecLimits): Promise<LegacyImportResult> => {
  const compressed = hash.slice('v2:'.length)
  assertStandardBase64(compressed)
  const lzString = await loadLzString()
  let decompressed: string | null
  try {
    decompressed = lzString.decompressFromBase64(compressed)
  } catch (error) {
    throw new ShareUrlError('legacy-invalid', 'SharpLab v2 LZString decompression failed.', {
      cause: error,
    })
  }
  if (decompressed === null) {
    throw new ShareUrlError('legacy-invalid', 'SharpLab v2 LZString decompression failed.')
  }
  assertLegacyDecodedLength(decompressed, limits, 'The SharpLab v2 payload')

  const separator = decompressed.indexOf('|')
  if (separator < 0) {
    throw new ShareUrlError('legacy-invalid', 'SharpLab v2 options and code are not separated.')
  }
  const options = parsePackedOptions(decompressed.slice(0, separator))
  const languageKey = options.l ?? 'cs'
  const languageId = languageIds[languageKey]
  if (!languageId) {
    throw new ShareUrlError('legacy-invalid', `SharpLab language '${languageKey}' is not recognized.`)
  }

  let code: string
  try {
    code = legacyPredecompress(decompressed.slice(separator + 1), languageId)
  } catch (error) {
    throw new ShareUrlError('legacy-invalid', 'SharpLab v2 dictionary expansion failed.', {
      cause: error,
    })
  }
  assertLegacyDecodedLength(code, limits, 'The imported SharpLab source')
  if (new TextEncoder().encode(code).length > limits.maxFileBytes) {
    throw new ShareUrlError('legacy-too-large', 'The imported SharpLab source exceeds the file limit.')
  }
  return createResult('sharplab-v2', code, createRequestedOptions(options.b, languageKey, options.t ?? 'cs', options.d !== '+'))
}

const importV1 = async (hash: string, limits: UrlCodecLimits): Promise<LegacyImportResult> => {
  const match = /^(?:b:([^/]+)\/)?(?:f:([^/]+)\/)?(.+)$/u.exec(hash)
  if (!match) throw new ShareUrlError('legacy-invalid', 'The SharpLab v1 URL shape is invalid.')

  const flags = /^(?:([^>]*?))(>.+?)?(r)?$/u.exec(match[2] ?? '') ?? []
  const languageKey = flags[1] || 'cs'
  const targetKey = (flags[2] || '>cs').slice(1)
  const compressed = match[3] ?? ''
  if (!/^[A-Za-z0-9+/]*={0,3}$/u.test(compressed)) {
    throw new ShareUrlError('invalid-base64', 'The SharpLab v1 payload is not valid Base64.')
  }

  const lzString = await loadLzString()
  let code = ''
  try {
    code = lzString.decompressFromBase64(compressed) ?? ''
  } catch (error) {
    throw new ShareUrlError('legacy-invalid', 'SharpLab v1 LZString decompression failed.', {
      cause: error,
    })
  }
  assertLegacyDecodedLength(code, limits, 'The imported SharpLab v1 source')
  if (new TextEncoder().encode(code).length > limits.maxFileBytes) {
    throw new ShareUrlError('legacy-too-large', 'The imported SharpLab source exceeds the file limit.')
  }
  return createResult('sharplab-v1', code, createRequestedOptions(match[1], languageKey, targetKey, flags[3] === 'r'))
}

export const importSharpLabLegacy = async (fragment: string, providedLimits?: UrlCodecLimits): Promise<LegacyImportResult> => {
  const limits = resolveUrlCodecLimits(providedLimits)
  const hash = decodeHashText(fragment, limits)
  if (hash.startsWith('v2:')) return importV2(hash, limits)
  if (/^v\d+:/u.test(hash)) {
    throw new ShareUrlError('legacy-invalid', 'This SharpLab URL version is not supported.')
  }
  return importV1(hash, limits)
}
