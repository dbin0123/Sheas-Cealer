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

    // 卸载专用：停引擎 + 幂等清理系统残留（根证书、hosts、/tmp 的 socket 与 pid），然后退出。
    // 与 cleanup 的区别是它不看「本次进程装过什么」的内存标记，所以能收掉上一任 agent
    // 被 SIGKILL/崩溃留下的脏系统。旧版 agent 不认这条命令，会回 unknown command，
    // 调用方据此退回「提权跑 Cealing-Agent --uninstall」。
    public const string Uninstall = "uninstall";


    // GUI 声明「我是这个 agent 的主人，连接在我进程退出时才断」。
    // agent 靠这条长连接判断 GUI 是否还活着：崩溃、被杀、注销、断电都表现为连接断开，
    // 而不需要 GUI 有礼貌地发 shutdown。
    public const string Hold = "hold";
}
