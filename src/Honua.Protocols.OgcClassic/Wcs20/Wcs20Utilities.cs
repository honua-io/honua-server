// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

internal static class Wcs20Utilities
{
    internal const string Version = "2.0.1";

    /// <summary>
    /// WCS 1.0.0, served alongside 2.0.1 because every stock QGIS build ships a WCS
    /// provider that speaks 1.0/1.1 only and therefore cannot open a 2.0.1 coverage at
    /// all (honua-server#5020). Version selection happens inside the existing handler,
    /// mirroring how <c>Wfs20DispatcherEndpoint</c> serves WFS 1.0.0/1.1.0 beside 2.0.0,
    /// so the routes, endpoint registry and telemetry classifiers are untouched.
    /// </summary>
    internal const string Version10 = "1.0.0";

    internal const string ServiceType = "WCS";
    internal const string XmlContentType = "application/xml";
    internal const string TiffContentType = "image/tiff";
    internal const string PngContentType = "image/png";
    internal const string JpegContentType = "image/jpeg";

    /// <summary>
    /// Content type WCS 1.0.0 clients expect for a <c>ServiceExceptionReport</c>. The
    /// QGIS provider triggers its exception parsing on <c>text/*</c>,
    /// <c>application/xml</c> or this value, so errors stay machine-readable.
    /// </summary>
    internal const string OgcServiceExceptionContentType = "application/vnd.ogc.se_xml";

    internal const string WcsNamespace = "http://www.opengis.net/wcs/2.0";
    internal const string OwsNamespace = "http://www.opengis.net/ows/2.0";

    /// <summary>WCS 1.0.0 default namespace (there is no version segment in 1.0).</summary>
    internal const string Wcs10Namespace = "http://www.opengis.net/wcs";

    /// <summary>GML 2.1.2, which WCS 1.0.0 uses for envelopes and grid limits.</summary>
    internal const string Gml10Namespace = "http://www.opengis.net/gml";

    /// <summary>
    /// Namespace of the WCS 1.0.0 <c>ServiceExceptionReport</c>. WCS 1.0.0 predates OWS
    /// and does NOT use the <c>ows:ExceptionReport</c> that 2.0.1 emits.
    /// </summary>
    internal const string OgcExceptionNamespace = "http://www.opengis.net/ogc";

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

    /// <summary>
    /// Highest-first, so the first entry is the version a request that omits
    /// <c>VERSION</c> negotiates to.
    /// </summary>
    internal static readonly ImmutableArray<string> SupportedVersions =
        ImmutableArray.Create(Version, Version10);


    /// <summary>
    /// True when the request asks for WCS 1.0.0. <c>VERSION</c> is the 1.0 negotiation
    /// parameter; <c>ACCEPTVERSIONS</c> arrived with OWS in 1.1, but it is honoured here
    /// too so a client that sends only <c>ACCEPTVERSIONS=1.0.0</c> still reaches the 1.0
    /// encoding instead of a 2.0.1 document it cannot parse.
    /// </summary>
    internal static bool IsVersion10(string? version, string? acceptVersions)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version.Trim().StartsWith("1.0", StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(acceptVersions))
        {
            return false;
        }

        var candidates = acceptVersions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Only when 1.0 is asked for and 2.0.1 is not: OWS negotiation prefers the
        // highest mutually supported version.
        return candidates.Any(candidate => candidate.StartsWith("1.0", StringComparison.Ordinal))
            && !candidates.Any(candidate => string.Equals(candidate, Version, StringComparison.Ordinal));
    }

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
    /// WCS 1.0.0 KVP parameter names. 1.0 predates the OWS common parameter set, so the
    /// coverage identifier, CRS and output-size parameters are all spelled differently
    /// from their 2.0.1 equivalents. Lookups are case-insensitive at read time, so the
    /// uppercase convention matches <see cref="Parameters"/>.
    /// </summary>
    internal static class Parameters10
    {
        /// <summary>The 1.0 coverage identifier. 2.0.1 spells this <c>COVERAGEID</c>.</summary>
        internal const string Coverage = "COVERAGE";

        /// <summary>CRS of the requested <c>BBOX</c>. 2.0.1 spells this <c>SUBSETTINGCRS</c>.</summary>
        internal const string Crs = "CRS";

        /// <summary>CRS of the returned coverage. 2.0.1 spells this <c>OUTPUTCRS</c>.</summary>
        internal const string ResponseCrs = "RESPONSE_CRS";

        internal const string Width = "WIDTH";
        internal const string Height = "HEIGHT";
        internal const string ResX = "RESX";
        internal const string ResY = "RESY";

        /// <summary>Singular in 1.0; 2.0.1 uses the plural <c>SECTIONS</c>.</summary>
        internal const string Section = "SECTION";

        internal const string Exceptions = "EXCEPTIONS";
        internal const string Interpolation = "INTERPOLATION";
    }

    /// <summary>
    /// WCS 1.0.0 <c>ServiceException</c> codes. These are the OGC service-exception codes,
    /// not the OWS <c>exceptionCode</c> values 2.0.1 uses, and clients map them to
    /// user-facing text - the QGIS provider recognises <c>InvalidFormat</c> and
    /// <c>CoverageNotDefined</c> by name.
    /// </summary>
    internal static class ExceptionCodes10
    {
        internal const string InvalidFormat = "InvalidFormat";
        internal const string CoverageNotDefined = "CoverageNotDefined";
        internal const string CurrentUpdateSequence = "CurrentUpdateSequence";
        internal const string InvalidUpdateSequence = "InvalidUpdateSequence";
        internal const string MissingParameterValue = "MissingParameterValue";
        internal const string InvalidParameterValue = "InvalidParameterValue";
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
