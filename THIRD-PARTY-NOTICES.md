# Third-Party Notices

SharpLabNext is implemented independently under the BSD 2-Clause License. The
project depends on or interoperates with third-party software whose notices are
collected here and in release SBOM/provenance artifacts.

Initial implementation references:

- SharpLab and MirrorSharp, BSD-2-Clause. Behavior and legacy URL compatibility
  reference; copied compatibility dictionaries retain their original notice.
  SharpLabNext's visible brace mark and browser/PWA icons are original project
  assets rather than copies of SharpLab branding. The SharpLab notice is
  reproduced below for the retained compatibility material.
- Roslyn, MIT.
- Microsoft CLR profiling API sample and public CoreCLR profiling headers,
  MIT, copyright .NET Foundation and contributors. A reduced x64 Linux startup
  profiler is built into .NET runtime-job images to read real IL-to-native JIT
  maps. Sources come from `microsoft/clr-samples` commit
  `5f9a631ecb4f558b7d5e1d17af7d4d93ea836cbc` path
  `ProfilingAPI/ELTProfiler`, and `dotnet/runtime` commit
  `7ee91972e27c086d92b7d223f905cf391578b256` paths
  `src/coreclr/{inc,pal}` and `src/native/minipal`; the retained upstream license is under
  `src/RuntimeJobs/SharpLabNext.JitProfiler.Native`.
- Microsoft.NETFramework.ReferenceAssemblies,
  Microsoft.NETFramework.ReferenceAssemblies.net20, and
  Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3, Microsoft MIT. The
  exact NuGet packages are used only as compile-time API surfaces and test
  fixtures for the independently attested `netfx20-managed-ref` and
  `netfx48-managed-ref` identities; they are not the C++/CLI operator-image
  reference identity and are not used as runtime implementations.
- Microsoft Visual J# 2.0 Second Edition and .NET Framework CLR 2.0 binaries
  are proprietary, operator-supplied inputs for the optional x64-only J#
  worker and Wine runtime. They are not BSD-licensed project content and are
  not included in the source tree or public release images. A self-built
  private bundle may contain them only when the operator has independently
  obtained the software, accepted the applicable Microsoft terms, and is
  authorized to use and transfer that bundle within the intended environment.
- FsAutoComplete, Apache-2.0. Architectural reference only; not currently bundled.
- FSharp.Compiler.Service 43.12.204, MIT.
- FSharp.Core 10.1.204, MIT. Bundled as an exact support assembly in executable F# artifact bundles.
- G# v0.3.33 and legacy v0.3.8, MIT, copyright 2019 David Obando. The compiler
  and language server profiles are built from commits
  `aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d` and
  `723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01`; the complete upstream licenses
  are retained beside each version in the G# worker image and both source
  identities are recorded in release provenance.
- PeachPie 1.1.13, Apache-2.0. `Peachpie.CodeAnalysis`, `Peachpie.Runtime`,
  and `Peachpie.Library` are consumed from exact NuGet packages built from
  commit `608bf30cf3f43f97e32825076a2cfdaa25043e50`. The complete upstream
  `LICENSE.txt` from that commit is retained in the PeachPie worker image.
- Peachpie.Library's Zlib implementation includes ComponentAce code under the
  BSD 3-Clause License. The exact notice from the pinned PeachPie commit is
  retained in the PeachPie worker image and offline bundle.
- JSIL 0.8.2, MIT. The JavaScript artifact worker builds original `sq/JSIL`
  commit `1d57d5427c87ab92ffa3ca4b82429cd7509796ba` from its verified source
  archive. `wherewhere/JSIL` was compared as a requested fork; its additional
  commits change package/CI metadata only, so no fork source is bundled.
- JSIL.Meta, historical ILSpy, NRefactory, and Mono.Cecil used by that JSIL
  build are MIT-licensed and are built from the exact source components in the
  release lock. Their complete upstream license files are retained under
  `/opt/jsil/licenses` in the worker image.
- Mono 6.12.0.182 runtime files used to launch the isolated JSIL compiler are
  from the digest-pinned official Mono image. The copied upstream runtime files
  are MIT-licensed; the complete Debian package copyright records for
  `mono-runtime-sgen` and `mono-runtime-common` are retained under
  `/opt/jsil/licenses` in the worker image.
- `libmono-native` uses the minimal MIT Kerberos shared-library closure from
  the same pinned Mono image. Its complete package copyright record is retained
  as `MIT-Kerberos-copyright.txt`. The dynamically linked `libkeyutils` library
  is LGPL-2.0-or-later; its package copyright and the complete LGPL 2.0 text are
  retained beside the JSIL licenses. No keyutils GPL command-line utility is
  copied into the worker image.
- DEVSENSE PHP Parser 8.5.17973 and DEVSENSE PHP PHAR 1.0.40, Apache-2.0.
  These are compiler dependencies of Peachpie.CodeAnalysis. Their official
  upstream repositories are `DEVSENSE/Parsers` and `DEVSENSE/Devsense.PHP.Phar`.
