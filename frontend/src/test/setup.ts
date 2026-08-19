import '@testing-library/jest-dom/vitest'

if (!Range.prototype.getClientRects) {
  Object.defineProperty(Range.prototype, 'getClientRects', {
    value: () => [] as unknown as DOMRectList,
  })
}

if (!Range.prototype.getBoundingClientRect) {
  Object.defineProperty(Range.prototype, 'getBoundingClientRect', {
    value: () => new DOMRect(0, 0, 0, 0),
  })
}
