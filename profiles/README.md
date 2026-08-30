# Release profile updates

`channels/*.yaml` records floating update intent. Production and Docker builds
consume only an exact, approved `profiles/lock.json`; they never resolve a
floating channel or download a runtime at startup.

## Candidate pipeline

Run the stages separately when reviewing an update:

```powershell
./eng/update-profiles.ps1 check --fail-on-change
./eng/update-profiles.ps1 resolve --release-id 2026.07.11.1
./eng/update-profiles.ps1 build
./eng/update-profiles.ps1 test --test-scope full
./eng/update-profiles.ps1 promote
```

The Bash wrapper accepts the same arguments:

```bash
./eng/update-profiles.sh check --fail-on-change
./eng/update-profiles.sh resolve --release-id 2026.07.11.1
./eng/update-profiles.sh build
./eng/update-profiles.sh test --test-scope full
./eng/update-profiles.sh promote
```

`resolve` prints the candidate digest. Later stages use the latest recorded
candidate by default, or accept `--candidate-digest sha256:...` / `--candidate
PATH` explicitly. The legacy command-less invocation remains equivalent to
`resolve`. Legacy `--apply` now runs the complete resolve, build, full-test and
promote pipeline; it cannot bypass release gates.

## Stored state

## Runtime matrix candidates

`runtime-matrix.json` describes the requested version matrix, but a matrix
entry is not automatically a deployable runtime. The generator writes the
corresponding operation profiles to `profiles/runtimes/candidates/`. That
directory is review material only: candidate profiles are never loaded by the
Runtime Supervisor, included in a release bundle, or exposed as selectable
presets until the exact image passes the promotion/preflight gates in ADR
0025. Top-level files in `profiles/runtimes/` are the active, promoted
profiles. Regenerating the matrix therefore cannot overwrite a running
profile or silently change its runtime/JIT identity.

`node eng/validation/validate-schemas.mjs` validates both channels with different
boundaries: candidate files are schema-checked only, while top-level active
files must map to a healthy selectable Catalog runtime with matching
runtime/image identity. Candidate identity closure is checked by the
promotion verifier after an immutable image and release-lock entry exist.

Use the following command to validate the matrix without changing the active
catalog or profiles:

```powershell
dotnet run eng/tools/generate-runtime-matrix.cs -- --check
```

Candidate profile generation is explicit and may overwrite only files below
the candidate directory:

```powershell
dotnet run eng/tools/generate-runtime-matrix.cs -- --overwrite-profiles
```

Promotion must materialize an immutable image and update the Catalog, profile,
deployment image, release lock, and preflight receipt together. A newer matrix
version must not replace an older healthy profile merely because it reuses the
same logical runtime ID.

After the canonical receipt and every retained evidence file have been
reviewed, stage the complete promotion without changing active files:

```powershell
node eng/release/promote-runtime-matrix.mjs --profile-id dotnet-9-linux-x64 --check
```

Remove `--check` to commit the promotion. The materializer verifies the
receipt/evidence digests and source revision, runs the matrix generator in an
isolated staging directory, then closes the Catalog runtime, top-level active
profile, source release-lock component, deployment image definition, and
matrix receipt reference before replacing anything. The active profile stores
the canonical `promotionReceipt`; the deployment definition stores the same
receipt's registry-digest `immutableReference`. Local Docker image IDs are not
deployment references and are never written to the source release lock.

The replacement set is rollback-capable and the matrix receipt binding is the
last commit point. A validation error, concurrent input change, cancellation,
or replacement failure leaves the previous active material authoritative.
Do not copy a candidate profile or edit `promotionState` manually.

`promotionState: verified` is valid only with a content-addressed
`promotionReceipt` reference. The referenced file must be named
`profiles/runtime-promotion-receipts/<profile-id>.json`; both schema validation
and `generate-runtime-matrix.cs` recompute its SHA-256 and close the receipt
against the exact matrix target, platform, immutable image, runtime/JIT
identity, upstream component source URI/digest, source revision, and every
declared capability check. Receipt schema version 2 binds Run and JIT
independently to an implementation, container assembly path, and assembly
SHA-256; profiler-backed Linux JIT additionally binds its profiler path and
SHA-256. These helper hashes remain in the receipt and are not duplicated in
the Runtime Profile. Each check must retain its exact evidence bytes at
`profiles/runtime-promotion-evidence/<profile-id>/<capability>.json` and record
their SHA-256. Validation rejects missing files, links, directory escape,
files larger than 1 MiB, and digest drift. A hash without the canonical retained
file is not promotion evidence. A blocked matrix candidate does not need a
receipt and cannot make an active healthy top-level runtime disappear.
ProfileUpdater's release-candidate receipt is a separate, additional gate and
cannot substitute for this per-runtime evidence.

