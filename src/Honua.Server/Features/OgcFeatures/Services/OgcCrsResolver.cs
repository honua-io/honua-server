// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;

namespace Honua.Server.Features.OgcFeatures.Services;

/// <summary>
/// Resolves and validates coordinate reference systems (CRS) for OGC Features operations.
/// </summary>
internal static class OgcCrsResolver
{
    /// <summary>
    /// Result of CRS resolution operation.
    /// </summary>
    public sealed class CrsResolutionResult
    {
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public OgcFeaturesUtilities.CrsDefinition CrsDefinition { get; init; }

        public static CrsResolutionResult Success(OgcFeaturesUtilities.CrsDefinition crsDefinition) => new()
        {
            IsSuccess = true,
            CrsDefinition = crsDefinition
        };

        public static CrsResolutionResult Failure(string errorMessage) => new()
        {
            IsSuccess = false,
            ErrorMessage = errorMessage
        };
    }

    private static readonly FrozenDictionary<string, OgcFeaturesUtilities.CrsDefinition> _supportedCrs
        = new Dictionary<string, OgcFeaturesUtilities.CrsDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [OgcFeaturesUtilities.Crs84Uri] = new OgcFeaturesUtilities.CrsDefinition(
                OgcFeaturesUtilities.Crs84Uri,
                4326,
                OgcFeaturesUtilities.AxisOrder.EastNorth,
                true),
            [OgcFeaturesUtilities.Epsg4326Uri] = new OgcFeaturesUtilities.CrsDefinition(
                OgcFeaturesUtilities.Epsg4326Uri,
                4326,
                OgcFeaturesUtilities.AxisOrder.NorthEast,
                true),
            // Add support for common Web Mercator
            ["http://www.opengis.net/def/crs/EPSG/0/3857"] = new OgcFeaturesUtilities.CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/3857",
                3857,
                OgcFeaturesUtilities.AxisOrder.EastNorth,
                false),
            // Add support for common UTM zones
            ["http://www.opengis.net/def/crs/EPSG/0/32633"] = new OgcFeaturesUtilities.CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/32633",
                32633,
                OgcFeaturesUtilities.AxisOrder.EastNorth,
                false)
        }.ToFrozenDictionary();

    /// <summary>
    /// Resolves CRS identifier to CRS definition with validation.
    /// </summary>
    public static CrsResolutionResult TryResolveCrs(string? crs)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            // Default to CRS84 (WGS84 with longitude-latitude order)
            return CrsResolutionResult.Success(_supportedCrs[OgcFeaturesUtilities.Crs84Uri]);
        }

        var normalizedCrs = NormalizeCrsIdentifier(crs);

        if (_supportedCrs.TryGetValue(normalizedCrs, out var definition))
        {
            return CrsResolutionResult.Success(definition);
        }

        // Try to parse EPSG codes directly
        if (TryParseEpsgCode(normalizedCrs, out var epsgCode))
        {
            var dynamicDefinition = CreateDynamicCrsDefinition(epsgCode);
            if (dynamicDefinition != null)
            {
                return CrsResolutionResult.Success(dynamicDefinition.Value);
            }
        }

        return CrsResolutionResult.Failure($"Unsupported CRS '{crs}'. Supported CRS identifiers: {GetSupportedCrsNames()}");
    }

    /// <summary>
    /// Gets the default CRS definition (CRS84).
    /// </summary>
    public static OgcFeaturesUtilities.CrsDefinition GetDefaultCrs()
    {
        return _supportedCrs[OgcFeaturesUtilities.Crs84Uri];
    }

    /// <summary>
    /// Gets all supported CRS definitions.
    /// </summary>
    public static IReadOnlyDictionary<string, OgcFeaturesUtilities.CrsDefinition> GetSupportedCrs()
    {
        return _supportedCrs;
    }

    /// <summary>
    /// Gets the supported CRS URIs for metadata advertisement.
    /// </summary>
    public static ImmutableArray<string> GetSupportedCrsUris()
    {
        var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var uri in _supportedCrs.Keys)
        {
            uris.Add(uri);
        }

        foreach (var epsgCode in EnumerateSupportedEpsgCodes())
        {
            uris.Add(ConvertEpsgToUri(epsgCode.ToString(CultureInfo.InvariantCulture)));
        }

        return uris.OrderBy(static uri => uri, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    /// <summary>
    /// Checks if a CRS is supported.
    /// </summary>
    public static bool IsCrsSupported(string? crs)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            return true; // Default CRS is always supported
        }

        var normalizedCrs = NormalizeCrsIdentifier(crs);
        return _supportedCrs.ContainsKey(normalizedCrs) ||
               TryParseEpsgCode(normalizedCrs, out var epsgCode) && IsEpsgCodeSupported(epsgCode);
    }

    /// <summary>
    /// Validates CRS compatibility for operations.
    /// </summary>
    public static bool AreCrsCompatible(OgcFeaturesUtilities.CrsDefinition source, OgcFeaturesUtilities.CrsDefinition target)
    {
        // Same SRID means compatible
        if (source.Srid == target.Srid)
        {
            return true;
        }

        // Geographic coordinate systems (4326-based) are generally compatible
        if (IsGeographicCrs(source.Srid) && IsGeographicCrs(target.Srid))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the axis order transformation requirements between CRS definitions.
    /// </summary>
    public static bool RequiresAxisOrderTransformation(
        OgcFeaturesUtilities.CrsDefinition source,
        OgcFeaturesUtilities.CrsDefinition target)
    {
        return source.AxisOrder != target.AxisOrder;
    }

    private static string NormalizeCrsIdentifier(string crs)
    {
        var trimmed = crs.Trim();

        // Handle common variations
        return trimmed switch
        {
            var uri when uri.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) =>
                ConvertEpsgToUri(uri[5..]),
            var code when IsNumericEpsgCode(code) =>
                ConvertEpsgToUri(code),
            _ => trimmed
        };
    }

    private static bool TryParseEpsgCode(string identifier, out int epsgCode)
    {
        epsgCode = 0;

        // Handle URI format: http://www.opengis.net/def/crs/EPSG/0/4326
        if (identifier.StartsWith("http://www.opengis.net/def/crs/EPSG/0/", StringComparison.OrdinalIgnoreCase))
        {
            var codeStr = identifier["http://www.opengis.net/def/crs/EPSG/0/".Length..];
            return int.TryParse(codeStr, out epsgCode);
        }

        // Handle direct numeric codes
        if (IsNumericEpsgCode(identifier))
        {
            return int.TryParse(identifier, out epsgCode);
        }

        return false;
    }

    private static bool IsNumericEpsgCode(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(char.IsDigit) &&
               value.Length is >= 3 and <= 6; // EPSG codes are typically 3-6 digits
    }

    private static string ConvertEpsgToUri(string epsgCode)
    {
        return $"http://www.opengis.net/def/crs/EPSG/0/{epsgCode}";
    }

    private static OgcFeaturesUtilities.CrsDefinition? CreateDynamicCrsDefinition(int epsgCode)
    {
        if (!IsEpsgCodeSupported(epsgCode))
        {
            return null;
        }

        var uri = ConvertEpsgToUri(epsgCode.ToString());
        var axisOrder = DetermineAxisOrder(epsgCode);

        var isGeographic = IsGeographicCrs(epsgCode);
        return new OgcFeaturesUtilities.CrsDefinition(uri, epsgCode, axisOrder, isGeographic);
    }

    private static bool IsEpsgCodeSupported(int epsgCode)
    {
        // Support common coordinate systems
        return epsgCode switch
        {
            4326 => true,  // WGS84
            3857 => true,  // Web Mercator
            >= 32601 and <= 32660 => true,  // UTM North
            >= 32701 and <= 32760 => true,  // UTM South
            2154 => true,  // RGF93 / Lambert-93 (France)
            25832 => true, // ETRS89 / UTM zone 32N
            25833 => true, // ETRS89 / UTM zone 33N
            _ => false
        };
    }

    private static IEnumerable<int> EnumerateSupportedEpsgCodes()
    {
        yield return 4326;
        yield return 3857;
        yield return 2154;
        yield return 25832;
        yield return 25833;

        for (var code = 32601; code <= 32660; code++)
        {
            yield return code;
        }

        for (var code = 32701; code <= 32760; code++)
        {
            yield return code;
        }
    }

    private static OgcFeaturesUtilities.AxisOrder DetermineAxisOrder(int epsgCode)
    {
        // Geographic coordinate systems typically use latitude-longitude order in EPSG definitions
        // but many systems expect longitude-latitude for web usage
        return epsgCode switch
        {
            4326 => OgcFeaturesUtilities.AxisOrder.NorthEast, // Official EPSG:4326 is lat-lon
            3857 => OgcFeaturesUtilities.AxisOrder.EastNorth, // Web Mercator is x-y (easting-northing)
            >= 32601 and <= 32760 => OgcFeaturesUtilities.AxisOrder.EastNorth, // UTM zones
            _ => OgcFeaturesUtilities.AxisOrder.EastNorth // Default to easting-northing for projected systems
        };
    }

    private static bool IsGeographicCrs(int srid)
    {
        return srid switch
        {
            4326 => true,
            4269 => true, // NAD83
            4267 => true, // NAD27
            >= 4000 and <= 4999 => true, // Most geographic coordinate systems are in 4000s
            _ => false
        };
    }

    private static string GetSupportedCrsNames()
    {
        var names = _supportedCrs.Keys.Take(3).ToArray();
        var additional = _supportedCrs.Count > 3 ? $" and {_supportedCrs.Count - 3} more" : "";
        return string.Join(", ", names) + additional;
    }

    /// <summary>
    /// Validates CRS for specific operation types.
    /// </summary>
    public static bool ValidateCrsForOperation(OgcFeaturesUtilities.CrsDefinition crs, string operationType)
    {
        return operationType.ToUpperInvariant() switch
        {
            "QUERY" => true, // All supported CRS are valid for queries
            "CREATE" => IsWriteOperationCrsSupported(crs),
            "UPDATE" => IsWriteOperationCrsSupported(crs),
            "DELETE" => true, // CRS doesn't affect delete operations
            _ => true
        };
    }

    private static bool IsWriteOperationCrsSupported(OgcFeaturesUtilities.CrsDefinition crs)
    {
        // For write operations, we might want to restrict to specific CRS to ensure data consistency
        // For now, allow all supported CRS
        return true;
    }

    /// <summary>
    /// Gets the precision requirements for a CRS.
    /// </summary>
    public static (double CoordinatePrecision, int DecimalPlaces) GetCrsPrecision(OgcFeaturesUtilities.CrsDefinition crs)
    {
        return crs.Srid switch
        {
            4326 => (0.000001, 6), // Geographic: ~0.1m precision with 6 decimal places
            3857 => (0.01, 2), // Web Mercator: cm precision
            >= 32601 and <= 32760 => (0.01, 2), // UTM: cm precision
            _ => (0.001, 3) // Default: mm precision
        };
    }
}
