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

J# 路径只支持 x64，并依赖单独准备、按摘要锁定的 operator 镜像，其中包含已获许可的
Visual J# 2.0 Second Edition 与 CLR 2.0 资产。Worker 固定调用 Framework64
`vjc.exe /platform:x64`；用户产物必须是 AMD64 PE32+、IL-only，且不得带
`Requires32Bit` 或 `Prefers32Bit`。编译与 Run 使用两个共享精简层但职责分离的镜像，
并使用独立 win64 prefix。微软二进制、安装器路径和凭据不会进入 BSD 源码树，也不会
发布为公共镜像。operator 必须自行取得安装器、接受对应许可，使用
`eng/prepare-jsharp-toolchain.cs` 构建私有前置镜像，并把生成的 release 保持在获许可的
部署边界内。

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

克隆时应使用 `--recurse-submodules`。已有 checkout 在 restore 或 build 前需要把经过
审计的 ILSense 源码初始化到 gitlink 固定的精确提交：

```powershell
git submodule update --init --recursive
```

构建和发布流程只读取固定提交的 `third_party/ILSense` 子模块，不读取同级目录或浮动
分支 checkout。

## 快速启动

完整环境包含从源码构建的 Roslyn Main、ConstGenerics、G#、PeachPie，以及 operator
自行构建的 x64 J# 镜像。先准备私有 J# 前置镜像，再把 unsigned development bundle
生成到一个新目录并安装到本机：

```powershell
$repositoryRoot = (Resolve-Path .).Path
$bundleRoot = Join-Path $repositoryRoot "artifacts/bundles/local-$(Get-Date -Format yyyyMMdd-HHmmss)"

./eng/bundle.ps1 `
  -OutputDirectory $bundleRoot `
  -AllowUncommittedSourceForDevelopment

$env:SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE = `
  (Resolve-Path ./deploy/secrets/internal-service-token.dev).Path
$env:SHARPLABNEXT_BIND_ADDRESS = "127.0.0.1"
$env:SHARPLABNEXT_HTTP_PORT = "8080"

& (Join-Path $bundleRoot "install.ps1") `
  -AllowUnsigned `
  -InstallRoot (Join-Path $repositoryRoot "artifacts/local-install") `
  -SmokeBaseAddress "http://127.0.0.1:8080"
```

打开 <http://127.0.0.1:8080>。第一次完整构建会比较耗时，因为需要校验并构建锁定
的上游源码树与 reference pack。每次重建应使用新的空 bundle 目录；bundle 输出目录
是不可变的。

Linux 使用对应的 `eng/bundle.sh` 和生成的 `install.sh`。安装前传入 Docker socket
group 和相同的宿主设置：

```bash
export DOCKER_GID="$(stat -c '%g' /var/run/docker.sock)"
export SHARPLABNEXT_BIND_ADDRESS=127.0.0.1
export SHARPLABNEXT_HTTP_PORT=8080
```

生成的 bundle 已包含离线部署所需的签名元数据、安装脚本和回滚脚本。
`deploy/compose.dev.yaml` 适合所有 development tag 都已经存在的环境，但不是干净机器
的 bootstrap 入口。

对已就绪的环境运行外部 smoke：

```powershell
dotnet run eng/smoke/gateway-compose.cs -- http://127.0.0.1:8080 --full
```

停止环境，但保留 Artifact Store volume：

```powershell
docker compose --project-name sharplabnext `
  -f (Join-Path $bundleRoot "compose.prod.yaml") `
  -f (Join-Path $bundleRoot "compose.generated.yaml") `
  down --remove-orphans
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

`eng/bundle.ps1` 与 `eng/bundle.sh` 会构建完整 Linux 镜像集合并生成离线 bundle。
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
