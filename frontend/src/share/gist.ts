import type { GistLoadOptions } from '../api/client'
import { ShareUrlError } from './errors'

export interface ParsedGistFragment {
  id: string
  options: GistLoadOptions
}

export function parseGistFragment(fragment: string): ParsedGistFragment | null {
  let value = fragment.startsWith('#') ? fragment.slice(1) : fragment
  try {
    value = decodeURIComponent(value)
  } catch {
    throw new ShareUrlError('invalid-fragment', 'The Gist URL fragment is not valid UTF-8.')
  }
  if (!value.startsWith('gist:')) return null
  const segments = value.slice('gist:'.length).split('/')
  const id = segments[0] ?? ''
  if (!/^[0-9a-fA-F]{5,64}$/.test(id)) {
    throw new ShareUrlError('invalid-fragment', 'The GitHub Gist ID is invalid.')
  }
  const target = segmentValue(segments[1])
  const branch = segmentValue(segments[2])
  let mode: 'debug' | 'release' | null = null
  if (segments.length > 1) mode = segments[3]?.toLowerCase() === 'debug' ? 'debug' : 'release'
  return { id, options: { target, branch, mode } }
}

export function gistFragment(id: string): string {
  if (!/^[0-9a-fA-F]{5,64}$/.test(id)) throw new Error('The GitHub Gist ID is invalid.')
  return `#gist:${id}`
}

function segmentValue(value: string | undefined): string | null {
  return !value || value === '_' ? null : value
}
