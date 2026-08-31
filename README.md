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
- Runtime coverage from .NET Core 2.0 through .NET 11 Preview, Windows .NET
  5-11 under Wine, every .NET Framework version from 2.0 through 4.8 under
  Wine, Mono 6.12, the J# CLR 2.0 runtime and the const-generics runtime.
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
- Signed offline bundles with exact image identities, SBOMs, checksums, and SLSA
  provenance.

## Supported Languages And Runtimes

This section lists the exact versions available in the current checked-in
Catalog. Old and current runtimes are equally intentional: the playground keeps
them available for behavior comparison, compatibility experiments and
regression testing. Patch versions can change when
[profiles/lock.json](profiles/lock.json) is updated.

Current coverage starts at .NET Core 2.0 and .NET Framework 2.0 and ends at
.NET 11 Preview and .NET Framework 4.8. The current Catalog does not define
.NET Core/.NET Framework 1.x or .NET Framework 4.8.1 profiles.

### Languages And Toolchains

| Language | Toolchains | Current version and scope |
| --- | --- | --- |
| C# | `roslyn-stable`, `roslyn-main`, `roslyn-stable-netfx48`, `roslyn-const-generics` | Roslyn Stable 5.6.0, Roslyn Main 5.10.0 (`708c0a9669c6`), all listed .NET/.NET Framework reference sets, and the atomic experimental const-generics profile. |
| Visual Basic | `roslyn-stable`, `roslyn-main`, `roslyn-stable-netfx48` | Roslyn Stable/Main LSP, AST and managed PE across the listed .NET and .NET Framework reference sets. |
| F# | `fsharp-stable` | FSharp.Compiler.Service 43.12.204, LSP/build, AST, source ordering and managed PE. |
| G# | `gsharp-stable`, `gsharp-legacy-0.3.8` | G# 0.3.33 by default and pinned 0.3.8 compatibility; both profiles produce managed PE/PDB artifacts. |
| PHP | `peachpie-stable` | PeachPie 1.1.13 diagnostics and managed PE. Full PHP LSP capability is not claimed. |
| IL | `mobius-ilasm-stable` | ILSense 0.1.0 semantic services and isolated Mobius.ILasm 0.1.0 compilation to managed PE. |
| C++/CLI | `msvc-cppcli-netfx48` | Experimental x64 MSVC 19.51 `/clr` compilation to a real .NET Framework 4.8 mixed PE; Compile Check, focused IL/Decompiled C# and Wine Run. |
| J# | `vjc-jsharp20` | Visual J# 2.0 Second Edition (2.0.50727.937), AMD64 CLR 2.0 managed executable, focused IL/Decompiled C# and a dedicated Wine Run route. |
| MiniLang | `minilang-stable` | Version 1.0.0 SDK/conformance sample that emits CIL. |

### Native .NET Runtimes - Linux x64

Every row below is installed and healthy. A matching compiler reference set is
also installed for each standard .NET version.

| Runtime | Exact version | Runtime capabilities |
| --- | --- | --- |
| .NET Core 2.0 | 2.0.9 | Run |
| .NET Core 2.1 | 2.1.30 | Run |
| .NET Core 2.2 | 2.2.8 | Run |
| .NET Core 3.0 | 3.0.3 | Run |
| .NET Core 3.1 | 3.1.32 | Run |
| .NET 5 | 5.0.17 | Run |
| .NET 6 | 6.0.36 | Run, JIT ASM |
| .NET 7 | 7.0.20 | Run, JIT ASM |
| .NET 8 | 8.0.29 | Run, JIT ASM |
| .NET 9 | 9.0.18 | Run, JIT ASM |
| .NET 10 | 10.0.10 | Run, JIT ASM, Inspection, Execution Flow |
| .NET 11 Preview | 11.0.0-preview.6.26359.118 | Run, JIT ASM, Inspection, Execution Flow |

### .NET Runtimes Under Wine 9.0 - Linux x64

These profiles run the Windows x64 runtime under Wine. The .NET Core 2.x and
3.x Windows/Wine definitions are retained for historical coverage but are not
currently installed; their native Linux runtimes remain available.

| Runtime | Exact version | Runtime capabilities |
| --- | --- | --- |
| .NET 5 / Wine | 5.0.17 | Run |
| .NET 6 / Wine | 6.0.36 | Run |
| .NET 7 / Wine | 7.0.20 | Run, JIT ASM |
| .NET 8 / Wine | 8.0.29 | Run, JIT ASM |
| .NET 9 / Wine | 9.0.18 | Run, JIT ASM |
| .NET 10 / Wine | 10.0.10 | Run, JIT ASM |
| .NET 11 Preview / Wine | 11.0.0-preview.6.26359.118 | Run, JIT ASM |

