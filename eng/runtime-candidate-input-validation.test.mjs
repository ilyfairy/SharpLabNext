import test from 'node:test'
import assert from 'node:assert/strict'
import childProcess from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import {
  candidateImageLabelBindings,
  isCandidateSourceUri,
  isDigestPinnedImageReference,
  isDotNetSdkVersion,
  isGitCommitIdentity,
  isHttpsUri,
  isSha256Digest,
  isSha512HexDigest,
  validateCandidateExpectedLabels,
  validateCandidateImageIdentity,
  validateCandidateImageInputs,
  validateCandidateImageLabels,
  validateWineCoreClrUserspaceInputs,
} from './runtime-candidate-input-validation.mjs'
import {
  candidateExpectedLabels,
  candidateIdentityLabelBindings,
  candidateImageTag,
  candidateOperationHelpers,
  candidateTargetSpecifications,
  createCandidateBakeArguments,
  runCandidateBuild as runCandidateBuildProduction,
  validateCandidateBuildInputs,
} from './build-runtime-candidate.mjs'
import { findDockerfileStageArgumentScopeViolations } from './dockerfile-stage-arguments.mjs'
import {
  pinnedDockerfileFrontend,
  validateDockerfileFrontend,
} from './dockerfile-frontend.mjs'
import {
  deriveRuntimeCandidateEnvironment,
  frameworkCandidateInputStrategy,
} from './runtime-candidate-environment.mjs'

const digest = 'a'.repeat(64)
const valid = `registry.example/runtime@sha256:${digest}`
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const runtimeMatrix = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
  'utf8',
))
const releaseLock = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'profiles', 'lock.json'),
  'utf8',
))
const wineUserspace = releaseLock.components['wine-coreclr-userspace']
const fakeOperatorReceiptSha256 = `sha256:${'8'.repeat(64)}`
const fakeOperatorReceiptKeyId = 'sha256:8528b0408a4f60a29132610413c90638777a9258f84d1fb5b849ee116445760f'
const developmentWineOperatorTag = 'registry.example/sharplabnext/operator-wine-coreclr:candidate-test'
const developmentWineOperatorImageId = `sha256:${'1'.repeat(64)}`
const sharedMatrixInputSha256 = `sha256:${'c'.repeat(64)}`
const sharedRowOperatorImage = pinnedImage('operator-netfx48', 'e')
const sharedRowDigest = `sha256:${'d'.repeat(64)}`
const shellValidator = path.join(
  repositoryRoot,
  'deploy',
  'docker',
  'validate-digest-pinned-image.sh',
)
const linuxRuntimeVerifier = path.join(
  repositoryRoot,
  'deploy',
  'docker',
  'verify-linux-coreclr-runtime.sh',
)
const shell = findShell()

function runCandidateBuild(argv, values, spawn, output, testHooks = {}) {
  return runCandidateBuildProduction(argv, values, spawn, output, {
    createCommittedSourceContext: () => ({
      directory: repositoryRoot,
      dispose() {},
    }),
    validateSharedFrameworkCandidateProvenance: () => [],
    loadWineCoreClrOperatorReceipt: candidateValues => ({
      receipt: {
        keyId: fakeOperatorReceiptKeyId,
        operator: { reference: candidateValues.RUNTIME_MATRIX_WINE_IMAGE },
      },
      sha256: fakeOperatorReceiptSha256,
    }),
    verifyWineOperatorLineage: () => {},
    verifyWineCoreClrOperatorReceiptBinding: (_candidateValues, _inspection, _sourceRoot, options) => ({
      ...options.loadedReceipt,
      receipt: options.loadedReceipt.receipt,
      sha256: options.loadedReceipt.sha256,
    }),
    ...testHooks,
  })
}

test('Dockerfile stage argument validation rejects a global-only CONTROL_TFM declaration', () => {
  const invalid = [
    'ARG CONTROL_TFM',
    'FROM ${SDK_IMAGE} AS publish',
    'RUN dotnet publish --framework ${CONTROL_TFM}',
  ].join('\n')
  const valid = [
    'ARG CONTROL_TFM',
    'FROM ${SDK_IMAGE} AS publish',
    'ARG CONTROL_TFM',
    'RUN dotnet publish --framework ${CONTROL_TFM}',
  ].join('\n')
  const globalFromUse = [
    'ARG CONTROL_TFM',
    'FROM sdk:${CONTROL_TFM} AS publish',
    'RUN dotnet --info',
  ].join('\n')

  assert.deepEqual(findDockerfileStageArgumentScopeViolations(invalid, 'CONTROL_TFM'), [
    { line: 3, stage: 'publish' },
  ])
  assert.deepEqual(findDockerfileStageArgumentScopeViolations(valid, 'CONTROL_TFM'), [])
  assert.deepEqual(findDockerfileStageArgumentScopeViolations(globalFromUse, 'CONTROL_TFM'), [])
})

test('Dockerfile frontend is pinned and rejects missing or floating directives', () => {
  const pinned = `# syntax=${pinnedDockerfileFrontend}\nFROM scratch\n`
  assert.deepEqual(validateDockerfileFrontend(pinned), [])
  assert.deepEqual(validateDockerfileFrontend('FROM scratch\n'), [
    `must start with '# syntax=${pinnedDockerfileFrontend}'`,
  ])
  assert.deepEqual(validateDockerfileFrontend('# syntax=docker/dockerfile:1.7\nFROM scratch\n'), [
    `must start with '# syntax=${pinnedDockerfileFrontend}'`,
  ])
  assert.deepEqual(
    validateDockerfileFrontend(
      `# syntax=docker/dockerfile:1.7@sha256:${'a'.repeat(64)}\nFROM scratch\n`,
    ),
    [`must start with '# syntax=${pinnedDockerfileFrontend}'`],
  )

  const trackedDockerfiles = childProcess.execFileSync(
    'git',
    ['ls-files', '--', '**/Dockerfile*'],
    { cwd: repositoryRoot, encoding: 'utf8' },
  ).split(/\r?\n/).filter(Boolean)
  assert.ok(trackedDockerfiles.length > 0)
  for (const relativePath of trackedDockerfiles) {
    const source = fs.readFileSync(path.join(repositoryRoot, relativePath), 'utf8')
    assert.deepEqual(validateDockerfileFrontend(source), [], relativePath)
  }
})

function findShell() {
  if (process.platform !== 'win32') return '/bin/sh'
  const candidates = [
    process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'Git', 'usr', 'bin', 'sh.exe'),
    process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'Git', 'bin', 'sh.exe'),
  ].filter(Boolean)
  return candidates.find(candidate => fs.existsSync(candidate))
}

function shellPath(value) {
  return process.platform === 'win32' ? value.replaceAll('\\', '/') : value
}

function shellEnvironment() {
  if (process.platform !== 'win32' || shell === undefined) return process.env
  const gitUsrBin = path.dirname(shell)
  const gitRoot = path.resolve(gitUsrBin, '..', '..')
  return {
    ...process.env,
    PATH: [gitUsrBin, path.join(gitRoot, 'mingw64', 'bin'), process.env.PATH]
      .filter(Boolean)
      .join(path.delimiter),
  }
}

function runShellValidator(...args) {
  return childProcess.spawnSync(
    shell,
    [shellPath(shellValidator), ...args],
    { encoding: 'utf8', env: shellEnvironment() },
  )
}

