#!/bin/sh
set -eu
# Mountinfo is host-provided text; never let pathname globbing alter its fields.
set -f

export LC_ALL=C

measurement_root=/measurement
target_cgroup_file=/run/sharplabnext-target-cgroup

fail() {
    echo "SharpLabNext runtime measurement helper: $1" >&2
    exit 70
}

if [ "$#" -ne 2 ]; then
    fail "expected a token and target container ID."
fi

token=$1
target_id=$2
if [ "${#token}" -ne 32 ]; then
    fail "the token length is invalid."
fi
case "$token" in
    *[!0-9a-f]*) fail "the token is not canonical lowercase hexadecimal." ;;
esac
if [ "${#target_id}" -ne 64 ]; then
    fail "the target container ID length is invalid."
fi
case "$target_id" in
    *[!0-9a-f]*) fail "the target container ID is not canonical lowercase hexadecimal." ;;
esac

armed_path="${measurement_root}/armed-${token}"
armed_tmp="${armed_path}.tmp"
capture_path="${measurement_root}/capture-${token}"
capture_upload_path="${capture_path}.upload"
completion_path="${measurement_root}/completion-${token}"
completion_tmp="${completion_path}.tmp"
finish_path="${measurement_root}/finish-${token}"
finish_upload_path="${finish_path}.upload"

cleanup() {
    rm -f -- "$armed_tmp" "$completion_tmp" "$capture_upload_path" "$finish_upload_path"
}
trap 'cleanup; exit 70' HUP INT TERM

if [ -L "$measurement_root" ] || [ ! -d "$measurement_root" ]; then
    fail "the control root is not a directory."
fi
if [ "$(stat -c '%u:%g:%a' "$measurement_root")" != "1654:1654:700" ]; then
    fail "the control root owner or mode is invalid."
fi
for path in \
    "$armed_path" "$armed_tmp" "$capture_path" "$capture_upload_path" \
    "$completion_path" "$completion_tmp" "$finish_path" "$finish_upload_path"
do
    if [ -e "$path" ] || [ -L "$path" ]; then
        fail "the control volume is not fresh."
    fi
done
if [ -L "$target_cgroup_file" ] || [ ! -f "$target_cgroup_file" ]; then
    fail "the target cgroup membership file is unavailable."
fi

validate_target_path() {
    case "$candidate_path" in
        /*) ;;
        *) fail "the target cgroup path is not absolute." ;;
    esac
    case "$candidate_path" in
        *//*|*/./*|*/../*|*/.|*/..) fail "the target cgroup path is not canonical." ;;
        *[!A-Za-z0-9/_.:-]*) fail "the target cgroup path contains an unsupported character." ;;
    esac
    target_component=${candidate_path##*/}
    if [ "$target_component" != "$target_id" ] \
        && [ "$target_component" != "docker-${target_id}.scope" ]; then
        fail "the target cgroup path is not bound to the complete container ID."
    fi
}

line_count=0
v2_count=0
v2_path=
memory_count=0
memory_path=
pids_count=0
pids_cgroup_path=
while IFS= read -r cgroup_line || [ -n "$cgroup_line" ]; do
    line_count=$((line_count + 1))
    if [ "$line_count" -gt 64 ] || [ "${#cgroup_line}" -gt 4096 ]; then
        fail "the target cgroup membership file is oversized."
    fi
    hierarchy=${cgroup_line%%:*}
    remainder=${cgroup_line#*:}
    if [ "$remainder" = "$cgroup_line" ]; then
        fail "the target cgroup membership file is malformed."
    fi
    controllers=${remainder%%:*}
    candidate_path=${remainder#*:}
    if [ "$candidate_path" = "$remainder" ]; then
        fail "the target cgroup membership file is malformed."
    fi
    case "$hierarchy" in
        ''|*[!0-9]*) fail "the target cgroup hierarchy is malformed." ;;
    esac
    validate_target_path
    if [ "$hierarchy" = "0" ] && [ -z "$controllers" ]; then
        v2_count=$((v2_count + 1))
        v2_path=$candidate_path
        continue
    fi
    case ",${controllers}," in
        *,memory,*)
            memory_count=$((memory_count + 1))
            memory_path=$candidate_path
            ;;
    esac
    case ",${controllers}," in
        *,pids,*)
            pids_count=$((pids_count + 1))
            pids_cgroup_path=$candidate_path
            ;;
    esac
