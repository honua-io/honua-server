// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using IOperationProposalStore = Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore;
using IOperationGateway = Honua.Core.Features.ControlPlane.Abstractions.IOperationGateway;
using OperationGatewayRequest = Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest;

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
        Assert.NotNull(handle.AuditId);
        Assert.NotEqual(handle.OperationId, handle.OperationInstanceId);
        Assert.Contains(
            "proposal or audit sink is unavailable",
            handle.Reason ?? string.Empty,
            StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task Allow_PersistsPolicyEvidenceBeforeExactlyOneActuatorInvocation()
    {
        var descriptor = Descriptor();
        var store = new RecordingStore();
        var executor = new EvidenceCheckingExecutor(descriptor.OperationId, store);
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new DescriptorProvider(descriptor)], TimeProvider.System),
            [executor],
            new AllowPolicy(),
            TimeProvider.System,
            instanceStore: store,
            auditLog: new DurableAuditLog());

        var handle = await dispatcher.SubmitAsync(
            new OperationRequest { OperationId = descriptor.OperationId },
            new OperationPolicyContext { AuthorizationOutcome = "allowed" });

        Assert.Equal(OperationHandleStatus.Completed, handle.Status);
        Assert.Equal(1, executor.SubmitCount);
        Assert.True(executor.SawDurablePolicyEvidence);
    }

    [ArchitectureTest]
    public async Task PolicyEvidenceInfrastructureFailure_ReturnsFailedEnvelopeWithoutActuation()
    {
        var descriptor = Descriptor();
        var executor = new CountingExecutor(descriptor.OperationId);
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new DescriptorProvider(descriptor)], TimeProvider.System),
            [executor],
            new AllowPolicy(),
            TimeProvider.System,
            instanceStore: new FailingPolicyEvidenceStore(),
            auditLog: new DurableAuditLog());

        var handle = await dispatcher.SubmitAsync(
            new OperationRequest { OperationId = descriptor.OperationId },
            new OperationPolicyContext { AuthorizationOutcome = "allowed" });

        Assert.Equal(OperationHandleStatus.Failed, handle.Status);
        Assert.Equal(0, executor.SubmitCount);
        Assert.NotNull(handle.AuditId);
        Assert.Contains("policy evaluation or evidence persistence failed", handle.Reason, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_WithoutDurableOperationStore_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore, VolatileOperationInstanceStore>();
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("durable IOperationInstanceStore", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_WithoutDurableAuditSink_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore>(new RecordingStore());
        services.AddSingleton<IOperationProposalStore>(new StubProposalStore());
        services.AddSingleton<IAuditLog>(NullAuditLog.Instance);
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("durable IAuditLog", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_WithoutDurableProposalStore_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore>(new RecordingStore());
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("durable IOperationProposalStore", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_WithAllowAllPolicy_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore>(new RecordingStore());
        services.AddSingleton<IOperationProposalStore>(new StubProposalStore());
        services.AddSingleton<IAuditLog>(new DurableAuditLog());
        services.AddSingleton<IOperationPolicyDecisionPoint>(new AllowAllPolicyDecisionPoint());
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("fail-closed policy decision point", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_WithDisabledCanonicalPolicy_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore>(new RecordingStore());
        services.AddSingleton<IOperationProposalStore>(new StubProposalStore());
        services.AddSingleton<IAuditLog>(new DurableAuditLog());
        services.AddSingleton<IOptions<OperationPolicyOptions>>(
            Options.Create(new OperationPolicyOptions { Enabled = false }));
        services.AddSingleton<IOperationPolicyDecisionPoint>(provider =>
            new CanonicalOperationPolicyDecisionPoint(
                provider.GetRequiredService<IOptions<OperationPolicyOptions>>(),
                new BlockingGuardrailLadder()));
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("enabled policy with a fail-closed default", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_WithPermissiveCanonicalPolicyDefault_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore>(new RecordingStore());
        services.AddSingleton<IOperationProposalStore>(new StubProposalStore());
        services.AddSingleton<IAuditLog>(new DurableAuditLog());
        services.AddSingleton<IOptions<OperationPolicyOptions>>(
            Options.Create(new OperationPolicyOptions
            {
                Enabled = true,
                DefaultDecision = PolicyDecisionKind.Allow,
            }));
        services.AddSingleton<IOperationPolicyDecisionPoint>(provider =>
            new CanonicalOperationPolicyDecisionPoint(
                provider.GetRequiredService<IOptions<OperationPolicyOptions>>(),
                new BlockingGuardrailLadder()));
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("fail-closed default decision", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void LoadSoakProductionTopology_ProvisionsFailClosedOperationPolicy()
    {
        var workflow = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            ".github/workflows/load-soak-nightly.yml"));

        Assert.Contains("ASPNETCORE_ENVIRONMENT: Production", workflow, StringComparison.Ordinal);
        Assert.Contains("Operations__Policy__Enabled: \"true\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Operations__Policy__DefaultDecision: Deny", workflow, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_WithoutExactlyOneActuator_FailsClosed()
    {
        var descriptor = Descriptor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore>(new RecordingStore());
        services.AddSingleton<IOperationProposalStore>(new StubProposalStore());
        services.AddSingleton<IAuditLog>(new DurableAuditLog());
        services.AddSingleton<IOperationPolicyDecisionPoint>(new AllowPolicy());
        services.AddSingleton<IOperationApprovalRequestMapper>(new TestMapper(descriptor.OperationId));
        services.AddSingleton<IOperationCatalog>(
            new OperationCatalog([new DescriptorProvider(descriptor)], TimeProvider.System));
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("exactly one actuator", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public async Task ProductionStartup_ApprovalDescriptorWithoutMapper_FailsClosed()
    {
        var descriptor = Descriptor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOperationInstanceStore>(new RecordingStore());
        services.AddSingleton<IOperationProposalStore>(new StubProposalStore());
        services.AddSingleton<IAuditLog>(new DurableAuditLog());
        services.AddSingleton<IOperationPolicyDecisionPoint>(new AllowPolicy());
        services.AddSingleton<IOperationCatalog>(
            new OperationCatalog([new DescriptorProvider(descriptor)], TimeProvider.System));
        services.AddSingleton<IOperationExecutor>(new CountingExecutor(descriptor.OperationId));
        await using var provider = services.BuildServiceProvider();
        var validator = new OperationRuntimeStartupValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("safe approval mapper", exception.Message, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void GatewayApprovalApi_RequiresOperationInstanceIdentity()
    {
        var method = typeof(IOperationGateway).GetMethod(nameof(IOperationGateway.CreateApprovalProposalAsync));
        Assert.NotNull(method);
        var first = method!.GetParameters()[0];

        Assert.Equal(typeof(string), first.ParameterType);
        Assert.Equal("operationInstanceId", first.Name);
        Assert.False(first.HasDefaultValue);
    }

    [ArchitectureTest]
    public void OperationInstanceStore_ExposesVersionedTransitionPrecondition()
    {
        var method = typeof(IOperationInstanceStore).GetMethod(nameof(IOperationInstanceStore.TrySetAsync));
        Assert.NotNull(method);
        Assert.Contains(method!.GetParameters(), parameter =>
            parameter.Name == "expectedVersion" && parameter.ParameterType == typeof(long));
    }

    [ArchitectureTest]
    public void RestStatusEndpoint_DependsOnDurableInstanceStoreNotHandleCache()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "src/Honua.Server/Features/Operations/OperationsEndpoints.cs"));
        Assert.Contains("IOperationInstanceStore instanceStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationHandleStore", source, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void CacheHitProjection_UsesCanonicalEnvelopeFactory()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "src/Honua.Ai/Features/Protocols/Mcp/Mcp/Tools/PublishedOperationTool.cs"));
        Assert.Contains("CompleteCacheHitAsync", source, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void PlannedJanitor_DoesNotCaptureScopedAuditSink()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "src/Honua.Server/Features/Operations/PlannedProposalReconciler.cs"));
        Assert.Contains("IServiceScopeFactory scopeFactory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAuditLog auditLog", source, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void CompatibilityDescriptorWithoutSafeMapper_IsNeverAdvertised()
    {
        var descriptor = Descriptor() with { IsCompatibilityOnly = true };

        Assert.False(OperationDescriptorPublication.CanAdvertise(
            descriptor,
            new Dictionary<string, int>(StringComparer.Ordinal)));
    }

    [ArchitectureTest]
    public void LegacyGatewayRoute_IsTypedTranslationWithoutLocalPolicyOrActuatorBranch()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var source = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            root, "src", "Honua.Server", "Features", "ControlPlane", "OperationGateway.cs"));
        var routeStart = source.IndexOf("public async Task<OperationGatewayResult> RouteAsync", StringComparison.Ordinal);
        var routeEnd = source.IndexOf("private async Task<OperationGatewayResult> RouteAutonomyCompatibilityAsync", routeStart, StringComparison.Ordinal);
        var route = source[routeStart..routeEnd];

        Assert.Contains("GetRequiredService<ICanonicalOperationInvoker>()", route, StringComparison.Ordinal);
        Assert.DoesNotContain("_ladder.Resolve", route, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteAsync(", route, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateProposalAsync(", route, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void LegacyGateway_CannotOwnAnExecutorRegistryOrInvokeAnActuator()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var source = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            root, "src", "Honua.Server", "Features", "ControlPlane", "OperationGateway.cs"));

        Assert.DoesNotContain("_executors", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ExecuteAsync(", source, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<ICanonicalOperationInvoker>()", source, StringComparison.Ordinal);
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

    private sealed class AllowPolicy : IOperationPolicyDecisionPoint
    {
        public Task<PolicyDecision> EvaluateAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PolicyDecision.Allowed);
    }

    private sealed class BlockingGuardrailLadder : IGuardrailLadder
    {
        private static readonly GuardrailDecision Decision = new(
            GuardrailTier.Blocked,
            OperationClass.AdminConfigChange,
            HonuaEdition.Community,
            "architecture-test");

        public GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition) => Decision;

        public GuardrailDecision Resolve(OperationClass operationClass) => Decision;

        public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator) => Decision;

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            string? actionDiscriminator,
            HonuaEdition edition) => Decision;
    }

    private sealed class RecordingStore : IOperationInstanceStore
    {
        private OperationHandle? _envelope;

        public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        {
            _envelope = envelope;
            return Task.FromResult(true);
        }

        public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        {
            _envelope = envelope;
            return Task.CompletedTask;
        }

        public Task<OperationHandle?> GetAsync(string operationInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(_envelope);
    }

    private sealed class FailingPolicyEvidenceStore : IOperationInstanceStore
    {
        private OperationHandle? _envelope;
        private int _setCount;

        public Task<bool> TryCreateAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        {
            _envelope = envelope;
            return Task.FromResult(true);
        }

        public Task SetAsync(OperationHandle envelope, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _setCount) == 2)
            {
                throw new InvalidOperationException("simulated durable store outage");
            }

            _envelope = envelope;
            return Task.CompletedTask;
        }

        public Task<OperationHandle?> GetAsync(
            string operationInstanceId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_envelope);
    }

    private sealed class DurableAuditLog : IAuditLog
    {
        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>($"audit-{Guid.NewGuid():N}");
    }

    private sealed class StubProposalStore : IOperationProposalStore
    {
        public Task<bool> TryCreateAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<OperationProposal?> GetAsync(
            string proposalId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposal?>(null);

        public Task<bool> TrySetAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
            OperationClass? kind = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OperationProposal>>([]);

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(
            string operationId,
            string ownerId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class EvidenceCheckingExecutor(string operationId, RecordingStore store) : IOperationExecutor
    {
        public string OperationId => operationId;
        public int SubmitCount { get; private set; }
        public bool SawDurablePolicyEvidence { get; private set; }

        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public async Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            var stored = await store.GetAsync(context.OperationInstanceId!, cancellationToken);
            SawDurablePolicyEvidence = stored is
            {
                Status: OperationHandleStatus.Accepted,
                PolicyDecision: PolicyDecisionKind.Allow,
                AuditId: not null,
            };
            var now = DateTimeOffset.UtcNow;
            return new OperationHandle
            {
                OperationInstanceId = context.OperationInstanceId!,
                OperationId = operationId,
                CorrelationId = context.CorrelationId!,
                Status = OperationHandleStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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

    private sealed class TestMapper(string operationId) : IOperationApprovalRequestMapper
    {
        public string OperationId => operationId;

        public OperationGatewayRequest Map(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            PolicyDecision decision)
            => new() { Kind = OperationClass.ServicePublish };

        public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
            => new() { Request = new OperationRequest { OperationId = OperationId } };
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
