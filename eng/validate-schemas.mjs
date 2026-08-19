import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { validateRuntimeProfileChannels } from './runtime-profile-channel-validation.mjs'
import { validateRuntimePromotionReceipts } from './runtime-promotion-receipt-validation.mjs'
import {
  isSupportedJsonSchemaFormat,
  isValidJsonSchemaFormat,
} from './json-schema-formats.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const schemaDirectory = path.join(repositoryRoot, 'schemas')
let releaseLockPath = 'profiles/lock.json'
for (let index = 2; index < process.argv.length; index += 1) {
  if (process.argv[index] !== '--release-lock' || index + 1 >= process.argv.length) {
    console.error('Usage: node eng/validate-schemas.mjs [--release-lock PATH]')
    process.exit(64)
  }
  releaseLockPath = path.resolve(repositoryRoot, process.argv[index + 1])
  index += 1
}
const expectedSchemas = new Set([
  'artifact-manifest.schema.json',
  'base-images.schema.json',
  'catalog.schema.json',
  'deployment-images.schema.json',
  'license-policy.schema.json',
  'language-worker-capability.schema.json',
  'maintained-provenance.schema.json',
  'profile-update-status.schema.json',
  'release-bundle.schema.json',
  'release-lock.schema.json',
  'runtime-matrix.schema.json',
  'runtime-capability-evidence.schema.json',
  'runtime-framework-installers.schema.json',
  'runtime-performance-evidence.schema.json',
  'runtime-performance-policy.schema.json',
  'runtime-promotion-plan.schema.json',
  'runtime-promotion-receipt.schema.json',
  'runtime-profile.schema.json',
  'url-v3.schema.json',
])

const failures = []
const schemas = new Map()

for (const fileName of fs.readdirSync(schemaDirectory).sort()) {
  if (!fileName.endsWith('.schema.json')) continue

  const filePath = path.join(schemaDirectory, fileName)
  let schema
  try {
    schema = JSON.parse(fs.readFileSync(filePath, 'utf8'))
  } catch (error) {
    failures.push(`${fileName}: invalid JSON (${error.message})`)
    continue
  }

  schemas.set(fileName, schema)
}

for (const expected of expectedSchemas) {
  if (!schemas.has(expected)) failures.push(`${expected}: required schema is missing`)
}

const ids = new Set()

for (const [fileName, schema] of schemas) {
  if (schema.$schema !== 'https://json-schema.org/draft/2020-12/schema') {
    failures.push(`${fileName}: $schema must select JSON Schema Draft 2020-12`)
  }
  if (typeof schema.$id !== 'string' || !schema.$id.startsWith('https://')) {
    failures.push(`${fileName}: $id must be an HTTPS URI`)
  } else if (ids.has(schema.$id)) {
    failures.push(`${fileName}: duplicate $id ${schema.$id}`)
  } else {
    ids.add(schema.$id)
  }
  if (schema.type !== 'object' || schema.additionalProperties !== false) {
    failures.push(`${fileName}: root must be an object with additionalProperties=false`)
  }

  inspectSchema(schema, schema, fileName, '#')
}

const documents = [
  ['profiles/base-images.json', 'base-images.schema.json'],
  ['profiles/catalog/catalog.json', 'catalog.schema.json'],
  [releaseLockPath, 'release-lock.schema.json'],
  ['deploy/images.json', 'deployment-images.schema.json'],
  ['profiles/license-policy.json', 'license-policy.schema.json'],
  ['profiles/profile-update-status.example.json', 'profile-update-status.schema.json'],
  ['profiles/runtime-framework-installers.json', 'runtime-framework-installers.schema.json'],
  ['profiles/runtime-matrix.json', 'runtime-matrix.schema.json'],
  ['profiles/runtime-performance-policies/runtime-image-linux-x64-v1.json', 'runtime-performance-policy.schema.json'],
  ['profiles/provenance/const-generics-runtime.json', 'maintained-provenance.schema.json'],
  ['profiles/provenance/const-generics-roslyn.json', 'maintained-provenance.schema.json'],
  ['profiles/provenance/const-generics-ilspy.json', 'maintained-provenance.schema.json'],
  ['profiles/provenance/cppcli.json', 'maintained-provenance.schema.json'],
  ['profiles/provenance/jsil.json', 'maintained-provenance.schema.json'],
  ['samples/Languages/SharpLabNext.SampleLanguage.Worker/language-worker.json', 'language-worker-capability.schema.json'],
  ['samples/Runtimes/dotnet-runtime-template/runtime-profile.json', 'runtime-profile.schema.json'],
]

