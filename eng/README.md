# Build Support

The repository has one ordinary image entry point:

```text
eng/build-images.ps1       # Windows
eng/build-images.sh        # POSIX
```

The scripts below are grouped by ownership so the root of `eng` contains only
public entry points and build inputs:

| Directory | Contents |
| --- | --- |
| `tools/` | File-based .NET utilities used by the host build or Docker stages. |
| `tests/` | Node contract tests. The build, test, and CI entrypoints recursively discover every `*.test.mjs`, including release fixtures. |
| `tests/release/` | Extended promotion/state and fixture tests included in the same recursive Node test gate. |
| `smoke/` | Compose and worker smoke programs. |
| `validation/` | Schema, Compose, and Bake validation commands. |
| `performance/` | Performance gates and their policies. |
| `patches/` | Reviewed source patches only. |
| `prerequisites/` | Operator-provided licensed bytes only. |
| `eng/profiles/` | Public trust keys used by release verification. |

The remaining root files are the JavaScript build/release modules, lock/config
inputs, and public wrappers. A module is kept at the root when it is imported
by Bake or by a release tool and moving it would only add a compatibility
wrapper. Do not add another wrapper for a single `dotnet`, `node`, or `docker`
invocation; put reusable logic in an existing module and expose a command only
when it has a distinct input/output contract.

`build-images` defaults to the Gateway image using the current lock release ID
and permits five concurrent image tasks. Use `--target` for another standalone
Bake target and `--all` only for the complete release graph.
Ordinary builds use source content identity and do not require Git metadata. The default test gate recursively
runs all `eng/tests/**/*.test.mjs` files; invoke `node --test` with that file
set directly when only the Node gate is needed.

The deployment manifest is the source of build planning metadata. Its optional
`producer` field records a non-default Bake or candidate target, and
`buildCapabilities` names shared prerequisites such as Wine or Framework. The
`capabilityDefinitions` section records dependency closure and, when needed,
the provisioner, runtime argument sources, operator script, seed, downloads,
licenses, and environment input. Runtime arguments refer to named outputs from
capabilities, and provisioners run in dependency order. The orchestrator
resolves those declarations generically; it does not branch on a language or
runtime name. Adding an image or capability therefore changes the manifest,
not a `const-generics`/language-specific branch in the scheduler.