function runLinuxRuntimeVerifier(mode) {
  const root = fs.mkdtempSync(path.join(repositoryRoot, '.tmp-runtime-verifier-'))
  try {
    const version = '3.0.3'
    const commit = 'c'.repeat(40)
    const shared = path.join(root, 'runtime', 'shared', 'Microsoft.NETCore.App', version)
    const fxr = path.join(root, 'runtime', 'host', 'fxr', version)
    const bin = path.join(root, 'bin')
    fs.mkdirSync(shared, { recursive: true })
    fs.mkdirSync(fxr, { recursive: true })
    fs.mkdirSync(bin, { recursive: true })
    fs.writeFileSync(path.join(shared, '.version'), `${commit}\n${version}\n`)
    // Git for Windows does not preserve a POSIX executable bit on temporary
    // NTFS files. An executable/searchable directory satisfies the verifier's
    // `test -x` fixture precondition without weakening the production check.
    fs.mkdirSync(path.join(root, 'runtime', 'dotnet'))
    fs.writeFileSync(path.join(shared, 'libcoreclrtraceptprovider.so'), '')
    fs.writeFileSync(path.join(shared, 'librequired.so'), '')
    const fakeLdd = path.join(bin, 'ldd')
    fs.writeFileSync(fakeLdd, `#!/bin/sh
case "\${FAKE_LDD_MODE}:\$1" in
  allowed:*libcoreclrtraceptprovider.so)
    echo 'liblttng-ust.so.0 => not found'
    ;;
  extra:*libcoreclrtraceptprovider.so)
    echo 'liblttng-ust.so.0 => not found'
    echo 'libunexpected.so.1 => not found'
    ;;
  wrong-file:*librequired.so)
    echo 'liblttng-ust.so.0 => not found'
    ;;
  *)
    echo 'libc.so.6 => /lib/libc.so.6'
    ;;
esac
`)
    fs.chmodSync(fakeLdd, 0o755)
    return childProcess.spawnSync(
      shell,
      [
        shellPath(linuxRuntimeVerifier),
        shellPath(path.join(root, 'runtime')),
        version,
        commit,
        commit,
      ],
      {
        encoding: 'utf8',
        env: {
          ...shellEnvironment(),
          FAKE_LDD_MODE: mode,
          PATH: [bin, shellEnvironment().PATH].join(path.delimiter),
        },
      },
    )
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
}

function pinnedImage(name, character) {
  return `registry.example/${name}@sha256:${character.repeat(64)}`
}

function commonCandidateEnvironment() {
  return {
    IMAGE_PREFIX: 'registry.example/sharplabnext',
    RELEASE_ID: 'candidate-test',
    SOURCE_DATE_EPOCH: '1',
    SOURCE_REVISION: 'f'.repeat(40),
    BASE_DOTNET_SDK_IMAGE: valid,
    BASE_DOTNET_RUNTIME_DEPS_IMAGE: pinnedImage('runtime-deps', 'a'),
    WINE_CONTROL_TFM: 'net10.0',
    WINE_CORECLR_USERSPACE_VERSION: wineUserspace.resolvedVersion,
    WINE_CORECLR_USERSPACE_DIGEST: wineUserspace.digest,
    WINE_CORECLR_USERSPACE_SOURCE_URI: wineUserspace.sourceUri,
  }
}

function dotnetCandidateEnvironment(id = 'dotnet-9') {
  return {
    ...commonCandidateEnvironment(),
    ...deriveRuntimeCandidateEnvironment(`${id}-linux-x64`, runtimeMatrix).environment,
  }
}

function profilerDotnetCandidateEnvironment(id = 'dotnet-10') {
  return {
    ...commonCandidateEnvironment(),
    ...deriveRuntimeCandidateEnvironment(`${id}-linux-x64`, runtimeMatrix).environment,
  }
}

function legacyDotnetCandidateEnvironment(id = 'dotnet-core-3.0') {
  return {
    ...commonCandidateEnvironment(),
    ...deriveRuntimeCandidateEnvironment(`${id}-linux-x64`, runtimeMatrix).environment,
  }
}

function monoCandidateEnvironment() {
  return {
    ...commonCandidateEnvironment(),
    ...deriveRuntimeCandidateEnvironment(runtimeMatrix.mono.id, runtimeMatrix).environment,
  }
}

function monoWineCandidateEnvironment() {
  return {
    ...commonCandidateEnvironment(),
    RUNTIME_MATRIX_RUNTIME_DIGEST: `sha256:${'b'.repeat(64)}`,
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI: `docker://${pinnedImage('mono-wine', 'b')}`,
    RUNTIME_MATRIX_MONO_WINE_IMAGE: pinnedImage('mono-wine', 'b'),
    RUNTIME_MATRIX_CONTROL_IMAGE: pinnedImage('control', 'c'),
  }
}

function wineDotnetCandidateEnvironment() {
  return {
    ...commonCandidateEnvironment(),
    ...deriveRuntimeCandidateEnvironment('wine-dotnet-9-linux-x64', runtimeMatrix, {
      wineImage: pinnedImage('wine', 'b'),
    }).environment,
  }
}

function wineFrameworkCandidateEnvironment() {
  return {
    ...commonCandidateEnvironment(),
    RUNTIME_MATRIX_PROFILE_ID: 'wine-netfx48-linux-x64',
    RUNTIME_MATRIX_RUNTIME_VERSION: '4.8',
    RUNTIME_MATRIX_RUNTIME_DIGEST: `sha256:${'b'.repeat(64)}`,
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI: `docker://${pinnedImage('wine', 'b')}`,
    RUNTIME_MATRIX_WINE_IMAGE: pinnedImage('wine', 'b'),
    RUNTIME_MATRIX_CONTROL_IMAGE: pinnedImage('control', 'c'),
  }
}

function sharedWineFrameworkCandidateEnvironment() {
  const frameworkInput = {
    schemaVersion: 1,
    strategy: frameworkCandidateInputStrategy,
    parentImage: pinnedImage('framework-parent', 'd'),
    metadataImage: pinnedImage('framework-context', 'e'),
    matrixInputSha256: sharedMatrixInputSha256,
    sourceRevision: 'f'.repeat(40),
    rows: runtimeMatrix.framework.targets.map((row, index) => ({
      id: row.id,
      operatorImage: row.id === 'netfx48'
        ? sharedRowOperatorImage
        : pinnedImage(`operator-${row.id}`, String((index % 8) + 1)),
      rowDigest: row.id === 'netfx48'
        ? sharedRowDigest
        : `sha256:${String((index % 6) + 4).repeat(64)}`,
    })),
  }
  return {
    ...commonCandidateEnvironment(),
    ...deriveRuntimeCandidateEnvironment('wine-netfx48-linux-x64', runtimeMatrix, {
      wineImage: pinnedImage('wine', 'b'),
      frameworkInput,
    }).environment,
  }
}

function candidateEnvironments() {
  return new Map([
    ['runtime-dotnet-matrix-candidate', dotnetCandidateEnvironment()],
    ['runtime-mono-matrix-candidate', monoCandidateEnvironment()],
    ['runtime-mono-wine-matrix-candidate', monoWineCandidateEnvironment()],
    ['runtime-wine-dotnet-matrix-candidate', wineDotnetCandidateEnvironment()],
    ['runtime-wine-framework-matrix-candidate', wineFrameworkCandidateEnvironment()],
    ['runtime-wine-framework-matrix-shared-candidate', sharedWineFrameworkCandidateEnvironment()],
  ])
}

function candidateLabels(target, environment) {
  const specification = candidateTargetSpecifications[target]
  const selectedImageBindings = Object.fromEntries(
    Object.entries(candidateImageLabelBindings)
      .filter(([, inputName]) => specification.imageInputs.includes(inputName)),
  )
  const bindings = {
    ...selectedImageBindings,
    ...candidateIdentityLabelBindings(target, environment),
  }
  return {
    ...candidateExpectedLabels(target),
    ...((specification.matrixBindingKind === 'wine-coreclr' ||
      specification.matrixBindingKind === 'wine-framework') &&
      environment.WINE_CORECLR_DEVELOPMENT_WRAPPER_OPT_IN !== 'true'
      ? {
          'io.sharplabnext.operator.receipt-sha256': fakeOperatorReceiptSha256,
          'io.sharplabnext.operator.receipt-key-id': fakeOperatorReceiptKeyId,
          'io.sharplabnext.operator.userspace-reference': environment.RUNTIME_MATRIX_WINE_IMAGE,
        }
      : {}),
    ...(environment.RUNTIME_CANDIDATE_SOURCE_CONTEXT === undefined
      ? {}
      : {
          'io.sharplabnext.source.context': environment.RUNTIME_CANDIDATE_SOURCE_CONTEXT,
          'com.sharplabnext.runtime-candidate.promotion-eligible':
            environment.RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE,
        }),
    ...Object.fromEntries(
      Object.entries(bindings).map(([label, inputName]) => [label, environment[inputName]]),
    ),
  }
}

function fakeDocker(labels, fixtureOptions = {}) {
  const calls = []
  const gitCalls = []
  let wineOperatorInspections = 0
  return {
    calls,
    gitCalls,
    spawn(command, arguments_, options) {
      if (command === 'git') {
        gitCalls.push([command, arguments_, options])
        return arguments_[0] === 'rev-parse'
          ? { status: 0, stdout: `${'f'.repeat(40)}\n`, stderr: '' }
          : { status: 0, stdout: '', stderr: '' }
      }
      calls.push([command, arguments_, options])
      if (arguments_[0] === 'buildx') return { status: 0 }
      if (arguments_[0] === 'image') {
        const reference = arguments_[2]
        const isWineOperator = reference === (
          fixtureOptions.wineOperatorReference ?? labels['io.sharplabnext.operator-image.wine']
        )
        const defaultWineOperatorLabels = {
          'org.opencontainers.image.title': 'SharpLabNext Wine CoreCLR Operator',
          'org.opencontainers.image.version': 'wine-9.0-noble-amd64',
          'org.opencontainers.image.revision': labels['org.opencontainers.image.revision'],
          'org.opencontainers.image.source': 'https://github.com/sharplabnext/SharpLabNext',
          'io.sharplabnext.source.revision': labels['io.sharplabnext.source.revision'],
          'io.sharplabnext.source.context': 'committed',
          'io.sharplabnext.development-only': 'false',
          'com.sharplabnext.operator.promotion-eligible': 'true',
          'io.sharplabnext.operator-only': 'true',
          'io.sharplabnext.operator.contract': 'wine-coreclr-v1',
          'io.sharplabnext.operator.platform': 'linux-amd64',
          'io.sharplabnext.operator.wine-version': '9.0',
          'io.sharplabnext.operator.prefix': '/opt/wine-dotnet',
          'io.sharplabnext.operator.prefix-architecture': 'win64',
          'io.sharplabnext.operator.root': labels['io.sharplabnext.operator.root'],
          'io.sharplabnext.component.wine-coreclr-userspace.version':
            labels['io.sharplabnext.component.wine-coreclr-userspace.version'],
          'io.sharplabnext.component.wine-coreclr-userspace.digest':
            labels['io.sharplabnext.component.wine-coreclr-userspace.digest'],
          'io.sharplabnext.component.wine-coreclr-userspace.source-uri':
            labels['io.sharplabnext.component.wine-coreclr-userspace.source-uri'],
        }
        const imageLabels = !isWineOperator
          ? labels
            : fixtureOptions.wineOperatorLabels === undefined
              ? defaultWineOperatorLabels
            : typeof fixtureOptions.wineOperatorLabels === 'function'
              ? fixtureOptions.wineOperatorLabels({ ...defaultWineOperatorLabels }, wineOperatorInspections)
              : { ...defaultWineOperatorLabels, ...fixtureOptions.wineOperatorLabels }
        if (isWineOperator) wineOperatorInspections++
        const imageId = !isWineOperator
          ? `sha256:${'1'.repeat(64)}`
          : typeof fixtureOptions.wineOperatorImageId === 'function'
            ? fixtureOptions.wineOperatorImageId(wineOperatorInspections)
            : fixtureOptions.wineOperatorImageId ?? `sha256:${'1'.repeat(64)}`
        return {
          status: 0,
          stdout: JSON.stringify([{
            Id: imageId,
            Size: 536870912,
            Os: 'linux',
            Architecture: 'amd64',
            RepoDigests: isWineOperator ? [reference] : [],
            Config: { Labels: imageLabels },
          }]),
          stderr: '',
        }
      }
      if (arguments_[0] === 'create') {
        return { status: 0, stdout: `${'2'.repeat(64)}\n`, stderr: '' }
      }
      if (arguments_[0] === 'cp') {
        fs.writeFileSync(arguments_[2], 'observed helper bytes')
        return { status: 0, stdout: '', stderr: '' }
      }
      if (arguments_[0] === 'rm') return { status: 0, stdout: '', stderr: '' }
      throw new Error(`Unexpected fake Docker call: ${arguments_.join(' ')}`)
    },
  }
}

test('only repository references with lowercase sha256 digests are accepted', () => {
  assert.equal(isDigestPinnedImageReference(valid), true)
  assert.equal(isDigestPinnedImageReference('registry.example/runtime:latest'), false)
  assert.equal(isDigestPinnedImageReference(`registry.example/runtime@sha256:${'A'.repeat(64)}`), false)
  assert.equal(isDigestPinnedImageReference(`sha256:${digest}`), false)
  assert.equal(isDigestPinnedImageReference(`registry.example/runtime@sha512:${digest}`), false)
  assert.equal(isDigestPinnedImageReference(`registry.example/runtime@sha256:${digest} `), false)
})

test('candidate provenance formats are strict and source URIs remain immutable', () => {
  assert.equal(isSha256Digest(`sha256:${digest}`), true)
  assert.equal(isSha256Digest(`sha256:${'A'.repeat(64)}`), false)
  assert.equal(isSha512HexDigest('b'.repeat(128)), true)
  assert.equal(isSha512HexDigest('B'.repeat(128)), false)
  assert.equal(isGitCommitIdentity('c'.repeat(40)), true)
  assert.equal(isGitCommitIdentity('c'.repeat(64)), true)
  assert.equal(isGitCommitIdentity('c'.repeat(41)), false)
  assert.equal(isDotNetSdkVersion('6.0.135'), true)
  assert.equal(isDotNetSdkVersion('11.0.100-preview.6.26316.8'), true)
  assert.equal(isDotNetSdkVersion('6.0'), false)
  assert.equal(isDotNetSdkVersion('../6.0.135'), false)
  assert.equal(isDotNetSdkVersion('6.0.135 latest'), false)
  assert.equal(isHttpsUri('https://example.invalid/runtime.tar.gz'), true)
  assert.equal(isHttpsUri('http://example.invalid/runtime.tar.gz'), false)
  assert.equal(isHttpsUri('https://user:secret@example.invalid/runtime.tar.gz'), false)
  assert.equal(isCandidateSourceUri('https://example.invalid/audit'), true)
  assert.equal(isCandidateSourceUri(`docker://${pinnedImage('runtime', 'd')}`), true)
  assert.equal(isCandidateSourceUri('docker://registry.example/runtime:latest'), false)
  assert.equal(isCandidateSourceUri('relative/audit.json'), false)
})

test('Wine userspace inputs reject malformed lock-derived component identity', () => {
  const environment = commonCandidateEnvironment()
  assert.deepEqual(validateWineCoreClrUserspaceInputs(environment), [])
  assert.deepEqual(validateWineCoreClrUserspaceInputs({
    ...environment,
    WINE_CORECLR_USERSPACE_VERSION: 'wine 9.0',
    WINE_CORECLR_USERSPACE_DIGEST: 'sha256:invalid',
    WINE_CORECLR_USERSPACE_SOURCE_URI: 'docker://registry.example/wine:latest',
  }), [
    'WINE_CORECLR_USERSPACE_VERSION must be a non-empty whitespace-free version',
    'WINE_CORECLR_USERSPACE_DIGEST must be sha256:<64 lowercase hex>',
    'WINE_CORECLR_USERSPACE_SOURCE_URI must be an absolute HTTPS URI without credentials',
  ])
})

test('candidate input validation is fail-closed for missing and floating values', () => {
  assert.deepEqual(
    validateCandidateImageInputs({
      CONTROL: valid,
      FLOATING: 'registry.example/runtime:9',
    }, ['CONTROL', 'FLOATING', 'MISSING']),
    [
      "FLOATING must use repository@sha256:<64 lowercase hex>; received 'registry.example/runtime:9'",
      'MISSING must be a non-empty repository@sha256:<64 lowercase hex> reference',
    ],
  )
})

test('Dockerfile image validator accepts pinned pairs and rejects floating or malformed references', {
  skip: shell === undefined,
}, () => {
  const second = `registry.example:5000/operator@sha256:${'b'.repeat(64)}`
  const accepted = runShellValidator('CONTROL_IMAGE', valid, 'OPERATOR_IMAGE', second)
  assert.equal(accepted.status, 0, accepted.stderr)

  for (const invalid of [
    'registry.example/runtime:latest',
    `registry.example/runtime@sha256:${'A'.repeat(64)}`,
    `registry.example/runtime@sha512:${digest}`,
    `sha256:${digest}`,
    `${valid} `,
  ]) {
    const rejected = runShellValidator('CONTROL_IMAGE', invalid)
    assert.equal(rejected.status, 1, invalid)
    assert.match(rejected.stderr, /Digest-pinned image validation failed/)
  }

  const oddArguments = runShellValidator('CONTROL_IMAGE')
  assert.equal(oddArguments.status, 1)
  assert.match(oddArguments.stderr, /NAME VALUE pairs/)
})

test('Dockerfile image validator gates the development Wine tag and identity independently', {
  skip: shell === undefined,
}, () => {
  const tag = 'sharplabnext/operator-wine-coreclr:development'
  const identity = `sha256:${'c'.repeat(64)}`
  const accepted = runShellValidator(
    '--allow-bare-image-id', 'true',
    '--allow-local-image-tag', 'true',
    'CONTROL_IMAGE', valid,
    'WINE_IMAGE', tag,
    'WINE_IDENTITY', identity,
  )
  assert.equal(accepted.status, 0, accepted.stderr)

  for (const arguments_ of [
    ['CONTROL_IMAGE', valid, 'WINE_IMAGE', tag, 'WINE_IDENTITY', identity],
    ['--allow-bare-image-id', 'true', '--allow-local-image-tag', 'true', 'WINE_IMAGE', 'latest', 'WINE_IDENTITY', identity],
    ['--allow-bare-image-id', 'true', '--allow-local-image-tag', 'true', 'WINE_IMAGE', tag, 'WINE_IDENTITY', `sha256:${'A'.repeat(64)}`],
  ]) {
    const rejected = runShellValidator(...arguments_)
    assert.equal(rejected.status, 1, arguments_.join(' '))
    assert.match(rejected.stderr, /Digest-pinned image validation failed/)
  }
})

test('candidate Bake graph is isolated from the production default graph', () => {
  const production = fs.readFileSync(path.join(repositoryRoot, 'eng', 'bake.hcl'), 'utf8')
  const candidates = fs.readFileSync(
    path.join(repositoryRoot, 'eng', 'bake.runtime-candidates.hcl'),
    'utf8',
  )
  assert.doesNotMatch(production, /RUNTIME_MATRIX_|runtime-[a-z-]+-matrix-candidate/)
  assert.doesNotMatch(candidates, /group\s+"default"|required\(RUNTIME_MATRIX_/)
  for (const target of Object.keys(candidateTargetSpecifications)) {
    assert.match(candidates, new RegExp(`target "${target}"`))
  }
})

test('candidate entry validates selected inputs before loading both reviewed Bake files', () => {
  const expectedImageInputs = {
    'runtime-dotnet-matrix-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_BASE_IMAGE',
    ],
    'runtime-mono-matrix-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_MONO_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ],
    'runtime-mono-wine-matrix-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_MONO_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ],
    'runtime-wine-dotnet-matrix-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
      'BASE_DOTNET_RUNTIME_DEPS_IMAGE',
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ],
    'runtime-wine-framework-matrix-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
      'BASE_DOTNET_RUNTIME_DEPS_IMAGE',
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ],
    'runtime-wine-framework-matrix-shared-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
      'BASE_DOTNET_RUNTIME_DEPS_IMAGE',
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
      'RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
      'RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
    ],
  }
  for (const [target, inputs] of Object.entries(expectedImageInputs)) {
    assert.deepEqual(candidateTargetSpecifications[target].imageInputs, inputs)
  }

  const environment = monoCandidateEnvironment()
  assert.deepEqual(validateCandidateBuildInputs('runtime-mono-matrix-candidate', environment), [])
  assert.match(
    validateCandidateBuildInputs('runtime-mono-matrix-candidate', {
      ...environment,
      RUNTIME_MATRIX_MONO_IMAGE: 'registry.example/mono:latest',
    }).join('\n'),
    /RUNTIME_MATRIX_MONO_IMAGE must use repository@sha256/,
  )
  assert.match(
    validateCandidateBuildInputs('runtime-mono-matrix-candidate', {
      ...environment,
      RUNTIME_MATRIX_MONO_IMAGE: pinnedImage('mono', 'e'),
    }).join('\n'),
    /RUNTIME_MATRIX_MONO_IMAGE must equal/,
  )

  assert.deepEqual(createCandidateBakeArguments('runtime-mono-matrix-candidate', ['--print']), [
    'buildx',
    'bake',
    '--file',
    'eng/bake.hcl',
    '--file',
    'eng/bake.runtime-candidates.hcl',
    '--print',
    'runtime-mono-matrix-candidate',
  ])
  assert.deepEqual(createCandidateBakeArguments('runtime-mono-matrix-candidate'), [
    'buildx',
    'bake',
    '--file',
    'eng/bake.hcl',
    '--file',
    'eng/bake.runtime-candidates.hcl',
    '--load',
    'runtime-mono-matrix-candidate',
  ])
  const committedRoot = path.resolve('committed-candidate-source')
  const committed = createCandidateBakeArguments(
    'runtime-mono-matrix-candidate',
    [],
    environment,
    committedRoot,
  )
  assert.deepEqual(committed.slice(0, 7), [
    'buildx',
    'bake',
    '--file',
    path.join(committedRoot, 'eng', 'bake.hcl'),
    '--file',
    path.join(committedRoot, 'eng', 'bake.runtime-candidates.hcl'),
    '--set',
  ])
  assert.equal(
    committed[7],
    `runtime-mono-matrix-candidate.context=${committedRoot}`,
  )
  assert.ok(committed.includes('--load'))
  assert.deepEqual(
    createCandidateBakeArguments('runtime-mono-matrix-candidate', ['--call=check']),
    [
      'buildx',
      'bake',
      '--file',
      'eng/bake.hcl',
      '--file',
      'eng/bake.runtime-candidates.hcl',
      '--set',
      'runtime-mono-matrix-candidate.output=type=cacheonly',
      '--call=check',
      'runtime-mono-matrix-candidate',
    ],
  )
  assert.throws(
    () => createCandidateBakeArguments('runtime-mono-matrix-candidate', ['--file=other.hcl']),
    /cannot override the reviewed Bake files/,
  )
  assert.throws(
    () => createCandidateBakeArguments('runtime-mono-matrix-candidate', [
      '--set',
      'runtime-mono-matrix-candidate.args.MONO_IMAGE=registry.example/mono:latest',
    ]),
    /cannot override validated target fields/,
  )
  assert.throws(
    () => createCandidateBakeArguments('runtime-mono-matrix-candidate', [
      'runtime-wine-framework-matrix-candidate',
    ]),
    /unsupported candidate Bake option/,
  )
  assert.throws(
    () => createCandidateBakeArguments('runtime-mono-matrix-candidate', ['--push']),
    /must remain local until their image labels are verified/,
  )
  assert.throws(
    () => createCandidateBakeArguments('runtime-mono-matrix-candidate', ['--call=build']),
    /unsupported candidate Bake --call value/,
  )
})