const generatedRuntimeDirectory = path.join(repositoryRoot, 'profiles', 'runtimes')
const runtimeProfilePaths = collectJsonFiles(generatedRuntimeDirectory, 'profiles/runtimes')
for (const relativePath of runtimeProfilePaths) {
  documents.push([relativePath, 'runtime-profile.schema.json'])
}

const promotionReceiptDirectory = path.join(repositoryRoot, 'profiles', 'runtime-promotion-receipts')
if (fs.existsSync(promotionReceiptDirectory)) {
  for (const relativePath of collectJsonFiles(
    promotionReceiptDirectory,
    'profiles/runtime-promotion-receipts',
  )) {
    documents.push([relativePath, 'runtime-promotion-receipt.schema.json'])
  }
}

const promotionPlanDirectory = path.join(repositoryRoot, 'profiles', 'runtime-promotion-plans')
if (fs.existsSync(promotionPlanDirectory)) {
  for (const relativePath of collectJsonFiles(
    promotionPlanDirectory,
    'profiles/runtime-promotion-plans',
  )) {
    documents.push([
      relativePath,
      relativePath.endsWith('.profile.json')
        ? 'runtime-profile.schema.json'
        : 'runtime-promotion-plan.schema.json',
    ])
  }
}

const promotionEvidenceDirectory = path.join(repositoryRoot, 'profiles', 'runtime-promotion-evidence')
if (fs.existsSync(promotionEvidenceDirectory)) {
  for (const relativePath of collectJsonFiles(
    promotionEvidenceDirectory,
    'profiles/runtime-promotion-evidence',
  )) {
    documents.push([
      relativePath,
      relativePath.endsWith('/performance.json')
        ? 'runtime-performance-evidence.schema.json'
        : 'runtime-capability-evidence.schema.json',
    ])
  }
}

for (const [relativePath, schemaName] of documents) {
  const documentPath = path.isAbsolute(relativePath) ? relativePath : path.join(repositoryRoot, relativePath)
  const documentLabel = path.isAbsolute(relativePath)
    ? path.relative(repositoryRoot, relativePath).replaceAll('\\', '/')
    : relativePath
  let document
  try {
    document = JSON.parse(fs.readFileSync(documentPath, 'utf8'))
  } catch (error) {
    failures.push(`${documentLabel}: invalid JSON (${error.message})`)
    continue
  }

  const schema = schemas.get(schemaName)
  if (schema !== undefined) {
    for (const error of validateInstance(document, schema, schema, '#')) {
      failures.push(`${documentLabel}${error}`)
    }
  }
}

const catalogForChannelsPath = path.join(repositoryRoot, 'profiles', 'catalog', 'catalog.json')
try {
  const catalogForChannels = JSON.parse(fs.readFileSync(catalogForChannelsPath, 'utf8'))
  failures.push(...validateRuntimeProfileChannels(
    runtimeProfilePaths,
    catalogForChannels,
    relativePath => JSON.parse(fs.readFileSync(path.join(repositoryRoot, relativePath), 'utf8')),
  ))
} catch (error) {
  failures.push(`profiles/catalog/catalog.json: cannot validate runtime profile channels (${error.message})`)
}

