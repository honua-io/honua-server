// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
        IOperationEnvelopeFactory? envelopeFactory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gateway);
        services.AddSingleton(envelopeFactory ?? new FakeEnvelopeFactory());
        if (catalog != null)
        {
            services.AddSingleton(catalog);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "agent-x")], "Test"))
        };
    }

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
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_ModelCall_UsesProposalPathAndNeverRoutesToDirectExecution()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, default, "model-facing-proposal-requires-approval"),
            ProposalId = "proposal-gated"
        });

        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        content.GetProperty("outcome").GetString().Should().Be("ProposalCreated");
        gateway.RouteCalls.Should().Be(0, "a model-facing call must never enter the direct-execution route");
        gateway.ProposalCalls.Should().Be(1);
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
    public async Task ProposeOperation_ReadScopedBearer_CannotProposeDeploy()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, default, "test"),
            ProposalId = "must-not-be-created",
        });
        var context = ContextWithGateway(gateway);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "agent-x"),
            new Claim(OperatorScopeCatalog.ScopeGovernedClaimType, OperatorScopeCatalog.ScopeGovernedClaimValue),
            new Claim(OperatorScopeCatalog.ScopeClaimType, OperatorScopeCatalog.Read),
        ], "Bearer"));
        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("outcome").GetString().Should().Be("rejected");
        result.StructuredContent.Value.GetProperty("message").GetString().Should().Contain(OperatorScopeCatalog.Publish);
        gateway.ProposalCalls.Should().Be(0);
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
            new McpProposeOperationArgument { Kind = "Deploy" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(ContextWithGateway(gateway, catalog), arguments, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("outcome").GetString().Should().Be("NotSupported");
        var supportedKinds = content.GetProperty("supportedKinds").EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();
        supportedKinds.Should().BeEquivalentTo(["Deploy", "MetadataRelease"]);
        supportedKinds.Should().NotContain("AdminConfigChange", "dedicated typed tools own admin mutations");
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_AcceptanceFailure_DoesNotCreateProposal()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, default, "test"),
            ProposalId = "must-not-be-created",
        });
        var envelopeFactory = new FakeEnvelopeFactory(OperationHandleStatus.Failed, auditId: null);
        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy" },
            McpJsonContext.Default.McpProposeOperationArgument);

        var result = await tool.InvokeAsync(
            ContextWithGateway(gateway, envelopeFactory: envelopeFactory),
            arguments,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("outcome").GetString().Should().Be("Failed");
        gateway.ProposalCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_propose_operation")]
    public async Task ProposeOperation_UsesCanonicalActorForProposalOwnership()
    {
        var gateway = new FakeGateway(new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, default, "test"),
            ProposalId = "proposal-canonical-actor",
        });
        var context = ContextWithGateway(gateway);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "agent-x"), new Claim(ClaimTypes.NameIdentifier, "subject-x")],
            "Test"));
        var tool = new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpProposeOperationArgument { Kind = "Deploy" },
            McpJsonContext.Default.McpProposeOperationArgument);

        await tool.InvokeAsync(context, arguments, CancellationToken.None);

        gateway.LastProposalRequest!.RequestedBy.Should().Be(McpAuthorizationHelper.ResolveActorId(context.User));
    }

    private sealed class FakeExecutorCatalog(IReadOnlyCollection<OperationClass> supportedKinds) : IOperationExecutorCatalog
    {
        public IReadOnlyCollection<OperationClass> SupportedKinds { get; } = supportedKinds;
    }

    private sealed class FakeGateway(OperationGatewayResult result) : IOperationGateway
    {
        public int RouteCalls { get; private set; }

        public int ProposalCalls { get; private set; }

        public OperationGatewayRequest? LastProposalRequest { get; private set; }

        public Task<OperationGatewayResult> RouteAsync(OperationGatewayRequest request, CancellationToken cancellationToken = default)
        {
            RouteCalls++;
            return Task.FromResult(result);
        }

        public Task<OperationGatewayResult> CreateApprovalProposalAsync(
            string operationInstanceId,
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            ProposalCalls++;
            LastProposalRequest = request;
            return Task.FromResult(result);
        }

        public Task<OperationProposal?> ApplyApprovedProposalAsync(string proposalId, string approvedBy, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);

        public Task<OperationProposal?> RejectProposalAsync(string proposalId, string rejectedBy, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);
    }

    private sealed class FakeEnvelopeFactory(
        OperationHandleStatus status = OperationHandleStatus.Accepted,
        string? auditId = "audit-model-call") : IOperationEnvelopeFactory
    {
        public Task<OperationHandle> CreateAcceptedAsync(
            string operationId,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OperationHandle
            {
                OperationInstanceId = "opinst-model-call",
                OperationId = operationId,
                Status = status,
                CorrelationId = "corr-model-call",
                AuditId = auditId,
                Reason = status == OperationHandleStatus.Failed ? "acceptance failed" : null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        public Task<OperationHandle> CompleteCacheHitAsync(
            string operationId,
            OperationPolicyContext context,
            string sourceOperationInstanceId,
            string? sourceAuditId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
