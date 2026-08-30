# SharpLabNext

[English](README.md) | 简体中文

SharpLabNext 是一个由 Catalog 驱动的 .NET 编译器与运行时工作台。语言服务、编译、
产物处理和执行分别位于独立的 Worker 边界后方，并通过紧凑的桌面端与移动端 Web
界面统一呈现。

仓库中的 [Catalog](profiles/catalog/catalog.json) 是已安装语言、工具链、输出、
reference set、运行时和已批准兼容路径的事实来源。精确的上游版本与源码身份记录在
[profiles/lock.json](profiles/lock.json) 中。

## 主要功能

- 支持 C#、Visual Basic、F#、G#、PeachPie PHP、IL、实验性 x64 C++/CLI 和 J#，
  以及一个 MiniLang SDK 示例。
- 提供 Roslyn Stable、从源码构建的 Roslyn Main，以及原子绑定的实验性 C# 常量泛型
  工具链、运行时和 ILSpy 组合。
- 根据所选 pipeline 的能力提供 Decompiled C#、IL、IL Verify、通过源码构建 JSIL
  生成的 JavaScript、AST、Explain、Run、带源码导航的紧凑型全用户方法 JIT 汇编、
  Execution Flow 和重写后的 Run IL。默认结果为 Decompiled C#。
- 桌面端默认 Monaco，紧凑/移动端默认 CodeMirror；用户可以手动切换，选择会持久化
  在浏览器本地。输入与结果代码共用可持久化的字体大小。
- 桌面端左右分屏，移动端上下分屏。
- LSP 3.17 与实时 operation 控制都使用 WebSocket。安全输出与 Run 使用不同的防抖
  时间；JIT 和 Execution Flow 仍由用户显式触发。
- 按语言保存浏览器本地工作区，支持多文件编辑；语义 token、诊断、补全、Hover、
  Signature Help 和 Code Action 由各语言 capability manifest 决定。
- 使用规范的 SharpLabNext v3 分享格式，并只读导入 SharpLab v1/v2 与旧 Gist。
  GitHub OAuth 和已认证 Gist 写入为可选功能。
- 可生成带精确镜像身份、SBOM、checksum、SLSA provenance、部署脚本和回滚支持的
  签名离线 bundle。

## 已支持语言

| 语言 | 当前工具链 | 说明 |
| --- | --- | --- |
| C# | `roslyn-stable`、`roslyn-stable-netfx48`、`roslyn-main`、`roslyn-const-generics` | 已声明的 Roslyn LSP 能力、AST、Explain、managed PE、.NET Framework 4.8，以及实验性常量泛型 profile。 |
| Visual Basic | `roslyn-stable`、`roslyn-stable-netfx48`、`roslyn-main` | Roslyn LSP、AST、managed PE，以及 .NET 10/.NET 11 Preview/.NET Framework 4.8 路径。 |
| F# | `fsharp-stable` | FSharp.Compiler.Service LSP/Build、AST、源码顺序和 managed PE。 |
| G# | `gsharp-stable`、`gsharp-legacy-0.3.8` | 默认使用 G# 0.3.33，并保留固定的 0.3.8 兼容 profile；两个编译器/LSP profile 共用一个 Worker 镜像，均生成 managed PE/PDB。 |
| PHP | `peachpie-stable` | PeachPie 1.1.13 diagnostics 与 managed PE pipeline；目前不声明完整 PHP LSP 能力。 |
| IL | `mobius-ilasm-stable` | 固定版本 ILSense Core 提供上下文感知的语义语言服务，隔离的 Mobius.ILasm 负责编译为 managed PE。 |
| C++/CLI | `msvc-cppcli-netfx48` | 实验性 x64 MSVC 19.51/`/clr`，生成真实的 .NET Framework 4.8 mixed PE。支持词法编辑、Compile Check、聚焦后的 IL/Decompiled C# 与 Wine Run；不声明 LSP、IL Verify、JIT、instrumentation 或 Execution Flow。 |
| J# | `vjc-jsharp20` | 实验性 Visual J# 2.0 Second Edition 编译，生成 AMD64 CLR 2.0 managed executable。支持词法编辑、Compile Check、IL、Decompiled C# 和专用 Wine/CLR2 Run；不声明 LSP、AST、IL Verify、JIT、instrumentation 或 Execution Flow。 |
| MiniLang | `minilang-stable` | 生成 CIL 的 SDK/conformance 示例，用于演示第三方语言 Worker。 |

