# SharpLabNext Artifact Store Client

This package provides the bounded streaming Artifact Store client plus the
canonical `ArtifactRef`, `ContentRef`, artifact path, digest, and manifest
identity helpers used by extension workers.

The client talks only to the internal Artifact Store API. Runtime job
containers and browsers must not receive Artifact Store credentials or connect
to it directly.
