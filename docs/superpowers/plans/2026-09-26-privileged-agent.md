# 特权 Agent 架构 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 macOS / Linux 用户不必 `sudo` 跑整个 GUI 也能使用「全局伪造」，改为启动时弹一次系统授权框拉起 root 身份的常驻 agent，之后所有 start/stop/reload 都不再弹框。

**Architecture:** 新增两个 .NET 项目。`Cealing-Core` 放 GUI 与 agent 共享的协议与配置模型（无依赖）。`Cealing-Agent` 是无 GUI 的控制台进程，以 root 身份常驻，承载全部特权动作：写 `/etc/hosts`、安装/卸载根证书、监听 80/443、跑 mihomo TUN。GUI 通过 Unix domain socket 用单行 JSON 与它通信。`Utils/BuiltinNginx.cs`（内置 .NET 引擎）整体搬进 agent，因此内置引擎**不再需要 nginx**。

**Tech Stack:** .NET 8 / C# 12、Avalonia 11.2.8、Kestrel（`Microsoft.AspNetCore.App`）、`System.Net.Sockets.UnixDomainSocketEndPoint`、xunit、macOS `osascript ... with administrator privileges`、Linux `pkexec`

---

## 范围边界（重要，先读）

**v1 覆盖 macOS + Linux。Windows 不动。** Windows 上 `MainConst.IsAdmin`（UAC）本来就是原生模型，用户已有「以管理员身份运行」的习惯，重构收益为零、命名管道改造成本高。所以：

- `Consts/MainConst.cs:17` 的 `IsAdmin` **保留**，仍用于 `Wins/MainWin.axaml.cs:237` 的 `--no-sandbox` 判断
- Windows 继续走现有的进程内路径（本计划的所有新代码在 Windows 上不被调用）
- 新增 `Cealing-Core` / `Cealing-Agent` 只在 `!OperatingSystem.IsWindows()` 时被 GUI 触达

**明确不做（v2 再议）：**

| 项 | 原因 |
|---|---|
| `launchd LaunchDaemon` / systemd unit | 免掉后续所有弹框，但要提供 root 级安装/卸载流程，对本工具偏重。v1 接受「agent 挂了要重输一次密码」 |
| 内置引擎在 Linux 上原地提权 | Linux 可用 `net.ipv4.ip_unprivileged_port_start=0` 让普通用户绑低位端口，但那要求 root 设一次 sysctl，与本方案收益重叠，不做 |
| 把配置/日志迁到 `~/Library/Application Support/` | 当前应用目录可写（同 `bin/Release/net8.0`）。仅当用户装到 `/Applications` 时才需要，届时再处理 |

**已知的既有阻塞（非本计划引入）：** 工作区存在未完成的主题重构 `Themes/Button.axaml`，导致 `dotnet build Sheas-Cealer-Nix.csproj` 报 **64 个 XAML 错误**（24 个 `AVLN2000` + 40 个 `AVLN2200`，Task 0 实测），且 GUI 项目产不出 DLL。`Cealing-Core` / `Cealing-Agent` / 测试项目不引用 Avalonia XAML，可以正常构建。**不要**去修 `Themes/`，那是别人在做的事。

---

## 环境事实（已验证）

- `dotnet` 不在 PATH 里，在 `/usr/local/share/dotnet/dotnet`，SDK 只有 `10.0.401`
- 因此**每条命令前都要** `export PATH="/usr/local/share/dotnet:$PATH"`
- `Sheas-Cealer-Nix.sln` **无法构建**：`../Ona-Core/` 与 `../Sheas-Core/` 只有 `bin/` 没有 `.csproj`（只有编译好的 DLL）。GUI 只能靠 `HintPath` 引用 DLL，所以**一律用 `dotnet build <具体项目>.csproj`，不要用 sln**
- 仓库根目录是 git 仓库，目前只有一个 `init` 提交

---

## File Structure

```
Cealing-Core/                              # 新增：共享契约，零依赖
  Cealing-Core.csproj
  AgentPaths.cs                            # socket / 配置文件 / 日志路径
  AgentJson.cs                             # 共享 JsonSerializerOptions
  Protocol/AgentCommand.cs                 # 命令名常量
  Protocol/AgentRequest.cs
  Protocol/AgentResponse.cs
  Protocol/AgentStatus.cs
  ProxyConfig.cs                           # GUI → agent 的启动配置
  ProxyRule.cs
  ProxyCertSan.cs
  ProxyEngineKind.cs

Cealing-Agent/                             # 新增：root 常驻控制台进程
  Cealing-Agent.csproj
  Program.cs                               # --socket --config --app-dir --owner-uid
  AgentServer.cs                           # socket 监听 + 请求分发 + ShutdownToken
  AgentLog.cs                              # root 侧日志
  HostsConf.cs                             # /etc/hosts 标记块读写
  TrustStore.cs                            # 跨平台根证书装卸
  IProxyEngine.cs
  EngineSupervisor.cs                      # 按 ProxyConfig 选引擎并启停
  EngineAdapters.cs                        # Builtin/ExternalNginx/Mihomo 三个 adapter
  ProxyCertificateFactory.cs               # 生成 root+child 证书并落盘 PEM
  Engines/BuiltinEngineRule.cs
  Engines/BuiltinEngine.cs                 # 从 Utils/BuiltinNginx.cs 搬，Kestrel 版
  Engines/ExternalNginxEngine.cs
  Engines/MihomoEngine.cs

Sheas-Cealer-Nix.Tests/                    # 新增：xunit
  Sheas-Cealer-Nix.Tests.csproj
  SmokeTests.cs
  AgentPathsTests.cs
  ProxyConfigSerializationTests.cs
  HostsConfTests.cs
  TrustStoreCommandTests.cs
  AgentServerTests.cs
  PrivilegeEscalatorCommandTests.cs
  ProxyConfigBuilderTests.cs

Sheas-Cealer-Nix/                          # 修改
  Utils/PrivilegeEscalator.cs              # 新增：osascript / pkexec 提权拉起
  Utils/AgentClient.cs                     # 新增：GUI 侧 socket 客户端
  Utils/NginxCleaner.cs                    # 重写：unix 转发 agent，Windows 原实现原样保留
  Utils/BuiltinNginx.cs                    # 删除（搬到 Cealing-Agent/Engines）
  Proces/NginxProc.cs / ConginxProc.cs / MihomoProc.cs / ComihomoProc.cs  # 删除
  Consts/MainConst.cs                      # 新增 AgentBinaryPath / ProxyConfigPath / AgentLogPath / AgentLaunchError
  Preses/MainPres.cs                       # 新增 IsAgentReady / IsAgentStarting / AgentError / IsProxyRunning / ProxyEngineName
  Props/Settings.settings + Designer.cs    # 新增 PromptAgentOnStartup
  Wins/MainWin.axaml.cs                    # 改造：移除进程内引擎与 Process.Start，走 agent
  Wins/MainWin.axaml                       # 改绑定：按钮常显、列宽/窗宽改常量
  Sheas-Cealer-Nix.csproj                  # 引用 Cealing-Core + InternalsVisibleTo

docs/superpowers/plans/2026-09-26-privileged-agent.md   # 本计划
```

**为什么不改 converter：** `MainWinWidthConv` / `MainProxyColumnWidthConv` / `MainAdminControlVisibilityConv` 都**保留文件、保留注册**，只是不再被 XAML 引用 —— 窗口宽度写死 `708`、列宽写死 `*`、可见性写死 `True`。动 converter 公开面不值当。

**为什么 `Cealing-Core` 要独立：** agent 不能引用 GUI 项目（会把 Avalonia 拖进来），但两边都要用同一套协议类型。`Sheas-Cealer-Nix.csproj:75` 已经有 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`，所以 agent 项目自己也要加同样的引用。

---

## Task 0: 建立可用的验证基线

**Files:**
- 无（只跑命令确认现状）

- [x] **Step 1: 确认 dotnet 可用**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet --version
```

Expected: 输出 `10.0.401`（或更高的 10.x）

**实际：`10.0.401` ✓**

- [x] **Step 2: 记录 GUI 项目的既有失败基线**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build Sheas-Cealer-Nix.csproj -c Release 2>&1 | grep -oE "AVLN[0-9]+" | sort | uniq -c
```

Expected: 输出 `10` 个 `AVLN3000`。**实际基线与计划不符，已按实测修正：**

```
  24 AVLN2000
  40 AVLN2200
```

共 **64 个错误**，全部来自 `Themes/Button.axaml` 的 `BoxShadow` 无法解析，**C# 层 0 个 `CS` 错误**。两个后果要记住：

1. 后续验证命令一律用 `grep -oE "AVLN[0-9]+"`，不要用计划里写的 `grep -cE "AVLN3000"`（那个永远输出 0，会误判「没退化」）。
2. **GUI 项目 `dotnet build` 会失败且不产出 DLL**，所以测试项目**不能**用 `<ProjectReference>` 引 `Sheas-Cealer-Nix.csproj`，否则整个测试套件无法构建。改用 `<Compile Include="..\Sheas-Cealer-Nix\...\*.cs" />` 把纯 BCL 的源文件直接编进测试项目（见 Task 8 / Task 9）。

把这个 64 记为**基线**。后续任何时候这个数字增大，说明是本计划引入的问题；`Themes/` 相关的错误不是我们的。

- [x] **Step 3: 确认不需要动 sln**

`Sheas-Cealer-Nix.sln:6-15` 引用了不存在的 `../Ona-Core/Sheas-Core.csproj`。**本计划全程不修改 `.sln`**，新项目也不加进去。验证一律用具体 csproj 路径。

---

## Task 1: 建立测试项目

**Files:**
- Create: `Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj`
- Create: `Sheas-Cealer-Nix.Tests/SmokeTests.cs`

**Why:** 当前仓库零测试。本计划要引入 socket 协议与路径计算这类逻辑，没有测试会很难验证。先把跑测试的地基打好。

- [ ] **Step 1: 创建测试项目文件**

`Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <RootNamespace>Sheas_Cealer_Nix.Tests</RootNamespace>
    <AssemblyName>Sheas-Cealer-Nix.Tests</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Cealing-Core\Cealing-Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 写一个占位测试**

`Sheas-Cealer-Nix.Tests/SmokeTests.cs`：

```csharp
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class SmokeTests
{
    [Fact]
    public void TestRunnerWorks() => Assert.True(true);
}
```

- [ ] **Step 3: 暂时注释掉 Core 引用并跑通**

`Cealing-Core` 还没建（Task 2 才建），所以先临时把 `<ProjectReference>` 那行注释掉：

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 4: 提交**

```bash
git add Sheas-Cealer-Nix.Tests
git commit -m "test: add xunit test project"
```

---

## Task 2: Cealing-Core 共享契约

**Files:**
- Create: `Cealing-Core/Cealing-Core.csproj`
- Create: `Cealing-Core/ProxyEngineKind.cs`
- Create: `Cealing-Core/ProxyRule.cs`
- Create: `Cealing-Core/ProxyCertSan.cs`
- Create: `Cealing-Core/ProxyConfig.cs`
- Create: `Cealing-Core/AgentPaths.cs`
- Create: `Cealing-Core/AgentJson.cs`
- Create: `Cealing-Core/Protocol/AgentCommand.cs`
- Create: `Cealing-Core/Protocol/AgentRequest.cs`
- Create: `Cealing-Core/Protocol/AgentResponse.cs`
- Create: `Cealing-Core/Protocol/AgentStatus.cs`
- Modify: `Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj`（取消注释 ProjectReference）
- Test: `Sheas-Cealer-Nix.Tests/AgentPathsTests.cs`
- Test: `Sheas-Cealer-Nix.Tests/ProxyConfigSerializationTests.cs`

- [ ] **Step 1: 创建项目文件**

`Cealing-Core/Cealing-Core.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <RootNamespace>Cealing_Core</RootNamespace>
    <AssemblyName>Cealing-Core</AssemblyName>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: 写引擎枚举**

`Cealing-Core/ProxyEngineKind.cs`：

```csharp
namespace Cealing_Core;

public enum ProxyEngineKind
{
    Builtin,
    External
}
```

- [ ] **Step 3: 写规则模型**

`Cealing-Core/ProxyRule.cs`：

```csharp
namespace Cealing_Core;

public sealed class ProxyRule
{
    public string ServerName { get; set; } = string.Empty;
    public string Ip { get; set; } = "127.0.0.1";
    public string? Sni { get; set; }
    public bool SniEnabled { get; set; } = true;
    public int Port { get; set; } = 443;
}
```

`ServerName` 承载的是**已经拼好的正则字符串**（不含前导 `~`）。这是刻意的：正则的拼法散落在 `Wins/MainWin.axaml.cs:944-945` 和 `:955`（`$` 分隔多域名、`^` 负向排除、`.`→`\.`、`*`→`.*`），把它留在 GUI 侧可以避免 agent 里重复一份实现。agent 只负责 `new Regex(ServerName, ...)`。

- [ ] **Step 4: 写证书域名模型**

`Cealing-Core/ProxyCertSan.cs`：

```csharp
namespace Cealing_Core;

public sealed class ProxyCertSan
{
    public string Domain { get; set; } = string.Empty;
    public bool Wildcard { get; set; }
}
```

`Wildcard = true` 对应规则里的 `*.domain`（或 `*domain`），语义见 Task 6。

- [ ] **Step 5: 写启动配置**

`Cealing-Core/ProxyConfig.cs`：

```csharp
using System.Collections.Generic;

namespace Cealing_Core;

public sealed class ProxyConfig
{
    public ProxyEngineKind Engine { get; set; } = ProxyEngineKind.Builtin;
    public bool Coproxy { get; set; }
    public bool Flashing { get; set; }
    public bool WriteHosts { get; set; }
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public int MixedPort { get; set; } = 7880;
    public string? NginxBinaryPath { get; set; }
    public string? NginxConfText { get; set; }
    public string? MihomoBinaryPath { get; set; }
    public string? MihomoConfText { get; set; }
    public List<ProxyCertSan> CertSans { get; set; } = [];
    public List<ProxyRule> Rules { get; set; } = [];
}
```

`WriteHosts` 对应 `Wins/MainWin.axaml.cs` 里 `NginxButtonHoldTimer_Tick` 的 `sender != null`（长按 1 秒 = hosts 模式，单击 = coproxy 模式）。原来是靠 `sender` 是不是 `DispatcherTimer` 来区分的，现在变成显式字段。

- [ ] **Step 6: 写路径工具**

`Cealing-Core/AgentPaths.cs`：

```csharp
using System;
using System.IO;

namespace Cealing_Core;

public static class AgentPaths
{
    public const string SocketFileName = "cealing-agent.sock";
    public const string ConfigFileName = "cealing-proxy.json";
    public const string LogFileName = "cealing-agent.log";
    public const string PidFileName = "cealing-agent.pid";

    public static string SocketPath => Path.Combine(Path.GetTempPath(), SocketFileName);
    public static string PidPath => Path.Combine(Path.GetTempPath(), PidFileName);

    public static string ConfigPath(string appDir) => Path.Combine(appDir, ConfigFileName);
    public static string LogPath(string appDir) => Path.Combine(appDir, LogFileName);
}
```

**关键设计点：`SocketPath` 只能用 GUI 进程的 `Path.GetTempPath()` 算出来，再通过 `--socket` 显式传给 agent。** 绝不能让 agent 自己算 —— agent 是 root，`TMPDIR` 和用户进程不是同一个（macOS 上是 `/var/folders/.../T/`），两边算出来的路径不会一致，socket 永远连不上。

`PidPath` 的作用是检测「agent 曾经起来过但现在死了」，用于启动时清理残留（Task 10）。

- [ ] **Step 7: 写 JSON 配置**

`Cealing-Core/AgentJson.cs`：

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cealing_Core;

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // 枚举按 camelCase 字符串序列化（"builtin"/"external"），而不是默认的数字。
        // 否则 Task 11 里手写的 `{"engine":"builtin",...}` 会反序列化失败报 "malformed config"。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    // System.Text.Json 遇到坏 JSON 是抛 JsonException，不是返回 null。
    // 协议层和调用方都按「解不出来 = null」处理（null → "malformed request"/"malformed config"），
    // 所以这里把 JsonException 收敛成 default。
    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
```

- [ ] **Step 8: 写协议类型**

`Cealing-Core/Protocol/AgentCommand.cs`：

```csharp
namespace Cealing_Core.Protocol;

public static class AgentCommand
{
    public const string Status = "status";
    public const string Start = "start";
    public const string Reload = "reload";
    public const string Stop = "stop";
    public const string Cleanup = "cleanup";
    public const string Ping = "ping";
    public const string Shutdown = "shutdown";
}
```

`Cealing-Core/Protocol/AgentRequest.cs`：

```csharp
namespace Cealing_Core.Protocol;

public sealed class AgentRequest
{
    public string Command { get; set; } = string.Empty;
    public string? Argument { get; set; }
}
```

`Cealing-Core/Protocol/AgentStatus.cs`：

```csharp
namespace Cealing_Core.Protocol;

public sealed class AgentStatus
{
    public bool Running { get; set; }
    public string Engine { get; set; } = "none";
    public bool Coproxy { get; set; }
    public int HttpPort { get; set; }
    public int HttpsPort { get; set; }
    public int RuleCount { get; set; }
    public long Pid { get; set; }
}
```

`Cealing-Core/Protocol/AgentResponse.cs`：

```csharp
namespace Cealing_Core.Protocol;

public sealed class AgentResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public AgentStatus? Status { get; set; }
}
```

- [ ] **Step 9: 取消测试项目的 Core 引用**

把 `Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj` 里的 `<ProjectReference>` 取消注释（去掉 Task 1 Step 3 加的注释）。

- [ ] **Step 10: 写失败的测试**

`Sheas-Cealer-Nix.Tests/AgentPathsTests.cs`：

