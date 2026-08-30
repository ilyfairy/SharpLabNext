import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const componentId = 'wine-coreclr-userspace';
const inputNames = Object.freeze({
  version: 'WINE_CORECLR_USERSPACE_VERSION',
  digest: 'WINE_CORECLR_USERSPACE_DIGEST',
  sourceUri: 'WINE_CORECLR_USERSPACE_SOURCE_URI',
})
const sha256 = /^sha256:[0-9a-f]{64}$/;

function readJson(filename) {
  let bytes;
  try { bytes = fs.readFileSync(filename) } catch (error) {
    throw new Error(`could not read ${filename}: ${error.message}`);
  }
  try { return { bytes, value: JSON.parse(bytes.toString('utf8')) }; } catch (error) {
    throw new Error(`could not parse ${filename}: ${error.message}`);
  }
}

/**
 * Resolve the Wine operator userspace from the two committed lock inputs.
 * The source root is deliberately a directory, never a caller supplied file.
 */
export function resolveWineCoreClrUserspaceLock(sourceRoot) {
  if (typeof sourceRoot !== 'string' || !path.isAbsolute(sourceRoot)) {
    throw new Error('Wine userspace lock sourceRoot must be an absolute directory');
  }
  const root = path.resolve(sourceRoot);
  const lockPath = path.join(root, 'profiles', 'lock.json');
  const manifestPath = path.join(root, 'profiles', 'runtime-wine-packages.json');
  const lock = readJson(lockPath).value;
  const manifest = readJson(manifestPath);
  const component = lock?.components?.[componentId];
  const manifestComponent = manifest.value?.component;
  const digest = `sha256:${crypto.createHash('sha256').update(manifest.bytes).digest('hex')}`;
  const failures = [];
  if (component?.kind !== 'runtime-dependency') failures.push('lock component kind must be runtime-dependency');
  if (component?.digest !== digest || !sha256.test(component?.digest ?? '')) failures.push('lock component digest must equal runtime-wine-packages.json SHA-256')
  if (typeof component?.resolvedVersion !== 'string' || component.resolvedVersion.length === 0) failures.push('lock component resolvedVersion is missing')
  if (component?.resolvedVersion !== manifestComponent?.resolvedVersion) failures.push('lock component resolvedVersion must equal manifest component resolvedVersion')
  if (component?.sourceUri !== manifestComponent?.sourceUri) failures.push('lock component sourceUri must equal manifest component sourceUri')
  if (manifestComponent?.id !== componentId) failures.push(`manifest component id must be ${componentId}`)
  if (manifestComponent?.kind !== 'runtime-dependency') failures.push('manifest component kind must be runtime-dependency')
  if (failures.length) throw new Error(`invalid Wine userspace lock: ${failures.join('; ')}`);
  return Object.freeze({
    version: component.resolvedVersion,
    digest: component.digest,
    sourceUri: component.sourceUri,
    files: Object.freeze(['profiles/lock.json', 'profiles/runtime-wine-packages.json']),
  });
}

/** Reject contradictory caller values and return an immutable canonical env overlay. */
export function wineCoreClrUserspaceEnvironment(values, sourceRoot) {
  const resolved = resolveWineCoreClrUserspaceLock(sourceRoot);
  const result = {};
  for (const [field, name] of Object.entries(inputNames)) {
    const supplied = values?.[name];
    if (supplied !== undefined && supplied !== '' && supplied !== resolved[field]) {
      throw new Error(`${name} is lock-derived and must equal '${resolved[field]}'`);
    }
    result[name] = resolved[field];
  }
  return Object.freeze(result);
}

export const wineCoreClrUserspaceInputNames = inputNames;
