// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Validation response for a persisted layer and its backing storage.
/// </summary>
internal sealed record LayerValidationResponse
{
    public int LayerId { get; init; }

    public required string LayerName { get; init; }

    public required string Status { get; init; }

    public bool IsValid { get; init; }

    public string? StorageSchema { get; init; }

    public string? StorageTable { get; init; }

    public DateTimeOffset CheckedAt { get; init; }

    public LayerValidationCheck[] Checks { get; init; } = [];
}

/// <summary>
/// Individual validation check result.
/// </summary>
internal sealed record LayerValidationCheck
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Message { get; init; }

    public string? Expected { get; init; }

    public string? Actual { get; init; }
}
