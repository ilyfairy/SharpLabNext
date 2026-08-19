# SharpLab.Runtime

This compatibility package provides the public `.Dump()`, `.Inspect()`,
`Inspect.*`, flow-reporting, and runtime inspection contracts understood by
SharpLabNext runtime instrumentation. Its assembly and package ID remain
`SharpLab.Runtime` for existing SharpLab snippets.

The package targets `netstandard2.1` and `net10.0`; this does not add a .NET
Framework execution profile. SharpLabNext runtime jobs remain isolated Linux
CoreCLR containers.
