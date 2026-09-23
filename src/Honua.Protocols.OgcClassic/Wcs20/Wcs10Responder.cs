// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;
using NetTopologySuite.Geometries;

namespace Honua.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// WCS 1.0.0 encoding, served from the same routes and the same coverage resolution as
/// 2.0.1 (honua-server#5020).
/// </summary>
/// <remarks>
/// <para>
/// This exists because every stock QGIS build - 3.44 LTR and 4.2 alike - ships a WCS
/// provider that identifies itself as "version 1.0/1.1" and rejects anything else, so
/// 2.0.1-only serving means no QGIS user can open a coverage at all. Upstream
/// <c>qgis/QGIS#45584</c> ("Add support for WCS 2.x") is still open, so the server has to
/// meet the client. ArcGIS Pro is unaffected: it negotiates 1.0.0 through 2.0.1 and
/// already uses 2.0.1.
/// </para>
/// <para>
/// It is a partial of <see cref="Wcs20Handler"/> rather than a separate handler so that
/// coverage resolution - the metadata-graph walk, the per-service and per-layer access
/// policy evaluation including the dual-policy fix in #4388, and the lifecycle
/// routability checks - is shared rather than reimplemented. Duplicating that logic
/// would duplicate a security boundary.
/// </para>
/// <para>
/// The element shapes here are driven by what the QGIS provider actually reads
/// (<c>qgswcscapabilities.cpp</c>, <c>qgswcsprovider.cpp</c>), not only by the schema:
/// the capabilities document must expose <c>ContentMetadata/CoverageOfferingBrief</c>
/// entries with <c>name</c>/<c>label</c>/<c>description</c> and a GetCoverage
/// <c>OnlineResource</c>, and DescribeCoverage must expose a <c>RectifiedGrid</c> with
/// <c>GridEnvelope</c> limits plus at least one usable CRS, because the provider derives
/// raster width, height and CRS from exactly those.
/// </para>
/// </remarks>
internal sealed partial class Wcs20Handler
{
    private static readonly XNamespace Wcs10 = Wcs20Utilities.Wcs10Namespace;
    private static readonly XNamespace Gml10 = Wcs20Utilities.Gml10Namespace;

    /// <summary>
    /// Formats advertised for 1.0.0. <c>GeoTIFF</c> leads deliberately: the QGIS provider
    /// picks the first advertised format whose name contains "tif", and gives up entirely
    /// when nothing is advertised.
    /// </summary>
    private static readonly (string Name, RasterFormat Format)[] _wcs10Formats =
    [
        ("GeoTIFF", RasterFormat.TIFF),
        ("PNG", RasterFormat.PNG),
        ("JPEG", RasterFormat.JPEG),
    ];

