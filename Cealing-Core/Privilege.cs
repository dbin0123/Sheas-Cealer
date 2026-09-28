using System;

namespace Cealing_Core;

/// <summary>
/// 特权判定。GUI 与 agent 共用一份实现：
/// 两边各自判断一次的话，「GUI 以为自己提权了、agent 其实没有」这种缝隙
/// 就会变成全局伪造静默失效（写 hosts 得到 Access is denied）。
/// </summary>
public static class Privilege
{
    /// <summary>
    /// 当前进程是否持有管理员/root 特权令牌。
    /// Windows 上普通账户即使属于 Administrators，UAC 过滤令牌的 IsInRole 也返回 false
    /// （已在本机 .NET Framework 与 .NET 8 上分别验证），这正是我们想要的语义。
    /// </summary>
    public static bool IsElevated => OperatingSystem.IsWindows()
        ? new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
        : Environment.UserName == "root";

    /// <summary>
    /// 纯函数版的权限前置检查：把「缺什么」翻译成人能照做的下一步。
    /// 返回 null 表示放行，否则是不能继续的原因（直接进 agent 日志与 GUI 弹窗）。
    /// </summary>
    public static string? RequireElevated(bool elevated, string platform) => elevated
        ? null
        : $"agent 当前未提权（platform={platform}），没有管理员权限：写不了 hosts，也绑不了 80/443。"
          + "请重新点击并在系统授权框（UAC / 密码提示）里允许提权。";
}
