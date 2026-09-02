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
- 运行时覆盖 .NET Core 2.0 到 .NET 11 Preview、Wine 下的 Windows .NET 5-11、Wine
  下从 2.0 到 4.8 的全部 .NET Framework 版本、Mono 6.12、J# CLR 2.0 和常量泛型
  runtime。
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
- 可生成带精确镜像身份、SBOM、checksum 和 SLSA provenance 的签名离线 bundle。

## 支持的语言与运行时

这里列出当前 Catalog 实际提供的精确版本。新旧运行时都是 playground 有意保留的正式
能力，用于比较行为、验证兼容性和测试回归，不会因为版本较旧而降低支持等级。更新
[profiles/lock.json](profiles/lock.json) 后，精确补丁版本也会随之变化。

当前范围从 .NET Core 2.0 和 .NET Framework 2.0 开始，到 .NET 11 Preview 和
.NET Framework 4.8 为止。当前 Catalog 尚未定义 .NET Core/.NET Framework 1.x 或
.NET Framework 4.8.1 profile。

### 语言与工具链

| 语言 | 工具链 | 当前版本与范围 |
| --- | --- | --- |
| C# | `roslyn-stable`、`roslyn-main`、`roslyn-stable-netfx48`、`roslyn-const-generics` | Roslyn Stable 5.6.0、Roslyn Main 5.10.0（`708c0a9669c6`）、下列全部 .NET/.NET Framework reference set，以及原子绑定的实验性常量泛型 profile。 |
| Visual Basic | `roslyn-stable`、`roslyn-main`、`roslyn-stable-netfx48` | Roslyn Stable/Main LSP、AST，以及面向下列 .NET 和 .NET Framework reference set 的 managed PE。 |
| F# | `fsharp-stable` | FSharp.Compiler.Service 43.12.204、LSP/Build、AST、源码顺序与 managed PE。 |
| G# | `gsharp-stable`、`gsharp-legacy-0.3.8` | 默认 G# 0.3.33，并保留固定的 0.3.8 兼容 profile；两者均生成 managed PE/PDB。 |
| PHP | `peachpie-stable` | PeachPie 1.1.13 diagnostics 与 managed PE；目前不声明完整 PHP LSP 能力。 |
| IL | `mobius-ilasm-stable` | ILSense 0.1.0 语义服务和隔离的 Mobius.ILasm 0.1.0 managed PE 编译。 |
| C++/CLI | `msvc-cppcli-netfx48` | 实验性 x64 MSVC 19.51 `/clr`，生成真实 .NET Framework 4.8 mixed PE；支持 Compile Check、聚焦后的 IL/Decompiled C# 与 Wine Run。 |
| J# | `vjc-jsharp20` | Visual J# 2.0 Second Edition（2.0.50727.937），生成 AMD64 CLR 2.0 managed executable，提供聚焦后的 IL/Decompiled C# 和专用 Wine Run。 |
| MiniLang | `minilang-stable` | 1.0.0 SDK/conformance 示例，输出 CIL。 |

### 原生 .NET 运行时 - Linux x64

下列每一行都已安装且健康；每个标准 .NET 版本也有对应的编译 reference set。

| 运行时 | 精确版本 | 运行时能力 |
| --- | --- | --- |
| .NET Core 2.0 | 2.0.9 | Run |
| .NET Core 2.1 | 2.1.30 | Run |
| .NET Core 2.2 | 2.2.8 | Run |
| .NET Core 3.0 | 3.0.3 | Run |
| .NET Core 3.1 | 3.1.32 | Run |
| .NET 5 | 5.0.17 | Run |
| .NET 6 | 6.0.36 | Run、JIT ASM |
| .NET 7 | 7.0.20 | Run、JIT ASM |
| .NET 8 | 8.0.29 | Run、JIT ASM |
| .NET 9 | 9.0.18 | Run、JIT ASM |
| .NET 10 | 10.0.10 | Run、JIT ASM、Inspection、Execution Flow |
| .NET 11 Preview | 11.0.0-preview.6.26359.118 | Run、JIT ASM、Inspection、Execution Flow |

### Wine 9.0 下的 .NET 运行时 - Linux x64

这些 profile 在 Wine 中运行 Windows x64 runtime。.NET Core 2.x 和 3.x 的
Windows/Wine 定义作为历史版本保留，但目前没有安装，也不会出现在可选择列表中；对应的
原生 Linux runtime 仍可用。

| 运行时 | 精确版本 | 运行时能力 |
| --- | --- | --- |
| .NET 5 / Wine | 5.0.17 | Run |
| .NET 6 / Wine | 6.0.36 | Run |
| .NET 7 / Wine | 7.0.20 | Run、JIT ASM |
| .NET 8 / Wine | 8.0.29 | Run、JIT ASM |
| .NET 9 / Wine | 9.0.18 | Run、JIT ASM |
| .NET 10 / Wine | 10.0.10 | Run、JIT ASM |
| .NET 11 Preview / Wine | 11.0.0-preview.6.26359.118 | Run、JIT ASM |

