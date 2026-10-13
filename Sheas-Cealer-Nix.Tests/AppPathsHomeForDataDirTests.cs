using Cealing_Core;
using System;
using System.IO;
using Xunit;

namespace Sheas_Cealer_Nix.Tests;

/// <summary>
/// 从数据目录反推用户家目录 —— agent 以 root 运行，<c>$HOME</c> 是 /root，
/// 只能靠这个找回 <c>~/.jdks</c>、Toolbox 运行时之类的路径。
/// </summary>
public class AppPathsHomeForDataDirTests
{
    [Fact]
    public void StripsThePlatformDataDirSuffix()
    {
        // 断言必须按当前平台的约定目录来造，HomeForDataDir 内部就是按平台选的。
        string dataDir;
        string expectedHome;

        if (OperatingSystem.IsWindows())
        {
            dataDir = @"C:\Users\me\AppData\Roaming\Sheas-Cealer-Nix";
            expectedHome = @"C:\Users\me";
        }
        else if (OperatingSystem.IsMacOS())
        {
            dataDir = "/Users/me/Library/Application Support/Sheas-Cealer-Nix";
            expectedHome = "/Users/me";
        }
        else
        {
            dataDir = "/home/me/.config/Sheas-Cealer-Nix";
            expectedHome = "/home/me";
        }

        Assert.Equal(expectedHome, AppPaths.HomeForDataDir(dataDir));

        // 安装脚本传路径时经常带个尾部分隔符，反推不能因此变成 null（那等于不装 Java 根证书）。
        Assert.Equal(expectedHome, AppPaths.HomeForDataDir(dataDir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ReturnsNullForAnythingThatIsNotOurDataDir()
    {
        // 用户把数据目录指到别处（--data-dir / XDG_CONFIG_HOME）时不能乱拿一个前缀当 Home，
        // 否则 Java 信任库的自动发现会去翻一个完全无关的目录树。
        Assert.Null(AppPaths.HomeForDataDir("/home/me/elsewhere/Sheas-Cealer-Nix"));
        Assert.Null(AppPaths.HomeForDataDir(string.Empty));
    }
}
