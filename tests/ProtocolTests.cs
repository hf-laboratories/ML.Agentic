namespace HFLabs.ML.Agentic.Tests;

using System.Text.Json;
using HFLabs.ML.Agentic;
using HFLabs.ML.Agentic.Protocols;
using Xunit;

public sealed class JsonRpcProtocolTests
{
    [Fact]
    public void Create_ProducesSpecEnvelope_WithCamelCaseParams()
    {
        JsonRpcRequest request = JsonRpcRequest.Create("tools/call", new McpToolCallParams { Name = "run", Arguments = new { PromptText = "hi" } }, id: "7");

        string json = JsonSerializer.Serialize(request, ProtocolJson.Options);

        Assert.Equal("""{"jsonrpc":"2.0","id":"7","method":"tools/call","params":{"name":"run","arguments":{"promptText":"hi"}}}""", json);
    }

    [Fact]
    public void Response_OmitsErrorOnSuccess_AndResultOnFailure()
    {
        JsonElement id = JsonSerializer.SerializeToElement(1);

        string ok = JsonSerializer.Serialize(JsonRpcResponse.Ok(id, new { value = 3 }), ProtocolJson.Options);
        string fail = JsonSerializer.Serialize(JsonRpcResponse.Fail(id, JsonRpcError.MethodNotFound("x")), ProtocolJson.Options);

        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{"value":3}}""", ok);
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found: x"}}""", fail);
    }

    [Fact]
    public void Deserialized_ResultElement_IsRawJson()
    {
        JsonRpcResponse response = JsonSerializer.Deserialize<JsonRpcResponse>("""{"jsonrpc":"2.0","id":"a","result":{"n":5}}""", ProtocolJson.Options)!;

        Assert.True(response.IsSuccess);
        Assert.Equal(5, response.ResultElement.GetProperty("n").GetInt32());
    }
}

public sealed class A2AProtocolTests
{
    [Fact]
    public void Message_RoundTrips_WithMetadataAndDataPart()
    {
        A2AMessage message = A2AMessage.UserText("plan it", taskId: "t1", metadata: new()
        {
            ["targetPath"] = JsonSerializer.SerializeToElement("src/app"),
        });

        string json = JsonSerializer.Serialize(message, ProtocolJson.Options);
        A2AMessage back = JsonSerializer.Deserialize<A2AMessage>(json, ProtocolJson.Options)!;

        Assert.Contains("\"kind\":\"text\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"contextId\"", json, StringComparison.Ordinal);
        Assert.Equal("plan it", back.FirstText);
        Assert.Equal("user", back.Role);
        Assert.Equal("src/app", back.Metadata!["targetPath"].GetString());
    }

    [Fact]
    public void Task_ParsesSpecShapedResponse_AndReadsTypedData()
    {
        const string wire = """
            {"id":"t1","contextId":"c1","kind":"task",
             "status":{"state":"input-required","timestamp":"2026-09-24T00:00:00Z",
               "message":{"role":"agent","messageId":"m2","kind":"message",
                 "parts":[{"kind":"text","text":"need more"},{"kind":"data","data":{"turnId":"t1","succeeded":false}}]}},
             "artifacts":[{"artifactId":"t1-result","parts":[{"kind":"data","data":{"turnId":"t1","succeeded":false}}]}]}
            """;

        A2ATask task = JsonSerializer.Deserialize<A2ATask>(wire, ProtocolJson.Options)!;
        TurnData? data = task.Status.Message!.Parts[1].DataAs<TurnData>();

        Assert.Equal(A2ATaskState.InputRequired, task.Status.State);
        Assert.Equal("need more", task.Status.Message.FirstText);
        Assert.Equal("t1", data!.TurnId);
        Assert.Single(task.Artifacts!);
    }

    [Fact]
    public void AgentCard_UsesSpecFieldNames_AndMapsFromRepoNativeCard()
    {
        AgentCard native = new("Planner", "Plans things", "1.2.0", "http://x", new AgentCapabilities(Streaming: false), ["plan", "route"]);

        A2AAgentCard card = A2AAgentCard.FromAgentCard(native, "http://x/a2a");
        string json = JsonSerializer.Serialize(card, ProtocolJson.Options);

        Assert.Contains("\"protocolVersion\":\"0.2.5\"", json, StringComparison.Ordinal);
        Assert.Contains("\"defaultInputModes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pushNotifications\":false", json, StringComparison.Ordinal);
        Assert.Equal(["plan", "route"], card.Skills.Select(s => s.Id));
        Assert.Equal("http://x/a2a", card.Url);
    }

    private sealed record TurnData(string TurnId, bool Succeeded);
}

public sealed class McpProtocolTests
{
    [Fact]
    public void ToolCallResult_UsesSpecFieldNames()
    {
        McpToolCallResult result = new() { Content = [McpContentItem.FromText("done")], IsError = false };

        string json = JsonSerializer.Serialize(result, ProtocolJson.Options);

        Assert.Equal("""{"content":[{"type":"text","text":"done"}],"isError":false}""", json);
    }

    [Fact]
    public void InitializeResult_UsesSpecFieldNames()
    {
        McpInitializeResult result = new()
        {
            ProtocolVersion = "2025-06-18",
            Capabilities = new { tools = new { } },
            ServerInfo = new McpServerInfo { Name = "S", Version = "1" },
        };

        string json = JsonSerializer.Serialize(result, ProtocolJson.Options);

        Assert.Equal("""{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"S","version":"1"}}""", json);
    }
}
