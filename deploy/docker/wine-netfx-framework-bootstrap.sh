#!/usr/bin/env bash
set -euo pipefail

mode="${1:-}"
if test "$#" -ne 1 || { test "${mode}" != seed && test "${mode}" != target; }; then
    printf '[framework-bootstrap] failed=mode\n' >&2
    exit 1
fi

fail_bootstrap() {
    printf '[framework-bootstrap] failed=%s\n' "$1" >&2
    exit 1
}

stage() {
    printf '[framework-bootstrap] stage=%s\n' "$1"
}

stage validate-inputs
[[ "${INSTALLER_MANIFEST_SHA256:-}" =~ ^[0-9a-f]{64}$ ]] \
    || fail_bootstrap manifest-sha256
[[ "${FRAMEWORK_SEED_INPUT_SHA256:-}" =~ ^[0-9a-f]{64}$ ]] \
    || fail_bootstrap seed-input-sha256
test "${ACCEPT_MICROSOFT_DOTNET_FRAMEWORK_EULA:-}" = true \
    || fail_bootstrap eula
for command in python3 sha256sum winetricks cabextract xvfb-run timeout tail awk find cp stat; do
    command -v "${command}" >/dev/null || fail_bootstrap "tool-${command}"
done
test -x "${WINE}" || fail_bootstrap tool-wine
test -x /usr/lib/wine/wine64 || fail_bootstrap tool-wine64
test -x "${WINESERVER}" || fail_bootstrap tool-wineserver
test -x /usr/bin/wine-stable || fail_bootstrap tool-wine-stable

manifest=/usr/local/share/sharplabnext/runtime-framework-installers.json
printf '%s  %s\n' "${INSTALLER_MANIFEST_SHA256}" "${manifest}" \
    | sha256sum --check --status - \
    || fail_bootstrap manifest-digest
chmod 0555 /usr/local/bin/sharplabnext-wine-netfx-preflight
chmod 0555 /usr/local/bin/sharplabnext-dedupe-wine-prefixes

case "${mode}" in
    seed)
        [[ "${FRAMEWORK_WOW64_BASE_IMAGE:-}" =~ ^[^[:space:]@]+@sha256:[0-9a-f]{64}$ ]] \
            || fail_bootstrap wow64-base-image
        [[ "${FRAMEWORK_SEED_GENERATION:-}" =~ ^clr[24]$ ]] \
            || fail_bootstrap seed-generation
        [[ "${FRAMEWORK_SEED_VERSION:-}" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]] \
            || fail_bootstrap seed-version
        [[ "${FRAMEWORK_SEED_PREFIX:-}" =~ ^/opt/wine-netfx-clr[24]$ ]] \
            || fail_bootstrap seed-prefix
        test -z "${FRAMEWORK_TARGET_ID:-}${FRAMEWORK_VERSION:-}${CLR_GENERATION:-}${FRAMEWORK_SEED_IMAGE:-}" \
            || fail_bootstrap seed-target-input
        python3 - \
            /usr/local/share/sharplabnext/framework-wow64-base.json \
            "${FRAMEWORK_SEED_INPUT_SHA256}" <<'PY'
import json
import os
import pathlib
import stat
import sys

path, input_sha256 = sys.argv[1:]
try:
    info = os.lstat(path)
    if not stat.S_ISREG(info.st_mode) or info.st_size < 1 or info.st_size > 1024:
        raise ValueError
    value = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    if value != {
        "schemaVersion": 1,
        "strategy": "framework-wow64-base-v1",
        "seedInputSha256": input_sha256,
    }:
        raise ValueError
except Exception:
    print("Framework WoW64 base receipt validation failed.", file=sys.stderr)
    raise SystemExit(1) from None
