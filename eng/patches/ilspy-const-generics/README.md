# ConstGenerics artifact processor patches

The ILSpy patch applies to `ilyfairy/ILSpy` commit
`a2042c704a935a5402c6d700626e52702866ed6d`. The fork commit contains an
untracked-history binary at
`ICSharpCode.Decompiler/Library/System.Reflection.Metadata.dll`; the Docker
build excludes that file while extracting the source archive and applies
`0001-use-source-built-metadata.patch` so the decompiler compiles against the
metadata and immutable assemblies produced from `hez2010/runtime` commit
`79f7f1408b2c811904c983419b45139e654f1e46`. The patch also updates ILSpy's
NuGet lock graph: the incompatible Immutable 9 package is removed, while
`System.Memory 4.5.5` and `System.Runtime.CompilerServices.Unsafe 6.0.0`
remain explicit, exact dependencies because ILSpy itself uses their APIs.

`0002-ilverification-const-type-arguments.patch` applies to that exact runtime
commit. It makes the source-built IL verifier consume `ELEMENT_TYPE_CTARG` and
verify its underlying primitive stack type. The constant value remains part of
the runtime type identity; IL verification only needs the underlying stack
type. The patch also replaces a floating `8.0.0-dev` package dependency with
the exact metadata and immutable reference assemblies from the source-built
runtime profile. `System.Memory` and `System.Runtime.CompilerServices.Unsafe`
remain exact normal package dependencies because the former metadata and
immutable packages supplied them transitively for the `netstandard2.0`
verifier build.
