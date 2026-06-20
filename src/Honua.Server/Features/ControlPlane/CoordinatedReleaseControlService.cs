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
/// Create path for coordinated platform-upgrade releases (Demo C). Builds and submits the parent
/// <see cref="WorkflowOperationKind.CoordinatedRelease"/> record — including the ordered step ledger
/// with the container-swap and data-affecting approval gates — for
/// <see cref="CoordinatedReleaseReconciler"/> to conduct. Also exposes the operator gate-approval and
/// coordinated-rollback actions the console drives. Mirrors <see cref="MetadataReleaseControlService"/>
/// conventions: durable store, deterministic operation id, idempotent create.
/// </summary>
internal sealed partial class CoordinatedReleaseControlService(
    IEnumerable<IWorkflowOperationStore> workflowStores)
{
    private static readonly Regex UnsafeOperationIdCharacters = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IWorkflowOperationStore? _workflowStore = workflowStores.FirstOrDefault();

    /// <summary>
    /// Creates and submits a coordinated release. The operation is persisted in
    /// <see cref="WorkflowOperationStatus.Submitted"/> so the conductor advances on its next cycle.
    /// </summary>
    public async Task<WorkflowOperationRecord> CreateAsync(
        CoordinatedReleaseContainerSpec container,
        MetadataReleaseExecutionPlan metadataPlan,
        string targetEnvironment,
        string? requestedBy,
        string? reason,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(metadataPlan);
        EnsureDurableStoreConfigured();

        var now = DateTimeOffset.UtcNow;
        // Default the idempotency key to the package id so the operation id is deterministic and the
        // package-based read endpoint can recompute it without a separate store index.
        var effectiveIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? metadataPlan.PackageId : idempotencyKey;
        var operationId = CreateOperationId(metadataPlan.PackageId, effectiveIdempotencyKey);
        var requestFingerprint = CreateRequestFingerprint(container, metadataPlan, targetEnvironment);

        var context = new CoordinatedReleaseContext
        {
            PackageId = metadataPlan.PackageId,
            TargetEnvironment = targetEnvironment,
            Container = container,
            MetadataPlan = metadataPlan,
            CurrentStep = CoordinatedReleaseStep.Preflight,
            Steps = BuildInitialLedger(container, now)
        };

        var operation = new WorkflowOperationRecord
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.CoordinatedRelease,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitted coordinated platform-upgrade release; awaiting preflight.",
            Audit = new OperationAuditInfo
            {
                RequestedBy = requestedBy,
                Reason = reason,
                IdempotencyKey = effectiveIdempotencyKey,
                CorrelationId = correlationId,
                RequestFingerprint = requestFingerprint
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = $"{targetEnvironment}:coordinated-release:{metadataPlan.ResourceSemanticId}",
                RequiresExclusiveLease = true
            },
            CoordinatedRelease = context
        };

        var created = await _workflowStore!.TryCreateAsync(operation, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            var existing = await _workflowStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                EnsureMatchingIdempotentRequest(existing, requestFingerprint);
                return existing;
            }

            throw new InvalidOperationException("Failed to durably record the coordinated release operation.");
        }

        return operation;
    }

    /// <summary>
    /// Approves one of the coordinated release gates (container-swap or data-affecting) so the
    /// conductor can advance past the awaiting-approval step on its next cycle.
    /// </summary>
    public async Task<WorkflowOperationRecord?> ApproveGateAsync(
        string operationId,
        CoordinatedReleaseStep gate,
        string? requestedBy,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();

        var operation = await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.Kind != WorkflowOperationKind.CoordinatedRelease || operation.CoordinatedRelease is null)
        {
            return null;
        }

        var context = operation.CoordinatedRelease;
        var updatedContext = gate switch
        {
            CoordinatedReleaseStep.ContainerRollout => context with { ContainerGateApproved = true },
            CoordinatedReleaseStep.MetadataAndSchema => context with { DataGateApproved = true },
            _ => throw new InvalidOperationException($"Coordinated release step '{gate}' is not an approvable gate.")
        };

        var updated = operation with
        {
            // Returning to Reconciling lets the conductor re-enter and advance the now-approved gate.
            Status = WorkflowOperationStatus.Reconciling,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = $"Operator approved the {gate} gate.",
            Audit = operation.Audit with
            {
                RequestedBy = requestedBy ?? operation.Audit.RequestedBy,
                Reason = reason ?? operation.Audit.Reason
            },
            CoordinatedRelease = updatedContext
        };

        await _workflowStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Requests the coordinated rollback. Flips the operation into RollbackRequested so the conductor
    /// unwinds the completed reversible steps in reverse order on its next cycle.
    /// </summary>
    public async Task<WorkflowOperationRecord?> RequestRollbackAsync(
        string operationId,
        string? requestedBy,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();

        var operation = await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.Kind != WorkflowOperationKind.CoordinatedRelease || operation.CoordinatedRelease is null)
        {
            return null;
        }

        if (operation.Status is WorkflowOperationStatus.RolledBack or WorkflowOperationStatus.Failed)
        {
            return operation;
        }

        var updated = operation with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Operator requested coordinated rollback; unwinding completed steps in reverse order.",
            Audit = operation.Audit with
            {
                RequestedBy = requestedBy ?? operation.Audit.RequestedBy,
                Reason = reason ?? operation.Audit.Reason
            },
            CoordinatedRelease = operation.CoordinatedRelease with { CurrentStep = CoordinatedReleaseStep.RollingBack }
        };

        await _workflowStore.SetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();
        return await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowOperationRecord?> GetByPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
    {
        EnsureDurableStoreConfigured();
        var operationId = CreateOperationId(packageId, packageId);
        return await _workflowStore!.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<CoordinatedReleaseStepState> BuildInitialLedger(
        CoordinatedReleaseContainerSpec container,
        DateTimeOffset at)
    {
        return
        [
            Step(CoordinatedReleaseStep.Preflight, requiresApproval: false, reversible: false, at,
                "Validate the container, DB-script, and metadata artifacts."),
            Step(CoordinatedReleaseStep.Backup, requiresApproval: false, reversible: false, at,
                "Capture backups (no-op for the additive reversible path)."),
            Step(CoordinatedReleaseStep.ContainerRollout, requiresApproval: container.RequiresExplicitApproval, reversible: true, at,
                $"Roll out container image '{container.DesiredImage}'."),
            Step(CoordinatedReleaseStep.MetadataAndSchema, requiresApproval: true, reversible: true, at,
                "Apply the additive DB schema change and activate the new metadata revision."),
            Step(CoordinatedReleaseStep.Smoke, requiresApproval: false, reversible: false, at,
                "Smoke-verify the upgraded platform across all three artifacts."),
            Step(CoordinatedReleaseStep.Promote, requiresApproval: false, reversible: false, at,
                "Promote the coordinated release to the target environment.")
        ];
    }

    private static CoordinatedReleaseStepState Step(
        CoordinatedReleaseStep step,
        bool requiresApproval,
        bool reversible,
        DateTimeOffset at,
        string detail) => new()
        {
            Step = step,
            Status = CoordinatedReleaseStepStatus.Pending,
            RequiresApproval = requiresApproval,
            IsReversible = reversible,
            Detail = detail,
            At = at
        };

    private static void EnsureMatchingIdempotentRequest(WorkflowOperationRecord existing, string requestFingerprint)
    {
        var existingFingerprint = existing.Audit.RequestFingerprint;
        if (!string.IsNullOrWhiteSpace(existingFingerprint) &&
            string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new ResourceConflictException(
            $"Idempotency key '{existing.Audit.IdempotencyKey}' is already associated with a different coordinated-release request.");
    }

    private static string CreateRequestFingerprint(
        CoordinatedReleaseContainerSpec container,
        MetadataReleaseExecutionPlan plan,
        string targetEnvironment)
    {
        var builder = new StringBuilder();
        builder.Append("environment=").Append(targetEnvironment.Trim()).Append('\n');
        builder.Append("package=").Append(plan.PackageId.Trim()).Append('\n');
        builder.Append("containerTarget=").Append(container.TargetId.Trim()).Append('\n');
        builder.Append("containerImage=").Append(container.DesiredImage.Trim()).Append('\n');
        builder.Append("resource=").Append(plan.ResourceSemanticId.Trim()).Append('\n');
        builder.Append("field=").Append(plan.NewFieldName.Trim()).Append('\n');
        builder.Append("scriptId=").Append(plan.Script.ScriptId.Trim()).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string CreateOperationId(string packageId, string? idempotencyKey)
    {
        var seed = string.IsNullOrWhiteSpace(idempotencyKey) ? packageId : idempotencyKey;
        var slug = UnsafeOperationIdCharacters.Replace(seed.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return $"coordinated-release-{Guid.NewGuid():N}";
        }

        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        return $"coordinated-release-{slug}";
    }

    private void EnsureDurableStoreConfigured()
    {
        if (_workflowStore == null)
        {
            throw new InvalidOperationException("Coordinated release operations require Redis-backed durable storage.");
        }
    }
}
