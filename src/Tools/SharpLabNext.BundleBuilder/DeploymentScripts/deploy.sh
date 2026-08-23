#!/usr/bin/env sh
set -eu

bundle_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
install_root=${SHARPLABNEXT_HOME:-${HOME:?HOME is required}/sharplabnext}
internal_service_token_file=${SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE:-$install_root/secrets/internal-service-token}

if [ ! -e "$internal_service_token_file" ]; then
  [ "$(id -u)" -eq 0 ] || {
    echo 'The first deployment must run through sudo so it can provision the internal service token.' >&2
    exit 1
  }
  command -v openssl >/dev/null 2>&1 || {
    echo 'OpenSSL is required to provision the internal service token.' >&2
    exit 1
  }
  token_directory=$(dirname -- "$internal_service_token_file")
  install -d -m 0700 "$token_directory"
  token_staging=$(mktemp "$token_directory/.internal-service-token.XXXXXX")
  trap 'rm -f -- "$token_staging"' EXIT HUP INT TERM
  openssl rand -base64 48 > "$token_staging"
  chown 0:1654 "$token_staging"
  chmod 0640 "$token_staging"
  mv "$token_staging" "$internal_service_token_file"
  trap - EXIT HUP INT TERM
fi

SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE=$internal_service_token_file
export SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE
if [ -z "${DOCKER_GID:-}" ] && [ -S /var/run/docker.sock ]; then
  DOCKER_GID=$(stat -c '%g' /var/run/docker.sock)
  export DOCKER_GID
fi

. "$bundle_root/deployment-common.sh"
superseded_image_ids=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-superseded-images.XXXXXX")
current_image_ids=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-current-images.XXXXXX")
trap 'rm -f -- "$superseded_image_ids" "$current_image_ids"' EXIT HUP INT TERM

for deploy_pointer_name in current-release previous-release; do
  deploy_previous_release_id=$(release_pointer "$install_root/$deploy_pointer_name")
  [ -n "$deploy_previous_release_id" ] || continue
  validate_release_id "$deploy_previous_release_id"
  deploy_previous_images="$install_root/releases/$deploy_previous_release_id/images.expected"
  [ -f "$deploy_previous_images" ] || {
    echo "Installed release '$deploy_previous_release_id' has no image identity manifest." >&2
    exit 1
  }
  sed -n 's/^[^ ]* \(sha256:[0-9a-f]\{64\}\)$/\1/p' "$deploy_previous_images" \
    >> "$superseded_image_ids"
done
sort -u -o "$superseded_image_ids" "$superseded_image_ids"

sh "$bundle_root/install.sh" \
  --install-root "$install_root" \
  --skip-artifact-backup \
  --current-only \
  "$@"

sed -n 's/^[^ ]* \(sha256:[0-9a-f]\{64\}\)$/\1/p' "$bundle_root/images.expected" \
  | sort -u > "$current_image_ids"
[ -s "$current_image_ids" ] || {
  echo 'The deployed release has no image identities.' >&2
  exit 1
}

deploy_image_cleanup_failed=false
while IFS= read -r deploy_image_id; do
  [ -n "$deploy_image_id" ] || continue
  grep -Fxq "$deploy_image_id" "$current_image_ids" && continue
  if [ -n "$(docker ps -aq --filter "ancestor=$deploy_image_id")" ]; then
    echo "Superseded image '$deploy_image_id' is still referenced by a container." >&2
    deploy_image_cleanup_failed=true
    continue
  fi
  deploy_image_references=$(docker image inspect \
    --format '{{range .RepoTags}}{{println .}}{{end}}' \
    "$deploy_image_id" 2>/dev/null || true)
  if [ -n "$deploy_image_references" ]; then
    while IFS= read -r deploy_image_reference; do
      [ -n "$deploy_image_reference" ] || continue
      docker image rm "$deploy_image_reference" >/dev/null || deploy_image_cleanup_failed=true
    done <<EOF
$deploy_image_references
EOF
  elif docker image inspect "$deploy_image_id" >/dev/null 2>&1; then
    docker image rm "$deploy_image_id" >/dev/null || deploy_image_cleanup_failed=true
  fi
done < "$superseded_image_ids"

[ "$deploy_image_cleanup_failed" = false ] || {
  echo 'The new release is ready, but one or more superseded images could not be removed.' >&2
  exit 1
}
