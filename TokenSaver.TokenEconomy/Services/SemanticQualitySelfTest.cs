using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;

using Microsoft.EntityFrameworkCore;

using System.Globalization;
using System.Text;

namespace TokenSaver.TokenEconomy.Services;

/// <summary>One fixture function's outcome in <see cref="SemanticQualitySelfTest" />.</summary>
public sealed record SemanticQualityCaseResult(
    string File,
    bool SummaryOk,
    bool RetrievalOk,
    string Summary,
    string TopHit);

/// <summary>Result of <see cref="SemanticQualitySelfTest.RunAsync" />.</summary>
public sealed record SemanticQualityResult(
    bool Passed,
    IReadOnlyList<SemanticQualityCaseResult> Cases,
    string Narrative);

/// <summary>
///     Checks that the semantic indexer describes code correctly, not just that it stores chunks
///     (that is <see cref="IndexSelfTest" />). It indexes a scratch folder of small functions whose
///     names say nothing about what they do, then verifies for each one that (1) the stored summary
///     mentions at least one term from the function's real behavior and (2) a plain-English query
///     about that behavior ranks that function's chunk first. It also fails if every function got
///     the same summary, which is what a constant or ignored-prompt model produces. No second model
///     judges the output, so the result is deterministic for a given model.
/// </summary>
public sealed class SemanticQualitySelfTest(
    IRepoIndexer indexer, ISemanticSearchService search, TokenEconomyDbAccessor db)
{
    private sealed record FixtureCase(string File, string Name, string Code, string[] Keywords, string Query);

    // Names are deliberately neutral so a summary has to come from the body, not the identifier.
    private static readonly FixtureCase[] Cases =
    [
        new("Apply.cs", "Apply",
            "public static async Task<string> Apply(Func<Task<string>> call)\n{\n" +
            "    for (int attempt = 0; attempt < 5; attempt++)\n    {\n" +
            "        try { return await call(); }\n" +
            "        catch (HttpRequestException) { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); }\n" +
            "    }\n    throw new TimeoutException(\"gave up\");\n}\n",
            ["retry", "retries", "retrying", "backoff", "again", "repeat"],
            "retry a failing network call with exponential backoff"),
        new("Resolve.cs", "Resolve",
            "public static string Resolve(string text)\n{\n" +
            "    byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));\n" +
            "    return Convert.ToHexString(bytes);\n}\n",
            ["hash", "sha", "digest", "checksum"],
            "compute a SHA-256 hash of a string as hex"),
        new("Execute.cs", "Execute",
            "public static double Execute(double celsius)\n{\n    return celsius * 9.0 / 5.0 + 32.0;\n}\n",
            ["celsius", "fahrenheit", "temperature"],
            "convert a temperature from celsius to fahrenheit"),
        new("Handle.cs", "Handle",
            "public static List<string> Handle(string line)\n{\n" +
            "    List<string> fields = new();\n    StringBuilder current = new();\n    bool quoted = false;\n" +
            "    foreach (char c in line)\n    {\n" +
            "        if (c == '\"') { quoted = !quoted; }\n" +
            "        else if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }\n" +
            "        else { current.Append(c); }\n    }\n" +
            "    fields.Add(current.ToString());\n    return fields;\n}\n",
            ["csv", "comma", "split", "field", "delimit", "column"],
            "split a comma separated line into fields respecting quotes")
    ];

    public async Task<SemanticQualityResult> RunAsync(CancellationToken ct = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "tokensaver-semqualityselftest-" + Guid.NewGuid());
        try
        {
            _ = Directory.CreateDirectory(root);
            foreach (FixtureCase c in Cases)
            {
                await File.WriteAllTextAsync(Path.Combine(root, c.File), c.Code, ct);
            }

            _ = await indexer.IndexRepoAsync(root, ct: ct);
            WorkspaceIndexContext ctx = await db.GetDbAsync(root, ct);
            List<ChunkEntity> chunks = await db.WithDbLockAsync(() => ctx.Chunks.ToListAsync(ct), ct);

            List<SemanticQualityCaseResult> results = [];
            foreach (FixtureCase c in Cases)
            {
                results.Add(await CheckCaseAsync(root, c, chunks, ct));
            }

            int distinct = results.Select(r => r.Summary.Trim()).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            bool allDistinct = distinct == Cases.Length;
            bool passed = allDistinct && results.All(r => r.SummaryOk && r.RetrievalOk);
            return new SemanticQualityResult(passed, results, BuildNarrative(results, allDistinct));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort: the SQLite handle may still be open on Windows; the folder is in temp
            }
        }
    }

    private async Task<SemanticQualityCaseResult> CheckCaseAsync(
        string root, FixtureCase c, List<ChunkEntity> chunks, CancellationToken ct)
    {
        ChunkEntity? chunk = chunks.FirstOrDefault(x =>
            string.Equals(x.RelativePath, c.File, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, c.Name, StringComparison.Ordinal));
        string summary = chunk?.Summary ?? "";
        bool summaryOk = IsConciseDescription(summary)
            && c.Keywords.Any(k => summary.Contains(k, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<SemanticSearchHit> hits = await search.SearchAsync(root, c.Query, topK: 1, ct: ct);
        string top = hits.Count == 0 ? "(none)" : hits[0].RelativePath;
        bool retrievalOk = hits.Count > 0
            && string.Equals(hits[0].RelativePath, c.File, StringComparison.OrdinalIgnoreCase);

        return new SemanticQualityCaseResult(c.File, summaryOk, retrievalOk, summary, top);
    }

    private static string BuildNarrative(IReadOnlyList<SemanticQualityCaseResult> results, bool allDistinct)
    {
        StringBuilder sb = new();
        foreach (SemanticQualityCaseResult r in results)
        {
            string line = r.SummaryOk && r.RetrievalOk
                ? string.Format(CultureInfo.InvariantCulture, "{0}: ok", r.File)
                : string.Format(CultureInfo.InvariantCulture,
                    "{0}: summary {1}, query {2} (top hit {3}); summary was \"{4}\"",
                    r.File, r.SummaryOk ? "ok" : "missing expected terms or not a short one-line description",
                    r.RetrievalOk ? "ok" : "ranked wrong chunk", r.TopHit, Truncate(r.Summary));
            _ = sb.AppendLine(line);
        }

        if (!allDistinct)
        {
            _ = sb.AppendLine("summaries are not distinct: the model returned the same text for different functions");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     The indexer asks for one line. A summary that is empty, has code fences, or runs to
    ///     several paragraphs means the model treated the prompt as an edit/explain request, and that
    ///     text would be embedded and shown in search results as noise.
    /// </summary>
    internal static bool IsConciseDescription(string summary)
    {
        string s = summary.Trim();
        return s.Length > 0
            && s.Length <= MaxSummaryLength
            && !s.Contains("```", StringComparison.Ordinal)
            && s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
    }

    private const int MaxSummaryLength = 300;

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "...";
}
