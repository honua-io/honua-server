// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Shared.Models;

public static class ExtentExtensions
{
    public static double[] ToBoundingBox(this FeatureExtent extent)
        => [extent.MinX, extent.MinY, extent.MaxX, extent.MaxY];

    public static double[][] ToBoundingBox2D(this FeatureExtent extent)
        => [[extent.MinX, extent.MinY], [extent.MaxX, extent.MaxY]];

    public static FeatureExtent ToFeatureExtent(this double[] bbox, int spatialReference)
    {
        if (bbox.Length != 4)
            throw new ArgumentException("Bounding box must have exactly 4 coordinates", nameof(bbox));

        return FeatureExtent.Create(bbox[0], bbox[1], bbox[2], bbox[3], spatialReference);
    }

    public static FeatureExtent ToFeatureExtent(this double[][] bbox, int spatialReference)
    {
        if (bbox.Length != 2 || bbox[0].Length != 2 || bbox[1].Length != 2)
            throw new ArgumentException("2D bounding box must have format [[minx, miny], [maxx, maxy]]", nameof(bbox));

        return FeatureExtent.Create(bbox[0][0], bbox[0][1], bbox[1][0], bbox[1][1], spatialReference);
    }

    extension(string? crsUri)
    {
        public int ExtractSridFromCrs()
        {
            if (TryExtractSridFromCrs(crsUri, out var srid))
            {
                return srid;
            }

            throw new FormatException("CRS must be a supported EPSG or CRS84 identifier.");
        }
    }

    public static bool TryExtractSridFromCrs(string? crsUri, out int srid)
    {
        srid = default;
        if (string.IsNullOrWhiteSpace(crsUri))
        {
            return false;
        }

        var trimmed = crsUri.Trim();
        // PA-028: Normalise https:// → http:// before any matching to handle servers that
        // emit https:// Content-Crs headers (e.g. OgcMapsRenderingHandler.FormatContentCrsHeader).
        var schemeNormalized = trimmed.StartsWith("https://www.opengis.net/", StringComparison.OrdinalIgnoreCase)
            ? "http://" + trimmed["https://".Length..]
            : trimmed;

        if (schemeNormalized.Equals(GetDefaultCrsUri(), StringComparison.OrdinalIgnoreCase) ||
            schemeNormalized.Equals("CRS84", StringComparison.OrdinalIgnoreCase) ||
            schemeNormalized.Equals("OGC:CRS84", StringComparison.OrdinalIgnoreCase))
        {
            srid = 4326;
            return true;
        }

        ReadOnlySpan<char> span = schemeNormalized;
        const string epsgMarker = "/EPSG/";

        var epsgIndex = span.IndexOf(epsgMarker.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (epsgIndex >= 0)
        {
            var sridSpan = span[(epsgIndex + epsgMarker.Length)..];
            var slashIndex = sridSpan.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                sridSpan = sridSpan[(slashIndex + 1)..];
            }

            if (int.TryParse(sridSpan, out srid))
            {
                return true;
            }
        }

        if (span.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var sridSpan = span["EPSG:".Length..];
            return int.TryParse(sridSpan, out srid);
        }

        return false;
    }

    public static string ToOgcCrsUri(int srid)
        => $"http://www.opengis.net/def/crs/EPSG/0/{srid}";

    public static string GetDefaultCrsUri()
        => "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}
