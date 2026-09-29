using System.Text.Json.Serialization;

namespace HFLabs.ML.Agentic.Protocols;

/// <summary>MCP JSON-RPC method names (spec 2025-06-18).</summary>
public static class McpMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "notifications/initialized";
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";
}

public sealed class McpTool
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("inputSchema")] public required object InputSchema { get; init; }
}

public sealed class McpToolsListResult
{
    [JsonPropertyName("tools")] public required McpTool[] Tools { get; init; }
}

/// <summary>Parameters of <c>tools/call</c>.</summary>
public sealed class McpToolCallParams
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Arguments { get; init; }
}

public sealed class McpContentItem
{
    [JsonPropertyName("type")] public required string Type { get; init; }

    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    public static McpContentItem FromText(string text) => new() { Type = "text", Text = text };
}

public sealed class McpToolCallResult
{
    [JsonPropertyName("content")] public required McpContentItem[] Content { get; init; }

    [JsonPropertyName("structuredContent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StructuredContent { get; init; }

    [JsonPropertyName("isError")] public bool IsError { get; init; }
}

public sealed class McpServerInfo
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
}

public sealed class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")] public required string ProtocolVersion { get; init; }
    [JsonPropertyName("capabilities")] public required object Capabilities { get; init; }
    [JsonPropertyName("serverInfo")] public required McpServerInfo ServerInfo { get; init; }

    [JsonPropertyName("instructions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; init; }
}