test('shared Framework candidate binds parent and selected-row identity before Docker', () => {
  const environment = sharedWineFrameworkCandidateEnvironment()
  assert.deepEqual(
    validateCandidateBuildInputs('runtime-wine-framework-matrix-shared-candidate', environment),
    [],
  )
  const sourceRevisionMismatch = {
    ...environment,
    RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: 'e'.repeat(40),
  }
  assert.match(
    validateCandidateBuildInputs(
      'runtime-wine-framework-matrix-shared-candidate',
      sourceRevisionMismatch,
    ).join('\n'),
    /RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION must equal/,
  )
  const operatorDigestMismatch = {
    ...environment,
    RUNTIME_MATRIX_RUNTIME_DIGEST: `sha256:${'f'.repeat(64)}`,
  }
  assert.match(
    validateCandidateBuildInputs('runtime-wine-framework-matrix-shared-candidate', operatorDigestMismatch).join('\n'),
    /RUNTIME_MATRIX_RUNTIME_DIGEST for selected Framework operator image/,
  )
  const sourceMismatch = {
    ...environment,
    RUNTIME_MATRIX_RUNTIME_SOURCE_URI: `docker://${pinnedImage('operator-other', 'f')}`,
  }
  assert.match(
    validateCandidateBuildInputs('runtime-wine-framework-matrix-shared-candidate', sourceMismatch).join('\n'),
    /RUNTIME_MATRIX_RUNTIME_SOURCE_URI for selected Framework row must equal/,
  )
  const rowDigestMismatch = {
    ...environment,
    RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST: `sha256:${'f'.repeat(64)}`,
  }
  assert.deepEqual(
    validateCandidateBuildInputs('runtime-wine-framework-matrix-shared-candidate', rowDigestMismatch),
    [],
    'row content digest is independently verified by the parent selector',
  )
})

test('shared Framework historical development input relaxes only the revision equality rule', () => {
  const target = 'runtime-wine-framework-matrix-shared-candidate'
  const environment = sharedWineFrameworkCandidateEnvironment()
  const historical = {
    ...environment,
    RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: 'e'.repeat(40),
  }
  assert.match(
    validateCandidateBuildInputs(target, historical).join('\n'),
    /RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION must equal/,
  )
  assert.deepEqual(validateCandidateBuildInputs(target, historical, repositoryRoot, {
    allowHistoricalFrameworkInputForDevelopment: true,
  }), [])
  assert.match(
    validateCandidateBuildInputs(target, {
      ...historical,
      RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: historical.SOURCE_REVISION,
    }, repositoryRoot, {
      allowHistoricalFrameworkInputForDevelopment: true,
    }).join('\n'),
    /must differ from SOURCE_REVISION/,
  )
  assert.match(
    validateCandidateBuildInputs('runtime-wine-dotnet-matrix-candidate', {
      ...wineDotnetCandidateEnvironment(),
      RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: 'e'.repeat(40),
    }, repositoryRoot, {
      allowHistoricalFrameworkInputForDevelopment: true,
    }).join('\n'),
    /supported only for the shared Framework candidate/,
  )
})

test('shared Framework historical development build is local-only, records both source contexts, and omits current Wine userspace attestations', () => {
  const target = 'runtime-wine-framework-matrix-shared-candidate'
  const environment = {
    ...sharedWineFrameworkCandidateEnvironment(),
    RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: 'e'.repeat(40),
    RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_DEVELOPMENT_OPT_IN: 'true',
  }
  const output = {
    errors: [], logs: [],
    log(message) { this.logs.push(message) },
    error(message) { this.errors.push(message) },
  }
  const labelsFor = context => {
    const labels = candidateLabels(target, {
      ...environment,
      RUNTIME_CANDIDATE_SOURCE_CONTEXT: context,
      RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE: 'false',
      RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT: 'true',
    })
    for (const label of [
      'io.sharplabnext.operator.receipt-sha256',
      'io.sharplabnext.operator.receipt-key-id',
      'io.sharplabnext.operator.userspace-reference',
    ]) delete labels[label]
    return labels
  }

  const missingFlag = fakeDocker(labelsFor('committed-historical-framework-input-development'))
  assert.equal(runCandidateBuild([target], environment, missingFlag.spawn, output), 64)
  assert.match(output.errors.join('\n'), /requires both the wrapper and candidate opt-ins/)

  output.errors.length = 0
  const checkOnly = fakeDocker(labelsFor('committed-historical-framework-input-development'))
  assert.equal(runCandidateBuild([
    target,
    '--allow-historical-framework-input-for-development',
    '--check',
  ], environment, checkOnly.spawn, output), 64)
  assert.match(output.errors.join('\n'), /accepted only for a real local build/)

  output.errors.length = 0
  const receiptInput = fakeDocker(labelsFor('committed-historical-framework-input-development'))
  assert.equal(runCandidateBuild([
    target,
    '--allow-historical-framework-input-for-development',
  ], {
    ...environment,
    WINE_CORECLR_OPERATOR_RECEIPT: 'D:\\operator-receipt.json',
    WINE_CORECLR_OPERATOR_RECEIPT_SIG: 'D:\\operator-receipt.json.sig',
  }, receiptInput.spawn, output), 64)
  assert.match(output.errors.join('\n'), /must not receive formal receipt inputs/)

  output.errors.length = 0
  const nonShared = fakeDocker(candidateLabels('runtime-wine-dotnet-matrix-candidate', wineDotnetCandidateEnvironment()))
  assert.equal(runCandidateBuild([
    'runtime-wine-dotnet-matrix-candidate',
    '--allow-historical-framework-input-for-development',
  ], {
    ...wineDotnetCandidateEnvironment(),
    RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_DEVELOPMENT_OPT_IN: 'true',
    RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: 'e'.repeat(40),
  }, nonShared.spawn, output), 64)
  assert.match(output.errors.join('\n'), /supported only for the shared Framework candidate/)

  for (const [dirty, context] of [
    [false, 'committed-historical-framework-input-development'],
    [true, 'working-tree-historical-framework-input-development'],
  ]) {
    output.errors.length = 0
    output.logs.length = 0
    const docker = fakeDocker(labelsFor(context))
    let bakeEnvironment
    const contexts = []
    const spawn = (command, arguments_, options) => {
      if (command === 'git' && arguments_[0] === 'status') {
        return { status: 0, stdout: dirty ? ' M eng/file.mjs\n' : '', stderr: '' }
      }
      if (command === 'docker' && arguments_[0] === 'buildx') bakeEnvironment = options.env
      return docker.spawn(command, arguments_, options)
    }
    assert.equal(runCandidateBuildProduction([
      target,
      '--allow-uncommitted-source-for-development',
      '--allow-historical-framework-input-for-development',
    ], environment, spawn, output, {
      createCommittedSourceContext(options) {
        contexts.push(options.revision)
        return { directory: repositoryRoot, dispose() {} }
      },
      validateSharedFrameworkCandidateProvenance(values, _spawn, sourceRoot, options) {
        assert.equal(values.RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION, 'e'.repeat(40))
        assert.equal(sourceRoot, repositoryRoot)
        assert.equal(options.allowHistoricalFrameworkInputForDevelopment, true)
        assert.equal(options.repositoryRoot, repositoryRoot)
        const historicalContext = options.createCommittedSourceContext({
          revision: values.RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION,
        })
        historicalContext.dispose()
        return []
      },
      loadWineCoreClrOperatorReceipt() { throw new Error('historical Framework mode must not load a receipt') },
    }), 0, output.errors.join('\n'))
    assert.deepEqual(contexts, dirty ? ['e'.repeat(40)] : ['f'.repeat(40), 'e'.repeat(40)])
    assert.equal(bakeEnvironment.RUNTIME_CANDIDATE_SOURCE_CONTEXT, context)
    assert.equal(bakeEnvironment.RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE, 'false')
    assert.equal(bakeEnvironment.RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT, 'true')
    for (const name of [
      'WINE_CORECLR_OPERATOR_RECEIPT',
      'WINE_CORECLR_OPERATOR_RECEIPT_SIG',
      'WINE_CORECLR_OPERATOR_RECEIPT_SHA256',
      'WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID',
      'WINE_CORECLR_OPERATOR_REFERENCE',
    ]) assert.equal(bakeEnvironment[name], undefined, `${name} must not enter historical Framework Bake`)
    assert.equal(bakeEnvironment.WINE_CORECLR_USERSPACE_VERSION, wineUserspace.resolvedVersion)
    assert.equal(bakeEnvironment.WINE_CORECLR_USERSPACE_DIGEST, wineUserspace.digest)
    assert.equal(bakeEnvironment.WINE_CORECLR_USERSPACE_SOURCE_URI, wineUserspace.sourceUri)
    const historicalBindings = candidateIdentityLabelBindings(target, bakeEnvironment)
    for (const label of [
      'io.sharplabnext.component.wine-coreclr-userspace.version',
      'io.sharplabnext.component.wine-coreclr-userspace.digest',
      'io.sharplabnext.component.wine-coreclr-userspace.source-uri',
    ]) assert.equal(historicalBindings[label], undefined, `${label} must not bind historical image output`)
    assert.match(output.logs.join('\n'), /promotion output remains disabled/)
  }
})

