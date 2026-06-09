// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Decorates the provider <see cref="IFeatureWriter"/> to emit audit events for
/// destructive feature writes (single deletes and bulk / transactional edits) at
/// the shared edit-pipeline boundary (#507).
/// </summary>
/// <remarks>
/// <para>
/// Every protocol adapter that mutates features — GeoServices FeatureServer
/// <c>applyEdits</c>/<c>deleteFeatures</c>, OGC API Features delete/transaction,
/// WFS-T, OData CRUD, and gRPC edits — funnels through <see cref="IFeatureWriter"/>.
/// Auditing here (rather than in each protocol handler) guarantees uniform
/// coverage and honours the protocol-adapter rule that cross-cutting behaviour is
/// implemented once in shared infrastructure. Creates and pure updates are not
/// audited as destructive operations; only deletes and batches that contain a
/// delete (or are otherwise bulk mutations) are recorded.
/// </para>
/// <para>
/// Audit emission never alters the write result and never throws back into the
/// edit path: <see cref="IAuditLog"/> implementations are expected to swallow
/// transient failures, and a defensive try/catch guards the rest.
/// </para>
/// </remarks>
internal sealed class AuditingFeatureWriter(
    IFeatureWriter inner,
    IAuditLog auditLog,
    IHttpContextAccessor httpContextAccessor) : IFeatureWriter
{
    private readonly IFeatureWriter _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IAuditLog _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor
        ?? throw new ArgumentNullException(nameof(httpContextAccessor));

    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(layerId, feature, cancellationToken);

    public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => _inner.UpdateAsync(layerId, feature, cancellationToken);

    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var deleted = false;
        try
        {
            deleted = await _inner.DeleteAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
            await EmitDeleteAsync(layerId, featureId, deleted, cancellationToken).ConfigureAwait(false);
            return deleted;
        }
        catch
        {
            await EmitDeleteFailureAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _inner.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
            await EmitBulkEditAsync(layerId, editBatch, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await EmitBulkEditFailureAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private Task EmitDeleteAsync(int layerId, long featureId, bool deleted, CancellationToken cancellationToken)
        => RecordAsync(
            action: "feature.delete",
            layerId: layerId,
            outcome: deleted ? AuditOutcome.Success : AuditOutcome.Failure,
            details: string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"objectId\":{featureId},\"deleted\":{(deleted ? "true" : "false")}}}"),
            cancellationToken);

    private Task EmitDeleteFailureAsync(int layerId, long featureId, CancellationToken cancellationToken)
        => RecordAsync(
            action: "feature.delete",
            layerId: layerId,
            outcome: AuditOutcome.Failure,
            details: string.Create(CultureInfo.InvariantCulture, $"{{\"objectId\":{featureId},\"deleted\":false}}"),
            cancellationToken);

    private Task EmitBulkEditAsync(
        int layerId,
        FeatureEditBatch editBatch,
        FeatureEditResult result,
        CancellationToken cancellationToken)
    {
        var deleteCount = CountDeletes(editBatch);

        // Only audit when the batch is destructive (contains deletes) or is a bulk
        // mutation (more than one operation). Single create/update writes are not
        // forensic events of interest for the destructive-write matrix row.
        if (deleteCount == 0 && editBatch.TotalOperations <= 1)
        {
            return Task.CompletedTask;
        }

        var action = deleteCount > 0 ? "feature.bulk-edit.delete" : "feature.bulk-edit";
        var details = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"created\":{result.CreatedCount},\"updated\":{result.UpdatedCount},\"deleted\":{result.DeletedCount},\"rolledBack\":{(result.WasRolledBack ? "true" : "false")}}}");

        return RecordAsync(
            action,
            layerId,
            result.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failure,
            details,
            cancellationToken);
    }

    private Task EmitBulkEditFailureAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken)
    {
        var deleteCount = CountDeletes(editBatch);
        if (deleteCount == 0 && editBatch.TotalOperations <= 1)
        {
            return Task.CompletedTask;
        }

        var action = deleteCount > 0 ? "feature.bulk-edit.delete" : "feature.bulk-edit";
        return RecordAsync(
            action,
            layerId,
            AuditOutcome.Failure,
            details: "{\"error\":true}",
            cancellationToken);
    }

    private static int CountDeletes(FeatureEditBatch editBatch)
    {
        var direct = editBatch.Deletes.IsDefault ? 0 : editBatch.Deletes.Length;
        var inOps = editBatch.Operations.IsDefault
            ? 0
            : editBatch.Operations.Count(static op => op.Kind == FeatureEditOperationKind.Delete);
        return direct + inOps;
    }

    private async Task RecordAsync(
        string action,
        int layerId,
        AuditOutcome outcome,
        string details,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = _httpContextAccessor.HttpContext;
            string actor;
            AuditActorType actorType;
            if (context is not null)
            {
                actor = AuditContextResolver.ResolveActor(context, out actorType);
            }
            else
            {
                // No request scope (e.g. background sync / replica apply). Record
                // the write as a system action rather than dropping the event.
                actor = AuditEvent.AnonymousActor;
                actorType = AuditActorType.System;
            }

            var auditEvent = new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.DataDelete,
                Actor = actor,
                ActorType = actorType,
                ResourceType = "feature",
                ResourceId = layerId.ToString(CultureInfo.InvariantCulture),
                Action = action,
                Outcome = outcome,
                CorrelationId = context is not null
                    ? AuditContextResolver.ResolveCorrelationId(context)
                    : Guid.NewGuid().ToString("D"),
                RemoteIp = context is not null ? AuditContextResolver.ResolveRemoteIp(context) : null,
                UserAgent = context is not null ? AuditContextResolver.ResolveUserAgent(context) : null,
                Details = details,
            };

            await _auditLog.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Auditing must never break the edit path. Sinks log their own errors.
        }
    }
}
