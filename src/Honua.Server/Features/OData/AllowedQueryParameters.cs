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

    public static readonly FrozenSet<string> Layers = new[]
        {
            "$filter",
            "$select",
            "$top",
            "$skip",
            "$count"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Features = new[]
        {
            "$filter",
            "$select",
            "$orderby",
            "$top",
            "$skip",
            "$count",
            "$expand"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Apply = new[]
        {
            "$apply",
            "$filter"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Search = new[]
        {
            "$search",
            "$top",
            "$skip",
            "$count"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
