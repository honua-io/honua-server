// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class McpRefusalNextActionTests
{
    public static TheoryData<Exception, string> Refusals() => new()
    {
        { new GeoprocessingAuthorizationException(requiresAuthentication: true), "X-API-Key" },
        {
            new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                "The access token's scopes do not permit 'Execute' on Process.",
                denialReason: AuthorizationDenialReason.InsufficientScope),
            "honua_admin_api_key_effective_permissions"
        },
        { new GeoprocessingAuthorizationException(requiresAuthentication: false), "role or grant" },
        { new GeoprocessingApprovalRequiredException("policy/publish"), "Submit the change as a proposal" },
        { new GeoprocessingPreconditionFailedException("already terminal"), "resolve the condition" },
        { new GeoprocessingNotFoundException("job-missing"), "matching list tool" },
        { new GeoprocessingValidationException("steps cannot be empty"), "inputSchema" },
        { new GeoprocessingIdempotencyConflictException("job-other"), "use a new key" },
    };

    [UnitTheory]
    [MemberData(nameof(Refusals))]
    public void Map_EveryRefusalClass_SaysWhatToDoNext(Exception exception, string expected)
    {
        var error = McpErrorMapper.Map(exception);

        error.Data!.NextAction.Should().Contain(expected);
        error.Message.Should().NotBeNullOrWhiteSpace("the reason stays in the message");
    }

    [UnitTest]
    public void ErrorResult_ToolCallFailure_CarriesNextActionInStructuredContent()
    {
        var result = Honua.Ai.Protocols.Mcp.Tools.McpToolHelpers.ErrorResult(
            new GeoprocessingApprovalRequiredException("policy/publish", proposalId: "prop-9"));

        result.IsError.Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("nextAction").GetString()
            .Should().Contain("prop-9", "a tools/call refusal must carry the same next step as the JSON-RPC error");
    }

    [UnitTest]
    public void Map_ApprovalWithProposal_NamesTheProposalAndItsResource()
    {
        var data = new McpErrorData
        {
            Code = McpErrorMapper.Codes.FailedPrecondition,
            ApprovalRequired = true,
            ProposalId = "prop-7",
            ResourceUri = McpResourceUris.ProposalUri("prop-7"),
        };

        var next = McpRefusalNextAction.For(data);

        next.Should().Contain("prop-7").And.Contain(McpResourceUris.ProposalUri("prop-7"));
    }

    [UnitTheory]
    [InlineData(30, "Retry after 30 seconds.")]
    [InlineData(null, "Retry shortly.")]
    public void For_RetryableUnavailable_SaysWhenToRetry(int? retryAfter, string expected)
    {
        var data = Unavailable(retryAfter);
        data.Retryable = true;

        McpRefusalNextAction.For(data).Should().Be(expected);
    }

    [UnitTest]
    public void For_NonRetryableUnavailable_ReturnsTheRemediation()
    {
        var data = Unavailable(retryAfter: null);
        data.Retryable = false;

        McpRefusalNextAction.For(data).Should().Be("Enable the PostgreSQL system database.");
    }

    private static McpErrorData Unavailable(int? retryAfter) => new()
    {
        Code = McpErrorMapper.Codes.Unavailable,
        RetryAfterSeconds = retryAfter,
        Remediation = "Enable the PostgreSQL system database.",
    };

    [UnitTest]
    public void Factories_NotFoundAndUnauthenticated_CarryNextAction_WithoutRevealingOtherTenants()
    {
        var notFound = McpErrorMapper.ResourceNotFound("Proposal 'p' was not found.");

        notFound.Data!.NextAction.Should().NotContain("tenant", "not found must not hint at another tenant");
        McpErrorMapper.Unauthenticated().Data!.NextAction.Should().Contain("Authorization: Bearer");
        McpErrorMapper.InvalidArgument("bad").Data!.NextAction.Should().Contain("inputSchema");
        JsonSerializer.Serialize(notFound, McpJsonContext.Default.McpJsonRpcError).Should().Contain("\"nextAction\":");
    }
}