test('shared Framework historical development build rejects Wine userspace and receipt labels while formal labels remain valid', () => {
  const target = 'runtime-wine-framework-matrix-shared-candidate'
  const environment = {
    ...sharedWineFrameworkCandidateEnvironment(),
    RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION: 'e'.repeat(40),
    RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_DEVELOPMENT_OPT_IN: 'true',
  }
  const baseLabels = candidateLabels(target, {
    ...environment,
    RUNTIME_CANDIDATE_SOURCE_CONTEXT: 'committed-historical-framework-input-development',
    RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE: 'false',
    RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT: 'true',
  })
  const output = { errors: [], log() {}, error(message) { this.errors.push(message) } }
  for (const label of [
    'io.sharplabnext.component.wine-coreclr-userspace.version',
    'io.sharplabnext.component.wine-coreclr-userspace.digest',
    'io.sharplabnext.component.wine-coreclr-userspace.source-uri',
    'io.sharplabnext.operator.receipt-sha256',
    'io.sharplabnext.operator.receipt-key-id',
    'io.sharplabnext.operator.userspace-reference',
  ]) {
    output.errors.length = 0
    const docker = fakeDocker({ ...baseLabels, [label]: 'forbidden' })
    assert.equal(runCandidateBuild([
      target,
      '--allow-historical-framework-input-for-development',
    ], environment, docker.spawn, output), 1, label)
    assert.match(output.errors.join('\n'), new RegExp(`${label.replaceAll('.', '\\.') } must be absent`))
  }

  const formal = sharedWineFrameworkCandidateEnvironment()
  const formalDocker = fakeDocker(candidateLabels(target, formal))
  const formalOutput = { errors: [], log() {}, error(message) { this.errors.push(message) } }
  let formalBakeEnvironment
  const formalSpawn = (command, arguments_, options) => {
    if (command === 'docker' && arguments_[0] === 'buildx') formalBakeEnvironment = options.env
    return formalDocker.spawn(command, arguments_, options)
  }
  assert.equal(runCandidateBuild([target], formal, formalSpawn, formalOutput), 0, formalOutput.errors.join('\n'))
  assert.equal(formalBakeEnvironment.RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT, 'false')
  assert.equal(formalBakeEnvironment.WINE_CORECLR_USERSPACE_DIGEST, wineUserspace.digest)
})

test('shared Framework build revalidates immutable provenance before Bake', () => {
  const target = 'runtime-wine-framework-matrix-shared-candidate'
  const environment = sharedWineFrameworkCandidateEnvironment()
  const output = {
    errors: [], logs: [],
    log(message) { this.logs.push(message) },
    error(message) { this.errors.push(message) },
  }
  const accepted = fakeDocker(candidateLabels(target, environment))
  let validations = 0
  let contexts = 0
  assert.equal(runCandidateBuildProduction(
    [target],
    environment,
    accepted.spawn,
    output,
    {
      createCommittedSourceContext(options) {
        contexts++
        assert.equal(options.revision, environment.SOURCE_REVISION)
        assert.ok(options.requiredFiles.includes('eng/bake.hcl'))
        assert.ok(options.requiredFiles.includes('profiles/runtime-matrix.json'))
        assert.ok(options.requiredFiles.includes('profiles/runtime-framework-installers.json'))
        assert.ok(options.requiredFiles.includes(
          'deploy/docker/Dockerfile.runtime-wine-framework-matrix-shared',
        ))
        return { directory: repositoryRoot, dispose() {} }
      },
      validateSharedFrameworkCandidateProvenance(values, spawn) {
        validations++
        assert.deepEqual(values, environment)
        assert.equal(spawn, accepted.spawn)
        return []
      },
      loadWineCoreClrOperatorReceipt: candidateValues => ({
        receipt: {
          keyId: fakeOperatorReceiptKeyId,
          operator: { reference: candidateValues.RUNTIME_MATRIX_WINE_IMAGE },
        },
        sha256: fakeOperatorReceiptSha256,
      }),
      verifyWineOperatorLineage: () => {},
      verifyWineCoreClrOperatorReceiptBinding: (_values, _inspection, _root, options) => ({
        ...options.loadedReceipt,
        receipt: options.loadedReceipt.receipt,
        sha256: options.loadedReceipt.sha256,
      }),
    },
  ), 0, output.errors.join('\n'))
  assert.equal(validations, 1)
  assert.equal(contexts, 1)
  assert.match(output.logs.join('\n'), /Validated immutable Framework metadata/)
  const acceptedBake = accepted.calls.find(([, arguments_]) => arguments_[0] === 'buildx')
  assert.ok(acceptedBake)
  assert.ok(acceptedBake[1].includes(
    `${target}.context=${repositoryRoot}`,
  ))

  const rejected = fakeDocker(candidateLabels(target, environment))
  output.errors.length = 0
  output.logs.length = 0
  assert.equal(runCandidateBuildProduction(
    [target],
    environment,
    rejected.spawn,
    output,
    {
      createCommittedSourceContext: () => ({
        directory: repositoryRoot,
        dispose() {},
      }),
      validateSharedFrameworkCandidateProvenance: () => ['selected row drifted'],
    },
  ), 1)
  assert.equal(rejected.calls.length, 0)
  assert.match(output.errors.join('\n'), /selected row drifted/)
})

test('candidate entry binds target, profile ID, version, and payload to one matrix row', () => {
  const framework = wineFrameworkCandidateEnvironment()
  const frameworkVersionSwap = {
    ...framework,
    RUNTIME_MATRIX_PROFILE_ID: 'wine-netfx40-linux-x64',
  }
  assert.match(
    validateCandidateBuildInputs(
      'runtime-wine-framework-matrix-candidate',
      frameworkVersionSwap,
    ).join('\n'),
    /RUNTIME_MATRIX_RUNTIME_VERSION must equal '4\.0'; received '4\.8'/,
  )

  const dotnet = dotnetCandidateEnvironment()
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...dotnet,
      RUNTIME_MATRIX_PROFILE_ID: 'wine-netfx48-linux-x64',
      RUNTIME_MATRIX_RUNTIME_VERSION: '4.8',
    }).join('\n'),
    /has no matching CoreCLR row/,
  )
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...dotnet,
      RUNTIME_MATRIX_RUNTIME_URL: 'https://example.invalid/wrong-runtime.tar.gz',
    }).join('\n'),
    /RUNTIME_MATRIX_RUNTIME_URL must equal 'https:\/\/builds\.dotnet\.microsoft\.com/,
  )
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...dotnet,
      RUNTIME_MATRIX_RUNTIME_SHA512: 'f'.repeat(128),
    }).join('\n'),
    /RUNTIME_MATRIX_RUNTIME_SHA512 must equal/,
  )
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...dotnet,
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: 'https://example.invalid/wrong-source',
    }).join('\n'),
    /RUNTIME_MATRIX_RUNTIME_SOURCE_URI must equal 'https:\/\/builds\.dotnet\.microsoft\.com/,
  )

  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...dotnet,
      RUNTIME_MATRIX_PROFILE_ID: 'const-generics-linux-x64',
    }).join('\n'),
    /profiles\/runtimes\/candidates\/const-generics-linux-x64\.json/,
  )

  const core30Failures = validateCandidateBuildInputs(
    'runtime-dotnet-matrix-candidate',
    legacyDotnetCandidateEnvironment(),
  )
  assert.deepEqual(core30Failures, [])
})

test('Checked JIT inputs and helper identity are closed by the selected matrix row', () => {
  const checked = dotnetCandidateEnvironment()
  assert.deepEqual(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', checked),
    [],
  )
  assert.equal(
    candidateOperationHelpers('runtime-dotnet-matrix-candidate', checked).jit.implementation,
    'sharplabnext-checked-jit-bridge-v1',
  )
  assert.equal(
    candidateOperationHelpers('runtime-dotnet-matrix-candidate', checked).jit.assemblyPath,
    '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
  )

  for (const [inputName, invalidValue, expectedError] of [
    ['RUNTIME_MATRIX_CHECKED_JIT_COMMIT', 'a'.repeat(40), /must equal 'd839c41c/],
    [
      'RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL',
      'https://github.com/dotnet/runtime/archive/' + 'a'.repeat(40) + '.tar.gz',
      /RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL must equal/,
    ],
    ['RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512', 'a'.repeat(128), /must equal/],
    [
      'RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE',
      'mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-20.04-amd64',
      /must use repository@sha256/,
    ],
    ['RUNTIME_MATRIX_CHECKED_JIT_CONFIGURATION', 'Release', /must equal 'Checked'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_TARGET_OS', 'windows', /must equal 'linux'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_ARCHITECTURE', 'arm64', /must equal 'x64'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_BUILD_COMPONENT', 'runtime', /must equal 'jit'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_PGO_MODE', 'enabled', /must equal 'disabled-by-default'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_COMPILER', 'clang', /must equal 'gcc'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_GENERATOR', 'ninja', /must equal 'make'/],
    [
      'RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE',
      'skip-by-upstream-flag',
      /must equal ''/,
    ],
    ['RUNTIME_MATRIX_CHECKED_JIT_SOURCE_MAPPING_KIND', 'none', /must equal 'checked-jit-debug-info'/],
  ]) {
    assert.match(
      validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
        ...checked,
        [inputName]: invalidValue,
      }).join('\n'),
      expectedError,
      inputName,
    )
  }

  const legacy = legacyDotnetCandidateEnvironment()
  assert.equal(
    candidateOperationHelpers('runtime-dotnet-matrix-candidate', legacy).jit.implementation,
    'sharplabnext-legacy-jit-inspector-v1',
  )
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...legacy,
      RUNTIME_MATRIX_CHECKED_JIT_COMMIT: checked.RUNTIME_MATRIX_CHECKED_JIT_COMMIT,
    }).join('\n'),
    /must be empty because matrix row 'dotnet-core-3\.0' has no checkedJit lock/,
  )
})

