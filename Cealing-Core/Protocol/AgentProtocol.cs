namespace Cealing_Core.Protocol;

/// <summary>
/// GUI 与 root agent 之间的 socket 协议版本。
/// </summary>
/// <remarks>
/// 只要 agent 启动参数（--config / --data-dir）语义发生变化、或 Status 字段含义变化，
/// 就要 +1。老版本 agent 不带版本号，反序列化后为 0，GUI 会直接退役它并重拉，
/// 避免「新 GUI 复用旧 agent」这类只在原地升级时才暴露的故障。
/// </remarks>
public static class AgentProtocol
{
    // 1 = 引入 AppDir / ConfigPath / ProtocolVersion 身份字段与 --data-dir
    // 2 = 引入 Elevated（agent 自报特权状态）：普通令牌的 agent 一律不复用
    // 3 = 引入 hold（主人存活租约）：GUI 崩了/被杀时 agent 自己拆掉引擎、还原 hosts、拆证书后退出。
    //     没有租约的旧 agent 会永久赖在特权态，所以绝不能再复用它。
    public const int Version = 3;
}
