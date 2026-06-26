// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// API response model for a provisioned tenant.
/// </summary>
public sealed class TenantResponse
{
    /// <summary>Tenant identifier.</summary>
    public required string TenantId { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Lifecycle status (Active, Suspended, Deleted).</summary>
    public required string Status { get; init; }

    /// <summary>Billing plan the tenant is enrolled in, if any.</summary>
    public string? Plan { get; init; }

    /// <summary>When the tenant was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the tenant record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the tenant was most recently suspended, if applicable.</summary>
    public DateTimeOffset? SuspendedAt { get; init; }

    /// <summary>When the tenant was deleted/retired, if applicable.</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>
/// Request to provision a new tenant.
/// </summary>
public sealed class CreateTenantRequest
{
    /// <summary>Stable opaque tenant identifier.</summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string TenantId { get; init; }

    /// <summary>Optional human-readable display name (defaults to the tenant id).</summary>
    [StringLength(256)]
    public string? DisplayName { get; init; }

    /// <summary>Optional billing plan to enroll the tenant in.</summary>
    [StringLength(64)]
    public string? Plan { get; init; }
}

/// <summary>
/// A single per-tenant metered usage record attributed for billing.
/// </summary>
public sealed class TenantUsageRecordResponse
{
    /// <summary>Tenant the usage is attributed to.</summary>
    public required string TenantId { get; init; }

    /// <summary>Metered billable request count.</summary>
    public required long RequestCount { get; init; }

    /// <summary>When the snapshot was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>
/// API response model for the per-tenant billing usage export.
/// </summary>
public sealed class TenantUsageResponse
{
    /// <summary>The per-tenant usage records.</summary>
    public required IReadOnlyList<TenantUsageRecordResponse> Records { get; init; }
}