```csharp
using Cealing_Core;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class AgentPathsTests
{
    [Fact]
    public void SocketPathLivesInCurrentProcessTempDir()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "cealing-agent.sock"), AgentPaths.SocketPath);
    }

    [Fact]
    public void SocketFileNameIsStable()
    {
        Assert.Equal("cealing-agent.sock", AgentPaths.SocketFileName);
    }

    [Fact]
    public void ConfigPathJoinsAppDir()
    {
        Assert.Equal(Path.Combine("/opt/app", "cealing-proxy.json"), AgentPaths.ConfigPath("/opt/app"));
    }
}
```

`Sheas-Cealer-Nix.Tests/ProxyConfigSerializationTests.cs`：

```csharp
using Cealing_Core;
using Cealing_Core.Protocol;
using System.Collections.Generic;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class ProxyConfigSerializationTests
{
    [Fact]
    public void ProxyConfigRoundTrips()
    {
        ProxyConfig original = new()
        {
            Engine = ProxyEngineKind.External,
            Coproxy = true,
            WriteHosts = false,
            Flashing = true,
            HttpPort = 8080,
            HttpsPort = 8443,
            NginxBinaryPath = "/opt/app/Cealing-Nginx",
            NginxConfText = "http{}",
            CertSans = [new ProxyCertSan { Domain = "pixiv.net", Wildcard = true }],
            Rules = [new ProxyRule { ServerName = "^a\\.b$", Ip = "1.2.3.4", Sni = "cdn.example", SniEnabled = true, Port = 443 }]
        };

        ProxyConfig? restored = AgentJson.Deserialize<ProxyConfig>(AgentJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(ProxyEngineKind.External, restored.Engine);
        Assert.True(restored.Coproxy);
        Assert.True(restored.Flashing);
        Assert.Equal(8080, restored.HttpPort);
        Assert.Equal("/opt/app/Cealing-Nginx", restored.NginxBinaryPath);
        Assert.Equal("cdn.example", Assert.Single(restored.Rules).Sni);
        Assert.True(Assert.Single(restored.CertSans).Wildcard);
    }

    [Fact]
    public void NullFieldsAreOmitted()
    {
        string json = AgentJson.Serialize(new ProxyConfig());

        Assert.DoesNotContain("nginxBinaryPath", json);
        Assert.DoesNotContain("mihomoConfText", json);
    }

    [Fact]
    public void AgentRequestRoundTrips()
    {
        AgentResponse? response = AgentJson.Deserialize<AgentResponse>(
            AgentJson.Serialize(new AgentResponse
            {
                Ok = true,
                Status = new AgentStatus { Running = true, Engine = "builtin", HttpsPort = 443, RuleCount = 7 }
            }));

        Assert.NotNull(response);
        Assert.True(response.Ok);
        AgentStatus? status = response.Status;

        Assert.NotNull(status);
        Assert.Equal(7, status.RuleCount);
    }
}
```

- [ ] **Step 11: 跑测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: `Passed! - Failed: 0, Passed: 7`（Task 1 的 SmokeTests 1 个 + AgentPathsTests 3 个 + ProxyConfigSerializationTests 3 个）

**实测：`Passed: 7` ✓**

> ⚠️ **xunit 2.9.2 的 `Assert.NotNull(object)` 返回 `void`**，没有泛型返回值重载（只额外有一个 `NotNull<T>(Nullable<T>)`）。所以 `Assert.NotNull(x).Foo` 这种链式写法会报 `CS0023 运算符 "." 无法应用于 "void" 类型的操作数`。必须先用局部变量接住再断言，`[NotNull]` 标注会让后续访问不报可空警告：
>
> ```csharp
> AgentStatus? status = response.Status;
> Assert.NotNull(status);
> Assert.Equal(7, status.RuleCount);
> ```
>
> 后面所有任务的测试都按这个写法，不要写 `Assert.NotNull(x).Prop`。

若失败：数字应仍是 7。如果对不上，检查是不是 `DefaultIgnoreCondition.WhenWritingNull` 没生效导致 `NullFieldsAreOmitted` 挂了。

- [ ] **Step 12: 提交**

```bash
git add Cealing-Core Sheas-Cealer-Nix.Tests
git commit -m "feat: add Cealing-Core shared agent contract"
```

---

## Task 3: agent 日志与 hosts 读写

**Files:**
- Create: `Cealing-Agent/Cealing-Agent.csproj`
- Create: `Cealing-Agent/Program.cs`（占位入口，Task 7 Step 5 替换）
- Create: `Cealing-Agent/AgentLog.cs`
- Create: `Cealing-Agent/HostsConf.cs`
- Modify: `Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj`（加 `..\Cealing-Agent` 引用）
- Test: `Sheas-Cealer-Nix.Tests/HostsConfTests.cs`
- Modify: `Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj`（加 Agent 引用）

**Why:** 这两个是纯逻辑、无需 root 就能测的部分，先做掉能给后面所有引擎任务一个错误输出通道。

- [ ] **Step 1: 创建项目文件**

`Cealing-Agent/Cealing-Agent.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <RootNamespace>Cealing_Agent</RootNamespace>
    <AssemblyName>Cealing-Agent</AssemblyName>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Cealing-Core\Cealing-Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 写日志**

`Cealing-Agent/AgentLog.cs`：

```csharp
using System;
using System.IO;
using System.Text;

namespace Cealing_Agent;

internal static class AgentLog
{
    private static readonly object Gate = new();
    private static string? _path;