    private async Task<IResult> HandleWcs10Async(
        HttpContext context,
        Wcs20RouteScope scope,
        string operation,
        CancellationToken cancellationToken)
    {
        if (string.Equals(operation, Wcs20Utilities.Operations.GetCapabilities, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleWcs10GetCapabilitiesAsync(context, scope, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(operation, Wcs20Utilities.Operations.DescribeCoverage, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleWcs10DescribeCoverageAsync(context, scope, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(operation, Wcs20Utilities.Operations.GetCoverage, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleWcs10GetCoverageAsync(context, scope, cancellationToken).ConfigureAwait(false);
        }

        Wcs20Log.ValidationFailed(_logger, operation, "Unsupported WCS operation.");
        return Wcs10ErrorResults.CreateNotImplemented(
            Wcs20Utilities.ExceptionCodes10.InvalidParameterValue,
            $"Unsupported WCS operation '{operation}'. Supported operations are GetCapabilities, DescribeCoverage, and GetCoverage.",
            Wcs20Utilities.Parameters.Request);
    }

    private async Task<IResult> HandleWcs10GetCapabilitiesAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        CancellationToken cancellationToken)
    {
        var coverageResult = await ResolveCoverageListAsync(context, scope, cancellationToken).ConfigureAwait(false);
        if (coverageResult.Error is not null)
        {
            // Resolution failures (service not published, WCS not enabled, access denied)
            // are still reported in the 2.0.1 encoding because the resolver owns those
            // results and is shared with the 2.0.1 path. The status code is correct in
            // both encodings; only the body differs. Every error this file raises itself
            // uses the 1.0.0 encoding.
            return coverageResult.Error;
        }

        var endpoint = GetCurrentEndpointUrl(context);
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Wcs10 + "WCS_Capabilities",
                new XAttribute(XNamespace.Xmlns + "wcs", Wcs20Utilities.Wcs10Namespace),
                new XAttribute(XNamespace.Xmlns + "gml", Wcs20Utilities.Gml10Namespace),
                new XAttribute(XNamespace.Xmlns + "xlink", Wcs20Utilities.XLinkNamespace),
                new XAttribute("version", Wcs20Utilities.Version10),
                new XAttribute("updateSequence", "0"),
                BuildWcs10Service(),
                BuildWcs10Capability(endpoint),
                new XElement(Wcs10 + "ContentMetadata",
                    coverageResult.Coverages.Select(BuildWcs10CoverageOfferingBrief))));

        return Xml(document);
    }

    private static XElement BuildWcs10Service()
        // Schema order for the 1.0 Service element is description, name, label.
        => new(Wcs10 + "Service",
            new XElement(Wcs10 + "description", "Honua Web Coverage Service for raster and coverage data."),
            new XElement(Wcs10 + "name", "Honua WCS"),
            new XElement(Wcs10 + "label", "Honua WCS 1.0.0"),
            new XElement(Wcs10 + "fees", "NONE"),
            new XElement(Wcs10 + "accessConstraints", "NONE"));

    private static XElement BuildWcs10Capability(string endpoint)
    {
        // 1.0 wants the request URL ready for parameter appending, so it carries the
        // trailing '?' by convention.
        var href = endpoint.Contains('?', StringComparison.Ordinal) ? endpoint : endpoint + "?";

        return new XElement(Wcs10 + "Capability",
            new XElement(Wcs10 + "Request",
                BuildWcs10Operation(Wcs20Utilities.Operations.GetCapabilities, href),
                BuildWcs10Operation(Wcs20Utilities.Operations.DescribeCoverage, href),
                BuildWcs10Operation(Wcs20Utilities.Operations.GetCoverage, href)),
            new XElement(Wcs10 + "Exception",
                new XElement(Wcs10 + "Format", Wcs20Utilities.OgcServiceExceptionContentType)));
    }

    private static XElement BuildWcs10Operation(string operation, string href)
        => new(Wcs10 + operation,
            new XElement(Wcs10 + "DCPType",
                new XElement(Wcs10 + "HTTP",
                    new XElement(Wcs10 + "Get",
                        new XElement(Wcs10 + "OnlineResource",
                            new XAttribute(XLink + "href", href))))));

    private static XElement BuildWcs10CoverageOfferingBrief(WcsCoverage coverage)
    {
        var coverageId = FormatCoverageId(coverage.LayerId);
        var label = coverage.Resource.Metadata.Title ?? coverage.Resource.Metadata.Name ?? coverageId;
        var description = string.IsNullOrWhiteSpace(coverage.Resource.Metadata.Description)
            ? label
            : coverage.Resource.Metadata.Description;

        var children = new List<object>
        {
            new XElement(Wcs10 + "description", description),
            new XElement(Wcs10 + "name", coverageId),
            new XElement(Wcs10 + "label", label),
        };

        var lonLatEnvelope = BuildWcs10LonLatEnvelope(coverage);
        if (lonLatEnvelope is not null)
        {
            children.Add(lonLatEnvelope);
        }

        return new XElement(Wcs10 + "CoverageOfferingBrief", children);
    }

    /// <summary>
    /// The 1.0 <c>lonLatEnvelope</c> must be WGS84. No in-process coordinate transform is
    /// available on this path, so - exactly as the 2.0.1 <c>ows:WGS84BoundingBox</c> does -
    /// it is emitted only when the coverage is already in WGS84 rather than reporting
    /// native coordinates under a WGS84 label. Clients that need the extent of a
    /// projected coverage read it from the DescribeCoverage <c>Envelope</c>, which carries
    /// its real <c>srsName</c>; the QGIS provider treats <c>lonLatEnvelope</c> as optional
    /// and derives extent from DescribeCoverage.
    /// </summary>
    private static XElement? BuildWcs10LonLatEnvelope(WcsCoverage coverage)
    {
        if (!TryResolveExtent(coverage.Raster, out var extent))
        {
            return null;
        }

        var srid = extent.Srid ?? coverage.Raster.Srid ?? coverage.Resource.ReadSrid();
        if (srid != 4326)
        {
            return null;
        }

        return new XElement(Wcs10 + "lonLatEnvelope",
            new XAttribute("srsName", "urn:ogc:def:crs:OGC:1.3:CRS84"),
            new XElement(Gml10 + "pos", FormatPosition(extent.XMin, extent.YMin)),
            new XElement(Gml10 + "pos", FormatPosition(extent.XMax, extent.YMax)));
    }

    private async Task<IResult> HandleWcs10DescribeCoverageAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        CancellationToken cancellationToken)
    {
        // 1.0 spells the identifier COVERAGE and allows a comma-separated list; omitting
        // it means "describe everything this service offers".
        var requested = GetQueryValue(context.Request.Query, Wcs20Utilities.Parameters10.Coverage);

        List<WcsCoverage> coverages = [];
        if (string.IsNullOrWhiteSpace(requested))
        {
            var all = await ResolveCoverageListAsync(context, scope, cancellationToken).ConfigureAwait(false);
            if (all.Error is not null)
            {
                return all.Error;
            }

            coverages.AddRange(all.Coverages);
        }
        else
        {
            foreach (var token in SplitCsv(requested))
            {
                if (!TryParseCoverageId(token, out var identifier))
                {
                    return Wcs10ErrorResults.CreateBadRequest(
                        Wcs20Utilities.ExceptionCodes10.CoverageNotDefined,
                        $"Coverage '{token}' is not a valid coverage name.",
                        Wcs20Utilities.Parameters10.Coverage);
                }

                var resolved = await ResolveCoverageAsync(context, scope, identifier, cancellationToken).ConfigureAwait(false);
                if (resolved.Error is not null)
                {
                    return resolved.Error;
                }

                if (resolved.Coverage is null)
                {
                    Wcs20Log.CoverageNotFound(_logger, identifier.Raw);
                    return Wcs10ErrorResults.CreateNotFound(
                        Wcs20Utilities.ExceptionCodes10.CoverageNotDefined,
                        $"Coverage '{identifier.Raw}' was not found.",
                        Wcs20Utilities.Parameters10.Coverage);
                }

                coverages.Add(resolved.Coverage.Value);
            }
        }

        var offerings = new List<XElement>(coverages.Count);
        foreach (var coverage in coverages)
        {
            if (!TryBuildWcs10CoverageOffering(coverage, out var offering, out var error))
            {
                return Wcs10ErrorResults.CreateInternalServerError(error ?? "Coverage metadata is incomplete.");
            }

            offerings.Add(offering);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Wcs10 + "CoverageDescription",
                new XAttribute(XNamespace.Xmlns + "wcs", Wcs20Utilities.Wcs10Namespace),
                new XAttribute(XNamespace.Xmlns + "gml", Wcs20Utilities.Gml10Namespace),
                new XAttribute("version", Wcs20Utilities.Version10),
                new XAttribute("updateSequence", "0"),
                offerings));

        return Xml(document);
    }

