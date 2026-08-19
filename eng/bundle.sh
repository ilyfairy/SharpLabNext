#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
release_id="$(dotnet run "$repository_root/eng/read-release-id.cs" -- "$repository_root/profiles/lock.json")"
output_directory="$repository_root/artifacts/bundles/sharplabnext-$release_id"
image_prefix="sharplabnext"
skip_build=false
skip_restore=false
metadata_only=false
signing_key=""
signing_public_key=""
signing_key_id=""
source_revision=""
allow_uncommitted_source_for_development=false
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
    --allow-uncommitted-source-for-development) allow_uncommitted_source_for_development=true; shift ;;
    --skip-restore) skip_restore=true; shift ;;
    --skip-build) skip_build=true; shift ;;
    --metadata-only) metadata_only=true; shift ;;
    -h|--help)
      echo "Usage: eng/bundle.sh [--output PATH] [--image-prefix PREFIX] [--image ID=REFERENCE] [--signing-key PATH --signing-public-key PATH] [--signing-key-id ID] [--source-revision COMMIT] [--allow-uncommitted-source-for-development] [--skip-restore] [--skip-build] [--metadata-only]"
      exit 0
      ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
done

if [[ -e "$output_directory" ]]; then
  echo "Bundle output already exists: $output_directory" >&2
  exit 1
fi
if [[ -n "$signing_key_id" && -z "$signing_key" ]]; then
  echo "--signing-key-id requires --signing-key and --signing-public-key" >&2
  exit 64
fi
if [[ "$allow_uncommitted_source_for_development" == true && -n "$signing_key" ]]; then
  echo "--allow-uncommitted-source-for-development cannot be used for a signed release bundle" >&2
  exit 64
fi

cd "$repository_root"
ilsense_arguments=(
  run "$repository_root/eng/verify-ilsense-inputs.cs" --
  --repository-root "$repository_root"
  --lock "$repository_root/profiles/lock.json"
)
if [[ "$skip_restore" == false ]]; then
  ilsense_arguments+=(--verify-restore)
fi
dotnet "${ilsense_arguments[@]}"

if [[ "$skip_build" == false ]]; then
  dotnet run "$repository_root/eng/verify-buildkit.cs"
fi
if [[ "$skip_restore" == false ]]; then
  dotnet restore SharpLabNext.slnx --locked-mode
  npm --prefix frontend ci --no-audit --no-fund
fi

source_arguments=(
  run "$repository_root/eng/resolve-source-provenance.cs" --
  --repository-root "$repository_root"
)
if [[ -n "$source_revision" ]]; then
  source_arguments+=(--source-revision "$source_revision")
fi
if [[ "$allow_uncommitted_source_for_development" == true ]]; then
  source_arguments+=(--allow-uncommitted-source-for-development)
fi
source_revision="$(
  dotnet "${source_arguments[@]}" |
    sed -n 's/^SHARPLABNEXT_SOURCE_REVISION=//p' |
    tail -n 1
)"
if [[ -z "$source_revision" ]]; then
  echo "Source provenance resolver did not return a revision" >&2
  exit 1
fi

if [[ "$skip_build" == false ]]; then
  lock_path="$repository_root/profiles/lock.json"
  bake_environment_arguments=(
    --lock "$lock_path"
    --base-images "$repository_root/profiles/base-images.json"
    --source-revision "$source_revision"
    --repository-root "$repository_root"
    --image-prefix "$image_prefix"
  )
  if [[ "$allow_uncommitted_source_for_development" == true ]]; then
    bake_environment_arguments+=(--allow-uncommitted-source-for-development)
  fi
  dotnet run "$repository_root/eng/run-with-bake-environment.cs" -- \
    "${bake_environment_arguments[@]}" \
    -- docker buildx bake --file eng/bake.hcl
fi

arguments=(
  run --project src/Tools/SharpLabNext.BundleBuilder --
  --output "$output_directory"
  --image-prefix "$image_prefix"
  --source-revision "$source_revision"
)
if [[ "$metadata_only" == true ]]; then arguments+=(--metadata-only); fi
if [[ "$allow_uncommitted_source_for_development" == true ]]; then
  arguments+=(--allow-uncommitted-source-for-development)
fi
if [[ -n "$signing_key" || -n "$signing_public_key" ]]; then
  if [[ -z "$signing_key" || -z "$signing_public_key" ]]; then
    echo "--signing-key and --signing-public-key are required together" >&2
    exit 64
  fi
  arguments+=(--signing-key "$signing_key" --signing-public-key "$signing_public_key")
  if [[ -n "$signing_key_id" ]]; then arguments+=(--signing-key-id "$signing_key_id"); fi
fi
for image in "${image_overrides[@]}"; do arguments+=(--image "$image"); done
dotnet "${arguments[@]}"
echo "Offline bundle created at $output_directory"
