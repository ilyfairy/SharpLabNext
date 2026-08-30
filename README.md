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

The J# route is x64-only and uses a private base source-built from the reusable
CLR 2/3.5 seed plus the exact Visual J# 2.0 Second Edition `vjredist64.exe`
stored through Git LFS. The worker always invokes Framework64 `vjc.exe` with
`/platform:x64`; emitted user assemblies must be AMD64 PE32+, IL-only, and free
of 32-bit-required/preferred flags. Compilation and Run use separate minimized
images with a dedicated win64 prefix. The Microsoft binary is separately
licensed, is not covered by the repository's BSD license, and is excluded from
public images and bundles. Operators must accept the applicable licenses and
keep the resulting release within their licensed deployment boundary.

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

The build only needs the pinned `third_party/ILSense` source files to be
present. How those files were obtained is outside the build; no Git metadata,
repository status, or submodule command is read by the ordinary entry points.

## Quick Start

Build and packaging entry points have separate responsibilities:

| Entry point | Responsibility |
| --- | --- |
| `eng/build.ps1` / `eng/build.sh` | Restore and build the host backend/frontend and run static contract checks. It does not build Docker images. |
| `eng/build-images.ps1` / `eng/build-images.sh` | Build one ordinary local Docker image by default; no Git metadata, environment switch, or bundle step is required. Pass `-All`/`--all` only for the complete image graph. |
| `eng/bundle.ps1` / `eng/bundle.sh` | Validate and package an already-complete image set. It performs no restore or image build and fails on missing or mismatched images. |
| `eng/release.ps1` / `eng/release.sh` | Complete entry point: preflight output and static contracts, build and validate every planned image, then create the offline bundle only after all images pass. |

For a normal local image, run one command:

```powershell
.\eng\build-images.ps1
```

The image is loaded into the local Docker store as
`sharplabnext/gateway:development`. Pass `-Target <name>` for another
standalone Bake target. Use `-All` or `release.ps1` only for the complete
release graph.

The complete stack also requires licensed Microsoft prerequisites. Two exact
binaries whose original download path is not a reliable clean-build input are
versioned through Git LFS:

- `.NET Framework 2.0 x64`:
  `eng/prerequisites/dotnet-framework-2.0/NetFx64.exe`
- `Visual J# 2.0 Second Edition x64`:
  `eng/prerequisites/visual-jsharp-2.0-se-x64/vjredist64.exe`

The build only requires the expanded bytes of these files. They may be supplied
by any controlled artifact mechanism; Git and Git LFS are not build
requirements. A pointer-only file is rejected by the preparation step with its
expected size and SHA-256.

The manifest and preparation tools require each exact size and SHA-256 before
Docker starts. Each file enters only its private BuildKit context; neither is
executed on the host or copied as an installer into a final image or offline
bundle. `NetFx64.exe` seeds Winetricks' `dotnet20` cache, while
`vjredist64.exe` is installed only inside the J# Docker stage. Other .NET
Framework payloads continue through the locked Winetricks/Microsoft download
paths.

The locked .NET Framework 3.5 SP1, 4.5.1, and 4.7 installers are downloaded
from Microsoft HTTPS origins into the ignored
`artifacts/prerequisites/downloads` cache and checked by size and SHA-256. They
are never started on the Windows host and do not modify its registry or system
directories. They enter BuildKit as private inputs and run silently only
inside isolated Linux/Wine build stages; the temporary installers are removed
after the dedicated Wine prefixes are built. The 3.5 SP1 file pre-populates
Winetricks' exact cache path so container builds do not depend on its legacy
downloader or TLS stack.

The complete image build creates the classic WoW64 build layer once, then
builds exactly two private companion seeds: CLR 2 with .NET Framework 3.5 and
CLR 4 with .NET Framework 4.8. Each exact Framework operator starts from the
opposite-generation seed and installs only its selected target. It still
verifies both prefixes, disables the matching NGen services, removes installer
residue, and records the seed image digest before the existing immutable-file
deduplication runs. Framework operators remain limited to two concurrent
builds.

The build does not export these seeds through an additional `docker image save`
archive. Every invocation submits the same locked build graph to BuildKit;
Docker reuses unchanged layers and naturally invalidates them when the build
input identity changes. Reinstalling Docker or clearing every image rebuilds
the seeds from the supplied and verified input bytes without requiring a
pre-generated image TAR.

