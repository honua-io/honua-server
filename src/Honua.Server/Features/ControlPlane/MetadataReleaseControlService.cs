// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Exceptions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Server-side control-plane service that constructs and submits additive metadata-release
/// layer-evolution operations. This is the create path that produces
/// <see cref="WorkflowOperationKind.MetadataRelease"/> records for
/// <see cref="MetadataReleaseReconciler"/> to advance. Mirrors <see cref="DeployWorkflowService"/>
/// conventions: durable store, deterministic operation id, idempotent create.
/// </summary>
internal sealed partial class MetadataReleaseControlService(
    IEnumerable<IWorkflowOperationStore> workflowStores)
{
    private static readonly Regex UnsafeOperationIdCharacters = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IWorkflowOperationStore? _workflowStore = workflowStores.FirstOrDefault();

    /// <summary>
    /// Creates and submits an additive metadata-release operation. The operation is persisted in the
    /// <see cref="WorkflowOperationStatus.Submitted"/> state so the metadata-release reconciler walks
    /// the additive stages on its next cycle.
    /// </summary>
    public async Task<WorkflowOperationRecord> CreateAsync(
        MetadataReleaseExecutionPlan plan,
        string? requestedBy,
        string? reason,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureDurableStoreConfigured();

        var now = DateTimeOffset.UtcNow;
        var operationId = CreateOperationId(plan.PackageId, idempotencyKey);

        var release = new MetadataReleaseContext
        {
            PackageId = plan.PackageId,
            DesiredRevision = plan.PackageId,
            TargetEnvironment = plan.TargetEnvironment,
            CurrentStage = MetadataReleaseStage.Preflight,
            ExecutionPlan = plan
        };

        var operation = new WorkflowOperationRecord
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.MetadataRelease,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitted additive metadata-release lifecycle; awaiting preflight.",
            Audit = new OperationAuditInfo
            {
                RequestedBy = requestedBy,
                Reason = reason,
                IdempotencyKey = idempotencyKey,
                CorrelationId = correlationId
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = $"{plan.TargetEnvironment}:metadata-release:{plan.ResourceSemanticId}",
                RequiresExclusiveLease = true
            },
            MetadataRelease = release
        };

        var created = await _workflowStore!.TryCreateAsync(operation, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            var existing = await _workflowStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                return existing;
            }

            throw new InvalidOperationException("Failed to durably record the metadata release operation.");
        }

        return operation;
    }

    public async Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();
        return await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateOperationId(string packageId, string? idempotencyKey)
    {
        var seed = string.IsNullOrWhiteSpace(idempotencyKey) ? packageId : idempotencyKey;
        var slug = UnsafeOperationIdCharacters.Replace(seed.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return $"metadata-release-{Guid.NewGuid():N}";
        }

        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        return string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"metadata-release-{slug}-{Guid.NewGuid():N}"
            : $"metadata-release-{slug}";
    }

    private void EnsureDurableStoreConfigured()
    {
        if (_workflowStore == null)
        {
            throw new InvalidOperationException("Metadata release operations require Redis-backed durable storage.");
        }
    }
}
