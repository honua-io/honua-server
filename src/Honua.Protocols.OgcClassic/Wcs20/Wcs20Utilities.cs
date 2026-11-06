// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

internal static class Wcs20Utilities
{
    internal const string Version = "2.0.1";

    internal const string Version10 = "1.0.0";

    internal const string ServiceType = "WCS";
    internal const string XmlContentType = "application/xml";
    internal const string TiffContentType = "image/tiff";
    internal const string PngContentType = "image/png";
    internal const string JpegContentType = "image/jpeg";

    internal const string OgcServiceExceptionContentType = "application/vnd.ogc.se_xml";

    internal const string WcsNamespace = "http://www.opengis.net/wcs/2.0";
    internal const string OwsNamespace = "http://www.opengis.net/ows/2.0";

    internal const string Wcs10Namespace = "http://www.opengis.net/wcs";

    internal const string Gml10Namespace = "http://www.opengis.net/gml";

    internal const string OgcExceptionNamespace = "http://www.opengis.net/ogc";

    // The WCS CRS-extension namespace is used for crsSupported values only, and its conformance class is not advertised, so the document stays valid for the core test suite.
    internal const string CrsNamespace = "http://www.opengis.net/wcs/crs/1.0";
    internal const string GmlNamespace = "http://www.opengis.net/gml/3.2";
    internal const string GmlcovNamespace = "http://www.opengis.net/gmlcov/1.0";
    internal const string SweNamespace = "http://www.opengis.net/swe/2.0";
    internal const string XLinkNamespace = "http://www.w3.org/1999/xlink";
    internal const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

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
        ImmutableArray.Create(Version, Version10);


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

        internal const string Interpolation = "INTERPOLATION";

        internal const string DateTime = "DATETIME";

        internal const string Time = "TIME";
    }

    internal static class Parameters10
    {
        internal const string Coverage = "COVERAGE";

        internal const string Crs = "CRS";

        internal const string ResponseCrs = "RESPONSE_CRS";

        internal const string Width = "WIDTH";
        internal const string Height = "HEIGHT";
        internal const string ResX = "RESX";
        internal const string ResY = "RESY";

        internal const string Bands = "BANDS";

        internal const string Section = "SECTION";

        internal const string Exceptions = "EXCEPTIONS";
        internal const string Interpolation = "INTERPOLATION";
    }

    internal static class ExceptionCodes10
    {
        internal const string InvalidFormat = "InvalidFormat";
        internal const string CoverageNotDefined = "CoverageNotDefined";
        internal const string CurrentUpdateSequence = "CurrentUpdateSequence";
        internal const string InvalidUpdateSequence = "InvalidUpdateSequence";
        internal const string MissingParameterValue = "MissingParameterValue";
        internal const string InvalidParameterValue = "InvalidParameterValue";
    }

    internal static class InterpolationMethods
    {
        internal const string NearestUri = "http://www.opengis.net/def/interpolation/OGC/1/nearest";
        internal const string LinearUri = "http://www.opengis.net/def/interpolation/OGC/1/linear";
        internal const string CubicUri = "http://www.opengis.net/def/interpolation/OGC/1/cubic";
    }

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

        internal const string InterpolationMethodNotSupported = "InterpolationMethodNotSupported";

        internal const string OutputCrsNotSupported = "OutputCrs-NotSupported";

        internal const string SubsettingCrsNotSupported = "SubsettingCrs-NotSupported";
    }
}
