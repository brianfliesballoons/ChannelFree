using Jellyfin.Plugin.QuasiTV.Channels;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.QuasiTV;

/// <summary>Registers QuasiTV's background services with Jellyfin.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<RecordWatcher>();
        serviceCollection.AddHostedService<Startup>();
    }
}
