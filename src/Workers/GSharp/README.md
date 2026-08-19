# G# worker

This worker integrates two pinned releases of the MIT-licensed G# toolchain
without loading compiler types into Gateway or the worker host process:

- `gsharp-stable`: G# `v0.3.33` at commit
  `aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d`.
- `gsharp-legacy-0.3.8`: G# `v0.3.8` at commit
  `723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01`.

Both profiles share one worker endpoint and one container. Each Build or
language-session request selects exactly one profile by `ToolchainId`; the
worker then starts only that profile's compiler or language-server child
process.

- Every Build starts the selected profile's `gsc.dll` as a bounded child
  process.
- The compiler receives only normalized `.gs` workspace files and the selected
  attested reference assembly directory.
- Successful artifacts use `dotnet-managed-pe-v1` and contain the managed PE
  plus a sidecar Portable PDB.
- Every language session starts the selected profile's
  `GSharp.LanguageServer.dll` and bridges WebSocket JSON messages to standard
  LSP `Content-Length` framing.
- Both pinned releases target .NET 10, so the worker advertises `net10-ref`.
  Emitted managed PE can still run and JIT on approved compatible .NET 10 or
  .NET 11 runtime profiles.

For local tests, build both fixed source checkouts first:

```powershell
dotnet restore artifacts/source-cache/gsharp-v0.3.33/src/Compiler/Compiler.csproj --configfile artifacts/source-cache/gsharp-v0.3.33/nuget.config
dotnet restore artifacts/source-cache/gsharp-v0.3.33/src/LanguageServer/LanguageServer.csproj --configfile artifacts/source-cache/gsharp-v0.3.33/nuget.config
dotnet build artifacts/source-cache/gsharp-v0.3.33/src/Compiler/Compiler.csproj -c Release --no-restore
dotnet build artifacts/source-cache/gsharp-v0.3.33/src/LanguageServer/LanguageServer.csproj -c Release --no-restore
dotnet restore artifacts/source-cache/gsharp-v0.3.8/src/Compiler/Compiler.csproj --configfile artifacts/source-cache/gsharp-v0.3.8/nuget.config
dotnet restore artifacts/source-cache/gsharp-v0.3.8/src/LanguageServer/LanguageServer.csproj --configfile artifacts/source-cache/gsharp-v0.3.8/nuget.config
dotnet build artifacts/source-cache/gsharp-v0.3.8/src/Compiler/Compiler.csproj -c Release --no-restore
dotnet build artifacts/source-cache/gsharp-v0.3.8/src/LanguageServer/LanguageServer.csproj -c Release --no-restore
dotnet test src/Workers/GSharp/SharpLabNext.Worker.GSharp.Tests/SharpLabNext.Worker.GSharp.Tests.csproj -c Release
```

Production configuration must provide both profiles' compiler and
language-server paths, the reference-set directory and attestation, and the
generated release/image identity. The worker fails startup when any configured
G# executable is absent.
