import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const runtimePromotionPlanKeyId = 'sha256:d07b3d023359dfea9b8994115095768f9070ba6312092404b132e83d0e45d200'
export const runtimePromotionPlanPublicKeyPath = path.join(
  path.dirname(fileURLToPath(import.meta.url)), 'profiles', 'trust', 'runtime-promotion-plan-public.pem')
const signaturePattern = /^[A-Za-z0-9+/]{86}==$/

function serializeCanonicalJson(value) {
  if (value === null || typeof value !== 'object') return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map(serializeCanonicalJson).join(',')}]`
  // JSON.stringify reorders integer-like own property names. Write object members
  // ourselves so this remains byte-for-byte aligned with C# StringComparer.Ordinal.
  return `{${Object.keys(value).sort().map(key =>
    `${JSON.stringify(key)}:${serializeCanonicalJson(value[key])}`).join(',')}}`
}

export function serializeRuntimePromotionPlan(plan) {
  return Buffer.from(`${serializeCanonicalJson(plan)}\n`, 'utf8')
}

export function sha256(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}

export function runtimePromotionPlanExpectedKeyId(options = {}) {
  if ((options.publicKey !== undefined || options.publicKeyPath !== undefined) &&
      options.keyId === undefined) {
    throw new Error('custom runtime promotion plan public key requires an explicit keyId')
  }
  return options.keyId ?? runtimePromotionPlanKeyId
}

function keyId(publicKey) {
  const key = publicKey?.type === 'public' ? publicKey : crypto.createPublicKey(publicKey)
  return `sha256:${crypto.createHash('sha256').update(
    key.export({ type: 'spki', format: 'der' }),
  ).digest('hex')}`
}

export function signRuntimePromotionPlan(planBytes, privateKey) {
  return crypto.sign(null, planBytes, privateKey).toString('base64')
}

export function verifyRuntimePromotionPlanSignature(planBytes, signature, options = {}) {
  const publicKeyPath = options.publicKeyPath ?? runtimePromotionPlanPublicKeyPath
  const publicKey = options.publicKey ?? fs.readFileSync(publicKeyPath)
  if (keyId(publicKey) !== runtimePromotionPlanExpectedKeyId(options)) {
    throw new Error('committed runtime promotion plan public key does not match its keyId')
  }
  const text = Buffer.isBuffer(signature) ? signature.toString('utf8') : String(signature)
  if (text.includes('\r') || (text.includes('\n') && !text.endsWith('\n')) || text.slice(0, -1).includes('\n')) {
    throw new Error('runtime promotion plan signature must be canonical Base64 text')
  }
  const base64 = text.endsWith('\n') ? text.slice(0, -1) : text
  if (!signaturePattern.test(base64)) throw new Error('runtime promotion plan signature must be one canonical 64-byte Ed25519 signature')
  const bytes = Buffer.from(base64, 'base64')
  if (bytes.length !== 64 || bytes.toString('base64') !== base64 || !crypto.verify(null, planBytes, publicKey, bytes)) {
    throw new Error('runtime promotion plan signature is invalid')
  }
}

export function runtimePromotionPlanSignaturePath(profileId) {
  return `profiles/runtime-promotion-plans/${profileId}.json.sig`
}
