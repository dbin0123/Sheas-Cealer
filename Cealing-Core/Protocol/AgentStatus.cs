namespace Cealing_Core.Protocol;

public sealed class AgentStatus
{
    public bool Running { get; set; }
    public string Engine { get; set; } = "none";
    public bool Coproxy { get; set; }
    public int HttpPort { get; set; }
    public int HttpsPort { get; set; }
    public int RuleCount { get; set; }
    public long Pid { get; set; }

    // agent 启动时所在的 app 目录（= GUI 的 ApplicationBase）。
    // 识别「应用被换目录/重装后仍存活的旧 agent」。
    public string AppDir { get; set; } = string.Empty;

    // agent 启动时被 --config 传入的配置文件路径。
    // 这是复用决策的**关键**字段：应用原地升级时 AppDir 不会变，只有 --config 会变
    // （v1.0.14 把配置从 app bundle 挪到了用户数据目录）。只看 AppDir 会把旧版本 agent
    // 误判成同一个 agent，于是它继续用旧 --config，报
    // 「config not found: <bundle 内路径>」——而 GUI 明明已经把配置写到了新位置。
    public string ConfigPath { get; set; } = string.Empty;

    // agent 实现的协议版本。老版本 agent 不报这个字段（反序列化为 0），
    // GUI 见到 0 就知道不能复用，直接退役重拉。
    public int ProtocolVersion { get; set; }

    // agent 自己有没有管理员/root 特权。默认 false 是故意的：
    // 老版本 agent 不上报（反序列化为 false）就当不可复用，
    // 而 UAC 被拒时拉起来的普通令牌 agent 也走同一条路，
    // 否则 GUI 会复用它，全局伪造表现为「点了没反应」，日志里只剩一句 Access is denied。
    public bool Elevated { get; set; }
}
