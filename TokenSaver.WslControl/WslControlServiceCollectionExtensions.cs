namespace TokenSaver.WslControl;

using Community.Wsl.Sdk;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TokenSaver.WslControl.Contracts;
using TokenSaver.WslControl.Core;

/// <summary>
///     DI wiring for the WSL provider. The one collaborator this library deliberately does not
///     register is <see cref="IWslCommandModel" /> — the host binds that to its local model
///     (SimpleModelServer/Ollama) so this library carries no dependency on the model transport.
/// </summary>
public static class WslControlServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the WSL provider, command executor, action planner, and debug-config service.
    ///     Call after registering an <see cref="IWslCommandModel" /> implementation; the planner
    ///     depends on it.
    /// </summary>
    public static IServiceCollection AddWslControl(this IServiceCollection services)
    {
        // SDK entry point. WslApi reads the registry to enumerate distros; a single instance is fine.
        services.TryAddSingleton<IWslApi>(_ => new WslApi());

        services.TryAddSingleton<IWslCommandExecutor, SdkWslCommandExecutor>();
        services.TryAddSingleton<IWslProvider, WslProvider>();
        services.TryAddSingleton<IWslActionPlanner, WslActionPlanner>();
        services.TryAddSingleton<IWslDebugConfigService, WslDebugConfigService>();

        return services;
    }
}
