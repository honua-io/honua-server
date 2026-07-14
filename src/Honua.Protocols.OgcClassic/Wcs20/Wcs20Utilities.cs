// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

internal static class Wcs20Utilities
{
    internal const string Version = "2.0.1";
    internal const string ServiceType = "WCS";
    internal const string XmlContentType = "application/xml";
    internal const string TiffContentType = "image/tiff";
    internal const string PngContentType = "image/png";
    internal const string JpegContentType = "image/jpeg";

    internal const string WcsNamespace = "http://www.opengis.net/wcs/2.0";
    internal const string OwsNamespace = "http://www.opengis.net/ows/2.0";

    /// <summary>
    /// WCS 2.0 CRS extension (OGC 11-053r1) namespace. Only its
    /// <c>crsSupported</c> advertisement values are emitted (inside the
    /// ServiceMetadata <c>xs:any</c> Extension slot); the CRS-extension
    /// conformance class itself is intentionally not advertised, so the
    /// document stays valid for the WCS core ETS.
    /// </summary>
    internal const string CrsNamespace = "http://www.opengis.net/wcs/crs/1.0";
    internal const string GmlNamespace = "http://www.opengis.net/gml/3.2";
    internal const string GmlcovNamespace = "http://www.opengis.net/gmlcov/1.0";
    internal const string SweNamespace = "http://www.opengis.net/swe/2.0";
    internal const string XLinkNamespace = "http://www.w3.org/1999/xlink";
    internal const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    internal static readonly ImmutableHashSet<string> ImplementedOperations =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            Operations.GetCapabilities,
            Operations.DescribeCoverage,
            Operations.GetCoverage);

    internal static readonly ImmutableHashSet<string> XmlFormats =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            XmlContentType,
            "text/xml");

    internal static readonly ImmutableHashSet<string> CoverageFormats =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            TiffContentType,
            "image/geotiff",
            "tiff",
            "tif",
            PngContentType,
            "png",
            JpegContentType,
            "jpg",
            "jpeg");

    // Ordered list of CoverageFormats for shared OgcParameterValidator. Order
    // mirrors the enumeration used in the OWS Capabilities document so error
    // messages list canonical content types first.
    internal static readonly ImmutableArray<string> CoverageFormatsList =
        ImmutableArray.Create(
            TiffContentType,
            "image/geotiff",
            "tiff",
            "tif",
            PngContentType,
            "png",
            JpegContentType,
            "jpg",
            "jpeg");

    internal static readonly ImmutableArray<string> SupportedVersions =
        ImmutableArray.Create(Version);

    /// <summary>
    /// EPSG SRIDs always offered as transformable output/subsetting CRS values
    /// (in addition to each coverage's native CRS), provided the CRS registry can
    /// resolve them. WGS84 geographic and WebMercator match the OGC API Coverages
    /// default identifier set so advertisement and validation agree across adapters.
    /// </summary>
    internal static readonly ImmutableArray<int> DefaultCrsIdentifiers =
        ImmutableArray.Create(4326, 3857);

    internal static class Operations
    {
        internal const string GetCapabilities = "GetCapabilities";
        internal const string DescribeCoverage = "DescribeCoverage";
        internal const string GetCoverage = "GetCoverage";
    }

    internal static class Parameters
    {
        internal const string Service = "SERVICE";
        internal const string Request = "REQUEST";
        internal const string Version = "VERSION";
        internal const string AcceptVersions = "ACCEPTVERSIONS";
        internal const string AcceptFormats = "ACCEPTFORMATS";
        internal const string Sections = "SECTIONS";
        internal const string CoverageId = "COVERAGEID";
        internal const string Format = "FORMAT";
        internal const string Subset = "SUBSET";
        internal const string SubsettingCrs = "SUBSETTINGCRS";
        internal const string OutputCrs = "OUTPUTCRS";
        internal const string BBox = "BBOX";
        internal const string BBoxCrs = "BBOXCRS";
        internal const string RangeSubset = "RANGESUBSET";
        internal const string ScaleSize = "SCALESIZE";
        internal const string ScaleFactor = "SCALEFACTOR";
        internal const string ScaleAxes = "SCALEAXES";
        internal const string ScaleExtent = "SCALEEXTENT";

        /// <summary>WCS 2.0 Interpolation extension: resampling method selection.</summary>
        internal const string Interpolation = "INTERPOLATION";

        /// <summary>OGC API-style temporal subset alias accepted alongside <c>SUBSET=phenomenonTime(...)</c>.</summary>
        internal const string DateTime = "DATETIME";

        /// <summary>Classic WCS temporal subset alias accepted alongside <c>SUBSET=phenomenonTime(...)</c>.</summary>
        internal const string Time = "TIME";
    }

    /// <summary>
    /// WCS 2.0 Interpolation extension method identifiers. The canonical forms are
    /// the OGC interpolation-method URIs; the trailing token (e.g. <c>nearest</c>)
    /// and common Esri-style aliases are also accepted for convenience.
    /// </summary>
    internal static class InterpolationMethods
    {
        internal const string NearestUri = "http://www.opengis.net/def/interpolation/OGC/1/nearest";
        internal const string LinearUri = "http://www.opengis.net/def/interpolation/OGC/1/linear";
        internal const string CubicUri = "http://www.opengis.net/def/interpolation/OGC/1/cubic";
    }

    /// <summary>
    /// WCS 2.0 temporal subset axis labels recognised by the
    /// <c>SUBSET=phenomenonTime(...)</c> form. Matched case-insensitively.
    /// </summary>
    internal static readonly ImmutableHashSet<string> TemporalAxisLabels =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "phenomenonTime",
            "time",
            "date",
            "ansi");

    internal static class ExceptionCodes
    {
        internal const string MissingParameterValue = "MissingParameterValue";
        internal const string InvalidParameterValue = "InvalidParameterValue";
        internal const string InvalidAxisLabel = "InvalidAxisLabel";
        internal const string InvalidSubsetting = "InvalidSubsetting";
        internal const string VersionNegotiationFailed = "VersionNegotiationFailed";
        internal const string OperationNotSupported = "OperationNotSupported";
        internal const string NoSuchCoverage = "NoSuchCoverage";
        internal const string NoApplicableCode = "NoApplicableCode";

        /// <summary>WCS 2.0 Interpolation extension exception for an unsupported method.</summary>
        internal const string InterpolationMethodNotSupported = "InterpolationMethodNotSupported";

        /// <summary>
        /// WCS 2.0 CRS extension (OGC 11-053r1) exception for a well-formed but
        /// non-transformable <c>OUTPUTCRS</c> value.
        /// </summary>
        internal const string OutputCrsNotSupported = "OutputCrs-NotSupported";

        /// <summary>
        /// WCS 2.0 CRS extension (OGC 11-053r1) exception for a well-formed but
        /// non-transformable <c>SUBSETTINGCRS</c>/<c>BBOXCRS</c> value.
        /// </summary>
        internal const string SubsettingCrsNotSupported = "SubsettingCrs-NotSupported";
    }
}
