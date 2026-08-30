import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

import {
  verifyWineCoreClrOperatorReceipt,
  wineCoreClrOperatorCommittedFiles,
  maximumWineOperatorSizeBytes,
} from './wine-coreclr-operator-receipt.mjs';

export const runtimeOperatorReceiptDirectory = 'profiles/runtime-operator-receipts';
export { maximumWineOperatorSizeBytes };
const digestPattern = /^sha256:[0-9a-f]{64}$/;
const imageReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/;
const gitObjectPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/;

export function runtimeOperatorReceiptPaths(sourceRevision) {
  if (!gitObjectPattern.test(sourceRevision ?? '')) throw new Error('Wine operator receipt source revision is invalid');
  const receiptPath = `${runtimeOperatorReceiptDirectory}/wine-coreclr-${sourceRevision}.json`;
  return Object.freeze({ receiptPath, signaturePath: `${receiptPath}.sig` });
}

export function sha256(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`;
}

export function isWinePromotionFamily(family) {
  return family === 'coreclr-wine' || family === 'netfx-clr-wine';
}

export function isRegularOwnedFile(root, relativePath, maximumBytes = 1024 * 1024) {
  const absolutePath = path.resolve(root, ...relativePath.split('/'));
  const allowedRoot = path.resolve(root, ...runtimeOperatorReceiptDirectory.split('/'));
  const relative = path.relative(allowedRoot, absolutePath);
  if (relative === '' || relative === '..' || relative.startsWith(`..${path.sep}`) ||
      path.isAbsolute(relative)) throw new Error(`Wine operator path '${relativePath}' escapes its owned directory`);
  const rootStat = fs.lstatSync(allowedRoot);
  const stat = fs.lstatSync(absolutePath);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink() || !stat.isFile() || stat.isSymbolicLink() ||
      stat.size < 1 || stat.size > maximumBytes) throw new Error(`Wine operator path '${relativePath}' is not a bounded regular non-link file`);
  const realRoot = fs.realpathSync.native(allowedRoot);
  const realPath = fs.realpathSync.native(absolutePath);
  if (!realPath.startsWith(`${realRoot}${path.sep}`)) throw new Error(`Wine operator path '${relativePath}' resolves outside its owned directory`);
  return fs.readFileSync(absolutePath);
}

function gitShow(root, args, label, spawn = spawnSync) {
  const result = spawn('git', ['-C', root, ...args], {
    encoding: 'buffer', timeout: 10_000, windowsHide: true, shell: false,
  });
  if (result.error !== undefined || result.status !== 0) throw new Error(`could not ${label} from committed source`);
  return Buffer.from(result.stdout);
}

export function verifyWineOperatorSourceAtRevision(root, receipt, sourceRevision, options = {}) {
  if (!gitObjectPattern.test(sourceRevision ?? '') || receipt.source.revision !== sourceRevision) {
    throw new Error('Wine operator receipt source revision does not equal the promotion source revision');
  }
  const show = options.gitShow ?? ((args, label) => gitShow(root, args, label, options.spawn));
  const tree = show(['rev-parse', `${sourceRevision}^{tree}`], 'resolve source tree').toString('utf8').trim();
  if (tree !== receipt.source.tree) throw new Error('Wine operator receipt source tree does not match the committed source tree');
  for (const relativePath of wineCoreClrOperatorCommittedFiles) {
    const bytes = show(['show', `${sourceRevision}:${relativePath}`], `read ${relativePath}`);
    if (sha256(bytes) !== receipt.source.files[relativePath]) {
      throw new Error(`Wine operator receipt source digest does not match committed ${relativePath}`);
    }
  }
}

export function loadOwnedWineOperatorBinding(root, sourceRevision, options = {}) {
  const paths = runtimeOperatorReceiptPaths(sourceRevision);
  const receiptBytes = isRegularOwnedFile(root, paths.receiptPath);
  const signatureBytes = isRegularOwnedFile(root, paths.signaturePath, 4096);
  const receipt = verifyWineCoreClrOperatorReceipt(receiptBytes, signatureBytes,
    options.publicKey === undefined ? {} : { publicKey: options.publicKey });
  verifyWineOperatorSourceAtRevision(root, receipt, sourceRevision, options);
  return Object.freeze({ receipt, receiptBytes, signatureBytes, paths });
}

export function wineOperatorBinding(receipt, sourceRevision, receiptBytes, signatureBytes, lineage) {
  const paths = runtimeOperatorReceiptPaths(sourceRevision);
  if (!imageReferencePattern.test(receipt.operator.reference) || !digestPattern.test(receipt.operator.imageId) ||
      !Number.isSafeInteger(receipt.operator.sizeBytes) || receipt.operator.sizeBytes <= 0 ||
      receipt.operator.sizeBytes > maximumWineOperatorSizeBytes) {
    throw new Error('Wine operator receipt has an invalid immutable operator identity');
  }
  const base = {
    receiptPath: paths.receiptPath,
    receiptSha256: sha256(receiptBytes),
    signaturePath: paths.signaturePath,
    signatureSha256: sha256(signatureBytes),
    keyId: receipt.keyId,
    reference: receipt.operator.reference,
    imageId: receipt.operator.imageId,
    sizeBytes: receipt.operator.sizeBytes,
    sourceRevision: receipt.source.revision,
    sourceTree: receipt.source.tree,
    lineageKind: lineage.kind,
  }
  return Object.freeze(lineage.intermediaryReference === undefined
    ? base
    : {
        ...base,
        intermediaryReference: lineage.intermediaryReference,
        intermediaryImageId: lineage.intermediaryImageId,
        intermediarySizeBytes: lineage.intermediarySizeBytes,
      });
}

export function validateWineOperatorBinding(binding, family, sourceRevision) {
  if (!isWinePromotionFamily(family)) {
    if (binding !== undefined) throw new Error('Non-Wine promotion material must not carry wineOperator');
    return;
  }
  const paths = runtimeOperatorReceiptPaths(sourceRevision);
  const expectedKeys = new Set([
    'receiptPath', 'receiptSha256', 'signaturePath', 'signatureSha256', 'keyId', 'reference',
    'imageId', 'sizeBytes', 'sourceRevision', 'sourceTree', 'lineageKind',
    ...(binding?.lineageKind === 'direct' ? [] : [
      'intermediaryReference', 'intermediaryImageId', 'intermediarySizeBytes',
    ]),
  ]);
  if (binding === null || typeof binding !== 'object' || Array.isArray(binding) ||
      Object.keys(binding).some(key => !expectedKeys.has(key)) ||
      binding.receiptPath !== paths.receiptPath || binding.signaturePath !== paths.signaturePath ||
      !digestPattern.test(binding.receiptSha256 ?? '') || !digestPattern.test(binding.signatureSha256 ?? '') ||
      !digestPattern.test(binding.keyId ?? '') || !imageReferencePattern.test(binding.reference ?? '') ||
      !digestPattern.test(binding.imageId ?? '') || !Number.isSafeInteger(binding.sizeBytes) || binding.sizeBytes <= 0 ||
      binding.sizeBytes > maximumWineOperatorSizeBytes ||
      binding.sourceRevision !== sourceRevision || !gitObjectPattern.test(binding.sourceTree ?? '') ||
      !['direct', 'framework-row', 'framework-parent'].includes(binding.lineageKind)) {
    throw new Error('Wine promotion material has an invalid wineOperator binding');
  }
  const needsIntermediary = binding.lineageKind !== 'direct';
  if (needsIntermediary !== Object.hasOwn(binding, 'intermediaryReference') ||
      needsIntermediary !== Object.hasOwn(binding, 'intermediaryImageId') ||
      needsIntermediary !== Object.hasOwn(binding, 'intermediarySizeBytes') ||
      needsIntermediary && (!imageReferencePattern.test(binding.intermediaryReference) ||
        !digestPattern.test(binding.intermediaryImageId) || !Number.isSafeInteger(binding.intermediarySizeBytes) ||
        binding.intermediarySizeBytes <= 0 || binding.intermediarySizeBytes > maximumWineOperatorSizeBytes)) {
    throw new Error('Wine promotion material has an invalid Wine operator intermediary lineage');
  }
  if (family === 'coreclr-wine' && binding.lineageKind !== 'direct') {
    throw new Error('Wine CoreCLR promotion material must use direct clean-operator lineage');
  }
  if (family === 'netfx-clr-wine' && binding.lineageKind === 'direct') {
    throw new Error('Framework Wine promotion material must retain an intermediary lineage');
  }
}
