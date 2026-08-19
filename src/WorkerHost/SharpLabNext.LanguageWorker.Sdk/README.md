# SharpLabNext Language Worker SDK

This package hosts third-party managed-language workers without taking a dependency on Gateway, Catalog, PipelineResolver, a compiler implementation, or a runtime implementation.

Implement `ILanguageWorkerBuildService` for immutable Build requests and, when editing support is available, `ILanguageWorkerSessionService` for the LSP WebSocket lifecycle. Register both implementations and immutable image/process metadata with `AddSharpLabNextLanguageWorker<TBuildService, TSessionService>()`, then call `MapSharpLabNextLanguageWorker()`.

The SDK provides:

- service identity, health, capability manifest, Build, and LSP session endpoints;
- deadline, cancellation, toolchain, language, reference-set, workspace-size, and capability checks;
- strict capability manifest loading and the packaged JSON Schema;
- typed reference-set attestations in `WorkerDescriptor`, loaded and verified with
  `ReferenceSetAttestationReader` from the WorkerHost package;
- `LanguageArtifactBuilder` for canonical generic artifact envelopes such as `cil-text-v1`.

Pass the verified attestations to `LanguageWorkerHostMetadata.Create`. Existing
source integrations remain compatible, but a Gateway configured with release-lock
reference identities rejects Build and LSP requests to workers that omit or
mismatch them. Use `SharpLabNext.LanguageWorker.Conformance` in the worker's
endpoint tests before publishing a worker image.
