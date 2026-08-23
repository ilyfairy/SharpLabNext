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
        /tmp/sharplabnext-jit-rich.map \
        /tmp/sharplabnext-desktop-jit.bin \
        /tmp/sharplabnext-desktop-jit.bin.tmp
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

if [ "${SHARPLABNEXT_WINE_CLEANUP:-0}" = "1" ] \
    && [ -n "${WINESERVER:-}" ]; then
    # Wine keeps a server and several service processes alive after the
    # managed host exits. A measured runtime must be quiescent before the
    # cgroup sidecar records completion, otherwise those descendants look
    # like a leaked user process (and zombies cannot be recovered by /bin/sh's
    # job table). Shut down this isolated prefix while the exec shell still
    # owns the command's exit status.
    if [ "$WINESERVER" != "/usr/lib/wine/wineserver64" ] \
        || [ ! -x "$WINESERVER" ]; then
        echo "SharpLabNext Wine cleanup requires the fixed x64 wineserver." >&2
        exit 70
    fi

    set +e
    "$@" &
    child=$!

    forward_term() {
        kill -TERM "$child" 2>/dev/null || :
        "$WINESERVER" -k >/dev/null 2>&1 || :
    }
    forward_int() {
        kill -INT "$child" 2>/dev/null || :
        "$WINESERVER" -k >/dev/null 2>&1 || :
    }
    trap forward_term TERM
    trap forward_int INT

    wait "$child"
    status=$?
    trap - TERM INT
    "$WINESERVER" -k >/dev/null 2>&1
    # Wait for Wine's service processes to exit, but never let cleanup extend
    # the runtime deadline indefinitely. Docker's PID1 reaper (or the shell
    # trap in the measured keeper) then reaps them before the sidecar's strict
    # keeper-only check.
    if command -v timeout >/dev/null 2>&1; then
        timeout 2 "$WINESERVER" -w >/dev/null 2>&1 || :
    else
        sleep 0.2
    fi
    exit "$status"
fi

exec "$@"
