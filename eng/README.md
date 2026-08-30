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

`build-images` defaults to one local image (`gateway:development` with the
default prefix). Use `--target` for another standalone Bake target and `--all`
only for the complete release graph. Ordinary builds use source content
identity and do not require Git metadata. The default test gate recursively
runs all `eng/tests/**/*.test.mjs` files; invoke `node --test` with that file
set directly when only the Node gate is needed.