当前可以路由的输出包括：

- 所有已安装语言的 Compile Check。
- C#、Visual Basic 和 F# 的 AST；C# 的 Explain。
- MiniLang 的 Generated IL。
- managed assembly 的 IL、Decompiled C# 和 IL Verify。
- 普通 .NET 10/.NET Main managed assembly 通过独立源码构建 JSIL processor 生成的
  JavaScript。
- 兼容 .NET 运行时上的 Run 与紧凑型全用户方法 JIT ASM。
- 兼容标准运行时上的 C#、Visual Basic、F# 和 G# Execution Flow。
- 标准 managed pipeline 的 Rewritten Run IL。

可用性由语言、工具链、reference set、artifact processor、输出和运行时共同解析。
工作台不会展示 Catalog 无法证明兼容的路径。当前运行时包括 .NET 10.0.9、
.NET 11 Preview 5、专用的常量泛型运行时、run-only 的 .NET Framework 4.8/Wine
9.0 profile，以及独立的 x64 CLR 2.0/J# Wine profile。

`roslyn-stable-netfx48` 为 C# 和 Visual Basic 提供可选择的 .NET Framework 4.8
路径。它复用唯一一份锁定的 Roslyn Stable 版本，使用独立校验摘要的
`Microsoft.NETFramework.ReferenceAssemblies.net48` 包编译，发布 IL-only framework
PE，并把 Run 路由到单独的 Wine runtime 容器。

J# 路径只支持 x64，其私有基础镜像由可复用的 CLR 2/3.5 seed 与 Git LFS 中固定的
Visual J# 2.0 Second Edition `vjredist64.exe` 从源码构建。Worker 固定调用
Framework64 `vjc.exe /platform:x64`；用户产物必须是 AMD64 PE32+、IL-only，且不得带
`Requires32Bit` 或 `Prefers32Bit`。编译与 Run 使用两个共享精简层但职责分离的镜像，
并使用独立 win64 prefix。该 Microsoft 二进制使用独立许可，不属于仓库 BSD 许可范围，
也不会进入公共镜像或 bundle。operator 必须接受相应许可，并把最终 release 保持在其
获许可的部署边界内。

## 工作台与传输

两种编辑器共享同一个 workspace store。编辑器类型、字体大小和非当前语言工作区保存
在浏览器 `localStorage` 中，不会写入分享 URL 或 Gist。切换语言时会恢复该语言的
文件与正确后缀，不会把不兼容的工作区直接带入另一种语言。

两种编辑器都使用 `Ctrl+Space` 打开补全。CodeMirror 在补全列表处于活动状态时，
会优先使用 `Tab` 接受候选项，之后才回退到缩进。可用时，高亮使用服务器返回的语义
token；词法 grammar 仅作为 fallback。

`/api/v1/operations/ws` operation WebSocket 承载 selection resolution、Start、
Cancel、State 和可恢复的事件订阅。LSP session 同样使用 WebSocket。大型不可变结果
文档继续通过普通 HTTP 下载。

## 隔离模型

- Gateway 不加载编译器、反编译器、运行时或 Docker SDK 类型。
- Roslyn、F#、G#、PeachPie、IL、C++/CLI 和 J# 的生产 Build 在各自 Toolchain Worker
  内受限的短命子进程中执行。
- JSIL 在独立的非 root Worker 中读取不可变 managed artifact，并为每次转换启动受限
  的短命 Mono 子进程；服务端不会执行生成的 JavaScript。
- Run 与 JIT 只能在 Runtime Supervisor 管理的 Linux 容器中执行；容器无网络、使用
  只读根文件系统、丢弃全部 capability、启用 `no-new-privileges`、
  校验过的 seccomp policy，并限制 CPU、内存、PID 和输出。
- 普通 CoreCLR job 使用非 root 用户。Wine/.NET Framework profile 是明确记录的
  容器内 root 例外，只开放有界的可执行 tmpfs，不访问宿主文件系统、设备或 Docker socket。
- 默认启用运行时容器复用，但仅限同一个 operation WebSocket 内兼容且串行的 Run/JIT。
  WebSocket 断开、浏览器刷新，或语言/工具链/reference/output/runtime/pipeline 发生
  变化时会释放该 generation。HTTP Run/JIT 仍为一次性 create-run-remove。
- 生产 pipeline 接受请求前会校验 artifact、Worker、reference set 和 runtime/JIT
  身份。

## 环境要求

