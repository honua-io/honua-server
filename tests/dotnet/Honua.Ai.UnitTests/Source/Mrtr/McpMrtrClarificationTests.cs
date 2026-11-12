// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using static Honua.Server.Tests.Features.Protocols.Mcp.Mrtr.McpMrtrTestKit;

namespace Honua.Server.Tests.Features.Protocols.Mcp.Mrtr;

public sealed class McpMrtrClarificationTests
{
    private const string Goal = """{"goal":"vague","explicitInputs":["parcels"]}""";
    private const string FormCapable = """{"elicitation":{}}""";
    private const string AcceptAnalyze = """{"clarify":{"action":"accept","content":{"workflow_family":"Analyze"}}}""";

    private readonly IGroundingService _grounding = Substitute.For<IGroundingService>();

    [UnitTest]
    public async Task GroundCandidates_FormCapableRequest_ReturnsInputRequiredWithoutCaching()
    {
        Grounds(Asking("workflow_family"));
        using var services = Services(_grounding);

        var result = (await SendAsync(services, new McpCall(Goal, FormCapable))).GetProperty("result");

        result.GetProperty("resultType").GetString().Should().Be("input_required");
        result.TryGetProperty("ttlMs", out _).Should().BeFalse();
        result.GetProperty("requestState").GetString().Should().StartWith("hr1.");
        var input = result.GetProperty("inputRequests").GetProperty("clarify");
        input.GetProperty("method").GetString().Should().Be("elicitation/create");
        input.GetProperty("params").GetProperty("mode").GetString().Should().Be("form");
        input.GetProperty("params").GetProperty("requestedSchema").GetProperty("properties")
            .GetProperty("workflow_family").GetProperty("enum").GetArrayLength().Should().Be(2);
    }

    [UnitTest]
    public async Task GroundCandidates_RequestWithoutElicitation_KeepsEnvelope()
    {
        Grounds(Asking("workflow_family"));
        using var services = Services(_grounding);

        var result = (await SendAsync(services, new McpCall(Goal, "{}"))).GetProperty("result");

        result.GetProperty("resultType").GetString().Should().Be("complete");
        result.GetProperty("structuredContent").GetProperty("clarification").GetProperty("intentId").GetString()
            .Should().Be("intent-abc");
    }

