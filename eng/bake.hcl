variable "RELEASE_ID" {
  default = ""
}

variable "IMAGE_PREFIX" {
  default = ""
}

variable "SOURCE_REVISION" {
  default = ""
}

variable "SOURCE_DATE_EPOCH" {
  default = ""
}

function "required" {
  params = [value]
  result = regex(".+", value)
}

# These image inputs are produced earlier in the same release build.
# Bake evaluates every target while loading this file, so unrelated early
# targets need a non-network placeholder until the real digest is injected.
function "deferred_image" {
  params = [value]
  result = value != "" ? value : "scratch"
}

function "unix_seconds" {
  params = [value]
  result = regex("^[0-9]+$", value)
}

variable "BASE_NODE_IMAGE" {
  default = ""
}

variable "BASE_DOTNET_SDK_IMAGE" {
  default = ""
}

variable "BASE_DOTNET_ASPNET_IMAGE" {
  default = ""
}

variable "BASE_CONST_GENERICS_ASPNET_IMAGE" {
  default = ""
}

variable "BASE_DOTNET_RUNTIME_DEPS_IMAGE" {
  default = ""
}

variable "BASE_DOTNET_RUNTIME_BUILD_IMAGE" {
  default = ""
}

variable "BASE_MONO_JSIL_IMAGE" {
  default = ""
}

# Framework selected for the shared Wine control-plane bridge.  This is
# injected by BakeEnvironmentResolver so the Dockerfiles never carry a
# mutable version default.
variable "WINE_CONTROL_TFM" {
  default = ""
}

variable "ARTIFACTS_JSIL_VERSION" {
  default = ""
}

variable "ARTIFACTS_JSIL_COMMIT" {
  default = ""
}

variable "ARTIFACTS_JSIL_DIGEST" {
  default = ""
}

variable "ARTIFACTS_JSIL_SOURCE_URI" {
  default = ""
}

variable "JSIL_VERSION" {
  default = ""
}

variable "JSIL_COMMIT" {
  default = ""
}

variable "JSIL_ARCHIVE_URL" {
  default = ""
}

variable "JSIL_ARCHIVE_SHA256" {
  default = ""
}

variable "JSIL_META_COMMIT" {
  default = ""
}

variable "JSIL_META_VERSION" {
  default = ""
}

variable "JSIL_META_ARCHIVE_URL" {
  default = ""
}

variable "JSIL_META_ARCHIVE_SHA256" {
  default = ""
}

variable "JSIL_ILSPY_COMMIT" {
  default = ""
}

variable "JSIL_ILSPY_VERSION" {
  default = ""
}

variable "JSIL_ILSPY_ARCHIVE_URL" {
  default = ""
}

variable "JSIL_ILSPY_ARCHIVE_SHA256" {
  default = ""
}

variable "JSIL_NREFACTORY_COMMIT" {
  default = ""
}

variable "JSIL_NREFACTORY_VERSION" {
  default = ""
}

variable "JSIL_NREFACTORY_ARCHIVE_URL" {
  default = ""
}

variable "JSIL_NREFACTORY_ARCHIVE_SHA256" {
  default = ""
}

variable "JSIL_CECIL_COMMIT" {
  default = ""
}

variable "JSIL_CECIL_VERSION" {
  default = ""
}

variable "JSIL_CECIL_ARCHIVE_URL" {
  default = ""
}

variable "JSIL_CECIL_ARCHIVE_SHA256" {
  default = ""
}

variable "ROSLYN_STABLE_VERSION" {
  default = ""
}

variable "ROSLYN_STABLE_SOURCE_URI" {
  default = ""
}

variable "ROSLYN_MAIN_VERSION" {
  default = ""
}

variable "ROSLYN_MAIN_COMMIT" {
  default = ""
}

variable "ROSLYN_MAIN_ARCHIVE_URL" {
  default = ""
}

variable "ROSLYN_MAIN_ARCHIVE_SHA256" {
  default = ""
}

variable "ROSLYN_MAIN_SOURCE_URI" {
  default = ""
}

variable "FSHARP_COMPILER_SERVICE_VERSION" {
  default = ""
}

variable "FSHARP_COMPILER_SERVICE_SOURCE_URI" {
  default = ""
}

variable "FSHARP_CORE_VERSION" {
  default = ""
}

variable "FSHARP_CORE_SOURCE_URI" {
  default = ""
}

variable "GSHARP_VERSION" {
  default = ""
}

variable "GSHARP_COMMIT" {
  default = ""
}

variable "GSHARP_ARCHIVE_URL" {
  default = ""
}

variable "GSHARP_ARCHIVE_SHA256" {
  default = ""
}

variable "GSHARP_SOURCE_URI" {
  default = ""
}

variable "GSHARP_LEGACY_VERSION" {
  default = ""
}

variable "GSHARP_LEGACY_COMMIT" {
  default = ""
}

variable "GSHARP_LEGACY_ARCHIVE_URL" {
  default = ""
}

variable "GSHARP_LEGACY_ARCHIVE_SHA256" {
  default = ""
}

variable "GSHARP_LEGACY_SOURCE_URI" {
  default = ""
}

variable "PEACHPIE_CODEANALYSIS_VERSION" {
  default = ""
}

variable "PEACHPIE_CODEANALYSIS_URL" {
  default = ""
}

variable "PEACHPIE_CODEANALYSIS_SHA512" {
  default = ""
}

variable "PEACHPIE_CODEANALYSIS_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "PEACHPIE_CODEANALYSIS_SOURCE_URI" {
  default = ""
}

variable "PEACHPIE_RUNTIME_VERSION" {
  default = ""
}

variable "PEACHPIE_RUNTIME_URL" {
  default = ""
}

variable "PEACHPIE_RUNTIME_SHA512" {
  default = ""
}

variable "PEACHPIE_RUNTIME_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "PEACHPIE_RUNTIME_SOURCE_URI" {
  default = ""
}

variable "PEACHPIE_LIBRARY_VERSION" {
  default = ""
}

variable "PEACHPIE_LIBRARY_URL" {
  default = ""
}

variable "PEACHPIE_LIBRARY_SHA512" {
  default = ""
}

variable "PEACHPIE_LIBRARY_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "PEACHPIE_LIBRARY_SOURCE_URI" {
  default = ""
}

variable "PEACHPIE_COMMIT" {
  default = ""
}

variable "PEACHPIE_LICENSE_URL" {
  default = ""
}

variable "ILSPY_VERSION" {
  default = ""
}

variable "ILSPY_SOURCE_URI" {
  default = ""
}

variable "ILVERIFICATION_VERSION" {
  default = ""
}

variable "ILVERIFICATION_SOURCE_URI" {
  default = ""
}

variable "MOBIUS_ILASM_VERSION" {
  default = ""
}

variable "MOBIUS_ILASM_SOURCE_URI" {
  default = ""
}

variable "ILSENSE_VERSION" {
  default = ""
}

variable "ILSENSE_COMMIT" {
  default = ""
}

variable "ILSENSE_ARCHIVE_URL" {
  default = ""
}

variable "ILSENSE_ARCHIVE_SHA256" {
  default = ""
}

variable "ILSENSE_SOURCE_URI" {
  default = ""
}

variable "NET10_REFERENCE_PACK_VERSION" {
  default = ""
}

variable "NET10_REFERENCE_URL" {
  default = ""
}

variable "NET10_REFERENCE_SHA512" {
  default = ""
}

variable "NET10_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NET10_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NET11_REFERENCE_VERSION" {
  default = ""
}

variable "NET11_REFERENCE_URL" {
  default = ""
}

variable "NET11_REFERENCE_SHA512" {
  default = ""
}

variable "NET11_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NET11_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETCOREAPP20_REFERENCE_VERSION" {
  default = ""
}

variable "NETCOREAPP20_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETCOREAPP20_REFERENCE_SHA512" {
  default = ""
}

variable "NETCOREAPP20_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NETCOREAPP21_REFERENCE_VERSION" {
  default = ""
}

variable "NETCOREAPP21_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETCOREAPP21_REFERENCE_SHA512" {
  default = ""
}

variable "NETCOREAPP21_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NETCOREAPP22_REFERENCE_VERSION" {
  default = ""
}

variable "NETCOREAPP22_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETCOREAPP22_REFERENCE_SHA512" {
  default = ""
}

variable "NETCOREAPP22_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NETCOREAPP30_REFERENCE_VERSION" {
  default = ""
}

variable "NETCOREAPP30_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETCOREAPP30_REFERENCE_SHA512" {
  default = ""
}

variable "NETCOREAPP30_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NETCOREAPP31_REFERENCE_VERSION" {
  default = ""
}

variable "NETCOREAPP31_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETCOREAPP31_REFERENCE_SHA512" {
  default = ""
}

variable "NETCOREAPP31_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NET5_REFERENCE_VERSION" {
  default = ""
}

variable "NET5_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NET5_REFERENCE_SHA512" {
  default = ""
}

variable "NET5_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NET6_REFERENCE_VERSION" {
  default = ""
}

variable "NET6_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NET6_REFERENCE_SHA512" {
  default = ""
}

variable "NET6_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NET7_REFERENCE_VERSION" {
  default = ""
}

variable "NET7_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NET7_REFERENCE_SHA512" {
  default = ""
}

variable "NET7_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NET8_REFERENCE_VERSION" {
  default = ""
}

variable "NET8_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NET8_REFERENCE_SHA512" {
  default = ""
}

variable "NET8_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NET9_REFERENCE_VERSION" {
  default = ""
}

variable "NET9_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NET9_REFERENCE_SHA512" {
  default = ""
}

variable "NET9_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "DOTNET10_RUNTIME_VERSION" {
  default = ""
}

variable "DOTNET10_RUNTIME_COMMIT" {
  default = ""
}

variable "DOTNET10_JIT_COMMIT" {
  default = ""
}

variable "DOTNET10_RUNTIME_URL" {
  default = ""
}

variable "DOTNET10_RUNTIME_SHA512" {
  default = ""
}

variable "DOTNET10_RUNTIME_SOURCE_URI" {
  default = ""
}

variable "DOTNET11_RUNTIME_VERSION" {
  default = ""
}

variable "DOTNET11_RUNTIME_COMMIT" {
  default = ""
}

variable "DOTNET11_JIT_COMMIT" {
  default = ""
}

variable "DOTNET11_RUNTIME_URL" {
  default = ""
}

variable "DOTNET11_RUNTIME_SHA512" {
  default = ""
}

variable "DOTNET11_RUNTIME_SOURCE_URI" {
  default = ""
}

variable "JIT_PROFILER_CLR_SAMPLES_COMMIT" {
  default = ""
}

variable "JIT_PROFILER_CLR_SAMPLES_SOURCE_URI" {
  default = ""
}

variable "JIT_PROFILER_RUNTIME_HEADERS_COMMIT" {
  default = ""
}

variable "JIT_PROFILER_RUNTIME_HEADERS_SOURCE_URI" {
  default = ""
}

variable "CONST_GENERICS_RUNTIME_COMMIT" {
  default = ""
}

variable "CONST_GENERICS_RUNTIME_ARCHIVE_URL" {
  default = ""
}

variable "CONST_GENERICS_RUNTIME_ARCHIVE_SHA256" {
  default = ""
}

variable "CONST_GENERICS_RUNTIME_SOURCE_URI" {
  default = ""
}

variable "CONST_GENERICS_RUNTIME_VERSION" {
  default = ""
}

variable "CONST_GENERICS_VERSIONTOOLS_VERSION" {
  default = ""
}

variable "CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256" {
  default = ""
}

variable "CONST_GENERICS_VERSIONTOOLS_SOURCE_URI" {
  default = ""
}

variable "CONST_GENERICS_REFERENCE_VERSION" {
  default = ""
}

variable "CONST_GENERICS_REFERENCE_DIGEST" {
  default = ""
}

variable "CONST_GENERICS_ROSLYN_COMMIT" {
  default = ""
}

variable "CONST_GENERICS_ROSLYN_ARCHIVE_URL" {
  default = ""
}

variable "CONST_GENERICS_ROSLYN_ARCHIVE_SHA256" {
  default = ""
}

variable "CONST_GENERICS_ROSLYN_VERSION" {
  default = ""
}

variable "CONST_GENERICS_ROSLYN_SOURCE_URI" {
  default = ""
}

variable "CONST_GENERICS_ROSLYN_COMPONENT_VERSION" {
  default = ""
}

variable "CONST_GENERICS_ILSPY_COMMIT" {
  default = ""
}

variable "CONST_GENERICS_ILSPY_ARCHIVE_URL" {
  default = ""
}

variable "CONST_GENERICS_ILSPY_ARCHIVE_SHA256" {
  default = ""
}

variable "CONST_GENERICS_ILSPY_SOURCE_URI" {
  default = ""
}

variable "MINILANG_VERSION" {
  default = ""
}

