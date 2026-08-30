import assert from 'node:assert/strict';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { resolveWineCoreClrUserspaceLock, wineCoreClrUserspaceEnvironment } from '../runtime-wine-userspace-lock.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

test('Wine userspace is derived from the bounded committed lock and rejects caller spoofing', () => {
  const resolved = resolveWineCoreClrUserspaceLock(root);
  assert.match(resolved.digest, /^sha256:[0-9a-f]{64}$/);
  assert.equal(wineCoreClrUserspaceEnvironment({}, root).WINE_CORECLR_USERSPACE_VERSION, resolved.version);
  assert.throws(() => wineCoreClrUserspaceEnvironment({
    WINE_CORECLR_USERSPACE_DIGEST: `sha256:${'0'.repeat(64)}`,
  }, root), /lock-derived/);
  assert.throws(() => resolveWineCoreClrUserspaceLock('profiles'), /absolute directory/);
});