    private static bool TryBuildWcs10CoverageOffering(
        WcsCoverage coverage,
        out XElement offering,
        out string? error)
    {
        offering = null!;
        error = null;

        if (!TryResolveExtent(coverage.Raster, out var extent))
        {
            error = "Coverage extent is unavailable.";
            return false;
        }

        var srid = extent.Srid ?? coverage.Raster.Srid ?? coverage.Resource.ReadSrid();
        if (srid is null or <= 0)
        {
            error = "Coverage spatial reference is unavailable.";
            return false;
        }

        var coverageId = FormatCoverageId(coverage.LayerId);
        var label = coverage.Resource.Metadata.Title ?? coverage.Resource.Metadata.Name ?? coverageId;
        var description = string.IsNullOrWhiteSpace(coverage.Resource.Metadata.Description)
            ? label
            : coverage.Resource.Metadata.Description;
        var srsName = CreateWcs10CrsName(srid.Value);

        var children = new List<object>
        {
            new XElement(Wcs10 + "description", description),
            new XElement(Wcs10 + "name", coverageId),
            new XElement(Wcs10 + "label", label),
        };

        var lonLatEnvelope = BuildWcs10LonLatEnvelope(coverage);
        if (lonLatEnvelope is not null)
        {
            children.Add(lonLatEnvelope);
        }

        children.Add(BuildWcs10DomainSet(coverage, extent, coverageId, srsName));
        children.Add(BuildWcs10RangeSet(coverage, coverageId));
        children.Add(new XElement(Wcs10 + "supportedCRSs",
            new XElement(Wcs10 + "requestResponseCRSs", srsName)));
        children.Add(new XElement(Wcs10 + "supportedFormats",
            new XAttribute("nativeFormat", _wcs10Formats[0].Name),
            _wcs10Formats.Select(format => new XElement(Wcs10 + "formats", format.Name))));
        children.Add(new XElement(Wcs10 + "supportedInterpolations",
            new XAttribute("default", "nearest neighbor"),
            new XElement(Wcs10 + "interpolationMethod", "nearest neighbor")));

        offering = new XElement(Wcs10 + "CoverageOffering", children);
        return true;
    }

