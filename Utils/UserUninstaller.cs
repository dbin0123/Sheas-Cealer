using Cealing_Core;
using Cealing_Core.Protocol;
using Sheas_Cealer_Nix.Consts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Sheas_Cealer_Nix.Utils;

/// <summary>
/// 用户侧卸载入口（<c>Sheas-Cealer-Nix --uninstall</c>）。
/// </summary>
/// <remarks>
/// 包管理器负责的文件（<c>/opt/Sheas-Cealer-Nix</c>、Inno 的 <c>{app}</c>、desktop 条目）
/// 这里一概不碰，只收本应用自己动过的东西：系统残留（特权部分交给 agent）、用户信任库、
/// 开机自启注册、暂存的 root 侧二进制（可达数百 MB）、以及可选的数据目录。
///
/// 数据目录里有用户自己维护的上游规则，所以默认不删，只有显式 <c>--purge-user-data</c>
/// 才带走 —— 与 deb 的 remove / purge 语义一致。
/// </remarks>
internal static class UserUninstaller
{
    internal static async Task<List<string>> RunAsync(bool purgeUserData)
    {
        List<string> report = [];

        await PurgeSystemResidueAsync(report);

        Step(report, "user trust entries", () => UserTrust.Remove());

        Step(report, "autostart registration", () => AutoStartManager.SetEnabled(false));

        Step(report, "staged engine binaries", () => AppPaths.DeleteStagedBin());

        if (purgeUserData)
            Step(report, "user data dir", () => AppPaths.DeleteAppFolder(AppPaths.DataDir));

        // socket 的属主就是 agent 的主人（即当前用户），普通身份也删得动；
        // 留着它的话任何「agent 是否活着」的判断都会看成假活。
        Step(report, "agent socket", () =>
        {
            if (File.Exists(AgentPaths.SocketPath))
                File.Delete(AgentPaths.SocketPath);
        });

        return report;
    }

    private static async Task PurgeSystemResidueAsync(List<string> report)
    {
        // 优先叫活着的那个 agent 清：特权在它手上，不用再弹一次授权框。
        if (new AgentClient(AgentPaths.SocketPath).IsAlive)
        {
            try
            {
                AgentResponse response = await new AgentClient(AgentPaths.SocketPath).UninstallAsync();

                if (response.Ok)
                {
                    report.Add("system residue: cleaned by the running agent");
                    return;
                }

                // 旧版 agent 回 unknown command，特权清理只能另起一次
                report.Add($"system residue: agent refused ({response.Error}); using an elevated one-shot instead");
            }
            catch (Exception ex)
            {
                report.Add($"system residue: agent unreachable ({ex.Message}); using an elevated one-shot instead");
            }
        }

        if (!File.Exists(MainConst.AgentBinaryPath))
        {
            report.Add($"system residue: agent binary missing at {MainConst.AgentBinaryPath}; let the package manager's uninstall script do it");
            return;
        }

        int exit = PrivilegeEscalator.RunElevatedUninstall(MainConst.AgentBinaryPath, MainConst.DataDir);

        report.Add(exit == 0
            ? "system residue: cleaned by the elevated agent"
            : $"system residue: elevated agent exited with {exit}");
    }

    private static void Step(List<string> report, string what, Action action)
    {
        try
        {
            action();

            report.Add($"{what}: done");
        }
        catch (Exception ex)
        {
            // 卸载的每一步都必须尝试到底，一步失败不能让它后面的步骤跳过
            report.Add($"{what}: failed ({ex.Message})");
        }
    }
}
