using Cealing_Core;
using System;
using System.Diagnostics;
using System.IO;

namespace Cealing_Agent.Engines;

/// <summary>
/// 引擎子进程（mihomo / nginx）的孤儿防护。
/// </summary>
/// <remarks>
/// 这两个引擎都是 agent 的**子进程**，但 agent 自己是被 GUI 甩到后台的独立会话，
/// 所以 agent 一旦被 SIGKILL 或崩溃，内核不会替它收掉孩子：现场就出现过
/// mihomo 活得比 agent 久、继续占着 TUN 和端口的情况，用户表现为「界面早关了，网还是上不去」。
///
/// 两道兜底：
/// - Windows 用 Job Object（KILL_ON_JOB_CLOSE），句柄随进程消失，内核直接收掉孩子，不靠我们配合；
/// - 其余平台（以及 Windows 上的双保险）在数据目录留 pid 记录，下次 agent 启动时清扫。
/// </remarks>
internal static class EngineGuard
{
    internal readonly record struct Record(int Pid, string Name, string BinaryPath);

    private static string Dir(string dataDir) => Path.Combine(dataDir, "engines");

    /// <summary>
    /// 登记一个刚启动的引擎。写失败绝不能影响引擎本身：登记只是给「下次开机清扫」留线索。
    /// </summary>
    internal static void Register(string dataDir, string name, Process process, string binaryPath)
    {
        // 先让内核负责（Windows），再做记录，顺序反过来的话中途抛错就两头都没保住
        WindowsProcessJob.Assign(process.Id);

        try
        {
            Directory.CreateDirectory(Dir(dataDir));

            File.WriteAllText(
                Path.Combine(Dir(dataDir), name + ".pid"),
                $"{process.Id}{Environment.NewLine}{process.ProcessName}{Environment.NewLine}{binaryPath}");
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"engine registry write failed ({name}): {ex.Message}");
        }
    }

    internal static void Unregister(string dataDir, string name)
    {
        try
        {
            File.Delete(Path.Combine(Dir(dataDir), name + ".pid"));
        }
        catch { }
    }

    /// <summary>
    /// agent 启动时清扫上一轮遗留的引擎进程。
    ///
    /// 只杀「pid 确实还活着，而且能证明它就是当初登记的那个进程」的：pid 会被内核复用，
    /// 光按数字杀等于拿一张过期名单去杀随机路人。
    /// </summary>
    internal static void ReapOrphans(string dataDir)
    {
        string dir = Dir(dataDir);

        if (!Directory.Exists(dir))
            return;

        foreach (string file in Directory.EnumerateFiles(dir, "*.pid"))
        {
            Record? record = Read(file);

            if (record is not null)
                Reap(record.Value, file);
            else
                TryDelete(file);
        }
    }

    private static void Reap(Record record, string file)
    {
        Process? process = TryGetProcess(record.Pid);

        if (process is null)
        {
            // 进程已经没了（自己退了，或 pid 已被回收），记录留着只会误导下一轮
            TryDelete(file);

            return;
        }

        using (process)
        {
            if (!IsSameEngine(process, record))
            {
                AgentLog.Warn($"stale engine record {file} points at pid {record.Pid} which is no longer {record.Name}; leaving it alone");

                TryDelete(file);

                return;
            }

            try
            {
                AgentLog.Warn($"reaping orphaned engine {record.Name} (pid {record.Pid}) left by a previous agent");

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                AgentLog.Error($"reaping {record.Name} (pid {record.Pid}) failed: {ex.Message}");
            }
        }

        TryDelete(file);
    }

    private static Process? TryGetProcess(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);

            return process.HasExited ? null : process;
        }
        catch (ArgumentException)
        {
            // 该 pid 不存在
            return null;
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"cannot inspect pid {pid}: {ex.Message}");

            return null;
        }
    }

    // 判定「这个 pid 是不是登记的那个引擎」。
    //
    // 首选可执行文件路径：agent 以 root/管理员身份运行，读得到别属主的进程路径，这是最强证据。
    // 路径读不到时（权限、进程正在退出）退回进程名比对，但要容忍 Linux 的截断：
    // comm 只有 15 个字符，"Cealing-Comihomo" 会变成 "Cealing-Comihom"，
    // 精确相等永远不成立，所以这里按前缀关系判等。
    private static bool IsSameEngine(Process process, Record record)
    {
        string? actualPath = TryGetMainModulePath(process);

        if (actualPath is not null)
            return string.Equals(AppPaths.Normalize(actualPath), AppPaths.Normalize(record.BinaryPath), StringComparison.OrdinalIgnoreCase);

        string actualName;

        try
        {
            actualName = process.ProcessName;
        }
        catch (Exception)
        {
            return false;
        }

        return PrefixMatch(record.Name, actualName);
    }

    private static bool PrefixMatch(string left, string right) =>
        left.Length >= 8 &&
        (left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal));

    private static string? TryGetMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Record? Read(string file)
    {
        try
        {
            string[] lines = File.ReadAllLines(file);

            if (lines.Length < 3 || !int.TryParse(lines[0].Trim(), out int pid))
                return null;

            return new Record(pid, lines[1].Trim(), lines[2].Trim());
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"unreadable engine record {file}: {ex.Message}");

            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch { }
    }
}
