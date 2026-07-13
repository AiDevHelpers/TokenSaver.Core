using TokenSaver.LanguageHelpers.Core.Contracts;
using TokenSaver.LanguageHelpers.Core.Extensions;

using Microsoft.Extensions.DependencyInjection;

namespace TokenSaver.LanguageHelpers.Python.Extensions;

/// <summary>DI wiring for the Python language helper.</summary>
public static class PythonLanguageHelperServiceExtensions
{
    /// <summary>Registers the Core services and the Python <see cref="ILanguageHelper" />.</summary>
    public static IServiceCollection AddPythonLanguageHelper(this IServiceCollection services)
    {
        services.AddLanguageHelpersCore();
        services.AddSingleton<ILanguageHelper, PythonLanguageHelper>();
        return services;
    }
}
