#!/bin/sh
set -eu

smoke_directory="${1:?A published JIT profiler smoke directory is required.}"
inspector=/opt/sharplabnext/SharpLabNext.JitInspector.dll
profiler=/opt/sharplabnext/SharpLabNext.JitProfiler.so
assembly="$smoke_directory/SharpLabNext.JitProfilerSmoke.dll"
live_map_path=/tmp/sharplabnext-jit-live.map
live_rich_map_path=/tmp/sharplabnext-jit-live-rich.map
live_stdout_path=/tmp/sharplabnext-jit-live.stdout
live_stderr_path=/tmp/sharplabnext-jit-live.stderr

rm -f \
  /tmp/sharplabnext-jit-smoke-*.map \
  /tmp/sharplabnext-jit-smoke-*.rich.map \
  /tmp/sharplabnext-jit-smoke-*.asm \
  /tmp/sharplabnext-jit-smoke-*.frames \
  /tmp/sharplabnext-jit-smoke-*.decoded \
  "$live_map_path" \
  "$live_rich_map_path" \
  "$live_stdout_path" \
  "$live_stderr_path"

run_mapping_smoke() {
  method_filter="$1"
  expected_display_name="$2"
  minimum_sequence_point_ranges="$3"
  suffix="$4"
  expected_mapping_source="$5"
  map_path="/tmp/sharplabnext-jit-smoke-$suffix.map"
  rich_map_path="/tmp/sharplabnext-jit-smoke-$suffix.rich.map"
  assembly_path="/tmp/sharplabnext-jit-smoke-$suffix.asm"
  frames_path="/tmp/sharplabnext-jit-smoke-$suffix.frames"
  decoded_path="/tmp/sharplabnext-jit-smoke-$suffix.decoded"

  env \
    DOTNET_EnableDiagnostics=1 \
    COMPlus_EnableDiagnostics=1 \
    DOTNET_EnableDiagnostics_IPC=0 \
    COMPlus_EnableDiagnostics_IPC=0 \
    DOTNET_EnableDiagnostics_Debugger=0 \
    COMPlus_EnableDiagnostics_Debugger=0 \
    DOTNET_EnableDiagnostics_Profiler=1 \
    COMPlus_EnableDiagnostics_Profiler=1 \
    CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER='{cf0d821e-299b-5307-a3d8-b283c03916dd}' \
    CORECLR_PROFILER_PATH="$profiler" \
    SHARPLABNEXT_JIT_MAP_MODULE=SharpLabNext.JitProfilerSmoke.dll \
    SHARPLABNEXT_JIT_MAP_PATH="$map_path" \
    SHARPLABNEXT_JIT_RICH_MAP_PATH="$rich_map_path" \
    COMPlus_RichDebugInfo=1 \
    DOTNET_RichDebugInfo=1 \
    COMPlus_TieredCompilation=0 \
    COMPlus_JitDisasm="*$method_filter*" \
    COMPlus_JitDisasmAssemblies=SharpLabNext.JitProfilerSmoke \
    COMPlus_JitDisasmWithCodeBytes=1 \
    COMPlus_JitStdOutFile="$assembly_path" \
    SHARPLABNEXT_JIT_OUTPUT_PATH="$assembly_path" \
    dotnet "$inspector" "$assembly" "$method_filter" > "$frames_path"

  while IFS= read -r frame; do
    printf '%s' "$frame" | base64 --decode
  done < "$frames_path" > "$decoded_path"

  test "$(grep --text --only-matching '"SourceRange":{[^}]*}' "$decoded_path" | sort -u | wc -l)" \
    -ge "$minimum_sequence_point_ranges"
  grep --text --fixed-strings --quiet "$expected_display_name" "$decoded_path"
  grep --text --fixed-strings --quiet \
    "\"MappingSource\":\"$expected_mapping_source\"" \
    "$decoded_path"
  ! grep --text --fixed-strings --quiet '"MappingSource":"marker"' "$decoded_path"
  grep --fixed-strings --quiet 'SLJM1' "$map_path"
  grep --fixed-strings --quiet 'SLJR1' "$rich_map_path"
  grep --text --extended-regexp --quiet '^method=[0-9a-f]+ ' "$rich_map_path"
  test "$(wc -c < "$map_path")" -le 8388608
  test "$(wc -c < "$rich_map_path")" -le 8388608
  ! grep --text --fixed-strings --quiet 'offset=0x' "$decoded_path"
}

