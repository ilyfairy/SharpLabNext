#!/usr/bin/env bash
set -euo pipefail

stage="${1:-check}"
if (($# > 0)); then shift; fi
case "$stage" in
  check|resolve|build|test|promote) ;;
  *)
    echo "Usage: eng/update-profiles.sh [check|resolve|build|test|promote] [updater options]" >&2
    exit 64
    ;;
esac

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"
dotnet run --project src/Tools/SharpLabNext.ProfileUpdater -- "$stage" "$@"
