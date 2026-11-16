// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Domain;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

internal sealed class StudioMcpHttpClient(HttpClient client)
{
    private const string JsonMediaType = "application/json";

    public async Task<JsonElement> CallToolAsync(string toolName, string argumentsJson)
    {
        var result = await CallToolRawAsync(toolName, argumentsJson);
        var failed = result.TryGetProperty("isError", out var isError) && isError.GetBoolean();
        var failure = failed && result.TryGetProperty("structuredContent", out var errorContent)
            ? errorContent.GetRawText()
            : "(none)";
        failed.Should().BeFalse($"'{toolName}' must succeed, but reported {failure}");
        return result.GetProperty("structuredContent");
    }

    // Only successful mutation scenarios opt into human approval; raw denial calls stay untouched.
    public async Task<JsonElement> CallApprovedToolAsync(WebAppFixture fixture, string toolName, string argumentsJson)
    {
        var result = await CallToolRawAsync(toolName, argumentsJson);
        var content = result.GetProperty("structuredContent");
        string proposalId;
        if (content.TryGetProperty("operation", out var operation))
        {
            var failed = result.TryGetProperty("isError", out var isError) && isError.GetBoolean();
            failed.Should().BeFalse();
            operation.GetProperty("status").GetString().Should().Be("RequiresApproval");
            proposalId = operation.GetProperty("proposalId").GetString()!;
        }
        else
        {
            result.GetProperty("isError").GetBoolean().Should().BeTrue();
            content.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
            proposalId = content.GetProperty("proposalId").GetString()!;
        }

        var handle = await HumanApprovedOperation.ApproveAndReadHandleAsync(fixture, proposalId);
        handle.GetProperty("status").GetString().Should().Be("Completed");
        using var payload = JsonDocument.Parse(
            handle.GetProperty("result").GetProperty("details").GetProperty("payload").GetString()!);
        return payload.RootElement.Clone();
    }

    public async Task<JsonElement> CallToolRawAsync(string toolName, string argumentsJson)
    {
        var response = await SendRpcAsync($$"""
            {"jsonrpc":"2.0","id":"{{toolName}}","method":"tools/call","params":{"name":"{{toolName}}","arguments":{{argumentsJson}} } }
            """);
        var hasProtocolError = response.TryGetProperty("error", out var error);
        var protocolError = hasProtocolError ? error.GetRawText() : "(none)";
        hasProtocolError.Should().BeFalse(
            $"'{toolName}' must not fail at the JSON-RPC protocol level: {protocolError}");
        return response.GetProperty("result");
    }

    public async Task<JsonElement> SendRpcAsync(string requestJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(requestJson, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) } },
        };

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    public async Task<StudioPackageEnvelope> SyncEnvelopeAsync(Guid draftId)
    {
        using var response = await client.GetAsync($"/api/v1/studio/package-drafts/{draftId:D}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var envelopeJson = document.RootElement.GetProperty("data").GetProperty("envelope").GetRawText();
        return JsonSerializer.Deserialize(envelopeJson, StudioJsonContext.Default.StudioPackageEnvelope)
            ?? throw new InvalidOperationException("Expected a Studio package envelope in the draft response.");
    }
}
