import { useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { resolveSelection } from '../api/client'
import type {
  CatalogDocument,
  ResolveSelectionRequest,
  ResolveSelectionResponse,
  SelectionChange,
} from '../api/types'
import { useWorkbenchStore } from './store'

interface ResolutionSnapshot {
  request: ResolveSelectionRequest
  selectionRevision: number
  workspaceRevision: number
  signature: string
}

interface DebouncedResolutionSnapshot extends ResolutionSnapshot {
  initial: boolean
}

function useDebouncedSnapshot(
  value: ResolutionSnapshot | null,
  initialSnapshotReady: boolean,
  delay: number,
): [DebouncedResolutionSnapshot | null, (value: ResolutionSnapshot) => void] {
  const [debounced, setDebounced] = useState<DebouncedResolutionSnapshot | null>(null)
  const initialSnapshotAccepted = useRef(false)
  const initialSnapshotKey = useRef<string | null>(null)
  const initialContinuationKey = useRef<string | null>(null)
  const continueInitialSnapshot = useCallback((next: ResolutionSnapshot) => {
    initialContinuationKey.current = JSON.stringify([next.signature, next.selectionRevision])
  }, [])
  useEffect(() => {
    if (!initialSnapshotReady || !value) return
    const snapshotKey = JSON.stringify([value.signature, value.selectionRevision])
    if (!initialSnapshotAccepted.current) {
      initialSnapshotAccepted.current = true
      initialSnapshotKey.current = snapshotKey
      setDebounced({ ...value, initial: true })
      return
    }
    if (initialContinuationKey.current !== null) {
      const continuesInitialSnapshot = initialContinuationKey.current === snapshotKey
      initialContinuationKey.current = null
      if (continuesInitialSnapshot) {
        initialSnapshotKey.current = snapshotKey
        setDebounced({ ...value, initial: true })
        return
      }
    }
    if (initialSnapshotKey.current === snapshotKey) return

    const timeout = window.setTimeout(() => setDebounced({ ...value, initial: false }), delay)
    return () => window.clearTimeout(timeout)
  }, [delay, initialSnapshotReady, value])
  return [debounced, continueInitialSnapshot]
}

export function selectionRequestSignature(request: ResolveSelectionRequest): string {
  return JSON.stringify([
    request.catalogRevision,
    request.languageId,
    request.toolchainId,
    request.referenceSetId,
    request.outputId,
    request.runtimeId,
    request.buildMode,
    request.workspaceRevision,
  ])
}

export interface SelectionResolutionState {
  resolution: ResolveSelectionResponse | null
  isInitialSnapshot: boolean
  selectionChanges: SelectionChange[]
  isResolving: boolean
  error: Error | null
}

export function useSelectionResolution(
  catalog: CatalogDocument | undefined,
  initialSnapshotReady: boolean,
): SelectionResolutionState {
  const languageId = useWorkbenchStore((state) => state.languageId)
  const toolchainId = useWorkbenchStore((state) => state.toolchainId)
  const referenceSetId = useWorkbenchStore((state) => state.referenceSetId)
  const outputId = useWorkbenchStore((state) => state.outputId)
  const runtimeId = useWorkbenchStore((state) => state.runtimeId)
  const buildMode = useWorkbenchStore((state) => state.buildMode)
  const workspaceRevision = useWorkbenchStore((state) => state.workspaceRevision)
  const selectionRevision = useWorkbenchStore((state) => state.selectionRevision)
  const [lastAppliedChanges, setLastAppliedChanges] = useState<SelectionChange[]>([])

  const snapshot = useMemo<ResolutionSnapshot | null>(() => {
    if (!catalog) return null
    const request: ResolveSelectionRequest = {
      languageId,
      toolchainId,
      referenceSetId,
      outputId,
      runtimeId,
      buildMode,
      catalogRevision: catalog.revision,
      workspaceRevision,
    }
    return {
      request,
      selectionRevision,
      workspaceRevision,
      signature: selectionRequestSignature(request),
    }
  }, [
    buildMode,
    catalog,
    languageId,
    outputId,
    referenceSetId,
    runtimeId,
    selectionRevision,
    toolchainId,
    workspaceRevision,
  ])
  const [debouncedSnapshot, continueInitialSnapshot] = useDebouncedSnapshot(
    snapshot,
    initialSnapshotReady,
    250,
  )

  const resolutionQuery = useQuery({
    queryKey: ['selection-resolution', debouncedSnapshot?.signature ?? 'none'],
    queryFn: ({ signal }) => {
      if (!debouncedSnapshot) throw new Error('Selection resolution is not ready.')
      return resolveSelection(debouncedSnapshot.request, signal)
    },
    enabled: debouncedSnapshot !== null,
    staleTime: 0,
  })

  useEffect(() => {
    if (!resolutionQuery.data || !debouncedSnapshot) return
    const current = useWorkbenchStore.getState()
    const currentRequest: ResolveSelectionRequest = {
      languageId: current.languageId,
      toolchainId: current.toolchainId,
      referenceSetId: current.referenceSetId,
      outputId: current.outputId,
      runtimeId: current.runtimeId,
      buildMode: current.buildMode,
      catalogRevision: catalog?.revision ?? '',
      workspaceRevision: current.workspaceRevision,
    }
    if (selectionRequestSignature(currentRequest) !== debouncedSnapshot.signature) return

    const applied = current.applyResolvedSelection(resolutionQuery.data.effectiveSelection, {
      selectionRevision: debouncedSnapshot.selectionRevision,
      workspaceRevision: debouncedSnapshot.workspaceRevision,
    })
    if (!applied) return
    setLastAppliedChanges(resolutionQuery.data.selectionChanges)

    const normalized = useWorkbenchStore.getState()
    if (
      debouncedSnapshot.initial &&
      normalized.selectionRevision !== debouncedSnapshot.selectionRevision
    ) {
      const request: ResolveSelectionRequest = {
        languageId: normalized.languageId,
        toolchainId: normalized.toolchainId,
        referenceSetId: normalized.referenceSetId,
        outputId: normalized.outputId,
        runtimeId: normalized.runtimeId,
        buildMode: normalized.buildMode,
        catalogRevision: catalog?.revision ?? '',
        workspaceRevision: normalized.workspaceRevision,
      }
      continueInitialSnapshot({
        request,
        selectionRevision: normalized.selectionRevision,
        workspaceRevision: normalized.workspaceRevision,
        signature: selectionRequestSignature(request),
      })
    }
  }, [catalog?.revision, continueInitialSnapshot, debouncedSnapshot, resolutionQuery.data])

  const aligned = snapshot?.signature === debouncedSnapshot?.signature
  return {
    resolution: aligned ? (resolutionQuery.data ?? null) : null,
    isInitialSnapshot: aligned && debouncedSnapshot?.initial === true,
    selectionChanges: aligned
      ? (resolutionQuery.data?.selectionChanges ?? lastAppliedChanges)
      : lastAppliedChanges,
    isResolving:
      catalog !== undefined &&
      (!aligned ||
        resolutionQuery.isPending ||
        (resolutionQuery.isFetching && !resolutionQuery.data)),
    error: aligned && resolutionQuery.error instanceof Error ? resolutionQuery.error : null,
  }
}
