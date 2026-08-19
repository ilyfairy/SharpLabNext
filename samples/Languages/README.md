# Language worker samples

`SharpLabNext.SampleLanguage.Worker` is the minimal third-party language example for the public Language Worker SDK.

- Language ID: `minilang`
- Toolchain and worker ID: `minilang-stable`
- Default source file: `Program.mini`
- Syntax: one `print "text"` statement per non-empty line
- Produced artifact: `cil-text-v1` containing `Program.il`
- Build targets: `compile-check` and `artifact`
- Editing surface: LSP 3.17 initialize, full-document synchronization, push diagnostics, and completion

The worker does not reference Gateway, Catalog, PipelineResolver, a runtime implementation, or an IL assembler. A deployment connects its `cil-text-v1` artifact to an approved IL assembler through compatibility data, then the resulting managed PE can use the normal IL, decompile, Run, and JIT pipeline.

Run its black-box SDK conformance tests with:

```powershell
dotnet restore samples/Languages/SharpLabNext.SampleLanguage.Worker.Tests/SharpLabNext.SampleLanguage.Worker.Tests.csproj --locked-mode
dotnet test samples/Languages/SharpLabNext.SampleLanguage.Worker.Tests/SharpLabNext.SampleLanguage.Worker.Tests.csproj -c Release --no-restore
```