variable "ARTIFACTS_DEFAULT_VERSION" {
  default = ""
}

variable "ARTIFACTS_CONST_GENERICS_VERSION" {
  default = ""
}

variable "IL_ASSEMBLER_VERSION" {
  default = ""
}

variable "CPPCLI_PREPARED_BASE_IMAGE" {
  default = ""
}

variable "JSHARP_TOOLCHAIN_IMAGE" {
  default = ""
}

variable "JSHARP_TOOLCHAIN_VERSION" {
  default = ""
}

variable "JSHARP_COMPILER_VERSION" {
  default = ""
}

variable "JSHARP_TOOLCHAIN_DIGEST" {
  default = ""
}

variable "JSHARP_TOOLCHAIN_SOURCE_URI" {
  default = ""
}

variable "JSHARP_REFERENCE_VERSION" {
  default = ""
}

variable "JSHARP_REFERENCE_DIGEST" {
  default = ""
}

variable "JSHARP_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "WINE_JSHARP20_RUNTIME_VERSION" {
  default = ""
}

variable "WINE_JSHARP20_RUNTIME_DIGEST" {
  default = ""
}

variable "WINE_JSHARP20_RUNTIME_SOURCE_URI" {
  default = ""
}

variable "CPPCLI_COMPILER_VERSION" {
  default = ""
}

variable "CPPCLI_TOOLCHAIN_DIGEST" {
  default = ""
}

variable "CPPCLI_TOOLCHAIN_SOURCE_URI" {
  default = ""
}

variable "MSVC_WINE_SOURCE_VERSION" {
  default = ""
}

variable "MSVC_WINE_SOURCE_COMMIT" {
  default = ""
}

variable "MSVC_WINE_SOURCE_DIGEST" {
  default = ""
}

variable "MSVC_WINE_SOURCE_URI" {
  default = ""
}

variable "NETFX48_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX48_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX48_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX48_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX48_MANAGED_REFERENCE_URL" {
  default = ""
}

variable "NETFX48_MANAGED_REFERENCE_SHA512" {
  default = ""
}

variable "NETFX48_MANAGED_REFERENCE_PACKAGE_CONTENT_HASH" {
  default = ""
}

variable "NETFX48_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX20_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX20_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX30_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX35_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX35_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX40_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX40_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX45_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX45_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX451_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX451_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX452_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX452_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX46_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX46_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX461_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX461_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX462_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX462_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX47_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX47_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX471_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX471_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX472_MANAGED_REFERENCE_VERSION" {
  default = ""
}

variable "NETFX472_MANAGED_REFERENCE_SOURCE_URI" {
  default = ""
}

variable "NETFX20_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX30_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX35_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX40_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX45_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX451_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX452_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX46_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX461_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX462_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX47_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX471_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX472_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "NETFX48_MANAGED_REFERENCE_DIGEST" {
  default = ""
}

variable "WINE_NETFX48_RUNTIME_VERSION" {
  default = ""
}

variable "WINE_NETFX48_RUNTIME_DIGEST" {
  default = ""
}

variable "WINE_NETFX48_RUNTIME_SOURCE_URI" {
  default = ""
}

variable "WINE_CORECLR_USERSPACE_VERSION" {
  default = ""
}

variable "WINE_CORECLR_USERSPACE_DIGEST" {
  default = ""
}

variable "WINE_CORECLR_USERSPACE_SOURCE_URI" {
  default = ""
}

# The Wine userspace operator has a dedicated committed-source build entry.
# These values are deliberately unset for direct Bake invocations so a dirty
# working tree cannot accidentally receive release-looking provenance labels.
variable "OPERATOR_SOURCE_CONTEXT" {
  default = ""
}

variable "OPERATOR_PROMOTION_ELIGIBLE" {
  default = ""
}

group "default" {
  targets = [
    "gateway",
    "artifact-store",
    "runtime-supervisor",
    "runtime-dotnet10",
    "runtime-dotnet11",
    "runtime-const-generics",
    "runtime-wine-netfx48",
    "runtime-wine-jsharp20",
    "worker-roslyn-stable",
    "worker-roslyn-netfx48",
    "worker-roslyn-main",
    "worker-roslyn-const-generics",
    "worker-fsharp",
    "worker-gsharp",
    "worker-peachpie",
    "worker-cppcli",
    "worker-jsharp",
    "worker-il",
    "worker-minilang",
    "worker-artifacts-default",
    "worker-artifacts-jsil",
    "worker-artifacts-const-generics",
    "worker-artifacts-il-assembler"
  ]
}

target "common" {
  context = "."
  platforms = ["linux/amd64"]
  # Timestamp rewriting and eager unpack are mutually exclusive in BuildKit.
  # The image is still registered in the local Docker store and can be saved
  # or run; Docker prepares its snapshot lazily on first container creation.
  output = ["type=docker,rewrite-timestamp=true,unpack=false"]
  # BuildKit's default provenance attestation contains per-invocation data,
  # which changes the loadable OCI index digest even for a fully cached build.
  # ReleaseBundleBuilder emits the checksummed SLSA provenance for the bundle.
  attest = ["type=provenance,disabled=true"]
  args = {
    SOURCE_DATE_EPOCH = unix_seconds(required(SOURCE_DATE_EPOCH))
    NODE_IMAGE = required(BASE_NODE_IMAGE)
    SDK_IMAGE = required(BASE_DOTNET_SDK_IMAGE)
    ASPNET_IMAGE = required(BASE_DOTNET_ASPNET_IMAGE)
    RUNTIME_DEPS_IMAGE = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)
    RUNTIME_BUILD_IMAGE = required(BASE_DOTNET_RUNTIME_BUILD_IMAGE)
  }
  labels = {
    "org.opencontainers.image.version" = required(RELEASE_ID)
    "org.opencontainers.image.revision" = required(SOURCE_REVISION)
    "org.opencontainers.image.source" = "https://github.com/ilyfairy/SharpLabNext"
    "io.sharplabnext.source.revision" = required(SOURCE_REVISION)
  }
}

target "operator-wine-coreclr" {
  context = "."
  platforms = ["linux/amd64"]
  output = ["type=docker,rewrite-timestamp=true,unpack=false"]
  attest = ["type=provenance,disabled=true"]
  dockerfile = "deploy/docker/Dockerfile.operator-wine-coreclr"
  target = "final"
  tags = ["${required(IMAGE_PREFIX)}/operator-wine-coreclr:${required(RELEASE_ID)}"]
  args = {
    SOURCE_DATE_EPOCH = unix_seconds(required(SOURCE_DATE_EPOCH))
    RUNTIME_DEPS_IMAGE = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)
    VERSION = RELEASE_ID
    SOURCE_REVISION = required(SOURCE_REVISION)
    WINE_CORECLR_USERSPACE_VERSION = required(WINE_CORECLR_USERSPACE_VERSION)
    WINE_CORECLR_USERSPACE_DIGEST = required(WINE_CORECLR_USERSPACE_DIGEST)
    WINE_CORECLR_USERSPACE_SOURCE_URI = required(WINE_CORECLR_USERSPACE_SOURCE_URI)
    OPERATOR_SOURCE_CONTEXT = required(OPERATOR_SOURCE_CONTEXT)
    OPERATOR_PROMOTION_ELIGIBLE = required(OPERATOR_PROMOTION_ELIGIBLE)
  }
  labels = {
    "org.opencontainers.image.version" = "wine-9.0-noble-amd64"
    "org.opencontainers.image.revision" = required(SOURCE_REVISION)
    "org.opencontainers.image.source" = "https://github.com/ilyfairy/SharpLabNext"
    "io.sharplabnext.source.revision" = required(SOURCE_REVISION)
    "io.sharplabnext.base-image.dotnet-runtime-deps" = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)
    "io.sharplabnext.component.wine-coreclr-userspace.version" = required(WINE_CORECLR_USERSPACE_VERSION)
    "io.sharplabnext.component.wine-coreclr-userspace.digest" = required(WINE_CORECLR_USERSPACE_DIGEST)
    "io.sharplabnext.component.wine-coreclr-userspace.source-uri" = required(WINE_CORECLR_USERSPACE_SOURCE_URI)
    "io.sharplabnext.source.context" = required(OPERATOR_SOURCE_CONTEXT)
    "com.sharplabnext.operator.promotion-eligible" = required(OPERATOR_PROMOTION_ELIGIBLE)
  }
}

target "runtime-dotnet10" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-dotnet"
  tags = ["${required(IMAGE_PREFIX)}/runtime-dotnet10:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    RUNTIME_PROFILE_ID = "dotnet-10-linux-x64"
    RUNTIME_TITLE = "SharpLabNext .NET 10 Runtime Job"
    DOTNET_ROLL_FORWARD = ""
    DOTNET_RUNTIME_VERSION = required(DOTNET10_RUNTIME_VERSION)
    DOTNET_RUNTIME_COMMIT = required(DOTNET10_RUNTIME_COMMIT)
    DOTNET_JIT_COMMIT = required(DOTNET10_JIT_COMMIT)
    DOTNET_RUNTIME_URL = required(DOTNET10_RUNTIME_URL)
    DOTNET_RUNTIME_SHA512 = required(DOTNET10_RUNTIME_SHA512)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-runtime-deps" = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)
    "io.sharplabnext.component.dotnet-10-linux-x64.version" = required(DOTNET10_RUNTIME_VERSION)
    "io.sharplabnext.component.dotnet-10-linux-x64.commit" = required(DOTNET10_RUNTIME_COMMIT)
    "io.sharplabnext.component.dotnet-10-linux-x64.source-uri" = required(DOTNET10_RUNTIME_SOURCE_URI)
    "io.sharplabnext.component.jit-profiler-clr-samples.commit" = required(JIT_PROFILER_CLR_SAMPLES_COMMIT)
    "io.sharplabnext.component.jit-profiler-clr-samples.source-uri" = required(JIT_PROFILER_CLR_SAMPLES_SOURCE_URI)
    "io.sharplabnext.component.jit-profiler-runtime-headers.commit" = required(JIT_PROFILER_RUNTIME_HEADERS_COMMIT)
    "io.sharplabnext.component.jit-profiler-runtime-headers.source-uri" = required(JIT_PROFILER_RUNTIME_HEADERS_SOURCE_URI)
    "io.sharplabnext.runtime.commit" = required(DOTNET10_RUNTIME_COMMIT)
    "io.sharplabnext.jit.commit" = required(DOTNET10_JIT_COMMIT)
  }
}

target "runtime-dotnet11" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-dotnet"
  tags = ["${required(IMAGE_PREFIX)}/runtime-dotnet11:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    RUNTIME_PROFILE_ID = "dotnet-11-preview-linux-x64"
    RUNTIME_TITLE = "SharpLabNext .NET 11 Preview Runtime Job"
    DOTNET_ROLL_FORWARD = "Major"
    DOTNET_RUNTIME_VERSION = required(DOTNET11_RUNTIME_VERSION)
    DOTNET_RUNTIME_COMMIT = required(DOTNET11_RUNTIME_COMMIT)
    DOTNET_JIT_COMMIT = required(DOTNET11_JIT_COMMIT)
    DOTNET_RUNTIME_URL = required(DOTNET11_RUNTIME_URL)
    DOTNET_RUNTIME_SHA512 = required(DOTNET11_RUNTIME_SHA512)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-runtime-deps" = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)
    "io.sharplabnext.component.dotnet-11-preview-linux-x64.version" = required(DOTNET11_RUNTIME_VERSION)
    "io.sharplabnext.component.dotnet-11-preview-linux-x64.commit" = required(DOTNET11_RUNTIME_COMMIT)
    "io.sharplabnext.component.dotnet-11-preview-linux-x64.source-uri" = required(DOTNET11_RUNTIME_SOURCE_URI)
    "io.sharplabnext.component.jit-profiler-clr-samples.commit" = required(JIT_PROFILER_CLR_SAMPLES_COMMIT)
    "io.sharplabnext.component.jit-profiler-clr-samples.source-uri" = required(JIT_PROFILER_CLR_SAMPLES_SOURCE_URI)
    "io.sharplabnext.component.jit-profiler-runtime-headers.commit" = required(JIT_PROFILER_RUNTIME_HEADERS_COMMIT)
    "io.sharplabnext.component.jit-profiler-runtime-headers.source-uri" = required(JIT_PROFILER_RUNTIME_HEADERS_SOURCE_URI)
    "io.sharplabnext.runtime.commit" = required(DOTNET11_RUNTIME_COMMIT)
    "io.sharplabnext.jit.commit" = required(DOTNET11_JIT_COMMIT)
  }
}

