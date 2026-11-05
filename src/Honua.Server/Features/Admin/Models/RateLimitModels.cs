// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class RateLimitPolicyResponse
{
    public required Guid PolicyId { get; init; }

    public required string Name { get; init; }

    public required string Scope { get; init; }

    public required string Key { get; init; }

    public required int RequestsPerWindow { get; init; }

    public required double WindowDurationSeconds { get; init; }

    public bool Enabled { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CreateRateLimitPolicyRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required]
    public required string Scope { get; init; }

    [Required]
    public required string Key { get; init; }

    [Range(1, 1_000_000)]
    public required int RequestsPerWindow { get; init; }

    [Range(1, 86400)]
    public required double WindowDurationSeconds { get; init; }

    public bool Enabled { get; init; } = true;
}

public sealed class UpdateRateLimitPolicyRequest
{
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; init; }

    [Range(1, 1_000_000)]
    public int? RequestsPerWindow { get; init; }

    [Range(1, 86400)]
    public double? WindowDurationSeconds { get; init; }

    public bool? Enabled { get; init; }
}

public sealed class RateLimitStatusResponse
{
    public required Guid PolicyId { get; init; }

    public required string Key { get; init; }

    public required int RequestsConsumed { get; init; }

    public required int RequestsRemaining { get; init; }

    public required DateTimeOffset WindowResetsAt { get; init; }
}
