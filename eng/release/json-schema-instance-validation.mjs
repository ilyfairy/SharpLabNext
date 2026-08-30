import { isValidJsonSchemaFormat } from './json-schema-formats.mjs'

/**
 * Validates a JSON value against the deliberately small Draft 2020-12 subset
 * used by this repository.  It is pure: callers own schema/document loading
 * and decide how validation failures become diagnostics.
 */
export function validateJsonSchemaInstance(value, schema, options = {}) {
  const pointer = options.pointer ?? '#'
  const label = options.label ?? ''
  if (typeof pointer !== 'string' || pointer.length === 0) {
    throw new Error('JSON Schema validation pointer must be a non-empty string')
  }
  if (typeof label !== 'string') throw new Error('JSON Schema validation label must be a string')

  const rootSchema = options.rootSchema ?? schema
  return validateInstance(value, schema, rootSchema, pointer).map(error => `${label}${error}`)
}

function validateInstance(value, schema, root, pointer) {
  if (schema === true) return []
  if (schema === false) return [`${pointer}: value is disallowed by schema`]
  if (schema === null || typeof schema !== 'object' || Array.isArray(schema)) {
    throw new Error(`${pointer}: schema must be an object or boolean`)
  }
  if (schema.$ref !== undefined) {
    return validateInstance(value, resolveRef(schema.$ref, root), root, pointer)
  }

  const errors = []
  if (Array.isArray(schema.allOf)) {
    for (const candidate of schema.allOf) {
      errors.push(...validateInstance(value, candidate, root, pointer))
    }
  }
  if (Array.isArray(schema.anyOf)) {
    const candidates = schema.anyOf.map(candidate => validateInstance(value, candidate, root, pointer))
    if (!candidates.some(candidate => candidate.length === 0)) {
      errors.push(`${pointer}: value does not match any allowed schema`)
    }
  }
  if (Array.isArray(schema.oneOf)) {
    const matches = schema.oneOf.filter(candidate =>
      validateInstance(value, candidate, root, pointer).length === 0).length
    if (matches !== 1) errors.push(`${pointer}: value must match exactly one allowed schema`)
  }
  if (schema.not !== undefined && validateInstance(value, schema.not, root, pointer).length === 0) {
    errors.push(`${pointer}: value matches a disallowed schema`)
  }

  if (schema.const !== undefined && !sameJson(value, schema.const)) {
    errors.push(`${pointer}: expected constant ${JSON.stringify(schema.const)}`)
  }
  if (Array.isArray(schema.enum) && !schema.enum.some(candidate => sameJson(value, candidate))) {
    errors.push(`${pointer}: value is not in the allowed enum`)
  }

  const allowedTypes = Array.isArray(schema.type)
    ? schema.type
    : schema.type === undefined ? [] : [schema.type]
  if (allowedTypes.length > 0 && !allowedTypes.some(type => hasJsonType(value, type))) {
    errors.push(`${pointer}: expected type ${allowedTypes.join('|')}`)
    return errors
  }

  if (typeof value === 'string') {
    const length = [...value].length
    if (schema.minLength !== undefined && length < schema.minLength) {
      errors.push(`${pointer}: string is shorter than minLength`)
    }
    if (schema.maxLength !== undefined && length > schema.maxLength) {
      errors.push(`${pointer}: string is longer than maxLength`)
    }
    if (schema.pattern !== undefined && !new RegExp(schema.pattern).test(value)) {
      errors.push(`${pointer}: string does not match pattern`)
    }
    if (schema.format !== undefined && !isValidJsonSchemaFormat(value, schema.format)) {
      errors.push(`${pointer}: string does not match format '${schema.format}'`)
    }
  }

  if (typeof value === 'number' && Number.isFinite(value)) {
    if (schema.minimum !== undefined && value < schema.minimum) {
      errors.push(`${pointer}: number is below minimum`)
    }
    if (schema.maximum !== undefined && value > schema.maximum) {
      errors.push(`${pointer}: number is above maximum`)
    }
    if (schema.exclusiveMinimum !== undefined && value <= schema.exclusiveMinimum) {
      errors.push(`${pointer}: number is not above exclusiveMinimum`)
    }
    if (schema.exclusiveMaximum !== undefined && value >= schema.exclusiveMaximum) {
      errors.push(`${pointer}: number is not below exclusiveMaximum`)
    }
    if (schema.multipleOf !== undefined && !isMultipleOf(value, schema.multipleOf)) {
      errors.push(`${pointer}: number is not a multiple of the required value`)
    }
  }

  if (Array.isArray(value)) {
    if (schema.minItems !== undefined && value.length < schema.minItems) {
      errors.push(`${pointer}: array has too few items`)
    }
    if (schema.maxItems !== undefined && value.length > schema.maxItems) {
      errors.push(`${pointer}: array has too many items`)
    }
    if (schema.uniqueItems === true && !hasUniqueJsonValues(value)) {
      errors.push(`${pointer}: array items must be unique`)
    }
    const prefixItems = Array.isArray(schema.prefixItems) ? schema.prefixItems : []
    prefixItems.forEach((itemSchema, index) => {
      if (index < value.length) {
        errors.push(...validateInstance(value[index], itemSchema, root, `${pointer}/${index}`))
      }
    })
    const remainingItems = value.slice(prefixItems.length)
    if (schema.items !== undefined && schema.items !== false) {
      remainingItems.forEach((item, relativeIndex) => {
        const index = prefixItems.length + relativeIndex
        errors.push(...validateInstance(item, schema.items, root, `${pointer}/${index}`))
      })
    } else if (schema.items === false && remainingItems.length > 0) {
      errors.push(`${pointer}: array has items beyond prefixItems`)
    }
    if (schema.contains !== undefined) {
      const matches = value.filter(item => validateInstance(item, schema.contains, root, pointer).length === 0)
        .length
      const minimum = schema.minContains ?? 1
      const maximum = schema.maxContains
      if (matches < minimum) {
        errors.push(`${pointer}: array has fewer matching items than minContains`)
      }
      if (maximum !== undefined && matches > maximum) {
        errors.push(`${pointer}: array has more matching items than maxContains`)
      }
    }
  }

  if (isObject(value)) {
    const properties = isObject(schema.properties) ? schema.properties : {}
    const propertyNames = Object.keys(value)
    for (const required of schema.required ?? []) {
      if (!Object.hasOwn(value, required)) errors.push(`${pointer}: missing required property ${required}`)
    }
    if (schema.minProperties !== undefined && propertyNames.length < schema.minProperties) {
      errors.push(`${pointer}: object has too few properties`)
    }
    if (schema.maxProperties !== undefined && propertyNames.length > schema.maxProperties) {
      errors.push(`${pointer}: object has too many properties`)
    }
    if (isObject(schema.dependentRequired)) {
      for (const [property, dependencies] of Object.entries(schema.dependentRequired)) {
        if (!Object.hasOwn(value, property) || !Array.isArray(dependencies)) continue
        for (const dependency of dependencies) {
          if (!Object.hasOwn(value, dependency)) {
            errors.push(`${pointer}: property ${property} requires property ${dependency}`)
          }
        }
      }
    }
    if (isObject(schema.dependentSchemas)) {
      for (const [property, dependencySchema] of Object.entries(schema.dependentSchemas)) {
        if (Object.hasOwn(value, property)) {
          errors.push(...validateInstance(value, dependencySchema, root, pointer))
        }
      }
    }
    for (const [key, item] of Object.entries(value)) {
      const itemPointer = `${pointer}/${escapePointer(key)}`
      if (Object.hasOwn(properties, key)) {
        errors.push(...validateInstance(item, properties[key], root, itemPointer))
      } else if (schema.additionalProperties === false) {
        errors.push(`${itemPointer}: unknown property`)
      } else if (schema.additionalProperties !== undefined && schema.additionalProperties !== true) {
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
  if (typeof reference !== 'string' || !reference.startsWith('#/')) {
    throw new Error(`Only local schema references are supported: ${reference}`)
  }
  let target = root
  for (const segment of reference.slice(2).split('/')) {
    if (target === null || typeof target !== 'object') {
      throw new Error(`Unresolved local schema reference: ${reference}`)
    }
    target = target[segment.replaceAll('~1', '/').replaceAll('~0', '~')]
  }
  if (target === undefined) throw new Error(`Unresolved local schema reference: ${reference}`)
  return target
}

function sameJson(left, right) { return canonicalJson(left) === canonicalJson(right); }

function canonicalJson(value) {
  if (value === null || typeof value !== 'object') return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`
  return `{${Object.keys(value).sort().map(key =>
    `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`
}

function hasJsonType(value, type) {
  switch (type) {
    case 'null': return value === null
    case 'array': return Array.isArray(value)
    case 'object': return isObject(value)
    case 'integer': return Number.isInteger(value)
    case 'number': return typeof value === 'number' && Number.isFinite(value)
    case 'string': return typeof value === 'string'
    case 'boolean': return typeof value === 'boolean'
    default: return false
  }
}

function hasUniqueJsonValues(values) {
  const canonicalValues = new Set()
  for (const value of values) {
    const encoded = canonicalJson(value)
    if (canonicalValues.has(encoded)) return false
    canonicalValues.add(encoded)
  }
  return true
}

function isMultipleOf(value, divisor) {
  if (typeof divisor !== 'number' || !Number.isFinite(divisor) || divisor <= 0) return false
  const quotient = value / divisor
  return Math.abs(quotient - Math.round(quotient)) <= Number.EPSILON * Math.max(1, Math.abs(quotient))
}

function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

function escapePointer(value) { return value.replaceAll('~', '~0').replaceAll('/', '~1'); }