PY
        selector="${FRAMEWORK_SEED_GENERATION}"
        ;;
    target)
        [[ "${BASE_IMAGE:-}" =~ ^[^[:space:]@]+@sha256:[0-9a-f]{64}$ ]] \
            || fail_bootstrap base-image
        [[ "${FRAMEWORK_SEED_IMAGE:-}" =~ ^[^[:space:]@]+@sha256:[0-9a-f]{64}$ ]] \
            || fail_bootstrap seed-image
        [[ "${FRAMEWORK_SEED_GENERATION:-}" =~ ^clr[24]$ ]] \
            || fail_bootstrap seed-generation
        [[ "${FRAMEWORK_SEED_VERSION:-}" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]] \
            || fail_bootstrap seed-version
        [[ "${FRAMEWORK_SEED_PREFIX:-}" =~ ^/opt/wine-netfx-clr[24]$ ]] \
            || fail_bootstrap seed-prefix
        [[ "${FRAMEWORK_TARGET_ID:-}" =~ ^netfx[0-9]+$ ]] \
            || fail_bootstrap target-id
        [[ "${FRAMEWORK_VERSION:-}" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]] \
            || fail_bootstrap framework-version
        [[ "${CLR_GENERATION:-}" =~ ^clr[24]$ ]] \
            || fail_bootstrap clr-generation
        test -z "${FRAMEWORK_WOW64_BASE_IMAGE:-}" \
            || fail_bootstrap target-wow64-input
        selector="${FRAMEWORK_TARGET_ID}"
        ;;
esac

installer_root="$(mktemp -d /tmp/sharplabnext-framework.XXXXXX)"
config_root="${installer_root}/config"
mkdir -p "${config_root}"
cleanup() {
    rm -rf "${installer_root}"
}
trap cleanup EXIT

python3 - "${manifest}" "${mode}" "${selector}" "${config_root}" <<'PY'
import json
import pathlib
import sys

manifest_path, mode, selector, output_path = sys.argv[1:]
try:
    manifest = json.loads(pathlib.Path(manifest_path).read_text(encoding="utf-8"))
    companion = manifest["companionPrefixes"]
    if mode == "seed":
        if selector not in ("clr2", "clr4"):
            raise ValueError
        selected = companion[selector]
        target = {
            "version": {"clr2": "3.5", "clr4": "4.8"}[selector],
            "clrGeneration": selector,
            "prefix": selected["prefix"],
            "recipe": {"kind": "winetricks", "verb": selected["winetricksVerb"]},
        }
    elif mode == "target":
        targets = [target for target in manifest["targets"] if target["id"] == selector]
        if len(targets) != 1:
            raise ValueError
        target = targets[0]
    else:
        raise ValueError

    recipe = target["recipe"]
    vendored_payloads = manifest["vendoredWinetricksPayloads"]
    if len(vendored_payloads) != 1:
        raise ValueError
    vendored_payload = vendored_payloads[0]
    cached_payloads = manifest["cachedWinetricksPayloads"]
    if len(cached_payloads) != 1:
        raise ValueError
    cached_payload = cached_payloads[0]
    values = {
        "winetricks-version": manifest["winetricksVersion"],
        "target-version": target["version"],
        "target-generation": target["clrGeneration"],
        "target-prefix": target["prefix"],
        "recipe-kind": recipe["kind"],
        "recipe-verb": recipe.get("verb", ""),
        "installer-file": recipe.get("fileName", ""),
        "installer-sha256": recipe.get("sha256", ""),
        "prerequisite-verb": recipe.get("prerequisiteVerb", ""),
        "vendored-payload-id": vendored_payload["id"],
        "vendored-payload-verb": vendored_payload["verb"],
        "vendored-payload-cache-path": vendored_payload["cachePath"],
        "vendored-payload-size": str(vendored_payload["sizeBytes"]),
        "vendored-payload-sha256": vendored_payload["sha256"],
        "cached-payload-id": cached_payload["id"],
        "cached-payload-verb": cached_payload["verb"],
        "cached-payload-prerequisite-id": cached_payload["prerequisiteId"],
        "cached-payload-cache-path": cached_payload["cachePath"],
        "cached-payload-size": str(cached_payload["sizeBytes"]),
        "cached-payload-sha256": cached_payload["sha256"],
        "clr2-prefix": companion["clr2"]["prefix"],
        "clr2-verb": companion["clr2"]["winetricksVerb"],
        "clr4-prefix": companion["clr4"]["prefix"],
        "clr4-verb": companion["clr4"]["winetricksVerb"],
    }
    output = pathlib.Path(output_path)
    for name, value in values.items():
        if not isinstance(value, str) or "\0" in value or "\n" in value or "\r" in value:
            raise ValueError
        (output / name).write_text(value, encoding="utf-8")
    arguments = recipe.get("arguments", [])
    if not isinstance(arguments, list) or any(not isinstance(argument, str) for argument in arguments):
        raise ValueError
    (output / "installer-arguments").write_text(
        "".join(f"{argument}\n" for argument in arguments),
        encoding="utf-8",
    )
