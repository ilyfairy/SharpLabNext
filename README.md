# SharpLabNext

English | [简体中文](README.zh-CN.md)

SharpLabNext is a catalog-driven .NET compiler and runtime workbench. It keeps
language services, compilation, artifact processing, and execution behind
separate worker boundaries, while presenting them through a compact desktop and
mobile web interface.

The checked-in [Catalog](profiles/catalog/catalog.json) is the source of truth
for installed languages, toolchains, outputs, reference sets, runtimes, and
approved compatibility routes. Exact upstream versions and source identities
are recorded in [profiles/lock.json](profiles/lock.json).

## Highlights

- C#, Visual Basic, F#, G#, PeachPie PHP, IL, experimental x64 C++/CLI and J#,
  and a MiniLang SDK sample.
- Roslyn Stable, source-built Roslyn Main, and an atomic experimental C# const
  generics toolchain/runtime/ILSpy combination.
- Decompiled C#, IL, IL verification, JavaScript through source-built JSIL,
  AST, Explain, Run, compact all-method JIT assembly with source navigation,
  execution flow, and rewritten Run IL where the selected pipeline supports
  them. Decompiled C# is the default result.
- Monaco on desktop and CodeMirror on compact/mobile viewports, with a manual
  persistent browser-local editor choice and shared source/result font size.
- Side-by-side desktop layout and a vertical source/results split on mobile.
- LSP 3.17 and live operation control over WebSocket. Safe outputs and Run use
  separate debounce windows; JIT and Execution Flow remain explicit actions.
- Per-language browser-local workspaces, multi-file editing, semantic tokens,
  diagnostics, completion, hover, signature help, and code actions according
  to each language capability manifest.
- SharpLab v1/v2 and legacy Gist import, plus a canonical SharpLabNext v3 share
  format. GitHub OAuth and authenticated Gist writes are optional.
- Signed offline bundles with exact image identities, SBOMs, checksums, SLSA
  provenance, deployment scripts, and rollback support.

## Supported Languages

| Language | Current toolchain | Notes |
| --- | --- | --- |
| C# | `roslyn-stable`, `roslyn-stable-netfx48`, `roslyn-main`, `roslyn-const-generics` | Declared Roslyn LSP capabilities, AST, Explain, managed PE, .NET Framework 4.8, and the experimental const-generics profile. |
| Visual Basic | `roslyn-stable`, `roslyn-stable-netfx48`, `roslyn-main` | Roslyn LSP, AST, managed PE, and .NET 10/.NET 11 Preview/.NET Framework 4.8 routes. |
| F# | `fsharp-stable` | FSharp.Compiler.Service LSP/build path, AST, source ordering, and managed PE. |
| G# | `gsharp-stable`, `gsharp-legacy-0.3.8` | G# 0.3.33 by default, with pinned 0.3.8 compatibility; both compiler/LSP profiles share one worker image and produce managed PE/PDB artifacts. |
| PHP | `peachpie-stable` | PeachPie 1.1.13 diagnostics and managed PE pipeline. Full PHP LSP features are not claimed. |
| IL | `mobius-ilasm-stable` | Context-aware semantic language services from the pinned ILSense core and isolated Mobius.ILasm compilation to managed PE. |
| C++/CLI | `msvc-cppcli-netfx48` | Experimental x64 MSVC 19.51/`/clr` compilation to a truthful .NET Framework 4.8 mixed PE. Lexical editing, Compile Check, focused IL/Decompiled C#, and Wine Run are supported; LSP, IL Verify, JIT, instrumentation, and Execution Flow are not claimed. |
| J# | `vjc-jsharp20` | Experimental Visual J# 2.0 Second Edition compilation to an AMD64 CLR 2.0 managed executable. Lexical editing, Compile Check, IL, Decompiled C#, and the dedicated Wine/CLR2 Run route are supported; LSP, AST, IL Verify, JIT, instrumentation, and Execution Flow are not claimed. |
| MiniLang | `minilang-stable` | SDK/conformance sample that emits CIL and demonstrates a third-party language worker. |

Current routable outputs include:

- Compile Check for every installed language.
- AST for C#, Visual Basic, and F#; Explain for C#.
- Generated IL for MiniLang.
- IL, Decompiled C#, and IL Verify for managed assemblies.
- JavaScript for ordinary .NET 10/.NET Main managed assemblies through the
  dedicated source-built JSIL processor.
- Run and compact all-user-method JIT ASM on compatible .NET runtimes.
- Execution Flow for C#, Visual Basic, F#, and G# on compatible standard
  runtimes.
- Rewritten Run IL for standard managed pipelines.

Availability is resolved from the selected language, toolchain, reference set,
artifact processor, output, and runtime. The workbench does not present a route
that the Catalog cannot prove compatible. Current runtimes are .NET 10.0.9,
.NET 11 Preview 5, the dedicated const-generics runtime, the run-only
.NET Framework 4.8/Wine 9.0 profile, and the independent x64 CLR 2.0/J# Wine
profile.

