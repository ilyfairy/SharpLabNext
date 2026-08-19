# SharpLabNext Artifact Contracts

This package defines immutable artifact manifests, file descriptors, runtime
requirements, and derivation metadata. Compiler and language workers publish
these contracts; artifact processors and one-shot runtime jobs consume them.

Artifact and content identifiers are SHA-256 content addresses. Use
`SharpLabNext.ArtifactStore.Client` for canonical path, digest, and manifest
identity helpers rather than calculating identities independently.
