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
installed_copy=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --load-images) load_images=true; shift ;;
    --trusted-public-key) trusted_public_key=$2; shift 2 ;;
    --trusted-public-key-sha256) trusted_fingerprint=$2; shift 2 ;;
    --expected-signing-key-id) expected_key_id=$2; shift 2 ;;
    --trust-bundled-public-key) trust_bundled=true; shift ;;
    --allow-unsigned) allow_unsigned=true; shift ;;
    --installed-copy) installed_copy=true; shift ;;
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
checksum_paths=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-checksums.XXXXXX")
promotion_paths=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-paths.XXXXXX")
promotion_kinds=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-kinds.XXXXXX")
actual_promotion_paths=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-actual.XXXXXX")
actual_bundle_paths=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-bundle-actual.XXXXXX")
deployment_paths=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-deployment-paths.XXXXXX")
deployment_expected_paths=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-deployment-expected.XXXXXX")
promotion_json_tsv=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-json.XXXXXX")
promotion_matrix_ids=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-matrix.XXXXXX")
promotion_manifest_ids=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-manifest.XXXXXX")
promotion_expected_triples=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-expected.XXXXXX")
promotion_actual_triples_unsorted=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-actual-unsorted.XXXXXX")
promotion_actual_triples=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-actual-triples.XXXXXX")
promotion_checks=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-checks.XXXXXX")
actual_promotion_paths_unsorted=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-actual-unsorted-paths.XXXXXX")
promotion_signature_binary=$(mktemp "${TMPDIR:-/tmp}/sharplabnext-promotion-signature.XXXXXX")
cleanup_verify_files() { rm -f "$checksum_paths" "$promotion_paths" "$promotion_kinds" "$actual_promotion_paths" "$actual_bundle_paths" "$deployment_paths" "$deployment_expected_paths" "$promotion_json_tsv" "$promotion_matrix_ids" "$promotion_manifest_ids" "$promotion_expected_triples" "$promotion_actual_triples_unsorted" "$promotion_actual_triples" "$promotion_checks" "$actual_promotion_paths_unsorted" "$promotion_signature_binary"; }
trap cleanup_verify_files EXIT HUP INT TERM
verify_canonical_ed25519_signature() {
  content_path=$1
  signature_path=$2
  public_key_path=$3
  signature_label=$4
  [ "$(wc -c < "$signature_path" | tr -d ' ')" = 89 ] &&
  [ "$(wc -l < "$signature_path" | tr -d ' ')" = 1 ] &&
  [ "$(tail -c 1 "$signature_path" | od -An -tu1 | tr -d ' \n')" = 10 ] &&
  ! grep -q "$(printf '\r')" "$signature_path" &&
  LC_ALL=C grep -Eq '^[A-Za-z0-9+/]{86}==$' "$signature_path" || {
    echo "$signature_label signature is not canonical 64-byte Ed25519 Base64 text." >&2
    exit 1
  }
  : > "$promotion_signature_binary"
  openssl base64 -d -A -in "$signature_path" -out "$promotion_signature_binary" >/dev/null 2>&1 || {
    echo "$signature_label signature could not be decoded." >&2
    exit 1
  }
  [ "$(wc -c < "$promotion_signature_binary" | tr -d ' ')" = 64 ] || {
    echo "$signature_label signature is not 64 bytes." >&2
    exit 1
  }
  openssl pkeyutl -verify -rawin -pubin -inkey "$public_key_path" \
    -in "$content_path" -sigfile "$promotion_signature_binary" >/dev/null 2>&1 || {
    echo "$signature_label signature verification failed." >&2
    exit 1
  }
}
while IFS= read -r line; do
  hash=${line%%  *}
  relative=${line#*  }
  case "$hash" in *[!0-9a-f]*|'') echo "Invalid checksum line: $line" >&2; exit 1;; esac
  [ "${#hash}" -eq 64 ] || { echo "Invalid checksum line: $line" >&2; exit 1; }
  case "$relative" in /*|*\\*|../*|*/../*|*/..|.|..|*"$tab"*) echo "Unsafe checksum path: $relative" >&2; exit 1;; esac
  [ -f "$relative" ] || { echo "Missing bundle file: $relative" >&2; exit 1; }
  actual=$(sha256sum -- "$relative" | awk '{print $1}')
  [ "$actual" = "$hash" ] || { echo "Checksum mismatch: $relative" >&2; exit 1; }
  printf '%s\n' "$relative" >> "$checksum_paths"
