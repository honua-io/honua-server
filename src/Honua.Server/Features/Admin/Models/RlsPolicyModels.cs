// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class RlsPolicyResponse
{
    public required Guid PolicyId { get; init; }

    public required string Role { get; init; }

    public required string Service { get; init; }

    public required string Layer { get; init; }

    public required string Attribute { get; init; }

    public required string ClaimType { get; init; }

    public required string Comparison { get; init; }

    public string? Description { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CreateRlsPolicyRequest
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Role { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Service { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Layer { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Attribute { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string ClaimType { get; init; }

    [StringLength(16)]
    public string? Comparison { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}
