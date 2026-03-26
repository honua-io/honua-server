// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Server.Features.Ogc.Common;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// Utilities and constants for WFS 2.0 implementation
/// </summary>
internal static class Wfs20Utilities
{
    /// <summary>
    /// WFS 2.0 specification version
    /// </summary>
    public const string Version = "2.0.0";

    /// <summary>
    /// WFS service type identifier
    /// </summary>
    public const string ServiceType = "WFS";

    /// <summary>
    /// Current capabilities update sequence.
    /// </summary>
    public const string CurrentUpdateSequence = "20260325";

    /// <summary>
    /// Default namespace for WFS 2.0
    /// </summary>
    public const string WfsNamespace = "http://www.opengis.net/wfs/2.0";

    /// <summary>
    /// Filter Encoding 2.0 namespace
    /// </summary>
    public const string FesNamespace = "http://www.opengis.net/fes/2.0";

    /// <summary>
    /// GML 3.2 namespace
    /// </summary>
    public const string GmlNamespace = "http://www.opengis.net/gml/3.2";

    /// <summary>
    /// OWS namespace for common elements
    /// </summary>
    public const string OwsNamespace = "http://www.opengis.net/ows/1.1";

    /// <summary>
    /// XLink namespace for links
    /// </summary>
    public const string XLinkNamespace = "http://www.w3.org/1999/xlink";

    /// <summary>
    /// XML Schema instance namespace
    /// </summary>
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Supported WFS 2.0 operations
    /// </summary>
    public static class Operations
    {
        public const string GetCapabilities = "GetCapabilities";
        public const string DescribeFeatureType = "DescribeFeatureType";
        public const string GetFeature = "GetFeature";
        public const string GetPropertyValue = "GetPropertyValue";
        public const string Transaction = "Transaction";
        public const string LockFeature = "LockFeature";
        public const string GetFeatureWithLock = "GetFeatureWithLock";
    }

    /// <summary>
    /// Supported output formats
    /// </summary>
    public static class OutputFormats
    {
        public const string Gml32 = MediaTypes.Gml;
        public const string Gml32Simplified = "GML3.2";
        public const string GeoJson = "application/geo+json";
        public const string Csv = "text/csv";
        public const string Json = "application/json";

        /// <summary>
        /// Default output format for WFS 2.0
        /// </summary>
        public const string Default = Gml32;

        /// <summary>
        /// All supported formats
        /// </summary>
        public static readonly ImmutableArray<string> All = ImmutableArray.Create(
            Gml32,
            Gml32Simplified,
            GeoJson,
            Csv,
            Json);

        /// <summary>
        /// Normalizes output format strings to standard MIME types
        /// </summary>
        public static string NormalizeOutputFormat(string? format)
        {
            if (string.IsNullOrEmpty(format))
                return Default;

            return format.ToLowerInvariant() switch
            {
                "gml3.2" or "gml32" or "gml" => Gml32,
                "application/gml+xml" => Gml32,
                "application/gml+xml;version=3.2" => Gml32,
                "application/gml+xml; version=3.2" => Gml32,
                "application/xml" or "text/xml" => Gml32,
                "geojson" or "application/geo+json" => GeoJson,
                "json" or "application/json" => Json,
                "csv" or "text/csv" => Csv,
                _ => format // Pass through unknown formats
            };
        }
    }

    /// <summary>
    /// Parameter names for WFS 2.0 operations
    /// </summary>
    public static class ParameterNames
    {
        // Common parameters
        public const string Service = "SERVICE";
        public const string Version = "VERSION";
        public const string Request = "REQUEST";
        public const string Namespace = "NAMESPACE";

        // GetCapabilities parameters
        public const string AcceptVersions = "ACCEPTVERSIONS";
        public const string Sections = "SECTIONS";
        public const string AcceptFormats = "ACCEPTFORMATS";
        public const string UpdateSequence = "UPDATESEQUENCE";

        // DescribeFeatureType parameters
        public const string TypeNames = "TYPENAMES";
        public const string TypeName = "TYPENAME";
        public const string OutputFormat = "OUTPUTFORMAT";

        // GetFeature parameters
        public const string Count = "COUNT";
        public const string MaxFeatures = "MAXFEATURES";
        public const string StartIndex = "STARTINDEX";
        public const string SortBy = "SORTBY";
        public const string Filter = "FILTER";
        public const string BBox = "BBOX";
        public const string ResourceId = "RESOURCEID";
        public const string FeatureId = "FEATUREID";
        public const string ResultType = "RESULTTYPE";
        public const string StoredQueryId = "STOREDQUERY_ID";
        public const string PropertyName = "PROPERTYNAME";
        public const string ValueReference = "VALUEREFERENCE";
        public const string Srs = "SRS";
        public const string SrsName = "SRSNAME";

        // Transaction parameters
        public const string LockId = "LOCKID";
        public const string ReleaseAction = "RELEASEACTION";
    }