| 用途 | 要求 |
| --- | --- |
| 宿主构建与测试 | `global.json` 选择的 .NET SDK 10.0.301 最低基线，并允许前滚到更新的 .NET 10 feature band |
| 前端 | 系统 Node.js `>=24 <25` 与 npm `>=11 <12` |
| 完整本地环境 | Docker Desktop，或 Docker Engine + Docker Compose v2 |
| Release bundle 构建 | Docker BuildKit 0.13 或更高版本 |
| 生产/离线宿主机 | Linux x64、Docker Engine、Compose v2、OpenSSL、`curl` 和 `sha256sum` |

宿主命令直接使用系统安装的 .NET SDK、Node.js 和 npm。Dockerfile 中的版本只属于
可复现镜像构建输入，不表示要在宿主机重复安装一套工具。

构建只要求经过审核的 `third_party/ILSense` 源码文件已经存在。文件的获取方式不属于
构建流程；普通入口不会读取 Git 元数据、仓库状态，也不会执行 submodule 命令。

## 快速启动

构建与打包入口按职责分开：

| 入口 | 职责 |
| --- | --- |
| `eng/build.ps1` / `eng/build.sh` | 在宿主机 restore、构建前后端并运行静态合同校验，不构建 Docker 镜像。 |
| `eng/build-images.ps1` / `eng/build-images.sh` | 默认构建一个普通本地 Docker 镜像（不需要 Git 元数据、环境开关或 bundle）；传入 `-All`/`--all` 时才构建完整镜像图。 |
| `eng/bundle.ps1` / `eng/bundle.sh` | 只检查并打包已经存在的完整镜像集合，不做 restore 或镜像构建。缺少或身份不匹配时立即失败。 |
| `eng/release.ps1` / `eng/release.sh` | 完整入口：预检输出和所有静态合同、构建并校验全部计划镜像，全部成功后才生成离线 bundle。 |

最简单的本地构建直接执行：

```powershell
.\eng\build-images.ps1
```

命令会把镜像加载到本机 Docker，默认标签为
`sharplabnext/gateway:development`。传入 `-Target <名称>` 可以换成其他独立 Bake 目标；需要
完整发布图时才使用 `-All` 或 `release.ps1`。

完整环境还需要受 Microsoft 许可约束的输入。以下两份原始下载路径不能作为可靠的首次
构建来源，因此精确文件通过 Git LFS 保存：

- `.NET Framework 2.0 x64`：
  `eng/prerequisites/dotnet-framework-2.0/NetFx64.exe`
- `Visual J# 2.0 Second Edition x64`：
  `eng/prerequisites/visual-jsharp-2.0-se-x64/vjredist64.exe`

构建只要求这两份文件的实体字节已经存在；文件可以由任意受控的制品分发方式提供，
不需要 Git 或 Git LFS 命令。若文件仍是 LFS pointer，准备阶段会明确报告文件大小和
SHA-256 不匹配。

Docker 启动前，清单和准备工具会校验两份文件各自的精确大小与 SHA-256。它们只进入各自
的私有 BuildKit context，不会在宿主机执行，也不会以安装器形式进入最终镜像或离线
bundle。`NetFx64.exe` 用于预填 Winetricks 的 `dotnet20` 缓存；`vjredist64.exe` 只在
J# Docker 阶段内安装。其他 .NET Framework 载荷继续通过锁定的 Winetricks/Microsoft
下载路径获取。

`.NET Framework 3.5 SP1`、`4.5.1` 和 `4.7` 安装器仍从清单锁定的 Microsoft HTTPS
地址下载到被忽略的 `artifacts/prerequisites/downloads`，并校验大小和 SHA-256。
安装器不会在 Windows 宿主机启动，也不会写入宿主注册表或系统目录；它们只作为
BuildKit 私有输入，在隔离的 Linux/Wine 构建阶段用静默参数安装到专用 Wine prefix，
临时安装器随后从镜像层删除。3.5 SP1 文件会预填 Winetricks 的精确缓存路径，因此容器
构建不再依赖其旧下载器或 TLS 栈。

完整镜像构建只创建一次 classic WoW64 构建层，然后只构建两份私有 companion seed：
CLR 2 + .NET Framework 3.5，以及 CLR 4 + .NET Framework 4.8。每个精确 Framework
operator 从另一代 CLR 的 seed 开始，只安装自己选择的目标版本；之后仍会预检两份
prefix、禁用对应 NGen 服务、删除安装器残留、记录 seed 镜像 digest，并执行现有的
不可变文件去重。Framework operator 的并发上限继续保持为 2。

