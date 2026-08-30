import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import test from 'node:test'
import {
  createRuntimeMatrixSnapshot,
  RuntimeMatrixSnapshotError,
} from '../../create-runtime-matrix-snapshot.mjs'

function git(root, args, options = {}) {
  const result = spawnSync('git', ['-C', root, ...args], {
    encoding: 'utf8',
    env: { ...process.env, ...options.env },
    input: options.input,
    windowsHide: true,
  })
  if (result.status !== 0) throw new Error(`git ${args.join(' ')} failed: ${result.stderr}`)
  return result.stdout.trim()
}

function write(root, relative, content) {
  const target = path.join(root, relative)
  fs.mkdirSync(path.dirname(target), { recursive: true })
  fs.writeFileSync(target, content)
}

function createRepository() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sln-runtime-snapshot-'))
  git(root, ['init'])
  git(root, ['config', 'user.name', 'Snapshot Test'])
  git(root, ['config', 'user.email', 'snapshot@test.invalid'])
  // Exercise the Windows default; the snapshot implementation must override
  // this for the detached build worktree.
  git(root, ['config', 'core.autocrlf', 'true'])
  write(root, '.gitignore', '*.tmp\n')
  write(root, 'tracked.txt', 'base\n')
  write(root, 'staged.txt', 'base\n')
  git(root, ['add', '-A'])
  git(root, ['commit', '-m', 'base'])
  return root
}

function digest(filePath) { return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex'); }

function cleanup(root) {
  fs.rmSync(root, { recursive: true, force: true })
}

test('creates a clean snapshot of worktree bytes without changing branch or index', t => {
  const root = createRepository()
  t.after(() => cleanup(root))
  const head = git(root, ['rev-parse', 'HEAD'])
  write(root, 'staged.txt', 'index-version\n')
  git(root, ['add', 'staged.txt'])
  write(root, 'staged.txt', 'worktree-version\n')
  write(root, 'tracked.txt', 'changed\n')
  write(root, 'untracked.txt', 'captured\n')
  write(root, '.tmp/ignored.txt', 'ignored\n')
  const indexPath = git(root, ['rev-parse', '--path-format=absolute', '--git-path', 'index'])
  const indexDigest = digest(indexPath)
  const status = git(root, ['status', '--porcelain=v1', '--untracked-files=all'])
  const outputPath = path.join(root, '.tmp', 'candidate')

  const result = createRuntimeMatrixSnapshot({ repositoryRoot: root, outputPath })

  assert.equal(git(root, ['rev-parse', 'HEAD']), head)
  assert.equal(digest(indexPath), indexDigest)
  assert.equal(git(root, ['status', '--porcelain=v1', '--untracked-files=all']), status)
  assert.equal(fs.readFileSync(path.join(outputPath, 'staged.txt'), 'utf8'), 'worktree-version\n')
  assert.equal(fs.readFileSync(path.join(outputPath, 'tracked.txt'), 'utf8'), 'changed\n')
  assert.equal(fs.readFileSync(path.join(outputPath, 'untracked.txt'), 'utf8'), 'captured\n')
  assert.equal(fs.existsSync(path.join(outputPath, '.tmp', 'ignored.txt')), false)
  assert.equal(git(outputPath, ['status', '--porcelain=v1', '--untracked-files=all']), '')
  assert.equal(git(outputPath, ['rev-parse', 'HEAD']), result.snapshotRevision)
  assert.equal(git(root, ['rev-parse', `${result.snapshotRevision}^`]), head)
  assert.equal(git(root, ['rev-parse', result.snapshotRef]), result.snapshotRevision)
})

test('rejects an output path that is not ignored', t => {
  const root = createRepository()
  t.after(() => cleanup(root))
  assert.throws(
    () => createRuntimeMatrixSnapshot({
      repositoryRoot: root,
      outputPath: path.join(root, 'candidate'),
    }),
    error => error instanceof RuntimeMatrixSnapshotError && /must be ignored/.test(error.message),
  )
})

test('detects files changed after the alternate index capture and rolls back outputs', t => {
  const root = createRepository()
  t.after(() => cleanup(root))
  write(root, 'tracked.txt', 'before\n')
  const outputPath = path.join(root, '.tmp', 'drift')
  assert.throws(
    () => createRuntimeMatrixSnapshot(
      { repositoryRoot: root, outputPath },
      { afterIndexCaptured: () => write(root, 'tracked.txt', 'after\n') },
    ),
    error => error instanceof RuntimeMatrixSnapshotError && /changed while/.test(error.message),
  )
  assert.equal(fs.existsSync(outputPath), false)
  assert.equal(
    git(root, ['for-each-ref', '--format=%(refname)', 'refs/sharplabnext/runtime-matrix-snapshots/']),
    '',
  )
})

test('does not delete a target directory created by another process', t => {
  const root = createRepository()
  t.after(() => cleanup(root))
  write(root, 'tracked.txt', 'snapshot-change\n')
  const outputPath = path.join(root, '.tmp', 'raced')
  assert.throws(
    () => createRuntimeMatrixSnapshot(
      { repositoryRoot: root, outputPath },
      { beforeWorktreeAdd: () => write(root, '.tmp/raced/external.txt', 'keep\n') },
    ),
    error => error instanceof RuntimeMatrixSnapshotError && /Git failed/.test(error.message),
  )
  assert.equal(fs.readFileSync(path.join(outputPath, 'external.txt'), 'utf8'), 'keep\n')
  assert.equal(
    git(root, ['for-each-ref', '--format=%(refname)', 'refs/sharplabnext/runtime-matrix-snapshots/']),
    '',
  )
})

test('rejects dirty initialized submodules', t => {
  const root = createRepository()
  const submodule = createRepository()
  t.after(() => cleanup(root))
  t.after(() => cleanup(submodule))
  git(root, ['-c', 'protocol.file.allow=always', 'submodule', 'add', submodule, 'third_party/sample'])
  git(root, ['commit', '-am', 'add submodule'])
  write(path.join(root, 'third_party', 'sample'), 'tracked.txt', 'dirty\n')

  assert.throws(
    () => createRuntimeMatrixSnapshot({
      repositoryRoot: root,
      outputPath: path.join(root, '.tmp', 'dirty-submodule'),
    }),
    error => error instanceof RuntimeMatrixSnapshotError && /Submodule.*must be clean/.test(error.message),
  )
})
