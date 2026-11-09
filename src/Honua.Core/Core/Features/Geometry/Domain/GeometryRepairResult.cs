// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Geometry.Domain;

public sealed record GeometryRepairResult
{
    public required bool Success { get; init; }

    public byte[]? RepairedWkb { get; init; }

    public ImmutableList<string> RepairsApplied { get; init; } = ImmutableList<string>.Empty;

    public string? ErrorMessage { get; init; }

    public string? OriginalValidationReason { get; init; }

    public bool GeometryTypeChanged { get; init; }

    public static GeometryRepairResult Repaired(byte[] repairedWkb, IEnumerable<string>? repairs = null) => new()
    {
        Success = true,
        RepairedWkb = repairedWkb,
        RepairsApplied = repairs?.ToImmutableList() ?? ImmutableList<string>.Empty
    };

    public static GeometryRepairResult Failed(string errorMessage, string? originalReason = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        OriginalValidationReason = originalReason
    };

    public static GeometryRepairResult NotNeeded(byte[] originalWkb) => new()
    {
        Success = true,
        RepairedWkb = originalWkb
    };
}
