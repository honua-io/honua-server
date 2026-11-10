// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.ObjectStore;

public static class ImportObjectStoreFormats
{
    public static string FileNameFor(string key, string? contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var leaf = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        if (string.IsNullOrWhiteSpace(leaf))
        {
            leaf = "object";
        }

        if (leaf.EndsWith(".gdb.zip", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(leaf).Length > 0)
        {
            return leaf;
        }

        var extension = ExtensionForContentType(contentType);
        return extension is null ? leaf : leaf + extension;
    }

    public static bool IsArchive(string fileName)
        => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase);

    public static bool NeedsRandomAccess(string fileName)
        => IsArchive(fileName)
           || HasExtension(fileName, ".gpkg", ".parquet", ".geoparquet", ".fgb", ".shp");

    private static bool HasExtension(string fileName, params ReadOnlySpan<string> extensions)
    {
        foreach (var extension in extensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtensionForContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var media = contentType.Split(';', 2)[0].Trim();
        return media.ToLowerInvariant() switch
        {
            "application/geo+json" or "application/vnd.geo+json" => ".geojson",
            "application/json" => ".json",
            "text/csv" or "application/csv" => ".csv",
            "application/gml+xml" => ".gml",
            "application/vnd.google-earth.kml+xml" => ".kml",
            "application/vnd.google-earth.kmz" => ".kmz",
            "application/zip" or "application/x-zip-compressed" => ".zip",
            "application/geopackage+sqlite3" => ".gpkg",
            "application/vnd.apache.parquet" => ".parquet",
            "application/flatgeobuf" => ".fgb",
            _ => null,
        };
    }
}
