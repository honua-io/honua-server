// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>Default durable operation-envelope acceptance factory.</summary>
public sealed class OperationEnvelopeFactory(
    IOperationInstanceStore instanceStore,
    IAuditLog auditLog,
    TimeProvider clock) : IOperationEnvelopeFactory
{
    /// <inheritdoc />
    public async Task<OperationHandle> CreateAcceptedAsync(
        string operationId,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(context);

        var now = clock.GetUtcNow();
        var operationInstanceId = string.IsNullOrWhiteSpace(context.OperationInstanceId)
            ? string.IsNullOrWhiteSpace(context.IdempotencyKey)
                ? $"opinst-{Guid.NewGuid():N}"
                : DeriveIdempotentInstanceId(operationId, context.IdempotencyKey, context.TenantId)
            : context.OperationInstanceId;
        var envelope = new OperationHandle
        {
            OperationInstanceId = operationInstanceId,
            OperationId = operationId,
            TenantId = context.TenantId,
            CorrelationId = string.IsNullOrWhiteSpace(context.CorrelationId)
                ? $"corr-{Guid.NewGuid():N}"
                : context.CorrelationId,
            Status = OperationHandleStatus.Accepted,
            CreatedAt = now,
            UpdatedAt = now,
            AuthorizationOutcome = context.AuthorizationOutcome,
        };

        try
        {
            if (!await instanceStore.TryCreateAsync(envelope, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(context.IdempotencyKey))
                {
                    return await TouchIdempotentRetryAsync(envelope, context, cancellationToken).ConfigureAwait(false);
                }

                return Failure(envelope, "The canonical operation instance could not be durably accepted.");
            }

            var auditId = await auditLog.RecordAsync(new AuditEvent
            {
                Timestamp = clock.GetUtcNow(),
                EventType = AuditEventType.AdminAction,
                Actor = context.PrincipalId ?? AuditEvent.AnonymousActor,
                ActorType = context.PrincipalId is null ? AuditActorType.Anonymous : AuditActorType.UserId,
                ResourceType = "operation_instance",
                ResourceId = envelope.OperationInstanceId,
                Action = "operation.accepted",
                Outcome = AuditOutcome.Success,
                CorrelationId = envelope.CorrelationId,
                Details = $"operationId={envelope.OperationId};status={envelope.Status}",
            }, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(auditId))
            {
                return await PersistFailureAsync(
                        envelope,
                        "The operation was not accepted because durable audit persistence did not return an identity.")
                    .ConfigureAwait(false);
            }

            envelope = envelope with { AuditId = auditId, UpdatedAt = clock.GetUtcNow() };
            await instanceStore.SetAsync(envelope, cancellationToken).ConfigureAwait(false);
            return envelope;
        }
        catch (OperationCanceledException)
        {
            await PersistFailureAsync(envelope, "Operation acceptance was canceled.").ConfigureAwait(false);
            throw;
        }
        catch (CapabilityUnavailableException)
        {
            // Preserve the structured dependency receipt for REST/MCP while
            // retaining a failed envelope if acceptance reached durable storage.
            await PersistFailureAsync(envelope, "Operation acceptance requires an unavailable capability.")
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await PersistFailureAsync(
                    envelope,
                    $"The canonical operation instance could not be durably accepted ({ex.GetType().Name}).")
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<OperationHandle> CompleteCacheHitAsync(
        string operationId,
        OperationPolicyContext context,
        string sourceOperationInstanceId,
        string? sourceAuditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOperationInstanceId);
        var accepted = await CreateAcceptedAsync(operationId, context, cancellationToken).ConfigureAwait(false);
        if (accepted.Status == OperationHandleStatus.Failed)
        {
            return accepted;
        }

        var auditId = await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = clock.GetUtcNow(),
            EventType = AuditEventType.AdminAction,
            Actor = context.PrincipalId ?? AuditEvent.AnonymousActor,
            ActorType = context.PrincipalId is null ? AuditActorType.Anonymous : AuditActorType.UserId,
            ResourceType = "operation_instance",
            ResourceId = accepted.OperationInstanceId,
            Action = "operation.cache-hit",
            Outcome = AuditOutcome.Success,
            CorrelationId = accepted.CorrelationId,
            Details = $"operationId={operationId};sourceOperationInstanceId={sourceOperationInstanceId}",
        }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(auditId))
        {
            return await PersistFailureAsync(
                    accepted,
                    "The cached result was not returned because durable audit persistence did not return an identity.")
                .ConfigureAwait(false);
        }

        var evidence = new List<string>
        {
            $"accepted-audit:{accepted.AuditId}",
            $"cached-operation-instance:{sourceOperationInstanceId}",
        };
        if (!string.IsNullOrWhiteSpace(sourceAuditId))
        {
            evidence.Add($"cached-audit:{sourceAuditId}");
        }

        var completed = accepted with
        {
            Status = OperationHandleStatus.Completed,
            AuditId = auditId,
            UpdatedAt = clock.GetUtcNow(),
            EvidenceRefs = evidence,
        };
        await instanceStore.SetAsync(completed, cancellationToken).ConfigureAwait(false);
        return completed;
    }

    private async Task<OperationHandle> PersistFailureAsync(OperationHandle envelope, string reason)
    {
        var failed = Failure(envelope, reason);
        try
        {
            await instanceStore.SetAsync(failed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return failed with
            {
                Reason = $"{reason} The failure envelope could not be durably persisted ({ex.GetType().Name}).",
            };
        }

        return failed;
    }

    private async Task<OperationHandle> TouchIdempotentRetryAsync(
        OperationHandle attempted,
        OperationPolicyContext context,
        CancellationToken cancellationToken)
    {
        var existing = await instanceStore.GetAsync(attempted.OperationInstanceId, cancellationToken).ConfigureAwait(false);
        if (existing is null || string.IsNullOrWhiteSpace(existing.AuditId) ||
            !string.Equals(existing.TenantId, context.TenantId, StringComparison.Ordinal))
        {
            return Failure(attempted, "The idempotent invocation exists but has not completed durable acceptance.");
        }

        var retryAuditId = await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = clock.GetUtcNow(),
            EventType = AuditEventType.AdminAction,
            Actor = context.PrincipalId ?? AuditEvent.AnonymousActor,
            ActorType = context.PrincipalId is null ? AuditActorType.Anonymous : AuditActorType.UserId,
            ResourceType = "operation_instance",
            ResourceId = existing.OperationInstanceId,
            Action = "operation.retry",
            Outcome = AuditOutcome.Success,
            CorrelationId = existing.CorrelationId,
            Details = $"operationId={existing.OperationId};idempotencyKey={context.IdempotencyKey}",
        }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(retryAuditId))
        {
            return Failure(existing, "The idempotent retry touch could not be durably audited.");
        }

        var touched = existing with
        {
            Status = existing.Status == OperationHandleStatus.Cancelled ||
                string.Equals(existing.Reason, "Operation acceptance was canceled.", StringComparison.Ordinal)
                    ? OperationHandleStatus.Accepted
                    : existing.Status,
            UpdatedAt = clock.GetUtcNow(),
            Reason = existing.Status == OperationHandleStatus.Cancelled ? null : existing.Reason,
            EvidenceRefs = [.. existing.EvidenceRefs, $"retry-audit:{retryAuditId}"],
        };
        await instanceStore.SetAsync(touched, cancellationToken).ConfigureAwait(false);
        return touched;
    }

    private static string DeriveIdempotentInstanceId(string operationId, string idempotencyKey, string? tenantId)
    {
        var key = $"{operationId}:{idempotencyKey}";
        var material = System.Text.Encoding.UTF8.GetBytes(tenantId is null ? key : $"{tenantId.Length}:{tenantId}:{key}");
        var hash = System.Security.Cryptography.SHA256.HashData(material);
        return $"opinst-{Convert.ToHexString(hash)[..32].ToLowerInvariant()}";
    }

    private OperationHandle Failure(OperationHandle envelope, string reason) => envelope with
    {
        Status = OperationHandleStatus.Failed,
        UpdatedAt = clock.GetUtcNow(),
        Reason = reason,
    };
}
