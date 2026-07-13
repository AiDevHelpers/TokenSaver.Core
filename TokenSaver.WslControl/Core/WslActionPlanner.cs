namespace TokenSaver.WslControl.Core;

using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using TokenSaver.WslControl.Contracts;
using TokenSaver.WslControl.Models;

/// <summary>
///     Default <see cref="IWslActionPlanner" />. Sends the caller's natural-language request to
///     the local model with a strict JSON contract and turns the reply into a
///     <see cref="WslActionPlan" />. Parsing is defensive: fenced code blocks are stripped, and
///     any malformed/uncertain reply degrades to <see cref="WslActionKind.Unknown" /> rather than
///     inventing a command. The planner never executes anything — it only proposes.
/// </summary>
public sealed partial class WslActionPlanner(
    IWslCommandModel model,
    ILogger<WslActionPlanner> logger) : IWslActionPlanner
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Planning WSL action for request: {Request}")]
    private static partial void LogPlanning(ILogger logger, string request);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WSL action planning failed — returning Unknown")]
    private static partial void LogPlanningFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Local model returned unparseable plan: {Raw}")]
    private static partial void LogUnparseable(ILogger logger, string raw);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Heuristic backstop: even if the model says needsConfirmation=false, these mutating verbs
    // force confirmation so a mis-classified destructive command can't auto-run.
    private static readonly Regex MutatingCommand = MyMutatingRegex();

    private readonly IWslCommandModel _model = model;
    private readonly ILogger<WslActionPlanner> _logger = logger;

    [GeneratedRegex(
        @"\b(rm|rmdir|mkfs|dd|shred|apt|apt-get|dpkg|pip|npm|snap|systemctl|mv|chmod|chown|sudo|tee|truncate)\b|>>|(?<!\d)>(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MyMutatingRegex();

    public async Task<WslActionPlan> PlanAsync(
        string request,
        string? distro = null,
        string? osHint = null,
        CancellationToken ct = default)
    {
        LogPlanning(_logger, request);

        string osClause = string.IsNullOrWhiteSpace(osHint) ? "" : $" running {osHint}";

        string system = $$"""
            You translate a natural-language request into ONE Linux shell command to run inside a
            Windows Subsystem for Linux (WSL) distribution{{osClause}}.

            Respond with ONLY a single minified JSON object, no prose, no markdown fences:
            {"action":"<action>","command":"<bash>","packages":["<pkg>"],"explanation":"<short>","needsConfirmation":<bool>}

            Rules:
            - "action" is one of: run, apt-install, apt-update, apt-upgrade, build, unknown.
            - "command" is the exact bash command line. Do NOT include a leading "sudo" for apt
              actions — the caller adds privilege handling. For "run"/"build", include what is needed.
            - "packages" lists apt package names for apt-install (else []).
            - "needsConfirmation" is true if the command installs/removes software, uses sudo, writes
              or deletes files, or changes system state. Read-only inspection is false.
            - If you cannot confidently produce a command, use action "unknown" with an empty command.
            - Never invent destructive commands. Prefer the simplest command that satisfies the request.
            """;

        string user = $"Request: {request}";

        string raw;
        try
        {
            raw = await _model.CompleteAsync(system, user, ct);
        }
        catch (Exception ex)
        {
            LogPlanningFailed(_logger, ex);
            return WslActionPlan.UnknownPlan(
                "The local model could not be reached to plan this action. Provide an explicit command instead.");
        }

        WslActionPlan? plan = TryParse(raw);
        if (plan is null)
        {
            LogUnparseable(_logger, raw);
            return WslActionPlan.UnknownPlan(
                "The local model did not return a usable plan. Rephrase the request or provide an explicit command.");
        }

        return plan;
    }

    private static WslActionPlan? TryParse(string raw)
    {
        string json = StripFences(raw).Trim();
        int start = json.IndexOf('{', StringComparison.Ordinal);
        int end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        json = json[start..(end + 1)];

        RawPlan? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawPlan>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null)
        {
            return null;
        }

        WslActionKind kind = (parsed.Action ?? "unknown").Trim().ToLowerInvariant() switch
        {
            "run" => WslActionKind.RunCommand,
            "apt-install" or "apt_install" or "install" => WslActionKind.AptInstall,
            "apt-update" or "apt_update" or "update" => WslActionKind.AptUpdate,
            "apt-upgrade" or "apt_upgrade" or "upgrade" => WslActionKind.AptUpgrade,
            "build" => WslActionKind.Build,
            _ => WslActionKind.Unknown
        };

        string command = (parsed.Command ?? string.Empty).Trim();
        IReadOnlyList<string> packages = parsed.Packages?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];

        if (kind == WslActionKind.Unknown && command.Length == 0 && packages.Count == 0)
        {
            return WslActionPlan.UnknownPlan(
                string.IsNullOrWhiteSpace(parsed.Explanation)
                    ? "The request could not be translated into a command."
                    : parsed.Explanation);
        }

        // Confirmation is the union of the model's own verdict and the mutating-command backstop —
        // and every non-read action category always confirms.
        bool mutatingByCategory = kind is WslActionKind.AptInstall or WslActionKind.AptUpdate
            or WslActionKind.AptUpgrade;
        bool requiresConfirmation = parsed.NeedsConfirmation
            || mutatingByCategory
            || (command.Length > 0 && MutatingCommand.IsMatch(command));

        string? warning = command.Length > 0 && MutatingCommand.IsMatch(command) && !parsed.NeedsConfirmation
            ? "Command looks like it modifies the system though the model marked it safe — review before running."
            : null;

        return new WslActionPlan(
            kind,
            command,
            packages,
            string.IsNullOrWhiteSpace(parsed.Explanation) ? "(no explanation provided)" : parsed.Explanation.Trim(),
            requiresConfirmation,
            warning);
    }

    private static string StripFences(string text)
    {
        string trimmed = text.Trim();
        return trimmed.StartsWith("```", StringComparison.Ordinal)
            ? Regex.Replace(trimmed, @"^```\w*\n?|\n?```$", "", RegexOptions.Multiline)
            : trimmed;
    }

    private sealed class RawPlan
    {
        public string? Action { get; set; }
        public string? Command { get; set; }
        public List<string>? Packages { get; set; }
        public string? Explanation { get; set; }
        public bool NeedsConfirmation { get; set; }
    }
}
