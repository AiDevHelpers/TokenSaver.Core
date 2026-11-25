using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AiDevTools.TokenSaver.CodeHelper.ContextSegmentation;

/// <summary>
/// Records access patterns in .tokensaver/context/access-patterns.jsonl.
/// Learns which segments are used together for adaptive loading.
/// Provides statistics on segment co-occurrence.
/// </summary>
public class AccessPatternTracker
{
    private readonly string _workspaceRoot;
    private readonly ILogger<AccessPatternTracker> _logger;
    private Dictionary<string, SegmentAffinityScore>? _cachedAffinities;

    public AccessPatternTracker(
        string workspaceRoot,
        ILogger<AccessPatternTracker> logger)
    {
        _workspaceRoot = workspaceRoot;
        _logger = logger;
    }

    /// <summary>
    /// Record a session's segment access. Appends to access-patterns.jsonl.
    /// </summary>
    public async Task RecordAccessAsync(AccessPattern pattern, CancellationToken ct = default)
    {
        string contextDir = Path.Combine(_workspaceRoot, ".tokensaver", "context");
        Directory.CreateDirectory(contextDir);

        string patternsPath = Path.Combine(contextDir, "access-patterns.jsonl");

        try
        {
            string json = JsonSerializer.Serialize(pattern);
            await File.AppendAllTextAsync(patternsPath, json + Environment.NewLine, ct);

            // Invalidate cached affinities so next analysis is fresh
            _cachedAffinities = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record access pattern");
        }
    }

    /// <summary>
    /// Analyze access patterns to compute co-occurrence affinities between segments.
    /// Returns a mapping: "segment1__segment2" -> affinity score (0.0-1.0).
    /// </summary>
    public async Task<Dictionary<string, SegmentAffinityScore>> AnalyzeAffinitiesAsync(
        CancellationToken ct = default)
    {
        if (_cachedAffinities != null)
        {
            return _cachedAffinities;
        }

        string patternsPath = Path.Combine(_workspaceRoot, ".tokensaver", "context", "access-patterns.jsonl");
        if (!File.Exists(patternsPath))
        {
            return _cachedAffinities = [];
        }

        var patterns = new List<AccessPattern>();

        try
        {
            using (var reader = new StreamReader(patternsPath))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var pattern = JsonSerializer.Deserialize<AccessPattern>(line);
                        if (pattern != null)
                        {
                            patterns.Add(pattern);
                        }
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read access patterns");
            return _cachedAffinities = [];
        }

        // Keep only recent patterns (last 100 sessions to avoid stale data)
        patterns = patterns
            .OrderByDescending(p => p.Timestamp)
            .Take(100)
            .ToList();

        var affinities = new Dictionary<string, SegmentAffinityScore>();
        var cooccurrence = new Dictionary<(string, string), int>();
        var segmentCounts = new Dictionary<string, int>();

        // Count co-occurrences
        foreach (var pattern in patterns)
        {
            var used = new HashSet<string>(pattern.SegmentsUsed);

            foreach (var seg in used)
            {
                segmentCounts.TryGetValue(seg, out int count);
                segmentCounts[seg] = count + 1;
            }

            // Record all pairs that co-occurred
            var pairs = used.OrderBy(s => s).ToList();
            for (int i = 0; i < pairs.Count; i++)
            {
                for (int j = i + 1; j < pairs.Count; j++)
                {
                    var key = (pairs[i], pairs[j]);
                    cooccurrence.TryGetValue(key, out int count);
                    cooccurrence[key] = count + 1;
                }
            }
        }

        // Compute affinities using co-occurrence / min(count[A], count[B])
        foreach (var ((segA, segB), count) in cooccurrence)
        {
            int minCount = Math.Min(
                segmentCounts.GetValueOrDefault(segA, 1),
                segmentCounts.GetValueOrDefault(segB, 1));

            double affinity = (double)count / Math.Max(1, minCount);
            affinity = Math.Min(1.0, affinity); // Cap at 1.0

            string key = $"{segA}__{segB}";
            affinities[key] = new SegmentAffinityScore
            {
                Segment1 = segA,
                Segment2 = segB,
                Affinity = affinity,
                CooccurrenceCount = count
            };
        }

        _cachedAffinities = affinities;
        return affinities;
    }

    /// <summary>
    /// Given a set of already-selected segments, recommend additional segments
    /// based on co-occurrence affinities.
    /// </summary>
    public async Task<List<string>> RecommendRelatedSegmentsAsync(
        IEnumerable<string> selectedSegments,
        CancellationToken ct = default)
    {
        var affinities = await AnalyzeAffinitiesAsync(ct);
        var selected = new HashSet<string>(selectedSegments);
        var recommended = new Dictionary<string, double>();

        foreach (var key in affinities.Keys)
        {
            var score = affinities[key];
            var other = selected.Contains(score.Segment1) ? score.Segment2 : score.Segment1;

            if (!selected.Contains(other))
            {
                recommended.TryGetValue(other, out double current);
                recommended[other] = Math.Max(current, score.Affinity);
            }
        }

        return recommended
            .OrderByDescending(x => x.Value)
            .Select(x => x.Key)
            .ToList();
    }
}

public class SegmentAffinityScore
{
    public string Segment1 { get; set; } = null!;
    public string Segment2 { get; set; } = null!;
    public double Affinity { get; set; }
    public int CooccurrenceCount { get; set; }
}
