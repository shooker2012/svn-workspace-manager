using System.Text.Json;
using System.Text.Json.Serialization;

namespace SvnWorkspaceManager.Core.Models;

public sealed class WorkspaceModeJsonConverter : JsonConverter<WorkspaceMode>
{
    public override WorkspaceMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32() == 1 ? WorkspaceMode.TaskMapping : WorkspaceMode.Normal;

        var value = reader.GetString();
        return value?.Trim().ToLowerInvariant() switch
        {
            "taskmapping" or "parallel" => WorkspaceMode.TaskMapping,
            "normal" or null or "" => WorkspaceMode.Normal,
            _ => throw new JsonException($"不支持的工作区模式：{value}")
        };
    }

    public override void Write(Utf8JsonWriter writer, WorkspaceMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == WorkspaceMode.TaskMapping ? "TaskMapping" : "Normal");
}
