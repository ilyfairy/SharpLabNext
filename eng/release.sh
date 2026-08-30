#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
bundle_arguments=(--allow-development-image-inputs)
build_arguments=(--all)
output_directory=""
accept_microsoft_licenses=false
rebuild_images=false
bundle_only=false

while (($# > 0)); do
  case "$1" in
    --output) output_directory="${2:?--output requires a value}"; shift 2 ;;
    --metadata-only) bundle_arguments+=(--metadata-only); shift ;;
    --image-prefix|--source-revision)
      build_arguments+=("$1" "$2")
      bundle_arguments+=("$1" "$2")
      shift 2
      ;;
    --allow-uncommitted-source-for-development)
      build_arguments+=("$1")
      bundle_arguments+=("$1")
      shift
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
      echo "Usage: eng/release.sh [--output PATH] [--image-prefix PREFIX] [--source-revision COMMIT] [--max-parallel 1..8] [--offline] [--rebuild-images] [--bundle-only] [--metadata-only] [--allow-uncommitted-source-for-development] --accept-microsoft-licenses"
      exit 0
      ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
done

if [[ "$accept_microsoft_licenses" != true ]]; then
  echo "--accept-microsoft-licenses is required because the complete image set contains Microsoft proprietary inputs" >&2
  exit 64
fi
if [[ -z "$output_directory" ]]; then
  release_id="$(dotnet run "$repository_root/eng/tools/read-release-id.cs" -- "$repository_root/profiles/lock.json")"
  output_directory="$repository_root/artifacts/sharplabnext-$release_id"
else
  output_directory="$(realpath -m -- "$output_directory")"
fi
if [[ -e "$output_directory" ]]; then
  echo "Bundle output already exists: $output_directory" >&2
  exit 1
fi
bundle_arguments+=(--output "$output_directory")
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
