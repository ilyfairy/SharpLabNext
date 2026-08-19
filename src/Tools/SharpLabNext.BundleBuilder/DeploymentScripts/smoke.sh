#!/usr/bin/env sh
set -eu

release_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
expected_release_id=''
timeout_seconds=180
base_address=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --release-root) release_root=$2; shift 2 ;;
    --expected-release-id) expected_release_id=$2; shift 2 ;;
    --timeout-seconds) timeout_seconds=$2; shift 2 ;;
    --base-address) base_address=$2; shift 2 ;;
    *) echo "Unknown smoke option: $1" >&2; exit 64 ;;
  esac
done
if [ -z "$expected_release_id" ]; then
  expected_release_id=$(sed -n 's/.*"releaseId"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$release_root/bundle.json" | head -n 1)
fi
case "$expected_release_id" in ''|[!A-Za-z0-9]*|*[!A-Za-z0-9._-]* ) echo 'Expected release ID is unsafe.' >&2; exit 1;; esac
if [ -z "$base_address" ]; then base_address="http://127.0.0.1:${SHARPLABNEXT_HTTP_PORT:-8080}"; fi
base_address=${base_address%/}
export SHARPLABNEXT_RELEASE_ID=$expected_release_id

tmp=${TMPDIR:-/tmp}/sharplabnext-smoke-$$
mkdir -p "$tmp"
trap 'rm -rf "$tmp"' EXIT HUP INT TERM
deadline=$(( $(date +%s) + timeout_seconds ))
last_failure='No readiness attempt was made.'

compose() {
  docker compose --project-name sharplabnext \
    -f "$release_root/compose.prod.yaml" \
    -f "$release_root/compose.generated.yaml" "$@"
}

while [ "$(date +%s)" -lt "$deadline" ]; do
  if compose config --services | sort > "$tmp/expected" &&
     compose ps --status running --services | sort > "$tmp/running" &&
     [ -s "$tmp/expected" ] && cmp -s "$tmp/expected" "$tmp/running"; then
    if curl --noproxy '*' --fail --silent --show-error --max-time 10 "$base_address/api/v1/system" > "$tmp/system" &&
       curl --noproxy '*' --fail --silent --show-error --max-time 10 "$base_address/api/v1/catalog" > "$tmp/catalog"; then
      compact_system=$(tr -d '[:space:]' < "$tmp/system")
      compact_catalog=$(tr -d '[:space:]' < "$tmp/catalog")
      if printf '%s' "$compact_system" | grep -Fq "\"ReleaseId\":\"$expected_release_id\"" &&
         printf '%s' "$compact_catalog" | grep -Fq "\"ReleaseId\":\"$expected_release_id\""; then
        echo "SharpLabNext release $expected_release_id passed deployment smoke checks."
        exit 0
      fi
      last_failure='Gateway or Catalog release identity does not match.'
    else
      last_failure='Gateway HTTP endpoints are not ready.'
    fi
  else
    last_failure='Not all Compose services are running.'
  fi
  sleep 2
done
echo "SharpLabNext release $expected_release_id did not become ready in $timeout_seconds seconds: $last_failure" >&2
exit 1
