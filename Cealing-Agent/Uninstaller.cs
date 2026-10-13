using Cealing_Agent.Engines;
using Cealing_Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Cealing_Agent;

/// <summary>
/// 卸载清理：把本应用留在机器上、但不在任何进程记忆里的东西全部还原。
/// </summary>
/// <remarks>
/// 与 <see cref="EngineSupervisor.StopAsync"/> 的区别是这里不看 <c>_rootThumbprint</c>
/// / <c>_hostsWritten</c> 这类「本次进程装过什么」的内存标记。上一任 agent 被 SIGKILL、
/// 崩溃或断电带走时标记随之消失，于是「拆系统根证书 + 还原 hosts」这两步永远不会再执行 ——
/// 机器上就永久留了一张可信的 MITM 根和一段伪造的域名解析，比进程残留难发现得多。
///
/// 每一步都独立尽力而为：任何一步失败都不能让后面的步骤跳过。
/// </remarks>
internal static class Uninstaller
{
    // socket / pid 路径由调用方给：常驻 agent 处理 uninstall 命令时要删的是**自己监听的那个**
    // socket（--socket 可以被传成任意路径），一次性 CLI 才用 AgentPaths 的默认值。
    // 也让清理逻辑可以在测试里对着临时目录跑，而不是真去删 /tmp 上活着的 socket。
    internal readonly record struct Options(
        string HostsPath,
        string? DataDir,
        int OwnerUid,
        IReadOnlyList<string> UserHomes,
        bool PurgeUserData,
        string SocketPath,
        string PidPath);

    internal static void Run(Options options)
    {
        Quiet("stop running agent", () => StopLiveAgent(options.PidPath));

        if (options.DataDir is not null)
            Quiet("reap orphan engines", () => EngineGuard.ReapOrphans(options.DataDir));

        Quiet("revert hosts", () => HostsConf.Remove(options.HostsPath));

        Quiet("purge root cert", () => TrustStore.Purge(EngineSupervisor.Platform));

        // 系统信任库拆干净了不等于 Java 侧干净：keytool 装进 cacerts 的那份是另一个库，
        // 漏掉它就等于在机器上永久留了一张可信 MITM 根，而且比系统域那份更难被发现。
        // 用户在设置页手填的库只记在代理配置文件里，所以趁配置还没被删先读出来一起清。
        Quiet("purge java trust stores", () => JavaTrust.Purge(EngineSupervisor.Platform, options.UserHomes, ConfiguredJavaTrustPaths(options.DataDir)));

        Quiet("remove runtime files", () => RemoveRuntimeFiles(options.SocketPath, options.PidPath));

        if (options.DataDir is not null)
            Quiet("hand data dir back to owner", () => HandOver(options.DataDir, options.OwnerUid));

        foreach (string home in options.UserHomes)
            Quiet($"purge files under {home}", () => PurgeUserFiles(home, options.OwnerUid, options.PurgeUserData));
    }

    // 手填路径只存在于代理配置文件里（agent 没有别的途径知道用户填了什么）。
    // 读不出来就按空处理：自动发现那批照清，最坏是用户自己指的那个库留了一张根证书，
    // 比让卸载整步失败好。
    private static IReadOnlyList<string> ConfiguredJavaTrustPaths(string? dataDir)
    {
        if (dataDir is null)
            return [];

        try
        {
            string configPath = AgentPaths.ConfigPath(dataDir);

            if (!File.Exists(configPath))
                return [];

            return AgentJson.Deserialize<ProxyConfig>(File.ReadAllText(configPath))?.JavaTrustPaths ?? [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not read configured java trust paths: {ex.Message}");

            return [];
        }
    }

    // 卸载时正在运行的 agent 必须先带走：它握的主人租约一断就会自己跑一遍清理，
    // 但那是异步的，而且它清完之后还会继续挂着等下一次 start。直接收掉进程，
    // 剩下的残留由本类的幂等清理兜住 —— 顺序也是先杀进程再还原 hosts，避免它把块又写回去。
    // internal：卸载里最容易出事的一步（拿过期 pid 去杀进程），单独可测。
    internal static void StopLiveAgent(string pidPath)
    {
        if (!File.Exists(pidPath))
            return;

        if (!int.TryParse(File.ReadAllText(pidPath).Trim(), out int pid))
            return;

        // uninstall 命令是活着的 agent 自己处理的，pid 文件里记的就是它自己。
        // 不加这道判断它会把自己 Kill 在清理中途，hosts 和证书就又被留在半截状态。
        if (pid == Environment.ProcessId)
            return;

        try
        {
            using Process process = Process.GetProcessById(pid);

            if (!IsOurAgent(process))
                return;

            AgentLog.Warn($"stopping agent pid {pid} before purge");

            process.Kill(entireProcessTree: true);
            process.WaitForExit(8000);
        }
        catch (ArgumentException)
        {
            // pid 已经没了
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"could not stop agent pid {pid}: {ex.Message}");
        }
    }

