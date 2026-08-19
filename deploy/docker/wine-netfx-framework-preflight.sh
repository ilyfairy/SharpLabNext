#!/bin/sh
set -eu

# Validate the exact Desktop CLR generation selected by a matrix profile.  A
# directory check alone is insufficient: CLR 2 prefixes can contain both the
# 3.0 and 3.5 feature packs, and CLR 4 uses the same v4.0.30319 directory for
# every 4.x release.  Wine's system.reg is the installer-owned identity source
# and is retained with the prefix, so inspect it without starting user code.

prefix=${1:?A Wine prefix path is required.}
requested=${2:?A .NET Framework version is required.}
registry=${prefix}/system.reg

fail() {
    printf 'Wine .NET Framework preflight failed: %s\n' "$1" >&2
    exit 1
}

test -r "$registry" || fail "registry file is missing: ${registry}"

# A normal 64-bit Wine prefix can contain a syswow64 compatibility directory,
# so its presence does not identify a 32-bit prefix.  system.reg is created by
# Wine for the prefix and carries the authoritative prefix architecture.
architecture=$(awk '
    index($0, "#arch=") == 1 {
        value = substr($0, length("#arch=") + 1)
        sub(/\r$/, "", value)
        print value
        exit
    }
' "$registry")
test "$architecture" = 'win64' \
    || fail "registry architecture '${architecture}' is not win64"

# Keep the section text in the environment.  Passing it with awk -v would
# consume one level of backslash escaping and fail to match Wine's registry
# format, whose section names contain two literal backslashes.
read_value() {
    section=$1
    key=$2
    REG_SECTION="$section" REG_KEY="$key" awk '
        BEGIN {
            target = "[" ENVIRON["REG_SECTION"] "]"
            key_prefix = "\"" ENVIRON["REG_KEY"] "\"="
        }
        index($0, target) == 1 {
            in_section = 1
            next
        }
        /^\[/ {
            in_section = 0
        }
        in_section && index($0, key_prefix) == 1 {
            print substr($0, length(key_prefix) + 1)
            exit
        }
    ' "$registry"
}

strip_quotes() {
    value=$1
    value=${value#\"}
    value=${value%\"}
    printf '%s' "$value"
}

require_install() {
    section=$1
    install=$(read_value "$section" Install)
    test "$install" = 'dword:00000001' \
        || fail "${section} is not marked Install=1"
}

base='Software\\Microsoft\\NET Framework Setup\\NDP'

case "$requested" in
    2.0)
        prefix_case=clr2
        section="${base}\\\\v2.0.50727"
        require_install "$section"
        # The original x64 .NET Framework 2.0 installer does not write a
        # Version value to this key under Wine. Its exact RTM identity is the
        # installer-owned MSI/SP/Increment tuple (2.0.50727.42).
        msi=$(read_value "$section" MSI)
        sp=$(read_value "$section" SP)
        increment=$(strip_quotes "$(read_value "$section" Increment)")
        test "$msi" = 'dword:00000001' \
            || fail "${section} is not marked MSI=1"
        test "$sp" = 'dword:00000000' \
            || fail "${section} does not identify .NET Framework 2.0 RTM SP=0"
        test "$increment" = '42' \
            || fail "${section} Increment '${increment}' does not identify 2.0.50727.42"
        ;;
    3.0)
        prefix_case=clr2
        # .NET Framework 3.0 is the exception in Microsoft's pre-4.5
        # detection table: its installer writes InstallSuccess under Setup.
        section="${base}\\\\v3.0\\\\Setup"
        install=$(read_value "$section" InstallSuccess)
        test "$install" = 'dword:00000001' \
            || fail "${section} is not marked InstallSuccess=1"
        ;;
    3.5)
        prefix_case=clr2
        section="${base}\\\\v3.5"
        require_install "$section"
        version=$(strip_quotes "$(read_value "$section" Version)")
        case "$version" in
            3.5.*) ;;
            *) fail "CLR 2 registry version '${version}' is not .NET Framework 3.5" ;;
        esac
        ;;
    4.0)
        prefix_case=clr4
        section="${base}\\\\v4\\\\Full"
        require_install "$section"
        version=$(strip_quotes "$(read_value "$section" Version)")
        release=$(read_value "$section" Release || true)
        case "$version" in
            4.0.*) ;;
            *) fail "CLR 4 registry version '${version}' is not .NET Framework 4.0" ;;
        esac
        test -z "$release" || fail '.NET Framework 4.0 prefix unexpectedly exposes a 4.5+ Release value'
        ;;
    4.5|4.5.1|4.5.2|4.6|4.6.1|4.6.2|4.7|4.7.1|4.7.2|4.8)
        prefix_case=clr4
        section="${base}\\\\v4\\\\Full"
        require_install "$section"
        release=$(read_value "$section" Release)
        release=${release#dword:}
        release=$(printf '%s' "$release" | tr 'A-F' 'a-f')
        case "$requested:$release" in
            4.5:0005c615|\
            4.5.1:0005c733|4.5.1:0005c786|\
            4.5.2:0005cbf5|\
            4.6:0006004f|4.6:00060051|\
            4.6.1:0006040e|4.6.1:0006041f|\
            4.6.2:00060632|4.6.2:00060636|\
            4.7:000707fe|4.7:00070805|\
            4.7.1:000709fc|4.7.1:000709fe|\
            4.7.2:00070bf0|4.7.2:00070bf6|\
            4.8:00080ea8|4.8:00080eb1|4.8:00080ff4|4.8:00081041) ;;
            *) fail "CLR 4 Release value '${release}' does not identify .NET Framework ${requested}" ;;
        esac
        ;;
    *)
        fail "unsupported exact .NET Framework version '${requested}'"
        ;;
esac

case "$prefix_case" in
    clr2)
        if test "${SHARPLABNEXT_FRAMEWORK_MATRIX_PREFLIGHT:-0}" = 1; then
            case "$prefix" in
                */clr2) ;;
                *) fail "${requested} matrix input must use a CLR 2 prefix" ;;
            esac
        else
            case "$prefix" in
                /opt/wine-netfx-clr2) ;;
                *) fail "${requested} must use the dedicated CLR 2 prefix" ;;
            esac
        fi
        framework="${prefix}/drive_c/windows/Microsoft.NET/Framework64/v2.0.50727"
        test -f "${framework}/mscorlib.dll" \
            || fail 'Framework64 CLR 2.0 mscorlib.dll is missing'
        ;;
    clr4)
        if test "${SHARPLABNEXT_FRAMEWORK_MATRIX_PREFLIGHT:-0}" = 1; then
            case "$prefix" in
                */clr4) ;;
                *) fail "${requested} matrix input must use a CLR 4 prefix" ;;
            esac
        else
            case "$prefix" in
                /opt/wine-netfx-clr4) ;;
                *) fail "${requested} must use the dedicated CLR 4 prefix" ;;
            esac
        fi
        framework="${prefix}/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319"
        test -f "${framework}/mscorlib.dll" \
            || fail 'Framework64 CLR 4.0 mscorlib.dll is missing'
        ;;
esac

printf 'Wine .NET Framework preflight passed: version=%s prefix=%s\n' "$requested" "$prefix"
