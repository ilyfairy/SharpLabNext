# Candidate-only runtime graph. Load this file together with eng/bake.hcl only
# through eng/build-runtime-candidate.mjs. Buildx eagerly evaluates every target
# in every loaded file, so keeping these variables out of the production graph
# is what lets the normal/default Bake remain independent of operator inputs.

variable "RUNTIME_MATRIX_PROFILE_ID" {
  default = ""
}

variable "RUNTIME_MATRIX_RUNTIME_VERSION" {
  default = ""
}

variable "RUNTIME_MATRIX_RUNTIME_COMMIT" {
  default = ""
}

variable "RUNTIME_MATRIX_JIT_COMMIT" {
  default = ""
}

variable "RUNTIME_MATRIX_RUNTIME_URL" {
  default = ""
}

variable "RUNTIME_MATRIX_RUNTIME_SHA512" {
  default = ""
}

variable "RUNTIME_MATRIX_RUNTIME_SOURCE_URI" {
  default = ""
}

# Mono and Desktop CLR identities are closed by immutable image/archive digest
# rather than a CoreCLR source commit.
variable "RUNTIME_MATRIX_RUNTIME_DIGEST" {
  default = ""
}

variable "RUNTIME_MATRIX_BASE_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_MONO_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_MONO_WINE_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_CONTROL_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_WINE_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_TARGET_ID" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST" {
  default = ""
}

variable "RUNTIME_CANDIDATE_SOURCE_CONTEXT" {
  default = ""
}

variable "RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE" {
  default = ""
}

variable "RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT" {
  default = ""
}

variable "WINE_CORECLR_OPERATOR_RECEIPT_SHA256" {
  default = ""
}

variable "WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID" {
  default = ""
}

variable "WINE_CORECLR_OPERATOR_REFERENCE" {
  default = ""
}

variable "WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE" {
  default = ""
}

variable "WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG" {
  default = ""
}

variable "RUNTIME_MATRIX_WINDOWS_URL" {
  default = ""
}

variable "RUNTIME_MATRIX_WINDOWS_SHA512" {
  default = ""
}

# Exact source-built Checked JIT identity. These remain empty for legacy
# Linux CoreCLR rows; the candidate then uses the SDK image as a no-op stage
# base and retains the Legacy JIT helper.
variable "RUNTIME_MATRIX_CHECKED_JIT_COMMIT" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_CONFIGURATION" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_TARGET_OS" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_ARCHITECTURE" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_BUILD_COMPONENT" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_PGO_MODE" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_COMPILER" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_GENERATOR" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE" {
  default = ""
}

variable "RUNTIME_MATRIX_CHECKED_JIT_SOURCE_MAPPING_KIND" {
  default = ""
}

# Exact source/provenance identity for the modern Release-runtime profiler.
# Rows without this provider use the SDK image as a no-op stage base so every
# FROM remains digest pinned while the final image omits the modern assets.
variable "RUNTIME_MATRIX_PROFILER_PROVIDER_ID" {
  default = ""
}

variable "RUNTIME_MATRIX_PROFILER_BUILD_IMAGE" {
  default = ""
}

variable "RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT" {
  default = ""
}

variable "RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI" {
  default = ""
}

variable "RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT" {
  default = ""
}

variable "RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI" {
  default = ""
}

variable "RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND" {
  default = ""
}

# Candidate values stay raw in this graph because Buildx evaluates all five
# targets even when one is selected. The mandatory JavaScript entry validates
# the selected target before Buildx can resolve any FROM, and every Dockerfile
# repeats the image validation inside the retained final image.

