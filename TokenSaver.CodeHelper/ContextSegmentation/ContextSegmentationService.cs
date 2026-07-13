using TokenSaver.Config;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace TokenSaver.CodeHelper.ContextSegmentation;

/// <summary>
/// Partitions the codebase into logical context segments based on project structure,
/// task type affinity, and file dependencies. Segments are cached in .tokensaver/context/segments.json.
/// </summary>
public class ContextSegmentationService
{
    private readonly string _workspaceRoot;
    private readonly ILogger<ContextSegmentationService> _logger;
    private readonly BehaviorConfigService _configService;
    private List<ContextSegment>? _cachedSegments;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(30);

    public ContextSegmentationService(
        string workspaceRoot,
        BehaviorConfigService configService,
        ILogger<ContextSegmentationService> logger)
    {
        _workspaceRoot = workspaceRoot;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Load or generate segments for this workspace.
    /// </summary>
    public async Task<List<ContextSegment>> GetSegmentsAsync(CancellationToken ct = default)
    {
        if (_cachedSegments != null && DateTime.UtcNow - _cacheTime < _cacheExpiry)
        {
            return _cachedSegments;
        }

        string segmentsPath = Path.Combine(_workspaceRoot, ".tokensaver", "context", "segments.json");

        if (File.Exists(segmentsPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(segmentsPath, ct);
                _cachedSegments = JsonSerializer.Deserialize<List<ContextSegment>>(json) ?? [];
                _cacheTime = DateTime.UtcNow;
                return _cachedSegments;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached segments, regenerating");
            }
        }

        // Generate default segments based on workspace structure
        _cachedSegments = GenerateDefaultSegments();
        await SaveSegmentsAsync(_cachedSegments, ct);

        return _cachedSegments;
    }

    private List<ContextSegment> GenerateDefaultSegments()
    {
        var segments = new List<ContextSegment>
        {
            new()
            {
                Id = "core_architecture",
                Label = "Core Architecture",
                PathPatterns = ["*.csproj", "**/CLAUDE.md", "docs/", ".tokensaver/"],
                Priority = SegmentPriority.Critical,
                PrimaryTaskTypes = ["Planning", "Review", "Explain"],
                EstimatedTokens = 3000
            },
            new()
            {
                Id = "codex",
                Label = "Indexing & Search",
                PathPatterns =
                [
                    "src/Core/TokenSaver.CodeHelper/Indexing/**",
                    "src/Core/TokenSaver.CodeHelper/Models/Index/**",
                    "src/Core/TokenSaver.Library/Services/ChunkExtractor.cs",
                    "src/Core/TokenSaver.Library/Contracts/ISemanticSearchService.cs"
                ],
                Priority = SegmentPriority.Important,
                PrimaryTaskTypes = ["Code", "Debug", "Test"],
                EstimatedTokens = 5000
            },
            new()
            {
                Id = "context_pipeline",
                Label = "Context Pipeline",
                PathPatterns =
                [
                    "src/Core/TokenSaver.ContextHelper/Pipeline/**",
                    "src/Core/TokenSaver.ContextHelper/Models/**"
                ],
                Priority = SegmentPriority.Important,
                PrimaryTaskTypes = ["Code", "Debug"],
                EstimatedTokens = 4000
            },
            new()
            {
                Id = "configuration",
                Label = "Config & Ignore",
                PathPatterns =
                [
                    "src/Core/TokenSaver.Config/**",
                    "src/Core/TokenSaver.CodeHelper/Configuration/**",
                    "src/Core/TokenSaver.ContextHelper/Configuration/**"
                ],
                Priority = SegmentPriority.Important,
                PrimaryTaskTypes = ["Code", "Planning"],
                EstimatedTokens = 2000
            },
            new()
            {
                Id = "mcp_server",
                Label = "MCP Server",
                PathPatterns =
                [
                    "src/Host/TokenSaver.MCP/**",
                    "src/Host/TokenSaver.MCP.Tests/**"
                ],
                Priority = SegmentPriority.Important,
                PrimaryTaskTypes = ["Code", "Debug", "Test"],
                EstimatedTokens = 6000
            },
            new()
            {
                Id = "auto_workflow",
                Label = "Auto Workflow",
                PathPatterns =
                [
                    "src/Core/TokenSaver.CodeHelper/AutoWorkflow/**",
                    "src/Core/TokenSaver.CodeHelper/Planning/**"
                ],
                Priority = SegmentPriority.Optional,
                PrimaryTaskTypes = ["Planning", "Code"],
                EstimatedTokens = 3000
            },
            new()
            {
                Id = "testing_harness",
                Label = "Testing & Calibration",
                PathPatterns =
                [
                    "**/**.Tests/**",
                    "src/Core/TokenSaver.CodeHelper/Calibration/**",
                    "src/Core/TokenSaver.ContextHelper.Calibration/**"
                ],
                Priority = SegmentPriority.Optional,
                PrimaryTaskTypes = ["Test", "Debug"],
                EstimatedTokens = 4000
            },
            new()
            {
                Id = "agent_helpers",
                Label = "Agent Adapters",
                PathPatterns =
                [
                    "src/Core/TokenSaver.AgentHelpers.*/**"
                ],
                Priority = SegmentPriority.Optional,
                PrimaryTaskTypes = ["Code", "Review"],
                EstimatedTokens = 3000
            },
            new()
            {
                Id = "docs",
                Label = "Documentation",
                PathPatterns =
                [
                    "docs/**",
                    "**/README.md",
                    "src/Core/TokenSaver.Docs/**"
                ],
                Priority = SegmentPriority.Optional,
                PrimaryTaskTypes = ["Explain", "Planning"],
                EstimatedTokens = 2000
            }
        };

        return segments;
    }

    private async Task SaveSegmentsAsync(List<ContextSegment> segments, CancellationToken ct)
    {
        string contextDir = Path.Combine(_workspaceRoot, ".tokensaver", "context");
        Directory.CreateDirectory(contextDir);

        string segmentsPath = Path.Combine(contextDir, "segments.json");
        string json = JsonSerializer.Serialize(segments, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(segmentsPath, json, ct);
    }

    /// <summary>
    /// Get segments relevant to a specific task type, ranked by relevance.
    /// Returns Critical segments always, then segments with primary affinity, then others.
    /// </summary>
    public async Task<List<ContextSegment>> GetSegmentsForTaskTypeAsync(
        string taskType,
        int tokenBudget = 50000,
        CancellationToken ct = default)
    {
        var allSegments = await GetSegmentsAsync(ct);
        var selected = new List<ContextSegment>();
        int tokensUsed = 0;

        // Critical segments always included
        var critical = allSegments
            .Where(s => s.Priority == SegmentPriority.Critical)
            .OrderBy(s => s.EstimatedTokens)
            .ToList();

        foreach (var seg in critical)
        {
            if (tokensUsed + seg.EstimatedTokens <= tokenBudget)
            {
                selected.Add(seg);
                tokensUsed += seg.EstimatedTokens;
            }
        }

        // Primary affinity for this task type
        var affinity = allSegments
            .Where(s => s.Priority != SegmentPriority.Critical &&
                       s.PrimaryTaskTypes.Contains(taskType, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Priority == SegmentPriority.Important)
            .ThenBy(s => s.EstimatedTokens)
            .ToList();

        foreach (var seg in affinity)
        {
            if (tokensUsed + seg.EstimatedTokens <= tokenBudget)
            {
                selected.Add(seg);
                tokensUsed += seg.EstimatedTokens;
            }
        }

        // Fill remaining budget with frequently-used optional segments
        var optional = allSegments
            .Where(s => !selected.Contains(s) && s.Priority == SegmentPriority.Optional)
            .OrderByDescending(s => s.LastAccessed)
            .ToList();

        foreach (var seg in optional)
        {
            if (tokensUsed + seg.EstimatedTokens <= tokenBudget)
            {
                selected.Add(seg);
                tokensUsed += seg.EstimatedTokens;
            }
        }

        return selected;
    }
}
