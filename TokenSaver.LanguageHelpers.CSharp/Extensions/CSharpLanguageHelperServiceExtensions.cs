using TokenSaver.LanguageHelpers.Core.Contracts;
using TokenSaver.LanguageHelpers.Core.Extensions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TokenSaver.LanguageHelpers.CSharp.Extensions;

/// <summary>DI wiring for the C# language helper. Pulls in the Core layer, then registers the helper.</summary>
public static class CSharpLanguageHelperServiceExtensions
{
    /// <summary>Registers the Core services and the C# <see cref="ILanguageHelper" />.</summary>
    public static IServiceCollection AddCSharpLanguageHelper(this IServiceCollection services)
    {
        services.AddLanguageHelpersCore();
        services.AddSingleton<ILanguageHelper, CSharpLanguageHelper>();
        return services;
    }
}