The same v2 receipt requires `image.sizeBytes` from Docker inspect and a
`performance` object. That object content-addresses a versioned policy at
`profiles/runtime-performance-policies/<policy-id>.json` and raw evidence at
`profiles/runtime-promotion-evidence/<profile-id>/performance.json`. The
evidence repeats immutable image/source/capability/mapping identities and
contains exact cold/warm Run, JIT, and (when declared) mapping samples with
latency and peak container memory. Node validation and BundleBuilder enforce
the same scenario set, sample counts, positive finite values, nearest-rank P95,
single-sample and memory budgets, and inspected image-size budget.
BundleBuilder also compares the recorded size to a fresh inspect before
signing. These contracts are not evidence by themselves; a row stays blocked
until a real networkless Supervisor preflight produces the retained bytes.

BundleBuilder treats the receipt as the signed release boundary. It recomputes
the receipt and evidence digests, requires the deployment repository digest to
appear in the inspected image's `RepoDigests`, captures the resulting immutable
image ID, and reads every bound helper/profiler from that ID. It rejects a
missing, linked, non-regular, empty, oversized, or path-escaped file and compares
the host-computed SHA-256 before signing. A local image ID is inspection evidence
only and must never replace the deployment `repository@sha256:` reference.

Candidate data is content-addressed by the SHA-256 of the exact `lock.json`
bytes:

```text
artifacts/profile-updater/
  state.json
  candidates/<sha256>/
    lock.json
    receipt.json
    compatibility-report.json
```

The receipt binds the candidate digest to the source approved-lock digest and
release ID. It appends start/completion times, result, configuration, test scope
and external command exit codes for every stage attempt. Build, test and promote
recompute both digests before doing work. A candidate becomes stale as soon as
`profiles/lock.json` changes and must be resolved again.

`build` performs locked NuGet/npm restore, frontend lint/build, solution build,
and Buildx Bake. `DOTNET10_RUNTIME_*` and `DOTNET11_RUNTIME_*` Bake variables are
read exclusively from the candidate lock, so an approved image cannot silently
use the previous runtime URL, version or checksum.

The source lock contains direct release/build inputs, not a second package
manifest. React, Vite and other frontend versions come from the root package in
`frontend/package-lock.json`; legacy `frontend-*` components are removed during
resolution. Maintained provenance refers to source/runtime/toolchain components
by lock ID and retains only license, build policy, patch paths and feature
contracts. BundleBuilder resolves the complete identity from the candidate lock
when it writes SLSA. Ordered patch SHA-256 values are computed from the actual files. Run
`npm --prefix frontend run validate:inputs` (or add `--json` to the underlying
Node command) before resolving a candidate. Build-only direct packages such as
`const-generics-versiontools` remain explicit lock components.

Multiple worker images may deliberately share one direct upstream toolchain.
For example, `roslyn-stable-netfx48` is a derived component whose single
`{roslyn-stable}` placeholder copies the exact Roslyn package/source identity.
It is not a second channel or independently editable version pin. The derived
component adds `netfx48-managed-ref` as a separate image input and keeps its
source `imageId` empty; BundleBuilder writes each worker's final image identity
only into the generated bundle lock. This keeps routine upgrades at one
maintained version entry while preserving distinct worker, reference-set, SBOM,
and SLSA identities.

`test --test-scope affected` runs focused unit tests plus the release-wide
schema, Compose and compatibility gates. `test --test-scope full` additionally
runs every solution and frontend test. Only a successful latest full-test
receipt can be promoted.

## Promotion and rollback material

Promotion writes immutable history and last-known-good copies before the active
lock is replaced atomically:

```text
profiles/history/<source-sha256>/lock.json
profiles/history/<candidate-sha256>/lock.json
profiles/history/<candidate-sha256>/receipt.json
profiles/last-known-good/previous.lock.json
profiles/last-known-good/lock.json
profiles/last-known-good/receipt.json
profiles/lock.json
```

Each history and last-known-good material snapshot also contains every
top-level active runtime profile under `runtimes/`. Promotion replaces that
complete profile collection together with Catalog, `versions.props`, and
package locks before committing `profiles/lock.json` last. The review-only
`profiles/runtimes/candidates/` directory is excluded from the material digest,
snapshots, and replacement set.

The active `profiles/lock.json` is the final commit point. A resolution, build,
test, receipt, history or last-known-good write failure leaves the current
approved lock unchanged. Offline bundle creation is a subsequent release step;
it continues to consume only the promoted lock and local images.
