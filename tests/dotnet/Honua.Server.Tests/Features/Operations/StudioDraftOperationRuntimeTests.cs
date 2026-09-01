// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;
using System.Text.Json;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class StudioDraftOperationRuntimeTests
{
    [Fact]
    public void PublicationRequest_UsesStudioApprovalLane()
    {
        var descriptor = StudioDraftOperations.BuildDescriptors()
            .Single(candidate => candidate.OperationId == StudioDraftOperations.CreatePublicationRequest);

        descriptor.ApprovalModel.Should().Be(OperationApprovalModel.StudioPublishRequest);
    }

    [Theory]
    [InlineData(StudioDraftOperations.Validate)]
    [InlineData(StudioDraftOperations.PreviewPlan)]
    [InlineData(StudioDraftOperations.SaveVersion)]
    public void LiveDraftOperations_AreRuntimeDynamic(string operationId)
    {
        var descriptor = StudioDraftOperations.BuildDescriptors()
            .Single(candidate => candidate.OperationId == operationId);

        descriptor.Policy.Determinism.Should().Be(OperationDeterminism.RuntimeDynamic);
    }

    [UnitTest]
    public async Task ValidateExecutor_InvokesLifecycleOnlyAfterCanonicalIdentityIsSupplied()
    {
        var draftId = Guid.NewGuid();
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        var summary = StudioValidationSummary.NotValidated;
        lifecycle.ValidateDraftAsync(draftId, "studio-author", Arg.Any<CancellationToken>())
            .Returns(summary);
        var executor = new StudioDraftValidateExecutor(lifecycle, TimeProvider.System);
        var payload = JsonSerializer.Serialize(
            new StudioDraftActorPayload { DraftId = draftId, ActorId = "studio-author" },
            StudioDraftOperationJsonContext.Default.StudioDraftActorPayload);

        var handle = await executor.SubmitAsync(
            Request(StudioDraftOperations.Validate, payload),
            Context("validate"));

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        handle.OperationInstanceId.Should().Be("opinst-validate");
        handle.Result!.Details[StudioDraftOperations.ResultParameter].Should().NotBeNullOrWhiteSpace();
        await lifecycle.Received(1).ValidateDraftAsync(draftId, "studio-author", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PreviewExecutor_InvokesExactlyOneTypedLifecycleActuator()
    {
        var draftId = Guid.NewGuid();
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.PreviewPlanAsync(draftId, "studio-author", Arg.Any<CancellationToken>())
            .Returns(new StudioPreviewPlan
            {
                DraftId = draftId,
                Family = StudioPackageFamily.Map,
                Synchronous = true,
                RequiresJob = false,
                Validation = StudioValidationSummary.NotValidated,
            });
        var executor = new StudioDraftPreviewPlanExecutor(lifecycle, TimeProvider.System);
        var payload = JsonSerializer.Serialize(
            new StudioDraftActorPayload { DraftId = draftId, ActorId = "studio-author" },
            StudioDraftOperationJsonContext.Default.StudioDraftActorPayload);

        var handle = await executor.SubmitAsync(
            Request(StudioDraftOperations.PreviewPlan, payload),
            Context("preview"));

        handle.Status.Should().Be(OperationHandleStatus.Completed);
        await lifecycle.Received(1).PreviewPlanAsync(draftId, "studio-author", Arg.Any<CancellationToken>());
        await lifecycle.DidNotReceiveWithAnyArgs().ValidateDraftAsync(default, default, default);
    }

    [UnitTest]
    public async Task SaveVersionExecutor_FencesActuationToPayloadGeneration()
    {
        var draftId = Guid.NewGuid();
        const long expectedGeneration = 17;
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.SaveDraftAsVersionAsync(
                draftId,
                "approved change",
                "studio-author",
                expectedGeneration,
                Arg.Any<CancellationToken>())
            .Returns((StudioContentVersion?)null);
        var executor = new StudioSaveVersionExecutor(lifecycle, TimeProvider.System);
        var payload = JsonSerializer.Serialize(
            new StudioSaveVersionPayload
            {
                DraftId = draftId,
                ExpectedGeneration = expectedGeneration,
                ChangeNote = "approved change",
                ActorId = "studio-author",
            },
            StudioDraftOperationJsonContext.Default.StudioSaveVersionPayload);

        var handle = await executor.SubmitAsync(
            Request(StudioDraftOperations.SaveVersion, payload),
            Context("save-version"));

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        await lifecycle.Received(1).SaveDraftAsVersionAsync(
            draftId,
            "approved change",
            "studio-author",
            expectedGeneration,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DeleteAsync_ProjectsResultFromDurableStore_NotInvokerHandle()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = Handle(now, "false");
        var durable = Handle(now, "true") with { Version = 2 };
        var runtime = new StudioDraftMutationRuntime(
            new StubInvoker(stale),
            new StubStore(durable));

        var receipt = await runtime.DeleteAsync(
            Guid.NewGuid(),
            new StudioDraftMutationContext { PrincipalId = "studio-author" });

        receipt.Operation.Should().BeSameAs(durable);
        receipt.Value.Should().BeTrue();
    }

    [UnitTest]
    public async Task SubmitAsync_IdempotentCompletedRetry_ReturnsOriginalWithoutSecondActuation()
    {
        var executor = new CountingExecutor();
        var store = new VolatileOperationInstanceStore();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [executor],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            instanceStore: store,
            auditLog: new VolatileOperationAuditLog());
        var context = new OperationPolicyContext { IdempotencyKey = "studio-create-1" };
        var request = new OperationRequest { OperationId = StudioDraftOperations.Create };

        var first = await dispatcher.SubmitAsync(request, context);
        var retry = await dispatcher.SubmitAsync(request, context);

        retry.OperationInstanceId.Should().Be(first.OperationInstanceId);
        retry.Status.Should().Be(OperationHandleStatus.Completed);
        retry.EvidenceRefs.Should().ContainSingle(reference => reference.StartsWith("retry-audit:", StringComparison.Ordinal));
        executor.SubmitCount.Should().Be(1);
    }

    [UnitTest]
    public async Task DeleteAsync_RequireApproval_PersistsEnvelopeWithoutLifecycleActuation()
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        var executor = new StudioDraftDeleteExecutor(lifecycle, TimeProvider.System);
        var bridge = new DurableApprovalBridge();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [executor],
            new RequireApprovalPolicy(),
            TimeProvider.System,
            approvalBridge: bridge,
            instanceStore: new VolatileOperationInstanceStore(),
            auditLog: new VolatileOperationAuditLog());
        var payload = JsonSerializer.Serialize(
            new StudioDraftDeletePayload { DraftId = Guid.NewGuid() },
            StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload);

        var handle = await dispatcher.SubmitAsync(
            new OperationRequest
            {
                OperationId = StudioDraftOperations.Delete,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [StudioDraftOperations.PayloadParameter] = payload,
                },
            },
            new OperationPolicyContext
            {
                PrincipalId = "studio-operator",
                ScopeGoverned = true,
                RecognizedScopes = ["honua.mcp.delete"],
            });

        handle.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        handle.ProposalId.Should().Be("proposal-studio");
        handle.AuditId.Should().NotBeNull();
        bridge.Request.Should().NotBeNull();
        bridge.Request!.OperationId.Should().Be(StudioDraftOperations.Delete);
        bridge.Context!.ScopeGoverned.Should().BeTrue();
        bridge.Context.RecognizedScopes.Should().Equal("honua.mcp.delete");
        await lifecycle.DidNotReceiveWithAnyArgs().DeleteDraftAsync(default, default);
    }

    [UnitTest]
    public void DeleteApprovalMapper_SealsAndReplaysExactTypedPayloadAndDescriptor()
    {
        var mapper = new StudioDraftApprovalRequestMapper(StudioDraftOperations.Delete);
        var descriptor = StudioDraftOperations.BuildDescriptors()
            .Single(candidate => candidate.OperationId == StudioDraftOperations.Delete);
        var payload = JsonSerializer.Serialize(
            new StudioDraftDeletePayload { DraftId = Guid.Parse("11111111-1111-1111-1111-111111111111") },
            StudioDraftOperationJsonContext.Default.StudioDraftDeletePayload);
        var request = new OperationRequest
        {
            OperationId = StudioDraftOperations.Delete,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [StudioDraftOperations.PayloadParameter] = payload,
            },
        };

        var mapped = mapper.Map(
            descriptor,
            request,
            new OperationPolicyContext
            {
                OperationInstanceId = "opinst-delete",
                CorrelationId = "corr-delete",
                TenantId = "tenant-a",
                SchemaName = "tenant_schema",
            },
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });

        mapped.OperationId.Should().Be(StudioDraftOperations.Delete);
        mapped.Kind.Should().Be(OperationClass.StudioDraftMutation);
        var replay = mapper.MapReplay(mapped);
        replay.Request.Parameters[StudioDraftOperations.PayloadParameter].Should().NotBeNull();
        replay.TenantId.Should().Be("tenant-a");
        replay.SchemaName.Should().Be("tenant_schema");
    }

    [Fact]
    public async Task RollbackProposalPlans_AreHighRisk()
    {
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        var executor = new StudioRollbackExecutor(lifecycle, TimeProvider.System);
        var descriptor = StudioDraftOperations.BuildDescriptors()
            .Single(candidate => candidate.OperationId == StudioDraftOperations.Rollback);
        var payload = JsonSerializer.Serialize(new StudioRollbackPayload
        {
            ItemId = Guid.NewGuid(),
            TargetVersionId = Guid.NewGuid(),
            Target = StudioRollbackPointer.Current,
        }, StudioDraftOperationJsonContext.Default.StudioRollbackPayload);
        var request = Request(StudioDraftOperations.Rollback, payload);

        var validation = await executor.ValidateAsync(request);
        var mapped = new StudioDraftApprovalRequestMapper(StudioDraftOperations.Rollback).Map(
            descriptor,
            request,
            new OperationPolicyContext(),
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });

        validation.ApprovalPlan!.RiskLevel.Should().Be(ProposalRiskLevel.High);
        mapped.Plan!.RiskLevel.Should().Be(ProposalRiskLevel.High);
    }

    [UnitTest]
    public void SaveVersionApprovalMapper_PreservesExpectedGenerationForReplay()
    {
        var mapper = new StudioDraftApprovalRequestMapper(StudioDraftOperations.SaveVersion);
        var descriptor = StudioDraftOperations.BuildDescriptors()
            .Single(candidate => candidate.OperationId == StudioDraftOperations.SaveVersion);
        var payload = JsonSerializer.Serialize(
            new StudioSaveVersionPayload { DraftId = Guid.NewGuid(), ExpectedGeneration = 23 },
            StudioDraftOperationJsonContext.Default.StudioSaveVersionPayload);
        var request = Request(StudioDraftOperations.SaveVersion, payload);

        var mapped = mapper.Map(
            descriptor,
            request,
            new OperationPolicyContext { TenantId = "tenant-a", SchemaName = "tenant_schema" },
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
        var replay = mapper.MapReplay(mapped);
        var replayPayload = JsonSerializer.Deserialize(
            replay.Request.Parameters[StudioDraftOperations.PayloadParameter]!,
            StudioDraftOperationJsonContext.Default.StudioSaveVersionPayload);

        replayPayload!.ExpectedGeneration.Should().Be(23);
        replay.TenantId.Should().Be("tenant-a");
        replay.SchemaName.Should().Be("tenant_schema");
    }

    private static OperationHandle Handle(DateTimeOffset now, string payload) => new()
    {
        OperationInstanceId = "opinst-studio",
        OperationId = StudioDraftOperations.Delete,
        CorrelationId = "corr-studio",
        AuditId = "audit-studio",
        Status = OperationHandleStatus.Completed,
        CreatedAt = now,
        UpdatedAt = now,
        Result = new OperationResultSummary
        {
            Summary = "deleted",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StudioDraftOperations.ResultParameter] = payload,
            },
        },
    };

    private static OperationRequest Request(string operationId, string payload) => new()
    {
        OperationId = operationId,
        Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StudioDraftOperations.PayloadParameter] = payload,
        },
    };

    private static OperationPolicyContext Context(string suffix) => new()
    {
        OperationInstanceId = $"opinst-{suffix}",
        CorrelationId = $"corr-{suffix}",
    };

    private sealed class StubInvoker(OperationHandle handle) : IOperationInvoker
    {
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(handle);
    }

    private sealed class StubStore(OperationHandle handle) : IOperationInstanceStore
    {
        public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationHandle?>(handle);
    }

    private sealed class CountingExecutor : IOperationExecutor
    {
        public string OperationId => StudioDraftOperations.Create;
        public int SubmitCount { get; private set; }

        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new OperationHandle
            {
                OperationInstanceId = context.OperationInstanceId!,
                OperationId = OperationId,
                CorrelationId = context.CorrelationId!,
                Status = OperationHandleStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RequireApprovalPolicy : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(new PolicyDecision
            {
                Kind = PolicyDecisionKind.RequireApproval,
                ApprovalLane = "studio-operator",
            });
    }

    private sealed class DurableApprovalBridge : IOperationApprovalBridge
    {
        public OperationRequest? Request { get; private set; }
        public OperationPolicyContext? Context { get; private set; }

        public Task<OperationApprovalBridgeResult> CreateProposalAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            PolicyDecision decision,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            Context = context;
            return Task.FromResult(new OperationApprovalBridgeResult
            {
                IsDurable = true,
                ProposalId = "proposal-studio",
                AuditId = "audit-proposal-studio",
            });
        }
    }
}