run_mapping_smoke \
  OrdinarySingleSequencePoint \
  MappingSmoke.OrdinarySingleSequencePoint \
  1 \
  ordinary \
  ordinary
run_mapping_smoke \
  SameLineFor \
  MappingSmoke.SameLineFor \
  5 \
  same-line \
  rich
same_line_decoded=/tmp/sharplabnext-jit-smoke-same-line.decoded
grep --text --fixed-strings --quiet \
  '"SourceRange":{"StartLine":22,"StartCharacter":13,"EndLine":22,"EndCharacter":26}' \
  "$same_line_decoded"
grep --text --fixed-strings --quiet \
  '"SourceRange":{"StartLine":22,"StartCharacter":28,"EndLine":22,"EndCharacter":41}' \
  "$same_line_decoded"
grep --text --fixed-strings --quiet \
  '"SourceRange":{"StartLine":22,"StartCharacter":43,"EndLine":22,"EndCharacter":46}' \
  "$same_line_decoded"
run_mapping_smoke \
  ConstructedGeneric \
  MappingSmoke.ConstructedGeneric \
  3 \
  constructed-generic \
  rich

env \
  DOTNET_EnableDiagnostics=1 \
  COMPlus_EnableDiagnostics=1 \
  DOTNET_EnableDiagnostics_IPC=0 \
  COMPlus_EnableDiagnostics_IPC=0 \
  DOTNET_EnableDiagnostics_Debugger=0 \
  COMPlus_EnableDiagnostics_Debugger=0 \
  DOTNET_EnableDiagnostics_Profiler=1 \
  COMPlus_EnableDiagnostics_Profiler=1 \
  CORECLR_ENABLE_PROFILING=1 \
  CORECLR_PROFILER='{cf0d821e-299b-5307-a3d8-b283c03916dd}' \
  CORECLR_PROFILER_PATH="$profiler" \
  SHARPLABNEXT_JIT_MAP_MODULE=SharpLabNext.JitProfilerSmoke.dll \
  SHARPLABNEXT_JIT_MAP_PATH="$live_map_path" \
  SHARPLABNEXT_JIT_RICH_MAP_PATH="$live_rich_map_path" \
  COMPlus_RichDebugInfo=1 \
  DOTNET_RichDebugInfo=1 \
  DOTNET_ROLL_FORWARD=Major \
  COMPlus_TieredCompilation=0 \
  dotnet "$assembly" hold > "$live_stdout_path" 2> "$live_stderr_path" &
smoke_pid=$!

cleanup_live_smoke() {
  kill "$smoke_pid" 2>/dev/null || true
  wait "$smoke_pid" 2>/dev/null || true
}
trap cleanup_live_smoke EXIT HUP INT TERM

attempt=0
while ! grep --fixed-strings --quiet 'SLJM1' "$live_map_path" 2>/dev/null || \
      ! grep --text --extended-regexp --quiet '^method=[0-9a-f]+ ' "$live_rich_map_path" 2>/dev/null; do
  if ! kill -0 "$smoke_pid" 2>/dev/null; then
    cat "$live_stderr_path" >&2
    exit 1
  fi

  attempt=$((attempt + 1))
  test "$attempt" -lt 100
  sleep 0.1
done

grep --fixed-strings --quiet 'SLJR1' "$live_rich_map_path"
grep --text --extended-regexp --quiet '^method=[0-9a-f]+ ' "$live_rich_map_path"

test -r "/proc/$smoke_pid/status"
! find /tmp -maxdepth 1 -name "dotnet-diagnostic-$smoke_pid-*" -print -quit | grep --quiet .
! grep --fixed-strings --quiet "dotnet-diagnostic-$smoke_pid-" /proc/net/unix
awk '$1 == "CapEff:" { if ($2 != "0000000000000000") exit 1 }' "/proc/$smoke_pid/status"

cleanup_live_smoke
trap - EXIT HUP INT TERM
