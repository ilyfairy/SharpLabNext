# SharpLabNext Artifact Store

The first deployment is a single-node content-addressed store backed by local files and SQLite. It is an internal service: browsers and one-shot runtime containers must not connect to it directly.

## Identity

- `ContentRef` is `sha256:<64 lowercase hex>` over the exact file bytes.
- `ArtifactRef` is `sha256:<64 lowercase hex>` over the canonical artifact manifest, excluding the self-referential `ArtifactId` field.
- Manifest arrays retain their order. Metadata dictionary keys are sorted before hashing.
- File paths are canonical relative `/`-separated paths. Absolute paths, backslashes, empty segments, `.`, `..`, NUL, duplicates, and storage symbolic links are rejected.

`ArtifactIdentity.WithComputedId` and `ContentIdentity.Compute` in `SharpLabNext.ArtifactStore.Client` are the shared producer-side implementations. Workers must use these helpers instead of inventing IDs.

## Internal HTTP API

| Method | Path | Purpose |
| --- | --- | --- |
| `PUT` | `/internal/v1/contents/sha256/{digest}` | Store one raw content blob and verify its digest and `Content-Length`. |
| `GET` | `/internal/v1/contents/sha256/{digest}` | Stream a checksum-verified blob. |
| `PUT` | `/internal/v1/artifacts/sha256/{digest}` | Store a multipart artifact with one JSON `Manifest` field and `Files` parts whose filenames are artifact paths. |
| `GET` | `/internal/v1/artifacts/sha256/{digest}` | Return an `ArtifactBundleDescriptor` containing only virtual paths and opaque refs. |
| `GET` | `/internal/v1/artifacts/sha256/{digest}/files/{path}` | Stream one checksum-verified artifact entry. |
| `POST` | `/internal/v1/artifacts/sha256/{digest}/leases` | Acquire a time-limited GC lease. |
| `PUT` | `/internal/v1/leases/{token}` | Renew a live lease. |
| `DELETE` | `/internal/v1/leases/{token}` | Release a lease; release is idempotent. |
| `POST` | `/internal/v1/maintenance/collect` | Collect expired, unleased artifacts and unreferenced content. |

PUT endpoints accept an optional positive `TtlSeconds` query parameter. Limits are enforced again while reading streams, so a forged or absent `Content-Length` cannot bypass them. Artifact multipart fields are named `Manifest` and `Files`.

Use the strongly typed `ArtifactStoreClient`; callers should not construct these routes manually. A runtime consumer should acquire an artifact lease before resolving and downloading all bundle entries, then release it after the Supervisor has injected the verified inputs.

## Commit Model

1. The request is streamed into a unique directory under `tmp/` while SHA-256 and size are calculated.
2. Every file is checked against the manifest before any artifact becomes visible.
3. Content blobs are atomically moved into the file CAS. Existing blobs are re-hashed before reuse.
4. `manifest.json` and `descriptor.json` are durably flushed, then their directory is atomically moved into the artifact CAS.
5. One SQLite transaction inserts the artifact, entries, content metadata, and reference counts. SQLite metadata is the visibility boundary.

An interrupted request can leave an unreachable content blob or artifact directory, but never a partially visible artifact. Retrying the same content address verifies and adopts the durable data. Abandoned `tmp/` directories are removed at startup.

## Disk Layout

```text
<root>/
  artifacts/sha256/ab/<artifact-digest>/
    manifest.json
    descriptor.json
  contents/sha256/cd/<content-digest>
  metadata/artifacts.db
  metadata/artifacts.db-wal
  metadata/artifacts.db-shm
  tmp/<request-id>/
```

No API response includes `<root>` or another host path. The digest prefix directories only bound directory fan-out; they are not part of the opaque reference format.

## Retention

SQLite tracks artifact and content expiry, content reference counts, last access, and hashed lease tokens. Garbage collection first expires leases, removes expired artifacts that have no live lease, decrements content reference counts, and then removes expired content with a zero count. A lease protects the whole artifact even after its TTL has elapsed.

Important settings are under `ArtifactStore` in `appsettings.json`: root path, per-content and total artifact limits, file count, default/maximum TTL, maximum lease duration, and cleanup interval/batch size.