target "runtime-dotnet-matrix-candidate" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-dotnet-matrix"
  tags = [
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:candidate",
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:${required(RELEASE_ID)}",
  ]
  args = {
    SDK_IMAGE = BASE_DOTNET_SDK_IMAGE
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CANDIDATE_SOURCE_CONTEXT = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    CANDIDATE_PROMOTION_ELIGIBLE = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    PROFILE_ID = RUNTIME_MATRIX_PROFILE_ID
    DOTNET_RUNTIME_VERSION = RUNTIME_MATRIX_RUNTIME_VERSION
    DOTNET_RUNTIME_COMMIT = RUNTIME_MATRIX_RUNTIME_COMMIT
    DOTNET_JIT_COMMIT = RUNTIME_MATRIX_JIT_COMMIT
    DOTNET_RUNTIME_URL = RUNTIME_MATRIX_RUNTIME_URL
    DOTNET_RUNTIME_SHA512 = RUNTIME_MATRIX_RUNTIME_SHA512
    RUNTIME_DEPS_IMAGE = RUNTIME_MATRIX_BASE_IMAGE
    CHECKED_JIT_BUILD_IMAGE = RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE != "" ? RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE : BASE_DOTNET_SDK_IMAGE
    DOTNET_CHECKED_JIT_COMMIT = RUNTIME_MATRIX_CHECKED_JIT_COMMIT
    DOTNET_CHECKED_JIT_SOURCE_URL = RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL
    DOTNET_CHECKED_JIT_SOURCE_SHA512 = RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512
    DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_VERSION = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION
    DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_URL = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL
    DOTNET_CHECKED_JIT_BOOTSTRAP_SDK_SHA512 = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512
    DOTNET_CHECKED_JIT_CONFIGURATION = RUNTIME_MATRIX_CHECKED_JIT_CONFIGURATION
    DOTNET_CHECKED_JIT_TARGET_OS = RUNTIME_MATRIX_CHECKED_JIT_TARGET_OS
    DOTNET_CHECKED_JIT_ARCHITECTURE = RUNTIME_MATRIX_CHECKED_JIT_ARCHITECTURE
    DOTNET_CHECKED_JIT_BUILD_COMPONENT = RUNTIME_MATRIX_CHECKED_JIT_BUILD_COMPONENT
    DOTNET_CHECKED_JIT_PGO_MODE = RUNTIME_MATRIX_CHECKED_JIT_PGO_MODE
    DOTNET_CHECKED_JIT_COMPILER = RUNTIME_MATRIX_CHECKED_JIT_COMPILER
    DOTNET_CHECKED_JIT_GENERATOR = RUNTIME_MATRIX_CHECKED_JIT_GENERATOR
    DOTNET_CHECKED_JIT_VERSION_GENERATION_MODE = RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE
    DOTNET_CHECKED_JIT_SOURCE_MAPPING_KIND = RUNTIME_MATRIX_CHECKED_JIT_SOURCE_MAPPING_KIND
    PROFILER_BUILD_IMAGE = RUNTIME_MATRIX_PROFILER_BUILD_IMAGE != "" ? RUNTIME_MATRIX_PROFILER_BUILD_IMAGE : BASE_DOTNET_SDK_IMAGE
    JIT_PROFILER_PROVIDER_ID = RUNTIME_MATRIX_PROFILER_PROVIDER_ID
    JIT_PROFILER_CLR_SAMPLES_COMMIT = RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT
    JIT_PROFILER_CLR_SAMPLES_SOURCE_URI = RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI
    JIT_PROFILER_RUNTIME_HEADERS_COMMIT = RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT
    JIT_PROFILER_RUNTIME_HEADERS_SOURCE_URI = RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI
    JIT_PROFILER_SOURCE_MAPPING_KIND = RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND
  }
  labels = {
    "com.sharplabnext.runtime-candidate" = "true"
    "io.sharplabnext.source.context" = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    "com.sharplabnext.runtime-candidate.promotion-eligible" = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    "io.sharplabnext.component.runtime-matrix.profile-id" = RUNTIME_MATRIX_PROFILE_ID
    "io.sharplabnext.component.runtime-matrix.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.runtime-matrix.commit" = RUNTIME_MATRIX_RUNTIME_COMMIT
    "io.sharplabnext.component.runtime-matrix.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.commit" = RUNTIME_MATRIX_RUNTIME_COMMIT
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.runtime.commit" = RUNTIME_MATRIX_RUNTIME_COMMIT
    "io.sharplabnext.jit.commit" = RUNTIME_MATRIX_JIT_COMMIT
    "io.sharplabnext.jit.checked.commit" = RUNTIME_MATRIX_CHECKED_JIT_COMMIT
    "io.sharplabnext.jit.checked.source-uri" = RUNTIME_MATRIX_CHECKED_JIT_SOURCE_URL
    "io.sharplabnext.jit.checked.source-sha512" = RUNTIME_MATRIX_CHECKED_JIT_SOURCE_SHA512
    "io.sharplabnext.jit.checked.bootstrap-sdk.version" = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_VERSION
    "io.sharplabnext.jit.checked.bootstrap-sdk.source-uri" = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_URL
    "io.sharplabnext.jit.checked.bootstrap-sdk.source-sha512" = RUNTIME_MATRIX_CHECKED_JIT_BOOTSTRAP_SDK_SHA512
    "io.sharplabnext.jit.checked.builder-image" = RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE != "" ? RUNTIME_MATRIX_CHECKED_JIT_BUILD_IMAGE : BASE_DOTNET_SDK_IMAGE
    "io.sharplabnext.jit.checked.configuration" = RUNTIME_MATRIX_CHECKED_JIT_CONFIGURATION
    "io.sharplabnext.jit.checked.target-os" = RUNTIME_MATRIX_CHECKED_JIT_TARGET_OS
    "io.sharplabnext.jit.checked.architecture" = RUNTIME_MATRIX_CHECKED_JIT_ARCHITECTURE
    "io.sharplabnext.jit.checked.build-component" = RUNTIME_MATRIX_CHECKED_JIT_BUILD_COMPONENT
    "io.sharplabnext.jit.checked.pgo-mode" = RUNTIME_MATRIX_CHECKED_JIT_PGO_MODE
    "io.sharplabnext.jit.checked.compiler" = RUNTIME_MATRIX_CHECKED_JIT_COMPILER
    "io.sharplabnext.jit.checked.generator" = RUNTIME_MATRIX_CHECKED_JIT_GENERATOR
    "io.sharplabnext.jit.checked.version-generation-mode" = RUNTIME_MATRIX_CHECKED_JIT_VERSION_GENERATION_MODE
    "io.sharplabnext.jit.checked.source-mapping-kind" = RUNTIME_MATRIX_CHECKED_JIT_SOURCE_MAPPING_KIND
    "io.sharplabnext.jit.profiler.provider" = RUNTIME_MATRIX_PROFILER_PROVIDER_ID
    "io.sharplabnext.jit.profiler.builder-image" = RUNTIME_MATRIX_PROFILER_BUILD_IMAGE != "" ? RUNTIME_MATRIX_PROFILER_BUILD_IMAGE : BASE_DOTNET_SDK_IMAGE
    "io.sharplabnext.component.jit-profiler-clr-samples.commit" = RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_COMMIT
    "io.sharplabnext.component.jit-profiler-clr-samples.source-uri" = RUNTIME_MATRIX_PROFILER_CLR_SAMPLES_SOURCE_URI
    "io.sharplabnext.component.jit-profiler-runtime-headers.commit" = RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_COMMIT
    "io.sharplabnext.component.jit-profiler-runtime-headers.source-uri" = RUNTIME_MATRIX_PROFILER_RUNTIME_HEADERS_SOURCE_URI
    "io.sharplabnext.jit.profiler.source-mapping-kind" = RUNTIME_MATRIX_PROFILER_SOURCE_MAPPING_KIND
    "io.sharplabnext.base-image.dotnet-sdk" = BASE_DOTNET_SDK_IMAGE
    "io.sharplabnext.base-image.dotnet-runtime-deps" = RUNTIME_MATRIX_BASE_IMAGE
  }
}