done < "$target_cgroup_file"

if [ "$v2_count" -eq 1 ] && [ "$line_count" -eq 1 ] && [ "$memory_count" -eq 0 ]; then
    cgroup_kind=cgroup-v2
    peak_path="/sys/fs/cgroup${v2_path}/memory.peak"
    pids_path="/sys/fs/cgroup${v2_path}/pids.current"
elif [ "$v2_count" -eq 0 ] && [ "$memory_count" -eq 1 ] && [ "$pids_count" -eq 1 ]; then
    memory_mount=
    memory_mount_count=0
    pids_mount=
    pids_mount_count=0
    while IFS= read -r mount_line || [ -n "$mount_line" ]; do
        case "$mount_line" in
            *' - cgroup '*) ;;
            *) continue ;;
        esac
        before_separator=${mount_line%%' - '*}
        after_separator=${mount_line#*' - '}
        set -- $before_separator
        if [ "$#" -lt 6 ] || [ "$4" != "/" ]; then
            fail "the cgroup v1 mount entry is malformed."
        fi
        candidate_mount=$5
        set -- $after_separator
        if [ "$#" -lt 3 ] || [ "$1" != "cgroup" ]; then
            continue
        fi
        case "$candidate_mount" in
            /sys/fs/cgroup|/sys/fs/cgroup/*) ;;
            *) fail "the cgroup v1 memory mount is outside the reviewed root." ;;
        esac
        case "$candidate_mount" in
            *//*|*/./*|*/../*|*/.|*/..|*[!A-Za-z0-9/_.:-]*)
                fail "the cgroup v1 memory mount is not canonical."
                ;;
        esac
        case ",$3," in
            *,memory,*)
                memory_mount=$candidate_mount
                memory_mount_count=$((memory_mount_count + 1))
                ;;
        esac
        case ",$3," in
            *,pids,*)
                pids_mount=$candidate_mount
                pids_mount_count=$((pids_mount_count + 1))
                ;;
        esac
    done < /proc/self/mountinfo
    if [ "$memory_mount_count" -ne 1 ] || [ "$pids_mount_count" -ne 1 ]; then
        fail "the cgroup v1 memory or pids controller mount is ambiguous or unavailable."
    fi
    cgroup_kind=cgroup-v1
    peak_path="${memory_mount}${memory_path}/memory.max_usage_in_bytes"
    pids_path="${pids_mount}${pids_cgroup_path}/pids.current"
else
    fail "the target cgroup hierarchy is ambiguous or unsupported."
fi

if [ -L "$peak_path" ] || [ ! -f "$peak_path" ] \
    || [ -L "$pids_path" ] || [ ! -f "$pids_path" ]; then
    fail "the target cgroup peak or pids file is unavailable."
fi

require_keeper_only() {
    current_pids=$(cat "$pids_path") \
        || fail "the target cgroup pids count could not be read."
    if [ "$current_pids" != "1" ]; then
        fail "the target cgroup does not contain exactly the keeper process."
    fi
}

write_armed() {
    umask 077
    if ! (set -C; printf '%s\n%s\n%s\n%s\n' \
        'sharplabnext-runtime-measurement-sidecar-armed-v1' \
        "$token" "$target_id" "$cgroup_kind" > "$armed_tmp"); then
        fail "the armed record could not be created."
    fi
    chmod 0600 "$armed_tmp"
    if [ "$(stat -c '%u:%g:%a' "$armed_tmp")" != "1654:1654:600" ]; then
        fail "the armed record owner or mode is invalid."
    fi
    mv -- "$armed_tmp" "$armed_path"
}

