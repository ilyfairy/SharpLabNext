/// <reference lib="webworker" />

import { asShareUrlError, decodeShareFragment, encodeV3 } from '../share';
import type { UrlCodecWorkerRequest, UrlCodecWorkerResponse } from './urlCodecProtocol';

export const handleUrlCodecWorkerRequest = async (request: UrlCodecWorkerRequest): Promise<UrlCodecWorkerResponse> => {
  try {
    const value = request.operation === 'encode-v3' ? await encodeV3(request.state, request.options) : await decodeShareFragment(request.fragment, request.limits);
    return { id: request.id, ok: true, value };
  } catch (error) {
    const urlError = asShareUrlError(error);
    return {
      id: request.id,
      ok: false,
      error: {
        code: urlError.code,
        message: urlError.message,
      },
    };
  }
};

const scope = globalThis as typeof globalThis & {
  document?: unknown;
  onmessage: ((event: MessageEvent<UrlCodecWorkerRequest>) => void) | null;
  postMessage(message: UrlCodecWorkerResponse): void;
};

if (scope.document === undefined && typeof scope.postMessage === 'function') {
  scope.onmessage = (event) => void handleUrlCodecWorkerRequest(event.data).then((response) => scope.postMessage(response));
}
