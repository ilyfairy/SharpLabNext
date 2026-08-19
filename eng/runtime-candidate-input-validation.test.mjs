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
} from './runtime-candidate-input-validation.mjs'
import {
  candidateExpectedLabels,
  candidateIdentityLabelBindings,
  candidateImageTag,
  candidateOperationHelpers,
  candidateTargetSpecifications,
  createCandidateBakeArguments,
  runCandidateBuild,
  validateCandidateBuildInputs,
} from './build-runtime-candidate.mjs'
import { findDockerfileStageArgumentScopeViolations } from './dockerfile-stage-arguments.mjs'
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
    WINE_CONTROL_TFM: 'net10.0',
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
    ...Object.fromEntries(
      Object.entries(bindings).map(([label, inputName]) => [label, environment[inputName]]),
    ),
  }
}

function fakeDocker(labels) {
  const calls = []
  const gitCalls = []
  return {
    calls,
    gitCalls,
    spawn(command, arguments_) {
      if (command === 'git') {
        gitCalls.push([command, arguments_])
        return arguments_[0] === 'rev-parse'
          ? { status: 0, stdout: `${'f'.repeat(40)}\n`, stderr: '' }
          : { status: 0, stdout: '', stderr: '' }
      }
      calls.push([command, arguments_])
      if (arguments_[0] === 'buildx') return { status: 0 }
      if (arguments_[0] === 'image') {
        return {
          status: 0,
          stdout: JSON.stringify([{
            Id: `sha256:${'1'.repeat(64)}`,
            Size: 536870912,
            Os: 'linux',
            Architecture: 'amd64',
            RepoDigests: [],
            Config: { Labels: labels },
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
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ],
    'runtime-wine-framework-matrix-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ],
    'runtime-wine-framework-matrix-shared-candidate': [
      'BASE_DOTNET_SDK_IMAGE',
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
    assert.equal(missingCandidate.calls.length, 2, target)
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
    assert.ok(correct.calls[0][1].includes('--load'), `${target} must load before inspection`)
    assert.deepEqual(correct.calls[1][1], [
      'image',
      'inspect',
      candidateImageTag(target, environment),
    ])
    assert.deepEqual(correct.gitCalls.map(([, arguments_]) => arguments_), [
      ['rev-parse', '--verify', 'HEAD'],
      ['status', '--porcelain=v1', '-z', '--untracked-files=normal'],
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
  const development = fakeDocker(labels)
  const developmentSpawn = (command, arguments_, options) => {
    if (command === 'git' && arguments_[0] === 'status') {
      development.gitCalls.push([command, arguments_])
      return { status: 0, stdout: ' M eng/file.mjs\n', stderr: '' }
    }
    return development.spawn(command, arguments_, options)
  }
  assert.equal(runCandidateBuild([
    target,
    '--allow-uncommitted-source-for-development',
  ], environment, developmentSpawn, output), 0)
  assert.match(output.logs.join('\n'), /not eligible for a promotion receipt/)
  assert.match(output.logs.join('\n'), /promotion output remains disabled/)

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
      probes: ['Z:\\\\opt\\\\wine-dotnet\\\\drive_c\\\\dotnet\\\\dotnet.exe'],
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
      ...Object.keys(candidateExpectedLabels(target)),
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
})