构建脚本不会再通过 `docker image save` 额外封存这些 seed。每次构建都会把同一个锁定
构建图交给 BuildKit；Docker 自身复用未变化的镜像层，输入摘要变化则使对应层自然失效。
重装 Docker 或清空全部镜像后，会从本地已提供的输入与经过校验的下载字节重新构建，不依赖任何
预先生成的镜像 TAR。

J# 会从已提供的安装器字节与 CLR2 seed 重建。C++/CLI 会从锁定的 `msvc-wine` revision、
Visual Studio 18.8 manifest 与 .NET Framework 4.8 Developer Pack 重建。源码归档和
Microsoft 输入只作为经过大小/SHA-256 校验的字节下载到被忽略的 prerequisite cache；
解压、准备和真实 `/clr` 预检全部发生在 Docker 内。

J# 和 C++/CLI 基础镜像同样始终交给 BuildKit 构建，不再另存 `private-images.tar`。
普通增量构建由 Docker 层缓存加速；清空 Docker 后则从上述锁定输入重新构建。
`artifacts/prerequisites/downloads` 只缓存校验过的原始下载字节，不保存 Docker 镜像。

在仓库根目录一键构建全部镜像并打包：

```powershell
.\eng\release.ps1 -AcceptMicrosoftLicenses
```

该完整本地入口会构建私有基础镜像，再把实际检查得到的 digest-pinned 引用
注入其余镜像图。因此即使 Git 干净，生成的镜像和 bundle 也会明确标记为 development
image inputs，只能生成可部署的 unsigned 开发产物。正式签名/晋级仍要求通过独立
operator receipt 和 promotion 流程得到的不可变镜像，再由
`bundle.ps1` 单独打包；开发输入授权不会弱化这条边界。

普通构建入口直接根据源码文件计算内容身份，不读取 Git 元数据或工作树状态；没有 `.git`
的导出源码树与 checkout 使用相同流程。生成的本地镜像是普通开发镜像，bundle 也是
unsigned。正式签名/晋级是独立操作，届时才可能要求独立、可验证的 Git provenance。

旧版本的开发开关仍接受，但只为兼容旧脚本保留：

```powershell
.\eng\release.ps1 `
  -AcceptMicrosoftLicenses `
  -AllowUncommittedSourceForDevelopment
```

默认输出目录是 `artifacts/sharplabnext-<release-id>`，且 bundle 输出不可变、不能覆盖。
需要重复生成或指定位置时传入一个尚不存在的目录：

```powershell
.\eng\release.ps1 `
  -AcceptMicrosoftLicenses `
  -OutputDirectory D:\Bundles\SharpLabNext-20260824
```

只重建普通镜像或只打包现有镜像时分别调用 `build-images.ps1` 与 `bundle.ps1`。普通的
`release.ps1`/`release.sh` 会先复用所有身份匹配的本地镜像，再让 BuildKit 只重建发生
变化的输入，因此打包阶段失败后再次执行通常不会全量重建。`-BundleOnly`（或
`--bundle-only`）只是可选的直接打包快捷方式。`-Offline`
只禁止前置缓存联网补齐；从完全空的 BuildKit 缓存构建上游源码仍可能需要访问清单锁定的
Docker、NuGet、npm 或源码地址。

生成的 `.env` 会按正确顺序选择 `compose.prod.yaml` 和
`compose.generated.yaml`，并固定 Compose 项目名。它只包含非敏感默认配置；部署入口会在
每次调用时传入真实的宿主机令牌路径和 Docker socket group。不要修改不可变或已签名
bundle 内的文件。

在 Windows 上测试 unsigned 开发 bundle 时，传入 Git 忽略的本地开发令牌并运行产物
自带的安装器：

```powershell
$bundleRoot = (Resolve-Path .\artifacts\sharplabnext-development).Path
$env:SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE = `
  (Resolve-Path .\deploy\secrets\internal-service-token.dev).Path
& (Join-Path $bundleRoot "install.ps1") `
  -AllowUnsigned `
  -InstallRoot (Join-Path (Resolve-Path .\artifacts) "local-install") `
  -SmokeBaseAddress "http://127.0.0.1:8080"
```

在 Linux 上，每个新传入的 bundle 只需调用一个部署入口；它会加载镜像归档、启动不可变
Compose 集合、检查就绪状态，并在失败时回滚：

