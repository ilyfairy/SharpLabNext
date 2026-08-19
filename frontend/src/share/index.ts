import { ShareUrlError } from './errors'
import { importSharpLabLegacy } from './legacy'
import type { DecodedShare, UrlCodecLimits } from './types'
import { decodeV3 } from './v3'

export { asShareUrlError, ShareUrlError } from './errors'
export { importSharpLabLegacy } from './legacy'
export { legacyDictionaries, legacyPrecompress, legacyPredecompress } from './legacyPrecompressor'
export { defaultUrlCodecLimits } from './limits'
export type {
  CompressionProfile,
  DecodedShare,
  DecodedV3Share,
  EncodedV3Share,
  EncodeV3Options,
  ImportedWorkspace,
  LegacyImportResult,
  LegacyRequestedOptions,
  ShareBuildMode,
  ShareFile,
  ShareWorkspaceState,
  UrlCodecId,
  UrlCodecLimits,
  UrlLengthDisposition,
} from './types'
export { createV3Envelope, decodeV3, encodeV3 } from './v3'

export const decodeShareFragment = async (
  fragment: string,
  limits?: UrlCodecLimits,
): Promise<DecodedShare> => {
  const withoutHash = fragment.startsWith('#') ? fragment.slice(1) : fragment
  if (withoutHash.startsWith('v3:')) return decodeV3(fragment, limits)
  if (withoutHash.startsWith('gist:')) {
    throw new ShareUrlError('invalid-fragment', 'Gist URLs must be loaded by the Gist client.')
  }
  return importSharpLabLegacy(fragment, limits)
}
