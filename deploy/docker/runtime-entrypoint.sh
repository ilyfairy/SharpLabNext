#!/bin/sh
set -eu

ready=/workspace/.sharplabnext/ready
while [ ! -f "$ready" ]; do
    sleep 0.01
done

if [ "${SHARPLABNEXT_JIT_RESET_OUTPUT:-0}" = "1" ]; then
    # A stopped reusable container retains its /tmp tmpfs. Clear only the
    # Supervisor-owned fixed paths before CoreCLR and the profiler open them.
    rm -f -- \
        /tmp/sharplabnext-jit.asm \
        /tmp/sharplabnext-jit.map \
        /tmp/sharplabnext-jit-rich.map
fi

if [ "${SHARPLABNEXT_PREPARE_WINE_XDG_RUNTIME_DIR:-0}" = "1" ] \
    && [ "$(id -u)" != "0" ]; then
    runtime_uid="$(id -u)"
    xdg_storage_dir="/tmp/sharplabnext-wine-runtime-${runtime_uid}"
    xdg_runtime_dir="/run/user/${runtime_uid}"

    if [ -L /tmp ] || [ ! -d /tmp ] \
        || [ "$(stat -c %u /tmp)" != "0" ] \
        || [ "$(stat -c %a /tmp)" != "1777" ]; then
        echo "SharpLabNext Wine runtime requires root-owned mode-1777 /tmp." >&2
        exit 70
    fi
    if [ -L /run/user ] || [ ! -d /run/user ] \
        || [ "$(stat -c %u /run/user)" != "0" ] \
        || [ "$(stat -c %a /run/user)" != "755" ]; then
        echo "SharpLabNext Wine runtime requires root-owned mode-755 /run/user." >&2
        exit 70
    fi
    if [ ! -L "${xdg_runtime_dir}" ] \
        || [ "$(readlink "${xdg_runtime_dir}")" != "${xdg_storage_dir}" ]; then
        echo "SharpLabNext Wine XDG runtime link is missing or unsafe." >&2
        exit 70
    fi
    if [ -L "${xdg_storage_dir}" ]; then
        echo "SharpLabNext Wine XDG runtime storage cannot be a symbolic link." >&2
        exit 70
    fi
    if [ ! -e "${xdg_storage_dir}" ]; then
        mkdir -m 0700 "${xdg_storage_dir}"
    fi
    if [ ! -d "${xdg_storage_dir}" ] \
        || [ "$(stat -c %u "${xdg_storage_dir}")" != "${runtime_uid}" ] \
        || [ "$(stat -c %a "${xdg_storage_dir}")" != "700" ]; then
        echo "SharpLabNext Wine XDG runtime directory has unsafe ownership or mode." >&2
        exit 70
    fi

    XDG_RUNTIME_DIR="${xdg_runtime_dir}"
    export XDG_RUNTIME_DIR
fi

exec "$@"
