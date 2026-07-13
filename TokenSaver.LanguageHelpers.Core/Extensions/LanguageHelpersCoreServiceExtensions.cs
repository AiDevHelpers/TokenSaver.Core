using TokenSaver.LanguageHelpers.Core.Contracts;
using TokenSaver.LanguageHelpers.Core.Process;
using TokenSaver.LanguageHelpers.Core.Registry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TokenSaver.LanguageHelpers.Core.Extensions;

/// <summary>
///     DI wiring for the language-helpers Core layer: the shared <see cref="IProcessRunner" /> and the
///     <see cref="ILanguageHelperRegistry" />. The per-language projects each add their own
///     <c>AddCSharpLanguageHelper()</c> / <c>AddPowerShellLanguageHelper()</c> / <c>AddPythonLanguageHelper()</c>
///     which register an <see cref="ILanguageHelper" />; the registry aggregates whatever was added.
/// </summary>
public static class LanguageHelpersCoreServiceExtensions
{
    /// <summary>
    ///     Registers the process runner and the language-helper registry. Idempotent — safe to call from
    ///     each language project's registration so callers can add just the languages they need.
    /// </summary>
    public static IServiceCollection AddLanguageHelpersCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<ILanguageHelperRegistry, LanguageHelperRegistry>();
        return services;
    }
}
