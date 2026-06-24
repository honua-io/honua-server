// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// API response model for a field-level security (column masking) policy (#1940).
/// </summary>
public sealed class FieldMaskPolicyResponse
{
    /// <summary>Policy identifier.</summary>
    public required Guid PolicyId { get; init; }

    /// <summary>Role name the policy applies to, or "*".</summary>
    public required string Role { get; init; }

    /// <summary>Service name the policy applies to, or "*".</summary>
    public required string Service { get; init; }

    /// <summary>Layer identifier the policy applies to, or "*".</summary>
    public required string Layer { get; init; }

    /// <summary>Layer attribute (column) that is masked from query output.</summary>
    public required string Attribute { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>When the policy was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the policy was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Request to create a field-level security (column masking) policy.
/// </summary>
public sealed class CreateFieldMaskPolicyRequest
{
    /// <summary>Role name the policy applies to, or "*" for any role.</summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Role { get; init; }

    /// <summary>Service name the policy applies to, or "*" for any service.</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Service { get; init; }

    /// <summary>Layer identifier the policy applies to, or "*" for any layer.</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Layer { get; init; }

    /// <summary>Layer attribute (column) to mask from query output (e.g. "ssn").</summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Attribute { get; init; }

    /// <summary>Optional description of the policy's intent.</summary>
    [StringLength(500)]
    public string? Description { get; init; }
}
