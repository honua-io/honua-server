// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
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
        var requestFingerprint = CreateRequestFingerprint(plan);

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
                CorrelationId = correlationId,
                RequestFingerprint = requestFingerprint
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
                // An idempotency key deterministically maps to one operation id. Guard against a
                // replay that reuses the key for a different package/resource/field: returning the
                // prior operation in that case would report success while silently dropping the new
                // request. Compare the request fingerprint and surface a conflict on mismatch.
                EnsureMatchingIdempotentRequest(existing, requestFingerprint);
                return existing;
            }

            throw new InvalidOperationException("Failed to durably record the metadata release operation.");
        }

        return operation;
    }

    private static void EnsureMatchingIdempotentRequest(WorkflowOperationRecord existing, string requestFingerprint)
    {
        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new ResourceConflictException(
            $"Idempotency key '{existing.Audit.IdempotencyKey}' is already associated with a different metadata-release request.");
    }

    /// <summary>
    /// Computes a stable fingerprint of the behavior-changing inputs of a metadata-release request
    /// (package, environment, resource, new field, optional data-populate workload, and the
    /// executable script shape). Mirrors the deploy workflow so a replay that reuses an idempotency
    /// key for a different intent is detected rather than silently skipped.
    /// </summary>
    private static string CreateRequestFingerprint(MetadataReleaseExecutionPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append("package=").Append(plan.PackageId.Trim()).Append('\n');
        builder.Append("environment=").Append(plan.TargetEnvironment.Trim()).Append('\n');
        builder.Append("resource=").Append(plan.ResourceSemanticId.Trim()).Append('\n');
        builder.Append("field=").Append(plan.NewFieldName.Trim()).Append('\n');
        builder.Append("dataPopulate=").Append(plan.DataPopulateWorkloadId?.Trim() ?? string.Empty).Append('\n');
        builder.Append("scriptId=").Append(plan.Script.ScriptId.Trim()).Append('\n');
        builder.Append("scriptReversible=").Append(plan.Script.Reversible ? "1" : "0").Append('\n');

        foreach (var op in plan.Script.ForwardOperations)
        {
            builder.Append("op=")
                .Append(op.Kind)
                .Append(':')
                .Append(op.ResourceSemanticId.Trim())
                .Append(':')
                .Append(op.FieldName.Trim())
                .Append(':')
                .Append(op.FieldType?.Trim() ?? string.Empty)
                .Append(':')
                .Append(op.Nullable ? "1" : "0")
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
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