### .NET Framework Runtimes Under Wine 9.0 - Linux x64

C# and Visual Basic have matching managed reference sets for every version in
this table. C++/CLI is limited to .NET Framework 4.8. J# uses its separate CLR
2.0 runtime described below.

| .NET Framework | CLR generation | Runtime capabilities |
| --- | --- | --- |
| 2.0 | CLR 2 | Run, JIT ASM |
| 3.0 | CLR 2 | Run, JIT ASM |
| 3.5 | CLR 2 | Run, JIT ASM |
| 4.0 | CLR 4 | Run, JIT ASM |
| 4.5 | CLR 4 | Run, JIT ASM |
| 4.5.1 | CLR 4 | Run, JIT ASM |
| 4.5.2 | CLR 4 | Run, JIT ASM |
| 4.6 | CLR 4 | Run, JIT ASM |
| 4.6.1 | CLR 4 | Run, JIT ASM |
| 4.6.2 | CLR 4 | Run, JIT ASM |
| 4.7 | CLR 4 | Run, JIT ASM |
| 4.7.1 | CLR 4 | Run, JIT ASM |
| 4.7.2 | CLR 4 | Run, JIT ASM |
| 4.8 | CLR 4 | Run, JIT ASM |

### Additional Runtimes

| Runtime | Version | Capabilities | Notes |
| --- | --- | --- | --- |
| Mono / Linux x64 | 6.12.0.182 | Run, JIT ASM | Uses the .NET Framework 4.8 managed reference set. |
| Const Generics Runtime | Locked atomic profile | Run, JIT ASM, Inspection | Must match the const-generics compiler, reference set and artifact processor. |
| Visual J# / CLR 2.0 / Wine 9.0 | J# 2.0.50727.937 | Run | Dedicated x64 J# runtime; not the general .NET Framework 2.0 profile. |

### Outputs And Compatibility

Current routable outputs include Compile Check for every installed language;
AST for C#, Visual Basic and F#; Explain for C#; Generated IL for MiniLang; IL,
Decompiled C# and IL Verify for compatible managed assemblies; JavaScript via
the source-built JSIL processor; Run; compact all-user-method JIT ASM;
Execution Flow; and Rewritten Run IL where the selected pipeline declares the
required capabilities. Decompiled C# is the default output.

Availability is resolved from the complete language, toolchain, reference set,
artifact processor, output and runtime selection. A runtime capability does not
make every producer compatible with it: for example, C++/CLI does not claim JIT
ASM or instrumentation even though the Framework 4.8 runtime can provide JIT
ASM for compatible managed artifacts.

The `roslyn-stable-netfx48` worker reuses the one locked Roslyn Stable version
and compiles C# or Visual Basic against the independently checked Framework
reference assemblies from 2.0 through 4.8. It emits IL-only framework PE files;
Run/JIT are handled by separate Wine runtime containers.

The J# route is x64-only. It invokes Framework64 `vjc.exe /platform:x64`; user
assemblies must be AMD64 PE32+, IL-only and free of 32-bit-required/preferred
flags. Visual J#, .NET Framework installers and MSVC/C++ build assets are
separately licensed and are not covered by this repository's BSD license. A
private bundle built after accepting those licenses is not automatically
redistributable as a public GitHub Release; verify every applicable license
before publishing a bundle or image.

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

### Entry Points

Build and packaging entry points have separate responsibilities:

| Entry point | Responsibility |
| --- | --- |
| `eng/build.ps1` / `eng/build.sh` | Restore and build the host backend/frontend and run static contract checks. It does not build Docker images. |
| `eng/build-images.ps1` / `eng/build-images.sh` | Build one ordinary local Docker image by default; no Git metadata, environment switch, or bundle step is required. Pass `-All`/`--all` only for the complete image graph. |
| `eng/bundle.ps1` / `eng/bundle.sh` | Validate and package an already-complete image set. It performs no restore or image build and fails on missing or mismatched images. |
| `eng/release.ps1` / `eng/release.sh` | Complete entry point: preflight output and static contracts, build and validate every planned image, then create the offline bundle only after all images pass. |

### Build One Image

For a normal local image, run one command:

```powershell
.\eng\build-images.ps1
```

The image is loaded into the local Docker store under the current lock release
ID. Pass `-Target <name>` for another standalone Bake target. Use `-All` or
`release.ps1` only for the complete release graph.