### Wine 9.0 下的 .NET Framework - Linux x64

C# 和 Visual Basic 为表中的每个版本提供对应的 managed reference set。C++/CLI 仅支持
.NET Framework 4.8；J# 使用下方单独列出的 CLR 2.0 runtime。

| .NET Framework | CLR 代际 | 运行时能力 |
| --- | --- | --- |
| 2.0 | CLR 2 | Run、JIT ASM |
| 3.0 | CLR 2 | Run、JIT ASM |
| 3.5 | CLR 2 | Run、JIT ASM |
| 4.0 | CLR 4 | Run、JIT ASM |
| 4.5 | CLR 4 | Run、JIT ASM |
| 4.5.1 | CLR 4 | Run、JIT ASM |
| 4.5.2 | CLR 4 | Run、JIT ASM |
| 4.6 | CLR 4 | Run、JIT ASM |
| 4.6.1 | CLR 4 | Run、JIT ASM |
| 4.6.2 | CLR 4 | Run、JIT ASM |
| 4.7 | CLR 4 | Run、JIT ASM |
| 4.7.1 | CLR 4 | Run、JIT ASM |
| 4.7.2 | CLR 4 | Run、JIT ASM |
| 4.8 | CLR 4 | Run、JIT ASM |

### 其他运行时

| 运行时 | 版本 | 能力 | 说明 |
| --- | --- | --- | --- |
| Mono / Linux x64 | 6.12.0.182 | Run、JIT ASM | 使用 .NET Framework 4.8 managed reference set。 |
| Const Generics Runtime | 锁定的原子 profile | Run、JIT ASM、Inspection | 必须与常量泛型编译器、reference set 和 artifact processor 匹配。 |
| Visual J# / CLR 2.0 / Wine 9.0 | J# 2.0.50727.937 | Run | 专用 x64 J# runtime，不等同于通用 .NET Framework 2.0 profile。 |

### 输出与兼容性

当前可路由输出包括：所有已安装语言的 Compile Check；C#、Visual Basic 和 F# 的 AST；
C# 的 Explain；MiniLang 的 Generated IL；兼容 managed assembly 的 IL、Decompiled C#
和 IL Verify；通过源码构建 JSIL processor 生成的 JavaScript；Run；紧凑型全用户方法
JIT ASM；Execution Flow；以及 pipeline 声明所需能力时的 Rewritten Run IL。默认输出为
Decompiled C#。

可用性由完整的语言、工具链、reference set、artifact processor、输出和运行时选择共同
解析。运行时具备某项能力，不代表每个 producer 都能使用它。例如 Framework 4.8 runtime
可以为兼容 managed artifact 提供 JIT ASM，但 C++/CLI 本身不声明 JIT ASM 或
instrumentation。

`roslyn-stable-netfx48` 复用唯一锁定的 Roslyn Stable 版本，让 C# 和 Visual Basic 针对
2.0 到 4.8 的独立校验 Framework reference assembly 编译，并输出 IL-only framework
PE；Run/JIT 由单独的 Wine runtime 容器负责。

J# 路径只支持 x64，固定调用 Framework64 `vjc.exe /platform:x64`；用户程序集必须是
AMD64 PE32+、IL-only，且不能带 32-bit-required/preferred 标志。Visual J#、.NET
Framework 安装器和 MSVC/C++ build 资产使用独立许可，不属于本仓库 BSD 许可范围。
接受许可后生成的私有 bundle 并不会自动获得公开再分发权；把 bundle 或镜像发布到
GitHub Release 前，必须逐项确认适用许可。

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

构建只要求经过审核的 `third_party/ILSense` 源码文件已经存在。

## 快速启动

### 构建入口

构建与打包入口按职责分开：

| 入口 | 职责 |
| --- | --- |
| `eng/build.ps1` / `eng/build.sh` | 在宿主机 restore、构建前后端并运行静态合同校验，不构建 Docker 镜像。 |
| `eng/build-images.ps1` / `eng/build-images.sh` | 默认构建一个普通本地 Docker 镜像；传入 `-Target <名称>` 构建独立目标，传入 `-All`/`--all` 构建完整镜像图。 |
| `eng/bundle.ps1` / `eng/bundle.sh` | 只检查并打包已经存在的完整镜像集合，不做 restore 或镜像构建。缺少或身份不匹配时立即失败。 |
| `eng/release.ps1` / `eng/release.sh` | 完整入口：预检输出和所有静态合同、构建并校验全部计划镜像，全部成功后才生成离线 bundle。 |

### 构建单个镜像

最简单的本地构建直接执行：

```powershell
.\eng\build-images.ps1
```

命令会把镜像加载到本机 Docker，并使用当前 lock 的 release ID 作为标签。传入
`-Target <名称>` 可以换成其他独立 Bake 目标；需要完整发布图时才使用 `-All` 或
`release.ps1`。

### 需要许可的构建输入

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

