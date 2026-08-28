// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Abstractions;
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
    public void ServicePublishMapper_MapsTypedReplayContractWithoutLegacyAlias()
    {
        var mapper = new ServicePublishApprovalRequestMapper();

        var mapped = mapper.Map(
            ServicePublishOperation.BuildDescriptor(),
            new OperationRequest
            {
                OperationId = ServicePublishOperation.OperationId,
                ConnectionId = "connection-1",
                ServiceName = "roads",
                DryRun = true,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["schema"] = "public",
                    ["table"] = "roads",
                    ["layerName"] = "Roads",
                },
            },
            Context() with { TenantId = "tenant-a", SchemaName = "tenant_a" },
            Decision());

        mapped.Kind.Should().Be(OperationClass.ServicePublish);
        mapped.Kind.Should().NotBe(OperationClass.AdminConfigChange);
        mapped.Kind.Should().NotBe(OperationClass.Deploy);
        mapped.Kind.Should().NotBe(OperationClass.MetadataRelease);
        mapped.Kind.Should().NotBe(OperationClass.Geoprocess);
        mapped.OperationInstanceId.Should().Be("opinst-123");
        mapped.CorrelationId.Should().Be("corr-123");
        mapped.Plan.Should().NotBeNull();
        mapped.Plan!.ExecutionPayload.Should().Be(mapped.ExecutionPayload);
        mapped.ExecutionPayload.Should().Contain("\"layerName\":\"Roads\"");
        mapped.ExecutionPayload.Should().Contain("\"dryRun\":true");
        mapped.ExecutionPayload.Should().Contain("\"tenantId\":\"tenant-a\"");
        mapped.ExecutionPayload.Should().Contain("\"schemaName\":\"tenant_a\"");

        var replay = mapper.MapReplay(mapped);
        replay.OperationId.Should().Be(ServicePublishOperation.OperationId);
        replay.ConnectionId.Should().Be("connection-1");
        replay.Parameters["schema"].Should().Be("public");
        replay.Parameters["table"].Should().Be("roads");
        replay.Parameters["layerName"].Should().Be("Roads");
    }

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
                Arg.Any<string>(),
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
            .AddSingleton(AllowApprovalGuardrail())
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
                Arg.Is<string>(value => value == "opinst-123"),
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
            .AddSingleton(AllowApprovalGuardrail())
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

    [UnitTest]
    public async Task CreateProposalAsync_BlockedControlPlaneClass_FailsBeforePersistence()
    {
        var gateway = Substitute.For<IOperationGateway>();
        var guardrail = Substitute.For<IGuardrailLadder>();
        guardrail.Resolve(OperationClass.AdminConfigChange)
            .Returns(new GuardrailDecision(
                GuardrailTier.Blocked,
                OperationClass.AdminConfigChange,
                HonuaEdition.Pro,
                "operator-blocked"));
        var services = new ServiceCollection()
            .AddSingleton(gateway)
            .AddSingleton(guardrail)
            .BuildServiceProvider();

        var result = await CreateBridge(services).CreateProposalAsync(
            Descriptor(), Request(), Context(), Decision());

        result.IsDurable.Should().BeFalse();
        result.Reason.Should().Contain("guardrail blocks");
        await gateway.DidNotReceiveWithAnyArgs()
            .CreateApprovalProposalAsync(default!, default);
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

    private static IGuardrailLadder AllowApprovalGuardrail()
    {
        var guardrail = Substitute.For<IGuardrailLadder>();
        guardrail.Resolve(Arg.Any<OperationClass>()).Returns(call =>
            new GuardrailDecision(
                GuardrailTier.RequiresApproval,
                call.Arg<OperationClass>(),
                HonuaEdition.Pro,
                "test"));
        return guardrail;
    }

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

        public OperationRequest MapReplay(OperationGatewayRequest request)
            => Request();
    }
}
