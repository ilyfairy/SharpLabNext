/// <reference types="node" />

import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const publicAsset = (name: string) => resolve(process.cwd(), 'public', name)
const pngDimensions = (name: string) => {
  const content = readFileSync(publicAsset(name))
  expect(content.subarray(1, 4).toString('ascii')).toBe('PNG')
  return {
    width: content.readUInt32BE(16),
    height: content.readUInt32BE(20),
  }
}

describe('SharpLabNext brand assets', () => {
  it('uses the same compact brace mark in the header and browser icon', () => {
    const logo = readFileSync(publicAsset('logo-mark.svg'), 'utf8')
    const favicon = readFileSync(publicAsset('favicon.svg'), 'utf8')
    const mark = /<path data-brand-mark="true"[^>]+d="([^"]+)"/.exec(logo)?.[1]

    expect(mark).toBeTruthy()
    expect(logo).not.toContain('<rect')
    expect(favicon).toContain('<rect width="128" height="128" fill="#4589e8"/>')
    expect(favicon).toContain(`d="${mark}"`)
  })

  it.each([
    ['favicon-32.png', 32],
    ['apple-touch-icon.png', 180],
    ['app-icon-192.png', 192],
    ['app-icon-512.png', 512],
  ])('renders %s at its declared size', (fileName, size) => {
    expect(pngDimensions(fileName)).toEqual({ width: size, height: size })
  })

  it('exposes the same assets through favicon, touch icon, and PWA entry points', () => {
    const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8')
    const manifest = JSON.parse(readFileSync(publicAsset('manifest.webmanifest'), 'utf8')) as {
      theme_color: string
      icons: Array<{ src: string; sizes: string }>
    }

    expect(html).toContain('<meta name="theme-color" content="#4589e8" />')
    expect(html).toContain('<link rel="icon" type="image/svg+xml" href="/favicon.svg" />')
    expect(html).toContain('<link rel="icon" type="image/png" sizes="32x32" href="/favicon-32.png" />')
    expect(html).toContain('<link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png" />')
    expect(html).toContain('<link rel="manifest" href="/manifest.webmanifest" />')
    expect(manifest.theme_color).toBe('#4589e8')
    expect(manifest.icons).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ src: '/app-icon-192.png', sizes: '192x192' }),
        expect.objectContaining({ src: '/app-icon-512.png', sizes: '512x512' }),
        expect.objectContaining({ src: '/favicon.svg', sizes: 'any' }),
      ]),
    )
  })
})
