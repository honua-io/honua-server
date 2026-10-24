// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;

namespace Honua.Core.Tests.Features.OperationsToolset;

public sealed class RootProvisioningOperationLineageTests
{
    private const string RootA = "urn:honua:provisioning:0123456789abcdef0123456789abcdef";
    private const string RootB = "urn:honua:provisioning:ffffffffffffffffffffffffffffffff";

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Acceptance_StampsTheVerifiedHandoffOnTheHandle()
    {
        var store = new VolatileOperationInstanceStore();
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);

        var handle = await factory.CreateAcceptedAsync("lineage.echo", Context(RootA));

        Assert.Equal(OperationHandleStatus.Accepted, handle.Status);
        Assert.Equal(RootA, handle.RootProvisioningOperationId);
        Assert.Equal(RootA, (await store.GetAsync(handle.OperationInstanceId))!.RootProvisioningOperationId);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(handle, CamelCase));
        Assert.Equal(RootA, json.RootElement.GetProperty("rootProvisioningOperationId").GetString());
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Acceptance_AbsentRoot_StaysNullAndIsOmitted()
    {
        var factory = new OperationEnvelopeFactory(
            new VolatileOperationInstanceStore(),
            new VolatileOperationAuditLog(),
            TimeProvider.System);

        var handle = await factory.CreateAcceptedAsync("lineage.echo", Context(null));

        Assert.Null(handle.RootProvisioningOperationId);
        Assert.DoesNotContain("rootProvisioningOperationId", JsonSerializer.Serialize(handle, CamelCase), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Acceptance_ReplayWithADifferentRoot_ConflictsWithoutOverwriting()
    {
        var store = new VolatileOperationInstanceStore();
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("lineage.echo", Context(RootA));

        var retry = await factory.CreateAcceptedAsync("lineage.echo", Context(RootB));

        Assert.Equal(OperationHandleStatus.Failed, retry.Status);
        Assert.Equal("409", retry.Result!.Details["statusCode"]);
        Assert.Equal(RootA, retry.RootProvisioningOperationId);
        var stored = await store.GetAsync(first.OperationInstanceId);
        Assert.Equal(OperationHandleStatus.Accepted, stored!.Status);
        Assert.Equal(RootA, stored.RootProvisioningOperationId);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Acceptance_ReplayThatOmitsTheRoot_KeepsTheRecordedRoot()
    {
        var store = new VolatileOperationInstanceStore();
        var factory = new OperationEnvelopeFactory(store, new VolatileOperationAuditLog(), TimeProvider.System);
        var first = await factory.CreateAcceptedAsync("lineage.echo", Context(RootA));
        await store.SetAsync(first with { Status = OperationHandleStatus.Queued });

        var retry = await factory.CreateAcceptedAsync("lineage.echo", Context(null));

        Assert.Equal(OperationHandleStatus.Queued, retry.Status);
        Assert.Equal(RootA, retry.RootProvisioningOperationId);
        Assert.Equal(RootA, (await store.GetAsync(first.OperationInstanceId))!.RootProvisioningOperationId);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void PreferCanonical_PrefersTheCanonicalFieldAndRejectsAMismatch()
    {
        Assert.Equal(RootA, RootProvisioningOperationIds.PreferCanonical(RootA, null));
        Assert.Equal(RootA, RootProvisioningOperationIds.PreferCanonical(null, RootA));
        Assert.Null(RootProvisioningOperationIds.PreferCanonical(null, null));
        Assert.Throws<ArgumentException>(() => RootProvisioningOperationIds.PreferCanonical(RootA, RootB));
        Assert.Throws<ArgumentException>(() => RootProvisioningOperationIds.Normalize("URN:HONUA:PROVISIONING:" + new string('A', 32)));
        Assert.Throws<ArgumentException>(() =>
            RootProvisioningOperationIds.FromParameters(
                new Dictionary<string, string?> { [RootProvisioningOperationIds.FieldName] = "not-a-root" }));
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Dispatcher_ParameterFallback_StampsTheCompletedHandle()
    {
        var store = new VolatileOperationInstanceStore();
        var executor = new EchoExecutor();
        var dispatcher = Dispatcher(store, executor);

        var handle = await dispatcher.SubmitAsync(
            new OperationRequest
            {
                OperationId = executor.OperationId,
                Parameters = new Dictionary<string, string?> { [RootProvisioningOperationIds.FieldName] = RootA },
            },
            new OperationPolicyContext());

        Assert.Equal(OperationHandleStatus.Completed, handle.Status);
        Assert.Equal(RootA, handle.RootProvisioningOperationId);
        Assert.Equal(1, executor.SubmitCount);
        Assert.Equal(RootA, (await store.GetAsync(handle.OperationInstanceId))!.RootProvisioningOperationId);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Dispatcher_CanonicalField_WinsWhenTheParameterAgrees()
    {
        var store = new VolatileOperationInstanceStore();
        var executor = new EchoExecutor();
        var dispatcher = Dispatcher(store, executor);

        var handle = await dispatcher.SubmitAsync(
            new OperationRequest
            {
                OperationId = executor.OperationId,
                Parameters = new Dictionary<string, string?> { [RootProvisioningOperationIds.FieldName] = RootA },
                GatewayRequest = new OperationGatewayRequest
                {
                    Kind = OperationClass.AdminConfigChange,
                    RootProvisioningOperationId = RootA,
                },
            },
            new OperationPolicyContext { RootProvisioningOperationId = RootA });

        Assert.Equal(RootA, handle.RootProvisioningOperationId);
        Assert.Equal(1, executor.SubmitCount);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Dispatcher_MismatchedCanonicalAndParameter_IsRejectedBeforeActuation()
    {
        var store = new VolatileOperationInstanceStore();
        var executor = new EchoExecutor();
        var dispatcher = Dispatcher(store, executor);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.SubmitAsync(
            new OperationRequest
            {
                OperationId = executor.OperationId,
                Parameters = new Dictionary<string, string?> { [RootProvisioningOperationIds.FieldName] = RootB },
            },
            new OperationPolicyContext { RootProvisioningOperationId = RootA }));

        Assert.Contains("provisioning root", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, executor.SubmitCount);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task Dispatcher_ActuatorSubstitutedRoot_FailsWithoutAdoptingIt()
    {
        var store = new VolatileOperationInstanceStore();
        var executor = new EchoExecutor(RootB);
        var dispatcher = Dispatcher(store, executor);

        var handle = await dispatcher.SubmitAsync(
            new OperationRequest
            {
                OperationId = executor.OperationId,
                Parameters = new Dictionary<string, string?> { [RootProvisioningOperationIds.FieldName] = RootA },
            },
            new OperationPolicyContext());

        Assert.Equal(OperationHandleStatus.Failed, handle.Status);
        Assert.Equal(RootA, handle.RootProvisioningOperationId);
        Assert.Contains("substituted", handle.Reason, StringComparison.Ordinal);
        var stored = await store.GetAsync(handle.OperationInstanceId);
        Assert.Equal(RootA, stored!.RootProvisioningOperationId);
        Assert.NotEqual(RootB, stored.RootProvisioningOperationId);
    }

    private static OperationDispatcher Dispatcher(VolatileOperationInstanceStore store, EchoExecutor executor)
        => new(
            new OperationCatalog([new DescriptorProvider(Descriptor(executor.OperationId))], TimeProvider.System),
            [executor],
            new AllowPolicy(),
            TimeProvider.System,
            instanceStore: store);

    private static OperationPolicyContext Context(string? root) => new()
    {
        PrincipalId = "operator-a",
        TenantId = "tenant-a",
        IdempotencyKey = "lineage-key",
        IdempotencyRequestHash = new string('a', 64),
        RootProvisioningOperationId = root,
    };

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static OperationDescriptor Descriptor(string operationId) => new()
    {
        OperationId = operationId,
        ProviderId = "lineage-test",
        Title = "Lineage echo",
        Description = "Stamps a verified provisioning root.",
        Category = "admin",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.None,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ResourceScope,
            SideEffectClass = OperationSideEffectClass.ReadOnly,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = true,
        },
    };

    private sealed class DescriptorProvider(OperationDescriptor descriptor) : IOperationDescriptorProvider
    {
        public string ProviderId => descriptor.ProviderId;

        public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IOperationDescriptor>>([descriptor]);
    }

    private sealed class AllowPolicy : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PolicyDecision.Allowed);
    }

    private sealed class EchoExecutor(string? substituteRoot = null) : Honua.Core.Features.Operations.Abstractions.IOperationExecutor
    {
        public int SubmitCount { get; private set; }

        public string OperationId => "lineage.echo";

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
                OperationInstanceId = context.OperationInstanceId ?? "missing",
                OperationId = OperationId,
                CorrelationId = context.CorrelationId ?? "missing",
                Status = OperationHandleStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
                RootProvisioningOperationId = substituteRoot,
            });
        }

        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