```bash
sudo env SHARPLABNEXT_HOME=/opt/sharplabnext \
  sh ./deploy.sh --allow-unsigned
```

正式签名 bundle 应改用 `install.sh` 说明中的信任参数，而不是
`--allow-unsigned`。第一次完整构建会比较耗时，因为需要校验并构建锁定的上游源码树与
reference pack。

生成的 bundle 已包含离线部署所需的签名元数据、安装脚本和回滚脚本。
`deploy/compose.dev.yaml` 适合所有 development tag 都已经存在的环境，但不是干净机器
的 bootstrap 入口。

对已就绪的环境运行外部 smoke：

```powershell
dotnet run eng/smoke/gateway-compose.cs -- http://127.0.0.1:8080 --full
```

在当前部署目录停止环境，但保留 Artifact Store volume：

```powershell
docker compose down --remove-orphans
```

需要保留本地 Artifact Store 数据时，不要添加 `--volumes`。

只迭代前端时，可以把 Vite 指向已部署的后端：

```powershell
$env:SHARPLABNEXT_DEV_API_TARGET = "http://127.0.0.1:8080"
npm --prefix frontend ci
npm --prefix frontend run dev
```

Vite 会在命令输出的本地地址提供前端，并把 HTTP 与 WebSocket API 代理到指定后端。

## 构建与测试

运行维护中的完整验证入口：

```powershell
./eng/test.ps1
```

```bash
./eng/test.sh
```

这两个脚本执行 locked restore、后端 build/test、前端 lint/test/build、schema 与
Compose 校验，以及 Catalog compatibility audit。

已有就绪的 Compose 部署时，可以加入完整 smoke、运行时失败检查，以及桌面/移动端
Playwright 测试：

```powershell
./eng/test.ps1 -SkipBuild -ComposeE2E
```

```bash
./eng/test.sh --skip-build --compose-e2e
```

使用 `SHARPLABNEXT_E2E_BASE_URL` 可以指定非默认部署。也可以单独校验 compatibility
resolver：

```powershell
dotnet run --project src/Tools/SharpLabNext.CompatibilityCli -- validate
```

## 发布与部署

`eng/release.ps1` 与 `eng/release.sh` 会构建完整 Linux 镜像集合并生成离线 bundle；
`eng/bundle.ps1` 与 `eng/bundle.sh` 只负责打包已经构建并验证的镜像集合。
生产 bundle 必须来自干净 Git worktree，使用带外可信签名密钥，并通过身份、安全、
smoke、性能与浏览器门禁。不要单独部署 `deploy/compose.prod.yaml`；生成的 bundle
overlay 会写入生产启动所需的不可变镜像和 Worker 身份。

## 扩展 SharpLabNext

公开 SDK 包包括传输合同、Worker Host、语言/Artifact Worker SDK、conformance tests、
runtime profile schema，以及 `SharpLab.Runtime` 兼容 API。语言集成可以从
[`samples/Languages/SharpLabNext.SampleLanguage.Worker`](samples/Languages/SharpLabNext.SampleLanguage.Worker)
开始；运行时集成可以从
[`samples/Runtimes/dotnet-runtime-template`](samples/Runtimes/dotnet-runtime-template)
开始。

扩展仍需提供 capability manifest、Catalog 条目、获批的 compatibility edge、release
镜像身份和 conformance/security tests，但不需要在 Gateway 中加入编译器专用分派代码。

## 贡献

保持既有服务边界；行为变化时同步 capability/Catalog 合同；运行定向测试以及受影响的
conformance、compatibility、security 和浏览器测试。不得在 Runtime Supervisor 管理的
容器之外执行或 JIT 用户代码。

## 安全

生产部署必须使用外部 secret、生成的不可变 Compose overlay，以及通过带外公钥或
fingerprint 验证的签名 bundle。Gateway 应放在可信反向代理之后，不要暴露内部 Worker
网络，并保留随项目提供的 seccomp/AppArmor 和资源限制。正式发布前，应在非生产环境
验证加固、升级、回滚与事故处理流程。

仓库目前没有公布专用漏洞报告地址。在正式安全策略发布前，请不要在公开 issue 中提交
凭据或可直接利用的漏洞细节；应通过私有渠道联系仓库所有者。

## 许可证

SharpLabNext 自有代码采用 [BSD 2-Clause License](LICENSE)。第三方组件和复制的兼容
数据继续保留其原许可证与 notice；详见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 release 生成的 SBOM。
