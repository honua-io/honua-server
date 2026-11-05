// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;

namespace Honua.Core.Features.MultiTenancy.Lifecycle;

public enum TenantLifecycleOutcome
{
    Created = 0,

    Updated = 1,

    NotFound = 2,

    Conflict = 3,

    InvalidTransition = 4,

    Invalid = 5,
}

public readonly record struct TenantLifecycleResult(
    TenantLifecycleOutcome Outcome,
    TenantRecord? Tenant,
    string? Error)
{
    public bool Success => Outcome is TenantLifecycleOutcome.Created or TenantLifecycleOutcome.Updated;
}

public readonly record struct TenantLifecycleActor(
    string Actor,
    AuditActorType ActorType,
    string CorrelationId,
    string? RemoteIp = null,
    string? UserAgent = null)
{
    public static TenantLifecycleActor System(string correlationId) =>
        new("tenant-lifecycle", AuditActorType.System, correlationId);
}
