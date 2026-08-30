import { type KeyboardEvent, type PointerEvent, type RefObject, useRef } from 'react'
import { clampSourcePanePercent, maximumSourcePanePercent, minimumSourcePanePercent } from './paneSplitPreference'

export interface PaneSplitSeparatorProps {
  containerRef: RefObject<HTMLElement | null>
  isMobile: boolean
  sourcePercent: number
  onChange: (percent: number) => void
  onReset: () => void
}

export function PaneSplitSeparator({ containerRef, isMobile, sourcePercent, onChange, onReset }: PaneSplitSeparatorProps) {
  const activePointer = useRef<number | null>(null)

  const updateFromPointer = (event: PointerEvent<HTMLHRElement>) => {
    const bounds = containerRef.current?.getBoundingClientRect()
    if (!bounds) return
    const percent = paneSplitPercentFromPointer(isMobile ? event.clientY : event.clientX, isMobile ? bounds.top : bounds.left, isMobile ? bounds.height : bounds.width)
    if (percent !== null) onChange(percent)
  }

  const finishPointer = (event: PointerEvent<HTMLHRElement>, update: boolean) => {
    if (activePointer.current !== event.pointerId) return
    if (update) updateFromPointer(event)
    activePointer.current = null
    event.currentTarget.dataset.resizing = 'false'
    if (event.currentTarget.hasPointerCapture?.(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId)
    }
  }

  const onKeyDown = (event: KeyboardEvent<HTMLHRElement>) => {
    const step = event.shiftKey ? 5 : 1
    let next: number | null = null
    if (event.key === (isMobile ? 'ArrowUp' : 'ArrowLeft')) next = sourcePercent - step
    if (event.key === (isMobile ? 'ArrowDown' : 'ArrowRight')) next = sourcePercent + step
    if (event.key === 'Home') next = minimumSourcePanePercent
    if (event.key === 'End') next = maximumSourcePanePercent
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onReset()
      return
    }
    if (next === null) return
    event.preventDefault()
    onChange(clampSourcePanePercent(next))
  }

  return (
    <hr
      className="pane-separator"
      aria-label="Resize source and result panes"
      aria-orientation={isMobile ? 'horizontal' : 'vertical'}
      aria-valuemin={minimumSourcePanePercent}
      aria-valuemax={maximumSourcePanePercent}
      aria-valuenow={sourcePercent}
      aria-valuetext={`Source ${sourcePercent}%, result ${Math.round((100 - sourcePercent) * 10) / 10}%`}
      tabIndex={0}
      title="Drag to resize panes; double-click to reset"
      data-resizing="false"
      onDoubleClick={(event) => {
        event.preventDefault()
        onReset()
      }}
      onKeyDown={onKeyDown}
      onPointerDown={(event) => {
        if (event.pointerType === 'mouse' && event.button !== 0) return
        event.preventDefault()
        activePointer.current = event.pointerId
        event.currentTarget.dataset.resizing = 'true'
        event.currentTarget.setPointerCapture?.(event.pointerId)
        updateFromPointer(event)
      }}
      onPointerMove={(event) => {
        if (activePointer.current === event.pointerId) updateFromPointer(event)
      }}
      onPointerUp={(event) => finishPointer(event, true)}
      onPointerCancel={(event) => finishPointer(event, false)}
    />
  )
}

export function paneSplitPercentFromPointer(coordinate: number, start: number, length: number): number | null {
  if (![coordinate, start, length].every(Number.isFinite) || length <= 0) return null
  return clampSourcePanePercent(((coordinate - start) / length) * 100)
}
