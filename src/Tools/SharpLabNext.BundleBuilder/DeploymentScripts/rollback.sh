#!/usr/bin/env sh
set -eu

script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_root/deployment-common.sh"
install_root=${SHARPLABNEXT_HOME:-${HOME:?HOME is required}/.local/share/sharplabnext}
ready_timeout_seconds=180
smoke_base_address=''
keep_current_artifact_data=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --install-root) install_root=$2; shift 2 ;;
    --ready-timeout-seconds) ready_timeout_seconds=$2; shift 2 ;;
    --smoke-base-address) smoke_base_address=$2; shift 2 ;;
    --keep-current-artifact-data) keep_current_artifact_data=true; shift ;;
    *) echo "Unknown rollback option: $1" >&2; exit 64 ;;
  esac
done

install_root=$(CDPATH= cd -- "$install_root" && pwd)
current=$(release_pointer "$install_root/current-release")
previous=$(release_pointer "$install_root/previous-release")
[ -n "$previous" ] || { echo 'No previous SharpLabNext release is recorded.' >&2; exit 1; }
previous_root="$install_root/releases/$previous"
safety_root=''

if [ -n "$current" ] && [ "$keep_current_artifact_data" = false ] && [ -d "$install_root/releases/$current/rollback/artifact-data" ]; then
  safety_root="$install_root/.rollback-safety.$$"
  mkdir -p "$safety_root"
  if ! backup_artifact_store "$install_root/releases/$current" "$current" "$safety_root"; then
    restore_installed_release "$install_root/releases/$current" "$ready_timeout_seconds" "$smoke_base_address" || true
    rm -rf "$safety_root"
    echo 'Could not create the Artifact Store rollback safety backup.' >&2
    exit 1
  fi
  if ! restore_artifact_store_backup "$install_root/releases/$current" "$previous_root" "$previous"; then
    restore_artifact_store_backup "$safety_root" "$install_root/releases/$current" "$current" || true
    restore_installed_release "$install_root/releases/$current" "$ready_timeout_seconds" "$smoke_base_address" || true
    rm -rf "$safety_root"
    echo 'Could not restore the predecessor Artifact Store snapshot.' >&2
    exit 1
  fi
fi

if restore_installed_release "$previous_root" "$ready_timeout_seconds" "$smoke_base_address"; then
  atomic_pointer "$install_root/current-release" "$previous"
  if [ -n "$current" ] && [ "$current" != "$previous" ]; then atomic_pointer "$install_root/previous-release" "$current"; fi
  echo "Rolled back SharpLabNext to release $previous"
  [ -z "$safety_root" ] || rm -rf "$safety_root"
  exit 0
fi

if [ -n "$current" ]; then
  if [ -n "$safety_root" ]; then restore_artifact_store_backup "$safety_root" "$install_root/releases/$current" "$current" || true; fi
fi
if [ -n "$current" ] && restore_installed_release "$install_root/releases/$current" "$ready_timeout_seconds" "$smoke_base_address"; then
  [ -z "$safety_root" ] || rm -rf "$safety_root"
  echo "Rollback to '$previous' failed; current release '$current' was restored." >&2
  exit 1
fi
[ -z "$safety_root" ] || rm -rf "$safety_root"
echo "Rollback to '$previous' failed and current release '$current' could not be restored." >&2
exit 1
