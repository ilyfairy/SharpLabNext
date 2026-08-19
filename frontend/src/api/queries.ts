import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import {
  type GatewayConnectionStatus,
  getCatalog,
  getOperation,
  type OperationEventStreamStatus,
  subscribeToGatewayConnectionStatus,
  subscribeToOperationEvents,
} from './client'
import type { OperationEvent, OperationState } from './types'

export const catalogQueryKey = ['catalog'] as const

export function useCatalogQuery() {
  return useQuery({
    queryKey: catalogQueryKey,
    queryFn: ({ signal }) => getCatalog(signal),
    staleTime: 30_000,
    refetchInterval: 5_000,
    refetchIntervalInBackground: true,
  })
}

export function useGatewayConnectionStatus(): GatewayConnectionStatus {
  const [status, setStatus] = useState<GatewayConnectionStatus>('idle')
  useEffect(() => subscribeToGatewayConnectionStatus(setStatus), [])
  return status
}

export function isOperationTerminal(status: OperationState['status'] | undefined): boolean {
  return status === 'completed' || status === 'failed' || status === 'cancelled'
}

export const operationQueryKeys = {
  state: (operationId: string) => ['operations', operationId, 'state'] as const,
  events: (operationId: string) => ['operations', operationId, 'events'] as const,
}

export function useOperationState(operationId: string | null) {
  return useQuery({
    queryKey: operationQueryKeys.state(operationId ?? 'none'),
    queryFn: ({ signal }) => getOperation(operationId ?? '', signal),
    enabled: operationId !== null,
    staleTime: Number.POSITIVE_INFINITY,
    refetchOnWindowFocus: false,
  })
}

export interface OperationEventsState {
  events: OperationEvent[]
  streamStatus: OperationEventStreamStatus | 'idle'
  streamError: Error | null
}

export function useOperationEvents(operationId: string | null): OperationEventsState {
  const queryClient = useQueryClient()
  const [streamStatus, setStreamStatus] = useState<OperationEventStreamStatus | 'idle'>('idle')
  const [streamError, setStreamError] = useState<Error | null>(null)
  const eventsQuery = useQuery({
    queryKey: operationQueryKeys.events(operationId ?? 'none'),
    queryFn: async (): Promise<OperationEvent[]> => [],
    enabled: false,
    initialData: [],
  })

  useEffect(() => {
    if (operationId === null) {
      setStreamStatus('idle')
      setStreamError(null)
      return
    }

    setStreamError(null)
    const key = operationQueryKeys.events(operationId)
    const existing = queryClient.getQueryData<OperationEvent[]>(key) ?? []
    const fromSequence = existing.at(-1)?.sequence ?? 0
    return subscribeToOperationEvents(operationId, fromSequence, {
      onEvent: (operationEvent) => {
        queryClient.setQueryData<OperationEvent[]>(key, (current = []) => {
          if (current.some((candidate) => candidate.sequence === operationEvent.sequence)) {
            return current
          }
          return [...current, operationEvent].sort((left, right) => left.sequence - right.sequence)
        })
        queryClient.setQueryData<OperationState>(operationQueryKeys.state(operationId), (current) =>
          updateOperationState(current, operationEvent),
        )
      },
      onStatus: setStreamStatus,
      onError: setStreamError,
    })
  }, [operationId, queryClient])

  return { events: eventsQuery.data, streamStatus, streamError }
}

function updateOperationState(
  current: OperationState | undefined,
  operationEvent: OperationEvent,
): OperationState | undefined {
  if (!current) return current
  const payload = operationEvent.payload
  const status: OperationState['status'] =
    payload.kind === 'completed'
      ? payload.status
      : payload.kind === 'failed'
        ? 'failed'
        : payload.kind === 'accepted'
          ? 'accepted'
          : current.status === 'cancelling'
            ? 'cancelling'
            : 'running'
  const terminal = isOperationTerminal(status)
  return {
    ...current,
    status,
    lastSequence: Math.max(current.lastSequence, operationEvent.sequence),
    updatedAtUtc: operationEvent.timestampUtc,
    completedAtUtc: terminal ? operationEvent.timestampUtc : (current.completedAtUtc ?? null),
    error: payload.kind === 'failed' ? payload.error : (current.error ?? null),
  }
}
