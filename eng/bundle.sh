#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output_directory=""
image_prefix="sharplabnext"
metadata_only=false
signing_key=""
signing_public_key=""
signing_key_id=""
source_revision=""
image_overrides=()

while (($# > 0)); do
  case "$1" in
    --output) output_directory="$(realpath -m -- "$2")"; shift 2 ;;
    --image-prefix) image_prefix="$2"; shift 2 ;;
    --image) image_overrides+=("$2"); shift 2 ;;
    --signing-key) signing_key="$(realpath -- "$2")"; shift 2 ;;
    --signing-public-key) signing_public_key="$(realpath -- "$2")"; shift 2 ;;
    --signing-key-id) signing_key_id="$2"; shift 2 ;;
    --source-revision) source_revision="$2"; shift 2 ;;
    --metadata-only) metadata_only=true; shift ;;
    -h|--help)
      echo "Usage: eng/bundle.sh [--output PATH] [--image-prefix PREFIX] [--image ID=REFERENCE] [--signing-key PATH --signing-public-key PATH] [--signing-key-id ID] [--source-revision COMMIT] [--metadata-only]"
      exit 0
      ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
done

if [[ -n "$output_directory" ]]; then
  if [[ -e "$output_directory" ]]; then
    echo "Bundle output already exists: $output_directory" >&2
    exit 1
  fi
fi
if [[ -n "$signing_key_id" && -z "$signing_key" ]]; then
  echo "--signing-key-id requires --signing-key and --signing-public-key" >&2
  exit 64
fi
if [[ -n "$signing_key" && -z "$signing_public_key" ]] ||
   [[ -z "$signing_key" && -n "$signing_public_key" ]]; then
  echo "--signing-key and --signing-public-key are required together" >&2
  exit 64
fi
if [[ -z "$signing_key" ]]; then
  export SHARPLABNEXT_SOURCE_IDENTITY_MODE=content
else
  unset SHARPLABNEXT_SOURCE_IDENTITY_MODE || true
fi

cd "$repository_root"
source_arguments=(run "$repository_root/eng/tools/resolve-source-provenance.cs" -- --repository-root "$repository_root")
if [[ -n "$source_revision" ]]; then source_arguments+=(--source-revision "$source_revision"); fi
if [[ -n "$signing_key" ]]; then
  source_arguments+=(--verify-git)
fi
source_revision="$(dotnet "${source_arguments[@]}" | sed -n 's/^SHARPLABNEXT_SOURCE_REVISION=//p' | tail -n 1)"
if [[ -z "$source_revision" ]]; then
  echo "Source provenance resolver did not return a revision" >&2
  exit 1
fi

arguments=(
  run --project src/Tools/SharpLabNext.BundleBuilder --configuration Release --
  --repository-root "$repository_root"
  --image-prefix "$image_prefix"
  --source-revision "$source_revision"
)
if [[ -n "$output_directory" ]]; then arguments+=(--output "$output_directory"); fi
if [[ "$metadata_only" == true ]]; then arguments+=(--metadata-only); fi
if [[ -n "$signing_key" ]]; then
  arguments+=(--signing-key "$signing_key" --signing-public-key "$signing_public_key")
  if [[ -n "$signing_key_id" ]]; then arguments+=(--signing-key-id "$signing_key_id"); fi
fi
for image in "${image_overrides[@]}"; do arguments+=(--image "$image"); done
dotnet "${arguments[@]}"