target "runtime-const-generics" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-const-generics"
  tags = ["${required(IMAGE_PREFIX)}/runtime-const-generics:${required(RELEASE_ID)}"]
  contexts = {
    "const-generics-fork-packages" = "./artifacts/prerequisites/downloads/const-generics-fork-packages"
  }
  args = {
    VERSION = RELEASE_ID
    CONST_GENERICS_RUNTIME_COMMIT = required(CONST_GENERICS_RUNTIME_COMMIT)
    CONST_GENERICS_RUNTIME_ARCHIVE_URL = required(CONST_GENERICS_RUNTIME_ARCHIVE_URL)
    CONST_GENERICS_RUNTIME_ARCHIVE_SHA256 = required(CONST_GENERICS_RUNTIME_ARCHIVE_SHA256)
    CONST_GENERICS_VERSIONTOOLS_VERSION = required(CONST_GENERICS_VERSIONTOOLS_VERSION)
    CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256 = required(CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256)
    CONST_GENERICS_VERSIONTOOLS_SOURCE_URI = required(CONST_GENERICS_VERSIONTOOLS_SOURCE_URI)
    CONST_GENERICS_REFERENCE_VERSION = required(CONST_GENERICS_REFERENCE_VERSION)
    CONST_GENERICS_REFERENCE_DIGEST = required(CONST_GENERICS_REFERENCE_DIGEST)
  }
  labels = {
    "org.opencontainers.image.revision" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "org.opencontainers.image.source" = required(CONST_GENERICS_RUNTIME_SOURCE_URI)
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-runtime-deps" = required(BASE_DOTNET_RUNTIME_DEPS_IMAGE)
    "io.sharplabnext.base-image.dotnet-runtime-build" = required(BASE_DOTNET_RUNTIME_BUILD_IMAGE)
    "io.sharplabnext.component.const-generics-linux-x64.version" = required(CONST_GENERICS_RUNTIME_VERSION)
    "io.sharplabnext.component.const-generics-linux-x64.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-linux-x64.source-uri" = required(CONST_GENERICS_RUNTIME_SOURCE_URI)
    "io.sharplabnext.component.const-generics-runtime-source.version" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-runtime-source.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-runtime-source.digest" = "sha256:${required(CONST_GENERICS_RUNTIME_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.const-generics-runtime-source.source-uri" = required(CONST_GENERICS_RUNTIME_ARCHIVE_URL)
    "io.sharplabnext.component.const-generics-ref.version" = required(CONST_GENERICS_REFERENCE_VERSION)
    "io.sharplabnext.component.const-generics-ref.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-ref.digest" = required(CONST_GENERICS_REFERENCE_DIGEST)
    "io.sharplabnext.component.const-generics-ref.source-uri" = required(CONST_GENERICS_RUNTIME_ARCHIVE_URL)
    "io.sharplabnext.component.const-generics-versiontools.version" = required(CONST_GENERICS_VERSIONTOOLS_VERSION)
    "io.sharplabnext.component.const-generics-versiontools.digest" = "sha256:${required(CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256)}"
    "io.sharplabnext.component.const-generics-versiontools.source-uri" = required(CONST_GENERICS_VERSIONTOOLS_SOURCE_URI)
    "io.sharplabnext.component.jit-profiler-clr-samples.commit" = required(JIT_PROFILER_CLR_SAMPLES_COMMIT)
    "io.sharplabnext.component.jit-profiler-clr-samples.source-uri" = required(JIT_PROFILER_CLR_SAMPLES_SOURCE_URI)
    "io.sharplabnext.component.jit-profiler-runtime-headers.commit" = required(JIT_PROFILER_RUNTIME_HEADERS_COMMIT)
    "io.sharplabnext.component.jit-profiler-runtime-headers.source-uri" = required(JIT_PROFILER_RUNTIME_HEADERS_SOURCE_URI)
    "io.sharplabnext.runtime.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.jit.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.reference-set.const-generics-ref" = required(CONST_GENERICS_REFERENCE_DIGEST)
  }
}

target "runtime-wine-netfx48" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-wine-netfx48"
  tags = ["${required(IMAGE_PREFIX)}/runtime-wine-netfx48:${required(RELEASE_ID)}"]
  contexts = {
    "cppcli-prepared-base-context" = "docker-image://${deferred_image(CPPCLI_PREPARED_BASE_IMAGE)}"
  }
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CONTROL_TFM = required(WINE_CONTROL_TFM)
    NETFX_RUNTIME_VERSION = required(WINE_NETFX48_RUNTIME_VERSION)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.wine-netfx48-linux-x64.version" = required(WINE_NETFX48_RUNTIME_VERSION)
    "io.sharplabnext.component.wine-netfx48-linux-x64.digest" = required(WINE_NETFX48_RUNTIME_DIGEST)
    "io.sharplabnext.component.wine-netfx48-linux-x64.source-uri" = required(WINE_NETFX48_RUNTIME_SOURCE_URI)
    "io.sharplabnext.component.msvc-cppcli-netfx48.version" = required(CPPCLI_COMPILER_VERSION)
    "io.sharplabnext.component.msvc-cppcli-netfx48.digest" = required(CPPCLI_TOOLCHAIN_DIGEST)
    "io.sharplabnext.component.msvc-cppcli-netfx48.source-uri" = required(CPPCLI_TOOLCHAIN_SOURCE_URI)
    "io.sharplabnext.component.msvc-wine-source.version" = required(MSVC_WINE_SOURCE_VERSION)
    "io.sharplabnext.component.msvc-wine-source.commit" = required(MSVC_WINE_SOURCE_COMMIT)
    "io.sharplabnext.component.msvc-wine-source.digest" = required(MSVC_WINE_SOURCE_DIGEST)
    "io.sharplabnext.component.msvc-wine-source.source-uri" = required(MSVC_WINE_SOURCE_URI)
    "io.sharplabnext.component.netfx48-ref.version" = required(NETFX48_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx48-ref.digest" = required(NETFX48_REFERENCE_DIGEST)
    "io.sharplabnext.component.netfx48-ref.source-uri" = required(NETFX48_REFERENCE_SOURCE_URI)
  }
}

target "jsharp-wine-base" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-wine-jsharp20"
  target = "built-jsharp-wine-base"
  tags = ["${required(IMAGE_PREFIX)}/jsharp-wine-base:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    JSHARP_TOOLCHAIN_IMAGE = deferred_image(JSHARP_TOOLCHAIN_IMAGE)
    JSHARP_TOOLCHAIN_VERSION = required(JSHARP_TOOLCHAIN_VERSION)
    JSHARP_COMPILER_VERSION = required(JSHARP_COMPILER_VERSION)
    JSHARP_TOOLCHAIN_DIGEST = required(JSHARP_TOOLCHAIN_DIGEST)
    JSHARP_TOOLCHAIN_SOURCE_URI = required(JSHARP_TOOLCHAIN_SOURCE_URI)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.jsharp20.version" = required(JSHARP_TOOLCHAIN_VERSION)
    "io.sharplabnext.component.jsharp20.digest" = required(JSHARP_TOOLCHAIN_DIGEST)
    "io.sharplabnext.component.jsharp20.source-uri" = required(JSHARP_TOOLCHAIN_SOURCE_URI)
    "io.sharplabnext.component.vjc-jsharp20.version" = required(JSHARP_COMPILER_VERSION)
  }
}

target "runtime-wine-jsharp20" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.runtime-wine-jsharp20"
  tags = ["${required(IMAGE_PREFIX)}/runtime-wine-jsharp20:${required(RELEASE_ID)}"]
  contexts = {
    "jsharp-wine-base-context" = "target:jsharp-wine-base"
  }
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    # The shared Dockerfile declares the toolchain in a global FROM. Bind it
    # for frontend parsing; the final runtime consumes target:jsharp-wine-base.
    JSHARP_TOOLCHAIN_IMAGE = deferred_image(JSHARP_TOOLCHAIN_IMAGE)
    JSHARP_TOOLCHAIN_VERSION = required(JSHARP_TOOLCHAIN_VERSION)
    JSHARP_COMPILER_VERSION = required(JSHARP_COMPILER_VERSION)
    JSHARP_TOOLCHAIN_DIGEST = required(JSHARP_TOOLCHAIN_DIGEST)
    JSHARP_TOOLCHAIN_SOURCE_URI = required(JSHARP_TOOLCHAIN_SOURCE_URI)
    WINE_JSHARP20_RUNTIME_VERSION = required(WINE_JSHARP20_RUNTIME_VERSION)
    WINE_JSHARP20_RUNTIME_DIGEST = required(WINE_JSHARP20_RUNTIME_DIGEST)
    WINE_JSHARP20_RUNTIME_SOURCE_URI = required(WINE_JSHARP20_RUNTIME_SOURCE_URI)
    CONTROL_TFM = required(WINE_CONTROL_TFM)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.jsharp20.version" = required(JSHARP_TOOLCHAIN_VERSION)
    "io.sharplabnext.component.jsharp20.digest" = required(JSHARP_TOOLCHAIN_DIGEST)
    "io.sharplabnext.component.jsharp20.source-uri" = required(JSHARP_TOOLCHAIN_SOURCE_URI)
    "io.sharplabnext.component.vjc-jsharp20.version" = required(JSHARP_COMPILER_VERSION)
    "io.sharplabnext.component.wine-jsharp20-linux-x64.version" = required(WINE_JSHARP20_RUNTIME_VERSION)
    "io.sharplabnext.component.wine-jsharp20-linux-x64.digest" = required(WINE_JSHARP20_RUNTIME_DIGEST)
    "io.sharplabnext.component.wine-jsharp20-linux-x64.source-uri" = required(WINE_JSHARP20_RUNTIME_SOURCE_URI)
  }
}

target "gateway" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.gateway"
  tags = ["${required(IMAGE_PREFIX)}/gateway:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
  }
  labels = {
    "io.sharplabnext.base-image.node-builder" = required(BASE_NODE_IMAGE)
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
  }
}

target "service" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker"
  target = "final-without-reference-sets"
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    ROSLYN_STABLE_VERSION = required(ROSLYN_STABLE_VERSION)
    FSHARP_COMPILER_SERVICE_VERSION = required(FSHARP_COMPILER_SERVICE_VERSION)
    FSHARP_CORE_VERSION = required(FSHARP_CORE_VERSION)
    ILSPY_VERSION = required(ILSPY_VERSION)
    ILVERIFICATION_VERSION = required(ILVERIFICATION_VERSION)
    MOBIUS_ILASM_VERSION = required(MOBIUS_ILASM_VERSION)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
  }
}