The `roslyn-stable-netfx48` worker gives C# and Visual Basic a selectable
.NET Framework 4.8 path. It reuses the single locked Roslyn Stable version,
compiles against the independently checksummed
`Microsoft.NETFramework.ReferenceAssemblies.net48` package, publishes an
IL-only framework PE, and routes Run to the separate Wine runtime container.

The J# route is x64-only and uses a separately prepared, digest-pinned operator
image containing licensed Visual J# 2.0 Second Edition and CLR 2.0 assets. The
worker always invokes Framework64 `vjc.exe` with `/platform:x64`; emitted user
assemblies must be AMD64 PE32+, IL-only, and free of 32-bit-required/preferred
flags. Compilation and Run use separate minimized images with a dedicated
win64 prefix. Microsoft binaries, installer paths, and credentials are not
checked into the BSD source tree or published as public images. Operators must
acquire the installers, accept their licenses, build the private prerequisite
with `eng/prepare-jsharp-toolchain.cs`, and keep the resulting release within
their licensed deployment boundary.

## Workbench And Transport

Both editors use the same workspace store. Editor choice, font size, and
inactive language workspaces are persisted in browser local storage and are not
written into share URLs or Gists. Switching language restores that language's
files and correct extensions instead of carrying an incompatible workspace
across languages.

`Ctrl+Space` opens completion in both editors. CodeMirror accepts an active
completion with `Tab` before falling back to indentation. Semantic highlighting
uses server-provided tokens when available and lexical grammars only as a
fallback.

The operation WebSocket at `/api/v1/operations/ws` carries selection
resolution, start, cancel, state, and resumable event subscriptions. LSP
sessions also use WebSocket. Large immutable result documents remain ordinary
HTTP downloads.

## Isolation Model

- Gateway does not load compiler, decompiler, runtime, or Docker SDK types.
- Production builds for Roslyn, F#, G#, PeachPie, IL, C++/CLI, and J# run in
  bounded short-lived child processes inside their toolchain workers.
- JSIL reads immutable managed artifacts in its dedicated non-root worker and
  performs each translation in a bounded short-lived Mono child process. The
  generated JavaScript is not executed by the service.
- Run and JIT execute only in Runtime Supervisor-managed Linux containers with
  no network, read-only root filesystems, dropped capabilities,
  `no-new-privileges`, verified seccomp policy, and CPU/memory/PID/output limits.
  Ordinary CoreCLR jobs use non-root users. The Wine/.NET Framework profile is
  a documented root-in-container exception with only bounded executable tmpfs
  paths and no host filesystem, device, or Docker socket access.
- Runtime container reuse is enabled by default but is limited to compatible,
  serialized Run/JIT jobs on one operation WebSocket. Disconnect, browser
  refresh, or a language/toolchain/reference/output/runtime/pipeline change
  releases the generation. HTTP Run/JIT remains one-shot.
- Artifact identities, worker identities, reference-set attestations, and
  runtime/JIT identities are verified before a production pipeline is accepted.

## Requirements

| Purpose | Requirement |
| --- | --- |
| Host build and tests | .NET SDK 10.0.301 minimum baseline selected by `global.json`, with roll-forward to newer .NET 10 feature bands |
| Frontend | Node.js `>=24 <25` and npm `>=11 <12` from the host system |
| Full local stack | Docker Desktop or Docker Engine with Docker Compose v2 |
| Release bundle build | Docker BuildKit 0.13 or newer |
| Production/offline host | Linux x64, Docker Engine, Compose v2, OpenSSL, `curl`, and `sha256sum` |

The repository uses the system .NET SDK, Node.js, and npm for host commands.
Versions inside Dockerfiles are reproducible image-build inputs, not additional
host installations.

Clone with `--recurse-submodules`. For an existing checkout, initialize the
audited ILSense source at the exact gitlink before restoring or building:

```powershell
git submodule update --init --recursive
```

Build and release automation consumes only the pinned `third_party/ILSense`
submodule and ignores sibling or floating checkouts.

## Quick Start

The complete stack includes source-built Roslyn Main, ConstGenerics, G#,
PeachPie, and operator-built x64 J# images. Prepare the private J# prerequisite
first, then build an unsigned development bundle into a new directory and
install it locally:

```powershell
$repositoryRoot = (Resolve-Path .).Path
$bundleRoot = Join-Path $repositoryRoot "artifacts/bundles/local-$(Get-Date -Format yyyyMMdd-HHmmss)"

./eng/bundle.ps1 `
  -OutputDirectory $bundleRoot `
  -AllowUncommittedSourceForDevelopment

$env:SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE = `
  (Resolve-Path ./deploy/secrets/internal-service-token.dev).Path