done < checksums.sha256
if [ -n "$(sort "$checksum_paths" | uniq -d)" ]; then
  echo 'Checksum manifest has duplicate paths.' >&2
  exit 1
fi
if [ "$installed_copy" = true ]; then
  [ -f deployment.sha256 ] && [ ! -L deployment.sha256 ] || {
    echo 'An installed copy requires a regular non-link deployment.sha256 file.' >&2
    exit 1
  }
  deployment_files='bundle.json catalog.json lock.json profile-update-status.json compose.prod.yaml compose.generated.yaml github-oauth-client-secret.disabled images.expected checksums.sha256 THIRD-PARTY-NOTICES.md security/README.md security/THIRD-PARTY-NOTICES.md security/inventory.json security/sharplabnext-runtime-job-v1.apparmor security/licenses/moby-profiles-Apache-2.0.txt'
  for relative in $deployment_files; do printf '%s\n' "$relative" >> "$deployment_expected_paths"; done
  while IFS= read -r line; do
    hash=${line%%  *}
    relative=${line#*  }
    case "$hash" in *[!0-9a-f]*|'') echo "Invalid installed deployment checksum line: $line" >&2; exit 1;; esac
    [ "${#hash}" -eq 64 ] || { echo "Invalid installed deployment checksum line: $line" >&2; exit 1; }
    case "$relative" in
      ''|/*|*\\*|*//*|../*|*/../*|*/..|.|..|*"$tab"*|*[!A-Za-z0-9._/-]*)
        echo "Unsafe installed deployment checksum path: $relative" >&2
        exit 1
        ;;
    esac
    if [ "$relative" != checksums.sha256 ]; then
      grep -Fqx -e "$relative" "$checksum_paths" || {
        echo "Installed deployment path is not checksummed by the bundle: $relative" >&2
        exit 1
      }
    fi
    [ -f "$relative" ] && [ ! -L "$relative" ] || {
      echo "Missing or unsafe installed deployment file: $relative" >&2
      exit 1
    }
    actual=$(sha256sum -- "$relative" | awk '{print $1}')
    [ "$actual" = "$hash" ] || { echo "Installed deployment checksum mismatch: $relative" >&2; exit 1; }
    printf '%s\n' "$relative" >> "$deployment_paths"
  done < deployment.sha256
  cmp -s "$deployment_expected_paths" "$deployment_paths" || {
    echo 'Installed deployment checksum manifest does not contain the exact expected files.' >&2
    exit 1
  }
  printf '%s\n' deployment.sha256 >> "$checksum_paths"
fi
printf '%s\n' checksums.sha256 >> "$checksum_paths"
if [ "$has_signature" = true ]; then printf '%s\n' checksums.sha256.sig >> "$checksum_paths"; fi
if [ -n "$(find . -type l -print -quit)" ]; then
  echo 'Bundle contains a symbolic link.' >&2
  exit 1
fi
find . -type f -print | sed 's#^./##' | sort > "$actual_bundle_paths"
sort "$checksum_paths" | cmp -s - "$actual_bundle_paths" || {
  echo 'Bundle contains missing or unchecksummed files.' >&2; exit 1;
}