    private static XElement BuildWcs10DomainSet(
        WcsCoverage coverage,
        RasterExtent extent,
        string coverageId,
        string srsName)
    {
        // The provider reads width and height from GridEnvelope high - low, so the grid
        // has to describe the real raster size rather than a placeholder.
        var high = FormattableString.Invariant(
            $"{Math.Max(coverage.Raster.Width - 1, 0)} {Math.Max(coverage.Raster.Height - 1, 0)}");

        var rectifiedGrid = new List<object>
        {
            new XAttribute("dimension", "2"),
            new XAttribute("srsName", srsName),
            new XElement(Gml10 + "limits",
                new XElement(Gml10 + "GridEnvelope",
                    new XElement(Gml10 + "low", "0 0"),
                    new XElement(Gml10 + "high", high))),
            new XElement(Gml10 + "axisName", "x"),
            new XElement(Gml10 + "axisName", "y"),
        };

        if (TryResolveGridVectors(coverage.Raster, extent, out var origin, out var xVector, out var yVector))
        {
            rectifiedGrid.Add(new XElement(Gml10 + "origin",
                new XElement(Gml10 + "pos", FormatPosition(origin.X, origin.Y))));
            rectifiedGrid.Add(new XElement(Gml10 + "offsetVector", FormatPosition(xVector.X, xVector.Y)));
            rectifiedGrid.Add(new XElement(Gml10 + "offsetVector", FormatPosition(yVector.X, yVector.Y)));
        }

        return new XElement(Wcs10 + "domainSet",
            new XElement(Wcs10 + "spatialDomain",
                new XElement(Gml10 + "Envelope",
                    new XAttribute("srsName", srsName),
                    new XElement(Gml10 + "pos", FormatPosition(extent.XMin, extent.YMin)),
                    new XElement(Gml10 + "pos", FormatPosition(extent.XMax, extent.YMax))),
                new XElement(Gml10 + "RectifiedGrid", rectifiedGrid)));
    }