target "runtime-mono-matrix-candidate" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-mono-matrix"
  tags = [
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:candidate",
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:${required(RELEASE_ID)}",
  ]
  args = {
    SDK_IMAGE = BASE_DOTNET_SDK_IMAGE
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CANDIDATE_SOURCE_CONTEXT = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    CANDIDATE_PROMOTION_ELIGIBLE = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    PROFILE_ID = RUNTIME_MATRIX_PROFILE_ID
    MONO_VERSION = RUNTIME_MATRIX_RUNTIME_VERSION
    MONO_IMAGE = RUNTIME_MATRIX_MONO_IMAGE
    CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE
    RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST
    RUNTIME_COMPONENT_SOURCE_URI = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    CONTROL_TFM = required(WINE_CONTROL_TFM)
  }
  labels = {
    "com.sharplabnext.runtime-candidate" = "true"
    "io.sharplabnext.source.context" = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    "com.sharplabnext.runtime-candidate.promotion-eligible" = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    "io.sharplabnext.component.runtime-matrix.profile-id" = RUNTIME_MATRIX_PROFILE_ID
    "io.sharplabnext.component.runtime-matrix.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.runtime-matrix.digest" = RUNTIME_MATRIX_RUNTIME_DIGEST
    "io.sharplabnext.component.runtime-matrix.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.digest" = RUNTIME_MATRIX_RUNTIME_DIGEST
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.control-image" = RUNTIME_MATRIX_CONTROL_IMAGE
    "io.sharplabnext.operator-image.mono" = RUNTIME_MATRIX_MONO_IMAGE
    "io.sharplabnext.base-image.dotnet-sdk" = BASE_DOTNET_SDK_IMAGE
  }
}

