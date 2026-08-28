// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Security ratchets for the canonical operation identity and approval envelope.
/// </summary>
public sealed class OperationEnvelopeArchitectureTests
{
    [ArchitectureTest]
    public void LegacyHandleId_IsGetterOnlyAliasAndCannotReceiveProposalId()
    {
        var property = typeof(OperationHandle).GetProperty(nameof(OperationHandle.HandleId));
        Assert.NotNull(property);
        Assert.False(property!.CanWrite);

        var now = DateTimeOffset.UtcNow;
        var handle = new OperationHandle
        {
            OperationInstanceId = "opinst-architecture",
            OperationId = "admin.service.publish",
            ProposalId = "proposal-architecture",
            CorrelationId = "corr-architecture",
            Status = OperationHandleStatus.RequiresApproval,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Assert.Equal(handle.OperationInstanceId, handle.HandleId);
        Assert.NotEqual(handle.ProposalId, handle.HandleId);
    }

    [ArchitectureTest]
    public async Task RequireApproval_WhenProposalAuditSinkIsUnavailable_FailsClosedBeforeActuator()
    {
        var descriptor = Descriptor();
        var executor = new CountingExecutor(descriptor.OperationId);
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new DescriptorProvider(descriptor)], TimeProvider.System),
            [executor],
            new RequireApprovalPolicy(),
            TimeProvider.System,
            new UnavailableApprovalBridge());

        var handle = await dispatcher.SubmitAsync(
            new OperationRequest { OperationId = descriptor.OperationId },
            new OperationPolicyContext());

        Assert.Equal(OperationHandleStatus.Failed, handle.Status);
        Assert.Equal(0, executor.SubmitCount);
        Assert.Null(handle.ProposalId);
        Assert.Null(handle.AuditId);
        Assert.NotEqual(handle.OperationId, handle.OperationInstanceId);
        Assert.Contains(
            "proposal or audit sink is unavailable",
            handle.Reason ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static OperationDescriptor Descriptor() => new()
    {
        OperationId = "admin.service.publish",
        ProviderId = "architecture-test",
        Title = "Publish service",
        Description = "Architecture test descriptor.",
        Category = "admin",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.CreatesMetadata,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = true,
        },
    };

    private sealed class DescriptorProvider(OperationDescriptor descriptor) : IOperationDescriptorProvider
    {
        public string ProviderId => descriptor.ProviderId;

        public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IOperationDescriptor>>([descriptor]);
    }

    private sealed class RequireApprovalPolicy : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                ApprovalLane = "operator-gate",
            });
    }

    private sealed class CountingExecutor(string operationId) : IOperationExecutor
    {
        public string OperationId => operationId;

        public int SubmitCount { get; private set; }

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            throw new InvalidOperationException("The fail-closed architecture guard allowed an actuator call.");
        }

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class UnavailableApprovalBridge : IOperationApprovalBridge
    {
        public Task<OperationApprovalBridgeResult> CreateProposalAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            PolicyDecision decision,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationApprovalBridgeResult
            {
                IsDurable = false,
                Reason = "The durable proposal or audit sink is unavailable.",
            });
    }
}
