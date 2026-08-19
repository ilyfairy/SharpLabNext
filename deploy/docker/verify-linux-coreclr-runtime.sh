#!/bin/sh
set -eu

if [ "$#" -ne 4 ]; then
    echo "Usage: verify-linux-coreclr-runtime.sh ROOT VERSION RUNTIME_COMMIT JIT_COMMIT" >&2
    exit 64
fi

runtime_root=$1
runtime_version=$2
expected_runtime_commit=$3
expected_jit_commit=$4
shared_directory="${runtime_root}/shared/Microsoft.NETCore.App/${runtime_version}"
fxr_directory="${runtime_root}/host/fxr/${runtime_version}"
version_file="${shared_directory}/.version"

test -x "${runtime_root}/dotnet"
test -d "${shared_directory}"
test -d "${fxr_directory}"
test -f "${version_file}"

actual_commit=$(sed -n '1{s/\r$//;p;}' "${version_file}")
actual_version=$(sed -n '2{s/\r$//;p;}' "${version_file}")
if [ "${actual_commit}" != "${expected_runtime_commit}" ]; then
    echo "Runtime archive commit '${actual_commit}' does not match '${expected_runtime_commit}'." >&2
    exit 1
fi
if [ "${actual_commit}" != "${expected_jit_commit}" ]; then
    echo "Runtime archive JIT commit '${actual_commit}' does not match '${expected_jit_commit}'." >&2
    exit 1
fi
if [ "${actual_version}" != "${runtime_version}" ]; then
    echo "Runtime archive version '${actual_version}' does not match '${runtime_version}'." >&2
    exit 1
fi

native_library_count=$(find "${shared_directory}" "${fxr_directory}" \
    -type f -name '*.so' -print | wc -l)
if [ "${native_library_count}" -eq 0 ]; then
    echo "Runtime archive contains no native Linux libraries to verify." >&2
    exit 1
fi

find "${shared_directory}" "${fxr_directory}" -type f -name '*.so' -print \
    | sort \
    | while IFS= read -r library; do
        if ! output=$(LD_LIBRARY_PATH="${shared_directory}:${fxr_directory}" ldd "${library}" 2>&1); then
            echo "Could not inspect native dependencies for ${library}." >&2
            printf '%s\n' "${output}" >&2
            exit 1
        fi
        if printf '%s\n' "${output}" | grep -q 'not found'; then
            missing_sonames=$(printf '%s\n' "${output}" \
                | sed -n 's/^[[:space:]]*\([^[:space:]]*\)[[:space:]]*=>[[:space:]]*not found.*$/\1/p' \
                | sort -u)
            if [ "$(basename "${library}")" = 'libcoreclrtraceptprovider.so' ] \
                && [ "${missing_sonames}" = 'liblttng-ust.so.0' ]; then
                # The official 3.x runtime images intentionally omit the
                # optional LTTng userspace tracer. Runtime jobs disable
                # diagnostics, so this provider is never loaded. Keep the
                # exception exact: no other file or missing soname is allowed.
                continue
            fi
            echo "Unresolved native dependencies for ${library}." >&2
            printf '%s\n' "${output}" >&2
            exit 1
        fi
    done

printf 'Verified %s Linux native libraries for .NET %s (%s).\n' \
    "${native_library_count}" "${runtime_version}" "${actual_commit}"
