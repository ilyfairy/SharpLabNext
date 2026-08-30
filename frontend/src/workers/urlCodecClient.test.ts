import { describe, expect, it } from 'vitest'
import { defaultUrlCodecLimits } from '../share'
import { goldenState } from '../share/goldenFixture'
import { handleUrlCodecWorkerRequest } from './urlCodec.worker'
import { UrlCodecWorkerClient } from './urlCodecClient'
import type { UrlCodecWorkerRequest, UrlCodecWorkerResponse } from './urlCodecProtocol'

class InProcessWorker {
  onmessage: ((event: MessageEvent<UrlCodecWorkerResponse>) => void) | null = null
  onerror: ((event: ErrorEvent) => void) | null = null
  onmessageerror: ((event: MessageEvent) => void) | null = null
  terminated = false

  postMessage(request: UrlCodecWorkerRequest): void {
    void handleUrlCodecWorkerRequest(request).then((response) => {
      if (!this.terminated)
        this.onmessage?.({
          data: response,
        } as MessageEvent<UrlCodecWorkerResponse>)
    })
  }

  terminate(): void {
    this.terminated = true
  }
}

class HangingWorker extends InProcessWorker {
  override postMessage(): void {}
}

class DelayedWorker extends InProcessWorker {
  private readonly delayMs: number

  constructor(delayMs: number) {
    super()
    this.delayMs = delayMs
  }

  override postMessage(request: UrlCodecWorkerRequest): void {
    setTimeout(() => super.postMessage(request), this.delayMs)
  }
}

describe('URL codec worker client', () => {
  it('encodes and decodes through the worker protocol', async () => {
    const client = new UrlCodecWorkerClient({
      workerFactory: () => new InProcessWorker() as unknown as Worker,
    })
    const encoded = await client.encodeV3(goldenState, { profile: 'share' })
    const decoded = await client.decode(encoded.fragment)
    expect(decoded).toMatchObject({ sourceFormat: 'v3', state: goldenState })
    client.dispose()
  })

  it('terminates a worker that exceeds the decode deadline', async () => {
    const worker = new HangingWorker()
    const client = new UrlCodecWorkerClient({
      limits: { ...defaultUrlCodecLimits, workerTimeoutMs: 5 },
      startupTimeoutMs: 5,
      workerFactory: () => worker as unknown as Worker,
    })

    await expect(client.decode('#v2:AAAA')).rejects.toMatchObject({
      code: 'worker-timeout',
    })
    expect(worker.terminated).toBe(true)
    client.dispose()
  })

  it('allows a cold worker startup longer than the warm request deadline', async () => {
    const worker = new DelayedWorker(20)
    const client = new UrlCodecWorkerClient({
      limits: { ...defaultUrlCodecLimits, workerTimeoutMs: 5 },
      startupTimeoutMs: 100,
      workerFactory: () => worker as unknown as Worker,
    })

    const encoded = await client.encodeV3(goldenState, { profile: 'live' })

    expect(encoded.fragment).toMatch(/^#v3:/)
    expect(worker.terminated).toBe(false)
    client.dispose()
  })

  it('reports worker startup failures as rejected promises', async () => {
    const client = new UrlCodecWorkerClient({
      workerFactory: () => {
        throw new Error('worker unavailable')
      },
    })
    await expect(client.decode('#v2:AAAA')).rejects.toMatchObject({
      code: 'worker-failed',
    })
    client.dispose()
  })
})
