namespace Cealing_Core.Protocol;

public static class AgentCommand
{
    public const string Status = "status";
    public const string Start = "start";
    public const string Reload = "reload";
    public const string Stop = "stop";
    public const string Cleanup = "cleanup";
    public const string Ping = "ping";
    public const string Shutdown = "shutdown";

    // GUI 声明「我是这个 agent 的主人，连接在我进程退出时才断」。
    // agent 靠这条长连接判断 GUI 是否还活着：崩溃、被杀、注销、断电都表现为连接断开，
    // 而不需要 GUI 有礼貌地发 shutdown。
    public const string Hold = "hold";
}
