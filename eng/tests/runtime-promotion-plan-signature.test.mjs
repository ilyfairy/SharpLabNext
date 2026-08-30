import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import test from 'node:test'

import {
  runtimePromotionPlanExpectedKeyId,
  serializeRuntimePromotionPlan,
  signRuntimePromotionPlan,
  verifyRuntimePromotionPlanSignature,
} from './runtime-promotion-plan-signature.mjs'

test('promotion plan canonical JSON preserves ordinal order for numeric-like label keys', () => {
  const bytes = serializeRuntimePromotionPlan({ labels: { 2: 'two', 10: 'ten', 1: 'one' } })
  assert.equal(bytes.toString('utf8'), '{"labels":{"1":"one","10":"ten","2":"two"}}\n')
})

test('production promotion-plan trust ignores public-key environment overrides', t => {
  const originalPath = process.env.RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH
  const originalKeyId = process.env.RUNTIME_PROMOTION_PLAN_KEY_ID
  t.after(() => {
    if (originalPath === undefined) delete process.env.RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH
    else process.env.RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH = originalPath
    if (originalKeyId === undefined) delete process.env.RUNTIME_PROMOTION_PLAN_KEY_ID
    else process.env.RUNTIME_PROMOTION_PLAN_KEY_ID = originalKeyId
  })

  const keys = crypto.generateKeyPairSync('ed25519')
  const bytes = serializeRuntimePromotionPlan({ schemaVersion: 1 })
  const signature = signRuntimePromotionPlan(bytes, keys.privateKey)
  process.env.RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH = 'attacker-controlled.pem'
  process.env.RUNTIME_PROMOTION_PLAN_KEY_ID = 'sha256:deadbeef'

  assert.notEqual(runtimePromotionPlanExpectedKeyId(), process.env.RUNTIME_PROMOTION_PLAN_KEY_ID)
  assert.throws(() => verifyRuntimePromotionPlanSignature(bytes, signature),
    /runtime promotion plan signature is invalid/)
  assert.throws(() => verifyRuntimePromotionPlanSignature(bytes, signature, {
    publicKey: keys.publicKey,
  }), /requires an explicit keyId/)
  const keyId = `sha256:${crypto.createHash('sha256').update(
    keys.publicKey.export({ type: 'spki', format: 'der' }),
  ).digest('hex')}`
  assert.doesNotThrow(() => verifyRuntimePromotionPlanSignature(bytes, signature, {
    publicKey: keys.publicKey,
    keyId,
  }))
  assert.throws(() => verifyRuntimePromotionPlanSignature(bytes, signature, {
    publicKey: keys.publicKey,
    keyId: `sha256:${'0'.repeat(64)}`,
  }), /public key does not match its keyId/)
})