target "runtime-mono-wine-matrix-candidate" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-mono-wine-matrix"
  tags = [
    "${required(IMAGE_PREFIX)}/runtime-mono-wine-matrix:candidate",
    "${required(IMAGE_PREFIX)}/runtime-mono-wine-matrix:${required(RELEASE_ID)}",
  ]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CANDIDATE_SOURCE_CONTEXT = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    CANDIDATE_PROMOTION_ELIGIBLE = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    PROFILE_GROUP_ID = "mono-wine-matrix"
    MONO_WINE_IMAGE = RUNTIME_MATRIX_MONO_WINE_IMAGE
    CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE
    SDK_IMAGE = BASE_DOTNET_SDK_IMAGE
    RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST
    RUNTIME_COMPONENT_SOURCE_URI = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    CONTROL_TFM = required(WINE_CONTROL_TFM)
  }
  labels = {
    "com.sharplabnext.runtime-candidate" = "true"
    "io.sharplabnext.source.context" = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    "com.sharplabnext.runtime-candidate.promotion-eligible" = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    "io.sharplabnext.runtime.matrix.profile-group" = "mono-wine-matrix"
    "io.sharplabnext.runtime.matrix.digest" = RUNTIME_MATRIX_RUNTIME_DIGEST
    "io.sharplabnext.runtime.matrix.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.control-image" = RUNTIME_MATRIX_CONTROL_IMAGE
    "io.sharplabnext.operator-image.mono-wine" = RUNTIME_MATRIX_MONO_WINE_IMAGE
    "io.sharplabnext.base-image.dotnet-sdk" = BASE_DOTNET_SDK_IMAGE
  }
}

target "runtime-wine-dotnet-matrix-candidate" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-wine-dotnet-matrix"
  tags = [
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:candidate",
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:${required(RELEASE_ID)}",
  ]
  args = {
    SDK_IMAGE = BASE_DOTNET_SDK_IMAGE
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CANDIDATE_SOURCE_CONTEXT = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    CANDIDATE_PROMOTION_ELIGIBLE = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    PROFILE_ID = RUNTIME_MATRIX_PROFILE_ID
    DOTNET_RUNTIME_VERSION = RUNTIME_MATRIX_RUNTIME_VERSION
    DOTNET_RUNTIME_COMMIT = RUNTIME_MATRIX_RUNTIME_COMMIT
    DOTNET_JIT_COMMIT = RUNTIME_MATRIX_JIT_COMMIT
    DOTNET_RUNTIME_URL = RUNTIME_MATRIX_WINDOWS_URL
    DOTNET_RUNTIME_SHA512 = RUNTIME_MATRIX_WINDOWS_SHA512
    WINE_IMAGE = WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG != "" ? WINE_CORECLR_DEVELOPMENT_OPERATOR_TAG : RUNTIME_MATRIX_WINE_IMAGE
    WINE_IDENTITY = RUNTIME_MATRIX_WINE_IMAGE
    ALLOW_DEVELOPMENT_IMAGE_ID = WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE
    ALLOW_DEVELOPMENT_LOCAL_TAG = WINE_CORECLR_DEVELOPMENT_OPERATOR_IMAGE
    CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE
    CONTROL_TFM = required(WINE_CONTROL_TFM)
  }
  labels = merge({
    "com.sharplabnext.runtime-candidate" = "true"
    "io.sharplabnext.source.context" = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    "com.sharplabnext.runtime-candidate.promotion-eligible" = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    "io.sharplabnext.component.runtime-matrix.profile-id" = RUNTIME_MATRIX_PROFILE_ID
    "io.sharplabnext.component.runtime-matrix.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.runtime-matrix.commit" = RUNTIME_MATRIX_RUNTIME_COMMIT
    "io.sharplabnext.component.runtime-matrix.jit-commit" = RUNTIME_MATRIX_JIT_COMMIT
    "io.sharplabnext.component.runtime-matrix.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.commit" = RUNTIME_MATRIX_RUNTIME_COMMIT
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.runtime.commit" = RUNTIME_MATRIX_RUNTIME_COMMIT
    "io.sharplabnext.jit.commit" = RUNTIME_MATRIX_JIT_COMMIT
    "io.sharplabnext.control-image" = RUNTIME_MATRIX_CONTROL_IMAGE
    "io.sharplabnext.operator-image.wine" = RUNTIME_MATRIX_WINE_IMAGE
    "io.sharplabnext.operator.root" = BASE_DOTNET_RUNTIME_DEPS_IMAGE
    "io.sharplabnext.component.wine-coreclr-userspace.version" = WINE_CORECLR_USERSPACE_VERSION
    "io.sharplabnext.component.wine-coreclr-userspace.digest" = WINE_CORECLR_USERSPACE_DIGEST
    "io.sharplabnext.component.wine-coreclr-userspace.source-uri" = WINE_CORECLR_USERSPACE_SOURCE_URI
    "io.sharplabnext.base-image.dotnet-sdk" = BASE_DOTNET_SDK_IMAGE
  }, WINE_CORECLR_OPERATOR_RECEIPT_SHA256 != "" ? {
    "io.sharplabnext.operator.receipt-sha256" = WINE_CORECLR_OPERATOR_RECEIPT_SHA256
    "io.sharplabnext.operator.receipt-key-id" = WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID
    "io.sharplabnext.operator.userspace-reference" = WINE_CORECLR_OPERATOR_REFERENCE
  } : {})
}

