#!/usr/bin/env sh
set -eu

bundle_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$bundle_root/deployment-common.sh"
install_root=${SHARPLABNEXT_HOME:-${HOME:?HOME is required}/.local/share/sharplabnext}
trusted_public_key=''
trusted_public_key_sha256=''
expected_signing_key_id=''
allow_unsigned=false
skip_artifact_backup=false
current_only=false
ready_timeout_seconds=180
smoke_base_address=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --install-root) install_root=$2; shift 2 ;;
    --trusted-public-key) trusted_public_key=$(realpath -- "$2"); shift 2 ;;
    --trusted-public-key-sha256) trusted_public_key_sha256=$2; shift 2 ;;
    --expected-signing-key-id) expected_signing_key_id=$2; shift 2 ;;
    --allow-unsigned) allow_unsigned=true; shift ;;
    --skip-artifact-backup) skip_artifact_backup=true; shift ;;
    --current-only) current_only=true; shift ;;
    --ready-timeout-seconds) ready_timeout_seconds=$2; shift 2 ;;
    --smoke-base-address) smoke_base_address=$2; shift 2 ;;
    *) echo "Unknown install option: $1" >&2; exit 64 ;;
  esac
done

internal_service_token_file=${SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE:-$bundle_root/secrets/internal-service-token}
validate_container_secret_file "$internal_service_token_file" 'Internal service token'
if [ "${SHARPLABNEXT_GITHUB_OAUTH_ENABLED:-false}" = true ]; then
  github_oauth_secret_file=${SHARPLABNEXT_GITHUB_OAUTH_CLIENT_SECRET_FILE:-}
  [ -n "$github_oauth_secret_file" ] || {
    echo 'GitHub OAuth is enabled but SHARPLABNEXT_GITHUB_OAUTH_CLIENT_SECRET_FILE is empty.' >&2
    exit 1
  }
  validate_container_secret_file "$github_oauth_secret_file" 'GitHub OAuth client secret'
fi

candidate_release_id=$(read_release_id "$bundle_root")
validate_release_id "$candidate_release_id"
mkdir -p "$install_root"
install_root=$(CDPATH= cd -- "$install_root" && pwd)
current_release_id=$(release_pointer "$install_root/current-release")
previous_release_id=$(release_pointer "$install_root/previous-release")
verify_release "$bundle_root" true incoming
candidate_release_root=$(install_release_assets "$bundle_root" "$install_root" "$candidate_release_id")
test_installed_deployment "$candidate_release_root"

backup_ready=false
if [ -n "$current_release_id" ] && [ "$current_release_id" != "$candidate_release_id" ] && [ "$skip_artifact_backup" = false ]; then
  if backup_artifact_store "$install_root/releases/$current_release_id" "$current_release_id" "$candidate_release_root"; then
    backup_ready=true
  else
    restore_installed_release "$install_root/releases/$current_release_id" "$ready_timeout_seconds" "$smoke_base_address" || true
    echo "Artifact Store backup failed; release '$candidate_release_id' was not started." >&2
    exit 1
  fi
fi

if compose_up_release "$candidate_release_root" "$candidate_release_id" &&
   smoke_release "$candidate_release_root" "$candidate_release_id" "$ready_timeout_seconds" "$smoke_base_address"; then
  if [ "$current_only" = true ]; then
    retained_previous_release_id=''
    if [ -n "$current_release_id" ] && [ "$current_release_id" != "$candidate_release_id" ]; then
      retained_previous_release_id=$current_release_id
    else
      retained_previous_release_id=$previous_release_id
    fi
    assert_current_only_retention_sources \
      "$install_root" \
      "$candidate_release_id" \
      "$retained_previous_release_id" \
      "$previous_release_id" \
      "$current_release_id" \
      "$previous_release_id"
    set_release_pointer_pair \
      "$install_root" \
      "$candidate_release_id" \
      "$current_release_id" \
      "$previous_release_id"
    remove_current_only_previous_release \
      "$install_root" \
      "$candidate_release_id" \
      "$retained_previous_release_id" \
      "$previous_release_id"
  else
    set_release_pointer_pair \
      "$install_root" \
      "$candidate_release_id" \
      "$current_release_id" \
      "$previous_release_id"
  fi
  echo "Installed SharpLabNext release $candidate_release_id at $candidate_release_root"
  exit 0
fi

deployment_failure="Release '$candidate_release_id' failed readiness checks."
if [ -n "$current_release_id" ]; then
  current_root="$install_root/releases/$current_release_id"
  compose_down_release "$candidate_release_root" "$candidate_release_id" || true
  if [ "$backup_ready" = true ]; then
    restore_artifact_store_backup "$candidate_release_root" "$current_root" "$current_release_id" || {
      echo "$deployment_failure Artifact Store restoration also failed." >&2
      exit 1
    }
  fi
  if restore_installed_release "$current_root" "$ready_timeout_seconds" "$smoke_base_address"; then
    echo "$deployment_failure Release '$current_release_id' was restored." >&2
    exit 1
  fi
  echo "$deployment_failure Restoration of '$current_release_id' also failed." >&2
  exit 1
fi
compose_down_release "$candidate_release_root" "$candidate_release_id" || true
echo "$deployment_failure No previous release was available." >&2
exit 1
