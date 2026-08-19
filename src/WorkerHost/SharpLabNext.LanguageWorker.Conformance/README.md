# SharpLabNext Language Worker Conformance

`LanguageWorkerConformanceRunner` performs reusable black-box checks against a running or in-memory Language Worker endpoint. It verifies service health and immutable identity, the capability manifest, Compile Check, a canonical generic artifact envelope, toolchain request isolation, and the minimum LSP lifecycle including diagnostics and completion.

The runner has no dependency on xUnit or a particular web test host. Supply an `HttpClient` and a WebSocket connection delegate, then execute a `LanguageWorkerConformanceScenario` from the test framework used by the worker repository.

See `samples/Languages/SharpLabNext.SampleLanguage.Worker.Tests` for an ASP.NET `WebApplicationFactory` example.
