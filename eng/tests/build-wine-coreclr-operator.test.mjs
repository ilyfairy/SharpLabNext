import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  createWineCoreClrOperatorBakeArguments,
  runWineCoreClrOperatorBuild,
  validateWineCoreClrOperatorBuildInputs,
  wineCoreClrOperatorImageTag,
} from '../build-wine-coreclr-operator.mjs'
import { wineCoreClrOperatorExpectedLabels } from '../build-runtime-candidate.mjs'
import { resolveWineCoreClrUserspaceLock } from '../runtime-wine-userspace-lock.mjs'
import {
  verifyWineCoreClrOperatorReceipt,
  wineCoreClrOperatorCommittedFiles,
} from '../release/wine-coreclr-operator-receipt.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const revision = 'f'.repeat(40)

function environment() {
  const userspace = resolveWineCoreClrUserspaceLock(repositoryRoot)
  return {
    IMAGE_PREFIX: 'registry.example/sharplabnext',
    RELEASE_ID: 'operator-test',
    SOURCE_DATE_EPOCH: '1',
    SOURCE_REVISION: revision,
    BASE_DOTNET_RUNTIME_DEPS_IMAGE: `registry.example/runtime-deps@sha256:${'a'.repeat(64)}`,
    WINE_CORECLR_USERSPACE_VERSION: userspace.version,
    WINE_CORECLR_USERSPACE_DIGEST: userspace.digest,
    WINE_CORECLR_USERSPACE_SOURCE_URI: userspace.sourceUri,
  }
}

function output() {
  return {
    errors: [], logs: [],
    error(message) { this.errors.push(message) },
    log(message) { this.logs.push(message) },
  }
}

function fakeDocker(labels, options = {}) {
  const calls = []
  let statusChecks = 0
  const spawn = (command, arguments_, invocation) => {
    calls.push([command, arguments_, invocation])
    if (command === 'git' && arguments_[0] === 'rev-parse') {
      return { status: 0, stdout: `${revision}\n`, stderr: '' }
    }
    if (command === 'git' && arguments_[0] === 'status') {
      statusChecks++
      const dirty = options.dirty === true ||
        (options.dirtyAfterBuild === true && statusChecks > 1)
      return { status: 0, stdout: dirty ? ' M eng/file.mjs\0' : '', stderr: '' }
    }
    if (command === 'docker' && arguments_[0] === 'buildx') {
      return { status: 0, stdout: '', stderr: '' }
    }
    if (command === 'docker' && (arguments_[0] === 'tag' || arguments_[0] === 'push')) {
      return { status: 0, stdout: '', stderr: '' }
    }
    if (command === 'docker' && arguments_[0] === 'image') {
      const reference = arguments_[2]
      const destination = options.publishedDestination
      const repository = destination?.slice(0, destination.lastIndexOf(':'))
      const repoDigest = repository === undefined
        ? undefined
        : `${repository}@sha256:${'2'.repeat(64)}`
      return {
        status: 0,
        stdout: JSON.stringify([{
          Id: `sha256:${'1'.repeat(64)}`,
          Size: 123,
          Os: 'linux',
          Architecture: 'amd64',
          RepoDigests: reference === destination || reference === repoDigest ? [repoDigest] : [],
          Config: { Labels: labels },
        }]),
        stderr: '',
      }
    }
    throw new Error(`unexpected command: ${command} ${arguments_.join(' ')}`)
  }
  return { spawn, calls }
}

function labelsFor(values, sourceBinding) {
  return {
    ...wineCoreClrOperatorExpectedLabels(values, sourceBinding),
    'org.opencontainers.image.revision': values.SOURCE_REVISION,
    'io.sharplabnext.source.revision': values.SOURCE_REVISION,
  }
}

function contextHook(calls, directory = repositoryRoot) {
  return {
    createCommittedSourceContext(options) {
      calls.push(options)
      return { directory, dispose() {} }
    },
  }
}

