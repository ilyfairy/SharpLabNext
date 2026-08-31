#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
bundle_arguments=()
build_arguments=(--all)
output_directory=""
source_revision=""
accept_microsoft_licenses=false
rebuild_images=false
bundle_only=false
rebuild_targets=()

while (($# > 0)); do
  case "$1" in
    --output) output_directory="${2:?--output requires a value}"; shift 2 ;;
    --metadata-only) bundle_arguments+=(--metadata-only); shift ;;
    --image-prefix)
      build_arguments+=("$1" "$2")
      bundle_arguments+=("$1" "$2")
      shift 2
      ;;
    --source-revision)
      source_revision="$2"
      shift 2
      ;;
    --rebuild-target)
      rebuild_targets+=("${2:?--rebuild-target requires a value}")
      shift 2
      ;;
    --max-parallel)
      build_arguments+=("$1" "$2")
      shift 2
      ;;
    --offline)
      build_arguments+=("$1")
      shift
      ;;
    --rebuild-images)
      rebuild_images=true
      shift
      ;;
    --bundle-only)
      bundle_only=true
      shift
      ;;
    --accept-microsoft-licenses)
      accept_microsoft_licenses=true
      build_arguments+=("$1")
      shift
      ;;
    -h|--help)
      echo "Usage: eng/release.sh [--output PATH] [--image-prefix PREFIX] [--source-revision COMMIT] [--rebuild-target TARGET ...] [--max-parallel 1..8 (default 5)] [--offline] [--rebuild-images] [--bundle-only] [--metadata-only] --accept-microsoft-licenses"
      exit 0
      ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
done

if [[ "$accept_microsoft_licenses" != true ]]; then
  echo "--accept-microsoft-licenses is required because the complete image set contains Microsoft proprietary inputs" >&2
  exit 64
fi
if [[ -n "$output_directory" ]]; then
  output_directory="$(realpath -m -- "$output_directory")"
  if [[ -e "$output_directory" ]]; then
    echo "Bundle output already exists: $output_directory" >&2
    exit 1
  fi
  bundle_arguments+=(--output "$output_directory")
fi

source_arguments=(run "$repository_root/eng/tools/resolve-source-provenance.cs" -- --repository-root "$repository_root")
if [[ -n "$source_revision" ]]; then source_arguments+=(--source-revision "$source_revision"); fi
source_revision="$(dotnet "${source_arguments[@]}" | sed -n 's/^SHARPLABNEXT_SOURCE_REVISION=//p' | tail -n 1)"
if [[ -z "$source_revision" ]]; then
  echo "Source provenance resolver did not return a revision" >&2
  exit 1
fi
build_arguments+=(--source-revision "$source_revision")
for target in "${rebuild_targets[@]}"; do build_arguments+=(--rebuild-target "$target"); done
bundle_arguments+=(--source-revision "$source_revision")
if [[ "$rebuild_images" == true ]]; then build_arguments+=(--no-reuse-existing); fi

if [[ "$bundle_only" == false ]]; then
  image_cache_hit=false
  if [[ "$rebuild_images" == false ]]; then
    probe_status=0
    if probe_output="$(bash "$repository_root/eng/build-images.sh" "${build_arguments[@]}" --cache-probe 2>&1)"; then
      probe_status=0
    else
      probe_status=$?
    fi
    printf '%s\n' "$probe_output"
    if [[ "$probe_status" != 0 ]]; then
      echo "SharpLabNext image cache probe failed." >&2
      exit "$probe_status"
    fi
    if grep -Fqx 'SHARPLABNEXT_IMAGE_CACHE=hit' <<< "$probe_output"; then
      image_cache_hit=true
    fi
  fi

  if [[ "$image_cache_hit" == false ]]; then
    bash "$repository_root/eng/build.sh" --configuration Release --skip-validation
    bash "$repository_root/eng/build-images.sh" "${build_arguments[@]}"
  else
    echo "All release images are cached; skipping host and Docker image builds."
  fi
fi
bash "$repository_root/eng/bundle.sh" "${bundle_arguments[@]}"