except Exception:
    print("Framework installer manifest selection failed.", file=sys.stderr)
    raise SystemExit(1) from None
PY

read_config() {
    local value
    value="$(cat "${config_root}/$1")"
    test -n "${value}"
    printf '%s' "${value}"
}

winetricks_version="$(read_config winetricks-version)"
target_version="$(read_config target-version)"
target_generation="$(read_config target-generation)"
target_prefix="$(read_config target-prefix)"
recipe_kind="$(read_config recipe-kind)"
recipe_verb="$(cat "${config_root}/recipe-verb")"
installer_file="$(cat "${config_root}/installer-file")"
installer_sha256="$(cat "${config_root}/installer-sha256")"
prerequisite_verb="$(cat "${config_root}/prerequisite-verb")"
vendored_payload_id="$(read_config vendored-payload-id)"
vendored_payload_verb="$(read_config vendored-payload-verb)"
vendored_payload_cache_path="$(read_config vendored-payload-cache-path)"
vendored_payload_size="$(read_config vendored-payload-size)"
vendored_payload_sha256="$(read_config vendored-payload-sha256)"
cached_payload_id="$(read_config cached-payload-id)"
cached_payload_verb="$(read_config cached-payload-verb)"
cached_payload_prerequisite_id="$(read_config cached-payload-prerequisite-id)"
cached_payload_cache_path="$(read_config cached-payload-cache-path)"
cached_payload_size="$(read_config cached-payload-size)"
cached_payload_sha256="$(read_config cached-payload-sha256)"
clr2_prefix="$(read_config clr2-prefix)"
clr2_verb="$(read_config clr2-verb)"
clr4_prefix="$(read_config clr4-prefix)"
clr4_verb="$(read_config clr4-verb)"
mapfile -t installer_arguments < "${config_root}/installer-arguments"

# These are BuildKit named contexts.  They point at the existing prerequisite
# directories supplied by the host; nothing here is a host-side staging area.
framework_vendored_root=/run/operator-assets/framework-vendored
framework_cached_root=/run/operator-assets/framework-cached
framework_installer_root=/run/operator-assets/framework-installer
vendored_payload_file="${vendored_payload_cache_path##*/}"
cached_payload_file="${cached_payload_cache_path##*/}"
test -d "${framework_vendored_root}" || fail_bootstrap vendored-context-missing
test -d "${framework_cached_root}" || fail_bootstrap cached-context-missing
test -d "${framework_installer_root}" || fail_bootstrap installer-context-missing

test "${clr2_prefix}" = /opt/wine-netfx-clr2 \
    || fail_bootstrap companion-clr2-prefix
test "${clr4_prefix}" = /opt/wine-netfx-clr4 \
    || fail_bootstrap companion-clr4-prefix
case "${target_generation}:${target_prefix}" in
    clr2:/opt/wine-netfx-clr2|clr4:/opt/wine-netfx-clr4) ;;
    *) fail_bootstrap target-prefix ;;
esac
for verb in "${clr2_verb}" "${clr4_verb}"; do
    [[ "${verb}" =~ ^dotnet[0-9]+(sp[0-9]+)?$ ]] \
        || fail_bootstrap companion-verb
