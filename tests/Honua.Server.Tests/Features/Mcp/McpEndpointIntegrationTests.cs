// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Integration tests for the MCP operator surface exercised through the full
/// ASP.NET Core pipeline. Covers JSON-RPC framing, method dispatch, and
/// error mapping so the public <c>POST /mcp</c> contract is validated end
/// to end, not just at the handler level.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Mcp)]
public sealed class McpEndpointIntegrationTests : IAsyncLifetime
{
    private const string McpRoute = "/mcp";
    private const string JsonMediaType = "application/json";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task Initialize_WithSupportedVersion_NegotiatesAndReturnsServerInfo()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
                "protocolVersion":"2025-03-26",
                "capabilities":{},
                "clientInfo":{"name":"honua-tests","version":"1.0.0"}
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(JsonMediaType);

        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("id").GetInt32().Should().Be(1);
        // The context omits null properties, so the absent "error" field signals success.
        root.TryGetProperty("error", out _).Should().BeFalse();

        var result = root.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().Should().Be("2025-03-26");
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("honua.operator.mcp");
        result.GetProperty("capabilities").GetProperty("tools").Should().NotBeNull();
        result.GetProperty("capabilities").GetProperty("resources").Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task Initialize_WithUnsupportedVersion_FallsBackToLatestServerVersion()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":2,"method":"initialize","params":{
                "protocolVersion":"1999-01-01",
                "capabilities":{},
                "clientInfo":{"name":"honua-tests","version":"1.0.0"}
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        // Spec requires the server to advertise a version it actually supports
        // when the client asks for one it does not implement.
        root.GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be("2025-03-26");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task Initialize_WithoutParams_ReturnsInvalidArgumentJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":3,"method":"initialize"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetInt32().Should().Be(3);
        var error = root.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
        error.GetProperty("message").GetString().Should().Contain("protocolVersion");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task Initialize_MissingClientInfo_ReturnsInvalidArgumentJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":4,"method":"initialize","params":{
                "protocolVersion":"2025-03-26",
                "capabilities":{}
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        var error = root.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
        error.GetProperty("message").GetString().Should().Contain("clientInfo");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task NotificationsInitialized_AcceptedWithEmptyBody()
    {
        var initialize = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"hello-1","method":"initialize","params":{
                "protocolVersion":"2025-03-26",
                "capabilities":{},
                "clientInfo":{"name":"honua-tests","version":"1.0.0"}
            }}
            """);
        initialize.StatusCode.Should().Be(HttpStatusCode.OK);

        // Per JSON-RPC 2.0 the notification has no `id`; per the MCP HTTP
        // transport the server must return 202 Accepted with no body.
        var notification = await PostRpcAsync("""
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """);

        notification.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var bodyBytes = await notification.Content.ReadAsByteArrayAsync();
        bodyBytes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task UnknownNotification_AcceptedSilentlyWithoutBody()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","method":"notifications/unknown"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var bodyBytes = await response.Content.ReadAsByteArrayAsync();
        bodyBytes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task NotificationsInitialized_WithId_IsRejectedAsInvalidRequest()
    {
        // JSON-RPC 2.0 and the MCP 2025-03-26 schema forbid an id on
        // notifications; a `notifications/*` method that carries an id is
        // not a real notification and must be surfaced as invalid_request
        // rather than silently dropped with HTTP 202.
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":42,"method":"notifications/initialized"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetInt32().Should().Be(42);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32600);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
        error.GetProperty("message").GetString().Should().Contain("notifications");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task UnknownNotification_WithId_IsRejectedAsInvalidRequest()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"n-1","method":"notifications/unknown"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("n-1");
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task UnknownMethod_ReturnsMethodNotFoundJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"probe-1","method":"nonexistent/method"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("probe-1");
        var error = root.GetProperty("error");
        // JSON-RPC 2.0 reserves -32601 for method-not-found.
        error.GetProperty("code").GetInt32().Should().Be(-32601);
        error.GetProperty("message").GetString().Should().Contain("nonexistent/method");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("not_found");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task MalformedJson_ReturnsParseErrorJsonRpcCode()
    {
        var response = await PostRpcAsync("{not-json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        // JSON-RPC 2.0 requires the response id to be explicit null when the
        // server cannot determine the client's original id.
        root.TryGetProperty("id", out var id).Should().BeTrue();
        id.ValueKind.Should().Be(JsonValueKind.Null);

        var error = root.GetProperty("error");
        // JSON-RPC 2.0 reserves -32700 for parse errors.
        error.GetProperty("code").GetInt32().Should().Be(-32700);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task EmptyBody_ReturnsParseErrorWithNullId()
    {
        var response = await PostRpcAsync(string.Empty);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.TryGetProperty("id", out var id).Should().BeTrue();
        id.ValueKind.Should().Be(JsonValueKind.Null);

        var error = root.GetProperty("error");
        // An empty body is not valid JSON — it must surface as parse error.
        error.GetProperty("code").GetInt32().Should().Be(-32700);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task PrimitiveBody_ReturnsInvalidRequestWithNullId()
    {
        // A valid JSON number is not a valid JSON-RPC envelope, so the transport
        // surfaces -32600 invalid_request rather than -32700 parse_error.
        var response = await PostRpcAsync("42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task NullId_IsRejectedAsInvalidRequest()
    {
        // MCP restricts request ids to string or integer; an explicit null id is
        // reserved for error responses when the server cannot determine the id.
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":null,"method":"tools/list"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task BooleanId_IsRejectedAsInvalidRequest()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":true,"method":"tools/list"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task FractionalId_IsRejectedAsInvalidRequest()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":1.5,"method":"tools/list"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task LargeIntegerId_IsAcceptedAndEchoedVerbatim()
    {
        // JSON's numeric model has no maximum, and MCP only requires the id to
        // be a string or integer. Validate by syntax, not by Int64 range, so
        // clients that generate very large integer ids still receive a
        // well-formed response echoing the original id token.
        const string bigId = "99999999999999999999";
        var response = await PostRpcAsync($$"""
            {"jsonrpc":"2.0","id":{{bigId}},"method":"tools/list"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.TryGetProperty("error", out _).Should().BeFalse();
        var id = root.GetProperty("id");
        id.ValueKind.Should().Be(JsonValueKind.Number);
        id.GetRawText().Should().Be(bigId);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ObjectId_IsRejectedAsInvalidRequest()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":{"nested":"value"},"method":"tools/list"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task BatchWithMultipleRequests_ReturnsArrayOfResponses()
    {
        var response = await PostRpcAsync("""
            [
              {"jsonrpc":"2.0","id":1,"method":"tools/list"},
              {"jsonrpc":"2.0","id":"a","method":"resources/list"}
            ]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(2);

        var ids = root.EnumerateArray().Select(e => e.GetProperty("id")).ToArray();
        ids.Should().Contain(e => e.ValueKind == JsonValueKind.Number && e.GetInt32() == 1);
        ids.Should().Contain(e => e.ValueKind == JsonValueKind.String && e.GetString() == "a");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task BatchOfOnlyNotifications_ReturnsAccepted()
    {
        var response = await PostRpcAsync("""
            [
              {"jsonrpc":"2.0","method":"notifications/initialized"},
              {"jsonrpc":"2.0","method":"notifications/unknown"}
            ]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var bodyBytes = await response.Content.ReadAsByteArrayAsync();
        bodyBytes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task BatchMixedWithNotification_ReturnsOnlyResponseForRequest()
    {
        var response = await PostRpcAsync("""
            [
              {"jsonrpc":"2.0","id":7,"method":"tools/list"},
              {"jsonrpc":"2.0","method":"notifications/initialized"}
            ]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(1);
        root[0].GetProperty("id").GetInt32().Should().Be(7);
        root[0].TryGetProperty("error", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task EmptyBatch_ReturnsInvalidRequestWithNullId()
    {
        var response = await PostRpcAsync("[]");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        // Per JSON-RPC 2.0 §6 an empty batch is itself an invalid request —
        // the server responds with a single response object, not an array.
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task BatchContainingInitialize_IsRejectedAsInvalidRequest()
    {
        // MCP 2025-03-26 lifecycle: the `initialize` request MUST NOT be
        // part of a JSON-RPC batch. The server rejects the entire batch
        // with a single invalid_request response object whose id is null
        // rather than dispatching other elements alongside it.
        var response = await PostRpcAsync("""
            [
              {"jsonrpc":"2.0","id":"init-1","method":"initialize","params":{
                "protocolVersion":"2025-03-26",
                "capabilities":{},
                "clientInfo":{"name":"honua-tests","version":"1.0.0"}
              }},
              {"jsonrpc":"2.0","id":1,"method":"tools/list"}
            ]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32600);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
        error.GetProperty("message").GetString().Should().Contain("initialize");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task BatchWithInvalidElement_ReturnsPerElementInvalidRequestError()
    {
        var response = await PostRpcAsync("""
            [
              {"jsonrpc":"2.0","id":1,"method":"tools/list"},
              "not-an-object"
            ]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(2);
        var errorElement = root.EnumerateArray()
            .FirstOrDefault(e => e.TryGetProperty("error", out _));
        errorElement.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        errorElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ResourceTemplatesList_AdvertisesParameterizedUris()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"tpl-1","method":"resources/templates/list"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        var templates = root.GetProperty("result").GetProperty("resourceTemplates");
        templates.ValueKind.Should().Be(JsonValueKind.Array);
        var uriTemplates = templates.EnumerateArray()
            .Select(t => t.GetProperty("uriTemplate").GetString())
            .ToArray();
        uriTemplates.Should().Contain("honua://jobs/{jobId}");
        uriTemplates.Should().Contain("honua://jobs/{jobId}/results");
        uriTemplates.Should().Contain("honua://workspaces/{workspaceId}");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ResourcesList_ExcludesParameterizedUris()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"list-1","method":"resources/list"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        var resources = root.GetProperty("result").GetProperty("resources");
        var uris = resources.EnumerateArray()
            .Select(t => t.GetProperty("uri").GetString())
            .ToArray();
        uris.Should().NotContain(u => u != null && u.Contains('{'));
        uris.Should().Contain("honua://catalog/processes");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ToolsCall_WithToolExecutionFailure_SurfacesIsErrorInsideResult()
    {
        // honua_cancel_job throws GeoprocessingValidationException for a blank
        // job id. Per MCP 2025-03-26 tool execution errors must appear inside
        // result with isError: true so the client can read a structured
        // envelope without parsing JSON-RPC protocol errors.
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"err-1","method":"tools/call","params":{"name":"honua_cancel_job","arguments":{"jobId":""}}}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("err-1");
        root.TryGetProperty("error", out _).Should().BeFalse();

        var result = root.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("error");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ToolsCall_PlanWithNullStepEntry_SurfacesInvalidArgumentInsideResult()
    {
        // Malformed {"steps":[null]} would otherwise dereference step.Kind and
        // turn into a NullReferenceException that the tool envelope renders as
        // an internal error. The invariant is that plan-shape validation
        // failures stay on the invalid_argument path inside the isError result.
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"null-step","method":"tools/call","params":{
                "name":"honua_validate_plan",
                "arguments":{"plan":{"planId":"plan-1","steps":[null]}}
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("null-step");
        root.TryGetProperty("error", out _).Should().BeFalse();

        var result = root.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should().Contain("step at index 0");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ToolsCall_UnknownToolName_ReturnsInvalidParamsJsonRpcError()
    {
        // Protocol-level param errors (unknown tool name) stay as JSON-RPC
        // invalid_params; only tool-execution failures move to isError.
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"unk-1","method":"tools/call","params":{"name":"honua_does_not_exist","arguments":{}}}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        var error = root.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32602);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ResourcesRead_UnknownUri_ReturnsResourceNotFoundJsonRpcCode()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"rr-1","method":"resources/read","params":{"uri":"honua://unknown/thing"}}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        var error = root.GetProperty("error");
        // MCP 2025-03-26 reserves -32002 for unknown resource URIs.
        error.GetProperty("code").GetInt32().Should().Be(-32002);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("not_found");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ToolsCall_WithMalformedParams_ReturnsInvalidArgumentJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"call-bad","method":"tools/call","params":["not","an","object"]}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("call-bad");
        var error = root.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ResourcesRead_WithMalformedParams_ReturnsInvalidArgumentJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"read-bad","method":"resources/read","params":"not-an-object"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("read-bad");
        var error = root.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    private async Task<HttpResponseMessage> PostRpcAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);
        return await _client.PostAsync(McpRoute, content);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
