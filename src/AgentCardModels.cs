namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Declares runtime execution and transport capabilities supported by an agent.
/// Conforms to PAT-CS-247.
/// </summary>
public sealed record AgentCapabilities(
    [property: JsonPropertyName("streaming")] bool Streaming = true,
    [property: JsonPropertyName("batchExecution")] bool BatchExecution = false,
    [property: JsonPropertyName("statePersistence")] bool StatePersistence = true,
    [property: JsonPropertyName("supportedProtocols")] IReadOnlyList<string>? SupportedProtocols = null);

/// <summary>
/// A2A self-describing metadata card advertising agent identity, skills, and endpoints.
/// Conforms to PAT-CS-247.
/// </summary>
public sealed record AgentCard(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("endpointUrl")] string EndpointUrl,
    [property: JsonPropertyName("capabilities")] AgentCapabilities Capabilities,
    [property: JsonPropertyName("skills")] IReadOnlyList<string> Skills,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object>? Metadata = null)
{
    /// <summary>
    /// Checks whether the agent supports a specific named skill or protocol.
    /// </summary>
    public bool HasSkill(string skillName) =>
        Skills.Any(s => string.Equals(s, skillName, StringComparison.OrdinalIgnoreCase));
}
