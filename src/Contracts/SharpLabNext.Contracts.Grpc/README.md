# SharpLabNext Worker Protocol Contracts

This package contains the versioned service interfaces and worker protocol
negotiation helpers shared by worker hosts and clients. The interfaces are
transport contracts; they do not grant a worker access to Gateway or Docker.

Third-party language and artifact workers normally consume the higher-level
worker SDK packages, which bring this package through an exact-version
dependency.
