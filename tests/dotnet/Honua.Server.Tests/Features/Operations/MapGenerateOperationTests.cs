// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.MapGeneration;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// In-memory unit coverage for the <c>map.generate</c> strangler proof: the grounding catalog
/// lists the Studio generator descriptor with Studio/Generation + StudioPublishRequest +
/// AiAssisted metadata; the executor WRAPS the real <see cref="IMapGenerationService"/> and
/// frames the produced draft as a Completed handle whose <see cref="OperationHandle.Result"/>
/// carries the draft and whose <see cref="OperationHandle.ApprovalLane"/> is the Studio
/// publish-request lane; and the dispatcher consults the policy decision point, short-circuiting
/// the executor on a Deny decision (the guardrail seam).
/// </summary>
public sealed class MapGenerateOperationTests
{
    [UnitTest]
    public async Task Catalog_Lists_MapGenerate_With_StudioGeneration_StudioPublishRequest_And_AiAssisted()
    {
        var catalog = new OperationCatalog(
            [new ServerOperationDescriptorProvider()],
            TimeProvider.System);

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        var descriptor = snapshot.Operations.Should().ContainSingle(op => op.OperationId == "map.generate").Subject;
        descriptor.Category.Should().Be("Studio/Generation");
        descriptor.ExecutionKind.Should().Be(OperationExecutionKind.Synchronous);
        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.StudioPublishRequest);
        descriptor.Policy.BlastRadiusClass.Should().Be(OperationBlastRadiusClass.ResourceScope);
        descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.CreatesMetadata);
        descriptor.Policy.Determinism.Should().Be(OperationDeterminism.AiAssisted);
        descriptor.Policy.SupportsDryRun.Should().BeFalse();

        var resolved = await catalog.GetDescriptorAsync("map.generate", CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be("honua.server.operations");
    }

    [UnitTest]
    public async Task SubmitAsync_With_AllowAll_Wraps_Generator_And_Handle_Carries_Draft_And_StudioPublishRequest_Lane()
    {
        var generation = Substitute.For<IMapGenerationService>();
        generation
            .GenerateAsync(Arg.Any<MapGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MapGenerationResult
            {
                Status = "generated",
                Provider = "bedrock",
                Model = "test-model",
                Package = new MapPackage
                {
                    MapPackageId = "map-pkg-123",
                    Format = "honua_map_package.v1",
                    Status = PackageStatus.Draft,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

        var executor = new MapGenerateExecutor(generation, TimeProvider.System);
        var dispatcher = BuildDispatcher(executor, new AllowAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // The generator produced a draft that enters the Studio publish-request lane: the handle
        // models generate → draft → lifecycle, not a flat sync result.
        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.ApprovalLane.Should().Be("studio-publish-request");
        handle.Result.Should().NotBeNull();
        handle.Result!.Details["mapPackageId"].Should().Be("map-pkg-123");
        handle.Result.Details["packageStatus"].Should().Be("Draft");
        handle.Result.Details["status"].Should().Be("generated");

        // Policy was Allow → the wrapped generator was actually invoked with the mapped prompt.
        await generation.Received(1).GenerateAsync(
            Arg.Is<MapGenerationRequest>(r => r.Prompt == "a map of parcels"),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_When_Generator_Returns_NonGenerated_Status_Produces_No_Lane()
    {
        var generation = Substitute.For<IMapGenerationService>();
        generation
            .GenerateAsync(Arg.Any<MapGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MapGenerationResult
            {
                Status = "clarification",
                Rationale = "Which basemap should the map use?"
            });

        var executor = new MapGenerateExecutor(generation, TimeProvider.System);
        var dispatcher = BuildDispatcher(executor, new AllowAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.ApprovalLane.Should().BeNull();
        handle.Reason.Should().Be("Which basemap should the map use?");
        handle.Result!.Details.Should().NotContainKey("mapPackageId");
    }

    [UnitTest]
    public async Task SubmitAsync_With_Deny_Policy_ShortCircuits_Executor_And_Generator_Is_Never_Called()
    {
        var generation = Substitute.For<IMapGenerationService>();
        var executor = new MapGenerateExecutor(generation, TimeProvider.System);
        var dispatcher = BuildDispatcher(executor, new DenyAllPolicyDecisionPoint());

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        // The guardrail seam: the wrapped generator is NEVER reached.
        await generation.DidNotReceive().GenerateAsync(
            Arg.Any<MapGenerationRequest>(), Arg.Any<CancellationToken>());
        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.ApprovalLane.Should().BeNull();
        handle.Reason.Should().Contain("blocked by policy");
    }

    [UnitTest]
    public async Task ValidateAsync_Rejects_Empty_Prompt_Without_Calling_Generator()
    {
        var generation = Substitute.For<IMapGenerationService>();
        var executor = new MapGenerateExecutor(generation, TimeProvider.System);

        var validation = await executor.ValidateAsync(
            new OperationRequest
            {
                OperationId = "map.generate",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["prompt"] = "  " }
            },
            CancellationToken.None);

        validation.IsValid.Should().BeFalse();
        validation.Status.Should().Be("invalid");
        await generation.DidNotReceive().GenerateAsync(
            Arg.Any<MapGenerationRequest>(), Arg.Any<CancellationToken>());
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
            OperationId = "map.generate",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["prompt"] = "a map of parcels"
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
}
