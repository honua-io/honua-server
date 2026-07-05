// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Regression coverage for the additive metadata-release lifecycle review fixes (PR #1739):
/// the singleton preflight gate must resolve the scoped compatibility prevalidation service per
/// evaluation (no scoped-from-singleton capture), and idempotency-key replays that change the
/// request must conflict rather than silently returning the prior operation.
/// </summary>
public sealed class MetadataReleaseLifecycleFixTests
{
    [Fact]
    public async Task PreflightGate_ResolvedAsSingletonUnderScopeValidation_ResolvesScopedPrevalidationPerEvaluation()
    {
        // Build a provider that validates scopes (the production/development configuration that
        // surfaced the scoped-from-singleton DI error). The gate is a singleton; the canonical
        // prevalidation service is scoped. Before the fix, resolving the singleton gate captured the
        // scoped service from the root provider and threw under scope validation.
        var recordingPrevalidation = new RecordingPrevalidationService();
        var services = new ServiceCollection();
        services.AddScoped<IMetadataCompatibilityPrevalidationService>(_ => recordingPrevalidation);
        services.AddSingleton<IMetadataReleasePreflightGate, MetadataReleasePreflightGate>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // Resolve the gate from the ROOT provider (as the singleton reconciler does).
        var gate = provider.GetRequiredService<IMetadataReleasePreflightGate>();

        // A GUID package id drives the canonical prevalidation path, exercising the per-evaluation
        // scope. This would throw before the fix; after it, the scoped service is resolved cleanly.
        var plan = CreatePlan(packageId: Guid.NewGuid().ToString());
        var result = await gate.EvaluateAsync(plan);

        result.CanProceed.Should().BeTrue();
        result.RollbackClassification.Should().Be(MetadataRollbackReadinessClassification.ScriptReversible);
        recordingPrevalidation.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_IdempotencyKeyReusedForDifferentRequest_ThrowsConflict()
    {
        var store = new InMemoryWorkflowOperationStore();
        var service = new MetadataReleaseControlService(new[] { (IWorkflowOperationStore)store });
        const string idempotencyKey = "demo-b-owner-email";

        var first = CreatePlan(
            packageId: "pkg-demo-b",
            resourceSemanticId: "parcels",
            newFieldName: "owner_email");
        var created = await service.CreateAsync(first, requestedBy: "tester", reason: null, idempotencyKey, correlationId: null);
        created.Status.Should().Be(WorkflowOperationStatus.Submitted);

        // Replaying the SAME request with the SAME key returns the original operation idempotently.
        var replay = await service.CreateAsync(first, requestedBy: "tester", reason: null, idempotencyKey, correlationId: null);
        replay.OperationId.Should().Be(created.OperationId);

        // Reusing the key for a DIFFERENT field must conflict rather than silently returning the
        // prior operation and dropping the new release.
        var mismatched = CreatePlan(
            packageId: "pkg-demo-b",
            resourceSemanticId: "parcels",
            newFieldName: "owner_phone");

        var act = async () => await service.CreateAsync(
            mismatched, requestedBy: "tester", reason: null, idempotencyKey, correlationId: null);

        await act.Should().ThrowAsync<ResourceConflictException>()
            .WithMessage($"*'{idempotencyKey}'*");
    }

    private static MetadataReleaseExecutionPlan CreatePlan(
        string packageId = "pkg-demo-b",
        string targetEnvironment = "production",
        string resourceSemanticId = "parcels",
        string newFieldName = "owner_email",
        string? dataPopulateWorkloadId = "populate-owner-email")
        => new()
        {
            PackageId = packageId,
            TargetEnvironment = targetEnvironment,
            ResourceSemanticId = resourceSemanticId,
            NewFieldName = newFieldName,
            DataPopulateWorkloadId = dataPopulateWorkloadId,
            Script = new MetadataReleaseScript
            {
                ScriptId = $"add-{newFieldName}",
                Reversible = true,
                ForwardOperations =
                [
                    new MetadataReleaseScriptOperation
                    {
                        Kind = MetadataReleaseScriptOperationKind.AddColumn,
                        ResourceSemanticId = resourceSemanticId,
                        FieldName = newFieldName,
                        FieldType = "String",
                        Nullable = true
                    }
                ]
            }
        };

    private sealed class RecordingPrevalidationService : IMetadataCompatibilityPrevalidationService
    {
        public int Calls { get; private set; }

        public Task<MetadataCompatibilityReport> PrevalidateAsync(
            MetadataCompatibilityPrevalidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new MetadataCompatibilityReport
            {
                TargetEnvironment = request.TargetEnvironment,
                GeneratedAt = DateTimeOffset.UtcNow,
                Status = MetadataCompatibilityStatus.Ready,
                RollbackReadiness = new MetadataRollbackReadiness
                {
                    Classification = MetadataRollbackReadinessClassification.ScriptReversible,
                    Reason = "Additive nullable adds are reversible."
                }
            });
        }
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            var created = _operations.TryAdd(operation.OperationId, operation);
            if (created)
            {
                Index(operation);
            }

            return Task.FromResult(created);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _metadataPackageIndex.TryGetValue(packageId, out var operationId) &&
                _operations.TryGetValue(operationId, out var operation)
                    ? operation
                    : null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        private void Index(WorkflowOperationRecord operation)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                _metadataPackageIndex[operation.MetadataRelease.PackageId] = operation.OperationId;
            }
        }
    }
}
