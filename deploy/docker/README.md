# Container Build Inputs

Files in this directory are grouped by the image they produce or the stage
they support:

| Prefix or path | Responsibility |
| --- | --- |
| `Dockerfile.gateway` and `Dockerfile.worker*` | Product services and language/artifact workers. |
| `Dockerfile.runtime*` | Runtime job images and runtime candidates. |
| `Dockerfile.operator*` | Private Wine, Framework, J#, and C++/CLI build inputs. |
| `*entrypoint.sh`, `*measurement*.sh`, `*preflight.sh` | Runtime and service entrypoints or checks copied into images. |
| `*framework*`, `*wine*`, `*prefix*` | Build-only Framework/Wine preparation and validation. |
| `certificates/` | Public certificates required by the locked build. |

The public Bake definitions in `eng/bake.hcl` and
`eng/bake.runtime-candidates.hcl` are the only supported callers. Keep image
variants as Bake arguments or stages when the filesystem and security contract
is the same; add a new Dockerfile only for a genuinely different image
boundary. Licensed or operator-supplied bytes must remain named contexts and
must never be copied into the repository's final product images.
