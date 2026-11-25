using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
/// Detects and optionally filters build/cache/generated artifacts from context to prevent
/// wasting tokens on files that shouldn't be sent to Claude. Runs AFTER the main context
/// pipeline but BEFORE returning to the caller, so it's a last-resort defense.
/// </summary>
public sealed class BuildArtifactDetector
{
    private readonly ILogger<BuildArtifactDetector> _logger;

    // Patterns that unmistakably identify build/cache dirs. These are intentionally broad
    // to catch the common cases (node_modules is node_modules anywhere), but conservative
    // enough not to false-positive on legitimate source (e.g., "build" in a README or comment).
    private static readonly string[] BuildDirPatterns =
    [
        // Common multi-language
        "bin/", "obj/", "dist/", "build/", "target/",
        "node_modules/", ".npm/", "package-lock.json", "yarn.lock",
        ".git/", ".github/",

        // Python
        "__pycache__/", ".pytest_cache/", ".tox/", "*.egg-info/",
        "venv/", ".venv/", "env/", ".env/",

        // C#/.NET
        ".vs/", "bin/", "obj/", ".nupkg",

        // Java/Gradle/Maven
        ".gradle/", "gradle/", "target/",

        // Rust
        "target/", "Cargo.lock",

        // Go
        "/vendor/", "/bin/",

        // Ruby/Bundler
        "Gemfile.lock", ".bundle/",

        // Cache/temp
        ".cache/", ".tmp/", "tmp/", "temp/",
        ".DS_Store", "Thumbs.db",
    ];

    // File patterns for generated code / build outputs
    private static readonly Regex[] GeneratedFilePatterns =
    [
        new Regex(@"\.min\.(js|css)$", RegexOptions.IgnoreCase),  // Minified
        new Regex(@"\.map$", RegexOptions.IgnoreCase),             // Source maps
        new Regex(@"\.(o|a|so|dylib)$", RegexOptions.IgnoreCase),  // Object/archive files
        new Regex(@"\.pyc$", RegexOptions.IgnoreCase),             // Python bytecode
        new Regex(@"\.class$", RegexOptions.IgnoreCase),           // Java bytecode
        new Regex(@"\.dll$", RegexOptions.IgnoreCase),             // Windows DLL
        new Regex(@"^\.proto\.pb\..*", RegexOptions.IgnoreCase),   // Protobuf generated
        new Regex(@"\.generated\.", RegexOptions.IgnoreCase),      // Explicit "generated" in name
    ];

    public BuildArtifactDetector(ILogger<BuildArtifactDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyzes included files and returns a list of suspicious paths that are likely
    /// build/cache artifacts. Does NOT modify the input — caller decides whether to remove them.
    /// </summary>
    public IReadOnlyList<string> DetectArtifacts(IEnumerable<string> includedFilePaths)
    {
        List<string> artifacts = [];

        foreach (string filePath in includedFilePaths)
        {
            if (IsBuildArtifact(filePath))
            {
                artifacts.Add(filePath);
            }
        }

        if (artifacts.Count > 0)
        {
            _logger.LogWarning(
                "Detected {Count} likely build/cache artifacts in context: {Artifacts}",
                artifacts.Count,
                string.Join(", ", artifacts.Take(10)) + (artifacts.Count > 10 ? "..." : ""));
        }

        return artifacts;
    }

    /// <summary>
    /// True if the path is obviously a build artifact or cache file that shouldn't be in context.
    /// Conservative: errs toward false negatives (allowing a questionable file) rather than
    /// false positives (excluding legitimate source).
    /// </summary>
    private bool IsBuildArtifact(string relativePath)
    {
        // Normalize to forward slashes
        string normalized = relativePath.Replace('\\', '/');

        // Check directory patterns (look for these dirs anywhere in the path)
        foreach (string pattern in BuildDirPatterns)
        {
            if (normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check file name patterns
        string fileName = Path.GetFileName(relativePath);
        foreach (Regex pattern in GeneratedFilePatterns)
        {
            if (pattern.IsMatch(fileName))
            {
                return true;
            }
        }

        return false;
    }
}
