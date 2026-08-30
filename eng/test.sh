#!/usr/bin/env bash
set -euo pipefail

configuration="Release"
skip_build=false
skip_frontend=false
skip_schemas=false
compose_e2e=false

while (($# > 0)); do
    case "$1" in
        --configuration)
            configuration="${2:?--configuration requires a value}"
            shift 2
            ;;
        --skip-build)
            skip_build=true
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
        --compose-e2e)
            compose_e2e=true
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

if [[ "$skip_build" == false ]]; then
    build_args=(--configuration "$configuration")
    if [[ "$skip_frontend" == true ]]; then
        build_args+=(--skip-frontend)
    fi
    if [[ "$skip_schemas" == true ]]; then
        build_args+=(--skip-schemas)
    fi
    ./eng/build.sh "${build_args[@]}"
fi

test_reference_set_root="$root/.tmp/test-coreclr-reference-sets"
test_reference_archive_cache="$root/.tmp/reference-package-cache"
test_reference_appsettings="$root/.tmp/test-coreclr-reference-sets.appsettings.json"
runtime_assembly="$root/src/RuntimeApi/SharpLabNext.Runtime/bin/$configuration/netstandard2.1/SharpLab.Runtime.dll"
if [[ ! -f "$runtime_assembly" ]]; then
    echo "The test reference-set materializer requires '$runtime_assembly'." >&2
    exit 1
fi
dotnet run eng/tools/materialize-coreclr-reference-sets.cs -- \
    --matrix profiles/runtime-matrix.json \
    --lock profiles/lock.json \
    --output "$test_reference_set_root" \
    --archive-cache "$test_reference_archive_cache" \
    --appsettings-template src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable/appsettings.json \
    --appsettings-output "$test_reference_appsettings" \
    --runtime-assembly "$runtime_assembly"
export SHARPLABNEXT_TEST_CORECLR_REFERENCE_SETS="$test_reference_set_root"
export SHARPLABNEXT_NET10_REF_PATH="$test_reference_set_root/net10-ref"
export SHARPLABNEXT_NET11_REF_PATH="$test_reference_set_root/net11-preview-ref"

dotnet test SharpLabNext.slnx \
    --configuration "$configuration" \
    --no-build \
    --no-restore

dotnet run eng/performance/runtime-performance-preflight.cs -- --self-test

dotnet run eng/tools/runtime-capability-preflight.cs -- --self-test

dotnet run --project src/Tools/SharpLabNext.CompatibilityCli \
    --configuration "$configuration" \
    --no-build -- validate --output artifacts/compatibility-report.json

if [[ "$skip_frontend" == false ]]; then
    npm --prefix frontend run test --if-present
    if [[ "$compose_e2e" == true ]]; then
        e2e_base_url="${SHARPLABNEXT_E2E_BASE_URL:-http://127.0.0.1:8080}"
        dotnet run eng/smoke/gateway-compose.cs -- "$e2e_base_url" --full
        dotnet run eng/smoke/gateway-compose.cs -- "$e2e_base_url" --security
        npm --prefix frontend run test:e2e
        dotnet run eng/smoke/runtime-failures.cs -- "$e2e_base_url"
    fi
elif [[ "$compose_e2e" == true ]]; then
    echo "--compose-e2e cannot be combined with --skip-frontend" >&2
    exit 2
fi

if [[ "$skip_build" == true && "$skip_schemas" == false ]]; then
    mapfile -t test_files < <(find eng/tests -type f -name '*.test.mjs' -print | sort)
    node --test "${test_files[@]}"
    node eng/validation/validate-bake-inputs.mjs
    node eng/validation/validate-schemas.mjs
fi