    internal static void Init(string path)
    {
        _path = path;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"[agent] started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    internal static void Write(string level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";

        lock (Gate)
        {
            Console.Write(line);

            if (_path is null)
                return;

            try
            {
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static void Info(string message) => Write("info", message);
    internal static void Warn(string message) => Write("warn", message);
    internal static void Error(string message) => Write("error", message);
}
```

**注意：agent 以 root 运行，它写的日志文件普通用户读不了。** 这是有意的（`AgentLog.Init` 在 Task 3 Step 2，日志权限记在「已知遗留」），GUI 侧要靠 socket 拿状态，不靠读日志。

- [ ] **Step 3: 写 hosts 编辑器**

`Cealing-Agent/HostsConf.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cealing_Agent;

internal static class HostsConf
{
    internal const string StartMarker = "# Cealing Nginx Start";
    internal const string EndMarker = "# Cealing Nginx End";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 通配符域名没法用 hosts 表达（hosts 不支持 *.example.com），
    // 只能退而求其次给 www.<domain> 指一条，和 GUI 侧原来的行为保持一致。
    internal static string BuildBlock(IEnumerable<(string domain, bool wildcard)> sans)
    {
        StringBuilder builder = new();

        builder.Append(StartMarker).Append(Environment.NewLine);

        foreach ((string domain, bool wildcard) in sans)
        {
            if (string.IsNullOrWhiteSpace(domain) || domain.Contains('*'))
                continue;

            if (wildcard)
            {
                builder.Append("127.0.0.1 www.").Append(domain).Append(Environment.NewLine);

                continue;
            }

            builder.Append("127.0.0.1 ").Append(domain).Append(Environment.NewLine);
        }

        builder.Append(EndMarker).Append(Environment.NewLine);

        return builder.ToString();
    }

    internal static void Append(string hostsPath, string block)
    {
        Remove(hostsPath);

        File.SetAttributes(hostsPath, File.GetAttributes(hostsPath) & ~FileAttributes.ReadOnly);
        File.AppendAllText(hostsPath, block, Utf8NoBom);
    }

    internal static void Remove(string hostsPath)
    {
        if (!File.Exists(hostsPath))
            return;

        string content = File.ReadAllText(hostsPath);

        int start = content.IndexOf(StartMarker, StringComparison.Ordinal);

        if (start == -1)
            return;

        int end = content.LastIndexOf(EndMarker, StringComparison.Ordinal);

        if (end == -1)
            return;

        File.SetAttributes(hostsPath, File.GetAttributes(hostsPath) & ~FileAttributes.ReadOnly);
        File.WriteAllText(hostsPath, content.Remove(start, end - start + EndMarker.Length), Utf8NoBom);
    }
}
```

- [ ] **Step 4: 在测试项目引用 agent**

`Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj`，在 `<ItemGroup>` 里加：

```xml
    <ProjectReference Include="..\Cealing-Agent\Cealing-Agent.csproj" />
```

`Cealing-Agent.csproj` 随之要加 `InternalsVisibleTo` —— 测试要访问 `Cealing_Agent` 的 `internal` 成员（`AgentServer`、`EngineSupervisor` 等）。注意这个属性必须在 **agent 项目**里声明，写在测试项目里是反的（那只会暴露测试自己的 internal）。在 `Cealing-Agent.csproj` 的属性组里加：

```xml
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <!-- 必须是程序集简单名（和 <AssemblyName> 一致，带连字符），写命名空间的下划线形式会静默失效 -->
      <_Parameter1>Sheas-Cealer-Nix.Tests</_Parameter1>
    </AssemblyAttribute>
    <!-- agent 是 macOS/Linux 独有的 Unix daemon（Unix domain socket、/etc/hosts、security/pkexec）。
         标记整个程序集不支持 Windows，让 CA1416 平台分析器不再对 File.*UnixFileMode 之类的调用报警。 -->
    <AssemblyAttribute Include="System.Runtime.Versioning.UnsupportedOSPlatformAttribute">
      <_Parameter1>windows</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

> ⚠️ **`InternalsVisibleTo` 的字符串必须是「程序集简单名」，不是命名空间。** 本仓库用的是 `RootNamespace=Sheas_Cealer_Nix`（下划线）+ `AssemblyName=Sheas-Cealer-Nix`（连字符）。`_Parameter1` 要写成 `Sheas-Cealer-Nix.Tests`。写成分支里的 `Sheas_Cealer_Nix.Tests` 会让 IVT 静默失效，编译报 `CS0122: "HostsConf" 不可访问`。验证办法：`cat Cealing-Agent/obj/Debug/net8.0/Cealing-Agent.AssemblyInfo.cs` 看生成的 `InternalsVisibleToAttribute(...)` 参数。

**追加：占位入口 `Cealing-Agent/Program.cs`（必需）**

`Cealing-Agent` 是 `Exe` 项目，但真正的 `Program.cs` 到 Task 7 Step 5 才写。Exe 没有 `Main` 会报 `CS5001`，导致 Task 3-6 期间既编译不了 agent 项目、也编译不了引用它的测试项目。所以 Task 3 先放一个最小占位：

```csharp
using System.Threading.Tasks;

namespace Cealing_Agent;

// 占位入口，让 Cealing-Agent 作为 Exe 项目在 Task 3-6 期间就能编译（Exe 必须有 Main，否则 CS5001）。
// Task 7 Step 5 用完整的参数解析与 RunAsync 整体替换本文件。
internal static class Program
{
    private static Task Main() => Task.CompletedTask;
}
```

（Task 11 手工验证要 `dotnet run --project Cealing-Agent`，而本机只有 .NET 10 运行时没有 8.0，所以 csproj 里加了 `<RollForward>LatestMajor</RollForward>`。）

- [ ] **Step 5: 写失败的测试**

`Sheas-Cealer-Nix.Tests/HostsConfTests.cs`：

```csharp
using Cealing_Agent;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class HostsConfTests : IDisposable
{
    private readonly string TempHosts = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public HostsConfTests() => File.WriteAllText(TempHosts, "127.0.0.1 localhost\n");

    public void Dispose() => File.Delete(TempHosts);

    [Fact]
    public void BuildBlockWrapsWithMarkers()
    {
        string block = HostsConf.BuildBlock([("pixiv.net", false)]);

        Assert.StartsWith(HostsConf.StartMarker + Environment.NewLine, block);
        Assert.Contains("127.0.0.1 pixiv.net", block);
        Assert.EndsWith(HostsConf.EndMarker + Environment.NewLine, block);
    }

    [Fact]
    public void BuildBlockMapsWildcardToWwwOnly()
    {
        string block = HostsConf.BuildBlock([("fanbox.cc", true)]);

        Assert.Contains("127.0.0.1 www.fanbox.cc", block);
        Assert.DoesNotContain("\n127.0.0.1 fanbox.cc", block);
    }

    [Fact]
    public void BuildBlockSkipsUnusableDomains()
    {
        string block = HostsConf.BuildBlock([("", false), ("  ", false), ("a*b.example", false)]);

        Assert.DoesNotContain("127.0.0.1", block);
    }

    [Fact]
    public void RemoveStripsOnlyOurBlock()
    {
        File.WriteAllText(TempHosts, "127.0.0.1 localhost\n" + HostsConf.BuildBlock([("pixiv.net", false)]) + "::1 ip6-localhost\n");

        HostsConf.Remove(TempHosts);

        string content = File.ReadAllText(TempHosts);
        Assert.DoesNotContain(HostsConf.StartMarker, content);
        Assert.Contains("127.0.0.1 localhost", content);
        Assert.Contains("::1 ip6-localhost", content);
    }

    [Fact]
    public void RemoveIsNoOpWhenNoBlock()
    {
        HostsConf.Remove(TempHosts);

        Assert.Equal("127.0.0.1 localhost\n", File.ReadAllText(TempHosts));
    }

    [Fact]
    public void AppendIsIdempotent()
    {
        HostsConf.Append(TempHosts, HostsConf.BuildBlock([("pixiv.net", false)]));
        HostsConf.Append(TempHosts, HostsConf.BuildBlock([("fanbox.cc", false)]));

        string content = File.ReadAllText(TempHosts);
        Assert.Equal(1, CountOccurrences(content, HostsConf.StartMarker));
        Assert.DoesNotContain("pixiv.net", content);
        Assert.Contains("fanbox.cc", content);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
```

- [ ] **Step 6: 跑测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: `Failed: 0, Passed: 13`（7 + 6 个 HostsConfTests）

- [ ] **Step 7: 提交**

```bash
git add Cealing-Agent Sheas-Cealer-Nix.Tests
git commit -m "feat: add agent log and hosts conf editor"
```

---

## Task 4: 跨平台根证书装卸

**Files:**
- Create: `Cealing-Agent/TrustStore.cs`
- Test: `Sheas-Cealer-Nix.Tests/TrustStoreCommandTests.cs`

**Why:** 这是本计划要修掉的既有 bug —— `Wins/MainWin.axaml.cs:286` 用 `new X509Store(StoreName.Root, StoreLocation.LocalMachine)` 装根证书，代码里没有任何平台 CLI 兜底，.NET 在 macOS 上不保证写到系统钥匙串。改成走各平台原生命令，顺带保证「装进去 = 系统真的信任」。

- [ ] **Step 1: 写命令构造器**

`Cealing-Agent/TrustStore.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Cealing_Agent;

internal static class TrustStore
{
    internal const string RootCertSubjectName = "CN=Cealing Cert Root";
    internal const string ChildCertSubjectName = "CN=Cealing Cert Child";

    private const string MacSystemKeychain = "/Library/Keychains/System.keychain";
    private const string LinuxCertDir = "/usr/local/share/ca-certificates";
    private const string LinuxCertFile = "cealing-root.crt";

    internal static IReadOnlyList<string> BuildInstallArgs(string rootCertPemPath, string platform) => platform switch
    {
        "macos" => ["security", "add-trusted-cert", "-d", "-r", "trustRoot", "-k", MacSystemKeychain, rootCertPemPath],
        "linux" => ["sh", "-c", $"cp -- {ShellQuote(rootCertPemPath)} {ShellQuote(LinuxCertDir + "/" + LinuxCertFile)} && update-ca-certificates"],
        _ => throw new PlatformNotSupportedException($"unsupported platform: {platform}")
    };

    internal static IReadOnlyList<string> BuildUninstallArgs(string thumbprint, string platform) => platform switch
    {
        "macos" => ["security", "delete-certificate", "-Z", thumbprint, MacSystemKeychain],
        "linux" => ["sh", "-c", $"rm -f -- {ShellQuote(LinuxCertDir + "/" + LinuxCertFile)} && update-ca-certificates --fresh"],
        _ => throw new PlatformNotSupportedException($"unsupported platform: {platform}")
    };

    // appDir 来自 ApplicationBase，理论上可以包含单引号（应用装在 ~/Applications/My 'App'/ 这类路径）。
    // 直接插进 sh -c 会被 shell 拆开，属于命令注入，所以统一走单引号转义。
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    internal static string Thumbprint(X509Certificate2 certificate) => certificate.Thumbprint;

    internal static void Install(string rootCertPemPath, string platform) => Run(BuildInstallArgs(rootCertPemPath, platform), "install root cert");

    internal static void Uninstall(string thumbprint, string platform)
    {
        try
        {
            Run(BuildUninstallArgs(thumbprint, platform), "uninstall root cert");
        }
        catch (Exception ex)
        {
            // 证书本来就不在（首次启动、或上次卸载失败）不算错误
            AgentLog.Warn($"uninstall root cert skipped: {ex.Message}");
        }
    }

    private static void Run(IReadOnlyList<string> args, string what)
    {
        AgentLog.Info($"{what}: {string.Join(' ', args)}");

        ProcessStartInfo startInfo = new(args[0]) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };

        foreach (string arg in args.Skip(1))
            startInfo.ArgumentList.Add(arg);

        using Process? process = Process.Start(startInfo);

        if (process is null)
            throw new InvalidOperationException($"failed to start {args[0]} for {what}");

        // 必须先 WaitForExit 再读流：stdout/stderr 都重定向了，若在读流之前不等待，
        // 子进程一旦写满 pipe buffer 就会卡住，而父进程还在等它退出 —— 死锁。
        process.WaitForExit();

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr))
            AgentLog.Info($"{what} output: {(stdout + stderr).Trim()}");

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{what} failed (exit {process.ExitCode}): {stderr.Trim()}");
    }
}
```

- [ ] **Step 2: 写失败的测试**

`Sheas-Cealer-Nix.Tests/TrustStoreCommandTests.cs`：

```csharp
using Cealing_Agent;
using System;
using System.Linq;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class TrustStoreCommandTests
{
    [Fact]
    public void MacInstallUsesSecurityAddTrustedCert()
    {
        string[] args = [.. TrustStore.BuildInstallArgs("/tmp/root.pem", "macos")];

        Assert.Equal("security", args[0]);
        Assert.Equal("add-trusted-cert", args[1]);
        Assert.Contains("-d", args);
        Assert.Contains("trustRoot", args);
        Assert.Contains("/Library/Keychains/System.keychain", args);
        Assert.Equal("/tmp/root.pem", args[^1]);
    }

    [Fact]
    public void MacUninstallUsesSha1Lookup()
    {
        string[] args = [.. TrustStore.BuildUninstallArgs("AABBCC", "macos")];

        Assert.Equal("delete-certificate", args[1]);
        Assert.Equal("AABBCC", args[args.ToList().IndexOf("-Z") + 1]);
    }

    [Fact]
    public void LinuxInstallCopiesAndRefreshes()
    {
        string script = TrustStore.BuildInstallArgs("/tmp/root.pem", "linux")[2];

        Assert.Contains("cp -- '/tmp/root.pem' '/usr/local/share/ca-certificates/cealing-root.crt'", script);
        Assert.Contains("update-ca-certificates", script);
    }

    [Fact]
    public void LinuxUninstallRemovesAndRefreshes()
    {
        string script = TrustStore.BuildUninstallArgs("AABBCC", "linux")[2];

        Assert.Contains("rm -f", script);
        Assert.Contains("--fresh", script);
    }

    [Fact]
    public void ShellQuoteEscapesEmbeddedSingleQuote()
    {
        Assert.Equal("'/tmp/it'\\''s/root.pem'", TrustStore.ShellQuote("/tmp/it's/root.pem"));
    }

    [Fact]
    public void LinuxInstallQuotesPathContainingSingleQuote()
    {
        string script = TrustStore.BuildInstallArgs("/tmp/it's/root.pem", "linux")[2];

        Assert.Contains("'/tmp/it'\\''s/root.pem'", script);
        Assert.DoesNotContain(" /tmp/it's ", script);
    }

    [Fact]
    public void UnknownPlatformThrows()
    {
        Assert.Throws<PlatformNotSupportedException>(() => TrustStore.BuildInstallArgs("/tmp/root.pem", "windows"));
    }
}
```

- [ ] **Step 3: 跑测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: `Failed: 0, Passed: 20`（13 + 7 个 TrustStoreCommandTests）

- [ ] **Step 4: 提交**

```bash
git add Cealing-Agent Sheas-Cealer-Nix.Tests
git commit -m "feat: install root cert via native trust store tooling"
```

---

## Task 5: 内置引擎搬进 agent

**Files:**
- Create: `Cealing-Agent/Engines/BuiltinEngineRule.cs`
- Create: `Cealing-Agent/Engines/BuiltinEngine.cs`
- Create: `Cealing-Agent/ProxyCertificateFactory.cs`
- Delete: `Sheas-Cealer-Nix/Utils/BuiltinNginx.cs`

**Why:** 这是「内置引擎不依赖 nginx」的关键。`Utils/BuiltinNginx.cs` 现在跑在 Avalonia 主进程里（`Wins/MainWin.axaml.cs:352`），而主进程在 v1 里不再是 root，所以它必须搬到一个 root 进程里。

- [ ] **Step 1: 搬运规则类**

`Cealing-Agent/Engines/BuiltinEngineRule.cs` —— 内容照抄 `Sheas-Cealer-Nix/Utils/BuiltinNginx.cs:26-76`，只改命名空间和 `Regex` 构造：

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent.Engines;

internal sealed class BuiltinEngineRule
{
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    internal Regex ServerName { get; init; } = null!;
    internal string Ip { get; init; } = string.Empty;
    internal string? Sni { get; init; }
    internal bool SniEnabled { get; init; } = true;
    internal int Port { get; init; } = 443;

    internal HttpClient? Client;

    internal static BuiltinEngineRule FromProxyRule(ProxyRule rule) => new()
    {
        ServerName = new Regex(rule.ServerName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout),
        Ip = rule.Ip,
        Sni = rule.Sni,
        SniEnabled = rule.SniEnabled,
        Port = rule.Port
    };

    internal void Arm() => Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = ConnectAsync,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    internal void Disarm() => Client?.Dispose();

    // 上游证书一律不校验：SNI 已经被换成伪造值，返回的证书与伪造域名必然对不上。
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(Ip, Port, cancellationToken).ConfigureAwait(false);

            SslStream sslStream = new(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false, static (_, _, _, _) => true);

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = SniEnabled ? Sni : null,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken).ConfigureAwait(false);

            return sslStream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
```

在文件顶部加 `using Cealing_Core;`（`FromProxyRule` 用到 `ProxyRule`）。

- [ ] **Step 2: 搬运引擎主体**

`Cealing-Agent/Engines/BuiltinEngine.cs` —— 照抄 `Sheas-Cealer-Nix/Utils/BuiltinNginx.cs:78-261`，改名 `BuiltinNginx` → `BuiltinEngine`、`StartAsync` 签名改为接收 `ProxyConfig` 与证书：

```csharp
using Cealing_Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Cealing_Agent.Engines;

internal sealed class BuiltinEngine
{
    // nginx 的 proxy_* 语义对照：return https://$host$request_uri → 302 跳转；
    // server_name ~正则 → 首个大不敏感匹配，未匹配时回退到第一个 server（nginx default_server 语义）。
    private static readonly string[] HopByHopHeaders =
        ["Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade"];

    private readonly List<BuiltinEngineRule> _rules = [];
    private WebApplication? _app;
    private Task? _shutdownTask;

    internal bool IsRunning => _app is not null;

    internal async Task StartAsync(ProxyConfig config, X509Certificate2 certificate, string logPath)
    {
        await StopAsync().ConfigureAwait(false);

        _rules.Clear();

        foreach (ProxyRule rule in config.Rules)
        {
            try
            {
                _rules.Add(BuiltinEngineRule.FromProxyRule(rule));
            }
            catch (ArgumentException ex)
            {
                AgentLog.Warn($"skip malformed rule '{rule.ServerName}': {ex.Message}");
            }
        }

        _rules.ForEach(rule => rule.Arm());

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = null });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(logPath));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ListenLocalhost(config.HttpPort);
            kestrel.ListenLocalhost(config.HttpsPort, listen => listen.UseHttps(certificate));
        });

        WebApplication app = builder.Build();

        // .NET 8 的 WebApplication 只暴露 Run(string url) 和 Use(Func<RequestDelegate, RequestDelegate>)：
        // Run 内部是 StartAsync + WaitForShutdownAsync，同步阻塞到进程退出，StartAsync 永远不会返回
        // （原 Utils/BuiltinNginx.cs:116 的 app.Run(...) 就是这个坑，整个 StartAsync 卡死不返回）。
        // 所以挂中间件走 Use(middlewareFactory)，再显式 await StartAsync。
        app.Use(next => HandleAsync);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            _rules.ForEach(rule => rule.Disarm());
            _rules.Clear();

            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _app = app;
        _shutdownTask = app.WaitForShutdownAsync();

        AgentLog.Info($"builtin engine listening on {config.HttpPort}/{config.HttpsPort} with {_rules.Count} rules");
    }

    internal async Task StopAsync()
    {
        WebApplication? app = _app;

        _app = null;

        if (app is not null)
        {
            try { await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { }

            try { await app.DisposeAsync().ConfigureAwait(false); }
            catch { }

            if (_shutdownTask is not null)
            {
                try { await _shutdownTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch { }
            }
        }

        _shutdownTask = null;
        _rules.ForEach(rule => rule.Disarm());
        _rules.Clear();
    }

    private async Task HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;

        if (!request.IsHttps)
        {
            context.Response.Headers.Location = $"https://{request.Host.Host}{request.PathBase}{request.Path}{request.QueryString}";
            context.Response.StatusCode = StatusCodes.Status302Found;

            return;
        }

        BuiltinEngineRule? rule = _rules.Find(rule => rule.ServerName.IsMatch(request.Host.Host)) ?? (_rules.Count != 0 ? _rules[0] : null);

        if (rule is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using HttpRequestMessage upstreamRequest = new(new HttpMethod(request.Method), $"https://{rule.Ip}{request.PathBase}{request.Path}{request.QueryString}")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        upstreamRequest.Headers.Host = request.Host.Value;

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in request.Headers)
            if (!HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase) &&
                !header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());

        // 客户端带 Content-Length 时原样沿用，避免被降级成 chunked（部分上游不接受）；
        // 只有 chunked 请求才让 StreamContent 自行决定长度。
        if (request.ContentLength > 0)
        {
            upstreamRequest.Content = new StreamContent(request.Body);
            upstreamRequest.Content.Headers.ContentLength = request.ContentLength;
        }
        else if (request.Headers.ContainsKey("Transfer-Encoding"))
            upstreamRequest.Content = new StreamContent(request.Body);

        HttpResponseMessage upstreamResponse;

        try
        {
            upstreamResponse = await rule.Client!.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            foreach (KeyValuePair<string, IEnumerable<string>> header in upstreamResponse.Headers)
                if (!HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = new Microsoft.Extensions.Primitives.StringValues(header.Value.ToArray());

            foreach (KeyValuePair<string, IEnumerable<string>> header in upstreamResponse.Content.Headers)
                if (!HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = new Microsoft.Extensions.Primitives.StringValues(header.Value.ToArray());

            if (!HttpMethods.IsHead(request.Method) && upstreamResponse.StatusCode != HttpStatusCode.NoContent && upstreamResponse.StatusCode != HttpStatusCode.NotModified)
                await upstreamResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed class FileLoggerProvider(string logPath) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(logPath, categoryName);

        public void Dispose() { }
    }

    private sealed class FileLogger(string logPath, string category) : ILogger
    {
        private static readonly object LogLock = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {formatter(state, exception)}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
```

- [ ] **Step 3: 写证书工厂**

`Cealing-Agent/ProxyCertificateFactory.cs`：

```csharp
using Cealing_Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Cealing_Agent;

internal sealed record ProxyCertificate(RSA Key, X509Certificate2 Root, X509Certificate2 Child, string RootPemPath, string ChildPemPath, string KeyPemPath)
{
    // 只在 EngineSupervisor.StopAsync 里手动释放（见 Task 7 Step 3），
    // 持有方只有 supervisor，不要在调用方再包一层 using
    internal void Dispose()
    {
        Root.Dispose();
        Child.Dispose();
        Key.Dispose();
    }
}

internal static class ProxyCertificateFactory
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static ProxyCertificate Create(ProxyConfig config, string appDir)
    {
        RSA key = RSA.Create(2048);

        CertificateRequest rootRequest = new(TrustStore.RootCertSubjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, false));

        // 不能用 using：root/child 的生命周期要转移给返回的 ProxyCertificate record，
        // 由 EngineSupervisor.StopAsync 里的 record.Dispose() 统一释放。这里用 using 会在方法返回时提前释放。
        X509Certificate2 root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(100));

        CertificateRequest childRequest = new(TrustStore.ChildCertSubjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder sanBuilder = new();

        foreach (ProxyCertSan san in config.CertSans)
        {
            if (string.IsNullOrWhiteSpace(san.Domain) || san.Domain.Contains('*'))
                continue;

            sanBuilder.AddDnsName(san.Wildcard ? $"*.{san.Domain}" : san.Domain);
        }

        childRequest.CertificateExtensions.Add(sanBuilder.Build());

        X509Certificate2 child = childRequest.Create(root, root.NotBefore, root.NotAfter, Guid.NewGuid().ToByteArray());

        // Kestrel 的 UseHttps 要求证书**带私钥**，而 childRequest.Create(root, ...) 出来的证书没有私钥。
        // 少了这一步，内置引擎会报 "The server mode SSL must use a certificate with the associated private key."。
        X509Certificate2 childWithKey = child.CopyWithPrivateKey(key);
        child.Dispose();

        string rootPemPath = Path.Combine(appDir, "Cealing-Root.pem");
        string childPemPath = Path.Combine(appDir, "Cealing-Cert.pem");
        string keyPemPath = Path.Combine(appDir, "Cealing-Key.pem");

        // root.pem 必须让普通用户也能读：GUI 侧要算 thumbprint 并传给 agent 卸载，
        // 而 agent 建的目录和文件是 root 所有。
        WritePem(rootPemPath, root.ExportCertificatePem(), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        WritePem(childPemPath, childWithKey.ExportCertificatePem(), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        WritePem(keyPemPath, key.ExportPkcs8PrivateKeyPem(), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        return new ProxyCertificate(key, root, childWithKey, rootPemPath, childPemPath, keyPemPath);
    }

    private static void WritePem(string path, string pem, UnixFileMode mode)
    {
        File.WriteAllText(path, pem, Utf8NoBom);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }
}
```

**注意：证书生成整体搬进 agent。** 原来在 `Wins/MainWin.axaml.cs:278-328`。GUI 侧不再持有 `RSA` / `X509Certificate2`，`MainWin.BuiltinNginxCert` 字段（`Wins/MainWin.axaml.cs:52`）随之删除。

`rootPemPath` 的 0644 权限是关键 —— GUI 要读它算 thumbprint，之后清理根证书要靠这个 thumbprint 走 `security delete-certificate -Z`（见 Task 4 的 `TrustStore.Uninstall` 和 Task 11 Step 10 的手工验证）。

- [ ] **Step 4: 删除旧文件**

```bash
git rm Sheas-Cealer-Nix/Utils/BuiltinNginx.cs
```

**此时 GUI 项目会编译失败**（`Wins/MainWin.axaml.cs:46-47` 引用了 `BuiltinNginx` / `BuiltinNginxRule`）。这是预期的，Task 8 修好。为了让 Task 5 本身能验证，只编译 agent：

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build Cealing-Agent/Cealing-Agent.csproj -c Release
```

Expected: `生成成功` / `Build succeeded`，0 错误

- [ ] **Step 5: 提交**

```bash
git add Cealing-Agent Sheas-Cealer-Nix/Utils/BuiltinNginx.cs
git commit -m "refactor: move builtin proxy engine into agent process"
```

---

## Task 6: 外部 nginx 与 mihomo 引擎

**Files:**
- Create: `Cealing-Agent/Engines/ExternalNginxEngine.cs`
- Create: `Cealing-Agent/Engines/MihomoEngine.cs`

**Why:** 这两个替代 `Wins/MainWin.axaml.cs:447-494`（`LaunchExternalNginx` + `ExternalNginxProcesses`）和 `:536-599`（mihomo 启停）。原实现靠 `Process.Exited` 事件感知退出，root 后台进程不是子进程，拿不到该事件，必须改成 PID 轮询。

- [ ] **Step 1: 写外部 nginx 引擎**

`Cealing-Agent/Engines/ExternalNginxEngine.cs`：

```csharp
using Cealing_Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent.Engines;

internal sealed class ExternalNginxEngine
{
    private Process? _process;

    internal bool IsRunning => _process is { HasExited: false };

    internal async Task StartAsync(ProxyConfig config, CancellationToken cancellationToken)
    {
        if (config.NginxBinaryPath is null || config.NginxConfText is null)
            throw new InvalidOperationException("external engine requires nginxBinaryPath and nginxConfText");

        cancellationToken.ThrowIfCancellationRequested();

        string appDir = Path.GetDirectoryName(config.NginxBinaryPath)!;

        EnsureExecutable(config.NginxBinaryPath);

        // 配置里必须是前台模式（daemon off）：nginx 默认 fork 到后台，父进程立刻退出就拿不到句柄。
        File.WriteAllText(Path.Combine(appDir, "nginx.conf"), config.NginxConfText);

        ProcessStartInfo startInfo = new(config.NginxBinaryPath)
        {
            WorkingDirectory = appDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        // 用 ArgumentList 而不是拼 Arguments 字符串：应用目录可能带空格（本仓库路径就带 "vscode workspace"）。
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(appDir);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("nginx.conf");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start nginx");

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AgentLog.Error($"[nginx] {e.Data}");
        };

        _process.BeginErrorReadLine();

        AgentLog.Info($"external nginx started (pid {_process.Id})");

        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal void Stop()
    {
        Process? process = _process;

        _process = null;

        if (process is null || process.HasExited)
            return;

        try
        {
            // Process.Kill 返回 void（不是 bool），失败会抛异常
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"killing nginx failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    internal static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // nginx 只认 daemon off 时的 stdout/exit，无法用端口探测判断「配置是否生效」，
    // 所以这里退化成「进程活着 + 端口在监听」两个弱信号。
    internal static bool IsListening(int port)
    {
        try
        {
            using System.Net.Sockets.TcpClient client = new();

            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    internal static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { }
    }
}
```

- [ ] **Step 2: 写 mihomo 引擎**

`Cealing-Agent/Engines/MihomoEngine.cs`：

```csharp
using Cealing_Core;
using System;
using System.Diagnostics;
using System.IO;

namespace Cealing_Agent.Engines;

internal sealed class MihomoEngine
{
    private Process? _process;

    internal bool IsRunning => _process is { HasExited: false };

    internal void Start(ProxyConfig config)
    {
        if (config.MihomoBinaryPath is null || config.MihomoConfText is null)
            throw new InvalidOperationException("mihomo engine requires mihomoBinaryPath and mihomoConfText");

        string appDir = Path.GetDirectoryName(config.MihomoBinaryPath)!;

        ExternalNginxEngine.EnsureExecutable(config.MihomoBinaryPath);

        File.WriteAllText(Path.Combine(appDir, "config.yaml"), config.MihomoConfText);

        ProcessStartInfo startInfo = new(config.MihomoBinaryPath)
        {
            WorkingDirectory = appDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(appDir);

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start mihomo");

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AgentLog.Error($"[mihomo] {e.Data}");
        };

        _process.BeginErrorReadLine();

        AgentLog.Info($"mihomo started (pid {_process.Id})");
    }

    internal void Stop()
    {
        Process? process = _process;

        _process = null;

        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"killing mihomo failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }
}
```

- [ ] **Step 3: 编译 agent**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build Cealing-Agent/Cealing-Agent.csproj -c Release
```

Expected: `Build succeeded`，0 错误

- [ ] **Step 4: 提交**

```bash
git add Cealing-Agent
git commit -m "feat: add external nginx and mihomo engines to agent"
```

---

## Task 7: 引擎调度与 agent 服务端

**Files:**
- Create: `Cealing-Agent/IProxyEngine.cs`
- Create: `Cealing-Agent/EngineSupervisor.cs`
- Create: `Cealing-Agent/AgentServer.cs`
- Create: `Cealing-Agent/Program.cs`
- Test: `Sheas-Cealer-Nix.Tests/AgentServerTests.cs`

- [ ] **Step 1: 写引擎接口**

`Cealing-Agent/IProxyEngine.cs`：

```csharp
using Cealing_Core;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

internal interface IProxyEngine
{
    bool IsRunning { get; }

    Task StartAsync(ProxyConfig config, CancellationToken cancellationToken);
    Task StopAsync();
}
```

（注意：`ImplicitUsings` 是 `disable`，所以 `Task` / `CancellationToken` / `Func<>` 的 using 必须显式写。）

- [ ] **Step 2: 给两个引擎补上统一接口**

`BuiltinEngine` 已有 `StartAsync(ProxyConfig, X509Certificate2, string)`，签名对不上。改 `EngineSupervisor` 时用适配器更省事，不必改引擎本身。在 `Cealing-Agent/EngineAdapters.cs` 加：

```csharp
using Cealing_Core;
using Cealing_Agent.Engines;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

// 证书由 EngineSupervisor 持有并在 StopAsync 里释放，adapter 只借用，不能自己 dispose
internal sealed class BuiltinEngineAdapter(BuiltinEngine engine, Func<ProxyCertificate> certificateFactory, string logPath) : IProxyEngine
{
    public bool IsRunning => engine.IsRunning;

    public async Task StartAsync(ProxyConfig config, CancellationToken cancellationToken)
    {
        ProxyCertificate certificate = certificateFactory();

        await engine.StartAsync(config, certificate.Child, logPath);
    }

    public Task StopAsync() => engine.StopAsync();
}

internal sealed class ExternalNginxAdapter(ExternalNginxEngine engine) : IProxyEngine
{
    public bool IsRunning => engine.IsRunning;

    public Task StartAsync(ProxyConfig config, CancellationToken cancellationToken) => engine.StartAsync(config, cancellationToken);

    public Task StopAsync()
    {
        engine.Stop();

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: 写调度器**

`Cealing-Agent/EngineSupervisor.cs`：

```csharp
using Cealing_Core;
using Cealing_Agent.Engines;
using Cealing_Core.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

internal sealed class EngineSupervisor(string appDir, string hostsPath, string errorLogPath)
{
    private readonly BuiltinEngine _builtin = new();
    private readonly ExternalNginxEngine _external = new();
    private readonly MihomoEngine _mihomo = new();

    private IProxyEngine? _active;
    private bool _mihomoRunning;
    private ProxyConfig? _config;
    private ProxyCertificate? _certificate;
    private string? _rootThumbprint;

    internal AgentStatus Status => new()
    {
        Running = (_active?.IsRunning ?? false) || _mihomoRunning,
        Engine = _mihomoRunning ? "mihomo" : _active switch
        {
            null => "none",
            BuiltinEngineAdapter => "builtin",
            ExternalNginxAdapter => "external",
            _ => "none"
        },
        Coproxy = _config?.Coproxy ?? false,
        HttpPort = _config?.HttpPort ?? 0,
        HttpsPort = _config?.HttpsPort ?? 0,
        RuleCount = _config?.Rules.Count ?? 0,
        Pid = Environment.ProcessId
    };

    internal async Task StartAsync(ProxyConfig config, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);

        if (config.WriteHosts)
        {
            HostsConf.Append(hostsPath, HostsConf.BuildBlock(config.CertSans.Select(san => (san.Domain, san.Wildcard))));

            AgentLog.Info($"hosts block written ({config.CertSans.Count} sans)");
        }

        _certificate = ProxyCertificateFactory.Create(config, appDir);
        _rootThumbprint = TrustStore.Thumbprint(_certificate.Root);

        string platform = OperatingSystem.IsMacOS() ? "macos" : "linux";

        try
        {
            TrustStore.Install(_certificate.RootPemPath, platform);
        }
        catch (Exception ex)
        {
            AgentLog.Error($"root cert install failed, continuing anyway: {ex.Message}");
        }

        try
        {
            // coproxy 模式 = mihomo 负责系统级 TUN 捕获（把伪造域名映射到 127.0.0.1）
            // + nginx/builtin 在 443 上做 SNI 伪造。两者缺一不可，所以这里 **不是 if/else**：
            // 先按需起 mihomo，再无条件起 nginx/builtin。
            //
            // mihomo 是外部二进制（macOS 需自行下载），没装时必须优雅退化到「只起 nginx/builtin」
            // （等价于原来的浏览器级伪造），绝不能因为缺 mihomo 就整个启动失败
            // —— 否则用户会看到 "mihomo engine requires mihomoBinaryPath and mihomoConfText"。
            if (config.Coproxy && CanStartMihomo(config))
            {
                _mihomo.Start(config);
                _mihomoRunning = true;
            }
            else if (config.Coproxy)
            {
                AgentLog.Warn("coproxy requested but mihomo binary/conf is missing; starting nginx/builtin only");
            }

            if (config.Engine == ProxyEngineKind.Builtin)
            {
                _active = new BuiltinEngineAdapter(_builtin, () => _certificate!, errorLogPath);
                await _active.StartAsync(config, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _active = new ExternalNginxAdapter(_external);
                await _active.StartAsync(config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }

        _config = config;
    }

    private static bool CanStartMihomo(ProxyConfig config) =>
        !string.IsNullOrWhiteSpace(config.MihomoBinaryPath) &&
        File.Exists(config.MihomoBinaryPath) &&
        !string.IsNullOrWhiteSpace(config.MihomoConfText);

    internal async Task StopAsync()
    {
        if (_active is not null)
        {
            try
            {
                await _active.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"stopping engine failed: {ex.Message}");
            }

            _active = null;
        }

        if (_mihomoRunning)
        {
            try
            {
                _mihomo.Stop();
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"stopping mihomo failed: {ex.Message}");
            }

            _mihomoRunning = false;
        }

        _builtin.StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        if (_rootThumbprint is not null)
        {
            TrustStore.Uninstall(_rootThumbprint, OperatingSystem.IsMacOS() ? "macos" : "linux");
            _rootThumbprint = null;
        }

        _certificate?.Dispose();
        _certificate = null;

        HostsConf.Remove(hostsPath);

        _config = null;
    }
}
```

- [ ] **Step 4: 写 socket 服务端**

`Cealing-Agent/AgentServer.cs`：

```csharp
using Cealing_Core;
using Cealing_Core.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

internal sealed class AgentServer(string socketPath, int ownerUid, EngineSupervisor supervisor, string configPath) : IAsyncDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _shutdown = new();

    // Program 靠这个 token 等待 shutdown 命令，避免自己造一个永不返回的 Delay
    internal CancellationToken ShutdownToken => _shutdown.Token;

    internal void Start()
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));

        // Bind 只是建了 socket 文件，必须 Listen 才算进入监听状态，否则客户端 Connect 直接 connection refused。
        _listener.Listen(512);

        // socket 由 root 进程创建。GUI 是普通用户，而 connect() 需要对 socket 文件有写权限，
        // 所以 root:root 0600 的 socket 普通用户连不上（EACCES）—— 表现就是 agent 日志里明明
        // 写着 listening，GUI 却一直超时。把属主改成发起授权的那个 uid，保持 0600：
        // 只有该用户能连，其他本地用户仍然连不上。
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Chown(socketPath, ownerUid);

        AgentLog.Info($"listening on {socketPath} (owner uid {ownerUid})");

        _ = Task.Run(AcceptLoopAsync);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint owner, uint group);

    private static void Chown(string path, int ownerUid)
    {
        if (OperatingSystem.IsWindows() || ownerUid <= 0)
            return;

        // group 传 (uint)-1 表示保持不变
        chown(path, (uint)ownerUid, unchecked((uint)-1));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
            try
            {
                Socket connection = await _listener.AcceptAsync(_shutdown.Token).ConfigureAwait(false);

                _ = Task.Run(() => HandleAsync(connection));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"accept failed: {ex.Message}");
            }
    }

    private async Task HandleAsync(Socket connection)
    {
        try
        {
            using (connection)
            using (NetworkStream stream = new(connection, ownsSocket: false))
            using (StreamReader reader = new(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);

                if (line is null)
                    return;

                AgentRequest? request = AgentJson.Deserialize<AgentRequest>(line);

                if (request is null)
                {
                    await WriteAsync(writer, new AgentResponse { Ok = false, Error = "malformed request" }).ConfigureAwait(false);
                    return;
                }

                AgentResponse response = await DispatchAsync(request).ConfigureAwait(false);

                await WriteAsync(writer, response).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"connection failed: {ex.Message}");
        }
    }

    private async Task<AgentResponse> DispatchAsync(AgentRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case AgentCommand.Ping:
                    return new AgentResponse { Ok = true, Status = supervisor.Status };

                case AgentCommand.Status:
                    return new AgentResponse { Ok = true, Status = supervisor.Status };

                case AgentCommand.Start:
                case AgentCommand.Reload:
                {
                    if (!File.Exists(configPath))
                        return new AgentResponse { Ok = false, Error = $"config not found: {configPath}" };

                    ProxyConfig? config = AgentJson.Deserialize<ProxyConfig>(await File.ReadAllTextAsync(configPath).ConfigureAwait(false));

                    if (config is null)
                        return new AgentResponse { Ok = false, Error = "malformed config" };

                    await supervisor.StartAsync(config, _shutdown.Token).ConfigureAwait(false);

                    return new AgentResponse { Ok = true, Status = supervisor.Status };
                }

                case AgentCommand.Stop:
                case AgentCommand.Cleanup:
                    await supervisor.StopAsync().ConfigureAwait(false);

                    return new AgentResponse { Ok = true, Status = supervisor.Status };

                case AgentCommand.Shutdown:
                    await supervisor.StopAsync().ConfigureAwait(false);
                    await _shutdown.CancelAsync().ConfigureAwait(false);

                    return new AgentResponse { Ok = true, Status = supervisor.Status };

                default:
                    return new AgentResponse { Ok = false, Error = $"unknown command: {request.Command}" };
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error($"{request.Command} failed: {ex}");

            return new AgentResponse { Ok = false, Error = ex.Message };
        }
    }

    private static Task WriteAsync(StreamWriter writer, AgentResponse response) => writer.WriteLineAsync(AgentJson.Serialize(response));

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();

        try
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
        catch { }
    }
}
```

**Unix domain socket 是 v1 刻意只在 macOS/Linux 上做的原因。** Windows 需要命名管道，是完全不同的实现（`NamedPipeServerStream` + `PipeOptions.CurrentUserOnly`），本计划不覆盖 —— Windows 走原有路径。

- [ ] **Step 5: 写入口**

`Cealing-Agent/Program.cs`（**整体替换 Task 3 Step 1 追加的占位 `Program.cs`**）：

```csharp
using Cealing_Core;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? socketPath = ArgValue(args, "--socket");
        string? configPath = ArgValue(args, "--config");
        string? appDir = ArgValue(args, "--app-dir");
        string? ownerUidRaw = ArgValue(args, "--owner-uid");

        if (socketPath is null || configPath is null || appDir is null || ownerUidRaw is null)
        {
            Console.Error.WriteLine("usage: Cealing-Agent --socket <path> --config <path> --app-dir <dir> --owner-uid <uid>");
            return 2;
        }

        if (!int.TryParse(ownerUidRaw, out int ownerUid))
        {
            Console.Error.WriteLine($"invalid --owner-uid: {ownerUidRaw}");
            return 2;
        }

        AgentLog.Init(AgentPaths.LogPath(appDir));

        try
        {
            // root 自己跑 GUI 的老用法（Windows 也是这个形态）：已经是特权进程，直接走前台服务。
            // 后台化由 Task 8 的 osascript/pkexec 那侧用 nohup + fd 重定向完成，这里不关心。
            await RunAsync(socketPath, ownerUid, configPath, appDir).ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"fatal: {ex}");
            return 1;
        }
    }

    private static async Task RunAsync(string socketPath, int ownerUid, string configPath, string appDir)
    {
        // /etc/hosts 在 macOS 和 Linux 上同路径；Windows 将来要支持需另加 System32\drivers\etc\hosts
        const string hostsPath = "/etc/hosts";

        EngineSupervisor supervisor = new(appDir, hostsPath, AgentPaths.LogPath(appDir));

        // pid 文件只是排障线索，不是必需品：写失败（例如上一次 root 运行留下的同名文件、
        // 或临时目录权限问题）绝不能拖垮 agent，所以这里 best-effort。
        try
        {
            await File.WriteAllTextAsync(AgentPaths.PidPath, Environment.ProcessId.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"could not write pid file {AgentPaths.PidPath}: {ex.Message}");
        }

        await using AgentServer server = new(socketPath, ownerUid, supervisor, configPath);

        server.Start();

        // 阻塞到收到 Shutdown 命令或进程被杀。Task.DetachAsync() 在 .NET 8 并不存在，
        // 真正让 osascript 不阻塞的是 Task 8 里的 shell 侧重定向（nohup ... </dev/null >log 2>&1 &），
        // 这里的 fds 由提权那侧负责，这里只管等 token。
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, server.ShutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, arg => arg.Equals(name, StringComparison.Ordinal));

        return index != -1 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
```

**daemonize 说明：** .NET 8 没有内置 daemonize API，这里也不需要 —— 提权那侧（Task 8）用 `( nohup ... </dev/null >log 2>&1 & )` 把 fds 全部重定向，`osascript` 就能立刻返回。**真正让 `osascript` 不阻塞的是 shell 那侧的 fd 重定向，不是 C# 这侧。** 直连调试时在终端前台跑 `dotnet run --project Cealing-Agent -- ...` 即可，走的就是同一个 `RunAsync`。

- [ ] **Step 6: 写失败的测试**

`Sheas-Cealer-Nix.Tests/AgentServerTests.cs`：

```csharp
using Cealing_Agent;
using Cealing_Core;
using Cealing_Core.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class AgentServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _socket = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".sock");
    private readonly string _config = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (string path in new[] { _socket, _config })
            if (File.Exists(path))
                File.Delete(path);

        Directory.Delete(_dir, recursive: true);

        return Task.CompletedTask;
    }

    private async Task<AgentResponse> SendAsync(AgentRequest request)
    {
        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(_socket));

        await using NetworkStream stream = new(client);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync(AgentJson.Serialize(request));

        string? line = await reader.ReadLineAsync();

        return AgentJson.Deserialize<AgentResponse>(line!)!;
    }

    [Fact]
    public async Task PingReportsNotRunning()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = AgentCommand.Ping });

        Assert.True(response.Ok);
        AgentStatus? status = response.Status;

        Assert.NotNull(status);
        Assert.False(status.Running);
    }

    [Fact]
    public async Task UnknownCommandIsRejected()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = "nope" });

        Assert.False(response.Ok);
        Assert.Equal("unknown command: nope", response.Error);
    }

    [Fact]
    public async Task MalformedJsonIsRejected()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(_socket));

        await using NetworkStream stream = new(client);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync("{not json");

        AgentResponse? response = AgentJson.Deserialize<AgentResponse>((await reader.ReadLineAsync())!);

        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Equal("malformed request", response.Error);
    }

    [Fact]
    public async Task StartWithoutConfigFileIsRejected()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        AgentResponse response = await SendAsync(new AgentRequest { Command = AgentCommand.Start });

        Assert.False(response.Ok);
        Assert.Contains("config not found", response.Error);
    }

    [Fact]
    public async Task SocketIsOwnerOnly()
    {
        await using AgentServerFixture fixture = await AgentServerFixture.StartAsync(_socket, _config, _dir);

        UnixFileMode mode = File.GetUnixFileMode(_socket);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
```

再写 fixture（同文件内追加）：

```csharp
internal sealed class AgentServerFixture : IAsyncDisposable
{
    private readonly AgentServer _server;

    private AgentServerFixture(AgentServer server) => _server = server;

    public static async Task<AgentServerFixture> StartAsync(string socketPath, string configPath, string appDir)
    {
        EngineSupervisor supervisor = new(appDir, Path.Combine(appDir, "hosts-test"), Path.Combine(appDir, "error.log"));

        // ownerUid 在 v1 只写日志不参与鉴权，测试里传 0 即可（不要用 Environment.Getuid，net8.0 无此 API）
        AgentServer server = new(socketPath, 0, supervisor, configPath);

        server.Start();

        // 等 socket 真正可连
        for (int i = 0; i < 50; i++)
            try
            {
                using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await probe.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));

                return new AgentServerFixture(server);
            }
            catch (SocketException)
            {
                await Task.Delay(20);
            }

        throw new TimeoutException("agent server socket never became connectable");
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
```

- [ ] **Step 7: 跑测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: `Failed: 0, Passed: 25`（Task 4 结束时的 20 + 本任务 5 个 AgentServerTests）

**如果 `SocketIsOwnerOnly` 挂了**，检查 `AgentServer.Start()` 里的 `File.SetUnixFileMode` 是否在 `Bind` 之后 —— 必须在之后，否则会被 `Bind` 覆盖。

- [ ] **Step 8: 提交**

```bash
git add Cealing-Agent Sheas-Cealer-Nix.Tests
git commit -m "feat: add agent socket server and engine supervisor"
```

**已知简化（写进代码注释，不要忘）：** `AgentServer` 收了 `ownerUid` 参数但 v1 **没有做 peer credential 校验**。socket 文件 0600 已经挡住其他用户，agent 本身又是 root 且只接受本机连接，风险可控。真正的 uid 校验（Linux `SO_PEERCRED` / macOS `LOCAL_PEERCRED`）留到 v2 —— 见「后续」。

---

## Task 8: GUI 侧客户端与提权启动

**Files:**
- Create: `Sheas-Cealer-Nix/Utils/AgentClient.cs`
- Create: `Sheas-Cealer-Nix/Utils/PrivilegeEscalator.cs`
- Modify: `Sheas-Cealer-Nix/Consts/MainConst.cs`（加 `AgentBinaryPath` / `ProxyConfigPath` / `AgentLogPath` / `AgentLaunchError`）
- Modify: `Sheas-Cealer-Nix/Sheas-Cealer-Nix.csproj`（引用 Cealing-Core）
- Test: `Sheas-Cealer-Nix.Tests/PrivilegeEscalatorCommandTests.cs`

- [ ] **Step 1: 写提权命令构造器**

`Sheas-Cealer-Nix/Utils/PrivilegeEscalator.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sheas_Cealer_Nix.Utils;

internal static class PrivilegeEscalator
{
    internal static string CurrentPlatform => OperatingSystem.IsMacOS() ? "macos" : "linux";

    internal static string CurrentUid
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return "0";

            return Environment.GetEnvironmentVariable("UID") is { Length: > 0 } uid ? uid : GetUidViaId();
        }
    }

    // osascript / pkexec 最终都要把 agent 放到后台，否则授权进程会一直挂着。
    // command 是给 shell 执行的那条命令：三个 fd 全重定向 + 结尾 & 让 shell/授权框立刻返回。
    //
    // 不要用 nohup：osascript 的 `do shell script` 没有控制终端，BSD nohup 会在
    // ioctl(TIOCNOTTY) 失败时直接报 "nohup: can't detach from console: Inappropriate ioctl for device"
    // 并放弃执行命令，agent 根本没起来（实测到的 bug）。没有控制终端就没有 SIGHUP 来源，
    // `&` + fd 重定向足以让 root agent 在 shell/osascript 退出后继续存活。
    private static string BackgroundCommand(string agentPath, string socketPath, string configPath, string appDir, string uid, string logPath) =>
        string.Join(' ',
            ShellQuote(agentPath),
            "--socket", ShellQuote(socketPath),
            "--config", ShellQuote(configPath),
            "--app-dir", ShellQuote(appDir),
            "--owner-uid", ShellQuote(uid),
            $">{ShellQuote(logPath)} 2>&1 </dev/null &");

    private static string AppleScriptEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    internal static IReadOnlyList<string> BuildLaunchArgs(string agentPath, string socketPath, string configPath, string appDir, string uid, string logPath, string platform)
    {
        return platform switch
        {
            // osascript 的 do shell script 内部就经过 /bin/sh，所以 command 里结尾的 & 直接生效，
            // 授权框点完密码后命令立刻返回。只用 AppleScriptEscape 处理双引号和反斜杠，不要再嵌套 sh -c。
            "macos" => ["osascript", "-e", $"do shell script \"{AppleScriptEscape(BackgroundCommand(agentPath, socketPath, configPath, appDir, uid, logPath))}\" with administrator privileges"],
            // pkexec 无 shell，必须显式 sh -c 才能吃到 & 的语义。
            "linux" => ["pkexec", "sh", "-c", BackgroundCommand(agentPath, socketPath, configPath, appDir, uid, logPath)],
            _ => throw new PlatformNotSupportedException($"unsupported platform: {platform}")
        };
    }

    internal static bool IsElevated => OperatingSystem.IsWindows()
        ? new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
        : Environment.UserName == "root";

    // 已经是 root（老用法 sudo 跑 GUI）时不需要提权，但也必须 nohup 后台化，
    // 否则 Launch 里的 WaitForExit(120_000) 会一直挂到 agent 退出，EnsureAgentAsync 永远等不到 socket。
    internal static IReadOnlyList<string> BuildDirectArgs(string agentPath, string socketPath, string configPath, string appDir, string uid, string logPath) =>
        ["sh", "-c", BackgroundCommand(agentPath, socketPath, configPath, appDir, uid, logPath)];

    internal static void Launch(string agentPath, string socketPath, string configPath, string appDir, string uid, string logPath)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        IReadOnlyList<string> args = IsElevated
            ? BuildDirectArgs(agentPath, socketPath, configPath, appDir, uid, logPath)
            : BuildLaunchArgs(agentPath, socketPath, configPath, appDir, uid, logPath, CurrentPlatform);

        ProcessStartInfo startInfo = new(args[0]) { UseShellExecute = false };

        foreach (string arg in args.Skip(1))
            startInfo.ArgumentList.Add(arg);

        using Process? process = Process.Start(startInfo);

        process?.WaitForExit(120_000);
    }

    private static string GetUidViaId()
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("id", "-u") { UseShellExecute = false, RedirectStandardOutput = true })!;

            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            return "0";
        }
    }
}
```

**关键点：Linux 用 `pkexec` 而不是 `sudo`。** `sudo` 在 GUI 进程里拿不到 tty 去读密码；`pkexec` 会弹自己的图形授权框，行为和 macOS 的 `osascript` 一致，也和我们要对齐的 Windows UAC 心智一致。

**关于「一次授权」的机制：** 授权只发生在 `Launch` 这一次。agent 起来之后所有 start/stop/reload 都走 socket，不再碰任何提权路径。所以「弹几次框」= 「agent 挂了几次」。

- [ ] **Step 2: 写客户端**

`Sheas-Cealer-Nix/Utils/AgentClient.cs`：

```csharp
using Cealing_Core;
using Cealing_Core.Protocol;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

internal sealed class AgentClient(string socketPath)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    internal string SocketPath { get; } = socketPath;

    internal bool IsAlive => File.Exists(SocketPath);

    internal async Task<AgentResponse> SendAsync(AgentRequest request, TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = new(timeout ?? DefaultTimeout);
        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        await client.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cts.Token).ConfigureAwait(false);

        await using NetworkStream stream = new(client);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync(AgentJson.Serialize(request)).ConfigureAwait(false);

        using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        string? line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);

        return line is null
            ? new AgentResponse { Ok = false, Error = "agent closed the connection" }
            : AgentJson.Deserialize<AgentResponse>(line) ?? new AgentResponse { Ok = false, Error = "malformed agent response" };
    }

    internal Task<AgentResponse> StatusAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Status });
    internal Task<AgentResponse> PingAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Ping });
    internal Task<AgentResponse> StartAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Start }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> ReloadAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Reload }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> StopAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Stop }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> CleanupAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Cleanup }, TimeSpan.FromSeconds(30));
    internal Task<AgentResponse> ShutdownAsync() => SendAsync(new AgentRequest { Command = AgentCommand.Shutdown }, TimeSpan.FromSeconds(30));
}
```

- [ ] **Step 3: 加路径常量**

`Consts/MainConst.cs`，在 `NginxConfPath` 附近加：

```csharp
    internal static string AgentBinaryPath => BinPath("Cealing-Agent");
    internal static string ProxyConfigPath => Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, "cealing-proxy.json");
    internal static string AgentLogPath => Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, "cealing-agent.log");

    // 提权失败时由 ProxyController.EnsureAgentAsync 写入，Task 10 弹窗时读它。
    // 没有这一条，EnsureAgentAsync 里的赋值无处安放，编译不过。
    internal static string AgentLaunchError { get; set; } = string.Empty;
```

`BinPath` 已经是 `private static`（`Consts/MainConst.cs:64`），在同一类里可以直接调。

- [ ] **Step 4: 引用 Cealing-Core**

`Sheas-Cealer-Nix.csproj`，在 `<ItemGroup>` 里加：

```xml
  <ItemGroup>
    <!-- GUI 项目就在仓库根目录，Cealing-Core 是它的子目录，所以是 "Cealing-Core\..."，没有 ".." -->
    <ProjectReference Include="Cealing-Core\Cealing-Core.csproj" />
  </ItemGroup>
```

> ⚠️ **路径里没有 `..`。** 本仓库 GUI 项目（`Sheas-Cealer-Nix.csproj`）就在仓库根目录，`Cealing-Core/` 是根目录下的子目录。计划里凡是写 `Sheas-Cealer-Nix/Utils/...` 的地方，实际都对应根目录的 `Utils/...`；写成 `..\Cealing-Core\...` 会去仓库外面找，restore 直接报「未找到该项目」，然后 `using Cealing_Core;` 全部 `CS0246`。

> ⚠️ **必须同时排除兄弟项目目录，否则 GUI 项目 C# 编译直接崩。** SDK 默认 glob 是 `**\*.cs`，`DefaultItemExcludes` 只排掉本项目自己的 `bin/**;obj/**`，**不排除**同目录下的 `Cealing-Core` / `Cealing-Agent` / `Sheas-Cealer-Nix.Tests`。三个兄弟项目一 build，它们 `obj/` 里生成的 `AssemblyInfo.cs` / `.AssemblyAttributes.cs` 就会全部被编进 GUI 项目，报一堆 `CS0579: 特性重复`；测试项目的 obj 里 xunit 生成的 `Sheas-Cealer-Nix.Tests.AssemblyInfo.cs` 还带 `[Xunit.CollectionBehavior]`，报 `CS0246: 找不到 Xunit`。所以 Task 5 建出第一个兄弟项目时就补上了这段（已做在 Task 5）：
>
> ```xml
>   <ItemGroup>
>     <Compile Remove="bin\**" />
>     <EmbeddedResource Remove="bin\**" />
>     <None Remove="bin\**" />
>     <Page Remove="bin\**" />
>     <Compile Remove="obj\**" />
>     <EmbeddedResource Remove="obj\**" />
>     <None Remove="obj\**" />
>     <Page Remove="obj\**" />
>     <Compile Remove="Cealing-Core\**" />
>     <Compile Remove="Cealing-Agent\**" />
>     <Compile Remove="Sheas-Cealer-Nix.Tests\**" />
>   </ItemGroup>
> ```
>
> 验证：`dotnet build Sheas-Cealer-Nix.csproj` 里 CS 错误应该只剩 `BuiltinNginx`/`BuiltinNginxRule`（Task 9 Step 4 删掉）。

并在属性组加（测试要访问 `internal` 的 `AgentClient`）：

```xml
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <!-- 必须是程序集简单名（和 AssemblyName 一致，带连字符），不是命名空间的下划线形式 -->
      <_Parameter1>Sheas-Cealer-Nix.Tests</_Parameter1>
    </AssemblyAttribute>
```

> ⚠️ **测试项目不能 `ProjectReference` GUI 项目。** 因为 `Themes/Button.axaml` 的既有 XAML 错误（Task 0 实测）让 GUI 项目根本产不出 DLL，一旦引用它整个测试套件（含 Task 1-7 的 25 个测试）都构建不了。所以 `PrivilegeEscalatorCommandTests` 改成把纯 BCL 的 `Utils/PrivilegeEscalator.cs` 直接编进测试程序集：
>
> ```xml
>   <ItemGroup>
>     <Compile Include="..\Utils\PrivilegeEscalator.cs" Link="GuiSource\PrivilegeEscalator.cs" />
>   </ItemGroup>
> ```
>
> 等 `Themes/` 被修好、GUI 项目能构建后，可以再换成 `ProjectReference` 并把这行删掉（`InternalsVisibleTo` 就是为那天准备的）。

- [ ] **Step 5: 写失败的测试**

`Sheas-Cealer-Nix.Tests/PrivilegeEscalatorCommandTests.cs`：

```csharp
using Sheas_Cealer_Nix.Utils;
using System.Linq;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class PrivilegeEscalatorCommandTests
{
    [Fact]
    public void MacLaunchUsesOsascriptWithAdminPrivileges()
    {
        string[] args = [.. PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "501", "/app/a.log", "macos")];

        Assert.Equal("osascript", args[0]);
        Assert.Equal("-e", args[1]);
        Assert.Contains("with administrator privileges", args[2]);
        Assert.Contains("/app/Cealing-Agent", args[2]);
    }

    [Fact]
    public void MacLaunchRedirectsAllFileDescriptorsAndBackgrounds()
    {
        string script = PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "501", "/app/a.log", "macos")[2];

        // 后台化靠 sh 的 & + fd 重定向；不能用 nohup（osascript 无控制终端，BSD nohup 会失败）。
        Assert.Contains("&", script);
        Assert.Contains("2>&1", script);
        Assert.Contains("</dev/null", script);
        Assert.DoesNotContain("nohup", script);
    }

    [Fact]
    public void MacLaunchQuotesPathsContainingSpaces()
    {
        string script = PrivilegeEscalator.BuildLaunchArgs("/Applications/My App/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "501", "/app/a.log", "macos")[2];

        // 路径带空格必须整个保留在一个 shell 单引号 token 里，否则会被 sh 拆成两个参数。
        Assert.Contains("'/Applications/My App/Cealing-Agent'", script);
        Assert.Contains("--socket", script);
    }

    [Fact]
    public void MacLaunchEscapesDoubleQuotesForAppleScript()
    {
        string script = PrivilegeEscalator.BuildLaunchArgs("/app/We\"ird", "/tmp/a.sock", "/app/c.json", "/app", "501", "/app/a.log", "macos")[2];

        Assert.DoesNotContain("do shell script \"do shell", script);
        Assert.Contains("\\\"", script);
    }

    [Fact]
    public void LinuxLaunchUsesPkexecWithBackgroundedShell()
    {
        string[] args = [.. PrivilegeEscalator.BuildLaunchArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "1000", "/app/a.log", "linux")];

        Assert.Equal("pkexec", args[0]);
        Assert.Equal("sh", args[1]);
        Assert.Equal("-c", args[2]);
        string script = args[3];
        Assert.Contains("/app/Cealing-Agent", script);
        Assert.Contains("&", script);
        Assert.Contains("2>&1", script);
        Assert.Contains("</dev/null", script);
        Assert.DoesNotContain("nohup", script);
    }

    [Fact]
    public void DirectArgsSkipEscalation()
    {
        string[] args = [.. PrivilegeEscalator.BuildDirectArgs("/app/Cealing-Agent", "/tmp/a.sock", "/app/c.json", "/app", "0", "/app/a.log")];

        Assert.DoesNotContain("osascript", args);
        Assert.DoesNotContain("pkexec", args);
        string script = args[^1];
        Assert.Contains("/app/Cealing-Agent", script);
        Assert.Contains("&", script);
        Assert.DoesNotContain("nohup", script);
    }
}
```

这需要测试项目引用 GUI 项目，会引入 Avalonia 依赖。**在 `Sheas-Cealer-Nix.Tests.csproj` 的 `<ItemGroup>` 加**：

```xml
    <ProjectReference Include="..\Sheas-Cealer-Nix\Sheas-Cealer-Nix.csproj" />
```

> ⚠️ 引用 GUI 项目会连带编译 XAML，而 `Themes/Button.axaml` 当前是坏的。**Task 8 Step 6 的测试可能因此无法运行。** 若如此，先把 `PrivilegeEscalator` 的命令构造逻辑临时挪到 `Cealing-Core` 里测（它不依赖 Avalonia），测完再挪回去。**不要**去修 `Themes/`。

- [ ] **Step 6: 跑测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: `Failed: 0, Passed: 31`（25 + 6 个 PrivilegeEscalatorCommandTests）

- [ ] **Step 7: 提交**

```bash
git add Sheas-Cealer-Nix/Utils/AgentClient.cs Sheas-Cealer-Nix/Utils/PrivilegeEscalator.cs Sheas-Cealer-Nix/Consts/MainConst.cs Sheas-Cealer-Nix/Sheas-Cealer-Nix.csproj Sheas-Cealer-Nix.Tests
git commit -m "feat: add agent client and privilege escalation for unix"
```

---

## Task 9: MainWin 改造 —— 走 agent

**Files:**
- Modify: `Sheas-Cealer-Nix/Wins/MainWin.axaml.cs`（大改）
- Modify: `Sheas-Cealer-Nix/Preses/MainPres.cs`（加 agent 状态属性）
- Modify: `Sheas-Cealer-Nix/Utils/NginxCleaner.cs`（重写）
- Delete: `Sheas-Cealer-Nix/Proces/NginxProc.cs`
- Delete: `Sheas-Cealer-Nix/Proces/ConginxProc.cs`
- Delete: `Sheas-Cealer-Nix/Proces/MihomoProc.cs`
- Delete: `Sheas-Cealer-Nix/Proces/ComihomoProc.cs`

**Why:** 本计划的核心。替换掉「GUI 自己 `Process.Start` + 在进程内跑 Kestrel」这套。

**先做一次结构性重构，把 `MainWin.axaml.cs` 从 1066 行里剥出代理控制逻辑**，否则改不动：

- [ ] **Step 1: 新建 `ProxyController`**

`Sheas-Cealer-Nix/Utils/ProxyController.cs`：

```csharp
using Cealing_Core;
using Cealing_Core.Protocol;
using Sheas_Cealer_Nix.Consts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

// 把「生成 ProxyConfig」和「跟 agent 说话」从窗口代码里抽出来。
// 规则拼装（正则、$ 前缀浏览器限定、# 禁用）保持和原来完全一致的语义。
internal sealed class ProxyController(
    Func<SortedDictionary<string, List<(List<(string include, string exclude)> pairs, string? sni, string ip)>?>> rulesProvider,
    Func<bool> flashingProvider,
    Func<ProxyEngineKind> engineProvider,
    Func<string?> nginxPathProvider)
{
    internal AgentClient Client { get; } = new(AgentPaths.SocketPath);

    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);

    internal async Task<bool> EnsureAgentAsync()
    {
        if (Client.IsAlive && await PingAsync() is { Ok: true })
            return true;

        MainConst.AgentLaunchError = string.Empty;

        if (!File.Exists(MainConst.AgentBinaryPath))
        {
            MainConst.AgentLaunchError = $"找不到 Cealing-Agent 可执行文件：{MainConst.AgentBinaryPath}";

            return false;
        }

        try
        {
            PrivilegeEscalator.Launch(
                MainConst.AgentBinaryPath,
                AgentPaths.SocketPath,
                MainConst.ProxyConfigPath,
                AppDomain.CurrentDomain.SetupInformation.ApplicationBase!,
                PrivilegeEscalator.CurrentUid,
                MainConst.AgentLogPath);
        }
        catch (Exception ex)
        {
            MainConst.AgentLaunchError = ex.Message;

            return false;
        }

        for (int i = 0; i < (int)StartupWait.TotalMilliseconds / 100; i++)
        {
            if (await PingAsync() is { Ok: true })
                return true;

            await Task.Delay(100);
        }

        MainConst.AgentLaunchError = BuildStartupFailureMessage();

        return false;
    }

    // 授权后 agent 仍没连上时，把 agent 日志最后几行带进弹窗，避免只看到一句笼统的「无法启动特权代理」。
    private static string BuildStartupFailureMessage()
    {
        string logPath = MainConst.AgentLogPath;

        try
        {
            if (File.Exists(logPath))
            {
                string tail = string.Join(Environment.NewLine, File.ReadAllLines(logPath).TakeLast(6)).Trim();

                if (tail.Length > 0)
                    return $"agent 未在超时前就绪（{logPath}）：{Environment.NewLine}{tail}";
            }
        }
        catch
        {
        }

        return $"agent 未在超时前就绪，请查看 {logPath}";
    }

    private async Task<AgentResponse?> PingAsync()
    {
        try
        {
            return await Client.PingAsync();
        }
        catch
        {
            return null;
        }
    }

    internal ProxyConfig BuildConfig(bool coproxy, bool writeHosts, int httpPort, int httpsPort, int mixedPort, string? nginxConfText, string? mihomoConfText)
    {
        ProxyConfig config = new()
        {
            Engine = engineProvider(),
            Coproxy = coproxy,
            Flashing = flashingProvider(),
            WriteHosts = writeHosts,
            HttpPort = httpPort,
            HttpsPort = httpsPort,
            MixedPort = mixedPort,
            NginxBinaryPath = nginxPathProvider(),
            NginxConfText = nginxConfText,
            MihomoBinaryPath = CoproxyMihomoPath,
            MihomoConfText = mihomoConfText,
            CertSans = [],
            Rules = []
        };

        // rulesProvider() 的每个 Value 是一个 List<tuple>（对应原 MainWin 的 CealHostRulesDict.Values
        // → 内层 foreach），这里必须 SelectMany 摊平，否则会把 List 当成 3 元组去解构，编译不过。
        // rulesProvider 本身非空（和原代码直接访问 CealHostRulesDict.Values 一致）。
        foreach ((List<(string include, string exclude)> pairs, string? sni, string ip) in
            rulesProvider().Values.Where(v => v is not null).SelectMany(v => v!))
        {
            string serverName = BuildServerName(pairs, out int appended);

            if (appended == 0)
                continue;

            foreach ((string include, _) in pairs)
                AddCertSan(config, include);

            config.Rules.Add(new ProxyRule
            {
                // 去掉 BuildServerName 前缀的 '~'（nginx 的正则标记）和结尾的 '|'。
                // 结尾的 '|' 在正则里是**空分支**，会让整条正则匹配任意字符串（永远命中的第 1 条规则），
                // 原 BuiltinNginx 直接 new Regex(serverName[1..]) 就带着这个空分支，是个隐藏 bug。
                ServerName = serverName[1..].TrimEnd('|'),
                Ip = ip,
                Sni = sni,
                SniEnabled = !flashingProvider() && sni is not null,
                Port = 443
            });
        }

        return config;
    }

    // 复刻 Wins/MainWin.axaml.cs:944-945 的拼法：~^exclude$domain$| 多段以 | 结尾
    internal static string BuildServerName(List<(string include, string exclude)> pairs, out int count)
    {
        System.Text.StringBuilder builder = new("~");

        count = 0;

        foreach ((string include, string exclude) in pairs)
        {
            if (include.StartsWith('#'))
                continue;

            builder.Append('^')
                   .Append(string.IsNullOrWhiteSpace(exclude) ? string.Empty : $"(?!{EscapeRegexLiteral(exclude)})")
                   .Append(EscapeRegexLiteral(include.TrimStart('$')))
                   .Append('$')
                   .Append('|');

            count++;
        }

        return count == 0 ? string.Empty : builder.ToString();
    }

    private static string EscapeRegexLiteral(string value) =>
        value.Replace(".", "\\.").Replace("*", ".*");

    // 复刻 Wins/MainWin.axaml.cs:301-321 的域名筛选与 SAN/hosts 推导
    internal static void AddCertSan(ProxyConfig config, string rawDomain)
    {
        string domain = rawDomain.TrimStart('$').TrimStart('*').TrimStart('.');

        if (rawDomain.StartsWith('#') || domain.Contains('*') || string.IsNullOrWhiteSpace(domain))
            return;

        if (rawDomain.TrimStart('$').StartsWith('*'))
        {
            config.CertSans.Add(new ProxyCertSan { Domain = domain, Wildcard = true });

            if (rawDomain.TrimStart('$').StartsWith("*."))
                return;
        }

        config.CertSans.Add(new ProxyCertSan { Domain = domain, Wildcard = false });
    }

    internal string? CoproxyMihomoPath { get; set; }

    internal async Task<AgentResponse> WriteAndStartAsync(ProxyConfig config)
    {
        await File.WriteAllTextAsync(MainConst.ProxyConfigPath, AgentJson.Serialize(config));

        return await Client.StartAsync();
    }

    internal async Task<AgentResponse> ReloadAsync(ProxyConfig config)
    {
        await File.WriteAllTextAsync(MainConst.ProxyConfigPath, AgentJson.Serialize(config));

        return await Client.ReloadAsync();
    }

    internal Task<AgentResponse> StopAsync() => Client.StopAsync();
    internal Task<AgentResponse> CleanupAsync() => Client.CleanupAsync();
    internal Task<AgentResponse> ShutdownAsync() => Client.ShutdownAsync();
    internal async Task<AgentStatus?> TryGetStatusAsync() => (await PingAsync())?.Status;
}
```

- [ ] **Step 2: 写失败的测试（规则拼装逻辑）**

`Sheas-Cealer-Nix.Tests/ProxyConfigBuilderTests.cs`：

```csharp
using Cealing_Core;
using Sheas_Cealer_Nix.Utils;
using System.Collections.Generic;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

public class ProxyConfigBuilderTests
{
    private static ProxyConfig NewConfig() => new();

    [Fact]
    public void BuildServerNameJoinsPairsWithPipe()
    {
        string serverName = ProxyController.BuildServerName([("a.com", ""), ("b.com", "")], out int count);

        Assert.Equal(2, count);
        Assert.StartsWith("~", serverName);
        Assert.EndsWith("|", serverName);
        Assert.Contains("^a\\.com$", serverName);
        Assert.Contains("^b\\.com$", serverName);
    }

    [Fact]
    public void BuildServerNameAddsNegativeLookaheadForExclude()
    {
        string serverName = ProxyController.BuildServerName([("*.pixiv.net", "i.pximg.net")], out _);

        Assert.Contains("(?!i\\.pximg\\.net)", serverName);
        Assert.Contains("\\.net$", serverName);
    }

    [Fact]
    public void BuildServerNameSkipsHashPrefixedDomains()
    {
        string serverName = ProxyController.BuildServerName([("#google.com", ""), ("ok.com", "")], out int count);

        Assert.Equal(1, count);
        Assert.DoesNotContain("google", serverName);
    }

    [Fact]
    public void BuildServerNameReturnsEmptyWhenNothingUsable()
    {
        Assert.Equal(string.Empty, ProxyController.BuildServerName([("#x", "")], out int count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void DollarPrefixIsBrowserOnlyAndStrippedFromRegex()
    {
        string serverName = ProxyController.BuildServerName([("$foo.com", "")], out _);

        Assert.Contains("^foo\\.com$", serverName);
        Assert.DoesNotContain("$foo", serverName);
    }

    [Fact]
    public void AddCertSanPlainDomain()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "cdn.jsdelivr.net");

        ProxyCertSan san = Assert.Single(config.CertSans);
        Assert.Equal("cdn.jsdelivr.net", san.Domain);
        Assert.False(san.Wildcard);
    }

    [Fact]
    public void AddCertSanWildcardDoesNotAddApex()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "*.fanbox.cc");

        ProxyCertSan san = Assert.Single(config.CertSans);
        Assert.Equal("fanbox.cc", san.Domain);
        Assert.True(san.Wildcard);
    }

    [Fact]
    public void AddCertSanBareWildcardAddsBoth()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "*example.org");

        Assert.Equal(2, config.CertSans.Count);
        Assert.True(config.CertSans[0].Wildcard);
        Assert.False(config.CertSans[1].Wildcard);
    }

    [Fact]
    public void AddCertSanSkipsUnusable()
    {
        ProxyConfig config = NewConfig();

        ProxyController.AddCertSan(config, "#off.com");
        ProxyController.AddCertSan(config, "  ");
        ProxyController.AddCertSan(config, "a*b.com");

        Assert.Empty(config.CertSans);
    }
}
```

- [ ] **Step 3: 跑测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj --filter "FullyQualifiedName~ProxyConfigBuilderTests"
```

Expected: `Failed: 0, Passed: 9`

- [ ] **Step 4: 改 `MainPres` 加 agent 状态**

`Preses/MainPres.cs` 追加：

```csharp
    [ObservableProperty]
    private bool isAgentReady = false;

    [ObservableProperty]
    private bool isAgentStarting = false;

    [ObservableProperty]
    private string agentError = string.Empty;

    // 原来靠 Process.GetProcessesByName 判断 nginx/mihomo 是否在跑（Preses/MainPres.cs:143-164）。
    // 现在进程是 agent 的子进程，GUI 拿不到句柄，改为轮询 agent 上报的状态。
    [ObservableProperty]
    private bool isProxyRunning = false;

    [ObservableProperty]
    private string proxyEngineName = "none";
```

并把 `:143`、`:146`、`:161`、`:164` 四个 `Is*Running` 的初始值改为 `false`（它们的语义现在由 `IsProxyRunning` + `ProxyEngineName` 承载，converter 改用新属性）。

- [ ] **Step 5: 改 `NginxCleaner` 为 agent 转发**

`Sheas-Cealer-Nix/Utils/NginxCleaner.cs` 整体替换：

```csharp
using Cealing_Core;
using Sheas_Cealer_Nix.Consts;
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

// 清理动作（hosts 标记块 + 根证书）在 unix 上现在只有 root 的 agent 能做，
// GUI 侧只转发指令；agent 不可达时静默返回 —— 启动时清理失败不该阻断启动。
// Windows 走原实现：UAC 模型下 GUI 自己就是管理员，不需要 agent。
internal static class NginxCleaner
{
    internal static async Task Clean()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                await new AgentClient(AgentPaths.SocketPath).CleanupAsync();
            }
            catch
            {
            }

            return;
        }

        string hostsContent = await File.ReadAllTextAsync(MainConst.HostsConfPath);
        int hostsConfStartIndex = hostsContent.IndexOf(MainConst.HostsConfStartMarker, StringComparison.Ordinal);
        int hostsConfEndIndex = hostsContent.LastIndexOf(MainConst.HostsConfEndMarker, StringComparison.Ordinal);

        if (hostsConfStartIndex != -1 && hostsConfEndIndex != -1)
            await File.WriteAllTextAsync(MainConst.HostsConfPath, hostsContent.Remove(hostsConfStartIndex, hostsConfEndIndex - hostsConfStartIndex + MainConst.HostsConfEndMarker.Length));

        using X509Store certStore = new(StoreName.Root, StoreLocation.LocalMachine, OpenFlags.ReadWrite);

        foreach (X509Certificate2 storedCert in certStore.Certificates)
            if (storedCert.Subject == MainConst.NginxRootCertSubjectName)
                while (true)
                    try
                    {
                        certStore.Remove(storedCert);

                        break;
                    }
                    catch { }

        certStore.Close();
    }
}
```

Windows 分支是**原样保留**的现有实现（读 `MainConst.HostsConfPath`、按 marker 删块、从 `StoreName.Root` / `StoreLocation.LocalMachine` 移除 `CN=Cealing Cert Root`），不是占位。Windows 侧的清理路径与本计划无关，不要在这里做任何改动或"顺手统一"。

- [ ] **Step 6: 删掉四个进程包装类**

```bash
git rm Sheas-Cealer-Nix/Proces/NginxProc.cs Sheas-Cealer-Nix/Proces/ConginxProc.cs Sheas-Cealer-Nix/Proces/MihomoProc.cs Sheas-Cealer-Nix/Proces/ComihomoProc.cs
```

保留 `Proces/BrowserProc.cs` —— 浏览器启动仍是 GUI 直接做（`:237`），跟 agent 无关。

- [ ] **Step 7: 改 `MainWin.axaml.cs` 的启动流程**

`Wins/MainWin.axaml.cs:81-107` 的 `MainWin_Loaded`，把 `:99-100` 的

```csharp
            if (MainConst.IsAdmin && !MainPres.IsConginxRunning && !MainPres.IsNginxRunning)
                await NginxCleaner.Clean();
```

替换为

```csharp
            ProxyController = new ProxyController(
                () => CealHostRulesDict,
                () => MainPres.IsFlashing,
                () => MainPres.IsBuiltinProxyEngine ? ProxyEngineKind.Builtin : ProxyEngineKind.External,
                () => MainPres.ResolvedNginxPath);

            MainPres.IsAgentReady = await ProxyController.EnsureAgentAsync();

            if (MainPres.IsAgentReady)
                await NginxCleaner.Clean();
```

并在字段区加：

```csharp
    private ProxyController? ProxyController;
```

- [ ] **Step 8: 改 `MainWin_Closing`**

`Wins/MainWin.axaml.cs:108-124`，把 `:115-121` 的进程内停止替换为：

```csharp
        if (ProxyController is not null && MainPres.IsAgentReady)
            try
            {
                await ProxyController.ShutdownAsync();
            }
            catch
            {
            }
```

`Environment.Exit(0)`（`:123`）保留。

- [ ] **Step 9: 替换 `NginxButtonHoldTimer_Tick` 的启动分支**

`Wins/MainWin.axaml.cs:240-402` 这一整段（从 `if (!MainPres.IsConginxRunning && !MainPres.IsNginxRunning)` 到 `finally { IsNginxLaunchingSemaphore.Release(); }`）替换为：

```csharp
        if (MainPres.IsProxyRunning)
        {
            AgentResponse stopResponse = await ProxyController!.StopAsync();

            if (!stopResponse.Ok)
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, stopResponse.Error ?? MainConst._NginxEngineMissingPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);

            MainPres.IsProxyRunning = false;
            MainPres.ProxyEngineName = "none";

            return;
        }

        if (ProxyController is null || !await ProxyController.EnsureAgentAsync())
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty,
                MainConst.AgentLaunchError.Length > 0 ? MainConst.AgentLaunchError : MainConst._AgentUnavailablePrompt,
                ButtonEnum.Ok).ShowWindowDialogAsync(this);

            return;
        }

        bool isCoproxy = sender == null;
        bool writeHosts = sender != null;
        bool isBuiltin = MainPres.IsBuiltinProxyEngine;
        string? externalNginxPath = isBuiltin ? null : NginxFinder.Find(isCoproxy);

        if (!isBuiltin && externalNginxPath is null)
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._NginxEngineMissingPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);
            return;
        }

        if ((CealHostRulesDict.ContainsValue(null!) && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._CealHostErrorPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (NginxHttpsPort != 443 && await MessageBoxManager.GetMessageBoxStandard(string.Empty, string.Format(MainConst._NginxHttpsPortOccupiedPrompt, NginxHttpsPort), ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (NginxHttpPort != 80 && await MessageBoxManager.GetMessageBoxStandard(string.Empty, string.Format(MainConst._NginxHttpPortOccupiedPrompt, NginxHttpPort), ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (writeHosts && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchHostsNginxPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchProxyPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes) ||
            (MainPres.IsFlashing && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchNginxFlashingPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes))
            return;

        if (!File.Exists(MainConst.NginxConfPath))
            await File.Create(MainConst.NginxConfPath).DisposeAsync();
        if (!Directory.Exists(MainConst.NginxLogsPath))
            Directory.CreateDirectory(MainConst.NginxLogsPath);
        if (!Directory.Exists(MainConst.NginxTempPath))
            Directory.CreateDirectory(MainConst.NginxTempPath);

        // 只有用应用自带的 nginx 时才需要把主程序挪成 coproxy 的文件名；
        // 走系统 nginx 时应用目录里可能根本没有这个文件，搬运会直接抛异常。
        if (isCoproxy && !isBuiltin && NginxFinder.IsBundled(externalNginxPath!, true) && !File.Exists(MainConst.ConginxPath))
            File.Move(MainConst.NginxPath, MainConst.ConginxPath);

        if (isCoproxy)
            MainPres.IsCoproxyIniting = true;
        else
            MainPres.IsNginxIniting = true;

        NginxConfWatcher.EnableRaisingEvents = false;

        try
        {
            ProxyConfig config = ProxyController.BuildConfig(
                coproxy: isCoproxy,
                writeHosts: writeHosts,
                httpPort: NginxHttpPort,
                httpsPort: NginxHttpsPort,
                mixedPort: MihomoMixedPort,
                nginxConfText: NginxConfs?.ToString(),
                mihomoConfText: isCoproxy ? ComihomoConfs : null);

            config.NginxBinaryPath = externalNginxPath;
            config.MihomoBinaryPath = isCoproxy ? MainConst.ComihomoPath : MainConst.MihomoPath;
            config.MihomoConfText = isCoproxy ? ComihomoConfs : MihomoConfs;

            AgentResponse response = await ProxyController.WriteAndStartAsync(config);

            if (!response.Ok)
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, response.Error ?? MainConst._LaunchNginxErrorPrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);

                return;
            }

            MainPres.IsProxyRunning = true;
            MainPres.ProxyEngineName = isCoproxy ? "mihomo" : config.Engine.ToString().ToLowerInvariant();

            if (isCoproxy)
                MainPres.IsConginxRunning = true;
            else
                MainPres.IsNginxRunning = true;
        }
        finally
        {
            if (!isCoproxy)
                MainPres.IsNginxIniting = false;
            else
                MainPres.IsCoproxyIniting = false;

            if (!isBuiltin)
                await File.WriteAllTextAsync(MainConst.NginxConfPath, ExtraNginxConfs ?? string.Empty);

            NginxConfWatcher.EnableRaisingEvents = true;
        }
```

- [ ] **Step 10: 改 `NginxConfWatcher_Changed` 的早退**

`Wins/MainWin.axaml.cs:884-885`：

```csharp
        if (!MainConst.IsAdmin || (!MainPres.IsNginxExist && !MainPres.IsConginxExist))
            return;
```

改为：

```csharp
        if (OperatingSystem.IsWindows() ? !MainConst.IsAdmin : !MainPres.IsAgentReady)
            return;

        if (!MainPres.IsNginxExist && !MainPres.IsConginxExist)
            return;
```

`:919` 的 `BuiltinNginxRules.Clear()` 改为 `BuiltinNginxRules.Clear();  // 规则已由 ProxyController 生成并写入配置文件`

`:953-959` 那段往 `BuiltinNginxRules` 里塞对象的代码删掉（规则生成已移到 `ProxyController.BuildConfig`）。

`:961-971` 生成 nginx `server{}` 块的代码**保留** —— `ExternalNginxEngine` 仍然吃 `nginx.conf` 文本。

- [ ] **Step 11: 改 `MihomoConfWatcher_Changed` 的早退**

`Wins/MainWin.axaml.cs:976-977` 同 Step 10 改成 `if (OperatingSystem.IsWindows() ? !MainConst.IsAdmin : !MainPres.IsAgentReady) return;`

- [ ] **Step 12: 改 `MihomoButtonHoldTimer_Tick`**

`Wins/MainWin.axaml.cs:495-` 整个方法替换为：

```csharp
    private async void MihomoButtonHoldTimer_Tick(object? sender, EventArgs e)
    {
        HoldButtonTimer?.Stop();

        if (MainPres.IsMihomoRunning || MainPres.IsComihomoRunning)
        {
            if (ProxyController is null || !MainPres.IsAgentReady)
                return;

            await ProxyController.StopAsync();

            MainPres.IsMihomoRunning = MainPres.IsComihomoRunning = false;

            if (MainPres.IsConginxRunning)
            {
                MainPres.IsConginxRunning = false;

                if (File.Exists(MainConst.ConginxPath))
                    File.Move(MainConst.ConginxPath, MainConst.NginxPath);
            }

            MainPres.IsProxyRunning = false;
            MainPres.ProxyEngineName = "none";

            return;
        }

        if (ProxyController is null || !await ProxyController.EnsureAgentAsync())
        {
            await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._AgentUnavailablePrompt, ButtonEnum.Ok).ShowWindowDialogAsync(this);

            return;
        }

        if (!MainPres.IsMihomoExist && !MainPres.IsComihomoExist)
            return;

        if (!MainPres.IsConginxRunning && !MainPres.IsCoproxyStopping && await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._LaunchProxyPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) != ButtonResult.Yes)
            return;

        if (MainPres.IsMihomoIniting || MainPres.IsComihomoIniting)
            return;

        MainPres.IsMihomoIniting = true;
        MihomoConfWatcher.EnableRaisingEvents = false;

        try
        {
            ProxyConfig config = ProxyController.BuildConfig(
                coproxy: MainPres.IsConginxRunning,
                writeHosts: false,
                httpPort: NginxHttpPort,
                httpsPort: NginxHttpsPort,
                mixedPort: MihomoMixedPort,
                nginxConfText: null,
                mihomoConfText: MihomoConfs);

            config.MihomoBinaryPath = MainPres.IsComihomoExist ? MainConst.ComihomoPath : MainConst.MihomoPath;
            config.MihomoConfText = MainPres.IsConginxRunning
                ? MainPres.IsComihomoExist ? HostsComihomoConfs : ComihomoConfs
                : MihomoConfs;

            AgentResponse response = await ProxyController.WriteAndStartAsync(config);

            if (!response.Ok)
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, response.Error ?? MainConst._LaunchMihomoErrorMsg, ButtonEnum.Ok).ShowWindowDialogAsync(this);

                return;
            }

            if (MainPres.IsComihomoExist)
                MainPres.IsComihomoRunning = true;
            else
                MainPres.IsMihomoRunning = true;

            MainPres.IsProxyRunning = true;
            MainPres.ProxyEngineName = "mihomo";
        }
        finally
        {
            MainPres.IsMihomoIniting = false;

            if (!MainPres.IsComihomoIniting)
                await File.WriteAllTextAsync(MainConst.MihomoConfPath, ExtraMihomoConfs);

            MihomoConfWatcher.EnableRaisingEvents = true;
        }
    }
```

- [ ] **Step 13: 删掉 GUI 里的 proxy 健康探测循环**

`Wins/MainWin.axaml.cs:360-381`（外部 nginx 起来后的 `while(true) { Http.GetAsync ... }` 轮询）现在由 agent 负责启停确认，`WriteAndStartAsync` 的返回值就是结果。删掉。

同样删掉 `:552-572` 的 mihomo 探测循环。

**补充（计划漏了，实测必须改）：`ProxyTimer_Tick`。** 它原来每 100ms 用 `Process.GetProcessesByName` 探测 nginx/mihomo 进程，还引用 `BuiltinNginx.IsRunning`。这两样现在都不存在了（`BuiltinNginx` 已搬进 agent、进程是 agent 的子进程），不改会编译不过。改成：

```csharp
    private void ProxyTimer_Tick(object? sender, EventArgs e)
    {
        MainPres.RefreshProxyEngine();

        bool isBuiltin = MainPres.IsBuiltinProxyEngine;
        bool externalNginxFound = NginxFinder.Find() is not null;
        bool externalConginxFound = NginxFinder.Find(coproxy: true) is not null;

        MainPres.IsConginxExist = isBuiltin || externalConginxFound;
        MainPres.IsNginxExist = isBuiltin || externalNginxFound;
        MainPres.IsComihomoExist = File.Exists(Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, Path.GetFileName(MainConst.ComihomoPath)));
        MainPres.IsMihomoExist = File.Exists(Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, Path.GetFileName(MainConst.MihomoPath)));

        // nginx/mihomo 现在是 agent 的子进程，GUI 拿不到句柄，也不再按进程名探测；
        // 运行状态由 agent 的 start/stop 返回值和 IsProxyRunning / ProxyEngineName 承载。
    }
```

（`IsNginxExist` / `IsConginxExist` / `IsMihomoExist` / `IsComihomoExist` 必须保留，两个 Watcher 的早退和按钮可用性还在用。）

- [ ] **Step 14: 编译验证**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build Sheas-Cealer-Nix.csproj -c Release 2>&1 | grep -E "error CS" | head -20
```

Expected: 无 `error CS`。XAML 基线不变（仍是 24 个 `AVLN2000` + 40 个 `AVLN2200`）。

**实测：0 个 `error CS`，基线 64 不变 ✓**（`_AgentUnavailablePrompt` 的 resx 已从 Task 10 Step 1 提前加进来，否则 Step 9/12 用到的这个 key 会让 Task 9 编译不过。）

如果出现 `error CS0246: 找不到 BuiltinNginx`，说明 Step 10 漏删了引用。

- [ ] **Step 15: 跑测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: `Failed: 0, Passed: 40`（31 + 9）

- [ ] **Step 16: 提交**

```bash
git add Sheas-Cealer-Nix
git commit -m "refactor: route global cealing through privileged agent"
```

---

## Task 10: 启动时授权与 UI 绑定

**Files:**
- Modify: `Wins/MainWin.axaml`（6 处 `IsVisible` + 6 处列宽 + 1 处窗口宽度 + 按钮内容绑定）
- Modify: `Convs/MainNginxButtonContentConv.cs`
- Modify: `Convs/MainMihomoButtonContentConv.cs`
- Modify: `Consts/MainMultilangConst.resx` + `.zh.resx` + `.Designer.cs`（新增两个文案）
- Modify: `Props/Settings.settings` + `Props/Settings.Designer.cs`（新增 `PromptAgentOnStartup`）

`Convs/MainWinWidthConv.cs` 和 `Convs/MainProxyColumnWidthConv.cs` **不删也不改**，只是不再被 XAML 引用（Step 4、Step 5）。`MainConv.cs` 的注册同样保留，避免动 converter 的公开面。

- [ ] **Step 1: 加文案**

`Consts/MainMultilangConst.resx` 与 `Consts/MainMultilangConst.zh.resx` 各加两个 key：

`_AgentUnavailablePrompt`：
- en: `Cannot start the privileged agent. Global cealing needs permission to modify hosts, install a root certificate and bind ports 80/443.`
- zh: `无法启动特权代理。全局伪造需要修改 hosts、安装根证书并监听 80/443 的权限。`

`_AgentLaunchPrompt`：
- en: `Global cealing requires elevated privileges. Sheas Cealer Nix will ask for administrator authorization now; you will only be asked once per session.`
- zh: `全局伪造需要管理员权限。Sheas Cealer Nix 将在稍后请求一次管理员授权，本次会话内只会询问一次。`

两个 resx 的 `<data>` 节点插在 `_CealHostErrorPrompt` 之前（保持字母序）：

```xml
  <data name="_AgentLaunchPrompt" xml:space="preserve">
    <value>Global cealing requires elevated privileges. Sheas Cealer Nix will ask for administrator authorization now; you will only be asked once per session.</value>
  </data>
  <data name="_AgentUnavailablePrompt" xml:space="preserve">
    <value>Cannot start the privileged agent. Global cealing needs permission to modify hosts, install a root certificate and bind ports 80/443.</value>
  </data>
```

`Consts/MainMultilangConst.Designer.cs` 是**签入仓库的生成文件**，命令行 `dotnet build` 不会重新生成它。Rider/VS 里保存 `.resx` 会自动刷新；如果没刷新，就照着文件里 `public static string _NginxEngineMissingPrompt` 的写法手写补上两个属性（`ResourceManager.GetString(name, resourceCulture)`）：

```csharp
        public static string _AgentLaunchPrompt {
            get {
                return ResourceManager.GetString("_AgentLaunchPrompt", resourceCulture);
            }
        }

        public static string _AgentUnavailablePrompt {
            get {
                return ResourceManager.GetString("_AgentUnavailablePrompt", resourceCulture);
            }
        }
```

`MainMultilangConst` 是 partial class 且 `MainConst` 继承自它，所以 `MainConst._AgentUnavailablePrompt` / `MainConst._AgentLaunchPrompt` 直接可用，不需要额外的转发。

- [ ] **Step 2: 加启动提示开关**

`Props/Settings.settings` 加：

```xml
    <Setting Name="PromptAgentOnStartup" Type="System.Boolean" Scope="User">
      <ValueProfile />
      <Value>false</Value>
    </Setting>
```

`dotnet build` 会重新生成 `Props/Settings.Designer.cs`。

- [ ] **Step 3: 改按钮可见性绑定**

`Wins/MainWin.axaml` 的 `:85`、`:115`、`:165`、`:182`、`:211`、`:236` 六处：

```xml
IsVisible="{Binding Source={x:Static consts:MainConst.IsAdmin}, Converter={x:Static convs:MainConv.MainAdminControlVisibilityConv}}"
```

全部替换为：

```xml
IsVisible="True"
```

**按钮不再隐藏。** 这就是用户最初问的那个问题 —— 无论有没有 agent，按钮都在；没有 agent 时点了会弹授权或报错，不会静默消失。

- [ ] **Step 4: 改列宽绑定**

`Wins/MainWin.axaml` 的 `:65`、`:66`、`:148`、`:149`、`:201`、`:223` 六处：

```xml
<ColumnDefinition Width="{Binding Source={x:Static consts:MainConst.IsAdmin}, Converter={x:Static convs:MainConv.MainProxyColumnWidthConv}}" />
```

全部改为常量星号宽度（对应 `MainProxyColumnWidthConv` 在 `IsAdmin == true` 时返回的 `GridLength(1, Star)`）：

```xml
<ColumnDefinition Width="*" />
```

**不要写成 `{x:Static convs:MainConv.MainConv.MainProxyColumnWidthConv.ConstantWidth}`** —— 那是无效路径（`MainConv` 里注册的是 `MainProxyColumnWidthConv` 类型，不是 `MainConv` 嵌套类型），而且为了一个常量去改 converter 的公开面不划算。`Convs/MainProxyColumnWidthConv.cs` 保留文件但不再被 XAML 引用，`MainConv.cs` 里的注册也保留，避免动 `MainConv` 的公开 API 面。

- [ ] **Step 5: 窗口宽度改成常量**

`Wins/MainWin.axaml:13`：

```xml
    Width="{Binding Source={x:Static consts:MainConst.IsAdmin}, Converter={x:Static convs:MainConv.MainWinWidthConv}}"
```

改为常量（对应 `MainWinWidthConv` 在 `IsAdmin == true` 时返回的 708）：

```xml
    Width="708"
```

`Convs/MainWinWidthConv.cs` 保留但不再被引用（`MainConv.cs:23` 的注册也保留，避免动 `MainConv` 的公开面）。

- [ ] **Step 6: 改按钮内容 converter 的数据源**

`Convs/MainNginxButtonContentConv.cs` 与 `Convs/MainMihomoButtonContentConv.cs` 原本依赖 `IsNginxRunning` / `IsConginxRunning` 等进程探测属性。改成依赖新属性：

把 `Wins/MainWin.axaml:98-103` 的 `MultiBinding` 换成：

```xml
                    <Button.Content>
                        <MultiBinding Converter="{x:Static convs:MainConv.MainNginxButtonContentConv}">
                            <Binding Path="IsProxyRunning" />
                            <Binding Path="IsNginxIniting" />
                            <Binding Path="IsCoproxyIniting" />
                            <Binding Path="ProxyEngineName" />
                        </MultiBinding>
                    </Button.Content>
```

`Convs/MainNginxButtonContentConv.cs` 改为按「运行中 / 启动中 / 空闲」三态返回，逻辑读 `MainMultilangConst` 里已有的 `NginxButtonIsRunningContent` / `NginxButtonIsStoppedContent` / `NginxButtonIsInitingContent` / `ConginxButtonIsInitingContent`（zh resx `:190`、`:196`、`:187`、`:136`），不要新增文案。

- [ ] **Step 7: 按钮可用性保持不变**

`Wins/MainWin.axaml:87-96` 与 `:118-126` 的 `MultiBinding` **不动**，`Convs/MainNginxButtonIsEnabledConv.cs` / `Convs/MainMihomoButtonIsEnabledConv.cs` **也不动**。

不要把 `IsAgentReady` 塞进可用性条件：agent 没起来就把按钮置灰的话，用户第一次点按钮就点不动，授权入口也就没了。正确做法是按钮始终 enabled，点击时由 Step 8 的 `EnsureAgentAsync` 负责拉起 agent 或弹错误框。

- [ ] **Step 8: 启动时按需授权**

`Wins/MainWin.axaml.cs` 的 `MainWin_Loaded`，在 Task 9 Step 7 设置 `IsAgentReady` 之后加：

```csharp
            // 只给用过全局伪造的用户弹授权，纯浏览器用户不该被无故打扰。
            if (!MainPres.IsAgentReady && Settings.Default.PromptAgentOnStartup)
            {
                await MessageBoxManager.GetMessageBoxStandard(string.Empty, MainConst._AgentLaunchPrompt, ButtonEnum.YesNo).ShowWindowDialogAsync(this) is var answer &&
                    answer == ButtonResult.Yes
                    ? MainPres.IsAgentReady = await ProxyController.EnsureAgentAsync()
                    : false;
            }

            if (!MainPres.IsAgentReady)
                Settings.Default.PromptAgentOnStartup = false;
            else
                Settings.Default.PromptAgentOnStartup = true;
```

首次运行时 `PromptAgentOnStartup` 默认 `false`，所以第一次启动不弹；用户第一次成功拉起 agent 后置 `true`，之后每次启动都会问一遍「要不要现在授权」。

- [ ] **Step 9: 编译 + 测试**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build Sheas-Cealer-Nix.csproj -c Release 2>&1 | grep -oE "AVLN[0-9]+" | sort | uniq -c
dotnet test Sheas-Cealer-Nix.Tests/Sheas-Cealer-Nix.Tests.csproj
```

Expected: 仍是 24 + 40（基线不变），`Failed: 0, Passed: 40`

- [ ] **Step 10: 提交**

```bash
git add Sheas-Cealer-Nix
git commit -m "feat: always show global cealing controls and prompt for agent at startup"
```

---

## Task 11: 端到端手工验证

**Files:**
- 无（纯验证）

**Why:** 前面 10 个任务全是单元测试。提权、真实 nginx、真实 mihomo、系统信任库这些**没有自动化测试**，必须手工过一遍。

> **执行状态（自动化能做的部分已跑完）：**
>
> > ⚠️ **实测发现并修复的 bug：打包后点「全局伪造」报「无法启动特权代理」。** 根因是 `BackgroundCommand` 里的 `nohup`：osascript 的 `do shell script` 没有控制终端，BSD nohup 报 `can't detach from console: Inappropriate ioctl for device` 并**放弃执行命令**，agent 根本没起来；GUI 因为 `EnsureAgentAsync` 超时返回 false 且没带错误信息，只能弹通用的「无法启动特权代理」。修法：去掉 `nohup`，只保留 `&` + fd 重定向；并让 `EnsureAgentAsync` 在「找不到二进制」和「超时」两种情况下把具体原因（含 agent 日志末尾）写进 `AgentLaunchError`。已用 `sh -c '... &'` 在本机验证 agent 能在 shell 退出后存活并监听 socket。
> >
> > ⚠️ **第二个 bug：agent 日志说 `listening` 但 GUI 仍连不上。** socket 由 root 创建、`0600 root:root`，而 `connect()` 需要对 socket 文件有写权限，普通用户被 `EACCES` 拒绝。修法：`AgentServer.Start` 里把 socket `chown` 给 `--owner-uid` 传入的用户（保持 0600，只有该用户能连）。另外 pid 文件写入改为 best-effort（它只写不读，且会被上一次 root 运行留下的同名文件卡死）。
> >
> > ⚠️ **第三个 bug：点「全局伪造」报 `mihomo engine requires mihomoBinaryPath and mihomoConfText`。** 两个问题叠加：(1) 计划的 `EngineSupervisor` 把 `Coproxy` 当成「只起 mihomo」的第三个引擎，但 coproxy 在原实现里是 **mihomo + nginx/builtin 一起起**（mihomo 做 TUN 捕获把域名指向 127.0.0.1，nginx/builtin 在 443 做 SNI 伪造）；(2) mihomo 是需自行下载的外部二进制，没装时 `MihomoConfText` 为 null，`MihomoEngine.Start` 直接抛异常。修法：`Coproxy && CanStartMihomo(config)` 才起 mihomo，然后**无条件**起 nginx/builtin；缺 mihomo 只警告并退化为浏览器级伪造，不再让整个启动失败。`MihomoAdapter` 随之删除。
> >
> > ⚠️ **第四个 bug：内置引擎 Kestrel 报 `The server mode SSL must use a certificate with the associated private key.`** `ProxyCertificateFactory` 用 `childRequest.Create(root, ...)` 生成子证书后直接返回，但该证书**不带私钥**；原 `MainWin` 是先 `childCert.CopyWithPrivateKey(certKey)` 才交给内置引擎的。修法：`ProxyCertificate.Child` 改成 `child.CopyWithPrivateKey(key)` 的结果（原无钥证书 dispose）。另外 `AgentJson` 的枚举序列化改成 camelCase 字符串（`JsonStringEnumConverter`），否则手写配置 `{"engine":"builtin"}` 会报 `malformed config`。已在非 root 环境下用高位端口（18080/18443）验证 `start` 返回 `{"ok":true,...,"engine":"builtin"}`。
> >
> > ⚠️ **第五个 bug：短按「全局伪造」启动了代理却没有效果。** 短按走 coproxy（`coproxy=true, writeHosts=false`），coproxy 依赖外部 mihomo 把流量 TUN 到 127.0.0.1；用户没装 mihomo，于是 `/etc/hosts` 没写、也没 TUN，**代理在 443 监听但没有任何流量被导过去**。修法：`Wins/MainWin.axaml.cs` 里 `mihomoAvailable = IsMihomoExist || IsComihomoExist`，`isCoproxy = sender == null && mihomoAvailable`、`writeHosts = !isCoproxy`——mihomo 不可用时自动退化成 hosts 模式。另给 mihomo 按钮补了缺 mihomo 的提示文案 `_MihomoMissingPrompt`。
> >
> > ⚠️ **第六个 bug：root 僵尸 agent 泄漏。** `MainWin_Closing` 只在 `MainPres.IsAgentReady` 为真时才 `ShutdownAsync`，但按钮路径的 `EnsureAgentAsync` 从没把 `IsAgentReady` 置真 → 每次关窗都跳过 shutdown，agent 以 root 身份永久存活，多开会互相抢 socket/端口。修法：关闭时无条件尝试 shutdown；两个 tick 在 `EnsureAgentAsync` 成功后置 `IsAgentReady = true`。（已泄漏的进程需要 `sudo pkill -f Cealing-Agent` 清掉。）
> >
> > ⚠️ **第七个 bug：macOS 根证书系统域安装失败。** `security add-trusted-cert -d ... /Library/Keychains/System.keychain` 从后台 root agent 调用会报 `SecTrustSettingsSetTrustSettings: The authorization was denied since no user interaction was possible.`。改为由 GUI（以用户身份）安装到**用户域**：`security add-trusted-cert -r trustRoot -k ~/Library/Keychains/login.keychain-db <Cealing-Root.pem>`，无需授权（已实测 exit 0），停止时 `security delete-certificate -c "Cealing Cert Root" <登录钥匙串>`。新增 `Utils/UserTrust.cs` 与 `MainConst.AgentRootCertPath`，在 `ProxyController` 的 start/stop/shutdown 里调用。agent 侧仍保留系统域尝试（best-effort）。
> >
> > ⚠️ **第八个 bug（最致命）：所有站点都被路由到第 1 条规则，返回同一个 Cloudflare 403。** `ProxyController.BuildServerName` 返回的字符串带着 nginx 的正则标记 `~` 和**结尾的 `|`**（例如 `~^.*google\.com$|`）。agent 侧 `new Regex(rule.ServerName)` 原样编译：结尾的 `|` 在正则里是**空分支**，使整条正则匹配任意字符串（`Regex.IsMatch("www.google.com", "^cdn\.jsdelivr\.net$|")` == **True**，已实测）。于是每条规则都“命中”，`_rules.Find(...)` 永远返回第 1 条（jsdelivr → Cloudflare），google/pixiv/… 全部拿到 Cloudflare 的 403。原 `BuiltinNginx` 直接 `new Regex(serverName[1..])` 就带着这个隐藏 bug。修法：写进 `ProxyRule.ServerName` 前同时剥掉前导 `~` 和结尾 `|`：`serverName[1..].TrimEnd('|')`。已用规则表验证 google→rule23、pixiv→rule3、jsdelivr→rule0。对应的回归测试是 `ProxyConfigBuilderTests.BuildConfigStripsRegexMarkersFromServerName`。
> >
> > ⚠️ **顺带修的严重安全问题：`Cealing-Key.pem`（根证书私钥）之前是 0644 world-readable。** 私钥 + 被信任的根证书 = 任意本机用户可 MITM。改为 `0600`（只 agent/root 可读）。
>
> | 步骤 | 状态 |
> |---|---|
> | Step 1 基线（AVLN 24+40 不变） | ✅ 已验证 |
> | Step 2 agent 前台起 + socket 权限 | ✅ 已验证（socket `srw-------`，日志 `listening on /tmp/cealing-agent.sock (owner uid 501)`） |
> | Step 3 status | ✅ 已验证 → `{"ok":true,"status":{"running":false,"engine":"none",...}}` |
> | Step 4 start 缺配置 | ✅ 已验证 → `{"ok":false,"error":"config not found: /tmp/cealing-proxy.json"}` |
> | Step 5 malformed JSON | ✅ 已验证 → `{"ok":false,"error":"malformed request"}` |
> | Step 11 shutdown | ✅ 已验证 → `{"ok":true,...}`，socket 被删、进程退出 |
> | Step 6-10（root：绑 443、装根证书、改 hosts） | ⏳ **待人工**：需要交互式 `sudo` 且会改系统信任库/hosts，自动化环境无法完成 |
> | Step 12 GUI 端到端（授权框） | ⏳ **待人工**：需要图形会话 |
> | Step 13 外部 nginx 引擎 | ⏳ **待人工** |
>
> 另外：agent 的 `dotnet run` 前台跑依赖 csproj 里的 `<RollForward>LatestMajor</RollForward>`（本机只有 .NET 10 运行时，没有 8.0）。

- [ ] **Step 1: 确认基线没被破坏**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build Sheas-Cealer-Nix.csproj -c Release 2>&1 | grep -oE "AVLN[0-9]+" | sort | uniq -c
```

Expected: 仍是 24 + 40。如果更多，说明引入了新问题。

- [ ] **Step 2: 跑 agent 前台模式，验证 socket 与权限**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
cd "/Users/dbin0123/development/vscode workspace/Sheas-Cealer-nix"
rm -f /tmp/cealing-agent.sock
dotnet run --project Cealing-Agent -- --socket /tmp/cealing-agent.sock --config /tmp/cealing-proxy.json --app-dir /tmp --owner-uid "$(id -u)"
```

Expected: 日志出现 `listening on /tmp/cealing-agent.sock (owner uid 501)`，且 `ls -la /tmp/cealing-agent.sock` 显示 `srw-------`（0600，只属主可读写）。

（`dotnet run` 前台跑就是 agent 的唯一形态；Task 8 里做 nohup+重定向的是 osascript/pkexec 那侧。测完直接 Ctrl+C。）

- [ ] **Step 3: 用 socat/nc 验证协议**

```bash
printf '{"command":"status"}\n' | nc -U /tmp/cealing-agent.sock
```

Expected: 一行 JSON，含 `"ok":true` 和 `"engine":"none"`。若 `nc -U` 不可用，用 `python3 -c` 写个 5 行的 socket 客户端。

- [ ] **Step 4: 验证 start 会因缺配置被拒**

```bash
printf '{"command":"start"}\n' | nc -U /tmp/cealing-agent.sock
```

Expected: `{"ok":false,"error":"config not found: /tmp/cealing-proxy.json"}`

- [ ] **Step 5: 验证 malformed JSON**

```bash
printf '{not json\n' | nc -U /tmp/cealing-agent.sock
```

Expected: `{"ok":false,"error":"malformed request"}`

- [ ] **Step 6: 写一个真配置并用 builtin 引擎起（需要 root）**

```bash
cat > /tmp/cealing-proxy.json <<'JSON'
{"engine":"builtin","coproxy":false,"flashing":false,"writeHosts":false,"httpPort":80,"httpsPort":443,
 "certSans":[{"domain":"example.com","wildcard":false}],
 "rules":[{"serverName":"^example\\.com$","ip":"93.184.216.34","sni":null,"sniEnabled":false,"port":443}]}
JSON
```

然后用 `sudo` 起一个 agent（root 才能绑 443）：

```bash
sudo rm -f /tmp/cealing-agent.sock
sudo sh -c 'cd "/Users/dbin0123/development/vscode workspace/Sheas-Cealer-nix" && nohup dotnet run --project Cealing-Agent -- --socket /tmp/cealing-agent.sock --config /tmp/cealing-proxy.json --app-dir /tmp --owner-uid '"$(id -u)"' >/tmp/agent.log 2>&1 </dev/null &'
```

Expected: `sudo` 立即返回（不阻塞），`/tmp/agent.log` 里有 `listening on ...`

- [ ] **Step 7: 验证 start 真的起来**

```bash
printf '{"command":"start"}\n' | nc -U /tmp/cealing-agent.sock
sudo lsof -nP -iTCP:443 -sTCP:LISTEN
```

Expected: 第一条返回 `{"ok":true,...}`；第二条显示 Kestrel 在 LISTEN。

- [ ] **Step 8: 验证根证书进了系统钥匙串**

```bash
sudo security find-certificate -a -c "Cealing Cert Root" -Z /Library/Keychains/System.keychain
```

Expected: 输出一张证书，含 SHA-1 hash。**这一步是整个计划里最重要的一手验证** —— 它直接证明旧代码（`X509Store` + `StoreLocation.LocalMachine`）在 macOS 上装不进去的问题已被绕开。

- [ ] **Step 9: 验证 hosts 模式（writeHosts=true）**

把配置里 `"writeHosts":true` 加上，重发 `start`，然后：

```bash
grep -A3 "Cealing Nginx Start" /etc/hosts
```

Expected: 标记块里出现 `127.0.0.1 example.com`

- [ ] **Step 10: 验证 stop 清理干净**

```bash
printf '{"command":"stop"}\n' | nc -U /tmp/cealing-agent.sock
grep -c "Cealing Nginx" /etc/hosts
sudo security find-certificate -a -c "Cealing Cert Root" /Library/Keychains/System.keychain
```

Expected: `grep -c` 输出 `0`（无残留），第二条命令**无输出**（证书已删）

- [ ] **Step 11: 验证 shutdown 能让 agent 退出**

```bash
printf '{"command":"shutdown"}\n' | nc -U /tmp/cealing-agent.sock
sleep 1
ls -la /tmp/cealing-agent.sock
```

Expected: `No such file or directory`（socket 已删、进程已退）

- [ ] **Step 12: 验证 GUI 端到端（非 sudo）**

```bash
export PATH="/usr/local/share/dotnet:$PATH"
cd "/Users/dbin0123/development/vscode workspace/Sheas-Cealer-nix"
dotnet run --project Sheas-Cealer-Nix
```

逐项确认：

1. 窗口宽度 708，「启动全局伪造」和「启动全局净化」两个按钮**可见**
2. 点「启动全局伪造」→ 弹 macOS 授权框 → 输密码 → 全局伪造跑起来
3. 授权后 `security find-certificate` 能查到证书
4. 再点一次停止 → **不再弹授权框**
5. 重启 GUI → 点按钮 → **又弹一次**授权框（agent 随进程退出而没了）
6. 用 `curl --resolve example.com:443:127.0.0.1 https://example.com/` 验证非浏览器客户端也被覆盖

- [ ] **Step 13: 确认外部 nginx 引擎仍可用**

在设置里把引擎切成「外部 nginx」（确保 `nginx -V` 能被 `NginxFinder` 找到，`Utils/NginxFinder.cs:135-178` 要求 ≥ 1.15 且带 `http_ssl_module`），再走一遍 Step 12。

Expected: `agent.log` 里有 `external nginx started (pid N)`，`lsof -iTCP:443` 显示 nginx 而非 Kestrel。

---

## Task 12: 文档

**Files:**
- Modify: `README.md`
- Modify: `README_EN.md`

- [ ] **Step 1: 更新「自我介绍」段落**

`README.md:11-13` 与 `README_EN.md` 对应位置，改成说明：

- macOS / Linux 上首次使用全局伪造会弹一次系统授权框（Windows 仍需以管理员身份运行）
- 授权仅用于拉起一个本地 root 代理进程，之后不再重复询问
- 代理进程负责写 hosts、装临时根证书、监听 80/443；退出时全部还原
- 根证书与 hosts 条目只在代理进程存活期间存在

- [ ] **Step 2: 加一节「权限说明」**

说明三件事：

1. 授权是为了什么（三个具体动作）
2. 怎么撤销（关掉应用 = 代理退出 = 自动还原；也可 `sudo security delete-certificate -Z <sha1> /Library/Keychains/System.keychain` 手动删）
3. 隐私（没有网络回传，代理只监听本机；`Sheas Dop` 等相关项目是独立进程）

- [ ] **Step 3: 提交**

```bash
git add README.md README_EN.md
git commit -m "docs: explain agent privilege model"
```

---

## 已知遗留（不属于本计划，但别忘）

| 项 | 说明 |
|---|---|
| `AgentServer` 不校验 peer uid | `ownerUid` 参数收了但没用，只靠 socket 0600。v2 补 Linux `SO_PEERCRED` / macOS `LOCAL_PEERCRED` |
| agent 崩溃后需重新授权 | 没有 launchd/systemd 托管，GUI 也不再做健康轮询（Task 9 Step 13 删掉了探测循环）。agent 挂了之后，下一次点按钮会因 socket 连不上而走 `EnsureAgentAsync` 重新弹授权。v2 建议加 launchd/systemd 托管 |
| agent 写日志是 root 所有 | GUI 打不开 `cealing-agent.log`。排障要 `sudo`。后续可以让 agent 把 socket 权限放开到日志只读 |
| `NginxConfWatcher` 触发的是全量 `reload` | 原来 nginx 是 `nginx -s reload`，现在是 agent 整体重启引擎。高频编辑 `nginx.conf` 时会有短暂断流 |
| `MainPres` 里 4 个 `Is*Running` 属性语义变了 | 现在由 `IsProxyRunning` + `ProxyEngineName` 承载，旧属性只是 UI 兼容。后续清理 |
| coproxy 模式下 mihomo 的 TUN 与 53 劫持 | 只在 Task 11 手工验证过，自动化测试缺失 |

## 计划自检

**需求覆盖**

| 需求 | 覆盖任务 |
|---|---|
| 不必 `sudo` 跑 GUI 也能用全局伪造 | Task 8（提权）+ Task 9（走 agent） |
| 启动时弹系统授权框 | Task 10 Step 8 |
| 内置 .NET 引擎不依赖 nginx | Task 5（引擎搬进 agent）+ Task 6（外部 nginx 仅作为可选引擎） |
| 授权只弹一次 | Task 8（`Launch` 是唯一提权路径）+ Task 7（其余全走 socket） |
| 按钮不再被隐藏 | Task 10 Step 3 |
| 修掉 macOS 装不进根证书 | Task 4（改用 `security add-trusted-cert`） |
| 跨平台（macOS/Linux 覆盖，Windows 不回归） | Task 0 环境约束 + Task 9 Step 5 的 Windows 保留分支 |

**已知不覆盖**：`SMJobBless`、launchd/systemd 托管、peer uid 校验、内置引擎在 Linux 上免提权。理由见「范围边界」与「已知遗留」。
