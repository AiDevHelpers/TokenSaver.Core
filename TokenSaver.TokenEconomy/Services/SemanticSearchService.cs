using AiDevTools.TokenSaver.OllamaIntegration.Services;
using AiDevTools.TokenSaver.TokenEconomy.Contracts;
using AiDevTools.TokenSaver.Database.Context;
using AiDevTools.TokenSaver.Database.Context.Entities;
using AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;
using AiDevTools.TokenSaver.Utilities.Services;

using Microsoft.EntityFrameworkCore;

using System.Text.Json;

namespace AiDevTools.TokenSaver.TokenEconomy.Services;

/// <summary>Default <see cref="ISemanticSearchService" /> — see that interface's doc comment.</summary>
public sealed class SemanticSearchService(OllamaService ollama, ModelRouteResolver modelRoutes, TokenEconomyDbAccessor db)
    : ISemanticSearchService
{
    public async Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
        string workspaceRoot, string query, string? pathFilter = null, int topK = 5, CancellationToken ct = default)
    {
        float[] queryVector = await ollama.EmbedAsync(modelRoutes.Resolve("embed"), query, ct: ct);

        WorkspaceIndexContext ctx = await db.GetDbAsync(workspaceRoot, ct);
        List<ChunkEntity> rows = await db.WithDbLockAsync(
            () =>
            {
                IQueryable<ChunkEntity> q = ctx.Chunks;
                if (!string.IsNullOrWhiteSpace(pathFilter))
                {
                    q = q.Where(c => c.RelativePath.Contains(pathFilter));
                }

                return q.ToListAsync(ct);
            },
            ct);

        return rows
            .Select(r => new
            {
                Row = r,
                Score = CosineSimilarity(queryVector, JsonSerializer.Deserialize<float[]>(r.EmbeddingVector) ?? [])
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new SemanticSearchHit(
                x.Row.RelativePath, x.Row.ChunkType, x.Row.Name, x.Row.StartLine, x.Row.EndLine, x.Row.Summary,
                Math.Round(x.Score, 3)))
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
