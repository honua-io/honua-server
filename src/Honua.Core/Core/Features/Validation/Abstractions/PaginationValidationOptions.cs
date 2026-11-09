// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Validation.Abstractions;

public sealed record PaginationValidationOptions(
    int MinOffset,
    int MinLimit,
    string OffsetParameterName,
    string LimitParameterName)
{
    // When clamping is off, a limit above the maximum fails validation so the caller sees that the page size was not honored.
    public bool ClampLimitToMaximum { get; init; }

    public static PaginationValidationOptions Default { get; } = new(0, 1, "Offset", "Limit");

    // OGC API Features requires an over-maximum limit to be clamped to the server maximum rather than rejected.
    public static PaginationValidationOptions OgcFeatures { get; } =
        new(0, 1, "offset", "limit") { ClampLimitToMaximum = true };
}
