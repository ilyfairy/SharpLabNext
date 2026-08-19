import {
  type DecodedShare,
  defaultUrlCodecLimits,
  type EncodedV3Share,
  type EncodeV3Options,
  ShareUrlError,
  type ShareWorkspaceState,
  type UrlCodecLimits,
} from '../share'
import type {
  UrlCodecWorkerRequest,
  UrlCodecWorkerResponse,
  UrlCodecWorkerValue,
} from './urlCodecProtocol'

interface PendingRequest {
  resolve(value: UrlCodecWorkerValue): void
  reject(reason: unknown): void
  timeout: ReturnType<typeof setTimeout>
}

export interface UrlCodecWorkerClientOptions {
  limits?: UrlCodecLimits
  startupTimeoutMs?: number
  workerFactory?: () => Worker
}

const defaultWorkerStartupTimeoutMs = 10_000

const defaultWorkerFactory = (): Worker =>
  new Worker(new URL('./urlCodec.worker.ts', import.meta.url), {
    type: 'module',
    name: 'sharplabnext-url-codec',
  })

export class UrlCodecWorkerClient {
  private readonly limits: UrlCodecLimits
  private readonly startupTimeoutMs: number
  private readonly workerFactory: () => Worker
  private readonly pending = new Map<number, PendingRequest>()
  private worker: Worker | null = null
  private workerReady = false
  private nextId = 1
  private disposed = false

  constructor(options: UrlCodecWorkerClientOptions = {}) {
    this.limits = options.limits ?? { ...defaultUrlCodecLimits }
    this.startupTimeoutMs = Math.max(
      this.limits.workerTimeoutMs,
      options.startupTimeoutMs ?? defaultWorkerStartupTimeoutMs,
    )
    this.workerFactory = options.workerFactory ?? defaultWorkerFactory
  }

  encodeV3(
    state: ShareWorkspaceState,
    options: Omit<EncodeV3Options, 'limits'> = {},
  ): Promise<EncodedV3Share> {
    return this.request({
      id: this.allocateId(),
      operation: 'encode-v3',
      state,
      options: { ...options, limits: this.limits },
    }) as Promise<EncodedV3Share>
  }

  decode(fragment: string): Promise<DecodedShare> {
    return this.request({
      id: this.allocateId(),
      operation: 'decode',
      fragment,
      limits: this.limits,
    }) as Promise<DecodedShare>
  }

  dispose(): void {
    if (this.disposed) return
    this.disposed = true
    this.failAll(new ShareUrlError('worker-failed', 'The URL codec worker was disposed.'))
    this.worker?.terminate()
    this.worker = null
  }

  private allocateId(): number {
    const id = this.nextId
    this.nextId += 1
    return id
  }

  private request(request: UrlCodecWorkerRequest): Promise<UrlCodecWorkerValue> {
    if (this.disposed) {
      return Promise.reject(new ShareUrlError('worker-failed', 'The URL codec worker is disposed.'))
    }

    let worker: Worker
    try {
      worker = this.getWorker()
    } catch (error) {
      return Promise.reject(
        new ShareUrlError('worker-failed', 'Failed to start the URL codec worker.', {
          cause: error,
        }),
      )
    }
    return new Promise((resolve, reject) => {
      const timeoutMs = this.workerReady ? this.limits.workerTimeoutMs : this.startupTimeoutMs
      const timeout = setTimeout(() => {
        if (!this.pending.has(request.id)) return
        this.restart(
          new ShareUrlError(
            'worker-timeout',
            `The URL codec worker exceeded its ${timeoutMs} ms limit.`,
          ),
        )
      }, timeoutMs)
      this.pending.set(request.id, { resolve, reject, timeout })
      try {
        worker.postMessage(request)
      } catch (error) {
        this.restart(
          new ShareUrlError('worker-failed', 'Failed to send work to the URL codec worker.', {
            cause: error,
          }),
        )
      }
    })
  }

  private getWorker(): Worker {
    if (this.worker) return this.worker

    const worker = this.workerFactory()
    worker.onmessage = (event: MessageEvent<UrlCodecWorkerResponse>) => {
      this.workerReady = true
      const response = event.data
      const pending = this.pending.get(response.id)
      if (!pending) return
      this.pending.delete(response.id)
      clearTimeout(pending.timeout)
      if (response.ok) pending.resolve(response.value)
      else pending.reject(new ShareUrlError(response.error.code, response.error.message))
    }
    worker.onerror = () => {
      this.restart(new ShareUrlError('worker-failed', 'The URL codec worker crashed.'))
    }
    worker.onmessageerror = () => {
      this.restart(
        new ShareUrlError('worker-failed', 'The URL codec worker returned invalid data.'),
      )
    }
    this.worker = worker
    return worker
  }

  private restart(error: ShareUrlError): void {
    this.worker?.terminate()
    this.worker = null
    this.workerReady = false
    this.failAll(error)
  }

  private failAll(error: ShareUrlError): void {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout)
      pending.reject(error)
    }
    this.pending.clear()
  }
}