完整镜像构建只创建一次 classic WoW64 构建层，然后只构建两份 companion seed：
CLR 2 + .NET Framework 3.5，以及 CLR 4 + .NET Framework 4.8。每个精确 Framework
operator 从另一代 CLR 的 seed 开始，只安装自己选择的目标版本；之后仍会预检两份
prefix、禁用对应 NGen 服务、删除安装器残留、记录 seed 镜像 digest，并执行现有的
不可变文件去重。Framework operator 使用与整个 release 图相同的
`--max-parallel`，默认并发数为 5。

J# 会从已提供的安装器字节与 CLR2 seed 重建。C++/CLI 会从锁定的 `msvc-wine` revision、
Visual Studio 18.8 manifest 与 .NET Framework 4.8 Developer Pack 重建。源码归档和
Microsoft 输入只作为经过大小/SHA-256 校验的字节下载到被忽略的 prerequisite cache；
解压、准备和真实 `/clr` 预检全部发生在 Docker 内。

Framework seed 和 J#/C++/CLI 基础镜像会在同一个构建图中直接交给 BuildKit。Docker 会复用
未变化的镜像层，只让 Dockerfile、上下文或构建参数发生变化的阶段失效。prerequisite cache
只保存校验过的源码和下载字节。

### 构建完整 Bundle

在仓库根目录一键构建全部镜像并打包：

```powershell
.\eng\release.ps1 -AcceptMicrosoftLicenses
```

该入口运行主机构建检查，构建并校验完整 Docker 图，然后生成一个可直接部署的 bundle。
普通命令默认生成 unsigned bundle；正式签名仍要求源码和镜像输入具备可独立验证的来源。

默认输出目录是 `artifacts/releases/sharplabnext-yyyy-MM-dd-HH-mm-ss`。每个时间戳子目录
都是完整部署单元，可以直接复制到生产主机，也可以改成 GitHub Release 名称后压缩为 ZIP。
bundle 不会覆盖已有目录；需要指定发布名时传入一个尚不存在的目录：

```powershell
.\eng\release.ps1 `
  -AcceptMicrosoftLicenses `
  -OutputDirectory D:\Bundles\sharplabnext-2026-08-24
```

### Docker 构建缓存

`build-images.ps1` 默认构建一个本地目标；传入 `-Target` 构建独立镜像，传入 `-All` 构建
完整镜像图。`release.ps1`/`release.sh` 负责构建并打包完整镜像图。Docker/BuildKit 会复用
未变化的镜像层。`-BundleOnly`（或 `--bundle-only`）只打包已经存在的镜像，`-Offline` 只使用
本地已有的前置构建输入。

### 部署 Bundle

生成的 `.env` 会自动组合 `compose.prod.yaml` 与 `compose.generated.yaml`，并固定
Compose 项目名。所有 bundle 都在 `secrets/internal-service-token` 中包含同一个可编辑的
默认令牌，不需要按环境执行初始化；需要自定义时直接修改该文件。Runtime Supervisor
需要访问宿主 Docker socket 时，在 `.env` 中将 `DOCKER_GID` 设为该 socket 的 group ID。

在 bundle 目录中加载随附镜像并启动 Compose：

```powershell
docker load -i images.tar
docker compose up -d
```

默认访问地址为 `http://127.0.0.1:8080/`。使用 `docker compose port gateway 8080`
可以查看实际映射到宿主机的端口。Docker Compose 会让当前 shell 的环境变量覆盖 bundle
中的 `.env`；复用旧部署使用过的 shell 时，应先删除过期的 `SHARPLABNEXT_*` 覆盖，或
明确更新为本次部署需要的值，再创建容器。

所有支持的宿主机都使用相同命令。第一次完整构建会比较耗时，因为需要校验并构建锁定的
上游源码树与 reference pack。

在当前部署目录停止环境，但保留 Artifact Store volume：

```powershell
docker compose down --remove-orphans
```

需要保留本地 Artifact Store 数据时，不要添加 `--volumes`。

### 前端开发

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
普通 unsigned bundle 可以直接部署。正式签名 release 还需要可验证的源码与镜像来源、
带外可信签名密钥，以及身份、安全、smoke、性能和浏览器门禁。不要单独部署
`deploy/compose.prod.yaml`；生成的 bundle overlay 会写入启动所需的不可变镜像与
Worker 身份。

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

每个 bundle 都包含一份可编辑的默认内部服务令牌，便于直接启动；将部署暴露给其他机器
或不可信网络前必须替换它。GitHub OAuth 在配置外部 client secret 前保持禁用。Gateway
应放在可信反向代理之后，不要暴露内部 Worker 网络，并保留项目提供的 seccomp/AppArmor
与资源限制。正式签名 release 应通过带外公钥或 fingerprint 验证。

仓库目前没有公布专用漏洞报告地址。在正式安全策略发布前，请不要在公开 issue 中提交
凭据或可直接利用的漏洞细节；应通过私有渠道联系仓库所有者。

## 许可证

SharpLabNext 自有代码采用 [BSD 2-Clause License](LICENSE)。第三方组件和复制的兼容
数据继续保留其原许可证与 notice；详见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 release 生成的 SBOM。
