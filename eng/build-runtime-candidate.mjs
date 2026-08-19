/**
 * Mandatory entry point for candidate runtime images.
 *
 * The production Bake graph deliberately has no RUNTIME_MATRIX_* variables.
 * This command validates the selected candidate's operator inputs before
 * Buildx is allowed to load a Dockerfile or resolve an external FROM image.
 */

import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
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
  bindRuntimeCandidateImage,
  hashRuntimeOperationHelpers,
  inspectDockerImage,
  inspectGitSourceState,
  validateGitSourceState,
} from './runtime-promotion-image-binding.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

const commonRequiredInputs = Object.freeze([
  'IMAGE_PREFIX',
  'RELEASE_ID',
  'SOURCE_DATE_EPOCH',
  'SOURCE_REVISION',
])

const commonIdentityLabelBindings = Object.freeze({
  'org.opencontainers.image.version': 'RELEASE_ID',
  'org.opencontainers.image.revision': 'SOURCE_REVISION',
  'io.sharplabnext.source.revision': 'SOURCE_REVISION',
})

const commonExpectedLabels = Object.freeze({
  'com.sharplabnext.runtime-candidate': 'true',
})

const developmentSourceOverride = '--allow-uncommitted-source-for-development'

const checkedJitInputs = Object.freeze({
  commit: 'RUNTIME_MATRIX_CHECKED_JIT_COMMIT',
  sourceUrl: 'RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL',
  sourceSha512: 'RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512',
  bootstrapSdkVersion: 'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION',
  bootstrapSdkUrl: 'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL',
  bootstrapSdkSha512: 'RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512',
  builderImage: 'RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE',
  configuration: 'RUNTIME_MATRIX_CHECKED_JIT_CONFIGURATION',
  targetOs: 'RUNTIME_MATRIX_CHECKED_JIT_TARGET_OS',
  architecture: 'RUNTIME_MATRIX_CHECKED_JIT_ARCHITECTURE',
  buildComponent: 'RUNTIME_MATRIX_CHECKED_JIT_BUILD_COMPONENT',
  pgoMode: 'RUNTIME_MATRIX_CHECKED_JIT_PGO_MODE',
  compiler: 'RUNTIME_MATRIX_CHECKED_JIT_COMPILER',
  generator: 'RUNTIME_MATRIX_CHECKED_JIT_GENERATOR',
  versionGenerationMode: 'RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE',
  sourceMappingKind: 'RUNTIME_MATRIX_CHECKED_JIT_SOURCE_MAPPING_KIND',
})

const checkedJitIdentityLabelBindings = Object.freeze({
  'io.sharplabnext.jit.checked.commit': checkedJitInputs.commit,
  'io.sharplabnext.jit.checked.source-uri': checkedJitInputs.sourceUrl,
  'io.sharplabnext.jit.checked.source-sha512': checkedJitInputs.sourceSha512,
  'io.sharplabnext.jit.checked.builder-image': checkedJitInputs.builderImage,
  'io.sharplabnext.jit.checked.configuration': checkedJitInputs.configuration,
  'io.sharplabnext.jit.checked.target-os': checkedJitInputs.targetOs,
  'io.sharplabnext.jit.checked.architecture': checkedJitInputs.architecture,
  'io.sharplabnext.jit.checked.build-component': checkedJitInputs.buildComponent,
  'io.sharplabnext.jit.checked.pgo-mode': checkedJitInputs.pgoMode,
  'io.sharplabnext.jit.checked.compiler': checkedJitInputs.compiler,
  'io.sharplabnext.jit.checked.generator': checkedJitInputs.generator,
  'io.sharplabnext.jit.checked.version-generation-mode': checkedJitInputs.versionGenerationMode,
  'io.sharplabnext.jit.checked.source-mapping-kind': checkedJitInputs.sourceMappingKind,
})

const checkedJitBootstrapIdentityLabelBindings = Object.freeze({
  'io.sharplabnext.jit.checked.bootstrap-sdk.version': checkedJitInputs.bootstrapSdkVersion,
  'io.sharplabnext.jit.checked.bootstrap-sdk.source-uri': checkedJitInputs.bootstrapSdkUrl,
  'io.sharplabnext.jit.checked.bootstrap-sdk.source-sha512': checkedJitInputs.bootstrapSdkSha512,
})

const profilerProviderInputs = Object.freeze({
  id: 'RUNTIME_MATRIX_PROFILER_PROVIDER_ID',
  builderImage: 'RUNTIME_MATRIX_PROFILER_BUILD_IMAGE',
  scaffoldCommit: 'RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT',
  scaffoldSourceUri: 'RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI',
  runtimeHeadersCommit: 'RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT',
  runtimeHeadersSourceUri: 'RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI',
  sourceMappingKind: 'RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND',
})

const profilerProviderIdentityLabelBindings = Object.freeze({
  'io.sharplabnext.jit.profiler.provider': profilerProviderInputs.id,
  'io.sharplabnext.jit.profiler.builder-image': profilerProviderInputs.builderImage,
  'io.sharplabnext.component.jit-profiler-clr-samples.commit': profilerProviderInputs.scaffoldCommit,
  'io.sharplabnext.component.jit-profiler-clr-samples.source-uri': profilerProviderInputs.scaffoldSourceUri,
  'io.sharplabnext.component.jit-profiler-runtime-headers.commit': profilerProviderInputs.runtimeHeadersCommit,
  'io.sharplabnext.component.jit-profiler-runtime-headers.source-uri': profilerProviderInputs.runtimeHeadersSourceUri,
  'io.sharplabnext.jit.profiler.source-mapping-kind': profilerProviderInputs.sourceMappingKind,
})

const candidateHelperOperations = Object.freeze({
  'runtime-dotnet-matrix-candidate': Object.freeze({
    run: Object.freeze({
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    }),
    jit: Object.freeze({
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    }),
  }),
  'runtime-mono-matrix-candidate': Object.freeze({
    run: Object.freeze({
      implementation: 'sharplabnext-target-runtime-runner-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
    }),
    jit: Object.freeze({
      implementation: 'sharplabnext-mono-jit-inspector-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll',
    }),
  }),
  'runtime-mono-wine-matrix-candidate': Object.freeze({
    run: Object.freeze({
      implementation: 'sharplabnext-target-runtime-runner-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
    }),
    jit: Object.freeze({
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    }),
  }),
  'runtime-wine-dotnet-matrix-candidate': Object.freeze({
    run: Object.freeze({
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    }),
    jit: Object.freeze({
      implementation: 'sharplabnext-legacy-jit-inspector-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll',
    }),
  }),
  'runtime-wine-framework-matrix-candidate': Object.freeze({
    run: Object.freeze({
      implementation: 'sharplabnext-target-runtime-runner-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
    }),
  }),
  'runtime-wine-framework-matrix-shared-candidate': Object.freeze({
    run: Object.freeze({
      implementation: 'sharplabnext-target-runtime-runner-v1',
      assemblyPath: '/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe',
    }),
  }),
})

