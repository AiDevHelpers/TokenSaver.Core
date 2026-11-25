using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.OllamaIntegration.Services;
using AiDevTools.TokenSaver.TokenEconomy.Contracts;
using AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.Extensions.Options;

using System.Diagnostics;

namespace AiDevTools.TokenSaver.TokenEconomy.Services;

/// <summary>
///     Default <see cref="IDistillationService" />: role-specific compression prompts (normal +
///     terse variants), the per-feature kill switches in <see cref="TokenEconomyConfig" />, and
///     automatic savings recording via <see cref="ISavingsTracker" />. Every tool that produces
///     bulk text (RunTests, LintFix, ContainerLogs, DiffSummarize, BuildWatch, SessionSnapshot)
///     calls this instead of returning raw text directly.
/// </summary>
public sealed class DistillationService(
    OllamaService ollama,
    ModelRouteResolver modelRoutes,
    ISavingsTracker savingsTracker,
    IOptions<TokenSaverConfig> config) : IDistillationService
{
    private const string TersePrefix =
        "Answer in fragments. Drop articles, filler words, and preamble. " +
        "No \"here is\" / \"this shows\". Telegraphic style — verdict first, no restating the question.\n\n";

    private readonly TokenEconomyConfig _config = config.Value.TokenEconomy;

    public int EstimateTokens(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);
    }

    public async Task<DistillResult> DistillAsync(
        string workspaceRoot,
        string tool,
        DistillRole role,
        string raw,
        int maxTokens = 300,
        bool? terse = null,
        string? model = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new DistillResult("(empty)", 0, 0, false, true);
        }

        // Feature kill-switch wins even over an explicit per-call terse:true — if EnableTerse is
        // false, no caller can force terse mode on.
        bool useTerse = _config.EnableTerse && (terse ?? _config.Terse);

        string capped = raw.Length > _config.RawTextCapChars ? raw[.._config.RawTextCapChars] : raw;
        int rawTokens = EstimateTokens(raw);

        // Master kill-switch: skip the Ollama call entirely and hand back raw text (still capped)
        // if distillation is turned off server-wide.
        if (!_config.EnableDistill)
        {
            int cappedTokens = EstimateTokens(capped);
            await savingsTracker.RecordAsync(workspaceRoot, tool, RoleKey(role), useTerse, rawTokens, cappedTokens, 0, ct: ct);
            return new DistillResult(capped, rawTokens, cappedTokens, useTerse, false);
        }

        string resolvedModel = model ?? modelRoutes.Resolve("summarize");
        int effectiveMaxTokens = useTerse ? (int)Math.Round(maxTokens * _config.TerseMaxTokensRatio) : maxTokens;
        string prompt = BuildPrompt(role, useTerse, capped);

        Stopwatch sw = Stopwatch.StartNew();
        string summary = await ollama.CallModelAsync(resolvedModel, prompt, systemPrompt: null, ct: ct);
        sw.Stop();

        int distilledTokens = EstimateTokens(summary);
        await savingsTracker.RecordAsync(
            workspaceRoot, tool, RoleKey(role), useTerse, rawTokens, distilledTokens, (int)sw.ElapsedMilliseconds, ct: ct);

        return new DistillResult(summary, rawTokens, distilledTokens, useTerse, true);
    }

    private static string RoleKey(DistillRole role)
    {
        return role switch
        {
            DistillRole.TestFailure => "test_failure",
            DistillRole.LogErrors => "log_errors",
            DistillRole.DiffSummary => "diff_summary",
            DistillRole.LintSummary => "lint_summary",
            _ => "generic_condense"
        };
    }

    private static string BuildPrompt(DistillRole role, bool terse, string raw)
    {
        return terse ? TersePrompt(role, raw) : NormalPrompt(role, raw);
    }

    private static string NormalPrompt(DistillRole role, string raw)
    {
        return role switch
        {
            DistillRole.TestFailure =>
                "You are compressing test output for an AI coding assistant that pays per token. " +
                "Extract ONLY: (1) which tests failed, (2) the one-line reason for each, (3) the single most likely " +
                "file/line to fix. Omit passing tests, stack trace noise, and framework boilerplate. " +
                $"Max 6 lines total.\n\nRAW OUTPUT:\n{raw}",

            DistillRole.LogErrors =>
                "Extract only new/anomalous error or warning lines from this log. " +
                $"Deduplicate repeated errors into one line with a count. Ignore routine INFO/DEBUG noise. Max 8 lines.\n\nLOG:\n{raw}",

            DistillRole.DiffSummary =>
                "Summarize this git diff as a commit message: one-line imperative summary, " +
                $"then up to 4 bullets for notable changes. No preamble.\n\nDIFF:\n{raw}",

            DistillRole.LintSummary =>
                "Group these lint results by rule. Report rule name, count, and ONE representative " +
                $"file:line per rule. Drop anything auto-fixed already.\n\nLINT OUTPUT:\n{raw}",

            _ => $"Condense this to the minimum a developer needs to decide their next action. Max 5 lines.\n\nCONTENT:\n{raw}"
        };
    }

    private static string TersePrompt(DistillRole role, string raw)
    {
        return role switch
        {
            DistillRole.TestFailure =>
                $"{TersePrefix}Failed tests + 1-line reason each + most likely fix file:line. " +
                $"Max 4 lines. Skip passing tests, stack noise.\n\nRAW:\n{raw}",

            DistillRole.LogErrors =>
                $"{TersePrefix}New/anomalous errors only, dedup repeats w/ count. Max 4 lines.\n\nLOG:\n{raw}",

            DistillRole.DiffSummary =>
                $"{TersePrefix}One-line commit summary + up to 2 bullets max.\n\nDIFF:\n{raw}",

            DistillRole.LintSummary =>
                $"{TersePrefix}Rule name + count + one file:line per rule. Max 4 lines.\n\nLINT:\n{raw}",

            _ => $"{TersePrefix}Absolute minimum to decide next action. Max 3 lines.\n\n{raw}"
        };
    }
}
