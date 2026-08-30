import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const wineCoreClrOperatorReceiptKeyId = 'sha256:16cdb3dd05ddc65de942187de063606b06c7c56c60e1a3394d166724d649e5a1'
export const wineCoreClrOperatorReceiptPublicKeyPath = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'profiles', 'trust', 'wine-coreclr-operator-receipt-public.pem')
export const wineCoreClrOperatorCommittedFiles = Object.freeze(['deploy/docker/Dockerfile.operator-wine-coreclr', 'eng/bake.hcl', 'profiles/lock.json', 'profiles/runtime-wine-packages.json'])

const gitObject = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const sha256 = /^sha256:[0-9a-f]{64}$/
const immutableImage = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const canonicalSignature = /^[A-Za-z0-9+/]{86}==$/
const printableAscii = /^[\x20-\x7e]+$/
export const maximumWineOperatorSizeBytes = 17_179_869_184

function exactKeys(value, keys) {
  return value !== null && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key))
}

function boundedAscii(value, maximum = 2048) { return typeof value === 'string' && value.length > 0 && value.length <= maximum && printableAscii.test(value); }

function publicKeyId(publicKey) {
  const der = crypto.createPublicKey(publicKey).export({ type: 'spki', format: 'der' })
  return `sha256:${crypto.createHash('sha256').update(der).digest('hex')}`
}

