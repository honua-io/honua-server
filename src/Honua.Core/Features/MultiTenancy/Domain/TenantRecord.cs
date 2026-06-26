// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Domain;

/// <summary>
/// A provisioned tenant and its lifecycle state (issue #2156). Persisted by an
/// <see cref="Abstractions.ITenantCatalog"/>; the request rail's
/// <see cref="Abstractions.ITenantContext"/> resolves the active tenant id, and this record
/// carries the durable provisioning/billing metadata behind it.
/// </summary>
public sealed record TenantRecord
{
    /// <summary>Stable opaque tenant identifier (never a secret).</summary>
    public required string TenantId { get; init; }

    /// <summary>Human-readable display name for operators.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required TenantStatus Status { get; init; }

    /// <summary>Billing plan the tenant is enrolled in, if any (drives per-plan quotas/billing).</summary>
    public string? Plan { get; init; }

    /// <summary>When the tenant was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the tenant record was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the tenant was most recently suspended, if currently/previously suspended.</summary>
    public DateTimeOffset? SuspendedAt { get; init; }

    /// <summary>When the tenant was deleted/retired, if applicable.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>
    /// The status the tenant held immediately before a suspension, so a resume can restore the
    /// prior state. Defaults to <see cref="TenantStatus.Active"/> for the common case.
    /// </summary>
    public TenantStatus PriorStatus { get; init; } = TenantStatus.Active;
}