done
actual_winetricks_version="$(winetricks --version | awk 'NR == 1 { print $1; exit }')"
test "${actual_winetricks_version}" = "${winetricks_version}" \
    || fail_bootstrap winetricks-version
test "${vendored_payload_id}" = dotnet20-x64 \
    || fail_bootstrap vendored-payload-id
test "${vendored_payload_verb}" = dotnet20 \
    || fail_bootstrap vendored-payload-verb
test "${vendored_payload_cache_path}" = dotnet20/NetFx64.exe \
    || fail_bootstrap vendored-payload-cache-path
test "${vendored_payload_file}" = NetFx64.exe \
    || fail_bootstrap vendored-payload-file
test "${vendored_payload_size}" = 47400128 \
    || fail_bootstrap vendored-payload-size
test "${vendored_payload_sha256}" = 7ea86dca8eeaedcaa4a17370547ca2cea9e9b6774972b8e03d2cb1fb0e798669 \
    || fail_bootstrap vendored-payload-sha256
vendored_payload_source="${framework_vendored_root}/${vendored_payload_file}"
test "${cached_payload_id}" = dotnet35sp1-full \
    || fail_bootstrap cached-payload-id
test "${cached_payload_verb}" = dotnet35sp1 \
    || fail_bootstrap cached-payload-verb
test "${cached_payload_prerequisite_id}" = netfx35sp1-installer \
    || fail_bootstrap cached-payload-prerequisite-id
test "${cached_payload_cache_path}" = dotnet35sp1/dotnetfx35.exe \
    || fail_bootstrap cached-payload-cache-path
test "${cached_payload_file}" = dotnetfx35.exe \
    || fail_bootstrap cached-payload-file
test "${cached_payload_size}" = 242743296 \
    || fail_bootstrap cached-payload-size
test "${cached_payload_sha256}" = 0582515bde321e072f8673e829e175ed2e7a53e803127c50253af76528e66bc1 \
    || fail_bootstrap cached-payload-sha256
cached_payload_source="${framework_cached_root}/${cached_payload_file}"
cached_payload_required=0
if test "${recipe_verb}" = "${cached_payload_verb}" \
    || test "${prerequisite_verb}" = "${cached_payload_verb}"; then
    cached_payload_required=1
fi
expected_installer_network=default
if test "${cached_payload_required}" -eq 1; then
    expected_installer_network=none
fi
test "${FRAMEWORK_INSTALLER_NETWORK:-}" = "${expected_installer_network}" \
    || fail_bootstrap installer-network
test -f "${vendored_payload_source}" \
    || fail_bootstrap vendored-payload-missing
test ! -L "${vendored_payload_source}" \
    || fail_bootstrap vendored-payload-symlink
test "$(stat -c '%s' "${vendored_payload_source}")" = "${vendored_payload_size}" \
    || fail_bootstrap vendored-payload-size
printf '%s  %s\n' "${vendored_payload_sha256}" "${vendored_payload_source}" \
    | sha256sum --check --status - \
    || fail_bootstrap vendored-payload-sha256
if test "${cached_payload_required}" -eq 1; then
    test -f "${cached_payload_source}" \
        || fail_bootstrap cached-payload-missing
    test ! -L "${cached_payload_source}" \
        || fail_bootstrap cached-payload-symlink
    test "$(stat -c '%s' "${cached_payload_source}")" = "${cached_payload_size}" \
        || fail_bootstrap cached-payload-size
    printf '%s  %s\n' "${cached_payload_sha256}" "${cached_payload_source}" \
        | sha256sum --check --status - \
        || fail_bootstrap cached-payload-sha256
else
    : # The direct context may contain other files; only required inputs matter.
fi

if test "${recipe_kind}" = operator-installer; then
    direct_installer_file="${FRAMEWORK_INSTALLER_FILE:-}"
    [[ "${direct_installer_file}" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$ ]] \
        || fail_bootstrap installer-file
else
    direct_installer_file=""
    test -z "${FRAMEWORK_INSTALLER_FILE:-}" \
        || fail_bootstrap unexpected-installer-file
fi