    /// <summary>
    /// Allowed query parameters for different operation types
    /// </summary>
    public static class AllowedQueryParameters
    {
        public static readonly ImmutableHashSet<string> GetCapabilities = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ParameterNames.Service,
            ParameterNames.Version,
            ParameterNames.Request,
            ParameterNames.AcceptVersions,
            ParameterNames.Sections,
            ParameterNames.AcceptFormats,
            ParameterNames.UpdateSequence);

        public static readonly ImmutableHashSet<string> DescribeFeatureType = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ParameterNames.Service,
            ParameterNames.Version,
            ParameterNames.Request,
            ParameterNames.TypeNames,
            ParameterNames.TypeName,
            ParameterNames.Namespace,
            ParameterNames.OutputFormat);

        public static readonly ImmutableHashSet<string> GetFeature = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ParameterNames.Service,
            ParameterNames.Version,
            ParameterNames.Request,
            ParameterNames.TypeNames,
            ParameterNames.TypeName,
            ParameterNames.Namespace,
            ParameterNames.OutputFormat,
            ParameterNames.Count,
            ParameterNames.MaxFeatures,
            ParameterNames.StartIndex,
            ParameterNames.SortBy,
            ParameterNames.Filter,
            ParameterNames.BBox,
            ParameterNames.ResourceId,
            ParameterNames.FeatureId,
            ParameterNames.ResultType,
            ParameterNames.StoredQueryId,
            ParameterNames.PropertyName,
            ParameterNames.Srs,
            ParameterNames.SrsName);

        public static readonly ImmutableHashSet<string> GetPropertyValue = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ParameterNames.Service,
            ParameterNames.Version,
            ParameterNames.Request,
            ParameterNames.TypeNames,
            ParameterNames.TypeName,
            ParameterNames.Namespace,
            ParameterNames.OutputFormat,
            ParameterNames.Count,
            ParameterNames.MaxFeatures,
            ParameterNames.StartIndex,
            ParameterNames.Filter,
            ParameterNames.BBox,
            ParameterNames.ResourceId,
            ParameterNames.FeatureId,
            ParameterNames.PropertyName,
            ParameterNames.ValueReference,
            ParameterNames.Srs,
            ParameterNames.SrsName);
    }

    /// <summary>
    /// Supported GetCapabilities section names.
    /// </summary>
    public static readonly ImmutableHashSet<string> GetCapabilitiesSections = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "All",
        "ServiceIdentification",
        "ServiceProvider",
        "OperationsMetadata",
        "FeatureTypeList",
        "Filter_Capabilities");

    /// <summary>
    /// WFS 2.0 conformance classes
    /// </summary>
    public static class ConformanceClasses
    {
        public const string Core = "http://www.opengis.net/spec/wfs-2/2.0/conf/Core";
        public const string KvpEncoding = "http://www.opengis.net/spec/wfs-2/2.0/conf/KVP-WFS";
        public const string XmlEncoding = "http://www.opengis.net/spec/wfs-2/2.0/conf/XML-WFS";
        public const string BasicWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Basic-WFS";
        public const string TransactionalWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Transactional-WFS";
        public const string LockingWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Locking-WFS";
        public const string InheritanceWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Inheritance-WFS";
        public const string RemoteResolveWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Remote-Resolve-WFS";
        public const string ResponsePagingWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Response-Paging-WFS";
        public const string StandardJoinsWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Standard-Joins-WFS";
        public const string SpatialJoinsWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Spatial-Joins-WFS";
        public const string TemporalJoinsWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Temporal-Joins-WFS";
        public const string FeatureVersionsWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Feature-Versions-WFS";
        public const string ManageStoredQueriesWfs = "http://www.opengis.net/spec/wfs-2/2.0/conf/Manage-Stored-Queries-WFS";

        // FES 2.0 conformance classes
        public const string QueryFilter = "http://www.opengis.net/spec/filter-2/2.0/conf/Query";
        public const string AdHocQuery = "http://www.opengis.net/spec/filter-2/2.0/conf/AdHocQuery";
        public const string ResourceIdentification = "http://www.opengis.net/spec/filter-2/2.0/conf/ResourceIdentification";
        public const string MinimumStandardFilter = "http://www.opengis.net/spec/filter-2/2.0/conf/MinStandardFilter";
        public const string StandardFilter = "http://www.opengis.net/spec/filter-2/2.0/conf/StandardFilter";
        public const string MinimumSpatialFilter = "http://www.opengis.net/spec/filter-2/2.0/conf/MinSpatialFilter";
        public const string SpatialFilter = "http://www.opengis.net/spec/filter-2/2.0/conf/SpatialFilter";
        public const string MinimumTemporalFilter = "http://www.opengis.net/spec/filter-2/2.0/conf/MinTemporalFilter";
        public const string TemporalFilter = "http://www.opengis.net/spec/filter-2/2.0/conf/TemporalFilter";
        public const string VersionNav = "http://www.opengis.net/spec/filter-2/2.0/conf/VersionNav";
        public const string SortingWfs = "http://www.opengis.net/spec/filter-2/2.0/conf/Sorting";
        public const string ExtendedOperators = "http://www.opengis.net/spec/filter-2/2.0/conf/ExtendedOperators";
        public const string MinimumXPath = "http://www.opengis.net/spec/filter-2/2.0/conf/MinXPath";
        public const string SchemaElementFunc = "http://www.opengis.net/spec/filter-2/2.0/conf/SchemaElementFunc";
    }

    /// <summary>
    /// Default maximum number of features to return
    /// </summary>
    public const int DefaultMaxFeatures = 1000;

    /// <summary>
    /// Maximum allowed features per request
    /// </summary>
    public const int MaxFeaturesPerRequest = 10000;

    /// <summary>
    /// Default spatial reference system (WGS84)
    /// </summary>
    public const string DefaultSrs = "EPSG:4326";

    /// <summary>
    /// Validates WFS 2.0 request parameters
    /// </summary>
    public static string? ValidateRequestParameters(IQueryCollection query, ImmutableHashSet<string> allowedParameters)
    {
        var service = query[ParameterNames.Service].FirstOrDefault();
        if (!string.Equals(service, ServiceType, StringComparison.OrdinalIgnoreCase))
        {
            return $"Invalid service parameter. Expected '{ServiceType}', got '{service}'";
        }

        var version = query[ParameterNames.Version].FirstOrDefault();
        if (!string.Equals(version, Version, StringComparison.OrdinalIgnoreCase))
        {
            return $"Unsupported version. Expected '{Version}', got '{version}'";
        }

        return null;
    }

    public static string? GetQueryValue(IQueryCollection query, string primaryName, params string[] aliases)
    {
        var value = query[primaryName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        foreach (var alias in aliases)
        {
            value = query[alias].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static bool TryParseSections(
        string? sections,
        out ImmutableHashSet<string>? requestedSections,
        out string? errorMessage)
    {
        requestedSections = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(sections))
        {
            return true;
        }

        var parts = sections.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            errorMessage = "SECTIONS contains an empty section name.";
            return false;
        }

        var parsedSections = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in parts)
        {
            if (!GetCapabilitiesSections.Contains(section))
            {
                errorMessage = $"Unsupported SECTIONS value '{section}'.";
                return false;
            }

            parsedSections.Add(section);
        }

        if (parsedSections.Count == 0 || parsedSections.Contains("All"))
        {
            return true;
        }

        requestedSections = parsedSections.ToImmutable();
        return true;
    }

    public static int CompareUpdateSequence(string? requestedUpdateSequence, string currentUpdateSequence)
    {
        if (string.IsNullOrWhiteSpace(requestedUpdateSequence))
        {
            return -1;
        }

        if (long.TryParse(requestedUpdateSequence, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedLong) &&
            long.TryParse(currentUpdateSequence, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentLong))
        {
            return requestedLong.CompareTo(currentLong);
        }

        return string.Compare(requestedUpdateSequence, currentUpdateSequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses type names parameter (comma-separated list)
    /// </summary>
    public static string[] ParseTypeNames(string? typeNames)
    {
        if (string.IsNullOrEmpty(typeNames))
            return Array.Empty<string>();

        return typeNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Parses count parameter with validation
    /// </summary>
    public static int ParseCount(string? count, int defaultValue = DefaultMaxFeatures)
    {
        return TryParseCount(count, out var value, defaultValue)
            ? value
            : defaultValue;
    }

    /// <summary>
    /// Parses start index parameter with validation
    /// </summary>
    public static int ParseStartIndex(string? startIndex)
    {
        return TryParseStartIndex(startIndex, out var value)
            ? value
            : 0;
    }

    public static bool TryParseCount(string? count, out int value, int defaultValue = DefaultMaxFeatures)
    {
        if (string.IsNullOrWhiteSpace(count))
        {
            value = defaultValue;
            return true;
        }

        if (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue) || parsedValue < 0)
        {
            value = defaultValue;
            return false;
        }

        value = Math.Min(parsedValue, MaxFeaturesPerRequest);
        return true;
    }

    public static bool TryParseStartIndex(string? startIndex, out int value)
    {
        if (string.IsNullOrWhiteSpace(startIndex))
        {
            value = 0;
            return true;
        }

        if (!int.TryParse(startIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue) || parsedValue < 0)
        {
            value = 0;
            return false;
        }

        value = parsedValue;
        return true;
    }

    public static bool TryNormalizeResultType(string? resultType, out string normalizedResultType)
    {
        normalizedResultType = "results";

        if (string.IsNullOrWhiteSpace(resultType))
        {
            return true;
        }

        if (string.Equals(resultType, "results", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(resultType, "hits", StringComparison.OrdinalIgnoreCase))
        {
            normalizedResultType = "hits";
            return true;
        }

        normalizedResultType = resultType;
        return false;
    }
}
