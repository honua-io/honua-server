// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;

namespace Honua.Core.Features.MultiTenancy.Lifecycle;

/// <summary>
/// Classifies the result of a tenant lifecycle operation so callers can map it to a transport
/// status without inspecting exception types.
/// </summary>
public enum TenantLifecycleOutcome
{
    /// <summary>A new tenant was created.</summary>
    Created = 0,

    /// <summary>An existing tenant transitioned to a new state.</summary>
    Updated = 1,

    /// <summary>The requested tenant does not exist.</summary>
    NotFound = 2,

    /// <summary>A tenant with the same id already exists.</summary>
    Conflict = 3,

    /// <summary>The requested transition is not valid from the tenant's current state.</summary>
    InvalidTransition = 4,

    /// <summary>The request was malformed (e.g. empty/oversized tenant id).</summary>
    Invalid = 5,
}

/// <summary>
/// Outcome of a tenant lifecycle operation, including the resulting record when successful.
/// </summary>
/// <param name="Outcome">The classified outcome.</param>
/// <param name="Tenant">The resulting tenant record, when the operation produced one.</param>
/// <param name="Error">A human-readable error message for non-success outcomes (never leaks internals).</param>
public readonly record struct TenantLifecycleResult(
    TenantLifecycleOutcome Outcome,
    TenantRecord? Tenant,
    string? Error)
{
    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool Success => Outcome is TenantLifecycleOutcome.Created or TenantLifecycleOutcome.Updated;
}

/// <summary>
/// The actor performing a tenant lifecycle operation, used to stamp the audit event. Built by the
/// admin endpoint from the request principal; carries no secrets.
/// </summary>
/// <param name="Actor">Actor identifier (user id, hashed api-key id, or service name).</param>
/// <param name="ActorType">Classification of the actor.</param>
/// <param name="CorrelationId">Correlation id for joining with logs/traces.</param>
/// <param name="RemoteIp">Remote IP of the caller, when policy allows capture.</param>
/// <param name="UserAgent">User-Agent of the caller, when available.</param>
public readonly record struct TenantLifecycleActor(
    string Actor,
    AuditActorType ActorType,
    string CorrelationId,
    string? RemoteIp = null,
    string? UserAgent = null)
{
    /// <summary>An anonymous/system actor fallback for contexts without a resolved principal.</summary>
    public static TenantLifecycleActor System(string correlationId) =>
        new("tenant-lifecycle", AuditActorType.System, correlationId);
}
