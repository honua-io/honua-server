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
    private const string RequestHashEvidencePrefix = "idempotency-request-sha256:";

    internal static IReadOnlyList<string> PreserveRequestBinding(OperationHandle envelope, IReadOnlyList<string> evidenceRefs)
    {
        var binding = envelope.EvidenceRefs.FirstOrDefault(reference => reference.StartsWith(RequestHashEvidencePrefix, StringComparison.Ordinal));
        return binding is null || evidenceRefs.Contains(binding, StringComparer.Ordinal)
            ? evidenceRefs
            : [binding, .. evidenceRefs];
    }

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
            EvidenceRefs = context.IdempotencyRequestHash is { } requestHash
                ? [RequestHashEvidencePrefix + requestHash]
                : [],
        };

        var ownsAcceptance = false;
        try
        {
            ownsAcceptance = await instanceStore.TryCreateAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (!ownsAcceptance)
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
            if (ownsAcceptance)
            {
                await PersistFailureAsync(envelope, "Operation acceptance was canceled.").ConfigureAwait(false);
            }
            throw;
        }
        catch (CapabilityUnavailableException)
        {
            // Preserve the structured dependency receipt for REST/MCP while
            // retaining a failed envelope if acceptance reached durable storage.
            if (ownsAcceptance)
            {
                await PersistFailureAsync(envelope, "Operation acceptance requires an unavailable capability.")
                    .ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!ownsAcceptance)
            {
                return Failure(envelope, $"The idempotent invocation could not be read or audited ({ex.GetType().Name}).");
            }
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
        if (context.IdempotencyRequestHash is not null && existing is not null)
        {
            return await TouchBoundRetryAsync(existing, context, cancellationToken).ConfigureAwait(false);
        }
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

    private async Task<OperationHandle> TouchBoundRetryAsync(
        OperationHandle existing,
        OperationPolicyContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.TenantId, context.TenantId, StringComparison.Ordinal) ||
            !existing.EvidenceRefs.Contains(RequestHashEvidencePrefix + context.IdempotencyRequestHash, StringComparer.Ordinal))
        {
            return Conflict(existing, "The idempotency key is already bound to a different request.");
        }

        if (existing.Status == OperationHandleStatus.Accepted || string.IsNullOrWhiteSpace(existing.AuditId))
        {
            // The atomic TryCreate winner owns actuation. Never return Accepted to a
            // concurrent retry, and never write its projection over the owner's progress.
            return Conflict(existing, "The idempotent invocation is still being accepted or executed; retry later.");
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
            Details = $"operationId={existing.OperationId}",
        }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(retryAuditId))
        {
            return Failure(existing, "The idempotent retry could not be durably audited.") with { Result = null };
        }

        // A queued job can progress while the retry is audited. Reading the latest
        // projection avoids a stale retry overwriting a worker's terminal transition.
        var current = await instanceStore.GetAsync(existing.OperationInstanceId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure(existing, "The idempotent invocation is no longer durably readable.") with { Result = null };
        }
        // Approved replay can claim a waiting envelope while the retry is audited.
        // A retry must never receive Accepted, which grants dispatcher actuation.
        return current.Status == OperationHandleStatus.Accepted
            ? Conflict(current, "The idempotent invocation is still being accepted or executed; retry later.")
            : current with { EvidenceRefs = [.. current.EvidenceRefs, $"retry-audit:{retryAuditId}"] };
    }

    private OperationHandle Conflict(OperationHandle existing, string reason) => Failure(existing, reason) with
    {
        Result = new OperationResultSummary
        {
            Summary = reason,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["statusCode"] = "409",
                ["errorKind"] = "conflict",
            },
        },
    };

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