- Peachpie.Microsoft.CodeAnalysis 3.7.4 and Peachpie.Library.RegularExpressions
  1.7.0, MIT. These are compiler/runtime dependencies of PeachPie. The Roslyn
  fork's packaged third-party notices, including its Apache-2.0 attribution,
  are retained with the worker image.
- BCrypt.Net-Next 4.0.3 and FluentFTP 39.4.0, MIT. These are transitive
  runtime dependencies of Peachpie.Library.
- Isopoh.Cryptography.Argon2, Isopoh.Cryptography.Blake2b, and
  Isopoh.Cryptography.SecureArray 1.1.10, CC-BY-4.0. These are transitive
  cryptography dependencies of Peachpie.Library, copyright Michael Heyman.
  Release bundles retain the package license text and the complete CC-BY-4.0
  legal text; no local modifications are made to these assemblies.
- OpenTelemetry .NET 1.16.0, Apache-2.0. Direct packages:
  `OpenTelemetry.Exporter.OpenTelemetryProtocol`,
  `OpenTelemetry.Extensions.Hosting`,
  `OpenTelemetry.Instrumentation.AspNetCore`,
  `OpenTelemetry.Instrumentation.Http`, and
  `OpenTelemetry.Instrumentation.Runtime`. Transitive packages:
  `OpenTelemetry`, `OpenTelemetry.Api`, and
  `OpenTelemetry.Api.ProviderBuilderExtensions`.
- Mobius.ILasm 0.1.0, MIT, copyright 2021 Konrad Kokosa. The package
  contains IL assembler portions derived from Mono under the MIT license. Its
  upstream license also identifies generated parser output from Mono `mcs/jay`
  under the 4-clause BSD license; release notices and SBOMs must preserve both
  notices.
- ILSense 0.1.0, MIT, copyright 2026 OrgEleCho contributors. The protocol-neutral
  `EleCho.ILSense` core is built from the audited `OrgEleCho/ILSense` submodule at
  commit `a2253dd77d052e02f654908e5ecb60b6602be782`; the demo host is not bundled.
  Its exact source identity is recorded in the release lock and provenance, and
  the complete upstream `LICENSE` is retained in the IL worker image.
- NETStandard.Library 2.0.3 is MIT. Its transitive
  Microsoft.NETCore.Platforms 1.1.0 build-time metadata package is governed by
  the Microsoft .NET Library terms included in that NuGet package and is
  recorded as `LicenseRef-Microsoft-DotNet-Library` in release dependency data.
- System.Security.Permissions 9.0.0 and System.Windows.Extensions 9.0.0, MIT.
  These Microsoft packages are pinned over Mobius.ILasm's older transitive
  dependency for the isolated compiler process.
- ILSpy, MIT.
- Monaco Editor, MIT.
- monaco-languageclient, MIT.
- fflate, MIT.
- lz-string, MIT. Legacy URL importer only.
- Docker.DotNet, MIT.
- Microsoft.Data.Sqlite 10.0.9, MIT.
- SQLitePCLRaw 2.1.11, Apache-2.0.
- SQLite native library bundled by SQLitePCLRaw, public domain.
- JsonSchema.Net 8.0.5, JsonPointer.Net 6.0.1, Json.More.Net 2.2.0, and
  Humanizer.Core 3.0.1, MIT. Used by the release performance gate to validate
  machine-readable reports against the checked-in JSON Schema.
- Moby default seccomp profile selected by Moby v28.5.2, Apache-2.0. Moby
  commit `89c5e8fd66634b6128fc4c0e6f1236e2540e46e0` pins
  `github.com/moby/profiles/seccomp` v0.1.0 at commit
  `c936cc7b4074219137bc0bee45670f5e4618d462`. The exact profile is vendored as
  the versioned runtime-job syscall policy and its SHA-256 is verified by the
  Runtime Supervisor before use. Its reviewed inventory, redistribution
  notice, and complete Apache-2.0 text are under `deploy/security` and are
  retained in every offline bundle.

OpenTelemetry .NET packages that include `THIRD-PARTY-NOTICES.TXT` carry the
following upstream attributions:

- .NET runtime and libraries: Copyright (c) .NET Foundation and Contributors,
  licensed under the MIT License.
- gRPC for .NET: Copyright 2019 The gRPC Authors, licensed under Apache-2.0.

The complete Apache-2.0 license text retained under `deploy/security/licenses`
is included in every offline bundle. The release dependency inventory and SBOM
record the exact OpenTelemetry packages, versions, integrity hashes, and
license expressions.

SharpScript is GPL-3.0 and is used only as a behavioral research reference. No
SharpScript source is copied or translated into SharpLabNext.

Exact versions, source commits, integrity hashes, and complete license texts
must be generated for every approved offline release.

## SharpLab BSD 2-Clause notice

Copyright (c) 2016-2017, Andrey Shchekin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
