using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Cealing_Agent;

/// <summary>
/// 把 root 写出来的文件归还给用户，否则 GUI 侧以普通用户身份再写同一个文件会 Permission denied。
/// </summary>
/// <remarks>
/// 数据目录（如 <c>~/Library/Application Support/Sheas-Cealer-Nix</c>）归用户所有，
/// 但 nginx.conf / config.yaml / 证书都是 root agent 写的。旧版本这些文件在 app bundle 里，
/// 现在移到数据目录后，GUI 仍要能覆盖 nginx.conf（MainWin）和 config.yaml（MihomoConfWatcher），
/// 所以写完必须 chown 回用户。
/// </remarks>
internal static class OwnedFile
{
    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint owner, uint group);

    private static void Chown(string path, int ownerUid)
    {
        // 只有「未知」（-1）才跳过。0 是一个完全合法的 uid：GUI 自己以 root 跑的时候，
        // 主人就是 root，把 0 当成「没有主人」跳过 chown，socket 就停在 agent 的属主上，
        // 于是同一个 uid 反而连不上自己拉起来的 agent。
        if (OperatingSystem.IsWindows() || ownerUid < 0)
            return;

        // group 传 (uint)-1 表示保持不变
        chown(path, (uint)ownerUid, unchecked((uint)-1));
    }

    /// <summary>
    /// 写文件并把 owner 设成用户。失败不抛：chown 只是「让用户也能改」的锦上添花，
    /// 写成功才是关键。
    /// </summary>
    internal static void WriteAllText(string path, string content, int ownerUid)
    {
        File.WriteAllText(path, content);

        HandOver(path, ownerUid);
    }

    /// <summary>把已存在的文件（或目录）交给用户，用于分步写盘的场景。</summary>
    internal static void HandOver(string path, int ownerUid)
    {
        try
        {
            Chown(path, ownerUid);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"could not chown {path} to uid {ownerUid}: {ex.Message}");
        }
    }
}
