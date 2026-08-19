# Roslyn Stable Worker

This worker hosts the exact Roslyn 5.6.0 C# and Visual Basic compiler,
Workspaces, and Features dependency closures. It compiles immutable workspace
snapshots and hosts isolated LSP 3.17 sessions. It never loads, JITs, or
executes a user-produced assembly.

## Reference sets

Every compiler and language session reference is loaded from an explicit server
configuration entry:

```text
ReferenceSets:<referenceSetId>:Path
ReferenceSets:<referenceSetId>:TargetFramework
ReferenceSets:<referenceSetId>:FrameworkVersion
```

The path must point to a reference assembly bundle such as
`Microsoft.NETCore.App.Ref/10.0.9/ref/net10.0`. The provider validates the
`ReferenceAssemblyAttribute`, rejects directories containing
`System.Private.CoreLib.dll`, and never scans the worker's shared runtime
directory. Readiness is unhealthy when a configured bundle is absent or invalid.

Production also requires `reference-set.attestation.json` in each bundle. The
worker verifies the exact DLL names, sizes and SHA-256 values, rejects missing,
additional, renamed or modified assemblies, and reports the result through
`WorkerDescriptor.ReferenceSets`. `Digest` identifies the package/source
selected by Catalog and the release lock; `ContentDigest` canonically identifies
the DLL set actually loaded by this worker. Gateway checks `Digest`, while the
full candidate deployment gate compares `ContentDigest` with every other worker
that exposes the same reference-set ID.

## Build API

```text
GET  /health/ready
GET  /api/v1/worker/describe
POST /api/v1/build
```

`POST /api/v1/build` accepts `SharpLabNext.Contracts.BuildRequest`. Supported
languages are `csharp` (`.cs`) and `visual-basic` (`.vb`); supported targets are
`artifact`, `compile-check`, and `ast`. A normal build returns PE, portable PDB,
content digests, and an `ArtifactManifest` from the core service.
The temporary base64 artifact envelope is development-only and size-limited;
production is expected to commit the binary result to Artifact Store.

Compile Check performs a real bounded Emit. Source generators, analyzers, user
project files, MSBuild targets, package restore, and scripts are not accepted.

In Production every Build target runs in a fresh compiler child process. The
long-lived parent continues to host HTTP and LSP sessions. Child admission is
bounded without waiting, and each child has a hard request deadline,
request/response/stderr byte limits, a GC heap limit plus RSS watermark, and
whole-process-tree termination. Timeout, memory, protocol and compiler-crash
failures affect only that Build. Disabling the child process is permitted only
in Development.

## LSP API

```text
POST   /api/v1/language-sessions
GET/WS /api/v1/language-sessions/{sessionId}/lsp
DELETE /api/v1/language-sessions/{sessionId}
```

The create request and response use `OpenLanguageSessionRequest` and
`LanguageSession`. The WebSocket carries one standard JSON-RPC 2.0/LSP JSON
object per text message. Binary frames and oversized messages are rejected.
Only one WebSocket can be attached to a session at a time; reconnect is allowed
after the previous connection closes.

Each session is permanently bound to either `csharp` or `visual-basic`. Its
workspace uses the matching Roslyn language services, parse options, file
extension, diagnostics, completion engine, quick info, formatter, semantic
classifier, signature adapter, and document-symbol adapter. C# and Visual Basic
sessions may run concurrently without sharing an `AdhocWorkspace`.

Supported methods:

```text
initialize
initialized
shutdown
exit
$/cancelRequest
textDocument/didOpen
textDocument/didChange            # incremental UTF-16 ranges
textDocument/didClose
textDocument/completion
completionItem/resolve
textDocument/hover
textDocument/signatureHelp
textDocument/semanticTokens/full
textDocument/documentSymbol
textDocument/codeAction
```

Diagnostics are published with `textDocument/publishDiagnostics`. Each
diagnostic's `data` contains `workspaceRevision`, `selectionRevision`, and
`documentVersion`. A newer change cancels pending diagnostics; results from an
older document/version snapshot are discarded before publication.

Documents use `file`, `sharplabnext`, or `inmemory` URIs whose decoded path is a
normalized relative workspace path. Absolute host paths and traversal segments
are rejected. Position encoding is UTF-16, matching Monaco and LSP 3.17.

Completion, resolve, hover, semantic classification, import organization, and
formatting use the selected language's Roslyn Workspaces/Features services.
Signature help uses the selected language's Roslyn syntax and semantic model
because Roslyn's in-process signature-help service is not a public supported
API. Current code actions are:

- Insert a missing semicolon for Roslyn diagnostic `CS1002` (C# only).
- Organize imports through Roslyn `Formatter.OrganizeImportsAsync`.
- Format the document through Roslyn `Formatter.FormatAsync`.

The capability list intentionally advertises only these implemented standard
methods, not private Roslyn LanguageServer extensions.

## Limits

`RoslynWorker:LspLimits` controls session TTL/count, JSON message size,
connection concurrency, completions and resolve cache, diagnostics, hover text,
semantic tokens, document symbols, code actions, and diagnostics debounce.
Source file count and byte limits are shared with Build through
`RoslynWorker:CompilationLimits`.

`RoslynWorker:BuildProcess` controls whether the one-shot child is enabled, its
maximum concurrent process count, working-set watermark, request/response and
stderr byte limits, and memory polling interval. These controls are process
containment; a future toolchain that executes untrusted compile-time code still
requires a stronger disposable container sandbox.

## Tests

Run the focused worker suite directly because this variant-owned test project is
not added to the root solution by this module:

```powershell
dotnet test src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable.Tests/SharpLabNext.Worker.Roslyn.Stable.Tests.csproj
```

The suite covers multi-file Build, Compile Check, AST, explicit reference-set
attestation and health, compiler-child crash containment, non-execution of
module initializers, session isolation, monotonic versions, cancellation,
revision-safe diagnostics, every advertised language feature, and the actual
WebSocket JSON-RPC endpoint.
