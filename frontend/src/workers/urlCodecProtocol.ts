import type { DecodedShare, EncodedV3Share, EncodeV3Options, ShareWorkspaceState, UrlCodecLimits } from '../share';
import type { ShareUrlErrorCode } from '../share/errors';

export type UrlCodecWorkerRequest =
  | {
      id: number
      operation: 'encode-v3'
      state: ShareWorkspaceState
      options: EncodeV3Options
    }
  | {
      id: number
      operation: 'decode'
      fragment: string
      limits: UrlCodecLimits
    }

export type UrlCodecWorkerValue = EncodedV3Share | DecodedShare;

export type UrlCodecWorkerResponse =
  | {
      id: number
      ok: true
      value: UrlCodecWorkerValue
    }
  | {
      id: number
      ok: false
      error: {
        code: ShareUrlErrorCode
        message: string
      }
    }
