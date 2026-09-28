using Cealing_Core;
using Cealing_Agent.Engines;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

// 证书由 EngineSupervisor 持有并在 StopAsync 里释放，adapter 只借用，不能自己 dispose
internal sealed class BuiltinEngineAdapter(BuiltinEngine engine, Func<ProxyCertificate> certificateFactory) : IProxyEngine
{
    public bool IsRunning => engine.IsRunning;

    public async Task StartAsync(ProxyConfig config, CancellationToken cancellationToken)
    {
        ProxyCertificate certificate = certificateFactory();

        await engine.StartAsync(config, certificate.Child);
    }

    public Task StopAsync() => engine.StopAsync();
}

internal sealed class ExternalNginxAdapter(ExternalNginxEngine engine) : IProxyEngine
{
    public bool IsRunning => engine.IsRunning;

    public Task StartAsync(ProxyConfig config, CancellationToken cancellationToken) => engine.StartAsync(config, cancellationToken);

    public Task StopAsync()
    {
        engine.Stop();

        return Task.CompletedTask;
    }
}