operator_installer_source="${framework_installer_root}/${direct_installer_file}"

bounded_log_tail() {
    tail -c 16384 "$1" | tail -n 80 | python3 -c '
import re
import sys

for line in sys.stdin:
    line = re.sub(r"https?://\S+", "<redacted-url>", line, flags=re.IGNORECASE)
    line = line.replace("/run/operator-assets/framework-vendored", "<operator-asset>")
    line = line.replace("/run/operator-assets/framework-cached", "<operator-asset>")
    line = line.replace("/run/operator-assets/framework-installer", "<operator-asset>")
    line = line.replace("/run/secrets/framework-installer-url", "<url-secret>")
    sys.stdout.write(line)
'
}

run_logged() {
    local operation="$1"
    shift
    local log="${installer_root}/${operation}.log"
    if "$@" >"${log}" 2>&1; then
        rm -f "${log}"
        return 0
    fi
    printf '[framework-bootstrap] failed=%s log-tail-bytes<=16384 log-tail-lines<=80\n' "${operation}" >&2
    bounded_log_tail "${log}" >&2 || true
    return 1
}

install_clean_winetricks() {
    local prefix="$1"
    local verb="$2"
    local operation="$3"
    local cache="${installer_root}/winetricks-cache-${operation}"
    rm -rf "${prefix}"
    mkdir -p "$(dirname "${prefix}")" "${cache}"
    test ! -e "${prefix}"
    local vendored_destination="${cache}/${vendored_payload_cache_path}"
    mkdir -p "$(dirname "${vendored_destination}")"
    cp "${vendored_payload_source}" "${vendored_destination}"
    test "$(stat -c '%s' "${vendored_destination}")" = "${vendored_payload_size}"
    printf '%s  %s\n' "${vendored_payload_sha256}" "${vendored_destination}" \
        | sha256sum --check --status -
    if test "${verb}" = "${cached_payload_verb}"; then
        test "${cached_payload_required}" -eq 1
        local cached_destination="${cache}/${cached_payload_cache_path}"
        mkdir -p "$(dirname "${cached_destination}")"
        cp "${cached_payload_source}" "${cached_destination}"
        test "$(stat -c '%s' "${cached_destination}")" = "${cached_payload_size}"
        printf '%s  %s\n' "${cached_payload_sha256}" "${cached_destination}" \
            | sha256sum --check --status -
    fi
    run_logged "${operation}" \
        env WINEPREFIX="${prefix}" WINEARCH=win64 W_CACHE="${cache}" \
        timeout --signal=KILL 1800 xvfb-run -a \
        winetricks --optout --unattended "${verb}"
    run_logged "${operation}-wineserver" \
        env WINEPREFIX="${prefix}" WINEARCH=win64 "${WINESERVER}" -w
    rm -rf "${cache}"
}

download_from_url_secret() {
    local destination="$1"
    python3 - /run/secrets/framework-installer-url "${destination}" <<'PY'
import shutil
import sys
import urllib.parse
import urllib.request

secret_path, destination = sys.argv[1:]
try:
    with open(secret_path, encoding="utf-8") as secret:
        url = secret.read().strip()
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme not in ("http", "https") or not parsed.netloc or any(character.isspace() for character in url):
        raise ValueError
    request = urllib.request.Request(url, headers={"User-Agent": "SharpLabNext-operator-bootstrap"})
    with urllib.request.urlopen(request, timeout=300) as response, open(destination, "xb") as output:
        shutil.copyfileobj(response, output, length=1024 * 1024)
except Exception:
    print("Operator asset download failed.", file=sys.stderr)
    raise SystemExit(1) from None
PY
}

materialize_operator_installer() {
    local destination="$1"
    local url_present=0
    local installer_present=0
    test -s /run/secrets/framework-installer-url && url_present=1
    if test -n "${direct_installer_file}" && test -s "${operator_installer_source}"; then
        installer_present=1
    fi
    test "$((url_present + installer_present))" -eq 1
    if test "${installer_present}" -eq 1; then
        test -f "${operator_installer_source}"
        test ! -L "${operator_installer_source}"
        cp "${operator_installer_source}" "${destination}"
    else
        download_from_url_secret "${destination}"
    fi
}

