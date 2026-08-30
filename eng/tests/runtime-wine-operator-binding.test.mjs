import assert from 'node:assert/strict';
import test from 'node:test';

import {
  maximumWineOperatorSizeBytes,
  validateWineOperatorBinding,
} from '../release/runtime-wine-operator-binding.mjs';

const sourceRevision = 'a'.repeat(40);
const digest = character => `sha256:${character.repeat(64)}`;
const reference = character => `registry.example/runtime@sha256:${character.repeat(64)}`;

function directBinding() {
  return {
    receiptPath: `profiles/runtime-operator-receipts/wine-coreclr-${sourceRevision}.json`,
    receiptSha256: digest('b'),
    signaturePath: `profiles/runtime-operator-receipts/wine-coreclr-${sourceRevision}.json.sig`,
    signatureSha256: digest('c'),
    keyId: digest('d'),
    reference: reference('e'),
    imageId: digest('f'),
    sizeBytes: maximumWineOperatorSizeBytes,
    sourceRevision,
    sourceTree: '1'.repeat(40),
    lineageKind: 'direct',
  };
}

test('Wine binding accepts exactly the 16 GiB operator size limit and rejects larger sizes', () => {
  assert.doesNotThrow(() => validateWineOperatorBinding(directBinding(), 'coreclr-wine', sourceRevision))
  const oversized = directBinding();
  oversized.sizeBytes++;
  assert.throws(() => validateWineOperatorBinding(oversized, 'coreclr-wine', sourceRevision), /invalid wineOperator binding/);
});

test('Framework Wine binding rejects an intermediary larger than the 16 GiB limit', () => {
  const binding = {
    ...directBinding(),
    lineageKind: 'framework-row',
    intermediaryReference: reference('2'),
    intermediaryImageId: digest('3'),
    intermediarySizeBytes: maximumWineOperatorSizeBytes + 1,
  };
  assert.throws(() => validateWineOperatorBinding(binding, 'netfx-clr-wine', sourceRevision), /invalid Wine operator intermediary lineage/);
});
