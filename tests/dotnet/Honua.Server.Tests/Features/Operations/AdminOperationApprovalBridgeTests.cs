// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class AdminOperationApprovalBridgeTests
{
    [UnitTest]
    public async Task CreateProposalAsync_MissingGateway_ReturnsNonDurableFailure()
    {
        var bridge = CreateBridge(new ServiceCollection().BuildServiceProvider());

        var result = await bridge.CreateProposalAsync(
            Descriptor(),
            Request(),
            Context(),
            Decision());

        result.IsDurable.Should().BeFalse();
        result.ProposalId.Should().BeNull();
        result.AuditId.Should().BeNull();
        result.Reason.Should().Contain("gateway is unavailable");
    }

    [UnitTest]
    public async Task CreateProposalAsync_GatewayOmitsAuditIdentity_ReturnsNonDurableFailure()
    {
        var gateway = Substitute.For<IOperationGateway>();
        gateway.CreateApprovalProposalAsync(
                Arg.Any<OperationGatewayRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new OperationGatewayResult
            {
                Outcome = OperationGatewayOutcome.ProposalCreated,
                Decision = GatewayDecision(),
                ProposalId = "proposal-123",
                AuditId = null,
                Message = "Proposal store accepted the request but audit did not.",
            });
        var services = new ServiceCollection()
            .AddSingleton(gateway)
            .BuildServiceProvider();
        var bridge = CreateBridge(services);

        var result = await bridge.CreateProposalAsync(
            Descriptor(),
            Request(),
            Context(),
            Decision());

        result.IsDurable.Should().BeFalse();
        result.ProposalId.Should().BeNull("a proposal without a joined audit receipt is not actionable");
        result.AuditId.Should().BeNull();
    }

    [UnitTest]
    public async Task CreateProposalAsync_DurableGateway_ReturnsSeparateJoinedIdentities()
    {
        OperationGatewayRequest? captured = null;
        var gateway = Substitute.For<IOperationGateway>();
        gateway.CreateApprovalProposalAsync(
                Arg.Do<OperationGatewayRequest>(request => captured = request),
                Arg.Any<CancellationToken>())
            .Returns(new OperationGatewayResult
            {
                Outcome = OperationGatewayOutcome.ProposalCreated,
                Decision = GatewayDecision(),
                ProposalId = "proposal-123",
                AuditId = "audit-456",
            });
        var services = new ServiceCollection()
            .AddSingleton(gateway)
            .BuildServiceProvider();
        var bridge = CreateBridge(services);

        var result = await bridge.CreateProposalAsync(
            Descriptor(),
            Request(),
            Context(),
            Decision());

        result.IsDurable.Should().BeTrue();
        result.ProposalId.Should().Be("proposal-123");
        result.AuditId.Should().Be("audit-456");
        captured.Should().NotBeNull();
        captured!.OperationInstanceId.Should().Be("opinst-123");
        captured.CorrelationId.Should().Be("corr-123");
        captured.OperationInstanceId.Should().NotBe(result.ProposalId);
    }

    private static AdminOperationApprovalBridge CreateBridge(IServiceProvider services)
        => new(
            services,
            [new TestMapper()],
            NullLogger<AdminOperationApprovalBridge>.Instance);

    private static OperationRequest Request() => new() { OperationId = "admin.test" };

    private static OperationPolicyContext Context() => new()
    {
        OperationInstanceId = "opinst-123",
        CorrelationId = "corr-123",
        PrincipalId = "operator-1",
    };

    private static PolicyDecision Decision() => new()
    {
        Kind = PolicyDecisionKind.RequireApproval,
        ApprovalLane = "operator-gate",
    };

    private static OperationDescriptor Descriptor() => new()
    {
        OperationId = "admin.test",
        ProviderId = "test",
        Title = "Test operation",
        Description = "Test approval bridge.",
        Category = "admin",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.MutatesData,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = true,
        },
    };

    private static GuardrailDecision GatewayDecision()
        => new(GuardrailTier.RequiresApproval, OperationClass.AdminConfigChange, HonuaEdition.Pro, "test");

    private sealed class TestMapper : IOperationApprovalRequestMapper
    {
        public string OperationId => "admin.test";

        public OperationGatewayRequest Map(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            PolicyDecision decision)
            => new()
            {
                Kind = OperationClass.AdminConfigChange,
                RequestedBy = context.PrincipalId,
                ExecutionPayload = "{}",
            };
    }
}