target "service-with-reference-sets" {
  inherits = ["service"]
  target = "final-with-reference-sets"
  args = {
    NET10_REFERENCE_PACK_VERSION = required(NET10_REFERENCE_PACK_VERSION)
    NET10_REFERENCE_URL = required(NET10_REFERENCE_URL)
    NET10_REFERENCE_SHA512 = required(NET10_REFERENCE_SHA512)
    NET10_REFERENCE_PACKAGE_CONTENT_HASH = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
    NET11_REFERENCE_VERSION = required(NET11_REFERENCE_VERSION)
    NET11_REFERENCE_URL = required(NET11_REFERENCE_URL)
    NET11_REFERENCE_SHA512 = required(NET11_REFERENCE_SHA512)
    NET11_REFERENCE_PACKAGE_CONTENT_HASH = required(NET11_REFERENCE_PACKAGE_CONTENT_HASH)
  }
  labels = {
    "io.sharplabnext.component.net10-ref.version" = required(NET10_REFERENCE_PACK_VERSION)
    "io.sharplabnext.component.net10-ref.source-uri" = required(NET10_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net11-preview-ref.version" = required(NET11_REFERENCE_VERSION)
    "io.sharplabnext.component.net11-preview-ref.source-uri" = required(NET11_REFERENCE_SOURCE_URI)
    "io.sharplabnext.reference-set.net10-ref" = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.reference-set.net11-preview-ref" = required(NET11_REFERENCE_PACKAGE_CONTENT_HASH)
  }
}

# Roslyn is the only generic worker family that compiles against every
# selectable CoreCLR reference set. The worker Dockerfile materializes the
# complete closure from the candidate source lock; the two current channels
# remain explicit build arguments so the helper can reject a stale lock.
target "service-with-roslyn-coreclr-reference-sets" {
  inherits = ["service"]
  target = "final-with-roslyn-coreclr-reference-sets"
  args = {
    NETCOREAPP20_REFERENCE_VERSION = required(NETCOREAPP20_REFERENCE_VERSION)
    NETCOREAPP20_REFERENCE_SOURCE_URI = required(NETCOREAPP20_REFERENCE_SOURCE_URI)
    NETCOREAPP20_REFERENCE_SHA512 = required(NETCOREAPP20_REFERENCE_SHA512)
    NETCOREAPP20_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP20_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP21_REFERENCE_VERSION = required(NETCOREAPP21_REFERENCE_VERSION)
    NETCOREAPP21_REFERENCE_SOURCE_URI = required(NETCOREAPP21_REFERENCE_SOURCE_URI)
    NETCOREAPP21_REFERENCE_SHA512 = required(NETCOREAPP21_REFERENCE_SHA512)
    NETCOREAPP21_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP21_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP22_REFERENCE_VERSION = required(NETCOREAPP22_REFERENCE_VERSION)
    NETCOREAPP22_REFERENCE_SOURCE_URI = required(NETCOREAPP22_REFERENCE_SOURCE_URI)
    NETCOREAPP22_REFERENCE_SHA512 = required(NETCOREAPP22_REFERENCE_SHA512)
    NETCOREAPP22_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP22_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP30_REFERENCE_VERSION = required(NETCOREAPP30_REFERENCE_VERSION)
    NETCOREAPP30_REFERENCE_SOURCE_URI = required(NETCOREAPP30_REFERENCE_SOURCE_URI)
    NETCOREAPP30_REFERENCE_SHA512 = required(NETCOREAPP30_REFERENCE_SHA512)
    NETCOREAPP30_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP30_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP31_REFERENCE_VERSION = required(NETCOREAPP31_REFERENCE_VERSION)
    NETCOREAPP31_REFERENCE_SOURCE_URI = required(NETCOREAPP31_REFERENCE_SOURCE_URI)
    NETCOREAPP31_REFERENCE_SHA512 = required(NETCOREAPP31_REFERENCE_SHA512)
    NETCOREAPP31_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP31_REFERENCE_PACKAGE_CONTENT_HASH)
    NET5_REFERENCE_VERSION = required(NET5_REFERENCE_VERSION)
    NET5_REFERENCE_SOURCE_URI = required(NET5_REFERENCE_SOURCE_URI)
    NET5_REFERENCE_SHA512 = required(NET5_REFERENCE_SHA512)
    NET5_REFERENCE_PACKAGE_CONTENT_HASH = required(NET5_REFERENCE_PACKAGE_CONTENT_HASH)
    NET6_REFERENCE_VERSION = required(NET6_REFERENCE_VERSION)
    NET6_REFERENCE_SOURCE_URI = required(NET6_REFERENCE_SOURCE_URI)
    NET6_REFERENCE_SHA512 = required(NET6_REFERENCE_SHA512)
    NET6_REFERENCE_PACKAGE_CONTENT_HASH = required(NET6_REFERENCE_PACKAGE_CONTENT_HASH)
    NET7_REFERENCE_VERSION = required(NET7_REFERENCE_VERSION)
    NET7_REFERENCE_SOURCE_URI = required(NET7_REFERENCE_SOURCE_URI)
    NET7_REFERENCE_SHA512 = required(NET7_REFERENCE_SHA512)
    NET7_REFERENCE_PACKAGE_CONTENT_HASH = required(NET7_REFERENCE_PACKAGE_CONTENT_HASH)
    NET8_REFERENCE_VERSION = required(NET8_REFERENCE_VERSION)
    NET8_REFERENCE_SOURCE_URI = required(NET8_REFERENCE_SOURCE_URI)
    NET8_REFERENCE_SHA512 = required(NET8_REFERENCE_SHA512)
    NET8_REFERENCE_PACKAGE_CONTENT_HASH = required(NET8_REFERENCE_PACKAGE_CONTENT_HASH)
    NET9_REFERENCE_VERSION = required(NET9_REFERENCE_VERSION)
    NET9_REFERENCE_SOURCE_URI = required(NET9_REFERENCE_SOURCE_URI)
    NET9_REFERENCE_SHA512 = required(NET9_REFERENCE_SHA512)
    NET9_REFERENCE_PACKAGE_CONTENT_HASH = required(NET9_REFERENCE_PACKAGE_CONTENT_HASH)
    NET10_REFERENCE_PACK_VERSION = required(NET10_REFERENCE_PACK_VERSION)
    NET10_REFERENCE_URL = required(NET10_REFERENCE_URL)
    NET10_REFERENCE_SHA512 = required(NET10_REFERENCE_SHA512)
    NET10_REFERENCE_PACKAGE_CONTENT_HASH = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
    NET11_REFERENCE_VERSION = required(NET11_REFERENCE_VERSION)
    NET11_REFERENCE_URL = required(NET11_REFERENCE_URL)
    NET11_REFERENCE_SHA512 = required(NET11_REFERENCE_SHA512)
    NET11_REFERENCE_PACKAGE_CONTENT_HASH = required(NET11_REFERENCE_PACKAGE_CONTENT_HASH)
  }
  labels = {
    "io.sharplabnext.component.netcoreapp2.0-ref.version" = required(NETCOREAPP20_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp2.0-ref.source-uri" = required(NETCOREAPP20_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp2.0-ref.source-sha512" = required(NETCOREAPP20_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp2.0-ref" = required(NETCOREAPP20_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp2.1-ref.version" = required(NETCOREAPP21_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp2.1-ref.source-uri" = required(NETCOREAPP21_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp2.1-ref.source-sha512" = required(NETCOREAPP21_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp2.1-ref" = required(NETCOREAPP21_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp2.2-ref.version" = required(NETCOREAPP22_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp2.2-ref.source-uri" = required(NETCOREAPP22_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp2.2-ref.source-sha512" = required(NETCOREAPP22_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp2.2-ref" = required(NETCOREAPP22_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp3.0-ref.version" = required(NETCOREAPP30_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp3.0-ref.source-uri" = required(NETCOREAPP30_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp3.0-ref.source-sha512" = required(NETCOREAPP30_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp3.0-ref" = required(NETCOREAPP30_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp3.1-ref.version" = required(NETCOREAPP31_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp3.1-ref.source-uri" = required(NETCOREAPP31_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp3.1-ref.source-sha512" = required(NETCOREAPP31_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp3.1-ref" = required(NETCOREAPP31_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net5-ref.version" = required(NET5_REFERENCE_VERSION)
    "io.sharplabnext.component.net5-ref.source-uri" = required(NET5_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net5-ref.source-sha512" = required(NET5_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net5-ref" = required(NET5_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net6-ref.version" = required(NET6_REFERENCE_VERSION)
    "io.sharplabnext.component.net6-ref.source-uri" = required(NET6_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net6-ref.source-sha512" = required(NET6_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net6-ref" = required(NET6_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net7-ref.version" = required(NET7_REFERENCE_VERSION)
    "io.sharplabnext.component.net7-ref.source-uri" = required(NET7_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net7-ref.source-sha512" = required(NET7_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net7-ref" = required(NET7_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net8-ref.version" = required(NET8_REFERENCE_VERSION)
    "io.sharplabnext.component.net8-ref.source-uri" = required(NET8_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net8-ref.source-sha512" = required(NET8_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net8-ref" = required(NET8_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net9-ref.version" = required(NET9_REFERENCE_VERSION)
    "io.sharplabnext.component.net9-ref.source-uri" = required(NET9_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net9-ref.source-sha512" = required(NET9_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net9-ref" = required(NET9_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net10-ref.version" = required(NET10_REFERENCE_PACK_VERSION)
    "io.sharplabnext.component.net10-ref.source-uri" = required(NET10_REFERENCE_URL)
    "io.sharplabnext.component.net10-ref.source-sha512" = required(NET10_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net10-ref" = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net11-preview-ref.version" = required(NET11_REFERENCE_VERSION)
    "io.sharplabnext.component.net11-preview-ref.source-uri" = required(NET11_REFERENCE_URL)
    "io.sharplabnext.component.net11-preview-ref.source-sha512" = required(NET11_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net11-preview-ref" = required(NET11_REFERENCE_PACKAGE_CONTENT_HASH)
  }
}

target "service-with-framework-reference-sets" {
  inherits = ["service-with-reference-sets"]
  target = "final-with-framework-reference-sets"
  args = {
    NETFX48_MANAGED_REFERENCE_VERSION = required(NETFX48_MANAGED_REFERENCE_VERSION)
    NETFX48_MANAGED_REFERENCE_URL = required(NETFX48_MANAGED_REFERENCE_URL)
    NETFX48_MANAGED_REFERENCE_SHA512 = required(NETFX48_MANAGED_REFERENCE_SHA512)
    NETFX48_MANAGED_REFERENCE_PACKAGE_CONTENT_HASH = required(NETFX48_MANAGED_REFERENCE_PACKAGE_CONTENT_HASH)
    NETFX48_MANAGED_REFERENCE_SOURCE_URI = required(NETFX48_MANAGED_REFERENCE_SOURCE_URI)
    NETFX20_MANAGED_REFERENCE_VERSION = required(NETFX20_MANAGED_REFERENCE_VERSION)
    NETFX20_MANAGED_REFERENCE_SOURCE_URI = required(NETFX20_MANAGED_REFERENCE_SOURCE_URI)
    NETFX30_MANAGED_REFERENCE_VERSION = required(NETFX30_MANAGED_REFERENCE_VERSION)
    NETFX35_MANAGED_REFERENCE_VERSION = required(NETFX35_MANAGED_REFERENCE_VERSION)
    NETFX35_MANAGED_REFERENCE_SOURCE_URI = required(NETFX35_MANAGED_REFERENCE_SOURCE_URI)
    NETFX40_MANAGED_REFERENCE_VERSION = required(NETFX40_MANAGED_REFERENCE_VERSION)
    NETFX40_MANAGED_REFERENCE_SOURCE_URI = required(NETFX40_MANAGED_REFERENCE_SOURCE_URI)
    NETFX45_MANAGED_REFERENCE_VERSION = required(NETFX45_MANAGED_REFERENCE_VERSION)
    NETFX45_MANAGED_REFERENCE_SOURCE_URI = required(NETFX45_MANAGED_REFERENCE_SOURCE_URI)
    NETFX451_MANAGED_REFERENCE_VERSION = required(NETFX451_MANAGED_REFERENCE_VERSION)
    NETFX451_MANAGED_REFERENCE_SOURCE_URI = required(NETFX451_MANAGED_REFERENCE_SOURCE_URI)
    NETFX452_MANAGED_REFERENCE_VERSION = required(NETFX452_MANAGED_REFERENCE_VERSION)
    NETFX452_MANAGED_REFERENCE_SOURCE_URI = required(NETFX452_MANAGED_REFERENCE_SOURCE_URI)
    NETFX46_MANAGED_REFERENCE_VERSION = required(NETFX46_MANAGED_REFERENCE_VERSION)
    NETFX46_MANAGED_REFERENCE_SOURCE_URI = required(NETFX46_MANAGED_REFERENCE_SOURCE_URI)
    NETFX461_MANAGED_REFERENCE_VERSION = required(NETFX461_MANAGED_REFERENCE_VERSION)
    NETFX461_MANAGED_REFERENCE_SOURCE_URI = required(NETFX461_MANAGED_REFERENCE_SOURCE_URI)
    NETFX462_MANAGED_REFERENCE_VERSION = required(NETFX462_MANAGED_REFERENCE_VERSION)
    NETFX462_MANAGED_REFERENCE_SOURCE_URI = required(NETFX462_MANAGED_REFERENCE_SOURCE_URI)
    NETFX47_MANAGED_REFERENCE_VERSION = required(NETFX47_MANAGED_REFERENCE_VERSION)
    NETFX47_MANAGED_REFERENCE_SOURCE_URI = required(NETFX47_MANAGED_REFERENCE_SOURCE_URI)
    NETFX471_MANAGED_REFERENCE_VERSION = required(NETFX471_MANAGED_REFERENCE_VERSION)
    NETFX471_MANAGED_REFERENCE_SOURCE_URI = required(NETFX471_MANAGED_REFERENCE_SOURCE_URI)
    NETFX472_MANAGED_REFERENCE_VERSION = required(NETFX472_MANAGED_REFERENCE_VERSION)
    NETFX472_MANAGED_REFERENCE_SOURCE_URI = required(NETFX472_MANAGED_REFERENCE_SOURCE_URI)
    NETFX20_MANAGED_REFERENCE_DIGEST = required(NETFX20_MANAGED_REFERENCE_DIGEST)
    NETFX30_MANAGED_REFERENCE_DIGEST = required(NETFX30_MANAGED_REFERENCE_DIGEST)
    NETFX35_MANAGED_REFERENCE_DIGEST = required(NETFX35_MANAGED_REFERENCE_DIGEST)
    NETFX40_MANAGED_REFERENCE_DIGEST = required(NETFX40_MANAGED_REFERENCE_DIGEST)
    NETFX45_MANAGED_REFERENCE_DIGEST = required(NETFX45_MANAGED_REFERENCE_DIGEST)
    NETFX451_MANAGED_REFERENCE_DIGEST = required(NETFX451_MANAGED_REFERENCE_DIGEST)
    NETFX452_MANAGED_REFERENCE_DIGEST = required(NETFX452_MANAGED_REFERENCE_DIGEST)
    NETFX46_MANAGED_REFERENCE_DIGEST = required(NETFX46_MANAGED_REFERENCE_DIGEST)
    NETFX461_MANAGED_REFERENCE_DIGEST = required(NETFX461_MANAGED_REFERENCE_DIGEST)
    NETFX462_MANAGED_REFERENCE_DIGEST = required(NETFX462_MANAGED_REFERENCE_DIGEST)
    NETFX47_MANAGED_REFERENCE_DIGEST = required(NETFX47_MANAGED_REFERENCE_DIGEST)
    NETFX471_MANAGED_REFERENCE_DIGEST = required(NETFX471_MANAGED_REFERENCE_DIGEST)
    NETFX472_MANAGED_REFERENCE_DIGEST = required(NETFX472_MANAGED_REFERENCE_DIGEST)
    NETFX48_MANAGED_REFERENCE_DIGEST = required(NETFX48_MANAGED_REFERENCE_DIGEST)
  }
  labels = {
    "io.sharplabnext.component.netfx20-managed-ref.version" = required(NETFX20_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx20-managed-ref.source-uri" = required(NETFX20_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx30-managed-ref.version" = required(NETFX30_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx30-managed-ref.digest" = required(NETFX30_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.component.netfx35-managed-ref.version" = required(NETFX35_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx35-managed-ref.source-uri" = required(NETFX35_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx40-managed-ref.version" = required(NETFX40_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx40-managed-ref.source-uri" = required(NETFX40_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx45-managed-ref.version" = required(NETFX45_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx45-managed-ref.source-uri" = required(NETFX45_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx451-managed-ref.version" = required(NETFX451_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx451-managed-ref.source-uri" = required(NETFX451_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx452-managed-ref.version" = required(NETFX452_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx452-managed-ref.source-uri" = required(NETFX452_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx46-managed-ref.version" = required(NETFX46_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx46-managed-ref.source-uri" = required(NETFX46_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx461-managed-ref.version" = required(NETFX461_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx461-managed-ref.source-uri" = required(NETFX461_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx462-managed-ref.version" = required(NETFX462_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx462-managed-ref.source-uri" = required(NETFX462_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx47-managed-ref.version" = required(NETFX47_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx47-managed-ref.source-uri" = required(NETFX47_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx471-managed-ref.version" = required(NETFX471_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx471-managed-ref.source-uri" = required(NETFX471_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx472-managed-ref.version" = required(NETFX472_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx472-managed-ref.source-uri" = required(NETFX472_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx48-managed-ref.version" = required(NETFX48_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx48-managed-ref.source-uri" = required(NETFX48_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.reference-set.netfx20-managed-ref" = required(NETFX20_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx30-managed-ref" = required(NETFX30_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx35-managed-ref" = required(NETFX35_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx40-managed-ref" = required(NETFX40_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx45-managed-ref" = required(NETFX45_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx451-managed-ref" = required(NETFX451_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx452-managed-ref" = required(NETFX452_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx46-managed-ref" = required(NETFX46_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx461-managed-ref" = required(NETFX461_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx462-managed-ref" = required(NETFX462_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx47-managed-ref" = required(NETFX47_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx471-managed-ref" = required(NETFX471_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx472-managed-ref" = required(NETFX472_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx48-managed-ref" = required(NETFX48_MANAGED_REFERENCE_DIGEST)
  }
}

target "artifact-store" {
  inherits = ["service"]
  tags = ["${required(IMAGE_PREFIX)}/artifact-store:${required(RELEASE_ID)}"]
  args = {
    PROJECT_PATH = "src/ArtifactStore/SharpLabNext.ArtifactStore/SharpLabNext.ArtifactStore.csproj"
    ASSEMBLY_NAME = "SharpLabNext.ArtifactStore.dll"
    SERVICE_TITLE = "SharpLabNext Artifact Store"
  }
}

target "runtime-supervisor" {
  inherits = ["service"]
  tags = ["${required(IMAGE_PREFIX)}/runtime-supervisor:${required(RELEASE_ID)}"]
  args = {
    PROJECT_PATH = "src/Supervisor/SharpLabNext.RuntimeSupervisor/SharpLabNext.RuntimeSupervisor.csproj"
    ASSEMBLY_NAME = "SharpLabNext.RuntimeSupervisor.dll"
    SERVICE_TITLE = "SharpLabNext Runtime Supervisor"
    DOTNET10_RUNTIME_VERSION = required(DOTNET10_RUNTIME_VERSION)
    DOTNET10_RUNTIME_COMMIT = required(DOTNET10_RUNTIME_COMMIT)
    DOTNET10_JIT_COMMIT = required(DOTNET10_JIT_COMMIT)
    DOTNET11_RUNTIME_VERSION = required(DOTNET11_RUNTIME_VERSION)
    DOTNET11_RUNTIME_COMMIT = required(DOTNET11_RUNTIME_COMMIT)
    DOTNET11_JIT_COMMIT = required(DOTNET11_JIT_COMMIT)
    CONST_GENERICS_RUNTIME_VERSION = required(CONST_GENERICS_RUNTIME_VERSION)
    CONST_GENERICS_RUNTIME_COMMIT = required(CONST_GENERICS_RUNTIME_COMMIT)
    CONST_GENERICS_JIT_COMMIT = required(CONST_GENERICS_RUNTIME_COMMIT)
    WINE_NETFX48_RUNTIME_VERSION = required(WINE_NETFX48_RUNTIME_VERSION)
    WINE_JSHARP20_RUNTIME_VERSION = required(WINE_JSHARP20_RUNTIME_VERSION)
  }
}

target "worker-roslyn-stable" {
  inherits = ["service-with-roslyn-coreclr-reference-sets"]
  tags = ["${required(IMAGE_PREFIX)}/worker-roslyn-stable:${required(RELEASE_ID)}"]
  args = {
    PROJECT_PATH = "src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable.csproj"
    ASSEMBLY_NAME = "SharpLabNext.Worker.Roslyn.Stable.dll"
    SERVICE_TITLE = "SharpLabNext Roslyn Stable Worker"
  }
  labels = {
    "io.sharplabnext.component.roslyn-stable.version" = required(ROSLYN_STABLE_VERSION)
    "io.sharplabnext.component.roslyn-stable.source-uri" = required(ROSLYN_STABLE_SOURCE_URI)
  }
}

target "worker-roslyn-netfx48" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-roslyn-netfx48"
  tags = ["${required(IMAGE_PREFIX)}/worker-roslyn-netfx48:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    ROSLYN_STABLE_VERSION = required(ROSLYN_STABLE_VERSION)
    ROSLYN_STABLE_SOURCE_URI = required(ROSLYN_STABLE_SOURCE_URI)
    NETFX48_MANAGED_REFERENCE_VERSION = required(NETFX48_MANAGED_REFERENCE_VERSION)
    NETFX48_MANAGED_REFERENCE_URL = required(NETFX48_MANAGED_REFERENCE_URL)
    NETFX48_MANAGED_REFERENCE_SHA512 = required(NETFX48_MANAGED_REFERENCE_SHA512)
    NETFX48_MANAGED_REFERENCE_PACKAGE_CONTENT_HASH = required(NETFX48_MANAGED_REFERENCE_PACKAGE_CONTENT_HASH)
    NETFX48_MANAGED_REFERENCE_SOURCE_URI = required(NETFX48_MANAGED_REFERENCE_SOURCE_URI)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.roslyn-stable.version" = required(ROSLYN_STABLE_VERSION)
    "io.sharplabnext.component.roslyn-stable.source-uri" = required(ROSLYN_STABLE_SOURCE_URI)
    "io.sharplabnext.component.roslyn-stable-netfx48.version" = required(ROSLYN_STABLE_VERSION)
    "io.sharplabnext.component.roslyn-stable-netfx48.source-uri" = required(ROSLYN_STABLE_SOURCE_URI)
    "io.sharplabnext.component.netfx20-managed-ref.version" = required(NETFX20_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx20-managed-ref.source-uri" = required(NETFX20_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx30-managed-ref.version" = required(NETFX30_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx30-managed-ref.digest" = required(NETFX30_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.component.netfx35-managed-ref.version" = required(NETFX35_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx35-managed-ref.source-uri" = required(NETFX35_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx40-managed-ref.version" = required(NETFX40_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx40-managed-ref.source-uri" = required(NETFX40_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx45-managed-ref.version" = required(NETFX45_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx45-managed-ref.source-uri" = required(NETFX45_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx451-managed-ref.version" = required(NETFX451_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx451-managed-ref.source-uri" = required(NETFX451_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx452-managed-ref.version" = required(NETFX452_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx452-managed-ref.source-uri" = required(NETFX452_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx46-managed-ref.version" = required(NETFX46_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx46-managed-ref.source-uri" = required(NETFX46_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx461-managed-ref.version" = required(NETFX461_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx461-managed-ref.source-uri" = required(NETFX461_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx462-managed-ref.version" = required(NETFX462_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx462-managed-ref.source-uri" = required(NETFX462_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx47-managed-ref.version" = required(NETFX47_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx47-managed-ref.source-uri" = required(NETFX47_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx471-managed-ref.version" = required(NETFX471_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx471-managed-ref.source-uri" = required(NETFX471_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx472-managed-ref.version" = required(NETFX472_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx472-managed-ref.source-uri" = required(NETFX472_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netfx48-managed-ref.version" = required(NETFX48_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx48-managed-ref.source-uri" = required(NETFX48_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.reference-set.netfx20-managed-ref" = required(NETFX20_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx30-managed-ref" = required(NETFX30_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx35-managed-ref" = required(NETFX35_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx40-managed-ref" = required(NETFX40_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx45-managed-ref" = required(NETFX45_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx451-managed-ref" = required(NETFX451_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx452-managed-ref" = required(NETFX452_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx46-managed-ref" = required(NETFX46_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx461-managed-ref" = required(NETFX461_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx462-managed-ref" = required(NETFX462_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx47-managed-ref" = required(NETFX47_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx471-managed-ref" = required(NETFX471_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx472-managed-ref" = required(NETFX472_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.reference-set.netfx48-managed-ref" = required(NETFX48_MANAGED_REFERENCE_DIGEST)
  }
}

target "worker-roslyn-main" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-roslyn-main"
  tags = ["${required(IMAGE_PREFIX)}/worker-roslyn-main:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    ROSLYN_MAIN_COMMIT = required(ROSLYN_MAIN_COMMIT)
    ROSLYN_MAIN_ARCHIVE_URL = required(ROSLYN_MAIN_ARCHIVE_URL)
    ROSLYN_MAIN_ARCHIVE_SHA256 = required(ROSLYN_MAIN_ARCHIVE_SHA256)
    ROSLYN_MAIN_VERSION = required(ROSLYN_MAIN_VERSION)
    NETCOREAPP20_REFERENCE_VERSION = required(NETCOREAPP20_REFERENCE_VERSION)
    NETCOREAPP20_REFERENCE_SOURCE_URI = required(NETCOREAPP20_REFERENCE_SOURCE_URI)
    NETCOREAPP20_REFERENCE_SHA512 = required(NETCOREAPP20_REFERENCE_SHA512)
    NETCOREAPP20_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP20_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP21_REFERENCE_VERSION = required(NETCOREAPP21_REFERENCE_VERSION)
    NETCOREAPP21_REFERENCE_SOURCE_URI = required(NETCOREAPP21_REFERENCE_SOURCE_URI)
    NETCOREAPP21_REFERENCE_SHA512 = required(NETCOREAPP21_REFERENCE_SHA512)
    NETCOREAPP21_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP21_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP22_REFERENCE_VERSION = required(NETCOREAPP22_REFERENCE_VERSION)
    NETCOREAPP22_REFERENCE_SOURCE_URI = required(NETCOREAPP22_REFERENCE_SOURCE_URI)
    NETCOREAPP22_REFERENCE_SHA512 = required(NETCOREAPP22_REFERENCE_SHA512)
    NETCOREAPP22_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP22_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP30_REFERENCE_VERSION = required(NETCOREAPP30_REFERENCE_VERSION)
    NETCOREAPP30_REFERENCE_SOURCE_URI = required(NETCOREAPP30_REFERENCE_SOURCE_URI)
    NETCOREAPP30_REFERENCE_SHA512 = required(NETCOREAPP30_REFERENCE_SHA512)
    NETCOREAPP30_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP30_REFERENCE_PACKAGE_CONTENT_HASH)
    NETCOREAPP31_REFERENCE_VERSION = required(NETCOREAPP31_REFERENCE_VERSION)
    NETCOREAPP31_REFERENCE_SOURCE_URI = required(NETCOREAPP31_REFERENCE_SOURCE_URI)
    NETCOREAPP31_REFERENCE_SHA512 = required(NETCOREAPP31_REFERENCE_SHA512)
    NETCOREAPP31_REFERENCE_PACKAGE_CONTENT_HASH = required(NETCOREAPP31_REFERENCE_PACKAGE_CONTENT_HASH)
    NET5_REFERENCE_VERSION = required(NET5_REFERENCE_VERSION)
    NET5_REFERENCE_SOURCE_URI = required(NET5_REFERENCE_SOURCE_URI)
    NET5_REFERENCE_SHA512 = required(NET5_REFERENCE_SHA512)
    NET5_REFERENCE_PACKAGE_CONTENT_HASH = required(NET5_REFERENCE_PACKAGE_CONTENT_HASH)
    NET6_REFERENCE_VERSION = required(NET6_REFERENCE_VERSION)
    NET6_REFERENCE_SOURCE_URI = required(NET6_REFERENCE_SOURCE_URI)
    NET6_REFERENCE_SHA512 = required(NET6_REFERENCE_SHA512)
    NET6_REFERENCE_PACKAGE_CONTENT_HASH = required(NET6_REFERENCE_PACKAGE_CONTENT_HASH)
    NET7_REFERENCE_VERSION = required(NET7_REFERENCE_VERSION)
    NET7_REFERENCE_SOURCE_URI = required(NET7_REFERENCE_SOURCE_URI)
    NET7_REFERENCE_SHA512 = required(NET7_REFERENCE_SHA512)
    NET7_REFERENCE_PACKAGE_CONTENT_HASH = required(NET7_REFERENCE_PACKAGE_CONTENT_HASH)
    NET8_REFERENCE_VERSION = required(NET8_REFERENCE_VERSION)
    NET8_REFERENCE_SOURCE_URI = required(NET8_REFERENCE_SOURCE_URI)
    NET8_REFERENCE_SHA512 = required(NET8_REFERENCE_SHA512)
    NET8_REFERENCE_PACKAGE_CONTENT_HASH = required(NET8_REFERENCE_PACKAGE_CONTENT_HASH)
    NET9_REFERENCE_VERSION = required(NET9_REFERENCE_VERSION)
    NET9_REFERENCE_SOURCE_URI = required(NET9_REFERENCE_SOURCE_URI)
    NET9_REFERENCE_SHA512 = required(NET9_REFERENCE_SHA512)
    NET9_REFERENCE_PACKAGE_CONTENT_HASH = required(NET9_REFERENCE_PACKAGE_CONTENT_HASH)
    NET10_REFERENCE_PACK_VERSION = required(NET10_REFERENCE_PACK_VERSION)
    NET10_REFERENCE_URL = required(NET10_REFERENCE_URL)
    NET10_REFERENCE_SHA512 = required(NET10_REFERENCE_SHA512)
    NET10_REFERENCE_PACKAGE_CONTENT_HASH = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
    NET11_REFERENCE_VERSION = required(NET11_REFERENCE_VERSION)
    NET11_REFERENCE_URL = required(NET11_REFERENCE_URL)
    NET11_REFERENCE_SHA512 = required(NET11_REFERENCE_SHA512)
    NET11_REFERENCE_PACKAGE_CONTENT_HASH = required(NET11_REFERENCE_PACKAGE_CONTENT_HASH)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.roslyn-main.version" = required(ROSLYN_MAIN_VERSION)
    "io.sharplabnext.component.roslyn-main.commit" = required(ROSLYN_MAIN_COMMIT)
    "io.sharplabnext.component.roslyn-main.digest" = "sha256:${required(ROSLYN_MAIN_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.roslyn-main.source-uri" = required(ROSLYN_MAIN_SOURCE_URI)
    "io.sharplabnext.component.net10-ref.version" = required(NET10_REFERENCE_PACK_VERSION)
    "io.sharplabnext.component.net10-ref.source-uri" = required(NET10_REFERENCE_URL)
    "io.sharplabnext.component.net10-ref.source-sha512" = required(NET10_REFERENCE_SHA512)
    "io.sharplabnext.component.net11-preview-ref.version" = required(NET11_REFERENCE_VERSION)
    "io.sharplabnext.component.net11-preview-ref.source-uri" = required(NET11_REFERENCE_URL)
    "io.sharplabnext.component.net11-preview-ref.source-sha512" = required(NET11_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net10-ref" = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.reference-set.net11-preview-ref" = required(NET11_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp2.0-ref.version" = required(NETCOREAPP20_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp2.0-ref.source-uri" = required(NETCOREAPP20_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp2.0-ref.source-sha512" = required(NETCOREAPP20_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp2.0-ref" = required(NETCOREAPP20_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp2.1-ref.version" = required(NETCOREAPP21_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp2.1-ref.source-uri" = required(NETCOREAPP21_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp2.1-ref.source-sha512" = required(NETCOREAPP21_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp2.1-ref" = required(NETCOREAPP21_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp2.2-ref.version" = required(NETCOREAPP22_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp2.2-ref.source-uri" = required(NETCOREAPP22_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp2.2-ref.source-sha512" = required(NETCOREAPP22_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp2.2-ref" = required(NETCOREAPP22_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp3.0-ref.version" = required(NETCOREAPP30_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp3.0-ref.source-uri" = required(NETCOREAPP30_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp3.0-ref.source-sha512" = required(NETCOREAPP30_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp3.0-ref" = required(NETCOREAPP30_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.netcoreapp3.1-ref.version" = required(NETCOREAPP31_REFERENCE_VERSION)
    "io.sharplabnext.component.netcoreapp3.1-ref.source-uri" = required(NETCOREAPP31_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.netcoreapp3.1-ref.source-sha512" = required(NETCOREAPP31_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.netcoreapp3.1-ref" = required(NETCOREAPP31_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net5-ref.version" = required(NET5_REFERENCE_VERSION)
    "io.sharplabnext.component.net5-ref.source-uri" = required(NET5_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net5-ref.source-sha512" = required(NET5_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net5-ref" = required(NET5_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net6-ref.version" = required(NET6_REFERENCE_VERSION)
    "io.sharplabnext.component.net6-ref.source-uri" = required(NET6_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net6-ref.source-sha512" = required(NET6_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net6-ref" = required(NET6_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net7-ref.version" = required(NET7_REFERENCE_VERSION)
    "io.sharplabnext.component.net7-ref.source-uri" = required(NET7_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net7-ref.source-sha512" = required(NET7_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net7-ref" = required(NET7_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net8-ref.version" = required(NET8_REFERENCE_VERSION)
    "io.sharplabnext.component.net8-ref.source-uri" = required(NET8_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net8-ref.source-sha512" = required(NET8_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net8-ref" = required(NET8_REFERENCE_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.net9-ref.version" = required(NET9_REFERENCE_VERSION)
    "io.sharplabnext.component.net9-ref.source-uri" = required(NET9_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net9-ref.source-sha512" = required(NET9_REFERENCE_SHA512)
    "io.sharplabnext.reference-set.net9-ref" = required(NET9_REFERENCE_PACKAGE_CONTENT_HASH)
  }
}

target "worker-roslyn-const-generics" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-roslyn-const-generics"
  tags = ["${required(IMAGE_PREFIX)}/worker-roslyn-const-generics:${required(RELEASE_ID)}"]
  contexts = {
    "const-generics-runtime" = "target:runtime-const-generics"
    "const-generics-fork-packages" = "./artifacts/prerequisites/downloads/const-generics-fork-packages"
  }
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    ASPNET_IMAGE = required(BASE_CONST_GENERICS_ASPNET_IMAGE)
    CONST_GENERICS_ROSLYN_COMMIT = required(CONST_GENERICS_ROSLYN_COMMIT)
    CONST_GENERICS_ROSLYN_ARCHIVE_URL = required(CONST_GENERICS_ROSLYN_ARCHIVE_URL)
    CONST_GENERICS_ROSLYN_ARCHIVE_SHA256 = required(CONST_GENERICS_ROSLYN_ARCHIVE_SHA256)
    CONST_GENERICS_ROSLYN_VERSION = required(CONST_GENERICS_ROSLYN_VERSION)
    CONST_GENERICS_RUNTIME_COMMIT = required(CONST_GENERICS_RUNTIME_COMMIT)
    CONST_GENERICS_RUNTIME_ARCHIVE_URL = required(CONST_GENERICS_RUNTIME_ARCHIVE_URL)
    CONST_GENERICS_RUNTIME_ARCHIVE_SHA256 = required(CONST_GENERICS_RUNTIME_ARCHIVE_SHA256)
    CONST_GENERICS_REFERENCE_VERSION = required(CONST_GENERICS_REFERENCE_VERSION)
    CONST_GENERICS_REFERENCE_DIGEST = required(CONST_GENERICS_REFERENCE_DIGEST)
  }
  labels = {
    "org.opencontainers.image.revision" = required(CONST_GENERICS_ROSLYN_COMMIT)
    "org.opencontainers.image.source" = required(CONST_GENERICS_ROSLYN_SOURCE_URI)
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.const-generics-aspnet" = required(BASE_CONST_GENERICS_ASPNET_IMAGE)
    "io.sharplabnext.component.roslyn-const-generics.version" = required(CONST_GENERICS_ROSLYN_COMPONENT_VERSION)
    "io.sharplabnext.component.roslyn-const-generics.commit" = required(CONST_GENERICS_ROSLYN_COMMIT)
    "io.sharplabnext.component.roslyn-const-generics.digest" = "sha256:${required(CONST_GENERICS_ROSLYN_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.roslyn-const-generics.source-uri" = required(CONST_GENERICS_ROSLYN_SOURCE_URI)
    "io.sharplabnext.component.const-generics-roslyn-source.version" = required(CONST_GENERICS_ROSLYN_COMMIT)
    "io.sharplabnext.component.const-generics-roslyn-source.commit" = required(CONST_GENERICS_ROSLYN_COMMIT)
    "io.sharplabnext.component.const-generics-roslyn-source.digest" = "sha256:${required(CONST_GENERICS_ROSLYN_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.const-generics-roslyn-source.source-uri" = required(CONST_GENERICS_ROSLYN_ARCHIVE_URL)
    "io.sharplabnext.component.const-generics-linux-x64.version" = required(CONST_GENERICS_RUNTIME_VERSION)
    "io.sharplabnext.component.const-generics-linux-x64.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-linux-x64.source-uri" = required(CONST_GENERICS_RUNTIME_SOURCE_URI)
    "io.sharplabnext.component.const-generics-ref.version" = required(CONST_GENERICS_REFERENCE_VERSION)
    "io.sharplabnext.component.const-generics-ref.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-ref.digest" = required(CONST_GENERICS_REFERENCE_DIGEST)
    "io.sharplabnext.component.const-generics-ref.source-uri" = required(CONST_GENERICS_RUNTIME_ARCHIVE_URL)
    "io.sharplabnext.roslyn.commit" = required(CONST_GENERICS_ROSLYN_COMMIT)
    "io.sharplabnext.metadata.runtime-commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.reference-set.const-generics-ref" = required(CONST_GENERICS_REFERENCE_DIGEST)
  }
}

target "worker-fsharp" {
  inherits = ["service-with-reference-sets"]
  tags = ["${required(IMAGE_PREFIX)}/worker-fsharp:${required(RELEASE_ID)}"]
  args = {
    PROJECT_PATH = "src/Workers/FSharp/SharpLabNext.Worker.FSharp/SharpLabNext.Worker.FSharp.csproj"
    ASSEMBLY_NAME = "SharpLabNext.Worker.FSharp.dll"
    SERVICE_TITLE = "SharpLabNext F# Worker"
  }
  labels = {
    "io.sharplabnext.component.fsharp-stable.version" = required(FSHARP_COMPILER_SERVICE_VERSION)
    "io.sharplabnext.component.fsharp-stable.source-uri" = required(FSHARP_COMPILER_SERVICE_SOURCE_URI)
    "io.sharplabnext.component.fsharp-core.version" = required(FSHARP_CORE_VERSION)
    "io.sharplabnext.component.fsharp-core.source-uri" = required(FSHARP_CORE_SOURCE_URI)
  }
}

target "worker-gsharp" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-gsharp"
  tags = ["${required(IMAGE_PREFIX)}/worker-gsharp:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    GSHARP_VERSION = required(GSHARP_VERSION)
    GSHARP_COMMIT = required(GSHARP_COMMIT)
    GSHARP_ARCHIVE_URL = required(GSHARP_ARCHIVE_URL)
    GSHARP_ARCHIVE_SHA256 = required(GSHARP_ARCHIVE_SHA256)
    GSHARP_LEGACY_VERSION = required(GSHARP_LEGACY_VERSION)
    GSHARP_LEGACY_COMMIT = required(GSHARP_LEGACY_COMMIT)
    GSHARP_LEGACY_ARCHIVE_URL = required(GSHARP_LEGACY_ARCHIVE_URL)
    GSHARP_LEGACY_ARCHIVE_SHA256 = required(GSHARP_LEGACY_ARCHIVE_SHA256)
    NET10_REFERENCE_PACK_VERSION = required(NET10_REFERENCE_PACK_VERSION)
    NET10_REFERENCE_URL = required(NET10_REFERENCE_URL)
    NET10_REFERENCE_SHA512 = required(NET10_REFERENCE_SHA512)
    NET10_REFERENCE_PACKAGE_CONTENT_HASH = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.gsharp-stable.version" = required(GSHARP_VERSION)
    "io.sharplabnext.component.gsharp-stable.commit" = required(GSHARP_COMMIT)
    "io.sharplabnext.component.gsharp-stable.digest" = "sha256:${required(GSHARP_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.gsharp-stable.source-uri" = required(GSHARP_SOURCE_URI)
    "io.sharplabnext.component.gsharp-source.version" = required(GSHARP_VERSION)
    "io.sharplabnext.component.gsharp-source.commit" = required(GSHARP_COMMIT)
    "io.sharplabnext.component.gsharp-source.digest" = "sha256:${required(GSHARP_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.gsharp-source.source-uri" = required(GSHARP_ARCHIVE_URL)
    "io.sharplabnext.component.gsharp-legacy-0.3.8.version" = required(GSHARP_LEGACY_VERSION)
    "io.sharplabnext.component.gsharp-legacy-0.3.8.commit" = required(GSHARP_LEGACY_COMMIT)
    "io.sharplabnext.component.gsharp-legacy-0.3.8.digest" = "sha256:${required(GSHARP_LEGACY_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.gsharp-legacy-0.3.8.source-uri" = required(GSHARP_LEGACY_SOURCE_URI)
    "io.sharplabnext.component.gsharp-legacy-0.3.8-source.version" = required(GSHARP_LEGACY_VERSION)
    "io.sharplabnext.component.gsharp-legacy-0.3.8-source.commit" = required(GSHARP_LEGACY_COMMIT)
    "io.sharplabnext.component.gsharp-legacy-0.3.8-source.digest" = "sha256:${required(GSHARP_LEGACY_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.gsharp-legacy-0.3.8-source.source-uri" = required(GSHARP_LEGACY_ARCHIVE_URL)
    "io.sharplabnext.reference-set.net10-ref" = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
  }
}

target "worker-peachpie" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-peachpie"
  tags = ["${required(IMAGE_PREFIX)}/worker-peachpie:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    PEACHPIE_CODEANALYSIS_VERSION = required(PEACHPIE_CODEANALYSIS_VERSION)
    PEACHPIE_CODEANALYSIS_URL = required(PEACHPIE_CODEANALYSIS_URL)
    PEACHPIE_CODEANALYSIS_SHA512 = required(PEACHPIE_CODEANALYSIS_SHA512)
    PEACHPIE_CODEANALYSIS_PACKAGE_CONTENT_HASH = required(PEACHPIE_CODEANALYSIS_PACKAGE_CONTENT_HASH)
    PEACHPIE_RUNTIME_VERSION = required(PEACHPIE_RUNTIME_VERSION)
    PEACHPIE_RUNTIME_URL = required(PEACHPIE_RUNTIME_URL)
    PEACHPIE_RUNTIME_SHA512 = required(PEACHPIE_RUNTIME_SHA512)
    PEACHPIE_RUNTIME_PACKAGE_CONTENT_HASH = required(PEACHPIE_RUNTIME_PACKAGE_CONTENT_HASH)
    PEACHPIE_LIBRARY_VERSION = required(PEACHPIE_LIBRARY_VERSION)
    PEACHPIE_LIBRARY_URL = required(PEACHPIE_LIBRARY_URL)
    PEACHPIE_LIBRARY_SHA512 = required(PEACHPIE_LIBRARY_SHA512)
    PEACHPIE_LIBRARY_PACKAGE_CONTENT_HASH = required(PEACHPIE_LIBRARY_PACKAGE_CONTENT_HASH)
    PEACHPIE_COMMIT = required(PEACHPIE_COMMIT)
    PEACHPIE_LICENSE_URL = required(PEACHPIE_LICENSE_URL)
    NET10_REFERENCE_PACK_VERSION = required(NET10_REFERENCE_PACK_VERSION)
    NET10_REFERENCE_URL = required(NET10_REFERENCE_URL)
    NET10_REFERENCE_SHA512 = required(NET10_REFERENCE_SHA512)
    NET10_REFERENCE_PACKAGE_CONTENT_HASH = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.peachpie-stable.version" = required(PEACHPIE_CODEANALYSIS_VERSION)
    "io.sharplabnext.component.peachpie-stable.commit" = required(PEACHPIE_COMMIT)
    "io.sharplabnext.component.peachpie-stable.package-content-hash" = required(PEACHPIE_CODEANALYSIS_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.peachpie-stable.source-uri" = required(PEACHPIE_CODEANALYSIS_SOURCE_URI)
    "io.sharplabnext.component.peachpie-runtime.version" = required(PEACHPIE_RUNTIME_VERSION)
    "io.sharplabnext.component.peachpie-runtime.commit" = required(PEACHPIE_COMMIT)
    "io.sharplabnext.component.peachpie-runtime.package-content-hash" = required(PEACHPIE_RUNTIME_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.peachpie-runtime.source-uri" = required(PEACHPIE_RUNTIME_SOURCE_URI)
    "io.sharplabnext.component.peachpie-library.version" = required(PEACHPIE_LIBRARY_VERSION)
    "io.sharplabnext.component.peachpie-library.commit" = required(PEACHPIE_COMMIT)
    "io.sharplabnext.component.peachpie-library.package-content-hash" = required(PEACHPIE_LIBRARY_PACKAGE_CONTENT_HASH)
    "io.sharplabnext.component.peachpie-library.source-uri" = required(PEACHPIE_LIBRARY_SOURCE_URI)
    "io.sharplabnext.reference-set.net10-ref" = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
  }
}

target "worker-cppcli" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-cppcli"
  tags = ["${required(IMAGE_PREFIX)}/worker-cppcli:${required(RELEASE_ID)}"]
  contexts = {
    "cppcli-prepared-base" = "docker-image://${deferred_image(CPPCLI_PREPARED_BASE_IMAGE)}"
  }
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CPPCLI_COMPILER_VERSION = required(CPPCLI_COMPILER_VERSION)
    NETFX48_REFERENCE_VERSION = required(NETFX48_REFERENCE_VERSION)
    NETFX48_REFERENCE_DIGEST = required(NETFX48_REFERENCE_DIGEST)
    NETFX48_REFERENCE_SOURCE_URI = required(NETFX48_REFERENCE_SOURCE_URI)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.msvc-cppcli-netfx48.version" = required(CPPCLI_COMPILER_VERSION)
    "io.sharplabnext.component.msvc-cppcli-netfx48.digest" = required(CPPCLI_TOOLCHAIN_DIGEST)
    "io.sharplabnext.component.msvc-cppcli-netfx48.source-uri" = required(CPPCLI_TOOLCHAIN_SOURCE_URI)
    "io.sharplabnext.component.msvc-wine-source.version" = required(MSVC_WINE_SOURCE_VERSION)
    "io.sharplabnext.component.msvc-wine-source.commit" = required(MSVC_WINE_SOURCE_COMMIT)
    "io.sharplabnext.component.msvc-wine-source.digest" = required(MSVC_WINE_SOURCE_DIGEST)
    "io.sharplabnext.component.msvc-wine-source.source-uri" = required(MSVC_WINE_SOURCE_URI)
    "io.sharplabnext.component.netfx48-ref.version" = required(NETFX48_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx48-ref.digest" = required(NETFX48_REFERENCE_DIGEST)
    "io.sharplabnext.component.netfx48-ref.source-uri" = required(NETFX48_REFERENCE_SOURCE_URI)
    "io.sharplabnext.reference-set.netfx48-ref" = required(NETFX48_REFERENCE_DIGEST)
  }
}

target "worker-jsharp" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-jsharp"
  tags = ["${required(IMAGE_PREFIX)}/worker-jsharp:${required(RELEASE_ID)}"]
  contexts = {
    "jsharp-wine-base" = "target:jsharp-wine-base"
  }
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    JSHARP_TOOLCHAIN_VERSION = required(JSHARP_TOOLCHAIN_VERSION)
    JSHARP_COMPILER_VERSION = required(JSHARP_COMPILER_VERSION)
    JSHARP_TOOLCHAIN_DIGEST = required(JSHARP_TOOLCHAIN_DIGEST)
    JSHARP_TOOLCHAIN_SOURCE_URI = required(JSHARP_TOOLCHAIN_SOURCE_URI)
    JSHARP_REFERENCE_VERSION = required(JSHARP_REFERENCE_VERSION)
    JSHARP_REFERENCE_DIGEST = required(JSHARP_REFERENCE_DIGEST)
    JSHARP_REFERENCE_SOURCE_URI = required(JSHARP_REFERENCE_SOURCE_URI)
  }
  labels = {
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.jsharp20.version" = required(JSHARP_TOOLCHAIN_VERSION)
    "io.sharplabnext.component.jsharp20.digest" = required(JSHARP_TOOLCHAIN_DIGEST)
    "io.sharplabnext.component.jsharp20.source-uri" = required(JSHARP_TOOLCHAIN_SOURCE_URI)
    "io.sharplabnext.component.vjc-jsharp20.version" = required(JSHARP_COMPILER_VERSION)
    "io.sharplabnext.component.jsharp20-ref.version" = required(JSHARP_REFERENCE_VERSION)
    "io.sharplabnext.component.jsharp20-ref.digest" = required(JSHARP_REFERENCE_DIGEST)
    "io.sharplabnext.component.jsharp20-ref.source-uri" = required(JSHARP_REFERENCE_SOURCE_URI)
    "io.sharplabnext.reference-set.jsharp20-ref" = required(JSHARP_REFERENCE_DIGEST)
  }
}

target "worker-il" {
  inherits = ["service-with-reference-sets"]
  target = "final-with-reference-sets-and-ilsense-license"
  tags = ["${required(IMAGE_PREFIX)}/worker-il:${required(RELEASE_ID)}"]
  args = {
    PROJECT_PATH = "src/Workers/IL/SharpLabNext.Worker.IL/SharpLabNext.Worker.IL.csproj"
    ASSEMBLY_NAME = "SharpLabNext.Worker.IL.dll"
    SERVICE_TITLE = "SharpLabNext Mobius IL Worker"
  }
  labels = {
    "io.sharplabnext.component.mobius-ilasm-stable.version" = required(MOBIUS_ILASM_VERSION)
    "io.sharplabnext.component.mobius-ilasm-stable.source-uri" = required(MOBIUS_ILASM_SOURCE_URI)
    "io.sharplabnext.component.ilsense.version" = required(ILSENSE_VERSION)
    "io.sharplabnext.component.ilsense.commit" = required(ILSENSE_COMMIT)
    "io.sharplabnext.component.ilsense.digest" = "sha256:${required(ILSENSE_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.ilsense.source-uri" = required(ILSENSE_SOURCE_URI)
    "io.sharplabnext.component.ilsense-source.version" = required(ILSENSE_VERSION)
    "io.sharplabnext.component.ilsense-source.commit" = required(ILSENSE_COMMIT)
    "io.sharplabnext.component.ilsense-source.digest" = "sha256:${required(ILSENSE_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.ilsense-source.source-uri" = required(ILSENSE_ARCHIVE_URL)
  }
}

target "worker-minilang" {
  inherits = ["service-with-reference-sets"]
  tags = ["${required(IMAGE_PREFIX)}/worker-minilang:${required(RELEASE_ID)}"]
  args = {
    PROJECT_PATH = "samples/Languages/SharpLabNext.SampleLanguage.Worker/SharpLabNext.SampleLanguage.Worker.csproj"
    ASSEMBLY_NAME = "SharpLabNext.SampleLanguage.Worker.dll"
    SERVICE_TITLE = "SharpLabNext MiniLang SDK Sample Worker"
  }
  labels = {
    "io.sharplabnext.component.minilang-stable.version" = required(MINILANG_VERSION)
  }
}

target "worker-artifacts-default" {
  inherits = ["service-with-framework-reference-sets"]
  target = "final-with-framework-and-jsharp-reference-sets"
  tags = ["${required(IMAGE_PREFIX)}/worker-artifacts-default:${required(RELEASE_ID)}"]
  contexts = {
    "jsharp-reference-source" = "target:worker-jsharp"
  }
  args = {
    PROJECT_PATH = "src/Workers/Artifacts.Default/SharpLabNext.Worker.Artifacts.Default/SharpLabNext.Worker.Artifacts.Default.csproj"
    ASSEMBLY_NAME = "SharpLabNext.Worker.Artifacts.Default.dll"
    SERVICE_TITLE = "SharpLabNext Default Artifact Worker"
    JSHARP_REFERENCE_VERSION = required(JSHARP_REFERENCE_VERSION)
    JSHARP_REFERENCE_DIGEST = required(JSHARP_REFERENCE_DIGEST)
    JSHARP_REFERENCE_SOURCE_URI = required(JSHARP_REFERENCE_SOURCE_URI)
  }
  labels = {
    "io.sharplabnext.component.artifacts-default.version" = required(ARTIFACTS_DEFAULT_VERSION)
    "io.sharplabnext.component.ilspy.version" = required(ILSPY_VERSION)
    "io.sharplabnext.component.ilspy.source-uri" = required(ILSPY_SOURCE_URI)
    "io.sharplabnext.component.dotnet-ilverify.version" = required(ILVERIFICATION_VERSION)
    "io.sharplabnext.component.dotnet-ilverify.source-uri" = required(ILVERIFICATION_SOURCE_URI)
    "io.sharplabnext.component.netfx48-managed-ref.version" = required(NETFX48_MANAGED_REFERENCE_VERSION)
    "io.sharplabnext.component.netfx48-managed-ref.source-uri" = required(NETFX48_MANAGED_REFERENCE_SOURCE_URI)
    "io.sharplabnext.reference-set.netfx48-managed-ref" = required(NETFX48_MANAGED_REFERENCE_DIGEST)
    "io.sharplabnext.component.jsharp20-ref.version" = required(JSHARP_REFERENCE_VERSION)
    "io.sharplabnext.component.jsharp20-ref.digest" = required(JSHARP_REFERENCE_DIGEST)
    "io.sharplabnext.component.jsharp20-ref.source-uri" = required(JSHARP_REFERENCE_SOURCE_URI)
    "io.sharplabnext.reference-set.jsharp20-ref" = required(JSHARP_REFERENCE_DIGEST)
  }
}

target "worker-artifacts-jsil" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-artifacts-jsil"
  tags = ["${required(IMAGE_PREFIX)}/worker-artifacts-jsil:${required(RELEASE_ID)}"]
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    MONO_IMAGE = required(BASE_MONO_JSIL_IMAGE)
    JSIL_VERSION = required(JSIL_VERSION)
    JSIL_COMMIT = required(JSIL_COMMIT)
    JSIL_ARCHIVE_URL = required(JSIL_ARCHIVE_URL)
    JSIL_ARCHIVE_SHA256 = required(JSIL_ARCHIVE_SHA256)
    JSIL_META_COMMIT = required(JSIL_META_COMMIT)
    JSIL_META_ARCHIVE_URL = required(JSIL_META_ARCHIVE_URL)
    JSIL_META_ARCHIVE_SHA256 = required(JSIL_META_ARCHIVE_SHA256)
    JSIL_ILSPY_COMMIT = required(JSIL_ILSPY_COMMIT)
    JSIL_ILSPY_ARCHIVE_URL = required(JSIL_ILSPY_ARCHIVE_URL)
    JSIL_ILSPY_ARCHIVE_SHA256 = required(JSIL_ILSPY_ARCHIVE_SHA256)
    JSIL_NREFACTORY_COMMIT = required(JSIL_NREFACTORY_COMMIT)
    JSIL_NREFACTORY_ARCHIVE_URL = required(JSIL_NREFACTORY_ARCHIVE_URL)
    JSIL_NREFACTORY_ARCHIVE_SHA256 = required(JSIL_NREFACTORY_ARCHIVE_SHA256)
    JSIL_CECIL_COMMIT = required(JSIL_CECIL_COMMIT)
    JSIL_CECIL_ARCHIVE_URL = required(JSIL_CECIL_ARCHIVE_URL)
    JSIL_CECIL_ARCHIVE_SHA256 = required(JSIL_CECIL_ARCHIVE_SHA256)
    NET10_REFERENCE_PACK_VERSION = required(NET10_REFERENCE_PACK_VERSION)
    NET10_REFERENCE_URL = required(NET10_REFERENCE_URL)
    NET10_REFERENCE_SHA512 = required(NET10_REFERENCE_SHA512)
    NET10_REFERENCE_PACKAGE_CONTENT_HASH = required(NET10_REFERENCE_PACKAGE_CONTENT_HASH)
    NET11_REFERENCE_VERSION = required(NET11_REFERENCE_VERSION)
    NET11_REFERENCE_URL = required(NET11_REFERENCE_URL)
    NET11_REFERENCE_SHA512 = required(NET11_REFERENCE_SHA512)
    NET11_REFERENCE_PACKAGE_CONTENT_HASH = required(NET11_REFERENCE_PACKAGE_CONTENT_HASH)
  }
  labels = {
    "io.sharplabnext.base-image.mono-jsil" = required(BASE_MONO_JSIL_IMAGE)
    "io.sharplabnext.component.artifacts-jsil.version" = required(ARTIFACTS_JSIL_VERSION)
    "io.sharplabnext.component.artifacts-jsil.commit" = required(ARTIFACTS_JSIL_COMMIT)
    "io.sharplabnext.component.artifacts-jsil.digest" = required(ARTIFACTS_JSIL_DIGEST)
    "io.sharplabnext.component.artifacts-jsil.source-uri" = required(ARTIFACTS_JSIL_SOURCE_URI)
    "io.sharplabnext.component.jsil-source.version" = required(JSIL_VERSION)
    "io.sharplabnext.component.jsil-source.commit" = required(JSIL_COMMIT)
    "io.sharplabnext.component.jsil-source.digest" = "sha256:${required(JSIL_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.jsil-source.source-uri" = required(JSIL_ARCHIVE_URL)
    "io.sharplabnext.component.jsil-meta-source.version" = required(JSIL_META_VERSION)
    "io.sharplabnext.component.jsil-meta-source.commit" = required(JSIL_META_COMMIT)
    "io.sharplabnext.component.jsil-meta-source.digest" = "sha256:${required(JSIL_META_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.jsil-meta-source.source-uri" = required(JSIL_META_ARCHIVE_URL)
    "io.sharplabnext.component.jsil-ilspy-source.version" = required(JSIL_ILSPY_VERSION)
    "io.sharplabnext.component.jsil-ilspy-source.commit" = required(JSIL_ILSPY_COMMIT)
    "io.sharplabnext.component.jsil-ilspy-source.digest" = "sha256:${required(JSIL_ILSPY_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.jsil-ilspy-source.source-uri" = required(JSIL_ILSPY_ARCHIVE_URL)
    "io.sharplabnext.component.jsil-nrefactory-source.version" = required(JSIL_NREFACTORY_VERSION)
    "io.sharplabnext.component.jsil-nrefactory-source.commit" = required(JSIL_NREFACTORY_COMMIT)
    "io.sharplabnext.component.jsil-nrefactory-source.digest" = "sha256:${required(JSIL_NREFACTORY_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.jsil-nrefactory-source.source-uri" = required(JSIL_NREFACTORY_ARCHIVE_URL)
    "io.sharplabnext.component.jsil-cecil-source.version" = required(JSIL_CECIL_VERSION)
    "io.sharplabnext.component.jsil-cecil-source.commit" = required(JSIL_CECIL_COMMIT)
    "io.sharplabnext.component.jsil-cecil-source.digest" = "sha256:${required(JSIL_CECIL_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.jsil-cecil-source.source-uri" = required(JSIL_CECIL_ARCHIVE_URL)
    "io.sharplabnext.component.net10-ref.version" = required(NET10_REFERENCE_PACK_VERSION)
    "io.sharplabnext.component.net10-ref.source-uri" = required(NET10_REFERENCE_SOURCE_URI)
    "io.sharplabnext.component.net11-preview-ref.version" = required(NET11_REFERENCE_VERSION)
    "io.sharplabnext.component.net11-preview-ref.source-uri" = required(NET11_REFERENCE_SOURCE_URI)
  }
}

target "worker-artifacts-const-generics" {
  inherits = ["common"]
  dockerfile = "deploy/docker/Dockerfile.worker-artifacts-const-generics"
  tags = ["${required(IMAGE_PREFIX)}/worker-artifacts-const-generics:${required(RELEASE_ID)}"]
  contexts = {
    "const-generics-runtime" = "target:runtime-const-generics"
    "const-generics-fork-packages" = "./artifacts/prerequisites/downloads/const-generics-fork-packages"
  }
  args = {
    VERSION = RELEASE_ID
    SOURCE_REVISION = SOURCE_REVISION
    CONST_GENERICS_ILSPY_COMMIT = required(CONST_GENERICS_ILSPY_COMMIT)
    CONST_GENERICS_ILSPY_ARCHIVE_URL = required(CONST_GENERICS_ILSPY_ARCHIVE_URL)
    CONST_GENERICS_ILSPY_ARCHIVE_SHA256 = required(CONST_GENERICS_ILSPY_ARCHIVE_SHA256)
    CONST_GENERICS_RUNTIME_COMMIT = required(CONST_GENERICS_RUNTIME_COMMIT)
    CONST_GENERICS_RUNTIME_ARCHIVE_URL = required(CONST_GENERICS_RUNTIME_ARCHIVE_URL)
    CONST_GENERICS_RUNTIME_ARCHIVE_SHA256 = required(CONST_GENERICS_RUNTIME_ARCHIVE_SHA256)
    CONST_GENERICS_VERSIONTOOLS_VERSION = required(CONST_GENERICS_VERSIONTOOLS_VERSION)
    CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256 = required(CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256)
    CONST_GENERICS_VERSIONTOOLS_SOURCE_URI = required(CONST_GENERICS_VERSIONTOOLS_SOURCE_URI)
    CONST_GENERICS_REFERENCE_VERSION = required(CONST_GENERICS_REFERENCE_VERSION)
  }
  labels = {
    "org.opencontainers.image.revision" = required(CONST_GENERICS_ILSPY_COMMIT)
    "org.opencontainers.image.source" = required(CONST_GENERICS_ILSPY_SOURCE_URI)
    "io.sharplabnext.base-image.dotnet-sdk" = required(BASE_DOTNET_SDK_IMAGE)
    "io.sharplabnext.base-image.dotnet-aspnet" = required(BASE_DOTNET_ASPNET_IMAGE)
    "io.sharplabnext.component.artifacts-const-generics.version" = required(ARTIFACTS_CONST_GENERICS_VERSION)
    "io.sharplabnext.component.artifacts-const-generics.commit" = required(CONST_GENERICS_ILSPY_COMMIT)
    "io.sharplabnext.component.artifacts-const-generics.digest" = "sha256:${required(CONST_GENERICS_ILSPY_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.artifacts-const-generics.source-uri" = required(CONST_GENERICS_ILSPY_SOURCE_URI)
    "io.sharplabnext.component.const-generics-ilspy-source.version" = required(CONST_GENERICS_ILSPY_COMMIT)
    "io.sharplabnext.component.const-generics-ilspy-source.commit" = required(CONST_GENERICS_ILSPY_COMMIT)
    "io.sharplabnext.component.const-generics-ilspy-source.digest" = "sha256:${required(CONST_GENERICS_ILSPY_ARCHIVE_SHA256)}"
    "io.sharplabnext.component.const-generics-ilspy-source.source-uri" = required(CONST_GENERICS_ILSPY_ARCHIVE_URL)
    "io.sharplabnext.component.const-generics-linux-x64.version" = required(CONST_GENERICS_RUNTIME_VERSION)
    "io.sharplabnext.component.const-generics-linux-x64.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-linux-x64.source-uri" = required(CONST_GENERICS_RUNTIME_SOURCE_URI)
    "io.sharplabnext.component.const-generics-ref.version" = required(CONST_GENERICS_REFERENCE_VERSION)
    "io.sharplabnext.component.const-generics-ref.commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.component.const-generics-ref.digest" = required(CONST_GENERICS_REFERENCE_DIGEST)
    "io.sharplabnext.component.const-generics-ref.source-uri" = required(CONST_GENERICS_RUNTIME_ARCHIVE_URL)
    "io.sharplabnext.component.const-generics-versiontools.version" = required(CONST_GENERICS_VERSIONTOOLS_VERSION)
    "io.sharplabnext.component.const-generics-versiontools.digest" = "sha256:${required(CONST_GENERICS_VERSIONTOOLS_PACKAGE_SHA256)}"
    "io.sharplabnext.component.const-generics-versiontools.source-uri" = required(CONST_GENERICS_VERSIONTOOLS_SOURCE_URI)
    "io.sharplabnext.ilspy.commit" = required(CONST_GENERICS_ILSPY_COMMIT)
    "io.sharplabnext.ilverification.runtime-commit" = required(CONST_GENERICS_RUNTIME_COMMIT)
    "io.sharplabnext.reference-set.const-generics-ref" = required(CONST_GENERICS_REFERENCE_DIGEST)
  }
}

target "worker-artifacts-il-assembler" {
  inherits = ["service"]
  tags = ["${required(IMAGE_PREFIX)}/worker-artifacts-il-assembler:${required(RELEASE_ID)}"]
  args = {
    PROJECT_PATH = "src/Workers/Artifacts.ILAssembler/SharpLabNext.Worker.Artifacts.ILAssembler/SharpLabNext.Worker.Artifacts.ILAssembler.csproj"
    ASSEMBLY_NAME = "SharpLabNext.Worker.Artifacts.ILAssembler.dll"
    SERVICE_TITLE = "SharpLabNext IL Assembler Artifact Worker"
  }
  labels = {
    "io.sharplabnext.component.il-assembler.version" = required(IL_ASSEMBLER_VERSION)
  }
}
