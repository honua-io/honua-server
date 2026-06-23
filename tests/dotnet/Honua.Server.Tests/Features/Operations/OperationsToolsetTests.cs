// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// In-memory unit coverage for the Honua Operations Toolset de-risking spike: the grounding
/// catalog lists the <c>service.publish</c> descriptor; the executor wraps the real
/// <see cref="ILayerPublishingService"/>; the dispatcher consults the policy decision point
/// and short-circuits the executor on a Deny decision (the guardrail seam).
/// </summary>
public sealed class OperationsToolsetTests
{
    private const string TestConnectionId = "11111111-1111-1111-1111-111111111111";

    [UnitTest]
    public async Task Catalog_Lists_ServicePublish_Descriptor_With_ExecutionKind_ApprovalModel_And_Policy()
    {
        var catalog = new OperationCatalog(
            [new ServerOperationDescriptorProvider()],
            TimeProvider.System);

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        var descriptor = snapshot.Operations.Should().ContainSingle(op => op.OperationId == "service.publish").Subject;
        descriptor.ExecutionKind.Should().Be(OperationExecutionKind.Synchronous);
        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        descriptor.Policy.BlastRadiusClass.Should().Be(OperationBlastRadiusClass.ServiceScope);
        descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.CreatesMetadata);
        descriptor.Policy.Determinism.Should().Be(OperationDeterminism.Deterministic);
        descriptor.Policy.SupportsDryRun.Should().BeTrue();

        // GetDescriptorAsync resolves the same descriptor by id.
        var resolved = await catalog.GetDescriptorAsync("service.publish", CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be("honua.server.operations");
    }

