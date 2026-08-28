// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
        var envelope = new OperationHandle
        {
            OperationInstanceId = string.IsNullOrWhiteSpace(context.OperationInstanceId)
                ? $"opinst-{Guid.NewGuid():N}"
                : context.OperationInstanceId,
            OperationId = operationId,
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await PersistFailureAsync(
                    envelope,
                    $"The canonical operation instance could not be durably accepted ({ex.GetType().Name}).")
                .ConfigureAwait(false);
        }
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

    private OperationHandle Failure(OperationHandle envelope, string reason) => envelope with
    {
        Status = OperationHandleStatus.Failed,
        UpdatedAt = clock.GetUtcNow(),
        Reason = reason,
    };
}
