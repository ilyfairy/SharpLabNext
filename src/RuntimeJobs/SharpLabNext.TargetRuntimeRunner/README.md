# Target runtime runner

This helper targets .NET Framework 2.0 so one binary can execute inside Mono,
Desktop CLR 2, and Desktop CLR 4. It is the Run provider for the Mono and Wine
.NET Framework matrix rows; the modern control runtime must never load the user
assembly for those rows.

```text
SharpLabNext.TargetRuntimeRunner.exe run <absolute-assembly> -- [arguments]
```

The helper duplicates its original stdout for canonical base64 RuntimeFrames,
then redirects managed, CRT, and native user stdout/stderr to private files.
It invokes the real entry point inside the target CLR, waits for Task-shaped
entry points through reflection when the target CLR supports them, and emits
structured exception and exit frames. On Desktop CLR it preserves C++/CLI
mixed-mode console support by starting an entry-point-free PE through its
native Windows entry point while inheriting the already bounded output handles.
This project is a Run provider only; it does not claim Desktop CLR or Mono JIT
disassembly support.

Container build preflight can verify that the exact target CLR starts this
binary and emits a valid frame without loading user code:

```text
SharpLabNext.TargetRuntimeRunner.exe self-test
```
