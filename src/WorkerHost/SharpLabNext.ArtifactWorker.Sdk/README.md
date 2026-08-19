# SharpLabNext Artifact Worker SDK

This package hosts isolated artifact processors behind the SharpLabNext worker
operation contract. A worker declares an `artifact-worker.json` capability
manifest, registers transform/render/verification handlers, and maps the
standard asynchronous endpoints.

```csharp
services.AddSingleton<MyTransform>();
services.AddSingleton<IArtifactTransformHandler>(sp => sp.GetRequiredService<MyTransform>());
services.AddSharpLabNextArtifactWorker(identity, workerImageId, manifest);

app.MapSharpLabNextArtifactWorker();
```

Handlers return typed operation results and optional produced content/artifact
events. The SDK enforces processor identity, request deadlines, idempotency,
bounded concurrency, retained-operation limits, cancellation, capability
registration, health reporting, and public worker-error mapping. Parsing or
executing untrusted artifacts remains the responsibility of a short-lived,
resource-limited processor process; it must not occur inside Gateway.
