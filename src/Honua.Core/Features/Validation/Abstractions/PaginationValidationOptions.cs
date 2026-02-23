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
    /// Default options for generic pagination validation.
    /// </summary>
    public static PaginationValidationOptions Default { get; } = new(0, 1, "Offset", "Limit");
}