test('Checked JIT bootstrap SDK inputs and labels are closed only by the .NET 6 matrix row', () => {
  const net6 = dotnetCandidateEnvironment('dotnet-6')
  assert.deepEqual(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', net6),
    [],
  )

  const expectedLabels = candidateIdentityLabelBindings(
    'runtime-dotnet-matrix-candidate',
    net6,
  )
  assert.equal(
    expectedLabels['io.sharplabnext.jit.checked.bootstrap-sdk.version'],
    'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION',
  )
  assert.equal(
    expectedLabels['io.sharplabnext.jit.checked.bootstrap-sdk.source-uri'],
    'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL',
  )
  assert.equal(
    expectedLabels['io.sharplabnext.jit.checked.bootstrap-sdk.source-sha512'],
    'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512',
  )
  assert.equal(
    net6.RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE,
    'skip-by-upstream-flag',
  )
  assert.equal(
    expectedLabels['io.sharplabnext.jit.checked.version-generation-mode'],
    'RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE',
  )

  for (const [inputName, invalidValue, expectedError] of [
    ['RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION', '', /must equal '6\.0\.135'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION', '6.0.136', /must equal '6\.0\.135'/],
    [
      'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL',
      'https://builds.dotnet.microsoft.com/dotnet/Sdk/6.0.136/dotnet-sdk-6.0.136-linux-x64.tar.gz',
      /RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL must equal/,
    ],
    ['RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL', 'http://example.invalid/sdk.tar.gz', /absolute HTTPS URI/],
    ['RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512', 'a'.repeat(128), /must equal/],
    ['RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512', 'A'.repeat(128), /128-character lowercase/],
    ['RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE', '', /must equal 'skip-by-upstream-flag'/],
    ['RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE', 'default', /must equal 'skip-by-upstream-flag'/],
  ]) {
    assert.match(
      validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
        ...net6,
        [inputName]: invalidValue,
      }).join('\n'),
      expectedError,
      inputName,
    )
  }

  const net7 = dotnetCandidateEnvironment('dotnet-7')
  assert.deepEqual(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', net7),
    [],
  )
  assert.equal(
    candidateIdentityLabelBindings('runtime-dotnet-matrix-candidate', net7)
      ['io.sharplabnext.jit.checked.bootstrap-sdk.version'],
    undefined,
  )
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...net7,
      RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION:
        net6.RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION,
    }).join('\n'),
    /RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION must equal ''/,
  )
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...net7,
      RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE:
        net6.RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE,
    }).join('\n'),
    /RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE must equal ''/,
  )

  const missingBootstrapLabel = candidateLabels('runtime-dotnet-matrix-candidate', net6)
  delete missingBootstrapLabel['io.sharplabnext.jit.checked.bootstrap-sdk.source-sha512']
  assert.match(
    validateCandidateImageLabels(
      missingBootstrapLabel,
      net6,
      candidateIdentityLabelBindings('runtime-dotnet-matrix-candidate', net6),
    ).join('\n'),
    /bootstrap-sdk\.source-sha512.*observed <missing>/,
  )

  const missingVersionGenerationLabel = candidateLabels(
    'runtime-dotnet-matrix-candidate',
    net6,
  )
  delete missingVersionGenerationLabel['io.sharplabnext.jit.checked.version-generation-mode']
  assert.match(
    validateCandidateImageLabels(
      missingVersionGenerationLabel,
      net6,
      candidateIdentityLabelBindings('runtime-dotnet-matrix-candidate', net6),
    ).join('\n'),
    /version-generation-mode.*observed <missing>/,
  )
})