### Licensed Build Inputs

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
deduplication runs. Framework operators use the same `--max-parallel` setting
as the rest of the release graph (the default is 5).

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

### Build A Complete Bundle

Build every image and package the result from the repository root:

```powershell
.\eng\release.ps1 -AcceptMicrosoftLicenses
```

This complete entry point source-builds the private bases, injects their
inspected digest-pinned references into the remaining image graph, and creates
one directly deployable bundle. The normal command produces an unsigned bundle;
formal signing still requires independently verifiable source and image inputs.

The ordinary build entry points resolve source identity from the source files
themselves. They do not read Git metadata or worktree status, and an exported
tree without `.git` builds the same way as a checkout. The resulting images are
ordinary local build outputs and the bundle is unsigned by default. Formal
signing is a separate operation that requires independently verifiable
provenance.

The default output is
`artifacts/releases/sharplabnext-yyyy-MM-dd-HH-mm-ss`. Each timestamped child
directory is a complete deployment unit that can be copied to another host,
renamed for a GitHub release, or archived as a ZIP. Bundle outputs are never
overwritten; use a new explicit path when a specific release name is needed:

```powershell
.\eng\release.ps1 `
  -AcceptMicrosoftLicenses `
  -OutputDirectory D:\Bundles\sharplabnext-2026-08-24
```

### Reuse And Selective Rebuild

Use `build-images.ps1` to build ordinary images or `bundle.ps1` to package an
existing image set. A normal `release.ps1`/`release.sh` run first reuses every
valid local image; source or wrapper changes do not invalidate that reuse. Add
`-RebuildTarget`/`--rebuild-target` for selected images, or
`-RebuildImages`/`--rebuild-images` for an explicit full rebuild. `-BundleOnly`
(or `--bundle-only`) is an optional direct packaging shortcut. `-Offline` only
prevents prerequisite-cache downloads; a cold BuildKit cache can still require
the locked Docker, NuGet, npm, or source origins.

Rebuild selectors accept `image:`, `runtime:`, `toolchain:`, `processor:`,
`producer:`, and `capability:` namespaces. An unqualified selector checks all
of those identities, and `*` is supported, for example `-RebuildTarget
"image:worker-gsharp"` or `-RebuildTarget "*const-generics*"`.

### Deploy The Bundle

The generated `.env` automatically combines `compose.prod.yaml` and
`compose.generated.yaml` and fixes the Compose project name. Every bundle
includes the same editable default token in `secrets/internal-service-token`,
so it starts without an environment-specific setup step. Edit that file to
replace the default. When Runtime Supervisor needs the host Docker socket, set
`DOCKER_GID` in `.env` to the socket group ID.

From the bundle directory, load the bundled images and start Compose:

```powershell
docker load -i images.tar
docker compose up -d
```

Open `http://127.0.0.1:8080/` by default. Run `docker compose port gateway
8080` to see the effective host port. Docker Compose gives variables from the
current shell precedence over the bundle `.env`; when reusing a shell from an
older deployment, remove stale `SHARPLABNEXT_*` overrides or update them
deliberately before creating the containers.

The same commands apply on every supported host. The first full build is
intentionally substantial because locked upstream source trees and reference
packs are verified and built.

From the active deployment directory, stop the stack without deleting the
Artifact Store volume:

```powershell
docker compose down --remove-orphans
```

Do not add `--volumes` when the local Artifact Store data must be preserved.

### Frontend Development

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
already-built and validated image set. The ordinary unsigned bundle is directly
deployable. A formally signed release additionally requires verifiable source
and image provenance, an out-of-band trusted signing key, and the identity,
security, smoke, performance and browser gates. Do not deploy
`deploy/compose.prod.yaml` by itself; the generated bundle overlay supplies the
immutable image and worker identities required for startup.

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

Every bundle contains an editable default internal-service token for immediate
startup. Replace it before exposing the deployment to another machine or an
untrusted network. GitHub OAuth remains disabled until its external client
secret is configured. Keep Gateway behind a trusted reverse proxy, do not
expose internal worker networks, and retain the supplied seccomp/AppArmor and
resource limits. Formally signed releases should be verified against an
out-of-band public key or fingerprint.

The repository does not currently publish a dedicated vulnerability-reporting
address. Do not post credentials or live exploit details in a public issue;
contact the repository owner privately until a formal security policy is
published.

## License

SharpLabNext's own code is licensed under the
[BSD 2-Clause License](LICENSE). Third-party components and copied compatibility
data retain their original licenses and notices; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the generated release SBOMs.
