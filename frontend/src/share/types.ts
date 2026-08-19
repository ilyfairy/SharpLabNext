export type CompressionProfile = 'live' | 'share'

export type UrlCodecId = 0 | 1

export type ShareBuildMode = 'debug' | 'release'

export interface ShareFile {
  path: string
  text: string
}

export interface ShareWorkspaceState {
  languageId: string
  toolchainId: string
  referenceSetId: string
  outputId: string
  runtimeId: string
  buildMode: ShareBuildMode
  releaseVersion: string
  activeFile: string
  sourceOrder: string[]
  files: ShareFile[]
}

export interface UrlCodecLimits {
  softUrlLength: number
  hardUrlLength: number
  maxUncompressedBytes: number
  maxFiles: number
  maxFileBytes: number
  maxTotalFileBytes: number
  maxPathLength: number
  maxSelectionIdLength: number
  maxLegacyEncodedLength: number
  maxLegacyDecodedCharacters: number
  workerTimeoutMs: number
}

export type UrlLengthDisposition = 'live' | 'explicit-warning'

export interface EncodedV3Share {
  fragment: string
  codecId: UrlCodecId
  compressionLevel: 6 | 9 | null
  payloadLength: number
  encodedPayloadLength: number
  envelopeLength: number
  urlLength: number
  lengthDisposition: UrlLengthDisposition
}

export interface EncodeV3Options {
  profile?: CompressionProfile
  baseUrl?: string
  limits?: UrlCodecLimits
}

export interface LegacyRequestedOptions {
  branchId: string | undefined
  languageKey: string
  languageId: string | undefined
  targetKey: string
  outputId: string | undefined
  buildMode: ShareBuildMode
}

export interface ImportedWorkspace {
  languageId: string
  activeFile: string
  sourceOrder: string[]
  files: ShareFile[]
}

export interface LegacyImportResult {
  sourceFormat: 'sharplab-v1' | 'sharplab-v2'
  workspace: ImportedWorkspace
  requestedLegacyOptions: LegacyRequestedOptions
  resolvedSelection: null
  warnings: string[]
}

export interface DecodedV3Share {
  sourceFormat: 'v3'
  state: ShareWorkspaceState
  codecId: UrlCodecId
}

export type DecodedShare = DecodedV3Share | LegacyImportResult