function serializeCanonicalJson(value) {
  if (value === null || typeof value !== 'object') return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map(serializeCanonicalJson).join(',')}]`
  // JSON.stringify reorders integer-like own property names. Emit sorted members
  // directly so canonical bytes use the same ordinal sort as the C# verifier.
  return `{${Object.keys(value).sort().map(key =>
    `${JSON.stringify(key)}:${serializeCanonicalJson(value[key])}`).join(',')}}`
}

/** Canonical JSON: recursively sorted keys, UTF-8, LF termination, no BOM. */
export function serializeWineCoreClrOperatorReceipt(receipt) {
  return Buffer.from(`${serializeCanonicalJson(receipt)}\n`, 'utf8')
}

function receiptFailures(receipt) {
  const failures = []
  if (!exactKeys(receipt, ['schemaVersion', 'keyId', 'source', 'operator'])) {
    failures.push('receipt must contain exactly schemaVersion, keyId, source, and operator')
    return failures
  }
  if (receipt?.schemaVersion !== 1) failures.push('schemaVersion must be 1')
  if (!boundedAscii(receipt?.keyId, 72) || receipt?.keyId !== wineCoreClrOperatorReceiptKeyId) failures.push('keyId is not the committed Wine operator receipt key')
  if (!exactKeys(receipt.source, ['revision', 'tree', 'files'])) failures.push('source must contain exactly revision, tree, and files')
  if (!boundedAscii(receipt?.source?.revision, 64) || !gitObject.test(receipt?.source?.revision ?? '')) failures.push('source.revision must be a full Git commit')
  if (!boundedAscii(receipt?.source?.tree, 64) || !gitObject.test(receipt?.source?.tree ?? '')) failures.push('source.tree must be a full Git tree')
  const sourceFiles = receipt?.source?.files
  const sourceFileNames = sourceFiles !== null && typeof sourceFiles === 'object' && !Array.isArray(sourceFiles)
    ? Object.keys(sourceFiles).sort()
    : []
  if (JSON.stringify(sourceFileNames) !== JSON.stringify([...wineCoreClrOperatorCommittedFiles].sort()) ||
      sourceFileNames.some(name => !boundedAscii(name, 256) || !boundedAscii(sourceFiles[name], 72) || !sha256.test(sourceFiles[name] ?? ''))) {
    failures.push('source.files must contain exactly the four committed Wine operator inputs with SHA-256 digests')
  }
  if (!exactKeys(receipt.operator, ['reference', 'imageId', 'sizeBytes', 'platform', 'userspace', 'baseImage', 'labels'])) {
    failures.push('operator must contain exactly the signed operator identity fields')
    return failures
  }
  if (!boundedAscii(receipt?.operator?.reference, 512) || !immutableImage.test(receipt?.operator?.reference ?? '')) failures.push('operator.reference must be immutable')
  if (!boundedAscii(receipt?.operator?.imageId, 72) || !sha256.test(receipt?.operator?.imageId ?? '')) failures.push('operator.imageId must be sha256')
  if (!Number.isSafeInteger(receipt?.operator?.sizeBytes) || receipt.operator.sizeBytes <= 0 ||
      receipt.operator.sizeBytes > maximumWineOperatorSizeBytes) {
    failures.push(`operator.sizeBytes must be between 1 and ${maximumWineOperatorSizeBytes}`)
  }
  if (!boundedAscii(receipt?.operator?.platform, 32) || receipt?.operator?.platform !== 'linux/amd64') failures.push('operator.platform must be linux/amd64')
  const userspace = receipt?.operator?.userspace
  if (!exactKeys(userspace, ['version', 'digest', 'sourceUri']) ||
      !boundedAscii(userspace?.version, 256) ||
      !boundedAscii(userspace?.digest, 72) || !sha256.test(userspace?.digest ?? '') ||
      !boundedAscii(userspace?.sourceUri) || !userspace.sourceUri.startsWith('https://')) {
    failures.push('operator.userspace must bind version, SHA-256 digest, and HTTPS source URI')
  }
  if (!boundedAscii(receipt?.operator?.baseImage, 512) || !immutableImage.test(receipt?.operator?.baseImage ?? '')) failures.push('operator.baseImage must be immutable')
  const labels = receipt?.operator?.labels
  if (labels === null || typeof labels !== 'object' || Array.isArray(labels) ||
      Object.keys(labels).length === 0 || Object.keys(labels).some(key => !boundedAscii(key, 256)) ||
      Object.values(labels).some(value => !boundedAscii(value))) {
    failures.push('operator.labels must be a non-empty string map')
  }
  return failures
}

export function createWineCoreClrOperatorReceipt(input) {
  const receipt = Object.freeze({ schemaVersion: 1, keyId: wineCoreClrOperatorReceiptKeyId, ...input })
  const failures = receiptFailures(receipt)
  if (failures.length) throw new Error(`invalid Wine operator receipt: ${failures.join('; ')}`)
  return receipt
}

export function signWineCoreClrOperatorReceipt(receipt, privateKey) {
  return crypto.sign(null, serializeWineCoreClrOperatorReceipt(receipt), privateKey).toString('base64')
}

export function verifyWineCoreClrOperatorReceipt(receiptBytes, signature, options = {}) {
  const publicKey = options.publicKey ?? fs.readFileSync(options.publicKeyPath ?? wineCoreClrOperatorReceiptPublicKeyPath)
  if (options.publicKey === undefined && options.publicKeyPath === undefined &&
      publicKeyId(publicKey) !== wineCoreClrOperatorReceiptKeyId) {
    throw new Error('committed Wine operator receipt public key does not match its keyId')
  }
  const text = Buffer.isBuffer(receiptBytes) ? receiptBytes.toString('utf8') : String(receiptBytes)
  if (text.charCodeAt(0) === 0xfeff || text.includes('\r') || !text.endsWith('\n')) throw new Error('receipt must be canonical LF UTF-8 JSON')
  let receipt
  try { receipt = JSON.parse(text) } catch (error) { throw new Error(`receipt is not JSON: ${error.message}`) }
  const failures = receiptFailures(receipt)
  if (failures.length) throw new Error(`invalid Wine operator receipt: ${failures.join('; ')}`)
  const canonical = serializeWineCoreClrOperatorReceipt(receipt)
  if (!Buffer.from(text, 'utf8').equals(canonical)) throw new Error('receipt is not canonical')
  const signatureText = Buffer.isBuffer(signature) ? signature.toString('utf8') : String(signature)
  if (signatureText.includes('\r') || (signatureText.includes('\n') && !signatureText.endsWith('\n')) ||
      signatureText.slice(0, -1).includes('\n')) {
    throw new Error('Wine operator receipt signature must be canonical Base64 text')
  }
  const signatureBase64 = signatureText.endsWith('\n') ? signatureText.slice(0, -1) : signatureText
  if (!canonicalSignature.test(signatureBase64)) {
    throw new Error('Wine operator receipt signature must be one canonical 64-byte Ed25519 signature')
  }
  const signatureBytes = Buffer.from(signatureBase64, 'base64')
  if (signatureBytes.length !== 64 || signatureBytes.toString('base64') !== signatureBase64) {
    throw new Error('Wine operator receipt signature must be one canonical 64-byte Ed25519 signature')
  }
  if (!crypto.verify(null, canonical, publicKey, signatureBytes)) throw new Error('Wine operator receipt signature is invalid')
  return Object.freeze(receipt)
}

export function receiptSha256(receipt) {
  return `sha256:${crypto.createHash('sha256').update(serializeWineCoreClrOperatorReceipt(receipt)).digest('hex')}`
}

export function writeWineCoreClrOperatorReceiptAtomically(receiptPath, receipt, signature) {
  const directory = path.dirname(receiptPath)
  fs.mkdirSync(directory, { recursive: true })
  const suffix = `${process.pid}.${crypto.randomBytes(8).toString('hex')}`
  const receiptTemp = `${receiptPath}.${suffix}.tmp`
  const signaturePath = `${receiptPath}.sig`
  const signatureTemp = `${signaturePath}.${suffix}.tmp`
  try {
    fs.writeFileSync(receiptTemp, serializeWineCoreClrOperatorReceipt(receipt), { mode: 0o600 })
    fs.writeFileSync(signatureTemp, `${signature.trim()}\n`, { mode: 0o600 })
    fs.renameSync(receiptTemp, receiptPath)
    fs.renameSync(signatureTemp, signaturePath)
  } finally {
    for (const filename of [receiptTemp, signatureTemp]) { try { fs.unlinkSync(filename) } catch {} }
  }
  return Object.freeze({ receiptPath, signaturePath })
}
