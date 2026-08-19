#!/usr/bin/env sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$root"
load_images=false
trusted_public_key=''
trusted_fingerprint=''
expected_key_id=''
trust_bundled=false
allow_unsigned=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --load-images) load_images=true; shift ;;
    --trusted-public-key) trusted_public_key=$2; shift 2 ;;
    --trusted-public-key-sha256) trusted_fingerprint=$2; shift 2 ;;
    --expected-signing-key-id) expected_key_id=$2; shift 2 ;;
    --trust-bundled-public-key) trust_bundled=true; shift ;;
    --allow-unsigned) allow_unsigned=true; shift ;;
    *) echo "Unknown verify option: $1" >&2; exit 64 ;;
  esac
done

json_string() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" bundle.json | head -n 1; }
normalize_sha256() {
  value=$(printf '%s' "$1" | tr 'A-F' 'a-f')
  value=${value#sha256:}
  case "$value" in *[!0-9a-f]*|'') echo 'Invalid SHA-256 fingerprint.' >&2; exit 1;; esac
  [ "${#value}" -eq 64 ] || { echo 'Invalid SHA-256 fingerprint length.' >&2; exit 1; }
  printf '%s' "$value"
}

has_signature=$(sed -n 's/.*"hasSignature"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' bundle.json | head -n 1)
case "$has_signature" in true|false) ;; *) echo 'bundle.json has no valid signature state.' >&2; exit 1;; esac
if [ "$has_signature" = true ]; then
  algorithm=$(json_string signatureAlgorithm)
  key_id=$(json_string signatureKeyId)
  declared_fingerprint=$(normalize_sha256 "$(json_string signingPublicKeySha256)")
  [ "$algorithm" = ed25519 ] || { echo 'The bundle signature algorithm is unsupported.' >&2; exit 1; }
  [ -n "$key_id" ] || { echo 'The signed bundle has no signing key ID.' >&2; exit 1; }
  [ -f checksums.sha256.sig ] && [ -f signing-public-key.pem ] || { echo 'Bundle signature files are incomplete.' >&2; exit 1; }
  [ -z "$expected_key_id" ] || [ "$expected_key_id" = "$key_id" ] || { echo "Unexpected signing key ID: $key_id" >&2; exit 1; }
  bundled_fingerprint=$(sha256sum signing-public-key.pem | awk '{print $1}')
  [ "$bundled_fingerprint" = "$declared_fingerprint" ] || { echo 'Bundled public-key fingerprint does not match bundle.json.' >&2; exit 1; }

  if [ -n "$trusted_public_key" ]; then
    [ -f "$trusted_public_key" ] || { echo 'Trusted public key does not exist.' >&2; exit 1; }
    actual_trusted=$(sha256sum "$trusted_public_key" | awk '{print $1}')
    if [ -n "$trusted_fingerprint" ]; then
      [ "$actual_trusted" = "$(normalize_sha256 "$trusted_fingerprint")" ] || { echo 'Trusted key fingerprint mismatch.' >&2; exit 1; }
    fi
    [ "$actual_trusted" = "$declared_fingerprint" ] || { echo 'Trusted key does not match the bundle signing identity.' >&2; exit 1; }
    verification_key=$trusted_public_key
  elif [ -n "$trusted_fingerprint" ]; then
    [ "$(normalize_sha256 "$trusted_fingerprint")" = "$declared_fingerprint" ] || { echo 'Out-of-band fingerprint mismatch.' >&2; exit 1; }
    verification_key=signing-public-key.pem
  elif [ "$trust_bundled" = true ]; then
    verification_key=signing-public-key.pem
  else
    echo 'Signed bundles require a trusted public key or out-of-band SHA-256 fingerprint.' >&2
    exit 1
  fi
  openssl pkeyutl -verify -rawin -pubin -inkey "$verification_key" -in checksums.sha256 -sigfile checksums.sha256.sig
else
  [ ! -e checksums.sha256.sig ] && [ ! -e signing-public-key.pem ] || { echo 'Unsigned bundle contains inconsistent signature material.' >&2; exit 1; }
  [ "$allow_unsigned" = true ] || { echo 'Unsigned bundles require --allow-unsigned.' >&2; exit 1; }
fi

tab=$(printf '\t')
while IFS= read -r line; do
  hash=${line%%  *}
  relative=${line#*  }
  case "$hash" in *[!0-9a-f]*|'') echo "Invalid checksum line: $line" >&2; exit 1;; esac
  [ "${#hash}" -eq 64 ] || { echo "Invalid checksum line: $line" >&2; exit 1; }
  case "$relative" in /*|*\\*|../*|*/../*|*/..|.|..|*"$tab"*) echo "Unsafe checksum path: $relative" >&2; exit 1;; esac
  [ -f "$relative" ] || { echo "Missing bundle file: $relative" >&2; exit 1; }
  actual=$(sha256sum -- "$relative" | awk '{print $1}')
  [ "$actual" = "$hash" ] || { echo "Checksum mismatch: $relative" >&2; exit 1; }
done < checksums.sha256

if [ "$load_images" = true ]; then
  contains_images=$(sed -n 's/.*"containsImages"[[:space:]]*:[[:space:]]*\(true\|false\).*/\1/p' bundle.json | head -n 1)
  [ "$contains_images" = true ] || { echo 'This is a metadata-only bundle.' >&2; exit 1; }
  docker image load --input images.tar
fi
while IFS=' ' read -r logical_id image_id; do
  actual=$(docker image inspect --format '{{.Id}}' "$image_id")
  [ "$actual" = "$image_id" ] || { echo "Image identity mismatch: $logical_id" >&2; exit 1; }
done < images.expected
docker compose -f compose.prod.yaml -f compose.generated.yaml config --quiet
echo "Verified SharpLabNext release $(json_string releaseId)."
