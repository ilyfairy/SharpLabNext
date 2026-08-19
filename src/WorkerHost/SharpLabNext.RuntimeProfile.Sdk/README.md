# SharpLabNext Runtime Profile SDK

This package defines the stable contract used to add a Linux runtime image
without changing Gateway request contracts.

A runtime package declares:

- immutable image and runtime/JIT identities;
- accepted artifact formats and required feature tags;
- the container/environment kind plus its trusted host and helper layout;
- a fixed Run/JIT executable and argv template bound to a stable helper
  `implementationId`;
- allowed one-shot container security policies;
- Run/JIT capabilities exposed to the Catalog.

`implementationId` is an executable contract, not descriptive metadata. SDK
validation binds each ID to its fixed helper path, verb, argv ordering, path
style, and supported source-mapping precision:

- `sharplabnext-runner-v1` is the modern CoreCLR Run helper and is required for
  `inspection` or `execution-flow`;
- `sharplabnext-jit-inspector-v1` is the modern CoreCLR JIT helper and is the
  only implementation allowed to claim `linux-profiler` mapping;
- `sharplabnext-legacy-jit-inspector-v1` provides legacy CoreCLR Run/JIT and
  may claim only `none` source mapping;
- `sharplabnext-checked-jit-bridge-v1` keeps the RuntimeFrame-emitting parent
  free of native JIT output and captures a same-runtime child within strict
  byte, time, and process-tree bounds. It may claim `none`, or
  `checked-jit-debug-info` only when retained evidence proves that Checked-JIT
  debug markers join to portable-PDB sequence points;
- `sharplabnext-target-runtime-runner-v1` is the shared `net20` helper launched
  by the exact target Mono or Desktop CLR host. It executes the user assembly
  inside that CLR and emits canonical bounded RuntimeFrames for output, exit,
  and nested exceptions;
- `sharplabnext-wine-runner-v1` is the bounded control-runtime ProcessBridge
  retained for the independently reviewed legacy Wine/J# boundary; and
- `sharplabnext-direct-runtime-v1` permits only the narrowly validated direct
  runtime invocation shape.

Changing only the ID cannot turn an arbitrary command into a trusted helper.
The default modern CoreCLR image contains `SharpLabNext.Runner.dll`,
`SharpLabNext.JitInspector.dll`, and the optional fixed profiler, and runs as
UID/GID 1654. New Mono and `wine-netfx` matrix candidates contain
`SharpLabNext.TargetRuntimeRunner.exe`, expose Run only, and bind it to
`/usr/bin/mono` or `/usr/lib/wine/wine64` respectively. The .NET Framework 4.8
row alone may accept an audited x64 mixed PE in addition to managed PE. The
closed Wine sandbox still runs as the Supervisor's fixed root-Wine profile;
profiles cannot declare arbitrary users or writable paths. The separately
reviewed J# compiler/runtime profile continues to use `SharpLabNext.WineRunner`
and an operator-supplied `vjc.exe` toolchain.

Every runtime accepts a read-only artifact volume at `/workspace`. Runtime
containers are networkless, read-only, capability-free and protected by
no-new-privileges plus the configured syscall policy. A WebSocket session may
reuse its stopped container only while the release, image, command,
environment, resource policy and isolation kind fingerprint remains identical;
changing any of them destroys that generation.

Start with `samples/Runtimes/dotnet-runtime-template`, validate
`runtime-profile.json` against the schema included in this package, add the
matching Catalog and release-lock entries, then run the Supervisor contract and
security tests before promotion.
