// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Studio;

internal static class ApprovedStudioDraftCall
{
    public static async Task<JsonElement?> CompleteAsync(JsonElement response, string tool,
        (WebAppFixture Fixture, AdminAuthEndpointsTests.StubHttpClientFactory IdentityProvider, string? Tenant) context)
    {
        var verb = tool.Replace("honua_studio_", "", StringComparison.Ordinal);
        if (verb is not ("create_draft" or "update_draft" or "validate_draft" or "save_version"
            or "add_layer" or "remove_layer" or "set_layer_style" or "set_layer_visibility" or "set_view"
            or "add_widget" or "remove_widget" or "bind_interaction" or "remove_interaction"
            or "add_control" or "remove_control")) return null;
        var proposalId = GetProposalId(response);
        if (proposalId is null) return null;

        using var human = await HumanApprovalHttpClient.AuthenticateAsync(
            context.Fixture, context.IdentityProvider, "studio-draft-setup-approver", context.Tenant);
        using var approval = await human.ApproveAsync(proposalId);
        var body = await approval.Content.ReadAsStringAsync();
        approval.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var scope = context.Fixture.Services.CreateScope();
        var proposal = await scope.ServiceProvider.GetRequiredService<IOperationProposalStore>().GetAsync(proposalId);
        proposal.Should().NotBeNull();
        proposal!.Status.Should().Be(OperationProposalStatus.Succeeded, body);
        proposal.Audit.OperationInstanceId.Should().NotBeNullOrWhiteSpace();
        var instance = await scope.ServiceProvider.GetRequiredService<IOperationInstanceStore>()
            .GetAsync(proposal.Audit.OperationInstanceId!);
        instance.Should().NotBeNull();
        instance!.Result.Should().NotBeNull();
        instance.Result!.Details.Should().ContainKey("payload");
        var payload = instance.Result.Details["payload"];
        using var document = JsonDocument.Parse(verb == "save_version" ? $"{{\"version\":{payload}}}" : payload);
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> RpcAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    public static JsonElement ReadResult(JsonElement response, string toolName)
    {
        var hasProtocolError = response.TryGetProperty("error", out var protocolError);
        hasProtocolError.Should().BeFalse(
            $"tool '{toolName}' should return a tool result: {(hasProtocolError ? protocolError.GetRawText() : "(none)")}");
        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse($"tool '{toolName}' failed: {result.GetRawText()}");
        }

        return result.GetProperty("structuredContent").Clone();
    }

    private static string? GetProposalId(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error)
            && error.TryGetProperty("data", out var data)
            && data.TryGetProperty("approvalRequired", out var required) && required.GetBoolean()
            && data.TryGetProperty("proposalId", out var errorProposal))
            return errorProposal.GetString();
        if (!response.TryGetProperty("result", out var result)
            || !result.TryGetProperty("structuredContent", out var content)) return null;
        if (content.TryGetProperty("approvalRequired", out var approvalRequired) && approvalRequired.GetBoolean()
            && content.TryGetProperty("proposalId", out var directProposal))
            return directProposal.GetString();
        if (content.TryGetProperty("operation", out var operation)
            && operation.TryGetProperty("status", out var status) && status.GetString() == "RequiresApproval"
            && operation.TryGetProperty("proposalId", out var proposal))
            return proposal.GetString();
        return null;
    }
}
