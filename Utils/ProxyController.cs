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

    // 自包含 .NET 应用首次从 Finder 启动时可能要几秒（Gatekeeper、JIT、证书生成、绑 443），
    // 10 秒太紧会误判「未在超时前就绪」，进而又拉起一个重复 agent。放宽到 30 秒。
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(30);

    // 复用旧 agent 前要确认它就是当前这个 app 拉起来的。旧 agent 是 root，
    // GUI 杀不动，只能走 Shutdown/Cleanup 协议；协议也不通才提示用户手动清理。
    private static readonly TimeSpan StaleShutdownWait = TimeSpan.FromSeconds(5);

    internal async Task<bool> EnsureAgentAsync()
    {
        MainConst.AgentLaunchError = string.Empty;

        // 先决定 root 侧的可执行文件目录，再谈复用：AppImage 之类挂在 FUSE 上的 bundle
        // 对 root 一律 EACCES（连 exec 都会被拒），所以要先把二进制复制到真实目录。
        // 放在最前面还有个必要条件：正在跑的 agent 上报的 --app-dir 就是上一次复制出来的目录，
        // 先判定复用会把它的目录「复制好之前」的 AppDir 拿去比对，误判成不兼容而白杀一轮。
        string? stageError = await BinaryStager.EnsureStagedAsync();

        if (stageError is not null)
        {
            MainConst.AgentLaunchError = stageError;

            return false;
        }

        // 已经有一个 agent 在跑：先确认身份，再复用。
        if (await WaitForAgentAsync(TimeSpan.FromSeconds(2)))
            return await ClaimLeaseAsync();

        // socket 文件存在但暂时连不上：可能 agent 正在启动中（尤其是第一次），
        // 先耐心等一下，别急着重开——重开会删掉它正在用的 socket、留下僵尸进程。
        if (Client.IsAlive && await WaitForAgentAsync(TimeSpan.FromSeconds(8)))
            return await ClaimLeaseAsync();

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
                MainConst.AgentAppDir,
                MainConst.DataDir,
                PrivilegeEscalator.CurrentUid,
                MainConst.AgentLogPath);
        }
        catch (Exception ex)
        {
            MainConst.AgentLaunchError = ex.Message;

            return false;
        }

        if (await WaitForAgentAsync(StartupWait))
            return await ClaimLeaseAsync();

        MainConst.AgentLaunchError = BuildStartupFailureMessage();

        return false;
    }

    // 「agent 活着」不等于「agent 归我管」。认领租约才算真正拿到它，
    // 而且这一步会让 agent 知道我们的进程句柄：GUI 一死（崩溃/被杀/注销都算），
    // 连接断掉，agent 就自己拆引擎、还原 hosts、拆根证书后退出。
    //
    // 认领失败必须当成硬失败：agent 若拿不到租约，只会在存活超时后自杀，
    // 用户看到的就成了「代理用着用着突然没了」。
    private async Task<bool> ClaimLeaseAsync()
    {
        try
        {
            await Client.HoldAsync();

            return true;
        }
        catch (Exception ex)
        {
            MainConst.AgentLaunchError = $"未能接管 agent 的主人租约，agent 会在超时后自行退出：{ex.Message}";

            return false;
        }
    }

    // 复用成功 = 能 ping 通，且对方报的 app 目录就是当前这个 app。
    // 应用被重装/换路径后，旧的 root agent 还活着并占着 socket，它捕获的是已删除 bundle 里的
    // --config / --app-dir。此时复用会一路报 “config not found: <当前新路径>”，看起来自相矛盾。
    private async Task<bool> WaitForAgentAsync(TimeSpan timeout)
    {
        for (int i = 0; i < (int)(timeout.TotalMilliseconds / 100); i++)
        {
            if (await PingAsync() is { Ok: true } response)
                return await IsCurrentAgentAsync(response);

            await Task.Delay(100);
        }

        return false;
    }

    // 复用条件：协议版本够新、app 目录一致、且 agent 正在用的 --config 就是我准备写的那份。
    // 第三条是原地升级场景的关键：AppDir 不会变，但 v1.0.14 把配置从 app bundle 挪到了
    // 用户数据目录。只比 AppDir 会把旧版本 agent 误判成「同一个 agent」继续复用，
    // 它随后用自己的旧 --config 去找文件，报出
    // 「config not found: <bundle 内路径>」——而 GUI 明明已经把配置写到新位置了。
    private async Task<bool> IsCurrentAgentAsync(AgentResponse response)
    {
        AgentStatus? status = response.Status;
        string? mismatch = DescribeMismatch(status);

        if (mismatch is null)
            return true;

        await RetireStaleAgentAsync(status, mismatch);

        return false;
    }

    // 返回 null 表示「同一个 agent，可以复用」；返回字符串表示不兼容的原因（用于报错文案）。
    internal static string? DescribeMismatch(AgentStatus? status)
    {
        // 老版本 agent 不报这些字段，反序列化后为 null / 0，一律判为不可复用
        if (status is null)
            return "未返回状态信息";

        if (status.ProtocolVersion < AgentProtocol.Version)
            return $"协议版本过旧（agent {status.ProtocolVersion} < 要求 {AgentProtocol.Version}）";

        // 特权比路径更优先：一个身份完全对得上、但没有管理员令牌的 agent
        // （UAC 被拒、或被人用普通权限手动拉起来）复用出去，
        // 表现就是「点了全局伪造啥也没有」，日志里只留一句 Access is denied。
        if (!status.Elevated)
            return "agent 未提权（没有管理员权限），无法写 hosts / 绑定 80·443，请在授权框里允许提权";

        if (string.IsNullOrWhiteSpace(status.ConfigPath))
            return "未上报配置路径";

        if (AppPaths.Normalize(status.ConfigPath) != AppPaths.Normalize(MainConst.ProxyConfigPath))
            return $"配置路径不一致（agent 用 {status.ConfigPath}，本应用用 {MainConst.ProxyConfigPath}）";

        if (!string.IsNullOrWhiteSpace(status.AppDir) && AppPaths.Normalize(status.AppDir) != AppPaths.Normalize(MainConst.AgentAppDir))
            return $"应用目录不一致（agent {status.AppDir}，本应用 {MainConst.AgentAppDir}）";

        return null;
    }

    private async Task RetireStaleAgentAsync(AgentStatus? status, string reason)
    {
        string describe = $"（pid {status?.Pid}, 协议 {status?.ProtocolVersion}, --config {status?.ConfigPath}）";

        try
        {
            await Client.ShutdownAsync();
        }
        catch (Exception ex)
        {
            MainConst.AgentLaunchError = $"旧 agent {describe} 无法通过协议关闭：{ex.Message}";
        }

        // 等它自己退掉；不退说明 Shutdown 没生效，socket 会一直占着
        if (await WaitForSocketGoneAsync(StaleShutdownWait))
        {
            MainConst.AgentLaunchError = string.Empty;

            return;
        }

        MainConst.AgentLaunchError =
            $"检测到不兼容的旧 agent 仍在运行{describe}：{reason}。" +
            $"{Environment.NewLine}请用管理员权限结束该进程后重试：{Environment.NewLine}" +
            $"sudo kill {status?.Pid}";
    }

    private async Task<bool> WaitForSocketGoneAsync(TimeSpan timeout)
    {
        for (int i = 0; i < (int)(timeout.TotalMilliseconds / 100); i++)
        {
            if (!Client.IsAlive)
                return true;

            await Task.Delay(100);
        }

        return !Client.IsAlive;
    }

    // 授权后 agent 仍没能连上时，把 agent 日志的最后几行带进弹窗，避免只看到一句笼统的
    // 「无法启动特权代理」而不知道是路径不对、权限被拒还是 agent 自己崩了。
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

            // SNI 三态（与 MainWin 的解析保持一致）：
            //   null    -> 规则明确要求不发 SNI
            //   ""      -> 无覆盖，用规则首个域名兜底
            //   非空     -> 显式覆盖
            // 旧实现只判 sni is not null，"" 会走「有 SNI」分支，把 Chrome 用的占位符
            // 当成真实 SNI 发给上游，Google 这类靠 SNI 选 vhost 的站点直接断连。
            string? effectiveSni = sni is null ? null :
                !string.IsNullOrEmpty(sni) ? sni :
                pairs.FirstOrDefault(p => !p.include.StartsWith('#')).include.TrimStart('$', '*', '.');

            config.Rules.Add(new ProxyRule
            {
                // 去掉 BuildServerName 前缀的 '~'（nginx 的正则标记）和结尾的 '|'。
                // 结尾的 '|' 在正则里是**空分支**，会让整条正则匹配任意字符串（永远命中的第 1 条规则），
                // 原 BuiltinNginx 直接 new Regex(serverName[1..]) 就带着这个空分支，是个隐藏 bug。
                ServerName = serverName[1..].TrimEnd('|'),
                Ip = ip,
                Sni = effectiveSni,
                SniEnabled = !flashingProvider() && !string.IsNullOrEmpty(effectiveSni),
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

        AgentResponse response = await Client.StartAsync();

        // agent 起来后把根证书信任进 macOS 用户域（系统域需要交互授权，后台 root 进程做不了）。
        if (response.Ok)
            UserTrust.Install();

        return response;
    }

    internal async Task<AgentResponse> ReloadAsync(ProxyConfig config)
    {
        await File.WriteAllTextAsync(MainConst.ProxyConfigPath, AgentJson.Serialize(config));

        return await Client.ReloadAsync();
    }

    internal async Task<AgentResponse> StopAsync()
    {
        // 用户域信任库只能由 GUI 自己清（macOS 登录钥匙串不让 root 后台进程动），
        // 而且必须排在协议调用**之前**：任何一步等待被丢弃时，删除都已经发生过了。
        UserTrust.Remove();

        return await Client.StopAsync();
    }

    internal Task<AgentResponse> CleanupAsync() => Client.CleanupAsync();

    internal async Task<AgentResponse> ShutdownAsync()
    {
        // 同上，这里的顺序是硬要求：MainWin.QuitAsync 只等 ExitHandshakeWait（8 秒）就
        // Environment.Exit，而 agent 要跑完「停引擎 + 还原 hosts + 拆系统根证书」才回 Shutdown 响应。
        // 把 UserTrust.Remove() 放在 await 之后，等于「agent 清理慢一点，
        // Cealing Cert Root 就永久留在 macOS 登录钥匙串里」。
        UserTrust.Remove();

        return await Client.ShutdownAsync();
    }

    internal async Task<AgentStatus?> TryGetStatusAsync() => (await PingAsync())?.Status;
}
