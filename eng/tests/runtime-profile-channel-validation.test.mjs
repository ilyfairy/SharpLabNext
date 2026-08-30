import test from 'node:test'
import assert from 'node:assert/strict'

import { validateRuntimeProfileChannels } from './runtime-profile-channel-validation.mjs'

const catalog = {
  runtimes: [
    {
      id: 'active-runtime',
      resolvedVersion: '10.0.9',
      runtimeImageId: 'sha256:active',
      availability: { installed: true, health: 'healthy' },
    },
    {
      id: 'blocked-runtime',
      resolvedVersion: '10.0.8',
      runtimeImageId: 'sha256:blocked',
      availability: { installed: false, health: 'blocked' },
    },
  ],
}

const profiles = new Map([
  ['profiles/runtimes/active-runtime.json', {
    id: 'active-runtime',
    runtimeVersion: '10.0.9',
    runtimeImageId: 'sha256:active',
  }],
  ['profiles/runtimes/active-missing.json', {
    id: 'missing-runtime',
    runtimeVersion: '1.0.0',
    runtimeImageId: 'sha256:missing',
  }],
  ['profiles/runtimes/candidates/new-runtime.json', {
    id: 'new-runtime',
    runtimeVersion: '11.0.0',
    runtimeImageId: 'sha256:new',
  }],
  ['profiles/runtimes/candidates/active-runtime.json', {
    id: 'active-runtime',
    runtimeVersion: '10.0.10',
    runtimeImageId: 'sha256:candidate',
  }],
])

const readProfile = path => profiles.get(path) ?? (() => {
  throw new Error('fixture profile is missing')
})()

test('review-only candidates do not require Catalog identity closure', () => {
  const failures = validateRuntimeProfileChannels(
    ['profiles/runtimes/candidates/new-runtime.json', 'profiles/runtimes/candidates/active-runtime.json'],
    catalog,
    readProfile,
  )

  assert.deepEqual(failures, [])
})

test('active profiles require a selectable Catalog runtime and matching identity', () => {
  const failures = validateRuntimeProfileChannels(
    ['profiles/runtimes/active-runtime.json', 'profiles/runtimes/active-missing.json'],
    catalog,
    readProfile,
  )

  assert.deepEqual(failures, [
    "profiles/runtimes/active-missing.json: runtime profile ID 'missing-runtime' is absent from the Catalog",
  ])
})

test('active profiles cannot stage a different version or image identity', () => {
  const profilesWithMismatch = new Map(profiles)
  profilesWithMismatch.set('profiles/runtimes/active-mismatch.json', {
    id: 'active-runtime',
    runtimeVersion: '10.0.10',
    runtimeImageId: 'sha256:candidate',
  })

  const failures = validateRuntimeProfileChannels(
    ['profiles/runtimes/active-mismatch.json'],
    catalog,
    path => profilesWithMismatch.get(path),
  )

  assert.deepEqual(failures, [
    "profiles/runtimes/active-mismatch.json: runtimeVersion '10.0.10' does not match Catalog '10.0.9'",
    'profiles/runtimes/active-mismatch.json: runtimeImageId does not match the selectable Catalog identity',
  ])
})

test('active profiles mapped to blocked Catalog entries fail closed', () => {
  const profilesWithBlocked = new Map(profiles)
  profilesWithBlocked.set('profiles/runtimes/blocked-runtime.json', {
    id: 'blocked-runtime',
    runtimeVersion: '10.0.8',
    runtimeImageId: 'sha256:blocked',
  })

  const failures = validateRuntimeProfileChannels(
    ['profiles/runtimes/blocked-runtime.json'],
    catalog,
    path => profilesWithBlocked.get(path),
  )

  assert.deepEqual(failures, [
    "profiles/runtimes/blocked-runtime.json: active profile 'blocked-runtime' maps to a non-selectable Catalog runtime",
  ])
})
