// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Reference to a style applied within a package.
/// </summary>
public sealed record StyleRef
{
    /// <summary>
    /// Identifier of the referenced style resource.
    /// </summary>
    public required string StyleId { get; init; }

    /// <summary>
    /// Optional display label for the style.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Optional reusable style preset identifier.
    /// </summary>
    public string? PresetId { get; init; }
}