$env:SHARPLABNEXT_BIND_ADDRESS = "127.0.0.1"
$env:SHARPLABNEXT_HTTP_PORT = "8080"

& (Join-Path $bundleRoot "install.ps1") `
  -AllowUnsigned `
  -InstallRoot (Join-Path $repositoryRoot "artifacts/local-install") `
  -SmokeBaseAddress "http://127.0.0.1:8080"
```

Open <http://127.0.0.1:8080>. The first full build is intentionally substantial
because locked upstream source trees and reference packs are verified and
built. Rebuild into another empty bundle directory; bundle output directories
are immutable.

Linux uses the equivalent `eng/bundle.sh` and generated `install.sh`. Pass the
Docker socket group and the same host settings before installation:

```bash
export DOCKER_GID="$(stat -c '%g' /var/run/docker.sock)"
export SHARPLABNEXT_BIND_ADDRESS=127.0.0.1
export SHARPLABNEXT_HTTP_PORT=8080
```

Generated bundles contain the signing metadata and installation and rollback
scripts needed for offline deployment. The `deploy/compose.dev.yaml` file is
useful when all referenced development tags already exist, but it is not the
bootstrap path for a clean machine.

Run an external smoke test against the ready stack:

```powershell
dotnet run eng/smoke/gateway-compose.cs -- http://127.0.0.1:8080 --full
```

Stop the stack without deleting the Artifact Store volume:

```powershell
docker compose --project-name sharplabnext `
  -f (Join-Path $bundleRoot "compose.prod.yaml") `
  -f (Join-Path $bundleRoot "compose.generated.yaml") `
  down --remove-orphans
```

Do not add `--volumes` when the local Artifact Store data must be preserved.

For frontend-only iteration against the deployed backend:

```powershell
$env:SHARPLABNEXT_DEV_API_TARGET = "http://127.0.0.1:8080"
npm --prefix frontend ci
npm --prefix frontend run dev
```

Vite serves the frontend on its reported local URL and proxies HTTP and
WebSocket API traffic to the selected backend.

## Build And Test

Run the maintained full validation entry point:

```powershell
./eng/test.ps1
```

```bash
./eng/test.sh
```

These scripts perform locked restore, backend build/tests, frontend lint/test/
build, schema and Compose validation, and the Catalog compatibility audit.

With a ready Compose deployment, include full smoke, runtime failure checks,
and desktop/mobile Playwright coverage:

```powershell
./eng/test.ps1 -SkipBuild -ComposeE2E
```

```bash
./eng/test.sh --skip-build --compose-e2e
```

Use `SHARPLABNEXT_E2E_BASE_URL` to target a non-default deployment. The
compatibility resolver can also be checked directly:

```powershell
dotnet run --project src/Tools/SharpLabNext.CompatibilityCli -- validate
```

## Release And Deployment

`eng/bundle.ps1` and `eng/bundle.sh` build the complete Linux image set and
produce an offline bundle. A production bundle must come from a clean Git
worktree, use an out-of-band trusted signing key, and pass identity, security,
smoke, performance, and browser gates. Do not deploy `deploy/compose.prod.yaml`
by itself; the generated bundle overlay supplies the immutable image and worker
identities required for production startup.

## Extending SharpLabNext

The public SDK package set includes transport contracts, worker hosting,
language/artifact worker SDKs, conformance tests, runtime profile schema, and
the `SharpLab.Runtime` compatibility API. Start a language integration from
[`samples/Languages/SharpLabNext.SampleLanguage.Worker`](samples/Languages/SharpLabNext.SampleLanguage.Worker)
and a runtime integration from
[`samples/Runtimes/dotnet-runtime-template`](samples/Runtimes/dotnet-runtime-template).

Extensions still require a capability manifest, Catalog entry, approved
compatibility edges, release image identity, and conformance/security tests.
They do not require compiler-specific dispatch code in Gateway.

## Contributing

Keep changes within the existing service boundaries, update capability/Catalog
contracts when behavior changes, and run focused tests plus the affected
conformance, compatibility, security, and browser checks. Do not execute or JIT
user code outside Runtime Supervisor-managed containers.

## Security

Production deployments require external secrets, an immutable generated
Compose overlay, and a signed bundle verified against an out-of-band public key
or fingerprint. Keep Gateway behind a trusted reverse proxy, do not expose
internal worker networks, and retain the supplied seccomp/AppArmor and resource
limits. Test operational hardening, upgrades, rollback, and incident procedures
against a non-production deployment before release.

The repository does not currently publish a dedicated vulnerability-reporting
address. Do not post credentials or live exploit details in a public issue;
contact the repository owner privately until a formal security policy is
published.

## License

SharpLabNext's own code is licensed under the
[BSD 2-Clause License](LICENSE). Third-party components and copied compatibility
data retain their original licenses and notices; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the generated release SBOMs.
