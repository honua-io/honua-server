// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;

namespace Honua.Core.Tests.OperationScopes;

/// <summary>
/// Every audit record the canonical operation runtime writes for one invocation must name the
/// authenticated caller that submitted it. MCP tools submit through <see cref="IOperationInvoker"/>
/// with the caller's canonical actor id, so an audit row that falls back to the anonymous actor
/// would record an authenticated MCP call as anonymous.
/// </summary>
public sealed class OperationActorAuditTests
{
    private const string OperationId = "service.publish";
    private const string McpApiKeyActor = "ApiKey:api-key:3f1c2b4a-8d7e-4c55-9a10-6b2e7d9c0f11";

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task SubmitAsync_AuthenticatedCallCanceledBeforeActuation_AuditsTheCallerNotAnonymous()
    {
        using var requestCancellation = new CancellationTokenSource();
        var audit = new CapturingAuditLog();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new StubDescriptorProvider()], TimeProvider.System),
            [new CancelingDuringValidationExecutor(requestCancellation)],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            auditLog: audit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.SubmitAsync(
            new OperationRequest { OperationId = OperationId },
            new OperationPolicyContext { PrincipalId = McpApiKeyActor, AuthorizationOutcome = "authorized" },
            requestCancellation.Token));

        var accepted = Assert.Single(audit.Events, entry => entry.Action == "operation.accepted");
        var cancelled = Assert.Single(audit.Events, entry => entry.Action == "operation.cancelled");
        Assert.Equal(accepted.ResourceId, cancelled.ResourceId);
        foreach (var entry in new[] { accepted, cancelled })
        {
            Assert.Equal(McpApiKeyActor, entry.Actor);
            Assert.NotEqual(AuditEvent.AnonymousActor, entry.Actor);
            Assert.NotEqual(AuditActorType.Anonymous, entry.ActorType);
        }
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task SubmitAsync_AnonymousCallCanceledBeforeActuation_StaysAnonymous()
    {
        using var requestCancellation = new CancellationTokenSource();
        var audit = new CapturingAuditLog();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new StubDescriptorProvider()], TimeProvider.System),
            [new CancelingDuringValidationExecutor(requestCancellation)],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System,
            auditLog: audit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.SubmitAsync(
            new OperationRequest { OperationId = OperationId },
            new OperationPolicyContext(),
            requestCancellation.Token));

        var cancelled = Assert.Single(audit.Events, entry => entry.Action == "operation.cancelled");
        Assert.Equal(AuditEvent.AnonymousActor, cancelled.Actor);
        Assert.Equal(AuditActorType.Anonymous, cancelled.ActorType);
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        private readonly List<AuditEvent> _events = [];

        public IReadOnlyList<AuditEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (_events)
            {
                _events.Add(auditEvent);
            }

            return Task.FromResult<string?>($"audit-{Guid.NewGuid():N}");
        }
    }

    private sealed class StubDescriptorProvider : IOperationDescriptorProvider
    {
        public string ProviderId => "test";

        public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IOperationDescriptor>>(
            [
                new OperationDescriptor
                {
                    OperationId = OperationId,
                    ProviderId = "test",
                    Title = "Publish",
                    Description = "Publish a layer",
                    Category = "Publishing",
                    ExecutionKind = OperationExecutionKind.Synchronous,
                    ApprovalModel = OperationApprovalModel.None,
                    Policy = new OperationPolicyMetadata
                    {
                        BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
                        SideEffectClass = OperationSideEffectClass.CreatesMetadata,
                        Determinism = OperationDeterminism.Deterministic,
                        SupportsDryRun = false,
                    },
                },
            ]);
    }

    private sealed class CancelingDuringValidationExecutor(CancellationTokenSource requestCancellation)
        : IOperationExecutor
    {
        public string OperationId => OperationActorAuditTests.OperationId;

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            requestCancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Actuator must not run after canceled validation.");

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