const checkedJitHelperOperations = Object.freeze({
  run: candidateHelperOperations['runtime-dotnet-matrix-candidate'].run,
  jit: Object.freeze({
    implementation: 'sharplabnext-checked-jit-bridge-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll',
  }),
})

const profilerHelperOperations = Object.freeze({
  run: Object.freeze({
    implementation: 'sharplabnext-runner-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.Runner.dll',
  }),
  jit: Object.freeze({
    implementation: 'sharplabnext-jit-inspector-v1',
    assemblyPath: '/opt/sharplabnext/SharpLabNext.JitInspector.dll',
    profilerPath: '/opt/sharplabnext/SharpLabNext.JitProfiler.so',
  }),
})

const inputFormatValidators = Object.freeze({
  commit: Object.freeze({
    accepts: isGitCommitIdentity,
    description: 'a 40- or 64-character lowercase hexadecimal commit',
  }),
  sha256: Object.freeze({
    accepts: isSha256Digest,
    description: 'sha256:<64 lowercase hex>',
  }),
  sha512: Object.freeze({
    accepts: isSha512HexDigest,
    description: 'a 128-character lowercase hexadecimal SHA-512 digest',
  }),
  image: Object.freeze({
    accepts: value => isDigestPinnedImageReference(value),
    description: 'a repository@sha256:<64 lowercase hex> image reference',
  }),
  httpsUri: Object.freeze({
    accepts: isHttpsUri,
    description: 'an absolute HTTPS URI without credentials',
  }),
  dotNetSdkVersion: Object.freeze({
    accepts: isDotNetSdkVersion,
    description: 'a canonical .NET SDK version',
  }),
  sourceUri: Object.freeze({
    accepts: isCandidateSourceUri,
    description: 'an absolute HTTPS URI or immutable docker://repository@sha256:<64 lowercase hex>',
  }),
})

