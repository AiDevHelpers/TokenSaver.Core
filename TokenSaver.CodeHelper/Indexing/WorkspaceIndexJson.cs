using System.Text.Encodings.Web;
using System.Text.Json;

namespace TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Shared <see cref="JsonSerializerOptions" /> for JSON produced by the Workspace Index
///     services (folder index JSON, preferences.json) — factored out so
///     <see cref="FolderTreeManager" /> and <see cref="WorkspaceConfigManager" /> don't each
///     declare their own copy.
/// </summary>
internal static class WorkspaceIndexJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
