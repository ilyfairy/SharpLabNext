#!/usr/bin/env sh

read_release_id() {
  sed -n 's/.*"releaseId"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$1/bundle.json" | head -n 1
}

validate_release_id() {
  case "$1" in ''|[!A-Za-z0-9]*|*[!A-Za-z0-9._-]* ) echo 'Release ID is unsafe.' >&2; return 1;; esac
}

validate_container_secret_file() {
  secret_path=$1
  secret_name=$2
  [ -f "$secret_path" ] || {
    echo "$secret_name does not exist: $secret_path" >&2
    return 1
  }
  set -- $(stat -c '%a %u %g' -- "$secret_path")
  secret_permissions=$(printf '%s' "$1" | sed 's/.*\(...\)$/\1/')
  secret_owner_id=$2
  secret_group_id=$3
  secret_owner_digit=${secret_permissions%??}
  secret_tail=${secret_permissions#?}
  secret_group_digit=${secret_tail%?}
  secret_other_digit=${secret_permissions#??}
  case "$secret_other_digit" in
    4|5|6|7)
      echo "$secret_name must not be readable by other host users: $secret_path" >&2
      return 1
      ;;
  esac
  secret_readable=false
  if [ "$secret_owner_id" = 1654 ]; then
    case "$secret_owner_digit" in 4|5|6|7) secret_readable=true;; esac
  fi
  if [ "$secret_group_id" = 1654 ]; then
    case "$secret_group_digit" in 4|5|6|7) secret_readable=true;; esac
  fi
  [ "$secret_readable" = true ] || {
    echo "$secret_name must be readable by container UID/GID 1654; use owner root, group 1654 and mode 0640: $secret_path" >&2
    return 1
  }
}

release_pointer() {
  pointer=$1
  if [ -f "$pointer" ]; then
    value=$(tr -d '\r\n' < "$pointer")
    validate_release_id "$value"
    printf '%s' "$value"
  fi
}

atomic_pointer() {
  target=$1
  value=$2
  temporary="$target.$$.tmp"
  printf '%s\n' "$value" > "$temporary"
  mv -f "$temporary" "$target"
}

verify_release() {
  release_root=$1
  load_images=$2
  trust_mode=$3
  shift 3
  set -- "$@"
  [ "$load_images" = true ] && set -- "$@" --load-images
  case "$trust_mode" in
    incoming)
      [ -n "${trusted_public_key:-}" ] && set -- "$@" --trusted-public-key "$trusted_public_key"
      [ -n "${trusted_public_key_sha256:-}" ] && set -- "$@" --trusted-public-key-sha256 "$trusted_public_key_sha256"
      [ -n "${expected_signing_key_id:-}" ] && set -- "$@" --expected-signing-key-id "$expected_signing_key_id"
      [ "${allow_unsigned:-false}" = true ] && set -- "$@" --allow-unsigned
      ;;
    installed)
      if grep -q '"hasSignature"[[:space:]]*:[[:space:]]*true' "$release_root/bundle.json"; then
        set -- "$@" --trust-bundled-public-key
      else
        set -- "$@" --allow-unsigned
      fi
      ;;
  esac
  sh "$release_root/verify.sh" "$@"
}

install_release_assets() {
  bundle_root=$1
  install_root=$2
  release_id=$3
  releases_root="$install_root/releases"
  release_root="$releases_root/$release_id"
  mkdir -p "$releases_root"
  if [ -e "$release_root" ]; then
    [ -f "$release_root/checksums.sha256" ] || { echo "Installed release is incomplete: $release_id" >&2; return 1; }
    incoming=$(sha256sum "$bundle_root/checksums.sha256" | awk '{print $1}')
    installed=$(sha256sum "$release_root/checksums.sha256" | awk '{print $1}')
    [ "$incoming" = "$installed" ] || { echo "Release '$release_id' is already installed with different content." >&2; return 1; }
    printf '%s' "$release_root"
    return
  fi
  staging="$releases_root/.$release_id.$$.tmp"
  rm -rf "$staging"
  mkdir -p "$staging"
  if ! cp -a "$bundle_root/." "$staging/"; then rm -rf "$staging"; return 1; fi
  deployment_files='bundle.json catalog.json lock.json profile-update-status.json compose.prod.yaml compose.generated.yaml github-oauth-client-secret.disabled images.expected checksums.sha256 THIRD-PARTY-NOTICES.md security/README.md security/THIRD-PARTY-NOTICES.md security/inventory.json security/sharplabnext-runtime-job-v1.apparmor security/licenses/moby-profiles-Apache-2.0.txt'
  (cd "$staging" && sha256sum $deployment_files > deployment.sha256)
  mv "$staging" "$release_root"
  printf '%s' "$release_root"
}

test_installed_deployment() {
  release_root=$1
  (cd "$release_root" && sha256sum --check deployment.sha256)
}

