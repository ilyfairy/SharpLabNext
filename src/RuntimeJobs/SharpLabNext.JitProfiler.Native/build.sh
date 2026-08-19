#!/bin/sh
set -eu

output="${1:-/out/SharpLabNext.JitProfiler.so}"
root="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
mkdir -p "$(dirname -- "$output")"

compiler="${CXX:-clang++}"

"$compiler" \
  -shared \
  -O2 \
  -fPIC \
  -fms-extensions \
  -pthread \
  -Wl,--no-undefined \
  -Wno-pragma-pack \
  -DHOST_64BIT \
  -DHOST_AMD64 \
  -D_AMD64_ \
  -DTARGET_AMD64 \
  -DBIT64 \
  -DPAL_STDCPP_COMPAT \
  -DPLATFORM_UNIX \
  -std=c++17 \
  -I "$root/include/src/native" \
  -I "$root/include/src/coreclr/pal/inc/rt" \
  -I "$root/include/src/coreclr/pal/prebuilt/inc" \
  -I "$root/include/src/coreclr/pal/inc" \
  -I "$root/include/src/coreclr/inc" \
  "$root/ClassFactory.cpp" \
  "$root/CorProfiler.cpp" \
  "$root/dllmain.cpp" \
  "$root/guids.cpp" \
  -o "$output"

self_test="$output.self-test"
"$compiler" \
  -O2 \
  -fms-extensions \
  -pthread \
  -Wl,--no-undefined \
  -Wno-pragma-pack \
  -DHOST_64BIT \
  -DHOST_AMD64 \
  -D_AMD64_ \
  -DTARGET_AMD64 \
  -DBIT64 \
  -DPAL_STDCPP_COMPAT \
  -DPLATFORM_UNIX \
  -std=c++17 \
  -I "$root/include/src/native" \
  -I "$root/include/src/coreclr/pal/inc/rt" \
  -I "$root/include/src/coreclr/pal/prebuilt/inc" \
  -I "$root/include/src/coreclr/pal/inc" \
  -I "$root/include/src/coreclr/inc" \
  "$root/RichMapChunkSelfTest.cpp" \
  "$root/guids.cpp" \
  -o "$self_test"
"$self_test"
rm -f "$self_test"