    // pid 会被内核复用，只按数字杀等于拿一张过期名单去杀随机路人，必须核对进程名。
    private static bool IsOurAgent(Process process)
    {
        try
        {
            return process.ProcessName.Contains("Cealing-Agent", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Contains("Cealing_Agent", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void RemoveRuntimeFiles(string socketPath, string pidPath)
    {
        TryDelete(socketPath);
        TryDelete(pidPath);
    }

    // 整棵树归还：卸载时用户侧是靠 rm -rf 删这些目录的，只要里面还有一个 root 属主的
    // 子目录（nginx 的 logs/、引擎 pid、NSS 的 cert9.db 都可能是），删除就会停在半路，
    // 残留就永远清不掉。
    // 跳过符号链接：chown 会跟随它，等于让我们有机会改链接目标所属文件的属主，
    // 而那些文件本来不在这次清理的范围内。
    private static void HandOver(string path, int ownerUid)
    {
        if (ownerUid < 0 || !Directory.Exists(path))
            return;

        OwnedFile.HandOver(path, ownerUid);

        // 单个条目的属性读不到（被并发的清理删掉了）不能中断整棵树的归还
        try
        {
            foreach (string file in Directory.EnumerateFiles(path))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    continue;

                OwnedFile.HandOver(file, ownerUid);
            }

            foreach (string dir in Directory.EnumerateDirectories(path))
            {
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                HandOver(dir, ownerUid);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"could not walk {path}: {ex.Message}");
        }
    }

    internal static void PurgeUserFiles(string home, int ownerUid, bool purgeUserData)
    {
        // Chromium/Edge 在 Linux 上只认 ~/.pki/nssdb，这条信任是 GUI 以普通用户身份写进去的，
        // 删数据目录也带不走它（库在 .pki 下）—— 卸载后浏览器会继续信任我们的 MITM 根。
        // certutil 这次是以 root 身份跑的，它会重写用户的 cert9.db，所以事后必须把库归还回去，
        // 否则用户的浏览器连自己的数据库都写不了。
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            string nssDb = Path.Combine(home, ".pki", "nssdb");
            bool exists = Directory.Exists(nssDb);

            Quiet($"purge nss root cert for {home}", () => TrustStore.PurgeNss(home));

            if (exists)
                Quiet($"hand nss db under {home} back to owner", () => HandOver(nssDb, ownerUid));
        }

        // 开机自启条目由 GUI 以普通用户身份写，位置完全在应用目录之外，包管理器看不见它。
        // 留着就是一条指向已删除可执行文件的死条目。
        Quiet($"remove autostart entry under {home}", () => RemoveAutoStartEntry(home));

        // 暂存的 root 侧二进制是应用自己的产物（可达数百 MB），remove 与 purge 都该带走；
        // 数据目录里有用户的上游规则/证书，按 Debian 惯例只有 purge 才删。
        // Windows 上没有这套机制（BinaryStager 只在 root 被文件系统挡住时才复制，见 NeedsStaging），
        // 那边的「暂存路径」没有定义，跳过而不是去猜一个 Linux 形状的路径。
        if (!OperatingSystem.IsWindows())
            Delete(AppPaths.StagedBinRootForHome(home), () => AppPaths.DeleteStagedBin(home));

        if (purgeUserData)
            Delete(AppPaths.DataDirForHome(home), () => AppPaths.DeleteAppFolder(AppPaths.DataDirForHome(home)));
    }

    // 注册表里的值名和主键必须和 GUI 侧 AutoStartManager 一致（文件形态的自启路径见
    // AppPaths.AutostartEntryForHome）：两个项目不能互相引用，所以这里按那份实现逐字抄，
    // 改一边要改另一边。
    private const string WindowsRunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsRunValue = "Sheas-Cealer-Nix";

    private static void RemoveAutoStartEntry(string home)
    {
        // 位置只有一份定义（AppPaths.AutostartEntryForHome），写入方是 GUI 的 AutoStartManager：
        // 两处各算各的就会变成「装了但删不掉」。
        string? entry = AppPaths.AutostartEntryForHome(home);

        if (entry is not null)
        {
            TryDelete(entry);

            return;
        }

        // Windows 的条目是 HKCU 的一个值，不是文件。agent 没有引注册表 API 的依赖
        // （为一个删除值把程序集加进三平台共同的产物不值当），走系统自带的 reg.exe。
        (int exitCode, string output) = TrustStore.Capture(["reg", "delete", WindowsRunKey, "/v", WindowsRunValue, "/f"], "remove autostart registry value");

        if (exitCode != 0)
            AgentLog.Info($"autostart registry value left alone: {output.Trim()}");
    }

    private static void Delete(string path, Action delete)
    {
        // 路径合法性由 AppPaths 把关（只认以应用目录名结尾的路径）：--user-home 传错时
        // 必须拒绝，而不是把别人的目录连锅端走。
        if (!Directory.Exists(path))
            return;

        long size = DirectorySize(path);

        delete();

        AgentLog.Info($"removed {path} ({size / 1024 / 1024} MB)");
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                AgentLog.Info($"removed {path}");
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"could not remove {path}: {ex.Message}");
        }
    }

    private static void Quiet(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AgentLog.Error($"{step} failed: {ex.Message}");
        }
    }
}