try {
  const runtimeMatrix = JSON.parse(fs.readFileSync(
    path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
    'utf8',
  ))
  failures.push(...validateRuntimePromotionReceipts(runtimeMatrix, repositoryRoot))
} catch (error) {
  failures.push(`profiles/runtime-matrix.json: cannot validate promotion receipts (${error.message})`)
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`schema error: ${failure}`)
  process.exit(1)
}

console.log(`Validated ${schemas.size} strict JSON schemas and ${documents.length} JSON documents.`)

function collectJsonFiles(directory, relativeDirectory) {
  const entries = fs.readdirSync(directory, { withFileTypes: true })
    .sort((left, right) => left.name.localeCompare(right.name))
  const files = []
  for (const entry of entries) {
    const absolutePath = path.join(directory, entry.name)
    const relativePath = `${relativeDirectory}/${entry.name}`
    if (entry.isDirectory()) {
      files.push(...collectJsonFiles(absolutePath, relativePath))
    } else if (entry.isFile() && entry.name.endsWith('.json')) {
      files.push(relativePath)
    }
  }
  return files
}

function inspectSchema(node, root, fileName, pointer) {
  if (Array.isArray(node)) {
    node.forEach((item, index) => inspectSchema(item, root, fileName, `${pointer}/${index}`))
    return
  }
  if (node === null || typeof node !== 'object') return

  if (typeof node.$ref === 'string' && node.$ref.startsWith('#/')) {
    const segments = node.$ref
      .slice(2)
      .split('/')
      .map(segment => segment.replaceAll('~1', '/').replaceAll('~0', '~'))
    let target = root
    for (const segment of segments) target = target?.[segment]
    if (target === undefined) failures.push(`${fileName}${pointer}: unresolved $ref ${node.$ref}`)
  }

  if (node.properties !== undefined) {
    if (node.type !== 'object') failures.push(`${fileName}${pointer}: properties requires type=object`)
    if (node.additionalProperties !== false && node.unevaluatedProperties !== false) {
      failures.push(`${fileName}${pointer}: object schemas with properties must reject unknown properties`)
    }
  }

  if (Array.isArray(node.required)) {
    const properties = node.properties ?? {}
    for (const required of node.required) {
      if (!(required in properties)) {
        failures.push(`${fileName}${pointer}: required property ${required} is not declared`)
      }
    }
  }

  if (typeof node.pattern === 'string') {
    try {
      new RegExp(node.pattern)
    } catch (error) {
      failures.push(`${fileName}${pointer}: invalid pattern (${error.message})`)
    }
  }

  if (typeof node.format === 'string' && !isSupportedJsonSchemaFormat(node.format)) {
    failures.push(`${fileName}${pointer}: unsupported format '${node.format}'`)
  }

  if (Array.isArray(node.enum) && new Set(node.enum.map(value => JSON.stringify(value))).size !== node.enum.length) {
    failures.push(`${fileName}${pointer}: enum values must be unique`)
  }

  for (const [key, value] of Object.entries(node)) {
    inspectSchema(value, root, fileName, `${pointer}/${escapePointer(key)}`)
  }
}

function escapePointer(value) {
  return value.replaceAll('~', '~0').replaceAll('/', '~1')
}

