using Cealing_Core;
using System.Threading;
using System.Threading.Tasks;

namespace Cealing_Agent;

internal interface IProxyEngine
{
    bool IsRunning { get; }

    Task StartAsync(ProxyConfig config, CancellationToken cancellationToken);
    Task StopAsync();
}
