// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Security;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Verifies the MCP propose-and-poll tool routes through the operation gateway and
/// returns a structured result with a proposalId + honua://proposals/{id} URI when
/// approval is required (#1696).
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class ProposeOperationToolTests
{
    private static DefaultHttpContext ContextWithGateway(
        IOperationGateway gateway,
        IOperationExecutorCatalog? catalog = null,
        AuthorizationResult? authorizationResult = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gateway);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object>(),
                AuthenticationExtensions.AdminPolicy)
            .Returns(authorizationResult ?? AuthorizationResult.Success());
        services.AddSingleton(authorization);
        if (catalog != null)
        {
            services.AddSingleton(catalog);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = CreatePrincipal()
        };
    }

    private static ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Agent X"),
                new Claim(ClaimTypes.NameIdentifier, "agent-x"),
                new Claim("sub", "agent-x"),
                new Claim("iss", "https://issuer.example"),
                new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc),
            ],
            "Oidc"));

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_WhenApprovalRequired_ReturnsProposalIdAndResourceUri()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, default, "test"),
            ProposalId = "proposal-123",
            Message = "Proposal created and awaiting approval."
        });

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy", Reason = "ship it" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var content = result.StructuredContent!.Value;
        content.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        content.GetProperty("proposalId").GetString().Should().Be("proposal-123");
        content.GetProperty("resourceUri").GetString().Should().Be("honua://proposals/proposal-123");
        gateway.LastRequest.Should().NotBeNull();
        gateway.LastRequest!.RequestedBy.Should().Be("oidc:subject:https%3A%2F%2Fissuer.example:agent-x");
        gateway.LastRequest.RequestedByAgent.Should().Be("agent:Agent X");
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_WhenDirectExecute_ReturnsExecutionOutcome()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.AdminConfigChange, default, "test"),
            ExecutionOperationId = "exec-1"
        });

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "AdminConfigChange" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        content.GetProperty("outcome").GetString().Should().Be("Executed");
        content.GetProperty("executionOperationId").GetString().Should().Be("exec-1");
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_UnknownKind_ReturnsRejected()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Seed, default, "test")
        });

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "NotARealKind" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway), arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("outcome").GetString().Should().Be("rejected");
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_ReportsSupportedKindsFromCatalog_OnEveryOutcome()
    {
        // #2563: supportedKinds must reflect genuinely registered executors (MetadataRelease
        // appears, Seed does not) so an agent never hits a silent dead end.
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.NotSupported,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Seed, default, "test"),
            Message = "No executor is registered for operation kind 'Seed'; the operation was not performed."
        });
        var catalog = new FakeExecutorCatalog([OperationClass.AdminConfigChange, OperationClass.Deploy, OperationClass.MetadataRelease]);

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Seed" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway, catalog), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("outcome").GetString().Should().Be("NotSupported");
        var supportedKinds = content.GetProperty("supportedKinds").EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();
        supportedKinds.Should().BeEquivalentTo(["AdminConfigChange", "Deploy", "MetadataRelease"]);
        supportedKinds.Should().NotContain("Seed", "Seed has no registered executor and must never be advertised as supported");
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_WithoutAdminWrite_IsDeniedBeforeGateway()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Deploy, default, "test")
        });
        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var act = () => tool.InvokeAsync(
            ContextWithGateway(gateway, authorizationResult: AuthorizationResult.Failed()),
            arguments,
            CancellationToken.None);

        await act.Should().ThrowAsync<Honua.Geoprocessing.GeoprocessingAuthorizationException>();
        gateway.LastRequest.Should().BeNull();
    }

    private sealed class FakeExecutorCatalog(IReadOnlyCollection<OperationClass> supportedKinds) : IOperationExecutorCatalog
    {
        public IReadOnlyCollection<OperationClass> SupportedKinds { get; } = supportedKinds;
    }

    private sealed class FakeGateway(OperationGatewayResult result) : IOperationGateway
    {
        public OperationGatewayRequest? LastRequest { get; private set; }

        public Task<OperationGatewayResult> RouteAsync(OperationGatewayRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<OperationGatewayResult> CreateApprovalProposalAsync(OperationGatewayRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task<OperationProposal?> ApplyApprovedProposalAsync(string proposalId, string approvedBy, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);

        public Task<OperationProposal?> RejectProposalAsync(string proposalId, string rejectedBy, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);
    }
}
