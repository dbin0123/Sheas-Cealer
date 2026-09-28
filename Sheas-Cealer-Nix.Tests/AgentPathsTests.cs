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
