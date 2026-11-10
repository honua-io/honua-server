// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;

namespace Honua.Core.Features.MultiTenancy.Lifecycle;

// The audit event is written only after the catalog commit, so the trail does not claim a transition that failed.
public sealed class TenantLifecycleService
{
    public const int MaxTenantIdLength = 128;

    private readonly ITenantCatalog _catalog;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    public TenantLifecycleService(
        ITenantCatalog catalog,
        IAuditLog auditLog,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog;
        _auditLog = auditLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<TenantRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
        => _catalog.GetAsync(tenantId, cancellationToken);

    public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default)
        => _catalog.ListAsync(cancellationToken);

    public async Task<TenantLifecycleResult> CreateAsync(
        string tenantId,
        string displayName,
        string? plan,
        TenantLifecycleActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidTenantId(tenantId))
        {
            return new TenantLifecycleResult(TenantLifecycleOutcome.Invalid, null, "Invalid tenant id.");
        }

        var now = _timeProvider.GetUtcNow();
        var record = new TenantRecord
        {
            TenantId = tenantId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? tenantId : displayName,
            Status = TenantStatus.Active,
            Plan = string.IsNullOrWhiteSpace(plan) ? null : plan,
            CreatedAt = now,
            UpdatedAt = now,
            PriorStatus = TenantStatus.Active,
        };

        if (!await _catalog.TryAddAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return new TenantLifecycleResult(TenantLifecycleOutcome.Conflict, null, "Tenant already exists.");
        }

        await RecordAsync(actor, record, "tenant.create", AuditEventType.ConfigChange, cancellationToken)
            .ConfigureAwait(false);
        return new TenantLifecycleResult(TenantLifecycleOutcome.Created, record, null);
    }

    public Task<TenantLifecycleResult> SuspendAsync(
        string tenantId,
        TenantLifecycleActor actor,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            tenantId,
            actor,
            "tenant.suspend",
            current =>
            {
                if (current.Status != TenantStatus.Active)
                {
                    return (null, $"Cannot suspend a tenant in '{current.Status}' state.");
                }

                var now = _timeProvider.GetUtcNow();
                return (current with
                {
                    Status = TenantStatus.Suspended,
                    PriorStatus = current.Status,
                    SuspendedAt = now,
                    UpdatedAt = now,
                }, null);
            },
            cancellationToken);

    public Task<TenantLifecycleResult> ResumeAsync(
        string tenantId,
        TenantLifecycleActor actor,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            tenantId,
            actor,
            "tenant.resume",
            current =>
            {
                if (current.Status != TenantStatus.Suspended)
                {
                    return (null, $"Cannot resume a tenant in '{current.Status}' state.");
                }

                var now = _timeProvider.GetUtcNow();
                return (current with
                {
                    Status = current.PriorStatus == TenantStatus.Suspended ? TenantStatus.Active : current.PriorStatus,
                    SuspendedAt = null,
                    UpdatedAt = now,
                }, null);
            },
            cancellationToken);

    public Task<TenantLifecycleResult> DeleteAsync(
        string tenantId,
        TenantLifecycleActor actor,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            tenantId,
            actor,
            "tenant.delete",
            current =>
            {
                if (current.Status == TenantStatus.Deleted)
                {
                    return (null, "Tenant is already deleted.");
                }

                var now = _timeProvider.GetUtcNow();
                return (current with
                {
                    Status = TenantStatus.Deleted,
                    DeletedAt = now,
                    UpdatedAt = now,
                }, null);
            },
            cancellationToken);

    private async Task<TenantLifecycleResult> TransitionAsync(
        string tenantId,
        TenantLifecycleActor actor,
        string action,
        Func<TenantRecord, (TenantRecord? Next, string? Error)> transition,
        CancellationToken cancellationToken)
    {
        if (!IsValidTenantId(tenantId))
        {
            return new TenantLifecycleResult(TenantLifecycleOutcome.Invalid, null, "Invalid tenant id.");
        }

        var current = await _catalog.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new TenantLifecycleResult(TenantLifecycleOutcome.NotFound, null, "Tenant not found.");
        }

        var (next, error) = transition(current);
        if (next is null)
        {
            return new TenantLifecycleResult(TenantLifecycleOutcome.InvalidTransition, current, error);
        }

        var updated = await _catalog.UpdateAsync(next, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            // Lost a race with a concurrent delete/update; surface as not found.
            return new TenantLifecycleResult(TenantLifecycleOutcome.NotFound, null, "Tenant not found.");
        }

        await RecordAsync(actor, updated, action, AuditEventType.ConfigChange, cancellationToken)
            .ConfigureAwait(false);
        return new TenantLifecycleResult(TenantLifecycleOutcome.Updated, updated, null);
    }

    private Task<string?> RecordAsync(
        TenantLifecycleActor actor,
        TenantRecord tenant,
        string action,
        AuditEventType eventType,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            EventType = eventType,
            Actor = string.IsNullOrEmpty(actor.Actor) ? AuditEvent.AnonymousActor : actor.Actor,
            ActorType = actor.ActorType,
            ResourceType = "tenant",
            ResourceId = tenant.TenantId,
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId,
            RemoteIp = actor.RemoteIp,
            UserAgent = actor.UserAgent,
            Details = $"{{\"status\":\"{tenant.Status}\"}}",
        };

        return _auditLog.RecordAsync(auditEvent, cancellationToken);
    }

    private static bool IsValidTenantId(string tenantId)
        => !string.IsNullOrWhiteSpace(tenantId) && tenantId.Length <= MaxTenantIdLength;
}