test('modern profiler inputs, helpers, and labels are closed by the selected matrix row', () => {
  const profiler = profilerDotnetCandidateEnvironment()
  assert.deepEqual(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', profiler),
    [],
  )
  assert.deepEqual(
    candidateOperationHelpers('runtime-dotnet-matrix-candidate', profiler),
    {
      run: {
        implementation: 'sharplabnext-runner-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.Runner.dll',
      },
      jit: {
        implementation: 'sharplabnext-jit-inspector-v1',
        assemblyPath: '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
        profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
      },
    },
  )

  for (const [inputName, invalidValue, expectedError] of [
    ['RUNTIME_MATRIX_PROFILER_PROVIDER_ID', 'other-profiler', /must equal 'sharplabnext-linux-profiler-v1'/],
    ['RUNTIME_MATRIX_PROFILER_BUILD_IMAGE', 'registry.example/profiler:latest', /must use repository@sha256/],
    ['RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT', 'a'.repeat(40), /must equal '5f9a631e/],
    ['RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI', 'https://example.invalid/scaffold', /must equal 'https:\/\/github\.com\/microsoft\/clr-samples/],
    ['RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT', 'b'.repeat(40), /must equal '7ee91972/],
    ['RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI', 'https://example.invalid/headers', /must equal 'https:\/\/github\.com\/dotnet\/runtime/],
    ['RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND', 'none', /must equal 'linux-profiler'/],
  ]) {
    assert.match(
      validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
        ...profiler,
        [inputName]: invalidValue,
      }).join('\n'),
      expectedError,
      inputName,
    )
  }

  const checked = dotnetCandidateEnvironment()
  const mixed = {
    ...profiler,
    RUNTIME_MATRIX_CHECKED_JIT_COMMIT: checked.RUNTIME_MATRIX_CHECKED_JIT_COMMIT,
  }
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', mixed).join('\n'),
    /must be empty because matrix row 'dotnet-10' has no checkedJit lock/,
  )
  assert.throws(
    () => candidateOperationHelpers('runtime-dotnet-matrix-candidate', mixed),
    /cannot be selected together/,
  )

  const legacy = legacyDotnetCandidateEnvironment()
  assert.match(
    validateCandidateBuildInputs('runtime-dotnet-matrix-candidate', {
      ...legacy,
      RUNTIME_MATRIX_PROFILER_PROVIDER_ID: profiler.RUNTIME_MATRIX_PROFILER_PROVIDER_ID,
    }).join('\n'),
    /must be empty because matrix row 'dotnet-core-3\.0' has no profilerProvider lock/,
  )
})

test('modern profiler post-build inspection hashes Runner, JIT Inspector, and native profiler', () => {
  const target = 'runtime-dotnet-matrix-candidate'
  const environment = profilerDotnetCandidateEnvironment()
  const docker = fakeDocker(candidateLabels(target, environment))
  const output = {
    errors: [],
    logs: [],
    log(message) { this.logs.push(message) },
    error(message) { this.errors.push(message) },
  }

  assert.equal(runCandidateBuild([target], environment, docker.spawn, output), 0)
  assert.match(output.logs.join('\n'), /Validated 3 digest-pinned image inputs/)
  const copiedPaths = docker.calls
    .filter(([, arguments_]) => arguments_[0] === 'cp')
    .map(([, arguments_]) => arguments_[1].slice(arguments_[1].indexOf(':') + 1))
    .sort()
  assert.deepEqual(copiedPaths, [
    '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
    '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
    '/opt/sharplabnext/SharpLabNext.Runner.dll',
  ])
})

test('legacy Linux CoreCLR rows retain digest-pinned ABI-compatible base images', () => {
  for (const runtime of runtimeMatrix.coreClr.filter(candidate =>
    candidate.id.startsWith('dotnet-core-'))) {
    assert.equal(
      isDigestPinnedImageReference(runtime.linuxBaseImage),
      true,
      `${runtime.id} must retain a digest-pinned Linux base image`,
    )
  }

  const core20 = runtimeMatrix.coreClr.find(candidate => candidate.id === 'dotnet-core-2.0')
  assert.equal(
    core20.linuxBaseImage,
    'mcr.microsoft.com/dotnet/core/runtime:1.1.13-stretch@sha256:' +
      'e5a5701f73eb4013d1b542c835113b240109cb4017a29032766b639103745314',
    'CoreCLR 2.0 still links libunwind8 and cannot use the 2.1 runtime-deps image',
  )

  const core21 = runtimeMatrix.coreClr.find(candidate => candidate.id === 'dotnet-core-2.1')
  assert.equal(
    core21.linuxBaseImage,
    'mcr.microsoft.com/dotnet/core/runtime:2.1.30-stretch-slim@sha256:' +
      'ea0a74b6a3804708cab2dd72caa6f9d58240dd862d7bccd748018d368ba8efe2',
    'CoreCLR 2.1 still links libcurl.so.4, which its runtime-deps image omits',
  )

  const core22 = runtimeMatrix.coreClr.find(candidate => candidate.id === 'dotnet-core-2.2')
  assert.equal(
    core22.linuxBaseImage,
    'mcr.microsoft.com/dotnet/core/runtime:2.2.8-stretch-slim@sha256:' +
      '8f81aab10b63e73c0797fe728f3d0e9134387c02aa4c1c418eb30639f07964e5',
    'CoreCLR 2.2 still links libcurl.so.4, which the retained runtime-deps image omits',
  )

  const core30 = runtimeMatrix.coreClr.find(candidate => candidate.id === 'dotnet-core-3.0')
  assert.equal(
    core30.linuxBaseImage,
    'mcr.microsoft.com/dotnet/core/runtime:3.0.3-buster-slim@sha256:' +
      '490b4a95e8d3cf651f648f05ebd12a8dfa53f0e0d73aed6159a443f6b2287650',
    'CoreCLR 3.0 still links libcurl.so.4, which the retained runtime-deps image omits',
  )

  const core31 = runtimeMatrix.coreClr.find(candidate => candidate.id === 'dotnet-core-3.1')
  assert.equal(
    core31.linuxBaseImage,
    'mcr.microsoft.com/dotnet/core/runtime:3.1.32-buster-slim@sha256:' +
      '341b5768c787690f502625e510041ffeb17851a6b40acffa8a1c717475e24057',
    'CoreCLR 3.1 retains its exact runtime layer to close the native dependency ABI',
  )
})

test('candidate entry rejects malformed provenance before Docker starts', () => {
  const output = {
    errors: [],
    log() {},
    error(message) { this.errors.push(message) },
  }
  const cases = [
    ['runtime-dotnet-matrix-candidate', dotnetCandidateEnvironment(),
      'RUNTIME_MATRIX_RUNTIME_COMMIT', 'D'.repeat(40), /lowercase hexadecimal commit/],
    ['runtime-dotnet-matrix-candidate', dotnetCandidateEnvironment(),
      'RUNTIME_MATRIX_JIT_COMMIT', 'e'.repeat(41), /lowercase hexadecimal commit/],
    ['runtime-dotnet-matrix-candidate', dotnetCandidateEnvironment(),
      'RUNTIME_MATRIX_RUNTIME_SHA512', 'f'.repeat(127), /SHA-512 digest/],
    ['runtime-dotnet-matrix-candidate', dotnetCandidateEnvironment(),
      'RUNTIME_MATRIX_RUNTIME_URL', 'http://example.invalid/runtime.tar.gz', /absolute HTTPS URI/],
    ['runtime-mono-matrix-candidate', monoCandidateEnvironment(),
      'RUNTIME_MATRIX_RUNTIME_DIGEST', `sha256:${'B'.repeat(64)}`, /sha256:<64 lowercase hex>/],
    ['runtime-mono-matrix-candidate', monoCandidateEnvironment(),
      'RUNTIME_MATRIX_RUNTIME_SOURCE_URI', 'relative/audit.json', /absolute HTTPS URI or immutable docker:/],
    ['runtime-wine-dotnet-matrix-candidate', wineDotnetCandidateEnvironment(),
      'RUNTIME_MATRIX_WINDOWS_SHA512', 'F'.repeat(128), /SHA-512 digest/],
    ['runtime-wine-dotnet-matrix-candidate', wineDotnetCandidateEnvironment(),
      'RUNTIME_MATRIX_WINDOWS_URL', 'https://user:secret@example.invalid/runtime.zip', /without credentials/],
  ]
  for (const [target, baseline, inputName, invalidValue, expectedError] of cases) {
    const environment = { ...baseline, [inputName]: invalidValue }
    let dockerCalls = 0
    const spawn = () => {
      dockerCalls++
      return { status: 0 }
    }
    output.errors.length = 0
    assert.equal(runCandidateBuild([target], environment, spawn, output), 1, inputName)
    assert.equal(dockerCalls, 0, `${inputName} must fail before Docker starts`)
    assert.match(output.errors.join('\n'), expectedError, inputName)
  }

  for (const [target, environment, imageInput] of [
    ['runtime-mono-matrix-candidate', monoCandidateEnvironment(), 'RUNTIME_MATRIX_MONO_IMAGE'],
    ['runtime-mono-wine-matrix-candidate', monoWineCandidateEnvironment(), 'RUNTIME_MATRIX_MONO_WINE_IMAGE'],
    ['runtime-wine-framework-matrix-candidate', wineFrameworkCandidateEnvironment(), 'RUNTIME_MATRIX_WINE_IMAGE'],
  ]) {
    const mismatched = {
      ...environment,
      RUNTIME_MATRIX_RUNTIME_DIGEST: `sha256:${'9'.repeat(64)}`,
    }
    assert.match(
      validateCandidateBuildInputs(target, mismatched).join('\n'),
      new RegExp(`must equal the digest pinned by ${imageInput}`),
    )
  }
})

test('every candidate target verifies its complete built-image identity', () => {
  const output = {
    errors: [],
    log() {},
    error(message) { this.errors.push(message) },
  }
  for (const [target, environment] of candidateEnvironments()) {
    const expectedLabels = candidateLabels(target, environment)

    const missingLabels = { ...expectedLabels }
    delete missingLabels['com.sharplabnext.runtime-candidate']
    const missingCandidate = fakeDocker(missingLabels)
    output.errors.length = 0
    assert.equal(runCandidateBuild([target], environment, missingCandidate.spawn, output), 1, target)
    const isWineCandidate = target.startsWith('runtime-wine-')
    assert.equal(
      missingCandidate.calls.filter(([, arguments_]) => arguments_[0] === 'image').length,
      isWineCandidate ? 3 : 1,
      target,
    )
    assert.match(output.errors.join('\n'), /runtime-candidate.*observed <missing>/, target)

    const bindingEntries = Object.entries(candidateIdentityLabelBindings(target, environment))
    assert.ok(bindingEntries.length > 2, `${target} must bind more than release metadata`)
    const [identityLabel] = bindingEntries.find(([, inputName]) =>
      inputName.startsWith('RUNTIME_MATRIX_')) ?? bindingEntries[0]
    const wrongLabels = { ...expectedLabels, [identityLabel]: 'wrong-identity' }
    const wrong = fakeDocker(wrongLabels)
    output.errors.length = 0
    assert.equal(runCandidateBuild([target], environment, wrong.spawn, output), 1, target)
    assert.match(output.errors.join('\n'), new RegExp(identityLabel.replaceAll('.', '\\.')), target)

    const correct = fakeDocker(expectedLabels)
    output.errors.length = 0
    assert.equal(runCandidateBuild([target], environment, correct.spawn, output), 0, target)
    const bake = correct.calls.find(([, arguments_]) => arguments_[0] === 'buildx')
    assert.ok(bake[1].includes('--load'), `${target} must load before inspection`)
    const imageInspections = correct.calls.filter(([, arguments_]) => arguments_[0] === 'image')
    assert.deepEqual(imageInspections.at(-1)[1], [
      'image',
      'inspect',
      candidateImageTag(target, environment),
    ])
    assert.deepEqual(correct.gitCalls.map(([, arguments_]) => arguments_), [
      ['rev-parse', '--verify', 'HEAD'],
      ['status', '--porcelain=v1', '-z', '--untracked-files=all'],
      ['rev-parse', '--verify', 'HEAD'],
      ['status', '--porcelain=v1', '-z', '--untracked-files=all'],
    ])
    const capturedImageId = `sha256:${'1'.repeat(64)}`
    const helperCreates = correct.calls.filter(([, arguments_]) => arguments_[0] === 'create')
    assert.ok(helperCreates.length > 0, `${target} must inspect trusted helper bytes`)
    assert.equal(
      helperCreates.every(([, arguments_]) => arguments_[1] === capturedImageId),
      true,
      `${target} must use only the captured image ID after inspection`,
    )
  }

  const environment = monoCandidateEnvironment()
  for (const option of ['--print', '--check', '--call=outline']) {
    const nonBuild = fakeDocker({})
    assert.equal(
      runCandidateBuild(['runtime-mono-matrix-candidate', option], environment, nonBuild.spawn, output),
      0,
      option,
    )
    assert.equal(nonBuild.calls.length, 1, `${option} must not inspect an image that was not built`)
    assert.equal(nonBuild.gitCalls.length, 0, `${option} must not inspect source for a non-build call`)
    assert.equal(nonBuild.calls[0][1].includes('--load'), false, option)
  }
})

test('candidate build binds SOURCE_REVISION to Git and dirty override remains non-promotable', () => {
  const target = 'runtime-mono-matrix-candidate'
  const environment = monoCandidateEnvironment()
  const labels = candidateLabels(target, environment)
  assert.equal(candidateExpectedLabels(target)['io.sharplabnext.source.context'], 'committed')
  assert.equal(
    candidateExpectedLabels(target)['com.sharplabnext.runtime-candidate.promotion-eligible'],
    'true',
  )
  // These are internal values: inherited caller input must never decide them.
  environment.RUNTIME_CANDIDATE_SOURCE_CONTEXT = 'working-tree-development'
  environment.RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE = 'true'
  const output = {
    errors: [],
    logs: [],
    log(message) { this.logs.push(message) },
    error(message) { this.errors.push(message) },
  }

  const dirty = fakeDocker(labels)
  const dirtySpawn = (command, arguments_, options) => {
    if (command === 'git' && arguments_[0] === 'status') {
      dirty.gitCalls.push([command, arguments_])
      return { status: 0, stdout: ' M eng/file.mjs\n', stderr: '' }
    }
    return dirty.spawn(command, arguments_, options)
  }
  assert.equal(runCandidateBuild([target], environment, dirtySpawn, output), 1)
  assert.match(output.errors.join('\n'), /worktree is dirty/)
  assert.equal(dirty.calls.length, 0, 'dirty source must fail before Docker starts')

  output.errors.length = 0
  output.logs.length = 0
  const development = fakeDocker(candidateLabels(target, {
    ...environment,
    RUNTIME_CANDIDATE_SOURCE_CONTEXT: 'working-tree-development',
    RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE: 'false',
  }))
  let developmentBakeEnvironment
  const developmentSpawn = (command, arguments_, options) => {
    if (command === 'git' && arguments_[0] === 'status') {
      development.gitCalls.push([command, arguments_])
      return { status: 0, stdout: ' M eng/file.mjs\n', stderr: '' }
    }
    if (command === 'docker' && arguments_[0] === 'buildx') {
      developmentBakeEnvironment = options.env
    }
    return development.spawn(command, arguments_, options)
  }
  assert.equal(runCandidateBuild([
    target,
    '--allow-uncommitted-source-for-development',
  ], environment, developmentSpawn, output), 0)
  assert.match(output.logs.join('\n'), /not eligible for a promotion receipt/)
  assert.match(output.logs.join('\n'), /promotion output remains disabled/)
  assert.equal(developmentBakeEnvironment.RUNTIME_CANDIDATE_SOURCE_CONTEXT, 'working-tree-development')
  assert.equal(developmentBakeEnvironment.RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE, 'false')

  output.errors.length = 0
  const mismatch = fakeDocker(labels)
  const mismatchSpawn = (command, arguments_, options) => {
    if (command === 'git' && arguments_[0] === 'rev-parse') {
      mismatch.gitCalls.push([command, arguments_])
      return { status: 0, stdout: `${'0'.repeat(40)}\n`, stderr: '' }
    }
    return mismatch.spawn(command, arguments_, options)
  }
  assert.equal(runCandidateBuild([
    target,
    '--allow-uncommitted-source-for-development',
  ], environment, mismatchSpawn, output), 1)
  assert.match(output.errors.join('\n'), /does not match Git HEAD/)
  assert.equal(mismatch.calls.length, 0, 'source mismatch must fail before Docker starts')

  output.errors.length = 0
  const postBuildDrift = fakeDocker(labels)
  let statusChecks = 0
  let formalBakeEnvironment
  const postBuildDriftSpawn = (command, arguments_, options) => {
    if (command === 'git' && arguments_[0] === 'status') {
      postBuildDrift.gitCalls.push([command, arguments_])
      statusChecks++
      return {
        status: 0,
        stdout: statusChecks === 1 ? '' : ' M deploy/docker/Dockerfile.runtime-mono-matrix\n',
        stderr: '',
      }
    }
    if (command === 'docker' && arguments_[0] === 'buildx') {
      formalBakeEnvironment = options.env
    }
    return postBuildDrift.spawn(command, arguments_, options)
  }
  assert.equal(runCandidateBuild(
    [target], environment, postBuildDriftSpawn, output,
  ), 1)
  assert.equal(postBuildDrift.calls.length, 1, 'post-build drift must fail before image inspection')
  assert.equal(formalBakeEnvironment.RUNTIME_CANDIDATE_SOURCE_CONTEXT, 'committed')
  assert.equal(formalBakeEnvironment.RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE, 'true')
  assert.match(output.errors.join('\n'), /worktree is dirty/)
})

test('formal rebuild allows only generated dirty paths and keeps them outside Bake inputs', t => {
  const target = 'runtime-mono-matrix-candidate'
  const environment = monoCandidateEnvironment()
  const labels = candidateLabels(target, environment)
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'candidate-formal-root-'))
  const archiveRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'candidate-formal-archive-'))
  t.after(() => fs.rmSync(fixtureRoot, { recursive: true, force: true }))
  t.after(() => fs.rmSync(archiveRoot, { recursive: true, force: true }))
  for (const root of [fixtureRoot, archiveRoot]) {
    fs.mkdirSync(path.join(root, 'profiles', 'runtimes', 'candidates'), { recursive: true })
    fs.copyFileSync(
      path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'),
      path.join(root, 'profiles', 'runtime-matrix.json'),
    )
    fs.copyFileSync(
      path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates',
        `${environment.RUNTIME_MATRIX_PROFILE_ID}.json`),
      path.join(root, 'profiles', 'runtimes', 'candidates',
        `${environment.RUNTIME_MATRIX_PROFILE_ID}.json`),
    )
  }
  const allowedPath =
    `profiles/runtime-promotion-plans/${environment.RUNTIME_MATRIX_PROFILE_ID}.json`
  const docker = fakeDocker(labels)
  let contextOptions
  const spawn = (command, arguments_, options) => {
    if (command === 'git' && arguments_[0] === 'status') {
      docker.gitCalls.push([command, arguments_, options])
      return { status: 0, stdout: ` M ${allowedPath}\0`, stderr: '' }
    }
    return docker.spawn(command, arguments_, options)
  }
  const output = {
    errors: [],
    log() {},
    error(message) { this.errors.push(message) },
  }
  const buildStdio = ['inherit', 2, 2]

  assert.equal(runCandidateBuild([target], environment, spawn, output, {
    repositoryRoot: fixtureRoot,
    allowedDirtyPaths: [allowedPath],
    buildStdio,
    createCommittedSourceContext(options) {
      contextOptions = options
      return { directory: archiveRoot, dispose() {} }
    },
  }), 0)
  assert.equal(output.errors.length, 0)
  assert.equal(contextOptions.repositoryRoot, fixtureRoot)
  assert.deepEqual(
    docker.gitCalls.map(([, arguments_, options]) => [arguments_[0], options.cwd]),
    [
      ['rev-parse', fixtureRoot],
      ['status', fixtureRoot],
      ['rev-parse', fixtureRoot],
      ['status', fixtureRoot],
    ],
  )
  const bake = docker.calls.find(([, arguments_]) => arguments_[0] === 'buildx')
  assert.equal(bake[2].cwd, archiveRoot)
  assert.deepEqual(bake[2].stdio, buildStdio)
  assert.equal(
    bake[1].includes(`${target}.context=${archiveRoot}`),
    true,
  )
  assert.equal(
    Object.keys(bake[2].env).some(name => name.includes('ALLOWED_DIRTY')),
    false,
  )
  for (const [, arguments_, options] of docker.calls.filter(([, arguments_]) =>
    ['image', 'create', 'cp', 'rm'].includes(arguments_[0]))) {
    assert.equal(options.cwd, fixtureRoot)
  }

  const rejected = fakeDocker(labels)
  const rejectedSpawn = (command, arguments_, options) => {
    if (command === 'git' && arguments_[0] === 'status') {
      rejected.gitCalls.push([command, arguments_, options])
      return { status: 0, stdout: ' M eng/build-runtime-candidate.mjs\0', stderr: '' }
    }
    return rejected.spawn(command, arguments_, options)
  }
  output.errors.length = 0
  assert.equal(runCandidateBuild([target], environment, rejectedSpawn, output, {
    repositoryRoot: fixtureRoot,
    allowedDirtyPaths: [allowedPath],
    createCommittedSourceContext() { throw new Error('must fail before archive') },
  }), 1)
  assert.match(output.errors.join('\n'), /worktree is dirty/)
  assert.equal(rejected.calls.length, 0)
})

test('every candidate Dockerfile repeats the shared immutable image contract', () => {

  const candidates = new Map([
    ['Dockerfile.runtime-dotnet-matrix', [
      'SDK_IMAGE',
      'RUNTIME_DEPS_IMAGE',
      'CHECKED_JIT_BUILD_IMAGE',
      'PROFILER_BUILD_IMAGE',
    ]],
    ['Dockerfile.runtime-mono-matrix', ['SDK_IMAGE', 'CONTROL_IMAGE', 'MONO_IMAGE']],
    ['Dockerfile.runtime-mono-wine-matrix', ['SDK_IMAGE', 'CONTROL_IMAGE', 'MONO_WINE_IMAGE']],
    ['Dockerfile.runtime-wine-dotnet-matrix', ['SDK_IMAGE', 'CONTROL_IMAGE', 'WINE_IMAGE']],
    ['Dockerfile.runtime-wine-framework-matrix', ['SDK_IMAGE', 'CONTROL_IMAGE', 'WINE_IMAGE']],
  ])
  for (const [fileName, inputNames] of candidates) {
    const dockerfile = fs.readFileSync(path.join(repositoryRoot, 'deploy', 'docker', fileName), 'utf8')
    assert.match(dockerfile, /COPY deploy\/docker\/validate-digest-pinned-image\.sh/)
    assert.match(dockerfile, /\/usr\/local\/bin\/sharplabnext-validate-image/)
    for (const inputName of inputNames) {
      assert.match(
        dockerfile,
        new RegExp(`${inputName} "\\$\\{${inputName}\\}"`),
        `${fileName} must validate ${inputName}`,
      )
    }
    if (fileName === 'Dockerfile.runtime-mono-matrix' ||
        fileName === 'Dockerfile.runtime-mono-wine-matrix' ||
        fileName === 'Dockerfile.runtime-wine-framework-matrix') {
      assert.match(dockerfile, /grep -Eq '\^sha256:\[0-9a-f\]\{64\}\$'/)
    }
  }
})