test('Wine CoreCLR operator removes the amd64 package i386 module before prefix initialization', () => {
  const dockerfile = fs.readFileSync(path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.operator-wine-coreclr'), 'utf8')
  const payloadPath = '/usr/lib/x86_64-linux-gnu/wine/i386-windows'
  const removal = `rm -rf ${payloadPath}`
  const prefixInitialization = 'xvfb-run -a /usr/bin/wineboot-stable --init'

  assert.ok(dockerfile.includes(removal))
  assert.ok(dockerfile.indexOf(removal) < dockerfile.indexOf(prefixInitialization))
  assert.equal(dockerfile.match(new RegExp(`test ! -e ${payloadPath}`, 'g'))?.length, 2);
})

test('formal Wine operator build uses an exact committed source context and verifies committed labels', () => {
  const values = environment()
  const out = output()
  const contexts = []
  const docker = fakeDocker(labelsFor(values, { context: 'committed', promotionEligible: true }))
  assert.equal(runWineCoreClrOperatorBuild([], values, docker.spawn, out, contextHook(contexts)), 0)
  assert.equal(contexts.length, 1)
  assert.equal(contexts[0].revision, revision)
  assert.deepEqual(contexts[0].requiredFiles, [...wineCoreClrOperatorCommittedFiles])
  const bake = docker.calls.find(([command, arguments_]) => command === 'docker' && arguments_[0] === 'buildx')
  assert.ok(bake)
  const committedSource = repositoryRoot
  assert.equal(bake[2].cwd, committedSource)
  assert.equal(bake[2].env.OPERATOR_SOURCE_CONTEXT, 'committed')
  assert.equal(bake[2].env.OPERATOR_PROMOTION_ELIGIBLE, 'true')
  assert.equal(bake[2].env.OPERATOR_DEVELOPMENT_ONLY, 'false')
  assert.ok(bake[1].includes('--load'))
  assert.equal(bake[1].includes(`operator-wine-coreclr.context=${committedSource}`), true);
  assert.match(out.logs.join('\n'), /Verified Wine CoreCLR operator/)
})

test('formal Wine operator derives omitted userspace inputs from the committed lock', () => {
  const complete = environment()
  const values = { ...complete }
  delete values.WINE_CORECLR_USERSPACE_VERSION
  delete values.WINE_CORECLR_USERSPACE_DIGEST
  delete values.WINE_CORECLR_USERSPACE_SOURCE_URI
  const out = output()
  const docker = fakeDocker(labelsFor(complete, { context: 'committed', promotionEligible: true }))
  assert.equal(runWineCoreClrOperatorBuild([], values, docker.spawn, out, contextHook([])), 0)
  const bake = docker.calls.find(([command, arguments_]) =>
    command === 'docker' && arguments_[0] === 'buildx')
  assert.equal(bake[2].env.WINE_CORECLR_USERSPACE_VERSION, complete.WINE_CORECLR_USERSPACE_VERSION)
  assert.equal(bake[2].env.WINE_CORECLR_USERSPACE_DIGEST, complete.WINE_CORECLR_USERSPACE_DIGEST)
  assert.equal(bake[2].env.WINE_CORECLR_USERSPACE_SOURCE_URI, complete.WINE_CORECLR_USERSPACE_SOURCE_URI)
})

test('dirty Wine operator source is rejected for a strict source identity', () => {
  const values = environment()
  const out = output()
  const docker = fakeDocker(labelsFor(values, { context: 'committed', promotionEligible: true }), { dirty: true })
  assert.equal(runWineCoreClrOperatorBuild([], values, docker.spawn, out), 1)
  assert.match(out.errors.join('\n'), /worktree is dirty/)
  assert.equal(docker.calls.some(([command]) => command === 'docker'), false)
})

test('content source identity labels the Wine operator as local-only and cannot look promotable', () => {
  const values = { ...environment(), SHARPLABNEXT_SOURCE_IDENTITY_MODE: 'content' }
  const out = output()
  const docker = fakeDocker(labelsFor(values, {
    context: 'working-tree-content', promotionEligible: false,
  }), { dirty: true })
  assert.equal(runWineCoreClrOperatorBuild([], values, docker.spawn, out), 0)
  const bake = docker.calls.find(([command, arguments_]) => command === 'docker' && arguments_[0] === 'buildx')
  assert.equal(bake[2].env.OPERATOR_SOURCE_CONTEXT, 'working-tree-content')
  assert.equal(bake[2].env.OPERATOR_PROMOTION_ELIGIBLE, 'false')
  assert.equal(bake[2].env.OPERATOR_DEVELOPMENT_ONLY, 'true')
  assert.match(out.logs.join('\n'), /development-only/)
})

test('content source identity makes a clean Wine operator local-only', () => {
  const values = { ...environment(), SHARPLABNEXT_SOURCE_IDENTITY_MODE: 'content' }
  const out = output()
  const docker = fakeDocker(labelsFor(values, {
    context: 'working-tree-content', promotionEligible: false,
  }))
  assert.equal(runWineCoreClrOperatorBuild([], values, docker.spawn, out, {
    createCommittedSourceContext() {
      throw new Error('development image inputs must not claim a committed source context')
    },
  }), 0, out.errors.join('\n'))
  const bake = docker.calls.find(([command, arguments_]) =>
    command === 'docker' && arguments_[0] === 'buildx')
  assert.equal(bake[2].env.OPERATOR_SOURCE_CONTEXT, 'working-tree-content')
  assert.equal(bake[2].env.OPERATOR_PROMOTION_ELIGIBLE, 'false')
  assert.equal(bake[2].env.OPERATOR_DEVELOPMENT_ONLY, 'true')
})

test('Wine operator rejects source drift and incorrect source labels after Bake', () => {
  const values = environment()
  const out = output()
  const drift = fakeDocker(labelsFor(values, { context: 'committed', promotionEligible: true }), {
    dirtyAfterBuild: true,
  })
  assert.equal(runWineCoreClrOperatorBuild([], values, drift.spawn, out, contextHook([])), 1)
  assert.match(out.errors.join('\n'), /worktree is dirty/)
  assert.equal(drift.calls.filter(([command, arguments_]) => command === 'docker' && arguments_[0] === 'image').length, 0)

  const labels = labelsFor(values, { context: 'committed', promotionEligible: true })
  labels['io.sharplabnext.development-only'] = 'true'
  const mismatch = fakeDocker(labels)
  out.errors.length = 0
  assert.equal(runWineCoreClrOperatorBuild([], values, mismatch.spawn, out, contextHook([])), 1)
  assert.match(out.errors.join('\n'), /development-only/)
})

test('Wine operator wrapper keeps direct Bake overrides and remote publication out of the entry point', () => {
  assert.throws(
    () => createWineCoreClrOperatorBakeArguments(['--set', 'operator-wine-coreclr.context=elsewhere']),
    /cannot override validated target fields/,
  )
  assert.throws(
    () => createWineCoreClrOperatorBakeArguments(['--push']),
    /must remain local/,
  )
  const values = environment()
  assert.equal(
    wineCoreClrOperatorImageTag(values),
    'registry.example/sharplabnext/operator-wine-coreclr:operator-test',
  )
  assert.deepEqual(validateWineCoreClrOperatorBuildInputs({
    ...values,
    BASE_DOTNET_RUNTIME_DEPS_IMAGE: 'registry.example/runtime-deps:latest',
  }), [
    'BASE_DOTNET_RUNTIME_DEPS_IMAGE must be a repository@sha256:<64 lowercase hex> reference',
  ])
})

test('non-build Wine operator inspection supplies required provenance without archiving source', () => {
  const values = environment()
  const out = output()
  const docker = fakeDocker(labelsFor(values, { context: 'committed', promotionEligible: true }))
  assert.equal(runWineCoreClrOperatorBuild(['--print'], values, docker.spawn, out, {
    createCommittedSourceContext() { throw new Error('non-build invocation must not archive source') },
  }), 0)
  const bake = docker.calls.find(([command, arguments_]) =>
    command === 'docker' && arguments_[0] === 'buildx')
  assert.ok(bake)
  assert.equal(bake[2].env.OPERATOR_SOURCE_CONTEXT, 'committed')
  assert.equal(bake[2].env.OPERATOR_PROMOTION_ELIGIBLE, 'true')
  assert.equal(bake[2].env.OPERATOR_DEVELOPMENT_ONLY, 'false')
  assert.equal(docker.calls.some(([command]) => command === 'git'), false)
  assert.equal(docker.calls.some(([command, arguments_]) =>
    command === 'docker' && arguments_[0] === 'image'), false)
})

test('formal publication signs the immutable operator and rejects a mismatched private key before Docker', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-wine-receipt-test-'))
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  const { privateKey, publicKey } = crypto.generateKeyPairSync('ed25519')
  const privateKeyPath = path.join(root, 'private.pem')
  const receiptPath = path.join(root, 'operator.json')
  fs.writeFileSync(privateKeyPath, privateKey.export({ format: 'pem', type: 'pkcs8' }))
  const destination = 'registry.example/sharplabnext/operator-wine-coreclr:operator-test'
  const values = {
    ...environment(),
    WINE_CORECLR_OPERATOR_PUBLISH_DESTINATION: destination,
    WINE_CORECLR_OPERATOR_RECEIPT_PATH: receiptPath,
    WINE_CORECLR_OPERATOR_SIGNING_KEY_PATH: privateKeyPath,
  }
  const out = output()
  const docker = fakeDocker(
    labelsFor(values, { context: 'committed', promotionEligible: true }),
    { publishedDestination: destination },
  )
  assert.equal(runWineCoreClrOperatorBuild([], values, docker.spawn, out, {
    ...contextHook([]),
    operatorReceiptPublicKey: publicKey,
  }), 0, out.errors.join('\n'))
  assert.equal(fs.existsSync(receiptPath), true)
  assert.equal(fs.existsSync(`${receiptPath}.sig`), true)
  const receipt = verifyWineCoreClrOperatorReceipt(
    fs.readFileSync(receiptPath),
    fs.readFileSync(`${receiptPath}.sig`),
    { publicKey },
  )
  assert.match(receipt.operator.reference, /@sha256:/)
  assert.deepEqual(Object.keys(receipt.source.files).sort(), [...wineCoreClrOperatorCommittedFiles].sort())

  const other = crypto.generateKeyPairSync('ed25519')
  fs.writeFileSync(privateKeyPath, other.privateKey.export({ format: 'pem', type: 'pkcs8' }))
  const rejected = fakeDocker(labelsFor(values, { context: 'committed', promotionEligible: true }))
  out.errors.length = 0
  assert.equal(runWineCoreClrOperatorBuild([], values, rejected.spawn, out, {
    ...contextHook([]),
    operatorReceiptPublicKey: publicKey,
  }), 1)
  assert.match(out.errors.join('\n'), /does not match the committed.*public key/)
  assert.equal(rejected.calls.some(([command]) => command === 'docker'), false)
})
