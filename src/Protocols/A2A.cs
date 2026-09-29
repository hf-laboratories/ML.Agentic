using System.Text.Json;
using System.Text.Json.Serialization;

namespace HFLabs.ML.Agentic.Protocols;

/// <summary>A2A JSON-RPC method names (spec v0.2.5).</summary>
public static class A2AMethods
{
    public const string MessageSend = "message/send";
    public const string TasksGet = "tasks/get";
}

/// <summary>
/// A2A <c>TaskState</c> wire values (spec §6.3). Kept as string constants because the values are kebab-case
/// (<c>input-required</c>), which avoids a custom enum converter.
/// </summary>
public static class A2ATaskState
{
    public const string Submitted = "submitted";
    public const string Working = "working";
    public const string InputRequired = "input-required";
    public const string Completed = "completed";
    public const string Canceled = "canceled";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
}

/// <summary>An A2A message: a role plus text/data parts, optionally bound to a task and context.</summary>
public sealed class A2AMessage
{
    [JsonPropertyName("role")] public string Role { get; init; } = "agent";
    [JsonPropertyName("parts")] public A2APart[] Parts { get; init; } = [];
    [JsonPropertyName("messageId")] public string MessageId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("taskId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }

    [JsonPropertyName("contextId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextId { get; init; }

    /// <summary>Optional extension metadata (spec: <c>Message.metadata</c>).</summary>
    [JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    [JsonPropertyName("kind")] public string Kind { get; init; } = "message";

    /// <summary>First text part, or null.</summary>
    [JsonIgnore]
    public string? FirstText => Parts.FirstOrDefault(p => p.Kind == A2APart.TextKind)?.Text;

    public static A2AMessage UserText(string text, string? taskId = null, string? contextId = null, Dictionary<string, JsonElement>? metadata = null) => new()
    {
        Role = "user",
        Parts = [A2APart.FromText(text)],
        TaskId = taskId,
        ContextId = contextId,
        Metadata = metadata,
    };
}

/// <summary>A message or artifact part: either <c>text</c> or structured <c>data</c>.</summary>
public sealed class A2APart
{
    public const string TextKind = "text";
    public const string DataKind = "data";

    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Structured payload. Any serializable object when sending; a <see cref="JsonElement"/> after deserialization.</summary>
    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }

    public static A2APart FromText(string text) => new() { Kind = TextKind, Text = text };
    public static A2APart FromData(object data) => new() { Kind = DataKind, Data = data };

    /// <summary>Deserializes <see cref="Data"/> into <typeparamref name="T"/>; null when there is no data.</summary>
    public T? DataAs<T>() => Data switch
    {
        null => default,
        JsonElement element => element.Deserialize<T>(ProtocolJson.Options),
        T typed => typed,
        var other => JsonSerializer.SerializeToElement(other, ProtocolJson.Options).Deserialize<T>(ProtocolJson.Options),
    };
}

public sealed class A2ATaskStatus
{
    [JsonPropertyName("state")] public required string State { get; init; }

    [JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public A2AMessage? Message { get; init; }

    [JsonPropertyName("timestamp")] public string Timestamp { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

public sealed class A2AArtifact
{
    [JsonPropertyName("artifactId")] public required string ArtifactId { get; init; }

    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("parts")] public A2APart[] Parts { get; init; } = [];
}

/// <summary>An A2A task: identity, current status, produced artifacts and message history.</summary>
public sealed class A2ATask
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("contextId")] public required string ContextId { get; init; }
    [JsonPropertyName("status")] public required A2ATaskStatus Status { get; init; }

    [JsonPropertyName("artifacts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public A2AArtifact[]? Artifacts { get; init; }

    [JsonPropertyName("history"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public A2AMessage[]? History { get; init; }

    [JsonPropertyName("kind")] public string Kind { get; init; } = "task";
}

/// <summary>Parameters of <c>message/send</c>.</summary>
public sealed class A2AMessageSendParams
{
    [JsonPropertyName("message")] public required A2AMessage Message { get; init; }
}

/// <summary>
/// Spec-shaped A2A Agent Card (v0.2.5 §5.5), served at <c>/.well-known/agent.json</c>. Distinct from the
/// repo-native <see cref="AgentCard"/> used for in-repo registries; see <see cref="FromAgentCard"/>.
/// </summary>
public sealed class A2AAgentCard
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; init; } = "0.2.5";
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("capabilities")] public A2AAgentCapabilities Capabilities { get; init; } = new();
    [JsonPropertyName("defaultInputModes")] public string[] DefaultInputModes { get; init; } = ["text/plain"];
    [JsonPropertyName("defaultOutputModes")] public string[] DefaultOutputModes { get; init; } = ["text/plain", "application/json"];
    [JsonPropertyName("skills")] public A2AAgentSkill[] Skills { get; init; } = [];

    /// <summary>Maps a repo-native card to the spec shape. Skills become id = name = description entries.</summary>
    public static A2AAgentCard FromAgentCard(AgentCard card, string url)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return new A2AAgentCard
        {
            Name = card.Name,
            Description = card.Description,
            Url = url,
            Version = card.Version,
            Capabilities = new A2AAgentCapabilities { Streaming = card.Capabilities.Streaming },
            Skills = card.Skills.Select(s => new A2AAgentSkill { Id = s, Name = s, Description = s }).ToArray(),
        };
    }
}

public sealed class A2AAgentCapabilities
{
    [JsonPropertyName("streaming")] public bool Streaming { get; init; }
    [JsonPropertyName("pushNotifications")] public bool PushNotifications { get; init; }
    [JsonPropertyName("stateTransitionHistory")] public bool StateTransitionHistory { get; init; }
}

public sealed class A2AAgentSkill
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("tags")] public string[] Tags { get; init; } = [];

    [JsonPropertyName("examples"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Examples { get; init; }

    [JsonPropertyName("inputModes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? InputModes { get; init; }

    [JsonPropertyName("outputModes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? OutputModes { get; init; }
}
