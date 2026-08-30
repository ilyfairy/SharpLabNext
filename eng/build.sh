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
export SHARPLABNEXT_SOURCE_IDENTITY_MODE=content

if [[ "$skip_restore" == false ]]; then
    dotnet run eng/tools/verify-ilsense-inputs.cs -- \
        --repository-root "$root" \
        --verify-restore \
        --allow-missing-git
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
    mapfile -t test_files < <(find eng/tests -type f -name '*.test.mjs' -print | sort)
    node --test "${test_files[@]}"
    node eng/validation/validate-bake-inputs.mjs
    node eng/validation/validate-schemas.mjs
    node eng/validation/validate-compose.mjs
fi