function validateInstance(value, schema, root, pointer) {
  if (schema.$ref !== undefined) {
    return validateInstance(value, resolveRef(schema.$ref, root), root, pointer)
  }

  const errors = []
  if (Array.isArray(schema.allOf)) {
    for (const candidate of schema.allOf) errors.push(...validateInstance(value, candidate, root, pointer))
  }
  if (Array.isArray(schema.anyOf)) {
    const candidates = schema.anyOf.map(candidate => validateInstance(value, candidate, root, pointer))
    if (!candidates.some(candidate => candidate.length === 0)) {
      errors.push(`${pointer}: value does not match any allowed schema`)
      return errors
    }
  }

  if (schema.const !== undefined && !sameJson(value, schema.const)) {
    errors.push(`${pointer}: expected constant ${JSON.stringify(schema.const)}`)
  }
  if (Array.isArray(schema.enum) && !schema.enum.some(candidate => sameJson(value, candidate))) {
    errors.push(`${pointer}: value is not in the allowed enum`)
  }

  const allowedTypes = Array.isArray(schema.type) ? schema.type : schema.type === undefined ? [] : [schema.type]
  if (allowedTypes.length > 0 && !allowedTypes.some(type => hasJsonType(value, type))) {
    errors.push(`${pointer}: expected type ${allowedTypes.join('|')}`)
    return errors
  }

  if (typeof value === 'string') {
    if (schema.minLength !== undefined && value.length < schema.minLength) errors.push(`${pointer}: string is shorter than minLength`)
    if (schema.maxLength !== undefined && value.length > schema.maxLength) errors.push(`${pointer}: string is longer than maxLength`)
    if (schema.pattern !== undefined && !new RegExp(schema.pattern).test(value)) errors.push(`${pointer}: string does not match pattern`)
    if (schema.format !== undefined && !isValidJsonSchemaFormat(value, schema.format)) {
      errors.push(`${pointer}: string does not match format '${schema.format}'`)
    }
  }

  if (typeof value === 'number') {
    if (schema.minimum !== undefined && value < schema.minimum) errors.push(`${pointer}: number is below minimum`)
    if (schema.maximum !== undefined && value > schema.maximum) errors.push(`${pointer}: number is above maximum`)
  }

  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) errors.push(`${pointer}: array has too few items`)
    if (schema.maxItems !== undefined && value.length > schema.maxItems) errors.push(`${pointer}: array has too many items`)
    if (schema.uniqueItems === true && new Set(value.map(item => JSON.stringify(item))).size !== value.length) {
      errors.push(`${pointer}: array items must be unique`)
    }
    if (schema.items !== undefined && schema.items !== false) {
      value.forEach((item, index) => errors.push(...validateInstance(item, schema.items, root, `${pointer}/${index}`)))
    }
  }

  if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
    const properties = schema.properties ?? {}
    for (const required of schema.required ?? []) {
      if (!(required in value)) errors.push(`${pointer}: missing required property ${required}`)
    }
    if (schema.minProperties !== undefined && Object.keys(value).length < schema.minProperties) {
      errors.push(`${pointer}: object has too few properties`)
    }
    for (const [key, item] of Object.entries(value)) {
      const itemPointer = `${pointer}/${escapePointer(key)}`
      if (key in properties) {
        errors.push(...validateInstance(item, properties[key], root, itemPointer))
      } else if (schema.additionalProperties === false) {
        errors.push(`${itemPointer}: unknown property`)
      } else if (schema.additionalProperties !== undefined && typeof schema.additionalProperties === 'object') {
        errors.push(...validateInstance(item, schema.additionalProperties, root, itemPointer))
      }
      if (schema.propertyNames !== undefined) {
        errors.push(...validateInstance(key, schema.propertyNames, root, `${pointer}/<property-name>`))
      }
    }
  }

  return errors
}

function resolveRef(reference, root) {
  if (!reference.startsWith('#/')) throw new Error(`Only local schema references are supported: ${reference}`)
  return reference
    .slice(2)
    .split('/')
    .map(segment => segment.replaceAll('~1', '/').replaceAll('~0', '~'))
    .reduce((node, segment) => node[segment], root)
}

function sameJson(left, right) {
  return JSON.stringify(left) === JSON.stringify(right)
}

function hasJsonType(value, type) {
  switch (type) {
    case 'null': return value === null
    case 'array': return Array.isArray(value)
    case 'object': return value !== null && typeof value === 'object' && !Array.isArray(value)
    case 'integer': return Number.isInteger(value)
    case 'number': return typeof value === 'number' && Number.isFinite(value)
    case 'string': return typeof value === 'string'
    case 'boolean': return typeof value === 'boolean'
    default: return false
  }
}
