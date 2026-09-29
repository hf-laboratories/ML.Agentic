using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HFLabs.ML.Agentic.Protocols;

/// <summary>Serializer settings shared by every protocol model in this namespace (camelCase, web defaults).</summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// JSON-RPC 2.0 request envelope. <see cref="Id"/> and <see cref="Params"/> are kept as raw JSON so a server
/// can dispatch on <see cref="Method"/> before binding parameters; clients build requests with <see cref="Create"/>.
/// </summary>
public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; init; }
    [JsonPropertyName("method")] public string Method { get; init; } = string.Empty;

    [JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; init; }

    public static JsonRpcRequest Create(string method, object? @params = null, string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return new JsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement(id ?? Guid.NewGuid().ToString("N"), ProtocolJson.Options),
            Method = method,
            Params = @params is null ? null : JsonSerializer.SerializeToElement(@params, ProtocolJson.Options),
        };
    }
}

/// <summary>JSON-RPC 2.0 error object, with factories for the standard codes.</summary>
public sealed class JsonRpcError
{
    public const int ParseErrorCode = -32700;
    public const int InvalidRequestCode = -32600;
    public const int MethodNotFoundCode = -32601;
    public const int InvalidParamsCode = -32602;
    public const int InternalErrorCode = -32603;

    [JsonPropertyName("code")] public required int Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }

    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }

    public static JsonRpcError ParseError() => new() { Code = ParseErrorCode, Message = "Parse error" };
    public static JsonRpcError MethodNotFound(string method) => new() { Code = MethodNotFoundCode, Message = $"Method not found: {method}" };
    public static JsonRpcError InvalidParams(string detail) => new() { Code = InvalidParamsCode, Message = $"Invalid params: {detail}" };
    public static JsonRpcError InternalError(string detail) => new() { Code = InternalErrorCode, Message = detail };
}

/// <summary>
/// JSON-RPC 2.0 response envelope. Servers set <see cref="Result"/> to any serializable object; after
/// deserialization on the client it is a <see cref="JsonElement"/> (see <see cref="ResultElement"/>).
/// </summary>
public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; init; }

    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; init; }

    [JsonIgnore]
    public bool IsSuccess => Error is null;

    /// <summary>The result as raw JSON; <c>default</c> (Undefined) when absent.</summary>
    [JsonIgnore]
    public JsonElement ResultElement => Result switch
    {
        JsonElement element => element,
        null => default,
        var other => JsonSerializer.SerializeToElement(other, ProtocolJson.Options),
    };

    public static JsonRpcResponse Ok(JsonElement? id, object? result) => new() { Id = id, Result = result };
    public static JsonRpcResponse Fail(JsonElement? id, JsonRpcError error) => new() { Id = id, Error = error };
}

/// <summary>Thrown by <see cref="JsonRpcHttpClient"/> when the peer answers with a JSON-RPC error object.</summary>
public sealed class JsonRpcException(JsonRpcError error) : Exception(error.Message)
{
    public JsonRpcError Error { get; } = error;
}

/// <summary>Minimal JSON-RPC 2.0 over HTTP POST caller, used by A2A and MCP clients.</summary>
public static class JsonRpcHttpClient
{
    /// <summary>
    /// Posts <paramref name="method"/> with <paramref name="params"/> to <paramref name="endpoint"/> and returns the
    /// result element. Sends <paramref name="bearerToken"/> as an Authorization header when supplied.
    /// Throws <see cref="HttpRequestException"/> on non-2xx and <see cref="JsonRpcException"/> on a JSON-RPC error.
    /// </summary>
    public static async Task<JsonElement> CallAsync(
        HttpClient httpClient,
        Uri endpoint,
        string method,
        object? @params,
        string? bearerToken = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        JsonRpcRequest request = JsonRpcRequest.Create(method, @params);
        using StringContent content = new(JsonSerializer.Serialize(request, ProtocolJson.Options), Encoding.UTF8, "application/json");
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, endpoint) { Content = content };
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        JsonRpcResponse rpcResponse = await JsonSerializer.DeserializeAsync<JsonRpcResponse>(body, ProtocolJson.Options, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Empty JSON-RPC response from {endpoint} for method {method}.");
        if (rpcResponse.Error is not null)
        {
            throw new JsonRpcException(rpcResponse.Error);
        }

        return rpcResponse.ResultElement;
    }
}
