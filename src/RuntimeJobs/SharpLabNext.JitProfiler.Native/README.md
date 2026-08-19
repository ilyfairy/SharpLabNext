# SharpLabNext JIT mapping profiler

This x64 Linux startup profiler subscribes only to CoreCLR JIT compilation
callbacks. For the one user module named by `SHARPLABNEXT_JIT_MAP_MODULE`, it
writes the runtime's real `GetILToNativeMapping3` ranges to the private file in
`SHARPLABNEXT_JIT_MAP_PATH`.

The profiler does not use diagnostics IPC, ptrace, a debugger, or a process
snapshot. Runtime job containers enable only the profiler diagnostics channel;
diagnostics IPC and debugger attachment remain disabled. A missing or invalid
map is non-fatal and the managed inspector falls back to method-level source
association.

For ordinary methods the inspector selects records by the prepared method
handle and native version. Constructed generic `FunctionID` values can differ
from managed `RuntimeMethodHandle.Value`; in that case a MethodDef token is
accepted only when it has exactly one candidate in the bounded map. Ambiguous
generic instantiations fall back to method-level mapping.

The profiling API scaffold is derived from
`microsoft/clr-samples@5f9a631ecb4f558b7d5e1d17af7d4d93ea836cbc`, path
`ProfilingAPI/ELTProfiler`. The public profiling/PAL headers are copied from
`dotnet/runtime@7ee91972e27c086d92b7d223f905cf391578b256`, paths
`src/coreclr/{inc,pal}` and `src/native/minipal`. Both upstreams are MIT
licensed; see `LICENSE.Microsoft.txt`.

The vendored include closure is about 2.1 MiB, of which the generated
`corprof.h` ABI declaration is about 1.2 MiB. Keeping the compiler-resolved
public header closure avoids hand-maintaining COM vtable slots or PAL type
layouts, where a seemingly harmless trim can silently call the wrong profiler
method. The built x64 shared object is about 52 KiB. Runtime images receive the
shared object and `LICENSE.Microsoft.txt`, but not the vendored header closure.
