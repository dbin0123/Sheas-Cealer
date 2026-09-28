using System;
using System.IO;

namespace Cealing_Core;

public static class AgentPaths
{
    public const string SocketFileName = "cealing-agent.sock";
    public const string ConfigFileName = "cealing-proxy.json";
    public const string LogFileName = "cealing-agent.log";
    public const string PidFileName = "cealing-agent.pid";

    public static string SocketPath => Path.Combine(Path.GetTempPath(), SocketFileName);
    public static string PidPath => Path.Combine(Path.GetTempPath(), PidFileName);

    public static string ConfigPath(string appDir) => Path.Combine(appDir, ConfigFileName);
    public static string LogPath(string appDir) => Path.Combine(appDir, LogFileName);
}
