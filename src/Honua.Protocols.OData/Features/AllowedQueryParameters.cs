// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Protocols.OData;

/// <summary>
/// Defines allowed OData query parameters for different endpoint types.
/// Provides validation sets for various OData operation contexts.
/// </summary>
internal static class AllowedQueryParameters
{
    public static readonly FrozenSet<string> None =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Layer = ODataSystemOptions(
        "$select",
        "$format");

    public static readonly FrozenSet<string> Feature = ODataSystemOptions(
        "$select",
        "$format");

    public static readonly FrozenSet<string> FeatureValue = ODataSystemOptions("$format");

    public static readonly FrozenSet<string> LayersCount = ODataSystemOptions(
        "$filter",
        "$format");

    public static readonly FrozenSet<string> FeaturesCount = ODataSystemOptions(
        "$filter",
        "$format");

    public static readonly FrozenSet<string> Layers = ODataSystemOptions(
        "$filter",
        "$select",
        "$orderby",
        "$top",
        "$skip",
        "$skiptoken",
        "$count",
        "$expand",
        "$format");

    public static readonly FrozenSet<string> Features = ODataSystemOptions(
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
        "$deltatoken",
        "$format",
        "bbox",
        "honua_track_changes",
        "honua_track_changes_snapshot");

    public static readonly FrozenSet<string> Apply = ODataSystemOptions(
        "$apply",
        "$filter",
        "$format");

    public static readonly FrozenSet<string> Search = ODataSystemOptions(
        "$search",
        "$filter",
        "$orderby",
        "$select",
        "$expand",
        "$top",
        "$skip",
        "$count",
        "$format");

    private static FrozenSet<string> ODataSystemOptions(params string[] names)
        => names
            .SelectMany(static name => name.StartsWith('$')
                ? new[] { name, name[1..] }
                : new[] { name })
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
