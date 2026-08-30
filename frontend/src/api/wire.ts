import type { PascalCaseWire } from './wireTypes';

/**
 * Converts the browser's internal model keys to the PascalCase JSON names
 * used by every SharpLabNext-owned HTTP/operation WebSocket boundary. This
 * deliberately lives at the transport boundary: editor/LSP internals remain
 * independent of the public wire spelling.
 *
 * LSP messages do not use this helper.  They are a separate, standards-based
 * protocol and must retain their JSON-RPC field names. The decoder targets
 * the current PascalCase response shape; it is not a legacy compatibility
 * layer.
 */

type JsonRecord = Record<string, unknown>;

function isRecord(value: unknown): value is JsonRecord {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

const dynamicPropertyMapNames = new Set([
  // AstNode.Properties is an IReadOnlyDictionary whose keys are syntax
  // property names, not contract member names.
  'properties',
  // Worker descriptors and readiness snapshots expose keyed maps whose keys
  // are stable IDs or provider-defined names. They are data, not DTO member
  // names, so changing their casing would make lookups fail in the browser.
  'dependencies',
  'components',
  // RFC 7807/problem extensions are application-defined keys.
  'extensions',
  // These contract members carry user/provider-defined keys (file names,
  // metadata names, and reference-set IDs), rather than nested DTO members.
  'metadata',
  'filecontentsbase64',
  'expectedreferencesetdigests',
]);

// `Identity` is unfortunately used for two different contract shapes:
// WorkerDescriptor.Identity is a provider-defined dictionary, while operation
// results use a fixed BuildIdentity/RuntimeIdentity/JitIdentity DTO. Keep the
// former's keys untouched without leaking PascalCase wire names from the
// latter into the camelCase application model.
const knownIdentityMemberNames = new Set([
  'releaseid',
  'languageid',
  'toolchainid',
  'compilerversion',
  'compilercommit',
  'referencesetid',
  'workerimageid',
  'processorid',
  'processorversion',
  'runtimeversion',
  'runtimecommit',
  'runtimeimageid',
  'rid',
  'architecture',
  'jitversion',
  'jitcommit',
  'cpufeatureprofile',
  'tieringpolicy',
  'pgopolicy',
  'jitprovider',
  'inspectionmethod',
]);

function isFixedIdentityDto(value: JsonRecord): boolean {
  const keys = Object.keys(value)
  return keys.length > 0 && keys.every((key) => knownIdentityMemberNames.has(key.toLowerCase()));
}

function isDynamicPropertyMap(parentKey: string | undefined, value: JsonRecord): boolean {
  if (parentKey === undefined) return false;
  const normalized = parentKey.toLowerCase();
  if (normalized === 'identity') return !isFixedIdentityDto(value);
  return dynamicPropertyMapNames.has(normalized);
}

function toPascalCase(name: string): string {
  if (name.length === 0) return name;
  const first = name[0];
  return first && first >= 'a' && first <= 'z' ? first.toUpperCase() + name.slice(1) : name;
}

function toCamelCase(name: string): string {
  if (name.length === 0) return name;
  const first = name[0];
  return first && first >= 'A' && first <= 'Z' ? first.toLowerCase() + name.slice(1) : name;
}

function isPascalCaseMemberName(name: string): boolean {
  if (name.length === 0) return true;
  const first = name[0];
  // ContractJson's policy changes only an ASCII lower-case initial. Keep the
  // same boundary rule here so acronyms, numeric names, and explicitly named
  // special members are not rejected by a stricter (and divergent) heuristic.
  return !(first && first >= 'a' && first <= 'z');
}

function invalidWireMember(path: string, key: string): Error {
  return new Error(`Invalid SharpLabNext wire member '${key}' at ${path}; expected PascalCase.`);
}

function encode(value: unknown, parentKey?: string): unknown {
  if (Array.isArray(value)) return value.map((item) => encode(item, parentKey));
  if (!isRecord(value)) return value;

  const preserveKeys = isDynamicPropertyMap(parentKey, value);
  return Object.fromEntries(Object.entries(value).map(([key, child]) => [preserveKeys ? key : toPascalCase(key), encode(child, preserveKeys ? undefined : key)]));
}

function decode(value: unknown, parentKey?: string, path = '$'): unknown {
  if (Array.isArray(value)) {
    return value.map((item, index) => decode(item, parentKey, `${path}[${index}]`));
  }
  if (!isRecord(value)) return value;

  const preserveKeys = isDynamicPropertyMap(parentKey, value);
  return Object.fromEntries(
    Object.entries(value).map(([key, child]) => {
      if (!preserveKeys && !isPascalCaseMemberName(key)) {
        throw invalidWireMember(`${path}.${key}`, key);
      }
      const modelKey = preserveKeys ? key : toCamelCase(key);
      return [modelKey, decode(child, preserveKeys ? undefined : modelKey, `${path}.${key}`)];
    }),
  )
}

/** Encodes an application request or envelope using public wire names. */
export function encodeWire<T>(value: T): PascalCaseWire<T> {
  return encode(value) as PascalCaseWire<T>;
}

/** Decodes an application response or envelope into the internal model shape. */
export function decodeWire<T>(value: PascalCaseWire<T>): T
export function decodeWire<T>(value: unknown): T
export function decodeWire<T>(value: unknown): T {
  return decode(value) as T;
}

/** JSON.stringify with the Gateway contract's PascalCase member names. */
export function stringifyWire<T>(value: T): string {
  return JSON.stringify(encodeWire(value));
}
