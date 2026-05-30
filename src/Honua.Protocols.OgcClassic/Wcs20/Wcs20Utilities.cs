// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wcs20;

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
    }

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
    }
}