test('Wine matrix candidates retain the operator filesystem and probe target runtime helpers', () => {
  const contracts = new Map([
    ['Dockerfile.runtime-wine-framework-matrix', {
      source: 'wine-source',
      controlHost: true,
      probes: [
        'SharpLabNext.TargetRuntimeRunner.exe',
        'self-test',
      ],
    }],
    ['Dockerfile.runtime-wine-dotnet-matrix', {
      source: 'wine-source',
      controlHost: false,
      probes: [
        'Z:\\\\opt\\\\wine-dotnet\\\\drive_c\\\\dotnet\\\\dotnet.exe',
        'od -An -t x1 -j 4 -N 1 /usr/lib/wine/wine64',
        'dpkg --print-foreign-architectures',
        "grep ':i386$'",
      ],
    }],
    ['Dockerfile.runtime-mono-wine-matrix', {
      source: 'runtime-source',
      controlHost: true,
      probes: [
        'target_frames="$(/usr/bin/mono',
        'clr2_frames="$(WINEPREFIX=/opt/wine-netfx-clr2',
        'clr4_frames="$(WINEPREFIX=/opt/wine-netfx-clr4',
      ],
    }],
  ])

  for (const [fileName, contract] of contracts) {
    const dockerfile = fs.readFileSync(path.join(repositoryRoot, 'deploy', 'docker', fileName), 'utf8')
    assert.match(dockerfile, new RegExp(`FROM ${contract.source} AS runtime-base`))
    assert.match(dockerfile, /FROM runtime-base AS preflight/)
    assert.match(dockerfile, /FROM runtime-base AS final/)
    for (const probe of contract.probes)
      assert.ok(dockerfile.includes(probe), `${fileName} must execute ${probe}`)
    assert.doesNotMatch(dockerfile, new RegExp(`COPY --from=${contract.source} \/usr\/ \/usr\/`))
    assert.doesNotMatch(dockerfile, /(?:wine|mono-wine|control)-(?:os|libc)-id/)
    assert.doesNotMatch(dockerfile, /cmp --silent/)
    if (contract.controlHost) {
      assert.match(dockerfile, /COPY --from=control-image \/usr\/share\/dotnet\/ \/usr\/share\/dotnet\//)
      assert.match(dockerfile, /\/usr\/share\/dotnet\/dotnet --info/)
    }
  }
})

test('Linux CoreCLR candidate verifies archive identity and every native library', () => {
  const dockerfile = fs.readFileSync(
    path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.runtime-dotnet-matrix'),
    'utf8',
  )
  const verifier = fs.readFileSync(
    path.join(repositoryRoot, 'deploy', 'docker', 'verify-linux-coreclr-runtime.sh'),
    'utf8',
  )

  assert.match(dockerfile, /actual_commit=.*sed -n '1\{s\/\\r\$\/\/;p;\}'/)
  assert.match(dockerfile, /actual_version=.*sed -n '2\{s\/\\r\$\/\/;p;\}'/)
  assert.match(dockerfile, /actual_commit.*DOTNET_RUNTIME_COMMIT/)
  assert.match(dockerfile, /actual_commit.*DOTNET_JIT_COMMIT/)
  assert.match(dockerfile, /verify-linux-coreclr-runtime\.sh/)
  assert.match(dockerfile, /rm -rf \/usr\/share\/dotnet/)
  assert.match(dockerfile, /rm -f \/usr\/bin\/dotnet/)
  assert.match(dockerfile, /DOTNET_VERSION=""/)
  assert.match(dockerfile, /SharpLabNext\.CheckedJitBridge\.csproj/)
  assert.match(dockerfile, /--framework net5\.0/)
  assert.match(
    dockerfile,
    /https:\/\/github\.com\/dotnet\/runtime\/archive\/\$\{DOTNET_CHECKED_JIT_COMMIT\}\.tar\.gz/,
  )
  assert.match(dockerfile, /DOTNET_CHECKED_JIT_SOURCE_SHA512.*sha512sum --check --strict/s)
  assert.match(dockerfile, /DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_SHA512.*dotnet-bootstrap-sdk\.tar\.gz[\s\S]*sha512sum --check --strict/)
  assert.match(dockerfile, /global\.json[\s\S]*\["tools"\]\["dotnet"\]/)
  assert.match(dockerfile, /test -d "\/bootstrap-dotnet\/sdk\/\$\{DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_VERSION\}"/)
  assert.match(dockerfile, /RUN --network=none set -eu/)
  assert.match(
    dockerfile,
    /RUN --network=none[\s\S]*export DOTNET_INSTALL_DIR=\/bootstrap-dotnet[\s\S]*build-runtime\.sh "\$@"/,
  )
  assert.ok(
    dockerfile.indexOf('/tmp/dotnet-bootstrap-sdk.tar.gz') <
      dockerfile.indexOf('RUN --network=none set -eu'),
    'the locked bootstrap SDK must be downloaded before the networkless native build',
  )
  assert.doesNotMatch(dockerfile, /dotnet-install\.sh/)
  assert.match(dockerfile, /-component "\$\{DOTNET_CHECKED_JIT_BUILD_COMPONENT\}"/)
  assert.match(dockerfile, /command -v gcc/)
  assert.match(dockerfile, /command -v make/)
  assert.match(dockerfile, /-gcc/)
  assert.doesNotMatch(dockerfile, /-ninja/)
  assert.match(
    dockerfile,
    /case "\$\{DOTNET_CHECKED_JIT_VERSION_GENERATION_MODE\}" in[\s\S]*skip-by-upstream-flag\) set -- "\$@" -skipgenerateversion/,
  )
  assert.match(dockerfile, /find "\$\{artifact_directory\}" -maxdepth 1 -type f -name libclrjit\.so/)
  assert.match(
    dockerfile,
    /find \/runtime-source\/artifacts\/bin\/coreclr -type f -name libclrjit\.so[\s\S]*sort -u/,
  )
  assert.match(dockerfile, /Class:\[\[:space:\]\]\+ELF64/)
  assert.match(dockerfile, /Machine:\[\[:space:\]\]\+Advanced Micro Devices X86-64/)
  assert.match(dockerfile, /original_sha256.*!=.*checked_sha256/s)
  assert.match(dockerfile, /install -m 0555 \/checked-jit\/libclrjit\.so "\$\{target\}"/)
  assert.match(dockerfile, /COPY --from=checked-jit \/checked-jit\/libclrjit\.so\.sha256/)
  assert.match(
    dockerfile,
    /SharpLabNext\.CheckedJitBridge\.dll[\s\S]*--verify-runtime-version[\s\S]*"\$\{DOTNET_RUNTIME_VERSION\}"/,
  )
  assert.ok(
    dockerfile.indexOf('USER 1654:1654') < dockerfile.indexOf('--verify-runtime-version'),
    'the Checked-JIT runtime identity probe must run as the sandbox user',
  )
  assert.match(dockerfile, /io\.sharplabnext\.jit\.checked\.bootstrap-sdk\.version=/)
  assert.match(dockerfile, /io\.sharplabnext\.jit\.checked\.bootstrap-sdk\.source-uri=/)
  assert.match(dockerfile, /io\.sharplabnext\.jit\.checked\.bootstrap-sdk\.source-sha512=/)
  assert.match(dockerfile, /io\.sharplabnext\.jit\.checked\.version-generation-mode=/)
  assert.match(dockerfile, /SharpLabNext\.Runtime\.csproj/)
  assert.match(dockerfile, /--framework netstandard2\.1/)
  assert.match(dockerfile, /--framework net10\.0[\s\S]*--output \/runtime-api-modern/)
  assert.match(dockerfile, /2\.\*\) test ! -e \/opt\/sharplabnext\/SharpLab\.Runtime\.dll/)
  assert.match(verifier, /find .*shared_directory.*fxr_directory.*-name '\*\.so'/s)
  assert.match(verifier, /LD_LIBRARY_PATH=.*ldd/)
  assert.match(verifier, /grep -q 'not found'/)
  assert.match(verifier, /libcoreclrtraceptprovider\.so/)
  assert.match(verifier, /missing_sonames.*liblttng-ust\.so\.0/s)
  assert.match(verifier, /actual_commit.*expected_runtime_commit/)
  assert.match(verifier, /actual_commit.*expected_jit_commit/)
})

test('Linux CoreCLR candidate carries the modern profiler through Bake, image labels, and real preflight', () => {
  const bake = fs.readFileSync(
    path.join(repositoryRoot, 'eng', 'bake.runtime-candidates.hcl'),
    'utf8',
  )
  const dockerfile = fs.readFileSync(
    path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.runtime-dotnet-matrix'),
    'utf8',
  )

  for (const inputName of [
    'RUNTIME_MATRIX_PROFILER_PROVIDER_ID',
    'RUNTIME_MATRIX_PROFILER_BUILD_IMAGE',
    'RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT',
    'RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI',
    'RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT',
    'RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI',
    'RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND',
  ]) {
    assert.match(bake, new RegExp(`variable "${inputName}"`), inputName)
  }
  for (const label of [
    'io.sharplabnext.jit.profiler.provider',
    'io.sharplabnext.jit.profiler.builder-image',
    'io.sharplabnext.component.jit-profiler-clr-samples.commit',
    'io.sharplabnext.component.jit-profiler-clr-samples.source-uri',
    'io.sharplabnext.component.jit-profiler-runtime-headers.commit',
    'io.sharplabnext.component.jit-profiler-runtime-headers.source-uri',
    'io.sharplabnext.jit.profiler.source-mapping-kind',
  ]) {
    assert.match(bake, new RegExp(label.replaceAll('.', '\\.')))
    assert.match(dockerfile, new RegExp(label.replaceAll('.', '\\.')))
  }

  assert.match(dockerfile, /FROM \$\{PROFILER_BUILD_IMAGE\} AS jit-profiler/)
  assert.match(dockerfile, /PROFILER_BUILD_IMAGE "\$\{PROFILER_BUILD_IMAGE\}"/)
  assert.match(dockerfile, /SharpLabNext\.Runner\.csproj/)
  assert.match(dockerfile, /SharpLabNext\.JitInspector\.csproj/)
  assert.match(dockerfile, /--framework net10\.0/)
  assert.match(dockerfile, /profiler\/build\.sh \/jit-profiler\/SharpLabNext\.JitProfiler\.so/)
  assert.match(dockerfile, /source=\/modern-run,target=\/mnt\/modern-run,ro/)
  assert.match(dockerfile, /source=\/modern-jit,target=\/mnt\/modern-jit,ro/)
  assert.match(dockerfile, /source=\/runtime-api,target=\/mnt\/runtime-api,ro/)
  assert.match(dockerfile, /source=\/runtime-api-modern,target=\/mnt\/runtime-api-modern,ro/)
  assert.match(
    dockerfile,
    /sha256sum \/mnt\/runtime-api-modern\/SharpLab\.Runtime\.dll.*sha256sum \/opt\/sharplabnext\/SharpLab\.Runtime\.dll/s,
  )
  assert.match(dockerfile, /sha256sum "\$\{source\}".*sha256sum "\$\{target\}"/s)
  assert.match(dockerfile, /jit-profiler-preflight\.sh[\s\\]+\/opt\/sharplabnext\/profiler-smoke/)
  assert.match(dockerfile, /test -z "\$\{DOTNET_CHECKED_JIT_SOURCE_URL\}"/)
})

test('Linux CoreCLR verifier allows only the disabled tracepoint provider LTTng gap', {
  skip: shell === undefined,
}, () => {
  const allowed = runLinuxRuntimeVerifier('allowed')
  assert.equal(
    allowed.status,
    0,
    `stdout:\n${allowed.stdout}\nstderr:\n${allowed.stderr}\nerror:\n${allowed.error ?? ''}`,
  )

  const extra = runLinuxRuntimeVerifier('extra')
  assert.equal(extra.status, 1)
  assert.match(extra.stderr, /Unresolved native dependencies/)

  const wrongFile = runLinuxRuntimeVerifier('wrong-file')
  assert.equal(wrongFile.status, 1)
  assert.match(wrongFile.stderr, /Unresolved native dependencies/)
})

