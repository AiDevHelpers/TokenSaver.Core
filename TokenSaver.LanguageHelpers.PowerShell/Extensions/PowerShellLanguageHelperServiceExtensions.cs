using TokenSaver.LanguageHelpers.Core.Contracts;
using TokenSaver.LanguageHelpers.Core.Extensions;

using Microsoft.Extensions.DependencyInjection;

namespace TokenSaver.LanguageHelpers.PowerShell.Extensions;

/// <summary>DI wiring for the PowerShell language helper.</summary>
public static class PowerShellLanguageHelperServiceExtensions
{
    /// <summary>Registers the Core services and the PowerShell <see cref="ILanguageHelper" />.</summary>
    public static IServiceCollection AddPowerShellLanguageHelper(this IServiceCollection services)
    {
        services.AddLanguageHelpersCore();
        services.AddSingleton<ILanguageHelper, PowerShellLanguageHelper>();
        return services;
    }
}