J# is rebuilt from its supplied installer bytes and the CLR2 seed. C++/CLI is
rebuilt from the locked `msvc-wine` revision, Visual Studio 18.8 manifest and
.NET Framework 4.8 Developer Pack. The source archives and Microsoft inputs are
downloaded as size/SHA-256-verified bytes under the ignored prerequisite cache;
all extraction, setup and `/clr` preflight work occurs inside Docker.

The J# and C++/CLI bases are likewise submitted to BuildKit on every image
build; no separate `private-images.tar` is written. Docker's layer cache speeds
up ordinary incremental builds, while a cleared Docker store rebuilds them from
the locked inputs above. `artifacts/prerequisites/downloads` caches only
verified source/download bytes, not Docker images.

Build every image and package the result from the repository root:

```powershell
.\eng\release.ps1 -AcceptMicrosoftLicenses
```

This complete local entry point source-builds the private bases,
then injects their inspected digest-pinned references into the remaining image
graph. Even from a clean Git checkout, its images and bundle are explicitly
marked as using development image inputs and form a deployable unsigned
development artifact. Formal signing and promotion still require immutable
images produced through the independent operator-receipt and promotion flows,
followed by packaging with `bundle.ps1`; the development grant does not relax
that boundary.

The ordinary build entry points resolve source identity from the source files
themselves. They do not read Git metadata or worktree status, and an exported
tree without `.git` builds the same way as a checkout. The resulting local
images are ordinary development images and unsigned bundles. Formal signing
and promotion are separate operations that may require independently
verifiable Git provenance.

The old development switch remains accepted for compatibility with existing
automation:

```powershell
.\eng\release.ps1 `
  -AcceptMicrosoftLicenses `
  -AllowUncommittedSourceForDevelopment
```

The default output is `artifacts/sharplabnext-<release-id>`. Bundle outputs are
immutable and never overwritten, so use a new explicit path for another run:

```powershell
.\eng\release.ps1 `
  -AcceptMicrosoftLicenses `
  -OutputDirectory D:\Bundles\SharpLabNext-20260824
```

Use `build-images.ps1` to rebuild ordinary images or `bundle.ps1` to package an
existing image set. A normal `release.ps1`/`release.sh` run first reuses every
matching local image and lets BuildKit rebuild only changed inputs; a bundle
failure therefore does not normally trigger a full rebuild. `-BundleOnly` (or
`--bundle-only`) is an optional direct packaging shortcut. `-Offline` only
prevents prerequisite-cache downloads; a cold BuildKit cache can still require
the locked Docker, NuGet, npm, or source origins.

The generated `.env` selects `compose.prod.yaml` and `compose.generated.yaml`
in the correct order and fixes the Compose project name. It contains only
non-secret defaults; the deployment entry points supply the real host token
path and Docker socket group on every invocation. Do not edit files inside an
immutable or signed bundle.

To test the unsigned development bundle on Windows, provide the ignored local
development token and run its installer:

```powershell
$bundleRoot = (Resolve-Path .\artifacts\sharplabnext-development).Path
$env:SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE = `
  (Resolve-Path .\deploy\secrets\internal-service-token.dev).Path
& (Join-Path $bundleRoot "install.ps1") `
  -AllowUnsigned `
  -InstallRoot (Join-Path (Resolve-Path .\artifacts) "local-install") `
  -SmokeBaseAddress "http://127.0.0.1:8080"
```

On Linux, each new transferred bundle is deployed or upgraded with one entry
point. It loads the archive, starts the immutable Compose set, checks readiness,
and rolls back on failure:

```bash
sudo env SHARPLABNEXT_HOME=/opt/sharplabnext \
  sh ./deploy.sh --allow-unsigned
```

Use the trust flags documented by `install.sh` instead of `--allow-unsigned`
for a formally signed bundle. The first full build is intentionally substantial
because locked upstream source trees and reference packs are verified and built.

Generated bundles contain the signing metadata and installation and rollback
scripts needed for offline deployment. The `deploy/compose.dev.yaml` file is
useful when all referenced development tags already exist, but it is not the
bootstrap path for a clean machine.

Run an external smoke test against the ready stack:

```powershell
dotnet run eng/smoke/gateway-compose.cs -- http://127.0.0.1:8080 --full
```

From the active deployment directory, stop the stack without deleting the
Artifact Store volume:

```powershell
docker compose down --remove-orphans
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

`eng/release.ps1` and `eng/release.sh` build the complete Linux image set and
produce an offline bundle. `eng/bundle.ps1` and `eng/bundle.sh` package only an
already-built and validated image set. A production bundle must come from a clean Git
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