    [UnitTest]
    public async Task ValidateAsync_Delegates_To_ValidateTableForPublishAsync()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .ValidateTableForPublishAsync(Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = "public",
                Table = "parcels",
                ServiceName = "default"
            });
        var executor = BuildExecutor(publishing);

        var validation = await executor.ValidateAsync(BuildRequest(), CancellationToken.None);

        validation.IsValid.Should().BeTrue();
        validation.Status.Should().Be("valid");
        await publishing.Received(1).ValidateTableForPublishAsync(
            Arg.Any<string>(),
            Arg.Is<TablePublishValidationRequest>(r => r.Schema == "public" && r.Table == "parcels"),
            Arg.Any<CancellationToken>());
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_With_AllowAll_Calls_PublishLayer_And_Handle_Carries_MetadataRevision()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 7,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default"
            });

        const long expectedRevision = 42;
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Revision = expectedRevision }, "\"etag\"", DateTimeOffset.UtcNow);
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider
            .GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var executor = BuildExecutor(publishing, graphProvider);
        var dispatcher = BuildDispatcher(executor, new AllowAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.MetadataRevision.Should().Be(expectedRevision);
        handle.Result.Should().NotBeNull();
        handle.Result!.Details["layerId"].Should().Be("7");
        handle.Result.Details["metadataRevision"].Should().Be("42");

        // Policy was Allow → publish was actually invoked with the mapped request.
        await publishing.Received(1).PublishLayerAsync(
            Arg.Any<string>(),
            Arg.Is<LayerPublishRequest>(r => r.Schema == "public" && r.Table == "parcels" && r.LayerName == "Parcels"),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_With_Deny_Policy_ShortCircuits_Executor_And_Returns_Denied_Handle()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);
        var dispatcher = BuildDispatcher(executor, new DenyAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // The guardrail seam: the executor's publish path is NEVER reached.
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        handle.Status.Should().Be(OperationHandleStatus.Denied);
        handle.Result.Should().BeNull();
        handle.MetadataRevision.Should().BeNull();
        handle.ApprovalLane.Should().BeNull();
        handle.Reason.Should().Contain("blocked by policy");
    }

    [UnitTest]
    public async Task SubmitAsync_With_RequireApproval_Policy_ShortCircuits_Executor_And_Routes_To_Approval_Lane()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);
        var dispatcher = BuildDispatcher(
            executor,
            new StubPolicyDecisionPoint(new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                Reason = "operator approval required",
                ApprovalLane = "studio-publish-requests"
            }));

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // Guardrail seam: RequireApproval never reaches the executor.
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        handle.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        handle.ApprovalLane.Should().Be("studio-publish-requests");
        handle.Result.Should().BeNull();
    }

    [UnitTest]
    public async Task SubmitAsync_With_DryRunFirst_Policy_ShortCircuits_Executor_And_Returns_DryRunRequired_Handle()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);
        var dispatcher = BuildDispatcher(
            executor,
            new StubPolicyDecisionPoint(new PolicyDecision
            {
                Kind = PolicyDecisionKind.DryRunFirst,
                Reason = "preview required before commit"
            }));

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // Guardrail seam: DryRunFirst never reaches the executor — no side effect.
        await publishing.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        handle.Status.Should().Be(OperationHandleStatus.DryRunRequired);
        handle.ApprovalLane.Should().BeNull();
        handle.Result.Should().BeNull();
        handle.Reason.Should().Contain("preview required");
    }

    [UnitTest]
    public async Task SubmitAsync_Flows_Descriptor_Policy_Metadata_Tier_And_Roles_Into_Decision_Input()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        var executor = BuildExecutor(publishing);

        // Return a non-Allow decision so the dispatcher short-circuits the executor after the
        // decision point has captured its inputs — the assertions below are on what reached the
        // decision point, not on execution.
        var capturing = new CapturingPolicyDecisionPoint(new PolicyDecision
        {
            Kind = PolicyDecisionKind.Deny,
            Reason = "captured"
        });
        var dispatcher = BuildDispatcher(executor, capturing);

        var context = new OperationPolicyContext
        {
            PrincipalId = "alice",
            Tier = "enterprise",
            Roles = ["operator", "publisher"]
        };

        await dispatcher.SubmitAsync(BuildRequest(), context, CancellationToken.None);

        // The descriptor's policy metadata reached the decision point...
        capturing.Descriptor.Should().NotBeNull();
        capturing.Descriptor!.OperationId.Should().Be("service.publish");
        capturing.Descriptor.Policy.BlastRadiusClass.Should().Be(OperationBlastRadiusClass.ServiceScope);
        capturing.Descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.CreatesMetadata);
        capturing.Descriptor.Policy.Determinism.Should().Be(OperationDeterminism.Deterministic);

        // ...alongside the caller's tier and role(s) for a tier/role-aware engine.
        capturing.Context.Should().NotBeNull();
        capturing.Context!.Tier.Should().Be("enterprise");
        capturing.Context.Roles.Should().BeEquivalentTo("operator", "publisher");
    }

    private static ServicePublishExecutor BuildExecutor(
        ILayerPublishingService publishing,
        IMetadataV2GraphProvider? graphProvider = null)
    {
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");

        return new ServicePublishExecutor(
            publishing,
            resolver,
            graphProvider ?? Substitute.For<IMetadataV2GraphProvider>(),
            TimeProvider.System);
    }

    private static OperationDispatcher BuildDispatcher(
        IOperationExecutor executor,
        IOperationPolicyDecisionPoint policy)
    {
        var catalog = new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System);
        return new OperationDispatcher(catalog, [executor], policy, TimeProvider.System);
    }

    private static OperationRequest BuildRequest()
        => new()
        {
            OperationId = "service.publish",
            ConnectionId = TestConnectionId,
            ServiceName = "default",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["schema"] = "public",
                ["table"] = "parcels",
                ["layerName"] = "Parcels"
            }
        };

    /// <summary>
    /// Stub policy decision point that denies every operation, used to prove the dispatcher
    /// short-circuits the executor even though the production default is pass-through Allow.
    /// </summary>
    private sealed class DenyAllPolicyDecisionPoint : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PolicyDecision
            {
                Kind = PolicyDecisionKind.Deny,
                Reason = "blocked by policy (test stub)"
            });
    }

    /// <summary>
    /// Stub decision point that returns a caller-supplied fixed decision, used to prove each
    /// non-Allow outcome (RequireApproval / DryRunFirst) maps to its handle status.
    /// </summary>
    private sealed class StubPolicyDecisionPoint(PolicyDecision decision) : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(decision);
    }

    /// <summary>
    /// Stub decision point that captures the descriptor + context it was evaluated with, used
    /// to prove the descriptor's policy metadata and the caller's tier/role(s) flow into the
    /// decision input.
    /// </summary>
    private sealed class CapturingPolicyDecisionPoint(PolicyDecision decision) : IOperationPolicyDecisionPoint
    {
        public IOperationDescriptor? Descriptor { get; private set; }

        public OperationPolicyContext? Context { get; private set; }

        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            Descriptor = descriptor;
            Context = context;
            return Task.FromResult(decision);
        }
    }
}
