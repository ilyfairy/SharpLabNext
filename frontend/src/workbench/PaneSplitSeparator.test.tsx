import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { createRef } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PaneSplitSeparator, paneSplitPercentFromPointer } from './PaneSplitSeparator'

afterEach(cleanup)

describe('PaneSplitSeparator', () => {
  it('converts pointer positions to bounded percentages', () => {
    expect(paneSplitPercentFromPointer(350, 100, 500)).toBe(50)
    expect(paneSplitPercentFromPointer(100, 100, 500)).toBe(20)
    expect(paneSplitPercentFromPointer(600, 100, 500)).toBe(80)
    expect(paneSplitPercentFromPointer(350, 100, 0)).toBeNull()
  })

  it('supports desktop keyboard sizing and double-click reset', () => {
    const onChange = vi.fn()
    const onReset = vi.fn()
    render(
      <PaneSplitSeparator
        containerRef={createRef<HTMLElement>()}
        isMobile={false}
        sourcePercent={50}
        onChange={onChange}
        onReset={onReset}
      />,
    )
    const separator = screen.getByRole('separator', { name: 'Resize source and result panes' })
    expect(separator).toHaveAttribute('aria-orientation', 'vertical')
    expect(separator).toHaveAttribute('aria-valuenow', '50')

    fireEvent.keyDown(separator, { key: 'ArrowRight' })
    expect(onChange).toHaveBeenLastCalledWith(51)
    fireEvent.keyDown(separator, { key: 'ArrowLeft', shiftKey: true })
    expect(onChange).toHaveBeenLastCalledWith(45)
    fireEvent.keyDown(separator, { key: 'Home' })
    expect(onChange).toHaveBeenLastCalledWith(20)
    fireEvent.keyDown(separator, { key: 'End' })
    expect(onChange).toHaveBeenLastCalledWith(80)
    fireEvent.doubleClick(separator)
    expect(onReset).toHaveBeenCalledOnce()
  })

  it('uses vertical movement keys for the mobile horizontal separator', () => {
    const onChange = vi.fn()
    const onReset = vi.fn()
    render(
      <PaneSplitSeparator
        containerRef={createRef<HTMLElement>()}
        isMobile
        sourcePercent={64}
        onChange={onChange}
        onReset={onReset}
      />,
    )
    const separator = screen.getByRole('separator')
    expect(separator).toHaveAttribute('aria-orientation', 'horizontal')
    expect(separator).toHaveAttribute('aria-valuetext', 'Source 64%, result 36%')
    fireEvent.keyDown(separator, { key: 'ArrowUp' })
    expect(onChange).toHaveBeenLastCalledWith(63)
    fireEvent.keyDown(separator, { key: 'ArrowDown', shiftKey: true })
    expect(onChange).toHaveBeenLastCalledWith(69)
    fireEvent.keyDown(separator, { key: 'Enter' })
    expect(onReset).toHaveBeenCalledOnce()
  })
})