target "runtime-wine-framework-matrix-candidate" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-wine-framework-matrix"
  tags = [
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:candidate",
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:${required(RELEASE_ID)}",
  ]
  args = {
    SDK_IMAGE = BASE_DOTNET_SDK_IMAGE
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CANDIDATE_SOURCE_CONTEXT = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    CANDIDATE_PROMOTION_ELIGIBLE = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    PROFILE_ID = RUNTIME_MATRIX_PROFILE_ID
    NETFX_RUNTIME_VERSION = RUNTIME_MATRIX_RUNTIME_VERSION
    RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST
    RUNTIME_COMPONENT_SOURCE_URI = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    WINE_IMAGE = RUNTIME_MATRIX_WINE_IMAGE
    CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE
    CONTROL_TFM = required(WINE_CONTROL_TFM)
  }
  labels = {
    "com.sharplabnext.runtime-candidate" = "true"
    "io.sharplabnext.source.context" = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    "com.sharplabnext.runtime-candidate.promotion-eligible" = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    "io.sharplabnext.component.runtime-matrix.profile-id" = RUNTIME_MATRIX_PROFILE_ID
    "io.sharplabnext.component.runtime-matrix.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.runtime-matrix.digest" = RUNTIME_MATRIX_RUNTIME_DIGEST
    "io.sharplabnext.component.runtime-matrix.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.digest" = RUNTIME_MATRIX_RUNTIME_DIGEST
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.control-image" = RUNTIME_MATRIX_CONTROL_IMAGE
    "io.sharplabnext.operator-image.wine" = RUNTIME_MATRIX_WINE_IMAGE
    "io.sharplabnext.operator.root" = BASE_DOTNET_RUNTIME_DEPS_IMAGE
    "io.sharplabnext.operator.receipt-sha256" = WINE_CORECLR_OPERATOR_RECEIPT_SHA256
    "io.sharplabnext.operator.receipt-key-id" = WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID
    "io.sharplabnext.operator.userspace-reference" = WINE_CORECLR_OPERATOR_REFERENCE
    "io.sharplabnext.base-image.dotnet-sdk" = BASE_DOTNET_SDK_IMAGE
  }
}

