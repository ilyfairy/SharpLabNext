import assert from 'node:assert/strict'
import test from 'node:test'

import {
  isSupportedJsonSchemaFormat,
  isValidJsonSchemaFormat,
} from './json-schema-formats.mjs'

test('the schema gate supports every format used by maintained schemas', () => {
  assert.equal(isSupportedJsonSchemaFormat('date'), true)
  assert.equal(isSupportedJsonSchemaFormat('date-time'), true)
  assert.equal(isSupportedJsonSchemaFormat('uri'), true)
  assert.equal(isSupportedJsonSchemaFormat('email'), false)
})

test('date and date-time formats reject calendar and clock overflow', () => {
  assert.equal(isValidJsonSchemaFormat('2026-02-28', 'date'), true)
  assert.equal(isValidJsonSchemaFormat('2024-02-29', 'date'), true)
  assert.equal(isValidJsonSchemaFormat('0099-01-01', 'date'), true)
  assert.equal(isValidJsonSchemaFormat('2025-02-29', 'date'), false)
  assert.equal(isValidJsonSchemaFormat('2026-13-01', 'date'), false)

  assert.equal(isValidJsonSchemaFormat('2026-07-23T12:34:56Z', 'date-time'), true)
  assert.equal(isValidJsonSchemaFormat('2026-07-23T12:34:56.123+08:00', 'date-time'), true)
  assert.equal(isValidJsonSchemaFormat('2026-07-23T24:00:00Z', 'date-time'), false)
  assert.equal(isValidJsonSchemaFormat('2026-07-23T12:60:00Z', 'date-time'), false)
  assert.equal(isValidJsonSchemaFormat('2026-07-23T12:34:56', 'date-time'), false)
})

test('URI format requires an absolute URI without control characters', () => {
  assert.equal(isValidJsonSchemaFormat('https://example.com/runtime.tar.gz', 'uri'), true)
  assert.equal(isValidJsonSchemaFormat('urn:sharplabnext:runtime:10', 'uri'), true)
  assert.equal(isValidJsonSchemaFormat('/relative/path', 'uri'), false)
  assert.equal(isValidJsonSchemaFormat('https://example.com/a b', 'uri'), false)
  assert.equal(isValidJsonSchemaFormat('https://', 'uri'), false)
})
