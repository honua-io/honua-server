// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Server.Features.OData;

/// <summary>
/// Defines allowed OData query parameters for different endpoint types.
/// Provides validation sets for various OData operation contexts.
/// </summary>
internal static class AllowedQueryParameters
{
    public static readonly FrozenSet<string> None =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Layer = new[]
        {
            "$select",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Feature = new[]
        {
            "$select",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> FeatureValue = new[]
        {
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> LayersCount = new[]
        {
            "$filter",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> FeaturesCount = new[]
        {
            "$filter",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Layers = new[]
        {
            "$filter",
            "$select",
            "$top",
            "$skip",
            "$skiptoken",
            "$count",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Features = new[]
        {
            "$filter",
            "$select",
            "$orderby",
            "$top",
            "$skip",
            "$skiptoken",
            "$count",
            "$expand",
            "$compute",
            "$apply",
            "$search",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Apply = new[]
        {
            "$apply",
            "$filter",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Search = new[]
        {
            "$search",
            "$filter",
            "$orderby",
            "$select",
            "$expand",
            "$top",
            "$skip",
            "$count",
            "$format"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
