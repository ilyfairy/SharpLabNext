/**
 * Materialize a bounded-lifetime build context from one exact Git commit.
 * Ignored and untracked working-tree bytes never enter the archive.
 */

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import { isGitCommitIdentity } from './runtime-candidate-input-validation.mjs';

const maximumArchiveBytes = 512 * 1024 * 1024;

function fail(message) { throw new Error(message); }

function validateRequiredPath(value) {
  if (typeof value !== 'string' || value.length === 0 || value.includes('\\') ||
      path.posix.isAbsolute(value) || value.split('/').some(part => part === '' || part === '.' || part === '..')) {
    fail(`committed source required path '${value}' is unsafe`);
  }
  return value;
}

function removeRoot(root, failClosed) {
  try {
    fs.rmSync(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 50 });
  } catch (error) {
    if (failClosed) fail(`committed source context could not be removed: ${error.message}`);
  }
}

export function createCommittedSourceContext(options) {
  const repositoryRoot = path.resolve(options?.repositoryRoot ?? '');
  const revision = options?.revision;
  const spawn = options?.spawn ?? spawnSync;
  if (!isGitCommitIdentity(revision)) {
    fail('committed source revision must be a full lowercase Git commit identity');
  }
  let repositoryInfo
  try { repositoryInfo = fs.lstatSync(repositoryRoot); } catch { fail('committed source repository root does not exist'); }
  if (!repositoryInfo.isDirectory() || repositoryInfo.isSymbolicLink()) {
    fail('committed source repository root must be a real directory');
  }
  const requiredFiles = [...new Set((options?.requiredFiles ?? []).map(validateRequiredPath))];
  if (requiredFiles.length === 0) fail('committed source context requires at least one guarded file');

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-committed-source-'));
  const directory = path.join(root, 'repository');
  const archive = path.join(root, 'source.tar');
  try {
    fs.mkdirSync(directory);
    const archived = spawn(
      'git',
      ['archive', '--format=tar', '--output', archive, revision],
      { cwd: repositoryRoot, encoding: 'utf8', shell: false },
    )
    if (archived.error !== undefined || archived.status !== 0) {
      fail('could not archive the committed candidate source')
    }
    const archiveInfo = fs.lstatSync(archive)
    if (!archiveInfo.isFile() || archiveInfo.isSymbolicLink() ||
        archiveInfo.size < 1 || archiveInfo.size > maximumArchiveBytes) {
      fail('committed candidate source archive is missing, linked, empty, or oversized')
    }
    const extracted = spawn(
      'tar',
      ['--extract', '--file', archive, '--directory', directory],
      { cwd: root, encoding: 'utf8', shell: false },
    )
    if (extracted.error !== undefined || extracted.status !== 0) {
      fail('could not extract the committed candidate source')
    }
    fs.rmSync(archive, { force: true });
    for (const relative of requiredFiles) {
      const filename = path.join(directory, ...relative.split('/'));
      const info = fs.lstatSync(filename, { throwIfNoEntry: false });
      if (!info?.isFile() || info.isSymbolicLink() || info.size < 1) {
        fail(`committed candidate source '${relative}' must be a non-empty regular file`);
      }
    }
    let disposed = false;
    return Object.freeze({
      directory,
      dispose() {
        if (disposed) return;
        disposed = true;
        removeRoot(root, true);
      },
    })
  } catch (error) {
    removeRoot(root, false);
    throw error;
  }
}