install_manual_target() {
    [[ "${installer_file}" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$ ]]
    [[ "${installer_sha256}" =~ ^[0-9a-f]{64}$ ]]
    [[ "${prerequisite_verb}" =~ ^dotnet[0-9]+(sp[0-9]+)?$ ]]
    test "${#installer_arguments[@]}" -ge 1
    test "${#installer_arguments[@]}" -le 8
    for argument in "${installer_arguments[@]}"; do
        [[ "${argument}" =~ ^/[A-Za-z0-9:._=-]+$ ]]
    done

    install_clean_winetricks "${target_prefix}" "${prerequisite_verb}" target-prerequisite
    local installer="${installer_root}/${installer_file}"
    stage materialize-private-installer
    materialize_operator_installer "${installer}"
    printf '%s  %s\n' "${installer_sha256}" "${installer}" | sha256sum --check --status -
    run_logged install-target-manual \
        env WINEPREFIX="${target_prefix}" WINEARCH=win64 WINEDLLOVERRIDES=fusion=b \
        timeout --signal=KILL 1200 xvfb-run -a \
        /usr/bin/wine-stable "${installer}" "${installer_arguments[@]}"
    run_logged install-target-manual-wineserver \
        env WINEPREFIX="${target_prefix}" WINEARCH=win64 "${WINESERVER}" -w
    rm -f "${installer}"
}

disable_ngen_services() {
    local prefix="$1"
    local service_version="$2"
    local operation="$3"
    local architecture
    local service
    local key

    for architecture in 32 64; do
        service="clr_optimization_${service_version}_${architecture}"
        key="HKLM\\System\\CurrentControlSet\\Services\\${service}"
        run_logged "${operation}-${architecture}" \
            env WINEPREFIX="${prefix}" WINEARCH=win64 \
            timeout --signal=KILL 60 /usr/lib/wine/wine64 \
            reg.exe add "${key}" /v Start /t REG_DWORD /d 4 /f
    done
    run_logged "${operation}-wineserver-stop" \
        env WINEPREFIX="${prefix}" WINEARCH=win64 "${WINESERVER}" -k
    run_logged "${operation}-wineserver-wait" \
        env WINEPREFIX="${prefix}" WINEARCH=win64 "${WINESERVER}" -w
}

cleanup_prefix() {
    local prefix="$1"
    local temporary
    for temporary in \
        "${prefix}/drive_c/windows/temp" \
        "${prefix}/drive_c/users/root/Temp"; do
        if test -d "${temporary}"; then
            find "${temporary}" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
        fi
    done
    find "${prefix}/drive_c/windows/Microsoft.NET" -type d -name SetupCache -prune -exec rm -rf -- {} +
    find "${prefix}" -type f \
        \( -iname 'dotnetfx*.exe' -o -iname 'netfx*.exe' -o -iname 'ndp*.exe' \) \
        -delete
}

