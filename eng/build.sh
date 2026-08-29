#!/usr/bin/env bash
set -euo pipefail

configuration="Release"
skip_restore=false
skip_frontend=false
skip_schemas=false
skip_validation=false

while (($# > 0)); do
    case "$1" in
        --configuration)
            configuration="${2:?--configuration requires a value}"
            shift 2
            ;;
        --skip-restore)
            skip_restore=true
            shift
            ;;
        --skip-frontend)
            skip_frontend=true
            shift
            ;;
        --skip-schemas)
            skip_schemas=true
            shift
            ;;
        --skip-validation)
            skip_validation=true
            shift
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

case "$configuration" in
    Debug|Release) ;;
    *)
        echo "Configuration must be Debug or Release." >&2
        exit 2
        ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export NUGET_XMLDOC_MODE=skip

if [[ "$skip_restore" == false ]]; then
    dotnet run eng/verify-ilsense-inputs.cs -- \
        --repository-root "$root" \
        --verify-restore
    dotnet restore SharpLabNext.slnx --locked-mode
fi

if [[ "$skip_frontend" == false ]]; then
    if [[ "$skip_restore" == false ]]; then
        npm --prefix frontend ci --no-audit --no-fund
    fi

    npm --prefix frontend run lint
    npm --prefix frontend run build
fi

dotnet build SharpLabNext.slnx \
    --configuration "$configuration" \
    --no-restore

if [[ "$skip_validation" == false && "$skip_schemas" == false ]]; then
    node --test \
        eng/runtime-profile-channel-validation.test.mjs \
        eng/runtime-wine-packages.test.mjs \
        eng/prerequisite-cache.test.mjs \
        eng/image-build-inputs.test.mjs \
        eng/cppcli-netfx-sdk-extraction.test.mjs \
        eng/build-images.test.mjs \
        eng/build-wine-coreclr-operator.test.mjs \
        eng/runtime-candidate-input-validation.test.mjs \
        eng/runtime-candidate-environment.test.mjs \
        eng/runtime-framework-installers.test.mjs \
        eng/build-framework-matrix-context.test.mjs \
        eng/build-framework-matrix-parent.test.mjs \
        eng/committed-source-context.test.mjs \
        eng/rebuild-runtime-candidate.test.mjs \
        eng/create-runtime-framework-candidate-input.test.mjs \
        eng/framework-prefix-matrix.test.mjs \
        eng/runtime-matrix-deployment-bridge.test.mjs \
        eng/runtime-matrix-generator.test.mjs \
        eng/runtime-promotion-receipt-validation.test.mjs \
        eng/wine-netfx-framework-preflight.test.mjs
    node eng/validate-bake-inputs.mjs
    node eng/validate-schemas.mjs
    node eng/validate-compose.mjs
fi