target "runtime-wine-framework-matrix-shared-candidate" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-wine-framework-matrix-shared"
  tags = [
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:candidate",
    "${required(IMAGE_PREFIX)}/runtime-${RUNTIME_MATRIX_PROFILE_ID}:${required(RELEASE_ID)}",
  ]
  args = {
    SDK_IMAGE = BASE_DOTNET_SDK_IMAGE
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CANDIDATE_SOURCE_CONTEXT = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    CANDIDATE_PROMOTION_ELIGIBLE = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    PROFILE_ID = RUNTIME_MATRIX_PROFILE_ID
    FRAMEWORK_TARGET_ID = RUNTIME_MATRIX_FRAMEWORK_TARGET_ID
    FRAMEWORK_RUNTIME_VERSION = RUNTIME_MATRIX_RUNTIME_VERSION
    FRAMEWORK_CLR_GENERATION = RUNTIME_MATRIX_FRAMEWORK_CLR_GENERATION
    RUNTIME_COMPONENT_DIGEST = RUNTIME_MATRIX_RUNTIME_DIGEST
    RUNTIME_COMPONENT_SOURCE_URI = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    PARENT_IMAGE = RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE
    FRAMEWORK_SOURCE_REVISION = RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION
    WINE_IMAGE = RUNTIME_MATRIX_WINE_IMAGE
    CONTROL_IMAGE = RUNTIME_MATRIX_CONTROL_IMAGE
    FRAMEWORK_MATRIX_INPUT_SHA256 = RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256
    FRAMEWORK_MATRIX_SOURCE_URI = RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI
    FRAMEWORK_ROW_OPERATOR_IMAGE = RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE
    FRAMEWORK_ROW_DIGEST = RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST
    HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT = RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT
    CONTROL_TFM = required(WINE_CONTROL_TFM)
  }
  labels = merge({
    "com.sharplabnext.runtime-candidate" = "true"
    "io.sharplabnext.source.context" = RUNTIME_CANDIDATE_SOURCE_CONTEXT
    "com.sharplabnext.runtime-candidate.promotion-eligible" = RUNTIME_CANDIDATE_PROMOTION_ELIGIBLE
    "io.sharplabnext.component.runtime-matrix.profile-id" = RUNTIME_MATRIX_PROFILE_ID
    "io.sharplabnext.component.runtime-matrix.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.runtime-matrix.digest" = RUNTIME_MATRIX_RUNTIME_DIGEST
    "io.sharplabnext.component.runtime-matrix.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.version" = RUNTIME_MATRIX_RUNTIME_VERSION
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.digest" = RUNTIME_MATRIX_RUNTIME_DIGEST
    "io.sharplabnext.component.${RUNTIME_MATRIX_PROFILE_ID}.source-uri" = RUNTIME_MATRIX_RUNTIME_SOURCE_URI
    "io.sharplabnext.control-image" = RUNTIME_MATRIX_CONTROL_IMAGE
    "io.sharplabnext.operator-image.wine" = RUNTIME_MATRIX_WINE_IMAGE
    "io.sharplabnext.operator.root" = BASE_DOTNET_RUNTIME_DEPS_IMAGE
    "io.sharplabnext.framework.matrix-parent" = RUNTIME_MATRIX_FRAMEWORK_PARENT_IMAGE
    "io.sharplabnext.framework.source-revision" = RUNTIME_MATRIX_FRAMEWORK_SOURCE_REVISION
    "io.sharplabnext.framework.matrix-input-sha256" = RUNTIME_MATRIX_FRAMEWORK_MATRIX_INPUT_SHA256
    "io.sharplabnext.framework.matrix-source-uri" = RUNTIME_MATRIX_FRAMEWORK_MATRIX_SOURCE_URI
    "io.sharplabnext.framework.row-operator-image" = RUNTIME_MATRIX_FRAMEWORK_ROW_OPERATOR_IMAGE
    "io.sharplabnext.framework.row-digest" = RUNTIME_MATRIX_FRAMEWORK_ROW_DIGEST
    "io.sharplabnext.framework.matrix-selector" = "true"
    "io.sharplabnext.base-image.dotnet-sdk" = BASE_DOTNET_SDK_IMAGE
  }, RUNTIME_MATRIX_HISTORICAL_FRAMEWORK_INPUT_FOR_DEVELOPMENT != "true" ? {
    "io.sharplabnext.operator.receipt-sha256" = WINE_CORECLR_OPERATOR_RECEIPT_SHA256
    "io.sharplabnext.operator.receipt-key-id" = WINE_CORECLR_OPERATOR_RECEIPT_KEY_ID
    "io.sharplabnext.operator.userspace-reference" = WINE_CORECLR_OPERATOR_REFERENCE
    "io.sharplabnext.component.wine-coreclr-userspace.version" = WINE_CORECLR_USERSPACE_VERSION
    "io.sharplabnext.component.wine-coreclr-userspace.digest" = WINE_CORECLR_USERSPACE_DIGEST
    "io.sharplabnext.component.wine-coreclr-userspace.source-uri" = WINE_CORECLR_USERSPACE_SOURCE_URI
  } : {})
}