    private static XElement BuildWcs10RangeSet(WcsCoverage coverage, string coverageId)
    {
        var bandCount = Math.Max(coverage.Raster.BandCount, 1);
        var axisDescription = new List<object>
        {
            new XElement(Wcs10 + "name", "bands"),
            new XElement(Wcs10 + "label", "Bands"),
            new XElement(Wcs10 + "values",
                Enumerable.Range(1, bandCount).Select(band =>
                    new XElement(Wcs10 + "singleValue", band.ToString(CultureInfo.InvariantCulture)))),
        };

        var rangeSetChildren = new List<object>
        {
            new XElement(Wcs10 + "name", coverageId),
            new XElement(Wcs10 + "label", "Coverage values"),
            new XElement(Wcs10 + "axisDescription",
                new XElement(Wcs10 + "AxisDescription", axisDescription)),
        };

        if (coverage.Raster.NoDataValue.HasValue)
        {
            rangeSetChildren.Add(new XElement(Wcs10 + "nullValues",
                new XElement(Wcs10 + "singleValue",
                    coverage.Raster.NoDataValue.Value.ToString("R", CultureInfo.InvariantCulture))));
        }

        return new XElement(Wcs10 + "rangeSet",
            new XElement(Wcs10 + "RangeSet", rangeSetChildren));
    }

