import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'

const fingerprintVersion = 'sharplabnext-build-cache-inputs-v1'
const maxDiffBytes = 256 * 1024 * 1024
const excludedRootDirectories = new Set([
  'artifacts',
  '.tmp',
  '_tmp',
  'third_party/ILSense',
])
const excludedBuildDirectories = new Set([
  'bin',
  'obj',
  'node_modules',
  'dist',
  'coverage',
  'TestResults',
])
const sourceIdentityModeEnvironmentVariable = 'SHARPLABNEXT_SOURCE_IDENTITY_MODE'
const contentSourceIdentityMode = 'content'

function normalizeRelativePath(value) {
  return value.replaceAll('\\', '/').replace(/^\.\//, '')
}

function isExcludedPath(value) {
  const relativePath = normalizeRelativePath(value)
  if (excludedRootDirectories.has(relativePath) ||
      [...excludedRootDirectories].some(root => relativePath.startsWith(`${root}/`))) {
    return true
  }
  const segments = relativePath.split('/')
  return segments.some(segment => excludedBuildDirectories.has(segment) ||
    segment.startsWith('.sharplabnext-'))
}

function runGit(repositoryRoot, arguments_, options = {}) {
  const result = spawnSync('git', arguments_, {
    cwd: repositoryRoot,
    encoding: options.encoding,
    maxBuffer: options.maxBuffer,
    shell: false,
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  if (result.error !== undefined) throw result.error
  if (result.status !== 0) {
    const detail = String(result.stderr ?? '').trim()
    throw new Error(`git ${arguments_.join(' ')} failed${detail.length > 0 ? `: ${detail}` : ''}`)
  }
  return result.stdout
}

function readHead(repositoryRoot) {
  const result = spawnSync('git', ['rev-parse', '--verify', 'HEAD'], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
    stdio: ['ignore', 'pipe', 'ignore'],
  })
  if (result.error !== undefined) throw result.error
  if (result.status !== 0) return 'no-head'
  return String(result.stdout).trim() || 'no-head'
}

function updateFile(hash, filename) {
  const descriptor = fs.openSync(filename, 'r')
  const buffer = Buffer.allocUnsafe(64 * 1024)
  try {
    let bytesRead
    do {
      bytesRead = fs.readSync(descriptor, buffer, 0, buffer.length, null)
      if (bytesRead > 0) hash.update(buffer.subarray(0, bytesRead))
    } while (bytesRead > 0)
  } finally {
    fs.closeSync(descriptor)
  }
}

function computeContentFingerprintSync(repositoryRoot) {
  const root = fs.realpathSync(path.resolve(repositoryRoot))
  const files = []
  const pending = [root]
  while (pending.length > 0) {
    const directory = pending.pop()
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const filename = path.join(directory, entry.name)
      const relativePath = normalizeRelativePath(path.relative(root, filename))
      if (isContentExcludedPath(relativePath) || relativePath === '.git' ||
          relativePath.startsWith('.git/')) continue
      if (entry.isSymbolicLink()) continue
      if (entry.isDirectory()) {
        pending.push(filename)
      } else if (entry.isFile()) {
        files.push({ filename, relativePath })
      }
    }
  }
  files.sort((left, right) =>
    left.relativePath < right.relativePath ? -1 :
      left.relativePath > right.relativePath ? 1 : 0)
  const hash = crypto.createHash('sha256')
  hash.update('sharplabnext-source-content-v1\0')
  for (const file of files) {
    hash.update(file.relativePath)
    hash.update('\0')
    updateFile(hash, file.filename)
    hash.update('\0')
  }
  return `sha256:${hash.digest('hex')}`
}

function isContentExcludedPath(value) {
  const relativePath = normalizeRelativePath(value)
  const segments = relativePath.split('/')
  return segments.some(segment =>
    segment === 'artifacts' || segment === '.tmp' || segment === '_tmp' ||
    segment === '.vs' || segment === '.idea' || segment === '.vscode' ||
    excludedBuildDirectories.has(segment) || segment.startsWith('.sharplabnext-'))
}

function trackedDiff(repositoryRoot, head) {
  const diffArguments = [
    'diff', '--binary', '--no-ext-diff', '--no-renames',
    ...(head === 'no-head' ? [] : ['HEAD']), '--', '.',
    ':(exclude)artifacts/**',
    ':(exclude).tmp/**',
    ':(exclude)_tmp/**',
    ':(exclude)third_party/ILSense',
    ':(exclude)third_party/ILSense/**',
    ':(exclude)**/bin/**',
    ':(exclude)**/obj/**',
    ':(exclude)**/node_modules/**',
    ':(exclude)**/dist/**',
    ':(exclude)**/coverage/**',
    ':(exclude)**/TestResults/**',
  ]
  const workingTree = runGit(repositoryRoot, diffArguments, {
    encoding: 'buffer',
    maxBuffer: maxDiffBytes,
  })
  if (head !== 'no-head') return workingTree
  const index = runGit(repositoryRoot, ['diff', '--cached', ...diffArguments.slice(1)], {
    encoding: 'buffer',
    maxBuffer: maxDiffBytes,
  })
  return Buffer.concat([workingTree, index])
}

function untrackedFiles(repositoryRoot) {
  const output = runGit(repositoryRoot, [
    'ls-files', '--others', '--exclude-standard', '-z', '--', '.',
  ], { encoding: 'buffer', maxBuffer: maxDiffBytes })
  return output.toString('utf8')
    .split('\0')
    .filter(Boolean)
    .map(normalizeRelativePath)
    .filter(relativePath => !isExcludedPath(relativePath))
    .sort()
}

export function computeBuildCacheInputFingerprintSync(repositoryRoot) {
  if (String(process.env[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() ===
      contentSourceIdentityMode) {
    return computeContentFingerprintSync(repositoryRoot)
  }
  const root = fs.realpathSync(path.resolve(repositoryRoot))
  try {
    const head = readHead(root)
    const hash = crypto.createHash('sha256')
    hash.update(`${fingerprintVersion}\0`)
    hash.update(`head\0${head}\0`)
    hash.update('tracked-diff\0')
    hash.update(trackedDiff(root, head))

    for (const relativePath of untrackedFiles(root)) {
      const filename = path.resolve(root, ...relativePath.split('/'))
      const info = fs.lstatSync(filename)
      if (info.isSymbolicLink()) {
        hash.update(`untracked-link\0${relativePath}\0`)
        hash.update(fs.readlinkSync(filename))
      } else if (info.isFile()) {
        hash.update(`untracked\0${relativePath}\0${info.mode}\0`)
        updateFile(hash, filename)
      } else {
        continue
      }
      hash.update('\0')
    }
    return `sha256:${hash.digest('hex')}`
  } catch {
    // Exported source trees and source archives have no Git metadata. Their
    // bytes are still a complete and deterministic cache identity.
    return computeContentFingerprintSync(root)
  }
}

export async function computeBuildCacheInputFingerprint(repositoryRoot) {
  return computeBuildCacheInputFingerprintSync(repositoryRoot)
}
