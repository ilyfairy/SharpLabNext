import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const fingerprintVersion = 'sharplabnext-source-content-v1';
const excludedDirectories = new Set(['.git', '.tmp', '_tmp', '.vs', '.idea', '.vscode', 'artifacts', 'bin', 'obj', 'node_modules', 'dist', 'coverage', 'TestResults', 'third_party/ILSense'])

function normalizeRelativePath(value) { return value.replaceAll('\\', '/').replace(/^\.\//, ''); }

function isExcludedPath(value) {
  const relativePath = normalizeRelativePath(value);
  const segments = relativePath.split('/');
  return segments.some(segment => excludedDirectories.has(segment) || segment.startsWith('.sharplabnext-')) ||
    relativePath === 'third_party/ILSense' || relativePath.startsWith('third_party/ILSense/')
}

function updateFile(hash, filename) {
  const descriptor = fs.openSync(filename, 'r');
  const buffer = Buffer.allocUnsafe(64 * 1024);
  try {
    let bytesRead;
    do {
      bytesRead = fs.readSync(descriptor, buffer, 0, buffer.length, null);
      if (bytesRead > 0) hash.update(buffer.subarray(0, bytesRead))
    } while (bytesRead > 0)
  } finally {
    fs.closeSync(descriptor);
  }
}

function collectFiles(root) {
  const files = [];
  const pending = [root];
  while (pending.length > 0) {
    const directory = pending.pop();
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const filename = path.join(directory, entry.name);
      const relativePath = normalizeRelativePath(path.relative(root, filename));
      if (isExcludedPath(relativePath) || entry.isSymbolicLink()) continue
      if (entry.isDirectory()) pending.push(filename);
      else if (entry.isFile()) files.push({ filename, relativePath });
    }
  }
  return files.sort((left, right) => left.relativePath < right.relativePath ? -1 : left.relativePath > right.relativePath ? 1 : 0)
}

// Cache identity is independent of Git, so exported trees and checkouts use
// the same key when their included source bytes are identical.
export function computeBuildCacheInputFingerprintSync(repositoryRoot) {
  const root = fs.realpathSync(path.resolve(repositoryRoot));
  const hash = crypto.createHash('sha256');
  hash.update(`${fingerprintVersion}\0`)
  for (const file of collectFiles(root)) {
    hash.update(file.relativePath);
    hash.update('\0');
    updateFile(hash, file.filename);
    hash.update('\0');
  }
  return `sha256:${hash.digest('hex')}`;
}

export async function computeBuildCacheInputFingerprint(repositoryRoot) { return computeBuildCacheInputFingerprintSync(repositoryRoot); }
