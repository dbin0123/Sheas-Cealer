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

// appDir  = 应用 bundle 目录，只放只读的二进制（nginx / mihomo / 自身）
// dataDir = 用户数据目录，放所有会被改写的文件（配置、证书、日志）
internal sealed class EngineSupervisor(string appDir, string dataDir, int ownerUid, string hostsPath)
{
    private readonly BuiltinEngine _builtin = new();
    private readonly ExternalNginxEngine _external = new(dataDir, ownerUid);
    private readonly MihomoEngine _mihomo = new(dataDir, ownerUid);

    private IProxyEngine? _active;
    private bool _mihomoRunning;
    private bool _hostsWritten;
    private ProxyConfig? _config;
    private ProxyCertificate? _certificate;
    private string? _rootThumbprint;

    // 本次真正导入成功的 Java 信任库。回删只认这些：别人的库没动过，就不该去 keytool -delete。
    private IReadOnlyList<string> _javaStores = [];

    // agent 以 root 跑，Environment.GetFolderPath(UserProfile) 是 /root；用户的 ~/.jdks
    // 和 Toolbox 运行时只能从 --data-dir 反推出来。
    private IReadOnlyList<string>? _userHomes;

    private IReadOnlyList<string> UserHomes =>
        _userHomes ??= AppPaths.HomeForDataDir(dataDir) is string home ? [home] : [];