export const candidateTargetSpecifications = Object.freeze({
  'runtime-dotnet-matrix-candidate': Object.freeze({
    matrixBindingKind: 'linux-coreclr',
    imageInputs: Object.freeze([
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_BASE_IMAGE',
    ]),
    requiredInputs: Object.freeze([
      'RUNTIME_MATRIX_PROFILE_ID',
      'RUNTIME_MATRIX_RUNTIME_VERSION',
      'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'RUNTIME_MATRIX_JIT_COMMIT',
      'RUNTIME_MATRIX_RUNTIME_URL',
      'RUNTIME_MATRIX_RUNTIME_SHA512',
      'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    ]),
    formattedInputs: Object.freeze({
      RUNTIME_MATRIX_RUNTIME_COMMIT: 'commit',
      RUNTIME_MATRIX_JIT_COMMIT: 'commit',
      RUNTIME_MATRIX_RUNTIME_URL: 'httpsUri',
      RUNTIME_MATRIX_RUNTIME_SHA512: 'sha512',
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: 'sourceUri',
      RUNTIME_MATRIX_CHECKED_JIT_COMMIT: 'commit',
      RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL: 'httpsUri',
      RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512: 'sha512',
      RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION: 'dotNetSdkVersion',
      RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL: 'httpsUri',
      RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512: 'sha512',
      RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT: 'commit',
      RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI: 'httpsUri',
      RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT: 'commit',
      RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI: 'httpsUri',
    }),
    identityLabelBindings: Object.freeze({
      'com.sharplabnext.runtime-profile': 'RUNTIME_MATRIX_PROFILE_ID',
      'com.sharplabnext.runtime-version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.component.runtime-matrix.profile-id': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.component.runtime-matrix.version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.component.runtime-matrix.commit': 'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'io.sharplabnext.component.runtime-matrix.source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'io.sharplabnext.runtime.commit': 'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'io.sharplabnext.jit.commit': 'RUNTIME_MATRIX_JIT_COMMIT',
      'io.sharplabnext.runtime.payload-sha512': 'RUNTIME_MATRIX_RUNTIME_SHA512',
    }),
    profileComponentFields: Object.freeze({
      version: 'RUNTIME_MATRIX_RUNTIME_VERSION',
      commit: 'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
  }),
  'runtime-mono-matrix-candidate': Object.freeze({
    matrixBindingKind: 'mono',
    imageInputs: Object.freeze([
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_MONO_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ]),
    requiredInputs: Object.freeze([
      'RUNTIME_MATRIX_PROFILE_ID',
      'RUNTIME_MATRIX_RUNTIME_VERSION',
      'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'WINE_CONTROL_TFM',
    ]),
    formattedInputs: Object.freeze({
      RUNTIME_MATRIX_RUNTIME_DIGEST: 'sha256',
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: 'sourceUri',
    }),
    runtimeDigestImageInput: 'RUNTIME_MATRIX_MONO_IMAGE',
    identityLabelBindings: Object.freeze({
      'com.sharplabnext.runtime-profile': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.runtime.version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.runtime.component-digest': 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'io.sharplabnext.runtime.component-source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'io.sharplabnext.component.runtime-matrix.profile-id': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.component.runtime-matrix.version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.component.runtime-matrix.digest': 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'io.sharplabnext.component.runtime-matrix.source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
    expectedLabels: Object.freeze({
      'io.sharplabnext.runtime.environment': 'mono',
    }),
    profileComponentFields: Object.freeze({
      version: 'RUNTIME_MATRIX_RUNTIME_VERSION',
      digest: 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
  }),
  'runtime-mono-wine-matrix-candidate': Object.freeze({
    matrixBindingKind: 'combined-mono-wine',
    imageInputs: Object.freeze([
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_MONO_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ]),
    requiredInputs: Object.freeze([
      'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'WINE_CONTROL_TFM',
    ]),
    formattedInputs: Object.freeze({
      RUNTIME_MATRIX_RUNTIME_DIGEST: 'sha256',
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: 'sourceUri',
    }),
    runtimeDigestImageInput: 'RUNTIME_MATRIX_MONO_WINE_IMAGE',
    identityLabelBindings: Object.freeze({
      'io.sharplabnext.runtime.matrix.digest': 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'io.sharplabnext.runtime.matrix.source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
    expectedLabels: Object.freeze({
      'io.sharplabnext.runtime.matrix.profile-group': 'mono-wine-matrix',
    }),
  }),
  'runtime-wine-dotnet-matrix-candidate': Object.freeze({
    matrixBindingKind: 'wine-coreclr',
    imageInputs: Object.freeze([
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ]),
    requiredInputs: Object.freeze([
      'RUNTIME_MATRIX_PROFILE_ID',
      'RUNTIME_MATRIX_RUNTIME_VERSION',
      'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'RUNTIME_MATRIX_JIT_COMMIT',
      'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'RUNTIME_MATRIX_WINDOWS_URL',
      'RUNTIME_MATRIX_WINDOWS_SHA512',
      'WINE_CONTROL_TFM',
    ]),
    formattedInputs: Object.freeze({
      RUNTIME_MATRIX_RUNTIME_COMMIT: 'commit',
      RUNTIME_MATRIX_JIT_COMMIT: 'commit',
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: 'sourceUri',
      RUNTIME_MATRIX_WINDOWS_URL: 'httpsUri',
      RUNTIME_MATRIX_WINDOWS_SHA512: 'sha512',
    }),
    identityLabelBindings: Object.freeze({
      'com.sharplabnext.runtime-profile': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.runtime.version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.component.runtime-matrix.profile-id': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.component.runtime-matrix.version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.component.runtime-matrix.commit': 'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'io.sharplabnext.component.runtime-matrix.jit-commit': 'RUNTIME_MATRIX_JIT_COMMIT',
      'io.sharplabnext.component.runtime-matrix.source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'io.sharplabnext.runtime.commit': 'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'io.sharplabnext.jit.commit': 'RUNTIME_MATRIX_JIT_COMMIT',
      'io.sharplabnext.runtime.payload-sha512': 'RUNTIME_MATRIX_WINDOWS_SHA512',
    }),
    expectedLabels: Object.freeze({
      'io.sharplabnext.runtime.environment': 'wine-coreclr',
    }),
    profileComponentFields: Object.freeze({
      version: 'RUNTIME_MATRIX_RUNTIME_VERSION',
      commit: 'RUNTIME_MATRIX_RUNTIME_COMMIT',
      'source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
  }),
  'runtime-wine-framework-matrix-candidate': Object.freeze({
    matrixBindingKind: 'wine-framework',
    imageInputs: Object.freeze([
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
    ]),
    requiredInputs: Object.freeze([
      'RUNTIME_MATRIX_PROFILE_ID',
      'RUNTIME_MATRIX_RUNTIME_VERSION',
      'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'WINE_CONTROL_TFM',
    ]),
    formattedInputs: Object.freeze({
      RUNTIME_MATRIX_RUNTIME_DIGEST: 'sha256',
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: 'sourceUri',
    }),
    runtimeDigestImageInput: 'RUNTIME_MATRIX_WINE_IMAGE',
    identityLabelBindings: Object.freeze({
      'com.sharplabnext.runtime-profile': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.runtime.framework-version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.runtime.component-digest': 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'io.sharplabnext.runtime.component-source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'io.sharplabnext.component.runtime-matrix.profile-id': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.component.runtime-matrix.version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.component.runtime-matrix.digest': 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'io.sharplabnext.component.runtime-matrix.source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
    expectedLabels: Object.freeze({
      'io.sharplabnext.runtime.environment': 'wine-netfx',
    }),
    profileComponentFields: Object.freeze({
      version: 'RUNTIME_MATRIX_RUNTIME_VERSION',
      digest: 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
  }),
  'runtime-wine-framework-matrix-shared-candidate': Object.freeze({
    matrixBindingKind: 'wine-framework',
    sharedFrameworkMatrix: true,
    imageInputs: Object.freeze([
      'BASE_DOTNET_SDK_IMAGE',
      'RUNTIME_MATRIX_WINE_IMAGE',
      'RUNTIME_MATRIX_CONTROL_IMAGE',
      'RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
      'RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
    ]),
    requiredInputs: Object.freeze([
      'RUNTIME_MATRIX_PROFILE_ID',
      'RUNTIME_MATRIX_RUNTIME_VERSION',
      'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
      'RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256',
      'RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI',
      'RUNTIME_MATRIX_FRAMEWORK_TARGET_ID',
      'RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION',
      'RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
      'RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST',
      'WINE_CONTROL_TFM',
    ]),
    formattedInputs: Object.freeze({
      RUNTIME_MATRIX_RUNTIME_DIGEST: 'sha256',
      RUNTIME_MATRIX_RUNTIME_SOURCE_URI: 'sourceUri',
      RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256: 'sha256',
      RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI: 'sourceUri',
      RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE: 'image',
      RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST: 'sha256',
    }),
    // The selected operator image is an identity input, not a FROM stage in
    // this candidate. Keep the generic runtimeDigestImageInput check off so
    // the underlying WINE_IMAGE cannot accidentally define the Framework
    // row's runtime identity.
    runtimeDigestImageInput: undefined,
    identityLabelBindings: Object.freeze({
      'com.sharplabnext.runtime-profile': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.runtime.framework-version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.runtime.component-digest': 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'io.sharplabnext.runtime.component-source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'io.sharplabnext.component.runtime-matrix.profile-id': 'RUNTIME_MATRIX_PROFILE_ID',
      'io.sharplabnext.component.runtime-matrix.version': 'RUNTIME_MATRIX_RUNTIME_VERSION',
      'io.sharplabnext.component.runtime-matrix.digest': 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'io.sharplabnext.component.runtime-matrix.source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
      'io.sharplabnext.framework.matrix-parent': 'RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE',
      'io.sharplabnext.framework.matrix-input-sha256': 'RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256',
      'io.sharplabnext.framework.matrix-source-uri': 'RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI',
      'io.sharplabnext.framework.row-operator-image': 'RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE',
      'io.sharplabnext.framework.row-digest': 'RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST',
    }),
    expectedLabels: Object.freeze({
      'io.sharplabnext.runtime.environment': 'wine-netfx',
      'io.sharplabnext.framework.matrix-selector': 'true',
    }),
    profileComponentFields: Object.freeze({
      version: 'RUNTIME_MATRIX_RUNTIME_VERSION',
      digest: 'RUNTIME_MATRIX_RUNTIME_DIGEST',
      'source-uri': 'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
    }),
  }),
})

const runtimeMatrixPath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
const candidateProfileDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')

function readJson(pathname, description, failures) {
  try {
    return JSON.parse(fs.readFileSync(pathname, 'utf8'))
  } catch (error) {
    failures.push(`could not read ${description}: ${error.message}`)
    return undefined
  }
}

function expectedVersion(matrixRow) {
  return matrixRow?.version ?? matrixRow?.resolvedVersion
}

function expectEqual(failures, actual, expected, description) {
  if (actual !== expected) {
    failures.push(`${description} must equal '${expected}'; received '${actual ?? '<missing>'}'`)
  }
}

function findCandidateBinding(kind, profileId, matrix, failures) {
  if (kind === 'mono') {
    if (matrix.mono?.id !== profileId) {
      failures.push(
        `profile '${profileId}' is not the Mono candidate row '${matrix.mono?.id ?? '<missing>'}'`,
      )
      return undefined
    }
    return {
      row: matrix.mono,
      family: 'mono',
      environment: 'mono',
      isolation: 'standard',
      executionUser: '1654:1654',
    }
  }

  if (kind === 'linux-coreclr' || kind === 'wine-coreclr') {
    const prefix = kind === 'wine-coreclr' ? 'wine-' : ''
    if (!profileId.startsWith(prefix) || !profileId.endsWith('-linux-x64')) {
      failures.push(`profile '${profileId}' is not a ${kind} candidate profile ID`)
      return undefined
    }
    const matrixId = profileId.slice(prefix.length, -'-linux-x64'.length)
    const row = matrix.coreClr?.find(candidate => candidate.id === matrixId)
    if (row === undefined) {
      failures.push(`profile '${profileId}' has no matching CoreCLR row in profiles/runtime-matrix.json`)
      return undefined
    }
    return kind === 'linux-coreclr'
      ? {
          row,
          payload: row.linux,
          family: 'coreclr',
          environment: 'coreclr',
          isolation: 'standard',
          executionUser: '1654:1654',
        }
      : {
          row,
          payload: row.windows,
          family: 'coreclr-wine',
          environment: 'wine',
          isolation: 'wine',
          executionUser: row.wineCapability?.executionUser,
        }
  }

  if (kind === 'wine-framework') {
    if (!profileId.startsWith('wine-') || !profileId.endsWith('-linux-x64')) {
      failures.push(`profile '${profileId}' is not a wine-framework candidate profile ID`)
      return undefined
    }
    const matrixId = profileId.slice('wine-'.length, -'-linux-x64'.length)
    const row = matrix.framework?.targets?.find(candidate => candidate.id === matrixId)
    if (row === undefined) {
      failures.push(`profile '${profileId}' has no matching Framework row in profiles/runtime-matrix.json`)
      return undefined
    }
    return {
      row,
      family: 'netfx-clr-wine',
      environment: 'wine',
      isolation: 'wine',
      executionUser: '0:0',
    }
  }

  failures.push(`candidate target has unsupported matrix binding kind '${kind}'`)
  return undefined
}

export function candidateMatrixBinding(target, profileId, matrix) {
  const kind = candidateTargetSpecifications[target]?.matrixBindingKind
  if (kind === undefined) throw new Error(`unknown candidate target '${target}'`)
  if (kind === 'combined-mono-wine') {
    throw new Error(
      `candidate target '${target}' is an operator image group, not a promotable runtime profile`,
    )
  }
  const failures = []
  const binding = findCandidateBinding(kind, profileId, matrix, failures)
  if (binding === undefined || failures.length > 0) {
    throw new Error(failures.join('; '))
  }
  return Object.freeze({
    ...binding,
    matrixTargetId: binding.row.id,
  })
}

function validateCoreClrPayloadBinding(kind, binding, profile, values, failures) {
  const payload = binding.payload
  if (payload === undefined) {
    failures.push(`matrix row '${binding.row.id}' has no ${kind === 'linux-coreclr' ? 'Linux' : 'Windows'} payload`)
    return
  }

  const urlInput = kind === 'linux-coreclr'
    ? 'RUNTIME_MATRIX_RUNTIME_URL'
    : 'RUNTIME_MATRIX_WINDOWS_URL'
  const shaInput = kind === 'linux-coreclr'
    ? 'RUNTIME_MATRIX_RUNTIME_SHA512'
    : 'RUNTIME_MATRIX_WINDOWS_SHA512'
  expectEqual(failures, values[urlInput], payload.url, urlInput)
  expectEqual(failures, values[shaInput], payload.sha512, shaInput)
  if (kind === 'linux-coreclr' && binding.row.linuxBaseImage !== undefined) {
    expectEqual(
      failures,
      values.RUNTIME_MATRIX_BASE_IMAGE,
      binding.row.linuxBaseImage,
      'RUNTIME_MATRIX_BASE_IMAGE',
    )
  }

  // Generated profiles currently use the row's canonical Linux payload marker
  // for the shared CoreCLR/JIT source identity, including the Wine variant.
  // The image payload URL/SHA above remains platform-specific.
  const profilePayloadSha512 = binding.row.linux?.sha512 ?? payload.sha512
  const payloadIdentity = `payload-sha512:${profilePayloadSha512}`
  for (const [profileField, inputName, matrixField] of [
    ['runtimeCommit', 'RUNTIME_MATRIX_RUNTIME_COMMIT', 'runtimeCommit'],
    ['jitCommit', 'RUNTIME_MATRIX_JIT_COMMIT', 'jitCommit'],
  ]) {
    const matrixIdentity = binding.row[matrixField]
    const profileIdentity = profile[profileField]
    const closedIdentity = matrixIdentity ?? (isGitCommitIdentity(profileIdentity) ? profileIdentity : undefined)
    if (closedIdentity !== undefined) {
      expectEqual(failures, values[inputName], closedIdentity, inputName)
      expectEqual(failures, profileIdentity, closedIdentity, `candidate profile ${profileField}`)
    } else {
      expectEqual(failures, profileIdentity, payloadIdentity, `candidate profile ${profileField}`)
    }
  }

  const sourceIdentity = payload.sourceUri ?? binding.row.sourceUri ??
    profile.runtimeSourceUri ?? payload.url
  expectEqual(
    failures,
    values.RUNTIME_MATRIX_RUNTIME_SOURCE_URI,
    sourceIdentity,
    'RUNTIME_MATRIX_RUNTIME_SOURCE_URI',
  )
}

function validateCheckedJitBinding(binding, values, failures) {
  const checkedJit = binding.row.checkedJit
  if (checkedJit === undefined) {
    for (const inputName of Object.values(checkedJitInputs)) {
      const value = values?.[inputName] ?? ''
      if (value !== '') {
        failures.push(
          `${inputName} must be empty because matrix row '${binding.row.id}' has no checkedJit lock`,
        )
      }
    }
    return
  }

  failures.push(...validateCandidateImageInputs(values, [checkedJitInputs.builderImage]))

  const canonicalSourceUrl =
    `https://github.com/dotnet/runtime/archive/${checkedJit.commit}.tar.gz`
  expectEqual(
    failures,
    checkedJit.commit,
    binding.row.runtimeCommit,
    `matrix row '${binding.row.id}' checkedJit commit`,
  )
  expectEqual(
    failures,
    checkedJit.commit,
    binding.row.jitCommit,
    `matrix row '${binding.row.id}' checkedJit/JIT commit`,
  )
  expectEqual(
    failures,
    checkedJit.sourceArchive?.url,
    canonicalSourceUrl,
    `matrix row '${binding.row.id}' checkedJit sourceArchive.url`,
  )

  const bootstrapSdk = checkedJit.bootstrapSdk
  if (bootstrapSdk !== undefined) {
    const canonicalBootstrapUrl =
      `https://builds.dotnet.microsoft.com/dotnet/Sdk/${bootstrapSdk.version}/` +
      `dotnet-sdk-${bootstrapSdk.version}-linux-x64.tar.gz`
    expectEqual(
      failures,
      bootstrapSdk.url,
      canonicalBootstrapUrl,
      `matrix row '${binding.row.id}' checkedJit bootstrapSdk.url`,
    )
  }

  for (const [inputName, expected] of [
    [checkedJitInputs.commit, checkedJit.commit],
    [checkedJitInputs.sourceUrl, checkedJit.sourceArchive?.url],
    [checkedJitInputs.sourceSha512, checkedJit.sourceArchive?.sha512],
    [checkedJitInputs.builderImage, checkedJit.builderImage],
    [checkedJitInputs.configuration, checkedJit.configuration],
    [checkedJitInputs.targetOs, checkedJit.targetOs],
    [checkedJitInputs.architecture, checkedJit.architecture],
    [checkedJitInputs.buildComponent, checkedJit.buildComponent],
    [checkedJitInputs.pgoMode, checkedJit.pgoMode],
    [checkedJitInputs.compiler, checkedJit.compiler],
    [checkedJitInputs.generator, checkedJit.generator],
    [checkedJitInputs.versionGenerationMode, checkedJit.versionGenerationMode ?? ''],
    [checkedJitInputs.sourceMappingKind, checkedJit.sourceMappingKind],
  ]) {
    expectEqual(failures, values?.[inputName], expected, inputName)
  }

  for (const [inputName, expected] of [
    [checkedJitInputs.bootstrapSdkVersion, bootstrapSdk?.version ?? ''],
    [checkedJitInputs.bootstrapSdkUrl, bootstrapSdk?.url ?? ''],
    [checkedJitInputs.bootstrapSdkSha512, bootstrapSdk?.sha512 ?? ''],
  ]) {
    expectEqual(failures, values?.[inputName] ?? '', expected, inputName)
  }
}

function validateProfilerProviderBinding(binding, values, failures) {
  const provider = binding.row.profilerProvider
  if (provider === undefined) {
    for (const inputName of Object.values(profilerProviderInputs)) {
      const value = values?.[inputName]
      if (value !== undefined && value !== '') {
        failures.push(
          `${inputName} must be empty because matrix row '${binding.row.id}' has no profilerProvider lock`,
        )
      }
    }
    return
  }

  if (binding.row.checkedJit !== undefined) {
    failures.push(
      `matrix row '${binding.row.id}' cannot select checkedJit and profilerProvider together`,
    )
  }
  failures.push(...validateCandidateImageInputs(values, [profilerProviderInputs.builderImage]))

  for (const [inputName, expected] of [
    [profilerProviderInputs.id, provider.id],
    [profilerProviderInputs.builderImage, provider.builderImage],
    [profilerProviderInputs.scaffoldCommit, provider.scaffold?.commit],
    [profilerProviderInputs.scaffoldSourceUri, provider.scaffold?.sourceUri],
    [profilerProviderInputs.runtimeHeadersCommit, provider.runtimeHeaders?.commit],
    [profilerProviderInputs.runtimeHeadersSourceUri, provider.runtimeHeaders?.sourceUri],
    [profilerProviderInputs.sourceMappingKind, provider.sourceMappingKind],
  ]) {
    expectEqual(failures, values?.[inputName], expected, inputName)
  }
}

function validateCoreClrFrameworkBinding(binding, profile, version, failures) {
  const framework = profile.acceptedFrameworks?.find(value =>
    value.name === 'Microsoft.NETCore.App')
  if (framework?.exactVersion !== undefined) {
    expectEqual(
      failures,
      framework.exactVersion,
      version,
      'candidate profile CoreCLR exactVersion',
    )
    return
  }

  expectEqual(
    failures,
    framework?.minimumVersion,
    binding.row.referencePackage?.version,
    'candidate profile CoreCLR minimumVersion',
  )
  expectEqual(
    failures,
    framework?.maximumVersion,
    version,
    'candidate profile CoreCLR maximumVersion',
  )
}

function validateCandidateMatrixBinding(target, values, failures) {
  const kind = candidateTargetSpecifications[target]?.matrixBindingKind
  if (kind === undefined || kind === 'combined-mono-wine') return

  const profileId = values?.RUNTIME_MATRIX_PROFILE_ID
  if (typeof profileId !== 'string' || !/^[a-z0-9][a-z0-9.-]*$/.test(profileId)) {
    failures.push('RUNTIME_MATRIX_PROFILE_ID must be a safe lowercase candidate profile ID')
    return
  }

  const matrix = readJson(runtimeMatrixPath, 'profiles/runtime-matrix.json', failures)
  const profilePath = path.join(candidateProfileDirectory, `${profileId}.json`)
  const profile = readJson(
    profilePath,
    `candidate runtime profile profiles/runtimes/candidates/${profileId}.json`,
    failures,
  )
  if (matrix === undefined || profile === undefined) return

  expectEqual(failures, profile.id, profileId, 'candidate profile id')
  const binding = findCandidateBinding(kind, profileId, matrix, failures)
  if (binding === undefined) return

  const version = expectedVersion(binding.row)
  if (typeof version !== 'string' || version.length === 0) {
    failures.push(`matrix row '${binding.row.id}' has no resolved version`)
    return
  }
  expectEqual(failures, values.RUNTIME_MATRIX_RUNTIME_VERSION, version, 'RUNTIME_MATRIX_RUNTIME_VERSION')
  expectEqual(failures, profile.runtimeVersion, version, 'candidate profile runtimeVersion')
  expectEqual(failures, profile.family, binding.family, 'candidate profile family')
  expectEqual(failures, profile.rid, 'linux-x64', 'candidate profile rid')
  expectEqual(failures, profile.architecture, 'x64', 'candidate profile architecture')
  expectEqual(
    failures,
    profile.container?.environmentKind,
    binding.environment,
    'candidate profile container.environmentKind',
  )
  expectEqual(
    failures,
    profile.container?.isolationKind,
    binding.isolation,
    'candidate profile container.isolationKind',
  )
  expectEqual(
    failures,
    profile.container?.executionUser,
    binding.executionUser,
    'candidate profile container.executionUser',
  )
  if (!profile.acceptedRuntimeFamilies?.includes(binding.family)) {
    failures.push(`candidate profile acceptedRuntimeFamilies must include '${binding.family}'`)
  }

  if (kind === 'linux-coreclr' || kind === 'wine-coreclr') {
    expectEqual(failures, profile.jitVersion, version, 'candidate profile jitVersion')
    validateCoreClrFrameworkBinding(binding, profile, version, failures)
    validateCoreClrPayloadBinding(kind, binding, profile, values, failures)
    if (kind === 'linux-coreclr') {
      validateCheckedJitBinding(binding, values, failures)
      validateProfilerProviderBinding(binding, values, failures)
    }
  } else if (kind === 'wine-framework') {
    const framework = profile.acceptedFrameworks?.find(value => value.name === '.NETFramework')
    expectEqual(failures, framework?.exactVersion, version, 'candidate profile Framework exactVersion')
    expectEqual(
      failures,
      profile.container?.winePrefixPath,
      binding.row.prefix,
      'candidate profile container.winePrefixPath',
    )
    if (candidateTargetSpecifications[target].sharedFrameworkMatrix) {
      expectEqual(
        failures,
        values.RUNTIME_MATRIX_FRAMEWORK_TARGET_ID,
        binding.row.id,
        'RUNTIME_MATRIX_FRAMEWORK_TARGET_ID',
      )
      expectEqual(
        failures,
        values.RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION,
        binding.row.clrGeneration,
        'RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION',
      )
    }
  } else if (kind === 'mono') {
    expectEqual(
      failures,
      values.RUNTIME_MATRIX_MONO_IMAGE,
      binding.row.image,
      'RUNTIME_MATRIX_MONO_IMAGE',
    )
  }
}

export function validateCandidateBuildInputs(target, values) {
  const specification = candidateTargetSpecifications[target]
  if (specification === undefined) {
    return [`unknown candidate target '${target}'`]
  }

  const failures = validateCandidateImageInputs(values, specification.imageInputs)
  for (const name of [...commonRequiredInputs, ...specification.requiredInputs]) {
    const value = values?.[name]
    if (typeof value !== 'string' || value.trim().length === 0) {
      failures.push(`${name} must be non-empty for ${target}`)
    }
  }
  if (typeof values?.SOURCE_REVISION === 'string' &&
      !isGitCommitIdentity(values.SOURCE_REVISION)) {
    failures.push('SOURCE_REVISION must be a 40- or 64-character lowercase hexadecimal commit')
  }
  for (const [name, formatName] of Object.entries(specification.formattedInputs ?? {})) {
    const value = values?.[name]
    if (typeof value !== 'string' || value.trim().length === 0) continue
    const format = inputFormatValidators[formatName]
    if (!format.accepts(value)) failures.push(`${name} must be ${format.description}`)
  }
  if (specification.sharedFrameworkMatrix) {
    if (typeof values?.RUNTIME_MATRIX_FRAMEWORK_TARGET_ID === 'string' &&
        !/^[a-z0-9][a-z0-9._-]{0,127}$/.test(values.RUNTIME_MATRIX_FRAMEWORK_TARGET_ID)) {
      failures.push('RUNTIME_MATRIX_FRAMEWORK_TARGET_ID must be a safe lowercase identifier')
    }
    if (typeof values?.RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION === 'string' &&
        !/^clr[24]$/.test(values.RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION)) {
      failures.push('RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION must be clr2 or clr4')
    }
    const rowOperatorImage = values?.RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE
    const rowDigest = values?.RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST
    if (isDigestPinnedImageReference(rowOperatorImage) && isSha256Digest(rowDigest)) {
      const operatorDigest = rowOperatorImage.slice(rowOperatorImage.lastIndexOf('@') + 1)
      expectEqual(
        failures,
        values.RUNTIME_MATRIX_RUNTIME_DIGEST,
        operatorDigest,
        'RUNTIME_MATRIX_RUNTIME_DIGEST for selected Framework operator image',
      )
      expectEqual(
        failures,
        values.RUNTIME_MATRIX_RUNTIME_SOURCE_URI,
        `docker://${rowOperatorImage}`,
        'RUNTIME_MATRIX_RUNTIME_SOURCE_URI for selected Framework row',
      )
    }
  }
  if (specification.runtimeDigestImageInput !== undefined) {
    const runtimeDigest = values?.RUNTIME_MATRIX_RUNTIME_DIGEST
    const imageReference = values?.[specification.runtimeDigestImageInput]
    if (isSha256Digest(runtimeDigest) && typeof imageReference === 'string') {
      const imageDigest = imageReference.slice(imageReference.lastIndexOf('@') + 1)
      if (runtimeDigest !== imageDigest) {
        failures.push(
          `RUNTIME_MATRIX_RUNTIME_DIGEST must equal the digest pinned by ${specification.runtimeDigestImageInput}`,
        )
      }
      const sourceUri = values?.RUNTIME_MATRIX_RUNTIME_SOURCE_URI
      if (typeof sourceUri === 'string' && sourceUri.startsWith('docker://') &&
          sourceUri.slice('docker://'.length) !== imageReference) {
        failures.push(
          `RUNTIME_MATRIX_RUNTIME_SOURCE_URI must identify ${specification.runtimeDigestImageInput} when it uses docker://`,
        )
      }
    }
  }
  validateCandidateMatrixBinding(target, values, failures)
  return failures
}

export function candidateComponentIdentity(target, values) {
  if (candidateTargetSpecifications[target] === undefined) {
    throw new Error(`unknown candidate target '${target}'`)
  }
  if (target === 'runtime-dotnet-matrix-candidate') {
    return Object.freeze({
      sourceUri: values.RUNTIME_MATRIX_RUNTIME_SOURCE_URI,
      sourceDigest: `sha512:${values.RUNTIME_MATRIX_RUNTIME_SHA512}`,
    })
  }
  if (target === 'runtime-wine-dotnet-matrix-candidate') {
    return Object.freeze({
      sourceUri: values.RUNTIME_MATRIX_RUNTIME_SOURCE_URI,
      sourceDigest: `sha512:${values.RUNTIME_MATRIX_WINDOWS_SHA512}`,
    })
  }
  return Object.freeze({
    sourceUri: values.RUNTIME_MATRIX_RUNTIME_SOURCE_URI,
    sourceDigest: values.RUNTIME_MATRIX_RUNTIME_DIGEST,
  })
}

export function candidateOperationHelpers(target, values = process.env) {
  const helpers = candidateHelperOperations[target]
  if (helpers === undefined) throw new Error(`unknown candidate target '${target}'`)
  if (target === 'runtime-dotnet-matrix-candidate') {
    const hasCheckedJit = typeof values?.[checkedJitInputs.commit] === 'string' &&
      values[checkedJitInputs.commit].length > 0
    const hasProfilerProvider = typeof values?.[profilerProviderInputs.id] === 'string' &&
      values[profilerProviderInputs.id].length > 0
    if (hasCheckedJit && hasProfilerProvider) {
      throw new Error('Checked-JIT and profiler provider inputs cannot be selected together')
    }
    if (hasCheckedJit) return checkedJitHelperOperations
    if (hasProfilerProvider) return profilerHelperOperations
  }
  return helpers
}

export function candidateIdentityLabelBindings(target, values) {
  const specification = candidateTargetSpecifications[target]
  if (specification === undefined) throw new Error(`unknown candidate target '${target}'`)
  const bindings = {
    ...commonIdentityLabelBindings,
    ...specification.identityLabelBindings,
  }
  if (target === 'runtime-dotnet-matrix-candidate') {
    const hasCheckedJit = typeof values?.[checkedJitInputs.commit] === 'string' &&
      values[checkedJitInputs.commit].length > 0
    const hasProfilerProvider = typeof values?.[profilerProviderInputs.id] === 'string' &&
      values[profilerProviderInputs.id].length > 0
    if (hasCheckedJit && hasProfilerProvider) {
      throw new Error('Checked-JIT and profiler provider inputs cannot be selected together')
    }
    if (hasCheckedJit) {
      Object.assign(bindings, checkedJitIdentityLabelBindings)
      const hasBootstrapSdk = typeof values?.[checkedJitInputs.bootstrapSdkVersion] === 'string' &&
        values[checkedJitInputs.bootstrapSdkVersion].length > 0
      if (hasBootstrapSdk) Object.assign(bindings, checkedJitBootstrapIdentityLabelBindings)
    } else {
      // The optional Checked-JIT stage still needs a valid FROM for legacy
      // rows. Bake uses the already-validated SDK image as that no-op base.
      bindings['io.sharplabnext.jit.checked.builder-image'] = 'BASE_DOTNET_SDK_IMAGE'
    }
    if (hasProfilerProvider) {
      Object.assign(bindings, profilerProviderIdentityLabelBindings)
    } else {
      // The optional profiler stage follows the same fail-closed FROM rule.
      bindings['io.sharplabnext.jit.profiler.builder-image'] = 'BASE_DOTNET_SDK_IMAGE'
    }
  }
  const profileId = values?.RUNTIME_MATRIX_PROFILE_ID
  if (specification.profileComponentFields !== undefined &&
      typeof profileId === 'string' && profileId.length > 0) {
    for (const [field, inputName] of Object.entries(specification.profileComponentFields)) {
      bindings[`io.sharplabnext.component.${profileId}.${field}`] = inputName
    }
  }
  return bindings
}

export function candidateExpectedLabels(target) {
  const specification = candidateTargetSpecifications[target]
  if (specification === undefined) throw new Error(`unknown candidate target '${target}'`)
  return { ...commonExpectedLabels, ...specification.expectedLabels }
}

export function createCandidateBakeArguments(target, additionalArguments = [], values = process.env) {
  if (candidateTargetSpecifications[target] === undefined) {
    throw new Error(`unknown candidate target '${target}'`)
  }
  validateAdditionalArguments(additionalArguments)
  const outputArguments = isNonBuildInvocation(additionalArguments) || additionalArguments.includes('--load')
    ? additionalArguments
    : ['--load', ...additionalArguments]
  const cacheOnlyOutput = requiresCacheOnlyOutput(additionalArguments)
    ? ['--set', `${target}.output=type=cacheonly`]
    : []
  return [
    'buildx',
    'bake',
    '--file',
    'eng/bake.hcl',
    '--file',
    'eng/bake.runtime-candidates.hcl',
    ...cacheOnlyOutput,
    ...outputArguments,
    target,
  ]
}

function validateAdditionalArguments(values) {
  const booleanOptions = new Set(['--check', '--load', '--no-cache', '--print', '--pull'])
  const valueOptions = new Set([
    '--allow',
    '--builder',
    '--call',
    '--metadata-file',
    '--progress',
    '--provenance',
    '--sbom',
  ])

  for (let index = 0; index < values.length; index++) {
    const argument = values[index]
    if (argument === '-f' || /^-f(?:=|[^-])/.test(argument) ||
        argument === '--file' || argument.startsWith('--file=')) {
      throw new Error('candidate builds cannot override the reviewed Bake files')
    }
    if (argument === '--set' || argument.startsWith('--set=')) {
      throw new Error('candidate builds cannot override validated target fields with --set')
    }
    if (argument === '--push' || argument.startsWith('--push=')) {
      throw new Error('candidate builds must remain local until their image labels are verified')
    }
    if (booleanOptions.has(argument)) continue

    const equalsIndex = argument.indexOf('=')
    const optionName = equalsIndex < 0 ? argument : argument.slice(0, equalsIndex)
    if (!valueOptions.has(optionName)) {
      throw new Error(`unsupported candidate Bake option '${argument}'`)
    }
    if (equalsIndex >= 0) {
      if (optionName === '--call') validateCallValue(argument.slice(equalsIndex + 1))
      continue
    }
    index++
    if (index >= values.length || values[index].length === 0) {
      throw new Error(`${optionName} requires a value`)
    }
    if (optionName === '--call') validateCallValue(values[index])
  }
}

function validateCallValue(value) {
  const nonBuildCalls = new Set(['check', 'outline', 'targets', 'subrequests.describe'])
  if (!nonBuildCalls.has(value)) {
    throw new Error(`unsupported candidate Bake --call value '${value}'`)
  }
}

export function candidateImageTag(target, values) {
  const suffix = target === 'runtime-mono-wine-matrix-candidate'
    ? 'mono-wine-matrix'
    : values.RUNTIME_MATRIX_PROFILE_ID
  return `${values.IMAGE_PREFIX}/runtime-${suffix}:candidate`
}

export function candidateReleaseImageTag(target, values) {
  if (candidateTargetSpecifications[target] === undefined) {
    throw new Error(`unknown candidate target '${target}'`)
  }
  const suffix = target === 'runtime-mono-wine-matrix-candidate'
    ? 'mono-wine-matrix'
    : values.RUNTIME_MATRIX_PROFILE_ID
  return `${values.IMAGE_PREFIX}/runtime-${suffix}:${values.RELEASE_ID}`
}

function isNonBuildInvocation(arguments_) {
  return arguments_.some((argument, index) =>
    argument === '--print' ||
    argument === '--check' ||
    argument === '--call' ||
    argument.startsWith('--call=') ||
    (index > 0 && arguments_[index - 1] === '--call'))
}

function requiresCacheOnlyOutput(arguments_) {
  return arguments_.some((argument, index) =>
    argument === '--check' ||
    argument === '--call' ||
    argument.startsWith('--call=') ||
    (index > 0 && arguments_[index - 1] === '--call'))
}

export function candidateExpectedImageLabels(target, values) {
  const specification = candidateTargetSpecifications[target]
  const selectedImageBindings = Object.fromEntries(
    Object.entries(candidateImageLabelBindings)
      .filter(([, inputName]) => specification.imageInputs.includes(inputName)),
  )
  return {
    ...candidateExpectedLabels(target),
    ...Object.fromEntries(
      Object.entries({
        ...selectedImageBindings,
        ...candidateIdentityLabelBindings(target, values),
      }).map(([label, inputName]) => [label, values[inputName]]),
    ),
  }
}

export function runCandidateBuild(
  argv,
  values = process.env,
  spawn = spawnSync,
  output = console,
) {
  const [target, ...rawAdditionalArguments] = argv
  if (target === undefined) {
    output.error(
      'Usage: node eng/build-runtime-candidate.mjs <candidate-target> ' +
      `[${developmentSourceOverride}] [docker buildx bake options]`,
    )
    return 64
  }

  const developmentOverrideCount = rawAdditionalArguments
    .filter(argument => argument === developmentSourceOverride).length
  if (developmentOverrideCount > 1) {
    output.error(`runtime candidate input error: ${developmentSourceOverride} may be specified once`)
    return 64
  }
  const allowUncommittedSourceForDevelopment = developmentOverrideCount === 1
  const additionalArguments = rawAdditionalArguments
    .filter(argument => argument !== developmentSourceOverride)

  const failures = validateCandidateBuildInputs(target, values)
  if (failures.length > 0) {
    for (const failure of failures) output.error(`runtime candidate input error: ${failure}`)
    return 1
  }

  let bakeArguments
  try {
    bakeArguments = createCandidateBakeArguments(target, additionalArguments, values)
  } catch (error) {
    output.error(`runtime candidate input error: ${error.message}`)
    return 64
  }

  const optionalDotnetBuilderCount = target === 'runtime-dotnet-matrix-candidate'
    ? [checkedJitInputs.builderImage, profilerProviderInputs.builderImage]
        .filter(inputName => typeof values?.[inputName] === 'string' && values[inputName].length > 0)
        .length
    : 0
  const imageCount = candidateTargetSpecifications[target].imageInputs.length +
    optionalDotnetBuilderCount
  output.log(`Validated ${imageCount} digest-pinned image inputs for ${target}.`)
  const dockerEnvironment = { ...values }
  delete dockerEnvironment.BUILDX_BAKE_FILE
  delete dockerEnvironment.BUILDX_BAKE_FILE_SEPARATOR

  const nonBuildInvocation = isNonBuildInvocation(additionalArguments)
  let sourceBinding = { promotionEligible: true }
  if (!nonBuildInvocation) {
    try {
      const sourceState = inspectGitSourceState({
        spawn,
        cwd: repositoryRoot,
        env: dockerEnvironment,
      })
      sourceBinding = validateGitSourceState(sourceState, values.SOURCE_REVISION, {
        allowUncommittedSourceForDevelopment,
      })
      if (sourceBinding.failures.length > 0) {
        for (const failure of sourceBinding.failures) {
          output.error(`runtime candidate source error: ${failure}`)
        }
        return 1
      }
      if (!sourceBinding.promotionEligible) {
        output.log(
          'Source worktree is dirty under the explicit development override; ' +
          'this local candidate is not eligible for a promotion receipt.',
        )
      }
    } catch (error) {
      output.error(`runtime candidate source error: ${error.message}`)
      return 1
    }
  }

  const result = spawn('docker', bakeArguments, {
    cwd: repositoryRoot,
    env: dockerEnvironment,
    stdio: 'inherit',
    shell: false,
  })
  if (result.error !== undefined) {
    output.error(`Could not start docker: ${result.error.message}`)
    return 1
  }
  if (result.status !== 0 || nonBuildInvocation) {
    return result.status ?? 1
  }

  const image = candidateImageTag(target, values)
  let imageBinding
  try {
    imageBinding = bindRuntimeCandidateImage({
      candidateReference: image,
      sourceRevision: values.SOURCE_REVISION,
      expectedLabels: candidateExpectedImageLabels(target, values),
      inspect: reference => inspectDockerImage(reference, {
        spawn,
        cwd: repositoryRoot,
        env: dockerEnvironment,
      }),
    })
  } catch (error) {
    output.error(`runtime candidate identity error: ${error.message}`)
    return 1
  }
  const identityFailures = validateCandidateImageIdentity(
    values,
    imageBinding.labels,
    candidateTargetSpecifications[target].imageInputs,
  )
  identityFailures.push(...validateCandidateImageLabels(
    imageBinding.labels,
    values,
    candidateIdentityLabelBindings(target, values),
  ))
  identityFailures.push(...validateCandidateExpectedLabels(
    imageBinding.labels,
    candidateExpectedLabels(target),
  ))
  if (identityFailures.length > 0) {
    for (const failure of identityFailures) {
      output.error(`runtime candidate identity error: ${failure}`)
    }
    return 1
  }

  let observedOperations
  try {
    observedOperations = hashRuntimeOperationHelpers(
      imageBinding.imageId,
      candidateOperationHelpers(target, values),
      {
        spawn,
        cwd: repositoryRoot,
        env: dockerEnvironment,
      },
    )
  } catch (error) {
    output.error(`runtime candidate helper error: ${error.message}`)
    return 1
  }

  const componentIdentity = candidateComponentIdentity(target, values)
  output.log(
    `Verified ${imageCount} immutable image labels on ${imageBinding.imageId} ` +
    `(captured from ${image}).`,
  )
  output.log(
    `Observed component source ${componentIdentity.sourceUri} ` +
    `with ${componentIdentity.sourceDigest}.`,
  )
  for (const [operation, helper] of Object.entries(observedOperations)) {
    output.log(
      `Observed ${operation} helper ${helper.assemblyPath} as ${helper.assemblySha256}.`,
    )
    if (helper.profilerPath !== undefined) {
      output.log(
        `Observed ${operation} profiler ${helper.profilerPath} as ${helper.profilerSha256}.`,
      )
    }
  }
  if (imageBinding.reference === null) {
    output.log(
      'Candidate image has no bound registry RepoDigest; its image ID is observed locally, ' +
      'but no promotable registry reference is claimed.',
    )
  }
  if (!sourceBinding.promotionEligible) {
    output.log('Development-only candidate verification completed; promotion output remains disabled.')
  }
  return 0
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runCandidateBuild(process.argv.slice(2))
}
