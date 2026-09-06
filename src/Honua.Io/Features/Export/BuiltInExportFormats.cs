// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Io.Export;

internal enum BuiltInExportFormat
{
    Csv,
    Shapefile,
    GeoPackage
}

/// <summary>The dispatch keys shared by synchronous and durable file exports.</summary>
internal static class BuiltInExportFormats
{
    internal static readonly FrozenDictionary<string, BuiltInExportFormat> Dispatch =
        new Dictionary<string, BuiltInExportFormat>(StringComparer.OrdinalIgnoreCase)
        {
            ["csv"] = BuiltInExportFormat.Csv,
            ["shapefile"] = BuiltInExportFormat.Shapefile,
            ["gpkg"] = BuiltInExportFormat.GeoPackage
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static BuiltInExportFormat? Resolve(string? token) =>
        token is not null && Dispatch.TryGetValue(token, out var format) ? format : null;
}
