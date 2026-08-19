# Legacy JIT inspector

`SharpLabNext.LegacyJitInspector` is the small process entry point used by
runtime jobs that must run on an old CoreCLR. It intentionally targets
`netcoreapp2.0`; the same assembly can be launched by a newer CoreCLR through
roll-forward, so the runtime-specific worker does not need a second copy of
the reflection and frame protocol code.

## Commands

```text
dotnet SharpLabNext.LegacyJitInspector.dll [--runtime-version <version>] jit <absolute-assembly> [filter]
dotnet SharpLabNext.LegacyJitInspector.dll [--runtime-version <version>] run <absolute-assembly> -- [args]
```

The unprefixed form (`<assembly> [filter]`) remains accepted for existing
runtime jobs. Matrix-generated operations pass `--runtime-version` immediately
before `run` or `jit`; the helper compares the numeric runtime prefix with
`Environment.Version` before loading user code. This prevents a missing target
framework from silently rolling forward to another CoreCLR. Prerelease suffixes
remain bound by the image/profile identity because `Environment.Version` exposes
only numeric components. Every result is emitted as one canonical base64 line per
`SLNR` RuntimeFrame. User stdout and stderr are redirected to private files
before the entry point is invoked; therefore a user `Console.OpenStandardOutput`
or native write cannot inject bytes into the frame stream. A background tailer
forwards appended bytes as `Stdout`/`Stderr` frames while the entry point is
running, then performs a final drain before `Exception` and/or `Exit`. The
`SHARPLABNEXT_MAX_OUTPUT_BYTES` environment value (set by the supervisor) is
enforced in the helper as well as by the supervisor; an overflow emits one
bounded byte beyond the budget so the supervisor can terminate the job without
waiting for user code to return.

For JIT inspection, the selected CoreCLR must be configured with
`COMPlus_JitDisasm` and `COMPlus_JitStdOutFile` (and
`SHARPLABNEXT_JIT_OUTPUT_PATH` must point at the same absolute file). The
helper prepares methods, selects matching JIT sections, counts instructions,
and associates a whole method with Portable PDB source ranges when available.
On Windows/Wine, CoreCLR can write JIT text through UCRT stdout even when
`COMPlus_JitStdOutFile` is set; the helper redirects that native stream before
preparing methods.

## Compatibility boundary

- CoreCLR 2.1 and 3.1: `run` works with this helper. The official retail
  `mcr.microsoft.com/dotnet/core/runtime:2.1` and `:3.1` images tested here do
  not emit `JitDisasm` text, so JIT inspection reports an empty assembly and
  `inspection-failed`; this is an upstream runtime diagnostic limitation.
- CoreCLR 7.0 and 10.0 on Wine: `run` and JIT inspection are verified. The JIT output
  includes the Windows x64 ABI registers (`rcx`, `rdx`, and `rax`) and maps to
  the fixture Portable PDB.
- CoreCLR 5.0 and 6.0 on Wine: `run` is verified, but the retail Windows JIT
  emits no `JitDisasm` file; these profiles are therefore Run-only.
- Native Windows CoreCLR 8.0 was also verified for `run` and JIT inspection.
  Newer CoreCLR releases should be promoted only after the runtime image
  preflight proves that its JIT output file is non-empty and matches the
  prepared method filter.
- CLR 2.0-4.8 and Mono are not handled by this CoreCLR helper. They require a
  separate runner/JIT provider and must not be advertised as supported by
  this project.

`CheckEolTargetFramework=false` is local to this project and only suppresses
the SDK end-of-life warning for the intentional `netcoreapp2.0` target. No
NuGet vulnerability warning is suppressed.
