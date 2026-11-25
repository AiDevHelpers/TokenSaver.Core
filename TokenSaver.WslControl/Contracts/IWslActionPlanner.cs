namespace AiDevTools.TokenSaver.WslControl.Contracts;

using AiDevTools.TokenSaver.WslControl.Models;

/// <summary>
///     Turns a natural-language request into an executable <see cref="WslActionPlan" /> by
///     routing command synthesis through the local model. This is what lets the calling AI say
///     "install ripgrep" instead of authoring <c>sudo apt-get install -y ripgrep</c> itself.
/// </summary>
public interface IWslActionPlanner
{
    /// <summary>
    ///     Plans an action for <paramref name="request" />. <paramref name="distro" /> and
    ///     <paramref name="osHint" /> (e.g. the distro's PRETTY_NAME) are passed to the model so
    ///     it can pick distro-appropriate package names and tools.
    /// </summary>
    Task<WslActionPlan> PlanAsync(
        string request,
        string? distro = null,
        string? osHint = null,
        CancellationToken ct = default);
}
