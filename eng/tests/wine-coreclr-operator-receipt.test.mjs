import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import path from 'node:path'
import test from 'node:test'

import {
  createWineCoreClrOperatorReceipt,
  serializeWineCoreClrOperatorReceipt,
  signWineCoreClrOperatorReceipt,
  verifyWineCoreClrOperatorReceipt,
  maximumWineOperatorSizeBytes,
  wineCoreClrOperatorCommittedFiles,
  wineCoreClrOperatorReceiptPublicKeyPath,
} from './wine-coreclr-operator-receipt.mjs'

function receipt() {
  return createWineCoreClrOperatorReceipt({
    source: {
      revision: 'a'.repeat(40),
      tree: 'b'.repeat(40),
      files: Object.fromEntries(wineCoreClrOperatorCommittedFiles.map((file, index) => [
        file,
        `sha256:${String(index + 1).repeat(64)}`,
      ])),
    },
    operator: {
      reference: `registry.example/operator@sha256:${'d'.repeat(64)}`, imageId: `sha256:${'e'.repeat(64)}`,
      sizeBytes: 1, platform: 'linux/amd64', userspace: { version: 'wine', digest: `sha256:${'f'.repeat(64)}`, sourceUri: 'https://example.test/wine' },
      baseImage: `registry.example/base@sha256:${'0'.repeat(64)}`, labels: { z: 'last', a: 'first' },
    },
  })
}

test('operator receipt is canonical, Ed25519 signed, and rejects tampering', () => {
  const { privateKey, publicKey } = crypto.generateKeyPairSync('ed25519')
  const value = receipt()
  const bytes = serializeWineCoreClrOperatorReceipt(value)
  const signature = signWineCoreClrOperatorReceipt(value, privateKey)
  assert.equal(bytes.toString('utf8').includes('\r'), false)
  assert.deepEqual(verifyWineCoreClrOperatorReceipt(bytes, signature, { publicKey }), value)
  assert.deepEqual(
    verifyWineCoreClrOperatorReceipt(bytes, Buffer.from(`${signature}\n`), { publicKey }),
    value,
  )
  assert.throws(() => verifyWineCoreClrOperatorReceipt(Buffer.from(bytes.toString('utf8').replace('linux/amd64', 'linux/arm64')), signature, { publicKey }), /platform|signature/)
  assert.throws(() => verifyWineCoreClrOperatorReceipt(Buffer.from(`\ufeff${bytes}`), signature, { publicKey }), /canonical LF/)
  assert.throws(() => verifyWineCoreClrOperatorReceipt(bytes, Buffer.from(signature, 'base64').map((byte, index) => index === 0 ? byte ^ 1 : byte), { publicKey }), /signature/)
  for (const malformed of [
    ` ${signature}`,
    `${signature} `,
    `${signature.slice(0, 20)}\n${signature.slice(20)}`,
    signature.slice(0, -2),
    `${signature.slice(0, -2)}__`,
  ]) {
    assert.throws(
      () => verifyWineCoreClrOperatorReceipt(bytes, malformed, { publicKey }),
      /canonical 64-byte Ed25519 signature|canonical Base64 text/,
    )
  }
})

test('operator receipt requires the exact committed source closure and a portable public key path', () => {
  assert.equal(path.isAbsolute(wineCoreClrOperatorReceiptPublicKeyPath), true)
  const value = receipt()
  const missing = structuredClone(value)
  delete missing.source.files[wineCoreClrOperatorCommittedFiles[0]]
  assert.throws(() => createWineCoreClrOperatorReceipt(missing), /exactly the four committed/)

  const extra = structuredClone(value)
  extra.source.files['profiles/extra.json'] = `sha256:${'a'.repeat(64)}`
  assert.throws(() => createWineCoreClrOperatorReceipt(extra), /exactly the four committed/)
})

test('operator receipt rejects non-ASCII canonicalization inputs and unknown nested keys', () => {
  const value = receipt()
  for (const mutate of [
    candidate => { candidate.operator.reference = `registry.example/wein\u00e9@sha256:${'d'.repeat(64)}` },
    candidate => { candidate.operator.baseImage = `registry.example/bas\u00e9@sha256:${'0'.repeat(64)}` },
    candidate => { candidate.operator.labels['n\u00e4me'] = 'value' },
    candidate => { candidate.operator.userspace.sourceUri = 'https://example.test/win\u00e9' },
    candidate => { candidate.operator.unexpected = 'value' },
    candidate => { candidate.source.extra = 'value' },
  ]) {
    const candidate = structuredClone(value)
    mutate(candidate)
    assert.throws(() => createWineCoreClrOperatorReceipt(candidate), /invalid Wine operator receipt/)
  }
})

test('operator receipt canonical JSON uses ordinal label order and bounds image size to 16 GiB', () => {
  const value = receipt()
  value.operator.labels = { 2: 'two', 10: 'ten', 1: 'one' }
  assert.match(
    serializeWineCoreClrOperatorReceipt(value).toString('utf8'),
    /"labels":\{"1":"one","10":"ten","2":"two"\}/,
  )

  value.operator.sizeBytes = maximumWineOperatorSizeBytes
  assert.doesNotThrow(() => createWineCoreClrOperatorReceipt(value))
  value.operator.sizeBytes++
  assert.throws(() => createWineCoreClrOperatorReceipt(value),
    /operator\.sizeBytes must be between 1 and 17179869184/)
})
