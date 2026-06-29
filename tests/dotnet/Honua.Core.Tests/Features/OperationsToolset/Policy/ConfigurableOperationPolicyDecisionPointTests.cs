// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.OperationsToolset.Policy;

/// <summary>
/// Unit coverage for <see cref="ConfigurableOperationPolicyDecisionPoint"/>: the configurable
/// guardrail policy decision point. Proves the default-permissive behavior, default-decision
/// fallback, per-operation / tier / role rule matching, first-match-wins ordering, dry-run
/// honouring, and an end-to-end <see cref="OperationDispatcher"/> integration showing a
/// require-approval rule routes to the approval lane WITHOUT touching the executor.
/// </summary>
public sealed class ConfigurableOperationPolicyDecisionPointTests
{
    private const string PublishOperationId = "service.publish";

    [UnitTest]
    public async Task EvaluateAsync_When_Disabled_Returns_Allow()
    {
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = false,
            DefaultDecision = PolicyDecisionKind.Deny,
            Rules = [new OperationPolicyRule { OperationId = "*", Decision = PolicyDecisionKind.Deny }]
        });

        var decision = await Evaluate(pdp);

        decision.Kind.Should().Be(PolicyDecisionKind.Allow);
    }

    [UnitTest]
    public async Task EvaluateAsync_When_Enabled_With_No_Rules_Uses_DefaultDecision_Deny()
    {
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = true,
            DefaultDecision = PolicyDecisionKind.Deny,
            DefaultReason = "deny by default"
        });

        var decision = await Evaluate(pdp);

        decision.Kind.Should().Be(PolicyDecisionKind.Deny);
        decision.Reason.Should().Be("deny by default");
    }

    [UnitTest]
    public async Task EvaluateAsync_With_PerOperation_Deny_Rule_Denies_That_Operation()
    {
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = true,
            DefaultDecision = PolicyDecisionKind.Allow,
            Rules =
            [
                new OperationPolicyRule
                {
                    OperationId = PublishOperationId,
                    Decision = PolicyDecisionKind.Deny,
                    Reason = "publishing is locked"
                }
            ]
        });

        var decision = await Evaluate(pdp);

        decision.Kind.Should().Be(PolicyDecisionKind.Deny);
        decision.Reason.Should().Be("publishing is locked");
    }

    [UnitTest]
    public async Task EvaluateAsync_With_RequireApproval_Rule_Routes_ApprovalLane()
    {
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = true,
            Rules =
            [
                new OperationPolicyRule
                {
                    OperationId = PublishOperationId,
                    Decision = PolicyDecisionKind.RequireApproval,
                    Reason = "operator approval required",
                    ApprovalLane = "studio-publish-requests"
                }
            ]
        });

        var decision = await Evaluate(pdp);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
        decision.ApprovalLane.Should().Be("studio-publish-requests");
        decision.Reason.Should().Be("operator approval required");
    }

    [UnitTest]
    public async Task EvaluateAsync_Matches_On_Tier_And_Role()
    {
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = true,
            DefaultDecision = PolicyDecisionKind.Allow,
            Rules =
            [
                new OperationPolicyRule
                {
                    OperationId = "*",
                    Tier = "enterprise",
                    Role = "viewer",
                    Decision = PolicyDecisionKind.Deny,
                    Reason = "viewers may not run operations"
                }
            ]
        });

        // Matches: enterprise tier + viewer role.
        var denied = await Evaluate(pdp, new OperationPolicyContext
        {
            Tier = "Enterprise", // case-insensitive
            Roles = ["operator", "viewer"]
        });
        denied.Kind.Should().Be(PolicyDecisionKind.Deny);

        // Does not match (wrong tier) → falls back to default Allow.
        var allowedWrongTier = await Evaluate(pdp, new OperationPolicyContext
        {
            Tier = "pro",
            Roles = ["viewer"]
        });
        allowedWrongTier.Kind.Should().Be(PolicyDecisionKind.Allow);

        // Does not match (missing role) → falls back to default Allow.
        var allowedNoRole = await Evaluate(pdp, new OperationPolicyContext
        {
            Tier = "enterprise",
            Roles = ["operator"]
        });
        allowedNoRole.Kind.Should().Be(PolicyDecisionKind.Allow);
    }

    [UnitTest]
    public async Task EvaluateAsync_Is_FirstMatchWins()
    {
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = true,
            Rules =
            [
                new OperationPolicyRule
                {
                    OperationId = PublishOperationId,
                    Decision = PolicyDecisionKind.RequireApproval,
                    Reason = "first rule wins"
                },
                new OperationPolicyRule
                {
                    OperationId = PublishOperationId,
                    Decision = PolicyDecisionKind.Deny,
                    Reason = "second rule (should be unreached)"
                }
            ]
        });

        var decision = await Evaluate(pdp);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
        decision.Reason.Should().Be("first rule wins");
    }

    [UnitTest]
    public async Task EvaluateAsync_DryRunFirst_With_DryRun_Request_Allows()
    {
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = true,
            Rules =
            [
                new OperationPolicyRule
                {
                    OperationId = PublishOperationId,
                    Decision = PolicyDecisionKind.DryRunFirst,
                    Reason = "preview required"
                }
            ]
        });

        // Without a dry-run the policy demands the preview first.
        var demandsPreview = await Evaluate(pdp, request: BuildRequest(dryRun: false));
        demandsPreview.Kind.Should().Be(PolicyDecisionKind.DryRunFirst);

        // With the caller already requesting a dry-run, the precondition is met → Allow.
        var previewSatisfied = await Evaluate(pdp, request: BuildRequest(dryRun: true));
        previewSatisfied.Kind.Should().Be(PolicyDecisionKind.Allow);
    }

    [UnitTest]
    public async Task Dispatcher_With_RequireApproval_Rule_ShortCircuits_Executor()
    {
        var executor = new RecordingExecutor();
        var pdp = BuildPdp(new OperationPolicyOptions
        {
            Enabled = true,
            Rules =
            [
                new OperationPolicyRule
                {
                    OperationId = PublishOperationId,
                    Decision = PolicyDecisionKind.RequireApproval,
                    Reason = "operator approval required",
                    ApprovalLane = "studio-publish-requests"
                }
            ]
        });

        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new StubDescriptorProvider()], TimeProvider.System),
            [executor],
            pdp,
            TimeProvider.System);

        var handle = await dispatcher.SubmitAsync(BuildRequest(), new OperationPolicyContext(), CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        handle.ApprovalLane.Should().Be("studio-publish-requests");
        handle.Result.Should().BeNull();

        // Guardrail seam: the executor was never invoked.
        executor.SubmitCount.Should().Be(0);
    }

    private static ConfigurableOperationPolicyDecisionPoint BuildPdp(OperationPolicyOptions options)
        => new(Options.Create(options));

    private static Task<PolicyDecision> Evaluate(
        ConfigurableOperationPolicyDecisionPoint pdp,
        OperationPolicyContext? context = null,
        OperationRequest? request = null)
        => pdp.EvaluateAsync(
            BuildDescriptor(),
            request ?? BuildRequest(),
            context ?? new OperationPolicyContext(),
            CancellationToken.None);

    private static OperationRequest BuildRequest(bool dryRun = false)
        => new() { OperationId = PublishOperationId, DryRun = dryRun };

    private static OperationDescriptor BuildDescriptor()
        => new()
        {
            OperationId = PublishOperationId,
            ProviderId = "test",
            Title = "Publish",
            Description = "Publish a layer",
            Category = "Publishing",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = OperationApprovalModel.OperatorGate,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
                SideEffectClass = OperationSideEffectClass.CreatesMetadata,
                Determinism = OperationDeterminism.Deterministic,
                SupportsDryRun = true
            }
        };

    private sealed class StubDescriptorProvider : IOperationDescriptorProvider
    {
        public string ProviderId => "test";

        public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IOperationDescriptor>>([BuildDescriptor()]);
    }

    private sealed class RecordingExecutor : IOperationExecutor
    {
        public int SubmitCount { get; private set; }

        public string OperationId => PublishOperationId;

        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(new OperationHandle
            {
                OperationId = OperationId,
                HandleId = "handle",
                Status = OperationHandleStatus.Completed
            });
        }

        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationStatus
            {
                OperationId = OperationId,
                HandleId = handle.HandleId,
                Status = handle.Status
            });
    }
}