if [ -e promotion-evidence ]; then
  command -v jq >/dev/null 2>&1 || { echo 'Promotion evidence verification requires jq.' >&2; exit 1; }
  command -v openssl >/dev/null 2>&1 || { echo 'Promotion evidence verification requires OpenSSL.' >&2; exit 1; }
  [ -d promotion-evidence ] || { echo 'Promotion evidence root is not a directory.' >&2; exit 1; }
  [ -f promotion-evidence/manifest.json ] && [ -f promotion-evidence/manifest.tsv ] || {
    echo 'Promotion evidence manifest is incomplete.' >&2; exit 1;
  }
  ! grep -q "$(printf '\r')" promotion-evidence/manifest.json &&
  ! grep -q "$(printf '\r')" promotion-evidence/manifest.tsv || {
    echo 'Promotion evidence manifests must be LF-only.' >&2; exit 1;
  }
  [ "$(od -An -N3 -tx1 promotion-evidence/manifest.json | tr -d ' \n')" != efbbbf ] || {
    echo 'Promotion evidence JSON manifest must be UTF-8 without BOM.' >&2; exit 1;
  }
  manifest_json_digest="sha256:$(sha256sum promotion-evidence/manifest.json | awk '{print $1}')"
  jq -er --arg manifest_json_digest "$manifest_json_digest" '
    . as $root |
    (["schemaVersion", 1] | @tsv),
    (["buildSourceRevision", .buildSourceRevision] | @tsv),
    (["releaseSourceRevision", .releaseSourceRevision] | @tsv),
    (["manifestJsonSha256", $manifest_json_digest] | @tsv),
    (["promotedRuntimeIds", (.promotedRuntimeIds | join(","))] | @tsv),
    (.entries[] | ["entry", .kind, (.profileIds | join(",")), (.runtimeIds | join(",")), .sourcePath, .bundlePath, .sha256, (.sizeBytes | tostring)] | @tsv)
  ' promotion-evidence/manifest.json > "$promotion_json_tsv"
  cmp -s "$promotion_json_tsv" promotion-evidence/manifest.tsv || {
    echo 'Promotion evidence JSON and verification manifests disagree.' >&2; exit 1;
  }
  [ "$(sed -n '1p' promotion-evidence/manifest.tsv)" = "schemaVersion${tab}1" ] || {
    echo 'Promotion evidence verification manifest has an invalid schema.' >&2; exit 1;
  }
  build_revision=$(sed -n "2s/^buildSourceRevision${tab}//p" promotion-evidence/manifest.tsv)
  release_revision=$(sed -n "3s/^releaseSourceRevision${tab}//p" promotion-evidence/manifest.tsv)
  manifest_json_sha256=$(sed -n "4s/^manifestJsonSha256${tab}//p" promotion-evidence/manifest.tsv)
  promoted_runtime_ids=$(sed -n "5s/^promotedRuntimeIds${tab}//p" promotion-evidence/manifest.tsv)
  case "$build_revision" in *[!0-9a-f]*|'') echo 'Promotion evidence build revision is invalid.' >&2; exit 1;; esac
  case "$release_revision" in *[!0-9a-f]*|'') echo 'Promotion evidence release revision is invalid.' >&2; exit 1;; esac
  case "$promoted_runtime_ids" in ''|*[^a-z0-9,.-]*|,*|*,) echo 'Promotion evidence promoted runtime IDs are invalid.' >&2; exit 1;; esac
  [ "${#build_revision}" -ge 40 ] && [ "${#build_revision}" -le 64 ] || { echo 'Promotion evidence build revision length is invalid.' >&2; exit 1; }
  [ "${#release_revision}" -ge 40 ] && [ "${#release_revision}" -le 64 ] || { echo 'Promotion evidence release revision length is invalid.' >&2; exit 1; }
  case "$manifest_json_sha256" in sha256:????????????????????????????????????????????????????????????????) ;; *) echo 'Promotion evidence JSON manifest digest is invalid.' >&2; exit 1;; esac
  case "${manifest_json_sha256#sha256:}" in *[!0-9a-f]*) echo 'Promotion evidence JSON manifest digest is invalid.' >&2; exit 1;; esac
  [ "$(sha256sum promotion-evidence/manifest.json | awk '{print $1}')" = "${manifest_json_sha256#sha256:}" ] || { echo 'Promotion evidence JSON and verification manifests disagree.' >&2; exit 1; }
  [ -z "$(printf '%s' "$promoted_runtime_ids" | tr ',' '\n' | sort | uniq -d)" ] || { echo 'Promotion evidence promoted runtime IDs are duplicated.' >&2; exit 1; }
  [ -z "$(printf '%s' "$promoted_runtime_ids" | tr ',' '\n' | sort -c 2>&1)" ] || { echo 'Promotion evidence promoted runtime IDs are not canonical.' >&2; exit 1; }
  manifest_line=0
  while IFS="$tab" read -r tag kind profile_ids runtime_ids source_path bundle_path digest size_bytes extra; do
    manifest_line=$((manifest_line + 1))
    [ "$manifest_line" -gt 5 ] || continue
    [ -z "${extra:-}" ] || { echo 'Promotion evidence manifest contains an overlong entry.' >&2; exit 1; }
    [ "$tag" = entry ] || { echo 'Promotion evidence manifest contains an invalid entry.' >&2; exit 1; }
    case "$kind" in active-profile|candidate-profile|capability-evidence|performance-evidence|performance-policy|plan|plan-signature|plan-signature-public-key|preflight-profile|receipt|operator-receipt|operator-receipt-signature|operator-receipt-public-key|source-closure) ;; *) echo 'Promotion evidence manifest has an invalid kind.' >&2; exit 1;; esac
    case "$source_path" in ''|/*|*\\*|*//*|../*|*/../*|*/..|.|..) echo 'Promotion evidence source path is unsafe.' >&2; exit 1;; esac
    [ "$bundle_path" = "source/$source_path" ] || { echo 'Promotion evidence bundle/source paths disagree.' >&2; exit 1; }
    case "$digest" in sha256:????????????????????????????????????????????????????????????????) ;; *) echo 'Promotion evidence digest is invalid.' >&2; exit 1;; esac
    case "${digest#sha256:}" in *[!0-9a-f]*) echo 'Promotion evidence digest is invalid.' >&2; exit 1;; esac
    case "$size_bytes" in *[!0-9]*|'0'|'') echo 'Promotion evidence size is invalid.' >&2; exit 1;; esac
    [ -f "promotion-evidence/$bundle_path" ] || { echo "Promotion evidence file is missing: $bundle_path" >&2; exit 1; }
    [ "$(sha256sum -- "promotion-evidence/$bundle_path" | awk '{print $1}')" = "${digest#sha256:}" ] || { echo "Promotion evidence digest mismatch: $bundle_path" >&2; exit 1; }
    [ "$(wc -c < "promotion-evidence/$bundle_path" | tr -d ' ')" = "$size_bytes" ] || { echo "Promotion evidence size mismatch: $bundle_path" >&2; exit 1; }
    printf '%s\n' "$bundle_path" >> "$promotion_paths"
    if [ "$profile_ids" != "$runtime_ids" ]; then echo 'Promotion evidence profile/runtime bindings disagree.' >&2; exit 1; fi
    if [ "$runtime_ids" != - ]; then
      [ -z "$(printf '%s' "$runtime_ids" | tr ',' '\n' | sort | uniq -d)" ] || { echo 'Promotion evidence runtime IDs are duplicated.' >&2; exit 1; }
      [ -z "$(printf '%s' "$runtime_ids" | tr ',' '\n' | sort -c 2>&1)" ] || { echo 'Promotion evidence runtime IDs are not canonical.' >&2; exit 1; }
      old_ifs=$IFS; IFS=,
      for runtime_id in $runtime_ids; do
        case ",$promoted_runtime_ids," in *",$runtime_id,"*) ;; *) echo 'Promotion evidence references an unknown runtime.' >&2; exit 1;; esac
        printf '%s\t%s\n' "$runtime_id" "$kind" >> "$promotion_kinds"
      done
      IFS=$old_ifs
    fi
  done < promotion-evidence/manifest.tsv
  [ -s "$promotion_paths" ] || { echo 'Promotion evidence manifest has no entries.' >&2; exit 1; }
  [ -z "$(sort "$promotion_paths" | uniq -d)" ] || { echo 'Promotion evidence manifest has duplicate bundle paths.' >&2; exit 1; }
  find promotion-evidence/source -type f -print > "$actual_promotion_paths_unsorted"
  sed 's#^promotion-evidence/##' "$actual_promotion_paths_unsorted" | sort > "$actual_promotion_paths"
  sort "$promotion_paths" | cmp -s - "$actual_promotion_paths" || { echo 'Promotion evidence has missing or unlisted source files.' >&2; exit 1; }
  old_ifs=$IFS; IFS=,
  for runtime_id in $promoted_runtime_ids; do
    for kind in candidate-profile plan plan-signature plan-signature-public-key preflight-profile receipt capability-evidence performance-evidence; do
      awk -F "$tab" -v runtime_id="$runtime_id" -v kind="$kind" '$1 == runtime_id && $2 == kind { found = 1 } END { exit found ? 0 : 1 }' "$promotion_kinds" || {
        echo "Promotion evidence for $runtime_id is missing $kind." >&2; exit 1;
      }
    done
  done
  IFS=$old_ifs

  jq -er '
    [
      (.coreClr[] | select(.linuxCapability.promotionState == "verified") | .id + "-linux-x64"),
      (.coreClr[] | select(.wineCapability.promotionState == "verified") | "wine-" + .id + "-linux-x64"),
      (.mono | select(.capability.promotionState == "verified") | .id),
      (.framework.targets[] | select(.capability.promotionState == "verified") | "wine-" + .id + "-linux-x64")
    ] | sort[]
  ' promotion-evidence/source/profiles/runtime-matrix.json > "$promotion_matrix_ids"
  printf '%s' "$promoted_runtime_ids" | tr ',' '\n' | sort > "$promotion_manifest_ids"
  cmp -s "$promotion_matrix_ids" "$promotion_manifest_ids" || {
    echo 'Promotion evidence promoted runtime IDs do not match the retained runtime matrix.' >&2; exit 1;
  }

  append_promotion_triple() { printf '%s\t%s\t%s\n' "$1" "$2" "$3" >> "$promotion_expected_triples"; }
  assert_canonical_source_path() {
    case "$1" in ''|/*|*\\*|*//*|../*|*/../*|*/..|.|..) echo "Promotion retained source path is unsafe: $1" >&2; exit 1;; esac
  }
  source_digest() {
    assert_canonical_source_path "$1"
    [ -f "promotion-evidence/source/$1" ] || { echo "Promotion retained source is missing: $1" >&2; exit 1; }
    printf 'sha256:%s' "$(sha256sum "promotion-evidence/source/$1" | awk '{print $1}')"
  }
  require_source_digest() {
    [ "$(source_digest "$1")" = "$2" ] || { echo "Promotion retained source digest mismatch: $1" >&2; exit 1; }
  }
  for runtime_id in $(cat "$promotion_manifest_ids"); do
    for shared in deploy/images.json profiles/catalog/catalog.json profiles/lock.json profiles/runtime-matrix.json; do
      append_promotion_triple "$shared" source-closure "$runtime_id"
    done
    active_path="profiles/runtimes/$runtime_id.json"
    active_file="promotion-evidence/source/$active_path"
    [ -f "$active_file" ] || { echo "Promotion active profile is missing: $runtime_id" >&2; exit 1; }
    [ "$(jq -er '.id' "$active_file")" = "$runtime_id" ] || { echo "Promotion active profile identity is invalid: $runtime_id" >&2; exit 1; }
    append_promotion_triple "$active_path" active-profile "$runtime_id"
    receipt_path=$(jq -er '.promotionReceipt.path' "$active_file")
    receipt_sha=$(jq -er '.promotionReceipt.sha256' "$active_file")
    [ "$receipt_path" = "profiles/runtime-promotion-receipts/$runtime_id.json" ] || { echo "Promotion receipt path is noncanonical: $runtime_id" >&2; exit 1; }
    require_source_digest "$receipt_path" "$receipt_sha"
    append_promotion_triple "$receipt_path" receipt "$runtime_id"
    receipt_file="promotion-evidence/source/$receipt_path"
    [ "$(jq -er '.schemaVersion' "$receipt_file")" = 2 ] && [ "$(jq -er '.profileId' "$receipt_file")" = "$runtime_id" ] || { echo "Promotion receipt identity is invalid: $runtime_id" >&2; exit 1; }
    [ "$(jq -er '.sourceRevision' "$receipt_file")" = "$build_revision" ] || { echo "Promotion receipt source revision is invalid: $runtime_id" >&2; exit 1; }
    plan_path="profiles/runtime-promotion-plans/$runtime_id.json"
    require_source_digest "$plan_path" "$(jq -er '.planSha256' "$receipt_file")"
    append_promotion_triple "$plan_path" plan "$runtime_id"
    plan_signature_path=$(jq -er '.planSignature.path' "$receipt_file")
    [ "$plan_signature_path" = "$plan_path.sig" ] && [ "$(jq -er '.planSignature.keyId' "$receipt_file")" = '__RUNTIME_PROMOTION_PLAN_KEY_ID__' ] || { echo "Promotion plan signature binding is invalid: $runtime_id" >&2; exit 1; }
    require_source_digest "$plan_signature_path" "$(jq -er '.planSignature.sha256' "$receipt_file")"
    append_promotion_triple "$plan_signature_path" plan-signature "$runtime_id"
    append_promotion_triple __RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH__ plan-signature-public-key "$runtime_id"
    require_source_digest __RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH__ __RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_SHA256__
    plan_file="promotion-evidence/source/$plan_path"
    [ "$(jq -er '.schemaVersion' "$plan_file")" = 1 ] && [ "$(jq -er '.profileId' "$plan_file")" = "$runtime_id" ] && [ "$(jq -er '.sourceRevision' "$plan_file")" = "$build_revision" ] || { echo "Promotion plan identity is invalid: $runtime_id" >&2; exit 1; }
    verify_canonical_ed25519_signature \
      "$plan_file" \
      "promotion-evidence/source/$plan_signature_path" \
      promotion-evidence/source/__RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH__ \
      "Promotion plan $runtime_id"
    candidate_path="profiles/runtimes/candidates/$runtime_id.json"
    require_source_digest "$candidate_path" "$(jq -er '.profileSha256' "$plan_file")"
    [ "$(jq -er '.id' "promotion-evidence/source/$candidate_path")" = "$runtime_id" ] || { echo "Promotion candidate profile identity is invalid: $runtime_id" >&2; exit 1; }
    append_promotion_triple "$candidate_path" candidate-profile "$runtime_id"
    jq -se '
      length == 3 and
      ((.[0].capabilities | sort) == (.[1].checks | map(.capability) | sort)) and
      ((.[0].capabilities | sort) == (.[2].capabilities | sort))
    ' "$plan_file" "$receipt_file" "$active_file" >/dev/null || { echo "Promotion capability sets disagree: $runtime_id" >&2; exit 1; }
    family=$(jq -er '.family' "$plan_file")
    case "$family" in
      coreclr-wine|netfx-clr-wine) requires_wine_operator=true ;;
      coreclr|mono) requires_wine_operator=false ;;
      *) echo "Promotion plan has an unsupported runtime family: $runtime_id" >&2; exit 1 ;;
    esac
    if [ "$requires_wine_operator" = true ]; then
      jq -e '.wineOperator != null' "$plan_file" >/dev/null &&
        jq -e '.wineOperator != null' "$receipt_file" >/dev/null || {
          echo "Promotion Wine runtime is missing its required Wine operator binding: $runtime_id" >&2; exit 1;
        }
      for operator_kind in operator-receipt operator-receipt-signature operator-receipt-public-key; do
        awk -F "$tab" -v runtime_id="$runtime_id" -v kind="$operator_kind" '$1 == runtime_id && $2 == kind { found = 1 } END { exit found ? 0 : 1 }' "$promotion_kinds" || {
          echo "Promotion Wine runtime is missing required Wine operator evidence: $runtime_id" >&2; exit 1;
        }
      done
      operator_receipt_path=$(jq -er '.wineOperator.receiptPath' "$receipt_file")
      operator_signature_path=$(jq -er '.wineOperator.signaturePath' "$receipt_file")
      operator_key_id=sha256:16cdb3dd05ddc65de942187de063606b06c7c56c60e1a3394d166724d649e5a1
      [ "$operator_receipt_path" = "profiles/runtime-operator-receipts/wine-coreclr-$build_revision.json" ] || { echo "Promotion Wine operator receipt path is invalid: $runtime_id" >&2; exit 1; }
      [ "$operator_signature_path" = "$operator_receipt_path.sig" ] || { echo "Promotion Wine operator signature path is invalid: $runtime_id" >&2; exit 1; }
      [ "$(jq -er '.wineOperator.keyId' "$receipt_file")" = "$operator_key_id" ] || { echo "Promotion Wine operator key ID is invalid: $runtime_id" >&2; exit 1; }
      require_source_digest "$operator_receipt_path" "$(jq -er '.wineOperator.receiptSha256' "$receipt_file")"
      require_source_digest "$operator_signature_path" "$(jq -er '.wineOperator.signatureSha256' "$receipt_file")"
      append_promotion_triple "$operator_receipt_path" operator-receipt "$runtime_id"
      append_promotion_triple "$operator_signature_path" operator-receipt-signature "$runtime_id"
      append_promotion_triple eng/profiles/trust/wine-coreclr-operator-receipt-public.pem operator-receipt-public-key "$runtime_id"
      require_source_digest eng/profiles/trust/wine-coreclr-operator-receipt-public.pem sha256:890cb122b7d50f2f437cf47ac71a57c624fc96bbef75dac6e187290742d01b3f
      operator_receipt_file="promotion-evidence/source/$operator_receipt_path"
      jq -e --arg source_revision "$build_revision" --arg key_id "$operator_key_id" '
        .schemaVersion == 1 and .keyId == $key_id and .source.revision == $source_revision
      ' "$operator_receipt_file" >/dev/null || { echo "Promotion Wine operator receipt identity is invalid: $runtime_id" >&2; exit 1; }
      jq -se '
        .[0].wineOperator == .[1].wineOperator and
        .[0].wineOperator.keyId == .[2].keyId and
        .[0].wineOperator.reference == .[2].operator.reference and
        .[0].wineOperator.imageId == .[2].operator.imageId and
        .[0].wineOperator.sizeBytes == .[2].operator.sizeBytes and
        .[0].wineOperator.sourceRevision == .[2].source.revision and
        .[0].wineOperator.sourceTree == .[2].source.tree
      ' "$receipt_file" "$plan_file" "$operator_receipt_file" >/dev/null || { echo "Promotion Wine operator binding is invalid: $runtime_id" >&2; exit 1; }
      verify_canonical_ed25519_signature \
        "$operator_receipt_file" \
        "promotion-evidence/source/$operator_signature_path" \
        promotion-evidence/source/eng/profiles/trust/wine-coreclr-operator-receipt-public.pem \
        "Wine operator receipt $runtime_id"
    else
      jq -e '.wineOperator == null' "$plan_file" >/dev/null &&
        jq -e '.wineOperator == null' "$receipt_file" >/dev/null || {
          echo "Promotion non-Wine runtime must not declare a Wine operator binding: $runtime_id" >&2; exit 1;
        }
      for operator_kind in operator-receipt operator-receipt-signature operator-receipt-public-key; do
        if awk -F "$tab" -v runtime_id="$runtime_id" -v kind="$operator_kind" '$1 == runtime_id && $2 == kind { found = 1 } END { exit found ? 0 : 1 }' "$promotion_kinds"; then
          echo "Promotion non-Wine runtime must not retain Wine operator evidence: $runtime_id" >&2; exit 1
        fi
      done
    fi
    preflight_path=$(jq -er '.preflightProfile.path' "$plan_file")
    [ "$preflight_path" = "profiles/runtime-promotion-plans/$runtime_id.profile.json" ] || { echo "Promotion preflight path is noncanonical: $runtime_id" >&2; exit 1; }
    require_source_digest "$preflight_path" "$(jq -er '.preflightProfile.sha256' "$plan_file")"
    [ "$(jq -er '.id' "promotion-evidence/source/$preflight_path")" = "$runtime_id" ] || { echo "Promotion preflight identity is invalid: $runtime_id" >&2; exit 1; }
    append_promotion_triple "$preflight_path" preflight-profile "$runtime_id"
    policy_path=$(jq -er '.performance.policyPath' "$receipt_file")
    case "$policy_path" in
      profiles/runtime-performance-policies/*.json)
        policy_id=${policy_path#profiles/runtime-performance-policies/}
        policy_id=${policy_id%.json}
        case "$policy_id" in ''|[!a-z0-9]*|*[!a-z0-9._-]*) echo "Promotion performance policy path is noncanonical: $runtime_id" >&2; exit 1;; esac
        ;;
      *) echo "Promotion performance policy path is noncanonical: $runtime_id" >&2; exit 1;;
    esac
    policy_sha=$(jq -er '.performance.policySha256' "$receipt_file")
    performance_path=$(jq -er '.performance.evidencePath' "$receipt_file")
    jq -e --arg policy_path "$policy_path" --arg policy_sha "$policy_sha" --arg performance_path "$performance_path" '
      .performance.policyPath == $policy_path and .performance.policySha256 == $policy_sha and .performance.evidencePath == $performance_path
    ' "$plan_file" >/dev/null || { echo "Promotion performance bindings disagree: $runtime_id" >&2; exit 1; }
    [ "$performance_path" = "profiles/runtime-promotion-evidence/$runtime_id/performance.json" ] || { echo "Promotion performance path is noncanonical: $runtime_id" >&2; exit 1; }
    require_source_digest "$policy_path" "$policy_sha"
    require_source_digest "$performance_path" "$(jq -er '.performance.evidenceSha256' "$receipt_file")"
    append_promotion_triple "$policy_path" performance-policy "$runtime_id"
    append_promotion_triple "$performance_path" performance-evidence "$runtime_id"
    jq -er '.checks[] | [.capability, .evidencePath, .evidenceSha256] | @tsv' "$receipt_file" > "$promotion_checks"
    while IFS="$tab" read -r capability evidence_path evidence_sha; do
      case "$capability" in run|jit-asm|inspection|execution-flow) ;; *) echo "Promotion capability is invalid: $runtime_id" >&2; exit 1;; esac
      [ "$evidence_path" = "profiles/runtime-promotion-evidence/$runtime_id/$capability.json" ] || { echo "Promotion capability path is noncanonical: $runtime_id" >&2; exit 1; }
      require_source_digest "$evidence_path" "$evidence_sha"
      append_promotion_triple "$evidence_path" capability-evidence "$runtime_id"
    done < "$promotion_checks"
  done
  jq -er '.entries[] | .sourcePath as $path | .kind as $kind | .runtimeIds[] | [$path, $kind, .] | @tsv' promotion-evidence/manifest.json > "$promotion_actual_triples_unsorted"
  sort "$promotion_actual_triples_unsorted" > "$promotion_actual_triples"
  sort "$promotion_expected_triples" | cmp -s - "$promotion_actual_triples" || {
    echo 'Promotion evidence entries do not match the closure derived from retained source bytes.' >&2; exit 1;
  }
fi

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
