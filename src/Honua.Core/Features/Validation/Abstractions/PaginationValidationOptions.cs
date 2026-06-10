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
    /// to the maximum instead of failing validation. OGC API - Features Part 1
    /// (/req/core/fc-limit-response-1) requires that an over-maximum limit "SHALL NOT result in
    /// an error (instead use the maximum as the parameter value)". Values below
    /// <see cref="MinLimit"/> still fail validation. Protocol presets that want a hard 400 on
    /// over-maximum limits (GeoServices, OData, gRPC) leave this disabled.
    /// </summary>
    public bool ClampLimitToMaximum { get; init; }

    /// <summary>
    /// Default options for generic pagination validation. Over-maximum limits are clamped to the
    /// configured maximum record count rather than rejected — the behavior OGC API - Features
    /// requires for the <c>limit</c> parameter on items requests.
    /// </summary>
    public static PaginationValidationOptions Default { get; } = new(0, 1, "Offset", "Limit")
    {
        ClampLimitToMaximum = true
    };
}