    // start/reload/stop 必须串行：两个并发的 StartAsync 会各自 StopAsync 后同时去绑
    // 80/443，其中一个必然抛 "Failed to bind to address https://localhost:443"。
    // GUI 点按钮与外部触发（如状态栏菜单、启动自检）可能几乎同时到达，所以在这里排队。
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    // TrustStore 的平台名必须与系统信任库的实际形态一致，装/拆两处共用同一个值。
    internal static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

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
        Pid = Environment.ProcessId,
        AppDir = appDir,
        Elevated = Privilege.IsElevated
    };

    internal async Task StartAsync(ProxyConfig config, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await StartCoreAsync(config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartCoreAsync(ProxyConfig config, CancellationToken cancellationToken)
    {
        // 权限先于一切：普通令牌下写 hosts 必挂（Access is denied），
        // 而检查如果放在 StopCoreAsync 之后，就会先把一个正常工作的引擎停掉再失败。
        string? denied = Privilege.RequireElevated(Privilege.IsElevated, Platform);

        if (denied is not null)
            throw new UnauthorizedAccessException(denied);

        // 注意：这里必须调 StopCoreAsync()（不取锁的私有版本），不能调公有 StopAsync()。
        // StartAsync 已持有 _lifecycleLock，而 SemaphoreSlim 不可重入，
        // 再取一次锁会永久死锁 —— 表现为 start 命令永不返回、界面像卡死。
        //
        // keepJavaTrust：重启引擎不要把 Java 信任库里的根删掉再装回来。删和装之间实测隔了
        // 11 秒，正好在这段里启动的 JVM 读不到根证书（JSSE 只在建 JVM 时读 cacerts），
        // 之后插件一直报连接错误，而用户只会觉得「明明装过证书了」。真正停止时照旧要删。
        AgentLog.Info("start: stopping previous engine");

        await StopCoreAsync(keepJavaTrust: true).ConfigureAwait(false);

        AgentLog.Info("start: previous engine stopped");

        if (config.WriteHosts)
        {
            HostsConf.Append(hostsPath, HostsConf.BuildBlock(config.CertSans.Select(san => (san.Domain, san.Wildcard))));

            // 只有真的写过才有资格去删。租约超时自杀的 agent 也会走 StopCoreAsync，
            // 不加这个标记它就会把**别的 agent** 正在用的 hosts 块一起删掉。
            _hostsWritten = true;

            // 通配符域名在这里会被削成「apex + www.」两条（hosts 语法没有 *.x 这种东西），
            // 这是「全局伪造对非浏览器应用只覆盖一部分域名」的头号原因（issue #1）。
            // 不写进日志的话，排查的人只能去猜为什么 translate.google.com 上不了。
            int wildcardSans = config.CertSans.Count(san => san.Wildcard);

            AgentLog.Info(
                $"hosts block written ({config.CertSans.Count} sans, {wildcardSans} of them wildcards" +
                (wildcardSans > 0 ? "; hosts cannot express *.domain, so each wildcard became apex + www. only" : ")"));
        }

        AgentLog.Info($"start: creating certificate ({config.CertSans.Count} sans)");

        _certificate = ProxyCertificateFactory.Create(config, dataDir, ownerUid);
        _rootThumbprint = TrustStore.Thumbprint(_certificate.Root);

        AgentLog.Info($"start: certificate ready (root {_rootThumbprint})");

        // 安装和卸载必须用同一个平台名：Windows 若被当成 linux，就会去跑
        // sh / update-ca-certificates，机器级 Root 存储里的根证书永远删不掉。
        try
        {
            TrustStore.Install(_certificate.RootPemPath, Platform);
        }
        catch (Exception ex)
        {
            AgentLog.Error($"root cert install failed, continuing anyway: {ex.Message}");
        }

        // JVM 完全不读系统信任库，只认 <java.home>/lib/security/cacerts，
        // 所以上面那步对 IntelliJ IDEA 这类 Java 客户端等于没做：伪造的叶子证书必被 JSSE 拒掉，
        // 插件侧只剩一句 "Network error"（issue #1）。同一张根证书再用 keytool 装一份进 Java 侧。
        try
        {
            _javaStores = JavaTrust.Install(_certificate.RootPemPath, Platform, UserHomes, config.JavaTrustPaths);
        }
        catch (Exception ex)
        {
            AgentLog.Error($"java root cert install failed, continuing anyway: {ex.Message}");
        }

        try
        {
            // coproxy 模式 = mihomo 负责系统级 TUN 捕获（把伪造域名映射到 127.0.0.1）
            // + nginx/builtin 在 443 上做 SNI 伪造。两者缺一不可，所以这里 **不是 if/else**：
            // 先按需起 mihomo，再无条件起 nginx/builtin。
            //
            // mihomo 是外部二进制（macOS 需自行下载），没装时必须优雅退化到「只起 nginx/builtin」
            // （等价于原来的浏览器级伪造），绝不能因为缺 mihomo 就整个启动失败。
            if (config.Coproxy && CanStartMihomo(config))
            {
                AgentLog.Info("start: launching mihomo");

                _mihomo.Start(config);
                _mihomoRunning = true;

                AgentLog.Info("start: mihomo launched");
            }
            else if (config.Coproxy)
            {
                AgentLog.Warn("coproxy requested but mihomo binary/conf is missing; starting nginx/builtin only");
            }

            if (config.Engine == ProxyEngineKind.Builtin)
            {
                _active = new BuiltinEngineAdapter(_builtin, () => _certificate!);

                AgentLog.Info("start: launching builtin engine");

                await _active.StartAsync(config, cancellationToken).ConfigureAwait(false);

                AgentLog.Info("start: builtin engine started");
            }
            else
            {
                _active = new ExternalNginxAdapter(_external);

                AgentLog.Info("start: launching external nginx");

                await _active.StartAsync(config, cancellationToken).ConfigureAwait(false);

                AgentLog.Info("start: external nginx started");
            }
        }
        catch
        {
            // 同上：已持锁，只能走不取锁的 StopCoreAsync()。
            await StopCoreAsync().ConfigureAwait(false);
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
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    // 清理必须「每一步都尽力做完」。原来的写法任何一步抛出都会让后面的步骤整个跳过 ——
    // 而后面的步骤恰好是拆系统根证书和还原 hosts：agent 照样退出，界面那边只看到「代理没了」，
    // 伪造的 hosts 和一张系统信任根却永久留在机器上，比进程残留难发现得多。
    private async Task StopCoreAsync(bool keepJavaTrust = false)
    {
        await RunQuietlyAsync(
            "stopping engine",
            async () =>
            {
                if (_active is not null)
                    await _active.StopAsync().ConfigureAwait(false);
            });

        _active = null;

        await RunQuietlyAsync(
            "stopping mihomo",
            () =>
            {
                if (_mihomoRunning)
                    _mihomo.Stop();

                return Task.CompletedTask;
            });

        _mihomoRunning = false;

        await RunQuietlyAsync("stopping builtin engine", () => _builtin.StopAsync());

        // 只拆自己装上去的东西。一个从未收到 start 的 agent（例如没人认领租约、60 秒后自杀的那个）
        // 绝不能去删**另一个正在工作的 agent** 写下的 hosts 块和根证书。
        if (_rootThumbprint is not null)
        {
            string thumbprint = _rootThumbprint;

            await RunQuietlyAsync(
                $"uninstalling root cert {thumbprint}",
                () =>
                {
                    TrustStore.Uninstall(thumbprint, Platform);

                    return Task.CompletedTask;
                });

            _rootThumbprint = null;
        }

        if (_javaStores.Count > 0 && !keepJavaTrust)
        {
            IReadOnlyList<string> stores = _javaStores;

            _javaStores = [];

            await RunQuietlyAsync(
                $"uninstalling root cert from {stores.Count} java trust store(s)",
                () =>
                {
                    JavaTrust.Remove(stores);

                    return Task.CompletedTask;
                });
        }

        await RunQuietlyAsync(
            "disposing certificate",
            () =>
            {
                _certificate?.Dispose();
                _certificate = null;

                return Task.CompletedTask;
            });

        if (_hostsWritten)
        {
            await RunQuietlyAsync(
                "reverting hosts",
                () =>
                {
                    HostsConf.Remove(hostsPath);

                    return Task.CompletedTask;
                });

            _hostsWritten = false;
        }

        _config = null;
    }

    private static async Task RunQuietlyAsync(string step, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Error($"{step} failed: {ex}");
        }
    }
}