test('post-build identity bindings name labels that exist in the reviewed Bake or Dockerfile', () => {
  const productionBake = fs.readFileSync(path.join(repositoryRoot, 'eng', 'bake.hcl'), 'utf8')
  const bake = fs.readFileSync(
    path.join(repositoryRoot, 'eng', 'bake.runtime-candidates.hcl'),
    'utf8',
  )
  const dockerfiles = {
    'runtime-dotnet-matrix-candidate': 'Dockerfile.runtime-dotnet-matrix',
    'runtime-mono-matrix-candidate': 'Dockerfile.runtime-mono-matrix',
    'runtime-mono-wine-matrix-candidate': 'Dockerfile.runtime-mono-wine-matrix',
    'runtime-wine-dotnet-matrix-candidate': 'Dockerfile.runtime-wine-dotnet-matrix',
    'runtime-wine-framework-matrix-candidate': 'Dockerfile.runtime-wine-framework-matrix',
    'runtime-wine-framework-matrix-shared-candidate': 'Dockerfile.runtime-wine-framework-matrix-shared',
  }
  for (const [target, environment] of candidateEnvironments()) {
    const dockerfile = fs.readFileSync(
      path.join(repositoryRoot, 'deploy', 'docker', dockerfiles[target]),
      'utf8',
    )
    const specification = candidateTargetSpecifications[target]
    const selectedImageBindings = Object.fromEntries(
      Object.entries(candidateImageLabelBindings)
        .filter(([, inputName]) => specification.imageInputs.includes(inputName)),
    )
    const labels = [
      ...Object.keys(selectedImageBindings),
      ...Object.keys(candidateIdentityLabelBindings(target, environment)),
      ...Object.keys(candidateExpectedLabels(target, environment)),
    ]
    for (const label of labels) {
      const reviewedLabel = environment.RUNTIME_MATRIX_PROFILE_ID === undefined
        ? label
        : label.replace(
            `io.sharplabnext.component.${environment.RUNTIME_MATRIX_PROFILE_ID}.`,
            'io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.',
          )
      assert.ok(
        productionBake.includes(reviewedLabel) || bake.includes(reviewedLabel) ||
          dockerfile.includes(reviewedLabel),
        `${target} identity check refers to undeclared label ${reviewedLabel}`,
      )
    }
  }
})

test('candidate labels must retain the exact image references used at build time', () => {
  const values = {
    CONTROL_IMAGE: valid,
    WINE_IMAGE: `registry.example/wine@sha256:${'b'.repeat(64)}`,
  }
  const bindings = {
    'io.sharplabnext.control-image': 'CONTROL_IMAGE',
    'io.sharplabnext.operator-image.wine': 'WINE_IMAGE',
  }

  assert.deepEqual(validateCandidateImageLabels({
    'io.sharplabnext.control-image': values.CONTROL_IMAGE,
    'io.sharplabnext.operator-image.wine': values.WINE_IMAGE,
  }, values, bindings), [])

  assert.deepEqual(validateCandidateImageLabels({
    'io.sharplabnext.control-image': values.CONTROL_IMAGE,
  }, values, bindings), [
    `io.sharplabnext.operator-image.wine must equal WINE_IMAGE (${values.WINE_IMAGE}); observed <missing>`,
  ])

  assert.deepEqual(validateCandidateExpectedLabels({
    'com.sharplabnext.runtime-candidate': 'false',
  }, {
    'com.sharplabnext.runtime-candidate': 'true',
  }), [
    "com.sharplabnext.runtime-candidate must equal 'true'; observed 'false'",
  ])
})

test('Wine candidates reject incomplete, development, and drifting userspace operators', () => {
  const target = 'runtime-wine-dotnet-matrix-candidate'
  const environment = wineDotnetCandidateEnvironment()
  const output = {
    errors: [],
    logs: [],
    log(message) { this.logs.push(message) },
    error(message) { this.errors.push(message) },
  }

  const missingComponent = fakeDocker(candidateLabels(target, environment), {
    wineOperatorLabels(labels) {
      const result = { ...labels }
      delete result['io.sharplabnext.component.wine-coreclr-userspace.digest']
      return result
    },
  })
  assert.equal(runCandidateBuild([target], environment, missingComponent.spawn, output), 1)
  assert.equal(
    missingComponent.calls.some(([, arguments_]) => arguments_[0] === 'buildx'),
    false,
  )
  assert.match(output.errors.join('\n'), /wine-coreclr-userspace\.digest/)

  output.errors.length = 0
  const developmentOperator = fakeDocker(candidateLabels(target, environment), {
    wineOperatorLabels: { 'io.sharplabnext.development-only': 'true' },
  })
  assert.equal(runCandidateBuild([target], environment, developmentOperator.spawn, output), 1)
  assert.equal(
    developmentOperator.calls.some(([, arguments_]) => arguments_[0] === 'buildx'),
    false,
  )
  assert.match(output.errors.join('\n'), /development-only/)

  output.errors.length = 0
  const privateOperator = fakeDocker(candidateLabels(target, environment), {
    wineOperatorLabels: {
      'io.sharplabnext.operator.wine-source':
        `docker://registry.example/private-wine@sha256:${'a'.repeat(64)}`,
    },
  })
  assert.equal(runCandidateBuild([target], environment, privateOperator.spawn, output), 1)
  assert.equal(
    privateOperator.calls.some(([, arguments_]) => arguments_[0] === 'buildx'),
    false,
  )
  assert.match(output.errors.join('\n'), /private Wine source lineage/)

  output.errors.length = 0
  const snapshotDrift = fakeDocker(candidateLabels(target, environment), {
    wineOperatorImageId(count) {
      return `sha256:${(count === 1 ? '1' : '2').repeat(64)}`
    },
  })
  assert.equal(runCandidateBuild([target], environment, snapshotDrift.spawn, output), 1)
  assert.match(output.errors.join('\n'), /operator image changed during Bake/)
})

test('Wine CoreCLR development build binds the captured local operator without receipt evidence', () => {
  const target = 'runtime-wine-dotnet-matrix-candidate'
  const environment = {
    ...wineDotnetCandidateEnvironment(),
    RUNTIME_MATRIX_WINE_IMAGE: developmentWineOperatorImageId,
    RUNTIME_CANDIDATE_SOURCE_CONTEXT: 'working-tree-development',
    RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE: 'false',
    WINE_CORECLR_DEVELOPMENT_WRAPPER_OPT_IN: 'true',
    WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG: developmentWineOperatorTag,
    WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE_ID: developmentWineOperatorImageId,
  }
  const labels = candidateLabels(target, environment)
  const output = {
    errors: [], logs: [],
    log(message) { this.logs.push(message) },
    error(message) { this.errors.push(message) },
  }

  function run(labelsOverride = labels) {
    const docker = fakeDocker(labelsOverride, {
      wineOperatorReference: developmentWineOperatorTag,
      wineOperatorLabels: {
        'io.sharplabnext.source.context': 'working-tree-development',
        'io.sharplabnext.development-only': 'true',
        'com.sharplabnext.operator.promotion-eligible': 'false',
      },
    })
    let bakeEnvironment
    const spawn = (command, arguments_, options) => {
      if (command === 'git' && arguments_[0] === 'status') {
        return { status: 0, stdout: ' M eng/file.mjs\n', stderr: '' }
      }
      if (command === 'docker' && arguments_[0] === 'buildx') bakeEnvironment = options.env
      return docker.spawn(command, arguments_, options)
    }
    const status = runCandidateBuild([
      target,
      '--allow-uncommitted-source-for-development',
    ], environment, spawn, output, {
      loadWineCoreClrOperatorReceipt() { throw new Error('development build must not load a receipt') },
    })
    return { status, docker, bakeEnvironment }
  }

  const accepted = run()
  assert.equal(accepted.status, 0, output.errors.join('\n'))
  assert.equal(accepted.bakeEnvironment.RUNTIME_MATRIX_WINE_IMAGE, developmentWineOperatorImageId)
  assert.equal(accepted.bakeEnvironment.WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG, developmentWineOperatorTag)
  assert.equal(accepted.bakeEnvironment.WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE, 'true')
  assert.equal(accepted.bakeEnvironment.WINE_CORECLR_OPERATOR_RECEIPT_SHA256, undefined)
  assert.match(output.logs.join('\n'), /promotion output remains disabled/)

  output.errors.length = 0
  const withReceiptLabel = run({
    ...labels,
    'io.sharplabnext.operator.receipt-sha256': fakeOperatorReceiptSha256,
  })
  assert.equal(withReceiptLabel.status, 1)
  assert.match(output.errors.join('\n'), /operator\.receipt-sha256 must be absent/)
})

test('formal Wine CoreCLR build still requires an explicit signed operator receipt', () => {
  const target = 'runtime-wine-dotnet-matrix-candidate'
  const environment = wineDotnetCandidateEnvironment()
  const docker = fakeDocker(candidateLabels(target, environment))
  const output = {
    errors: [], logs: [],
    log(message) { this.logs.push(message) },
    error(message) { this.errors.push(message) },
  }
  assert.equal(runCandidateBuildProduction([target], environment, docker.spawn, output, {
    createCommittedSourceContext: () => ({ directory: repositoryRoot, dispose() {} }),
  }), 1)
  assert.match(output.errors.join('\n'), /required for formal Wine candidate builds/)
  assert.equal(docker.calls.some(([, arguments_]) => arguments_[0] === 'buildx'), false)
})

test('combined identity validation reports input and label mismatches together', () => {
  const values = { IMAGE: 'registry.example/runtime:latest' }
  const bindings = { 'io.sharplabnext.operator-image.wine': 'IMAGE' }
  assert.deepEqual(validateCandidateImageIdentity(values, {}, ['IMAGE'], bindings), [
    "IMAGE must use repository@sha256:<64 lowercase hex>; received 'registry.example/runtime:latest'",
    `io.sharplabnext.operator-image.wine must equal IMAGE (${values.IMAGE}); observed <missing>`,
  ])
})

test('generated runtime profiles use the generic matrix candidate image convention', () => {
  const profileDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')
  for (const fileName of fs.readdirSync(profileDirectory).filter(name => name.endsWith('.json'))) {
    const profile = JSON.parse(fs.readFileSync(path.join(profileDirectory, fileName), 'utf8'))
    const expected = `sharplabnext/runtime-${profile.id}:candidate`
    assert.equal(profile.image, expected, `${fileName} image must match the generic Bake target`)
    assert.equal(profile.runtimeImageId, expected, `${fileName} runtimeImageId must match its image`)
  }
})

test('Mono candidate final stage retains and consumes its operator identity', () => {
  const dockerfile = fs.readFileSync(
    path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.runtime-mono-matrix'),
    'utf8',
  )
  const bakeValidator = fs.readFileSync(
    path.join(repositoryRoot, 'eng', 'validate-bake-inputs.mjs'),
    'utf8',
  )
  const finalStageIndex = dockerfile.indexOf('FROM mono-runtime-check AS final')
  assert.notEqual(finalStageIndex, -1, 'Mono candidate Dockerfile must have a final stage')
  const finalStage = dockerfile.slice(finalStageIndex)
  assert.match(finalStage, /^ARG MONO_IMAGE$/m)
  assert.match(finalStage, /^ARG RUNTIME_COMPONENT_DIGEST$/m)
  assert.match(finalStage, /^ARG RUNTIME_COMPONENT_SOURCE_URI$/m)
  assert.match(finalStage, /^COPY --from=control-image \/usr\/share\/dotnet\/ \/usr\/share\/dotnet\/$/m)
  assert.match(finalStage, /SharpLabNext\.TargetRuntimeRunner\.exe self-test/)
  assert.match(finalStage, /io\.sharplabnext\.operator-image\.mono="\$\{MONO_IMAGE\}"/)
  assert.match(finalStage, /io\.sharplabnext\.runtime\.component-digest="\$\{RUNTIME_COMPONENT_DIGEST\}"/)
  assert.match(finalStage, /io\.sharplabnext\.runtime\.component-source-uri="\$\{RUNTIME_COMPONENT_SOURCE_URI\}"/)

  const normalizationChecks = [
    'command -v stat >/dev/null',
    'command -v cp >/dev/null',
    'command -v cmp >/dev/null',
    'mono_source=/usr/bin/mono-sgen',
    'mono_destination=/usr/bin/mono',
    'test -f "${mono_source}"',
    'test ! -L "${mono_source}"',
    'test -L "${mono_destination}"',
    'cp --preserve=mode,ownership,timestamps -- "${mono_source}" "${mono_destination}"',
    'test -f "${mono_destination}"',
    'test ! -L "${mono_destination}"',
    'test "$(stat --format=%a "${mono_destination}")" = "${mono_mode}"',
    'test "$(stat --format=%u:%g "${mono_destination}")" = "${mono_owner}"',
    'test "$(stat --format=%y "${mono_destination}")" = "${mono_timestamp}"',
    'test "$(stat --format=%h "${mono_destination}")" -eq 1',
    'cmp --silent "${mono_source}" "${mono_destination}"',
  ]
  for (const check of normalizationChecks) {
    assert.ok(finalStage.includes(check), `Mono final stage must fail closed with '${check}'`)
    assert.ok(bakeValidator.includes(`'${check}'`), `Bake validation must require '${check}'`)
  }
  let previous = -1
  for (const check of normalizationChecks) {
    const current = finalStage.indexOf(check)
    assert.ok(current > previous, `Mono normalization step '${check}' must remain in fail-closed order`)
    previous = current
  }
})
