using Cealing_Core;
using Cealing_Core.Protocol;
using Sheas_Cealer_Nix.Consts;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

// 清理动作（hosts 标记块 + 根证书）需要特权，而新权限模型里只有 agent 持有特权：
// GUI 故意不再提权，自己动 hosts / 机器级 Root 证书存储只会得到
// UnauthorizedAccessException / CryptographicException「拒绝访问」
// （以前每次启动都往 error log 里写一条，且什么都没清掉）。
internal static class NginxCleaner
{
    internal static async Task Clean()
    {
        try
        {
            AgentResponse response = await new AgentClient(AgentPaths.SocketPath).CleanupAsync();

            if (response.Ok)
                return;
        }
        catch
        {
            // agent 不可达：启动/退出清理失败不该阻断流程，往下看不提权能不能自己清
        }

        // 没有可用的特权 agent。GUI 自己也不是管理员就什么都做不了，直接返回。
        if (!Privilege.IsElevated)
            return;

        // 老用法兜底：直接以管理员/root 跑 GUI（Windows 老版本、或 sudo 起 GUI）时，
        // 特权就在本进程里，按原实现清理。
        if (!OperatingSystem.IsWindows())
            return;

        string hostsContent = await File.ReadAllTextAsync(MainConst.HostsConfPath);
        int hostsConfStartIndex = hostsContent.IndexOf(MainConst.HostsConfStartMarker, StringComparison.Ordinal);
        int hostsConfEndIndex = hostsContent.LastIndexOf(MainConst.HostsConfEndMarker, StringComparison.Ordinal);

        if (hostsConfStartIndex != -1 && hostsConfEndIndex != -1)
            await File.WriteAllTextAsync(MainConst.HostsConfPath, hostsContent.Remove(hostsConfStartIndex, hostsConfEndIndex - hostsConfStartIndex + MainConst.HostsConfEndMarker.Length));

        using X509Store certStore = new(StoreName.Root, StoreLocation.LocalMachine, OpenFlags.ReadWrite);

        foreach (X509Certificate2 storedCert in certStore.Certificates.Cast<X509Certificate2>().ToArray())
        {
            if (storedCert.Subject != MainConst.NginxRootCertSubjectName)
                continue;

            // 原来这里是 while (true) { try { Remove } catch { } }：
            // 一旦 Remove 持续失败就是死循环，界面表现成「启动后一直没反应」。
            try
            {
                certStore.Remove(storedCert);
            }
            catch
            {
            }
        }

        certStore.Close();
    }
}