compose_for_release() {
  release_root=$1
  release_id=$2
  shift 2
  SHARPLABNEXT_RELEASE_ID=$release_id docker compose --project-name sharplabnext \
    -f "$release_root/compose.prod.yaml" \
    -f "$release_root/compose.generated.yaml" "$@"
}

compose_up_release() {
  compose_for_release "$1" "$2" up -d --pull never --no-build --remove-orphans
}

compose_down_release() {
  compose_for_release "$1" "$2" down --remove-orphans
}

compose_stop_release() {
  compose_for_release "$1" "$2" stop
}

backup_artifact_store() {
  release_root=$1
  release_id=$2
  backup_owner_root=$3
  rollback_root="$backup_owner_root/rollback"
  backup_root="$rollback_root/artifact-data"
  if [ -d "$backup_root" ]; then
    recorded=$(tr -d '\r\n' < "$rollback_root/predecessor-release")
    [ "$recorded" = "$release_id" ] || { echo 'Artifact backup belongs to a different predecessor release.' >&2; return 1; }
    return
  fi

  compose_stop_release "$release_root" "$release_id"
  container_id=$(compose_for_release "$release_root" "$release_id" ps --all -q artifact-store)
  [ -n "$container_id" ] || { echo 'The Artifact Store container is unavailable for backup.' >&2; return 1; }
  mkdir -p "$rollback_root"
  staging="$rollback_root/.artifact-data.$$.tmp"
  rm -rf "$staging"
  mkdir -p "$staging"
  if ! docker cp "$container_id:/var/lib/sharplabnext/." "$staging"; then rm -rf "$staging"; return 1; fi
  [ -n "$(find "$staging" -type f -print -quit)" ] || { rm -rf "$staging"; echo 'Artifact Store backup is empty.' >&2; return 1; }
  mv "$staging" "$backup_root"
  (cd "$rollback_root" && find artifact-data -type f -print0 | sort -z | xargs -0 -r sha256sum > artifact-data.sha256)
  printf '%s\n' "$release_id" > "$rollback_root/predecessor-release"
}

restore_artifact_store_backup() {
  backup_owner_root=$1
  target_release_root=$2
  expected_release_id=$3
  rollback_root="$backup_owner_root/rollback"
  backup_root="$rollback_root/artifact-data"
  recorded=$(tr -d '\r\n' < "$rollback_root/predecessor-release")
  [ "$recorded" = "$expected_release_id" ] || { echo 'Artifact backup predecessor does not match rollback target.' >&2; return 1; }
  (cd "$rollback_root" && sha256sum --check artifact-data.sha256)
  volume=$(docker volume ls --filter label=com.docker.compose.project=sharplabnext --filter label=com.docker.compose.volume=artifact-data --format '{{.Name}}')
  [ -n "$volume" ] && [ "$(printf '%s\n' "$volume" | wc -l)" -eq 1 ] || { echo 'Could not resolve the SharpLabNext Artifact Store volume.' >&2; return 1; }
  image_id=$(sed -n 's/^artifact-store \(sha256:[0-9a-f]\{64\}\)$/\1/p' "$target_release_root/images.expected")
  [ -n "$image_id" ] && [ "$(printf '%s\n' "$image_id" | wc -l)" -eq 1 ] || { echo 'Target release has no unique Artifact Store image.' >&2; return 1; }
  container_user=$(docker image inspect --format '{{.Config.User}}' "$image_id")
  case "$container_user" in *[!0-9:]*|*:*:*) echo 'Artifact Store image has an unsupported runtime user.' >&2; return 1;; esac
  case "$container_user" in *:*) ;; *) echo 'Artifact Store image has an unsupported runtime user.' >&2; return 1;; esac
  case "$backup_root" in *,*) echo 'Artifact backup path cannot contain a comma.' >&2; return 1;; esac
  docker run --rm --pull never --network none --read-only --security-opt no-new-privileges --user 0 --entrypoint /bin/sh --pids-limit 32 \
    --mount "type=volume,source=$volume,target=/var/lib/sharplabnext" \
    --mount "type=bind,source=$backup_root,target=/backup,readonly" \
    "$image_id" -c "find /var/lib/sharplabnext -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + && cp -a /backup/. /var/lib/sharplabnext/ && chown -R $container_user /var/lib/sharplabnext"
}

smoke_release() {
  release_root=$1
  release_id=$2
  timeout_seconds=$3
  base_address=$4
  set -- --release-root "$release_root" --expected-release-id "$release_id" --timeout-seconds "$timeout_seconds"
  [ -n "$base_address" ] && set -- "$@" --base-address "$base_address"
  SHARPLABNEXT_RELEASE_ID=$release_id sh "$release_root/smoke.sh" "$@"
}

restore_installed_release() {
  release_root=$1
  timeout_seconds=$2
  base_address=$3
  release_id=$(read_release_id "$release_root")
  validate_release_id "$release_id"
  test_installed_deployment "$release_root"
  verify_release "$release_root" true installed
  compose_up_release "$release_root" "$release_id"
  smoke_release "$release_root" "$release_id" "$timeout_seconds" "$base_address"
}