    /// <summary>
    /// CRS identifier in the short form WCS 1.0.0 uses, e.g. <c>EPSG:4326</c>. The 2.0.1
    /// path emits an OGC URI (<c>http://www.opengis.net/def/crs/EPSG/0/4326</c>), which a
    /// 1.0.0 client does not resolve: QGIS runs every advertised value through
    /// <c>fromOgcWmsCrs()</c> and builds no layer when none survives, so advertising the
    /// URI form left the coverage unopenable even though the document was otherwise
    /// complete.
    /// </summary>
    private static string CreateWcs10CrsName(int srid)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"EPSG:{srid}");

    private async Task<IResult> HandleWcs10GetCoverageAsync(
        HttpContext context,
        Wcs20RouteScope scope,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;

        var requested = GetQueryValue(query, Wcs20Utilities.Parameters10.Coverage);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Wcs10ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes10.MissingParameterValue,
                "GetCoverage requires a COVERAGE parameter.",
                Wcs20Utilities.Parameters10.Coverage);
        }

        if (!TryParseCoverageId(requested.Trim(), out var identifier))
        {
            return Wcs10ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes10.CoverageNotDefined,
                $"Coverage '{requested}' is not a valid coverage name.",
                Wcs20Utilities.Parameters10.Coverage);
        }

        var resolved = await ResolveCoverageAsync(context, scope, identifier, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        if (resolved.Coverage is null)
        {
            Wcs20Log.CoverageNotFound(_logger, identifier.Raw);
            return Wcs10ErrorResults.CreateNotFound(
                Wcs20Utilities.ExceptionCodes10.CoverageNotDefined,
                $"Coverage '{identifier.Raw}' was not found.",
                Wcs20Utilities.Parameters10.Coverage);
        }

        var coverage = resolved.Coverage.Value;

        if (!TryResolveWcs10Format(query, out var outputFormat, out var formatError))
        {
            return formatError!;
        }

        if (!TryResolveExtent(coverage.Raster, out var nativeExtent))
        {
            return Wcs10ErrorResults.CreateInternalServerError("Coverage extent is unavailable.");
        }

        var nativeSrid = nativeExtent.Srid ?? coverage.Raster.Srid ?? coverage.Resource.ReadSrid();
        if (nativeSrid is null or <= 0)
        {
            return Wcs10ErrorResults.CreateInternalServerError("Coverage spatial reference is unavailable.");
        }

        if (!TryResolveWcs10Crs(query, Wcs20Utilities.Parameters10.Crs, nativeSrid.Value, out var requestSrid, out var crsError))
        {
            return crsError!;
        }

        if (!TryResolveWcs10Crs(query, Wcs20Utilities.Parameters10.ResponseCrs, requestSrid, out var responseSrid, out var responseCrsError))
        {
            return responseCrsError!;
        }

        if (!TryResolveWcs10Envelope(query, nativeExtent, out var envelope, out var envelopeError))
        {
            return envelopeError!;
        }

        if (!TryResolveWcs10Size(query, envelope, coverage.Raster, out var width, out var height, out var sizeError))
        {
            return sizeError!;
        }

        var rasterQuery = new RasterQuery
        {
            ClipRegion = CreateClipRegion(envelope, requestSrid),
            OutputSrid = responseSrid,
            OutputFormat = outputFormat,
            OutputWidth = width,
            OutputHeight = height,
            ResamplingAlgorithm = ResamplingAlgorithm.NearestNeighbor,
        };

        var result = await _coverageBackend.ExportImageAsync(
            coverage.LayerId,
            coverage.Raster.Id,
            rasterQuery,
            cancellationToken).ConfigureAwait(false);

        Wcs20Log.CoverageReturned(_logger, identifier.Raw, result.Data.Length, result.ContentType);

        return Results.File(result.Data, result.ContentType);
    }

    private static bool TryResolveWcs10Format(
        IQueryCollection query,
        out RasterFormat format,
        out IResult? error)
    {
        error = null;
        var requested = GetQueryValue(query, Wcs20Utilities.Parameters.Format);
        if (string.IsNullOrWhiteSpace(requested))
        {
            // 1.0 makes FORMAT mandatory, but defaulting to the native format is more
            // useful than refusing a request that is otherwise complete.
            format = _wcs10Formats[0].Format;
            return true;
        }

        var value = requested.Trim();
        foreach (var (name, candidate) in _wcs10Formats)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        // Accept the content-type spellings too, since 2.0.1 advertises those and a
        // client may carry one over.
        format = value.ToLowerInvariant() switch
        {
            "image/tiff" or "image/geotiff" or "tiff" or "tif" => RasterFormat.TIFF,
            "image/png" or "png" => RasterFormat.PNG,
            "image/jpeg" or "jpeg" or "jpg" => RasterFormat.JPEG,
            _ => RasterFormat.Raw,
        };

        if (format != RasterFormat.Raw)
        {
            return true;
        }

        format = _wcs10Formats[0].Format;
        error = Wcs10ErrorResults.CreateBadRequest(
            Wcs20Utilities.ExceptionCodes10.InvalidFormat,
            $"Format '{value}' is not supported. Supported formats are {string.Join(", ", _wcs10Formats.Select(f => f.Name))}.",
            Wcs20Utilities.Parameters.Format);
        return false;
    }

    private static bool TryResolveWcs10Crs(
        IQueryCollection query,
        string parameterName,
        int fallbackSrid,
        out int srid,
        out IResult? error)
    {
        error = null;
        srid = fallbackSrid;

        var requested = GetQueryValue(query, parameterName);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        var value = requested.Trim();

        // 1.0 clients send short forms: EPSG:4326, and OGC:CRS84 for lon/lat WGS84.
        if (value.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            srid = parsed;
            return true;
        }

        if (value.EndsWith("CRS84", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("WGS84", StringComparison.OrdinalIgnoreCase))
        {
            srid = 4326;
            return true;
        }

        error = Wcs10ErrorResults.CreateBadRequest(
            Wcs20Utilities.ExceptionCodes10.InvalidParameterValue,
            $"CRS '{value}' could not be interpreted. Use an EPSG:<code> value.",
            parameterName);
        return false;
    }

    private static bool TryResolveWcs10Envelope(
        IQueryCollection query,
        RasterExtent nativeExtent,
        out Envelope envelope,
        out IResult? error)
    {
        error = null;
        envelope = new Envelope(nativeExtent.XMin, nativeExtent.XMax, nativeExtent.YMin, nativeExtent.YMax);

        var bbox = GetQueryValue(query, Wcs20Utilities.Parameters.BBox);
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return true;
        }

        var parts = SplitCsv(bbox);
        if (parts.Length is not (4 or 6))
        {
            error = Wcs10ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes10.InvalidParameterValue,
                "BBOX must be minx,miny,maxx,maxy.",
                Wcs20Utilities.Parameters.BBox);
            return false;
        }

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                error = Wcs10ErrorResults.CreateBadRequest(
                    Wcs20Utilities.ExceptionCodes10.InvalidParameterValue,
                    $"BBOX value '{parts[i]}' is not a number.",
                    Wcs20Utilities.Parameters.BBox);
                return false;
            }
        }

        if (values[0] >= values[2] || values[1] >= values[3])
        {
            error = Wcs10ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes10.InvalidParameterValue,
                "BBOX minimum values must be less than the maximum values.",
                Wcs20Utilities.Parameters.BBox);
            return false;
        }

        envelope = new Envelope(values[0], values[2], values[1], values[3]);
        return true;
    }

    private static bool TryResolveWcs10Size(
        IQueryCollection query,
        Envelope envelope,
        RasterInfo raster,
        out int width,
        out int height,
        out IResult? error)
    {
        error = null;
        width = 0;
        height = 0;

        if (!TryParseWcs10PositiveInt(query, Wcs20Utilities.Parameters10.Width, out width, out error) ||
            !TryParseWcs10PositiveInt(query, Wcs20Utilities.Parameters10.Height, out height, out error))
        {
            return false;
        }

        // RESX/RESY are the alternative 1.0 form: ground resolution rather than a pixel
        // count. QGIS sends WIDTH/HEIGHT, but other 1.0 clients use resolution.
        if (width == 0 || height == 0)
        {
            var resX = GetQueryValue(query, Wcs20Utilities.Parameters10.ResX);
            var resY = GetQueryValue(query, Wcs20Utilities.Parameters10.ResY);
            if (!string.IsNullOrWhiteSpace(resX) && !string.IsNullOrWhiteSpace(resY) &&
                double.TryParse(resX, NumberStyles.Float, CultureInfo.InvariantCulture, out var stepX) &&
                double.TryParse(resY, NumberStyles.Float, CultureInfo.InvariantCulture, out var stepY) &&
                stepX > 0 && stepY > 0)
            {
                width = (int)Math.Max(1, Math.Round(envelope.Width / stepX));
                height = (int)Math.Max(1, Math.Round(envelope.Height / stepY));
            }
        }

        if (width == 0 || height == 0)
        {
            width = Math.Max(raster.Width, 1);
            height = Math.Max(raster.Height, 1);
        }

        if (width > MaxWcsOutputDimension || height > MaxWcsOutputDimension)
        {
            error = Wcs10ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes10.InvalidParameterValue,
                $"Requested output exceeds the {MaxWcsOutputDimension}px limit per dimension.",
                Wcs20Utilities.Parameters10.Width);
            return false;
        }

        return true;
    }

    private static bool TryParseWcs10PositiveInt(
        IQueryCollection query,
        string parameterName,
        out int value,
        out IResult? error)
    {
        error = null;
        value = 0;

        var raw = GetQueryValue(query, parameterName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            error = Wcs10ErrorResults.CreateBadRequest(
                Wcs20Utilities.ExceptionCodes10.InvalidParameterValue,
                $"{parameterName} must be a positive integer.",
                parameterName);
            return false;
        }

        value = parsed;
        return true;
    }
}