if test "${mode}" = seed; then
    test "${target_generation}" = "${FRAMEWORK_SEED_GENERATION}" \
        || fail_bootstrap seed-manifest-generation
    test "${target_version}" = "${FRAMEWORK_SEED_VERSION}" \
        || fail_bootstrap seed-manifest-version
    test "${target_prefix}" = "${FRAMEWORK_SEED_PREFIX}" \
        || fail_bootstrap seed-manifest-prefix
    case "${target_generation}:${target_version}:${target_prefix}:${recipe_kind}:${recipe_verb}" in
        clr2:3.5:/opt/wine-netfx-clr2:winetricks:dotnet35sp1|\
        clr4:4.8:/opt/wine-netfx-clr4:winetricks:dotnet48) ;;
        *) fail_bootstrap seed-manifest-selection ;;
    esac
    test -z "${installer_file}${installer_sha256}${prerequisite_verb}" \
        || fail_bootstrap seed-installer-input
    test "${#installer_arguments[@]}" -eq 0 \
        || fail_bootstrap seed-installer-arguments
    test ! -e "${clr2_prefix}" && test ! -e "${clr4_prefix}" \
        || fail_bootstrap seed-prefix-state
    test ! -s /run/secrets/framework-installer-url \
        || fail_bootstrap seed-url-input

    stage "install-shared-${target_generation}-companion"
    install_clean_winetricks "${target_prefix}" "${recipe_verb}" "seed-${target_generation}"
    stage disable-runtime-ngen-services
    if test "${target_generation}" = clr2; then
        disable_ngen_services "${target_prefix}" v2.0.50727 disable-seed-clr2-ngen
    else
        disable_ngen_services "${target_prefix}" v4.0.30319 disable-seed-clr4-ngen
    fi
    stage preflight-shared-companion
    /usr/local/bin/sharplabnext-wine-netfx-preflight "${target_prefix}" "${target_version}"
    stage cleanup-private-assets
    cleanup_prefix "${target_prefix}"
    mkdir -p /opt/sharplabnext
    python3 - \
        /opt/sharplabnext/framework-companion-seed.json \
        "${FRAMEWORK_SEED_INPUT_SHA256}" \
        "${FRAMEWORK_WOW64_BASE_IMAGE}" \
        "${INSTALLER_MANIFEST_SHA256}" \
        "${target_generation}" \
        "${target_version}" \
        "${target_prefix}" <<'PY'
import json
import pathlib
import sys

output, input_sha256, wow64_base, manifest_sha256, generation, version, prefix = sys.argv[1:]
value = {
    "schemaVersion": 1,
    "strategy": "framework-companion-seed-v1",
    "seedInputSha256": input_sha256,
    "wow64BaseImage": wow64_base,
    "installerManifestSha256": manifest_sha256,
    "generation": generation,
    "version": version,
    "prefix": prefix,
}
pathlib.Path(output).write_text(
    json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n",
    encoding="utf-8",
)
PY
    chmod 0444 /opt/sharplabnext/framework-companion-seed.json
else
    test "${target_version}" = "${FRAMEWORK_VERSION}" \
        || fail_bootstrap manifest-version
    test "${target_generation}" = "${CLR_GENERATION}" \
        || fail_bootstrap manifest-generation
    if test "${target_generation}" = clr2; then
        expected_seed_generation=clr4
        expected_seed_version=4.8
        expected_seed_prefix="${clr4_prefix}"
    else
        expected_seed_generation=clr2
        expected_seed_version=3.5
        expected_seed_prefix="${clr2_prefix}"
    fi
    test "${FRAMEWORK_SEED_GENERATION}" = "${expected_seed_generation}" \
        || fail_bootstrap seed-generation-selection
    test "${FRAMEWORK_SEED_VERSION}" = "${expected_seed_version}" \
        || fail_bootstrap seed-version-selection
    test "${FRAMEWORK_SEED_PREFIX}" = "${expected_seed_prefix}" \
        || fail_bootstrap seed-prefix-selection
    test ! -e "${target_prefix}" \
        || fail_bootstrap target-prefix-not-empty
    test -d "${expected_seed_prefix}" \
        || fail_bootstrap seed-prefix-missing

    stage validate-companion-seed
    python3 - \
        /opt/sharplabnext/framework-companion-seed.json \
        "${FRAMEWORK_SEED_INPUT_SHA256}" \
        "${INSTALLER_MANIFEST_SHA256}" \
        "${expected_seed_generation}" \
        "${expected_seed_version}" \
        "${expected_seed_prefix}" <<'PY'
import json
import os
import pathlib
import stat
import sys

