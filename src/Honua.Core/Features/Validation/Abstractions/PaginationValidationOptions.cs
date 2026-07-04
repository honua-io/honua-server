// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Validation.Abstractions;

/// <summary>
/// Validation options for pagination parameters.
/// </summary>
public sealed record PaginationValidationOptions(
    int MinOffset,
    int MinLimit,
    string OffsetParameterName,
    string LimitParameterName)
{
    /// <summary>
    /// When <see langword="true"/>, a limit above the configured maximum record count is clamped
    /// to the maximum instead of failing validation. When <see langword="false"/> (the default for
    /// every preset, including <see cref="Default"/>), an over-maximum limit fails validation and
    /// the adapter surfaces a 400. Honua's GeoServices, OData, and gRPC surfaces reject
    /// over-maximum limits with a 400 rather than silently clamping, so that callers learn their
    /// page size was not honored. OGC API - Features is the exception: OGC API - Features Part 1
    /// requirement /req/core/fc-limit-definition (d) mandates clamping to the server maximum, so
    /// its preset sets this flag. Values below <see cref="MinLimit"/> always fail validation
    /// regardless of this flag.
    /// </summary>
    public bool ClampLimitToMaximum { get; init; }

    /// <summary>
    /// Default options for generic pagination validation. Over-maximum limits fail validation
    /// (surfaced as a 400) rather than being silently clamped.
    /// </summary>
    public static PaginationValidationOptions Default { get; } = new(0, 1, "Offset", "Limit");

    /// <summary>
    /// OGC API Features pagination options. OGC API - Features Part 1 requirement
    /// /req/core/fc-limit-definition (d) mandates that over-maximum limits are silently clamped
    /// to the server maximum rather than rejected with a 400.
    /// </summary>
    public static PaginationValidationOptions OgcFeatures { get; } =
        new(0, 1, "offset", "limit") { ClampLimitToMaximum = true };
}