wait_for_signal() {
    signal_name=$1
    upload_path=$2
    signal_path=$3
    expected_size=$(printf '%s\n%s\n%s\n%s\n' \
        'sharplabnext-runtime-measurement-signal-v1' \
        "$token" "$target_id" "$signal_name" | wc -c | tr -d '[:space:]')
    expected_payload=$(printf '%s\n%s\n%s\n%s' \
        'sharplabnext-runtime-measurement-signal-v1' \
        "$token" "$target_id" "$signal_name")
    partial_attempts=0
    while :; do
        if [ -L "$upload_path" ] || [ -L "$signal_path" ]; then
            fail "the ${signal_name} signal is a symbolic link."
        fi
        if [ -e "$signal_path" ]; then
            fail "the ${signal_name} signal was published more than once."
        fi
        if [ ! -e "$upload_path" ]; then
            sleep 0.01
            continue
        fi
        if [ ! -f "$upload_path" ]; then
            fail "the ${signal_name} signal is not a regular file."
        fi
        signal_size=$(stat -c '%s' "$upload_path") \
            || fail "the ${signal_name} signal size could not be read."
        case "$signal_size" in
            ''|*[!0-9]*) fail "the ${signal_name} signal size is invalid." ;;
        esac
        if [ "$signal_size" -gt "$expected_size" ]; then
            fail "the ${signal_name} signal is larger than its canonical payload."
        fi
        if [ "$signal_size" -lt "$expected_size" ]; then
            partial_attempts=$((partial_attempts + 1))
            if [ "$partial_attempts" -gt 1000 ]; then
                fail "the ${signal_name} signal remained partially written."
            fi
            sleep 0.01
            continue
        fi
        if [ "$(stat -c '%u:%g:%a' "$upload_path")" != "1654:1654:600" ]; then
            partial_attempts=$((partial_attempts + 1))
            if [ "$partial_attempts" -gt 1000 ]; then
                fail "the ${signal_name} signal metadata is invalid."
            fi
            sleep 0.01
            continue
        fi
        signal_content=$(cat "$upload_path") \
            || fail "the ${signal_name} signal could not be read."
        if [ "$signal_content" != "$expected_payload" ]; then
            fail "the ${signal_name} signal is not canonical."
        fi
        mv -- "$upload_path" "$signal_path" \
            || fail "the ${signal_name} signal could not be published atomically."
        break
    done
}

require_keeper_only
write_armed
wait_for_signal capture "$capture_upload_path" "$capture_path"

if [ -L "$peak_path" ] || [ ! -f "$peak_path" ] \
    || [ -L "$pids_path" ] || [ ! -f "$pids_path" ]; then
    fail "the target cgroup peak or pids file disappeared."
fi
require_keeper_only
peak_memory_bytes=$(cat "$peak_path") \
    || fail "the target cgroup peak could not be read."
case "$peak_memory_bytes" in
    ''|0|0[0-9]*|*[!0-9]*) fail "the target cgroup peak is not canonical and positive." ;;
esac

umask 077
if ! (set -C; printf '%s\n%s\n%s\n%s\n%s\n' \
    'sharplabnext-runtime-measurement-sidecar-v1' \
    "$token" "$target_id" "$cgroup_kind" "$peak_memory_bytes" > "$completion_tmp"); then
    fail "the completion record could not be created."
fi
chmod 0600 "$completion_tmp"
if [ "$(stat -c '%u:%g:%a' "$completion_tmp")" != "1654:1654:600" ]; then
    fail "the completion record owner or mode is invalid."
fi
mv -- "$completion_tmp" "$completion_path"

wait_for_signal finish "$finish_upload_path" "$finish_path"
trap - HUP INT TERM
exit 0