path, input_sha256, manifest_sha256, generation, version, prefix = sys.argv[1:]
try:
    info = os.lstat(path)
    if not stat.S_ISREG(info.st_mode) or info.st_size < 1 or info.st_size > 4096:
        raise ValueError
    value = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    expected = {
        "schemaVersion": 1,
        "strategy": "framework-companion-seed-v1",
        "seedInputSha256": input_sha256,
        "installerManifestSha256": manifest_sha256,
        "generation": generation,
        "version": version,
        "prefix": prefix,
    }
    if set(value) != set(expected) | {"wow64BaseImage"}:
        raise ValueError
    if any(value[key] != expected[key] for key in expected):
        raise ValueError
    wow64_base = value["wow64BaseImage"]
    if not isinstance(wow64_base, str) or "@sha256:" not in wow64_base:
        raise ValueError
except Exception:
    print("Framework companion seed receipt validation failed.", file=sys.stderr)
    raise SystemExit(1) from None
PY
    /usr/local/bin/sharplabnext-wine-netfx-preflight \
        "${expected_seed_prefix}" "${expected_seed_version}"

    url_present=0
    installer_present=0
    test -s /run/secrets/framework-installer-url && url_present=1
    if test -n "${direct_installer_file}" && test -s "${operator_installer_source}"; then
        installer_present=1
    fi
    stage validate-recipe-source
    case "${recipe_kind}" in
        winetricks)
            test "$((url_present + installer_present))" -eq 0
            [[ "${recipe_verb}" =~ ^dotnet[0-9]+(sp[0-9]+)?$ ]]
            test -z "${installer_file}${installer_sha256}${prerequisite_verb}"
            test "${#installer_arguments[@]}" -eq 0
            ;;
        operator-installer)
            test "$((url_present + installer_present))" -eq 1
            test -z "${recipe_verb}"
            ;;
        *) fail_bootstrap recipe-kind ;;
    esac

    stage "install-target-${target_generation}"
    if test "${recipe_kind}" = winetricks; then
        install_clean_winetricks "${target_prefix}" "${recipe_verb}" "target-${target_generation}"
    else
        install_manual_target
    fi
    stage disable-runtime-ngen-services
    if test "${target_generation}" = clr2; then
        disable_ngen_services "${target_prefix}" v2.0.50727 disable-target-clr2-ngen
    else
        disable_ngen_services "${target_prefix}" v4.0.30319 disable-target-clr4-ngen
    fi

    stage preflight-installed-prefixes
    if test "${target_generation}" = clr2; then
        /usr/local/bin/sharplabnext-wine-netfx-preflight "${clr2_prefix}" "${target_version}"
        /usr/local/bin/sharplabnext-wine-netfx-preflight "${clr4_prefix}" 4.8
    else
        /usr/local/bin/sharplabnext-wine-netfx-preflight "${clr2_prefix}" 3.5
        /usr/local/bin/sharplabnext-wine-netfx-preflight "${clr4_prefix}" "${target_version}"
    fi

    stage cleanup-private-assets
    cleanup_prefix "${clr2_prefix}"
    cleanup_prefix "${clr4_prefix}"
    python3 - \
        /opt/sharplabnext/framework-companion-binding.json \
        "${FRAMEWORK_TARGET_ID}" \
        "${FRAMEWORK_SEED_IMAGE}" \
        "${FRAMEWORK_SEED_INPUT_SHA256}" \
        "${expected_seed_generation}" \
        "${expected_seed_version}" <<'PY'
import json
import pathlib
import sys

output, target_id, seed_image, input_sha256, generation, version = sys.argv[1:]
value = {
    "schemaVersion": 1,
    "strategy": "framework-companion-binding-v1",
    "targetId": target_id,
    "seedImage": seed_image,
    "seedInputSha256": input_sha256,
    "generation": generation,
    "version": version,
}
pathlib.Path(output).write_text(
    json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n",
    encoding="utf-8",
)
PY
    chmod 0444 /opt/sharplabnext/framework-companion-binding.json
    stage deduplicate-immutable-prefix-files
    python3 /usr/local/bin/sharplabnext-dedupe-wine-prefixes \
        --source /opt/wine-netfx-clr2 \
        --target /opt/wine-netfx-clr4 \
        --manifest /opt/sharplabnext/.wine-prefix-layout.json \
        --freeze
fi

stage cleanup
cleanup
trap - EXIT
