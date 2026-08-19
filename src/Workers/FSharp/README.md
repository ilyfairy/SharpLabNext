# SharpLabNext F# Worker

The F# toolchain is isolated in its own worker process and does not load FCS in
the Gateway. It pins these packages through the repository lock files:

- `FSharp.Compiler.Service` `43.12.204`
- `FSharp.Core` `10.1.204`

## Build contract

`POST /api/v1/build` accepts the common `BuildRequest` contract for toolchain
`fsharp-stable`. The worker supports `artifact`, `compile-check`, and `ast`.
It requires `.fs` files and treats `WorkspaceSnapshot.SourceOrder` as semantic;
the worker never sorts source files by path.

Compilation uses an explicit read-only reference set. The compiler receives
`--noframework` plus allowlisted reference assembly paths and never scans the
worker runtime directory. PE and portable PDB files are emitted by FCS. The
artifact manifest records the exact FCS, FSharp.Core, reference set, framework,
and source-order identities.

Production verifies `reference-set.attestation.json` before using a reference
bundle. The attestation enumerates every DLL name, size and SHA-256 and rejects
missing, additional, renamed or modified files. `WorkerDescriptor` reports the
Catalog/lock package or source `Digest` separately from the canonical
`ContentDigest` of the assemblies actually loaded. Gateway checks `Digest`; the
full candidate deployment gate requires the same reference-set ID to have the
same `ContentDigest` in all workers.

Every Production Build target runs in a fresh compiler child process while the
parent retains LSP sessions. Child admission is bounded without waiting, and a
hard deadline, request/response/stderr limits, GC heap/RSS limits and
whole-process-tree termination contain timeout, memory, protocol and compiler
crash failures to the current request. `FSharpWorker:BuildProcess:Enabled` may
be false only in Development.

FSharp.Core is linked with the F# compiler's `--standalone` mode. This keeps the
current common PE/PDB artifact envelope sufficient and ensures Run/JIT does not
depend on an unrecorded FSharp.Core installation in the runtime image. The
manifest records `fsharpCoreLinkMode=standalone` and the exact package/product
versions.

The worker rejects `#r`, `#load`, `#I`, `#cd`, `#time`, `#help`, and `#quit`.
These directives would otherwise widen compilation beyond the immutable
workspace/reference-set boundary. User project files, MSBuild targets, package
restore, analyzers, and type-provider packages are not accepted.

## Language service

The worker exposes standard JSON-RPC LSP over:

```text
POST   /api/v1/language-sessions
GET    /api/v1/language-sessions/{sessionId}/lsp   (WebSocket)
DELETE /api/v1/language-sessions/{sessionId}
```

The current adapter is implemented directly on the pinned FCS APIs and
truthfully advertises:

- diagnostics
- completion
- hover
- signature help
- semantic tokens (full-document, UTF-16 coordinates)
- document symbols
- unused-`open` quick fixes and `source.organizeImports`

Semantic classification and unused-`open` analysis come from the pinned FCS
APIs. The worker does not claim formatting, arbitrary refactorings, or other
FsAutoComplete-only features. A future FsAutoComplete stdio adapter may add
those capabilities only after its source commit, FCS identity, process limits,
and contract tests are locked. The present worker does not bundle or launch
FsAutoComplete.

Sessions keep the initial file set and source order immutable. Text and version
updates are bounded, written to an isolated temporary workspace, and checked by
FCS using revisioned project options. Document coordinates use UTF-16 as
required by the shared LSP contract.

## Development

On Windows, `appsettings.Development.json` points to the installed .NET 10.0.9
reference pack and enables the bounded development artifact envelope. Run:

```powershell
dotnet run --project src/Workers/FSharp/SharpLabNext.Worker.FSharp
dotnet test src/Workers/FSharp/SharpLabNext.Worker.FSharp.Tests
```

Production must mount the approved reference bundles at
`/reference-sets/net10-ref` and `/reference-sets/net11-preview-ref`; no fallback
to implementation assemblies exists. Each mounted directory must carry its
verified attestation in Production. `FSharpWorker:BuildProcess` configures the
one-shot compiler concurrency, working-set watermark, request/response/stderr
limits and memory polling interval.

The worker endpoint tests cover reference-set attestation and verify that a
compiler-child timeout does not take down active language sessions.