    [UnitTest]
    public async Task GroundCandidates_RetryOnAnotherReplica_ResumesIntentWithAnswers()
    {
        Grounds(Asking("workflow_family"), Resolved());
        using var replicaA = Services(_grounding);
        using var replicaB = Services(_grounding);
        var state = (await SendAsync(replicaA, new McpCall(Goal, FormCapable)))
            .GetProperty("result").GetProperty("requestState").GetString();

        var retry = new McpCall("""{"explicitInputs":["parcels"],"goal":"vague"}""", FormCapable)
        {
            RequestState = state,
            InputResponses = AcceptAnalyze,
        };
        var result = (await SendAsync(replicaB, retry)).GetProperty("result");

        result.GetProperty("resultType").GetString().Should().Be("complete");
        await _grounding.Received(1).GroundAsync(
            Arg.Is<GroundingRequest>(request => request.IntentId == "intent-abc"
                && request.ClarificationResponse!.Answers["workflow_family"].Single() == "Analyze"),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task GroundCandidates_SecondRound_CarriesEarlierAnswers()
    {
        Grounds(Asking("workflow_family"), Asking("extent"), Resolved());
        using var services = Services(_grounding);
        var first = (await SendAsync(services, new McpCall(Goal, FormCapable))).GetProperty("result");
        var second = (await SendAsync(services, new McpCall(Goal, FormCapable)
        {
            RequestState = first.GetProperty("requestState").GetString(),
            InputResponses = AcceptAnalyze,
        })).GetProperty("result");
        second.GetProperty("resultType").GetString().Should().Be("input_required");

        await SendAsync(services, new McpCall(Goal, FormCapable)
        {
            RequestState = second.GetProperty("requestState").GetString(),
            InputResponses = """{"clarify":{"action":"accept","content":{"extent":"PublishData"}}}""",
        });

        await _grounding.Received(1).GroundAsync(
            Arg.Is<GroundingRequest>(request => request.ClarificationResponse != null
                && request.ClarificationResponse.Answers.Count == 2
                && request.ClarificationResponse.Answers["workflow_family"].Single() == "Analyze"
                && request.ClarificationResponse.Answers["extent"].Single() == "PublishData"),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("""{"clarify":{"action":"decline"}}""")]
    [InlineData("""{"clarify":{"action":"accept","content":{}}}""")]
    [InlineData("""{"clarify":{"action":"accept","content":{"workflow_family":"  "}}}""")]
    public async Task GroundCandidates_DeclinedOrEmptyRetry_ReturnsEnvelopeInsteadOfAskingAgain(string responses)
    {
        Grounds(Asking("workflow_family"));
        using var services = Services(_grounding);
        var state = Mint(services, Binding(services, GroundCandidatesTool.ToolName, Goal),
            new(McpRequestStateKind.Clarification, """{"i":"intent-abc","a":{}}""", InMinutes(services, 5)));

        var result = (await SendAsync(services, new McpCall(Goal, FormCapable)
        {
            RequestState = state,
            InputResponses = responses,
        })).GetProperty("result");

        result.GetProperty("resultType").GetString().Should().Be("complete");
        result.GetProperty("structuredContent").TryGetProperty("clarification", out _).Should().BeTrue();
    }

    [UnitTest]
    public void Refusal_AnonymousCaller_GetsNoContinuation()
    {
        using var services = Services(_grounding);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Items[McpModernProtocol.RequestItemKey] = true;

        McpRequestContinuation.Refusal(context, new PlainTool(), new McpToolsCallParams { Name = PlainTool.ToolName })
            .Should().BeNull();
        McpRequestContinuation.From(context).Should().BeNull("anonymous callers share one binding, so MRTR is off for them");
    }

    [UnitTheory]
    [InlineData("tampered")]
    [InlineData("responses-without-state")]
    [InlineData("other-arguments")]
    [InlineData("tool-without-mrtr")]
    public async Task Retry_InvalidState_IsRefusedBeforeTheToolRuns(string variant)
    {
        using var services = Services(_grounding);
        var toolName = variant == "tool-without-mrtr" ? PlainTool.ToolName : GroundCandidatesTool.ToolName;
        var state = Mint(services, Binding(services, toolName, Goal),
            new(McpRequestStateKind.Clarification, """{"i":"intent-abc","a":{}}""", InMinutes(services, 5)));
        var call = new McpCall(variant == "other-arguments" ? """{"goal":"different"}""" : Goal, FormCapable)
        {
            ToolName = toolName,
            RequestState = variant switch
            {
                "responses-without-state" => null,
                "tampered" => state[..^3] + "AAA",
                _ => state,
            },
            InputResponses = AcceptAnalyze,
        };

        var error = (await SendAsync(services, call)).GetProperty("error");

        error.GetProperty("code").GetInt32().Should().Be(McpErrorMapper.JsonRpcInvalidParams);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.InvalidRequestState);
        await _grounding.DidNotReceiveWithAnyArgs().GroundAsync(default!, default!, default);
    }

    [UnitTest]
    public void Refusal_LegacyRequestCarryingState_IsRefusedAndPlainLegacyCallIsUnchanged()
    {
        using var services = Services(_grounding);
        var context = new DefaultHttpContext { RequestServices = services, User = User() };
        var carrying = new McpToolsCallParams { Name = PlainTool.ToolName, RequestState = Json("\"hr1.x\"") };

        McpRequestContinuation.Refusal(context, new PlainTool(), carrying)!.Data!.Code
            .Should().Be(McpErrorMapper.Codes.InvalidRequestState);
        McpRequestContinuation.Refusal(context, new PlainTool(), new McpToolsCallParams { Name = PlainTool.ToolName })
            .Should().BeNull();
        McpRequestContinuation.From(context).Should().BeNull();
    }

    private void Grounds(GroundingResult first, params GroundingResult[] rest) =>
        _grounding.GroundAsync(Arg.Any<GroundingRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(first, rest);
}
