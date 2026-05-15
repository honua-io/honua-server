// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.ServiceDefaults;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

internal sealed partial class Wfs20Handler
{
    private const string Wfs11Version = "1.1.0";
    private const string Wfs10Version = "1.0.0";
    private const string LegacyWfsNamespace = "http://www.opengis.net/wfs";
    private const string Ows10Namespace = "http://www.opengis.net/ows";
    private const string OgcFilterNamespace = "http://www.opengis.net/ogc";
    private const string GmlLegacyNamespace = "http://www.opengis.net/gml";
    private static readonly WKBReader LegacyWkbReader = new();

    internal async Task<string> HandleLegacyGetCapabilitiesAsync(
        HttpContext context,
        string version,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            IsWfs10(version) ? "wfs10.get_capabilities" : "wfs11.get_capabilities",
            ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, IsWfs10(version) ? "WFS-1.0.0" : "WFS-1.1.0");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetCapabilities);

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var featureTypes = await BuildFeatureTypesAsync(descriptors, cancellationToken).ConfigureAwait(false);
        var wfsUrl = $"{baseUrl.TrimEnd('/')}/wfs";

        return IsWfs10(version)
            ? BuildWfs10Capabilities(wfsUrl, featureTypes)
            : BuildWfs11Capabilities(wfsUrl, featureTypes);
    }

    internal async Task<string> HandleLegacyDescribeFeatureTypeAsync(
        HttpContext context,
        string version,
        string? typeNames,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            IsWfs10(version) ? "wfs10.describe_feature_type" : "wfs11.describe_feature_type",
            ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, IsWfs10(version) ? "WFS-1.0.0" : "WFS-1.1.0");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.DescribeFeatureType);

        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var unknownTypes = GetUnknownRequestedFeatureTypes(publishedTypes, requestedTypes);
        if (unknownTypes.Length > 0)
        {
            throw new ArgumentException(
                unknownTypes.Length == 1
                    ? $"Unknown feature type '{unknownTypes[0]}'."
                    : $"Unknown feature types: {string.Join(", ", unknownTypes.Select(type => $"'{type}'"))}.");
        }

        var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
        if (selectedTypes.Length == 0 && requestedTypes.Length > 0)
        {
            throw new ArgumentException($"Requested feature type(s) not found: {string.Join(", ", requestedTypes)}.");
        }

        return GenerateLegacySchemaForTypes(selectedTypes, version);
    }

    internal async Task<IResult> HandleLegacyGetFeatureAsync(
        HttpContext context,
        string version,
        string? typeNames,
        string? outputFormat,
        string? maxFeatures,
        string? sortBy,
        string? bbox,
        string? filter,
        string? featureId,
        string? propertyName,
        string? srsName,
        string? resultType,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            IsWfs10(version) ? "wfs10.get_feature" : "wfs11.get_feature",
            ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, IsWfs10(version) ? "WFS-1.0.0" : "WFS-1.1.0");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetFeature);

        if (!IsLegacyXmlOutputFormat(version, outputFormat))
        {
            return CreateLegacyWfsException(
                version,
                "InvalidParameterValue",
                $"Unsupported output format '{outputFormat}'. WFS {version} supports XML/GML output.",
                "outputFormat",
                IsWfs10(version) ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        }

        if (!Wfs20Utilities.TryParseCount(maxFeatures, out var limit))
        {
            return CreateLegacyWfsException(
                version,
                "InvalidParameterValue",
                $"Invalid MAXFEATURES parameter '{maxFeatures}'. MAXFEATURES must be a non-negative integer.",
                "maxFeatures");
        }

        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        var isHitsRequest = string.Equals(resultType, "hits", StringComparison.OrdinalIgnoreCase);

        try
        {
            var normalizedFilter = NormalizeLegacyFilterXml(filter);
            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
            var unknownTypes = GetUnknownRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (unknownTypes.Length > 0)
            {
                return CreateLegacyWfsException(
                    version,
                    "InvalidParameterValue",
                    unknownTypes.Length == 1
                        ? $"Unknown feature type '{unknownTypes[0]}'."
                        : $"Unknown feature types: {string.Join(", ", unknownTypes.Select(type => $"'{type}'"))}.",
                    "typeName");
            }

            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            var planSet = await BuildLayerQueryPlansAsync(
                selectedTypes,
                propertyName,
                sortBy,
                bbox,
                normalizedFilter,
                featureId,
                srsName,
                offset: 0,
                count: limit,
                cancellationToken).ConfigureAwait(false);

            if (isHitsRequest)
            {
                return CreateLegacyFeatureCollectionResult(context, version, [], planSet.TotalMatched, reportMatchedCount: true);
            }

            return await BuildLegacyFeatureCollectionResultAsync(context, version, planSet, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Fes20ParseException)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return CreateLegacyWfsException(version, "InvalidParameterValue", "Invalid WFS parameter value; see logs for details.");
        }
        catch (WfsQueryException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return CreateLegacyWfsException(version, ex.ExceptionCode, ex.Message, ex.Locator);
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.GetFeature, ex.Message);
            return CreateLegacyWfsException(version, "NoApplicableCode", "Failed to process GetFeature request.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    internal static IResult CreateLegacyWfsException(
        string version,
        string code,
        string message,
        string? locator = null,
        int statusCode = StatusCodes.Status400BadRequest)
    {
        var xml = IsWfs10(version)
            ? BuildWfs10Exception(code, message, locator)
            : BuildWfs11Exception(code, message, locator);
        return new LegacyXmlResult(xml, "application/xml", statusCode);
    }

    private static string BuildWfs11Capabilities(string wfsUrl, IReadOnlyList<FeatureType> featureTypes)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine($"""<wfs:WFS_Capabilities xmlns:wfs="{LegacyWfsNamespace}" xmlns:ows="{Ows10Namespace}" xmlns:ogc="{OgcFilterNamespace}" xmlns:gml="{GmlLegacyNamespace}" xmlns:{FeatureNamespacePrefix}="{FeatureNamespaceUri}" xmlns:cdf="http://www.opengis.net/cite/data" xmlns:cgf="http://www.opengis.net/cite/geometry" xmlns:xlink="{Wfs20Utilities.XLinkNamespace}" xmlns:xsi="{Wfs20Utilities.XsiNamespace}" version="1.1.0" updateSequence="{Wfs20Utilities.CurrentUpdateSequence}" xsi:schemaLocation="{LegacyWfsNamespace} http://schemas.opengis.net/wfs/1.1.0/wfs.xsd">""");
        sb.AppendLine("  <ows:ServiceIdentification>");
        sb.AppendLine("    <ows:Title>Honua WFS 1.1.0</ows:Title>");
        sb.AppendLine("    <ows:Abstract>Honua WFS 1.1.0 compatibility endpoint.</ows:Abstract>");
        sb.AppendLine("    <ows:ServiceType>WFS</ows:ServiceType>");
        sb.AppendLine("    <ows:ServiceTypeVersion>1.1.0</ows:ServiceTypeVersion>");
        sb.AppendLine("    <ows:Fees>NONE</ows:Fees>");
        sb.AppendLine("    <ows:AccessConstraints>NONE</ows:AccessConstraints>");
        sb.AppendLine("  </ows:ServiceIdentification>");
        sb.AppendLine("  <ows:ServiceProvider>");
        sb.AppendLine("    <ows:ProviderName>Honua</ows:ProviderName>");
        sb.AppendLine($"""    <ows:ProviderSite xlink:href="{XmlEscape(wfsUrl)}" />""");
        sb.AppendLine("    <ows:ServiceContact>");
        sb.AppendLine("      <ows:IndividualName>Honua Support</ows:IndividualName>");
        sb.AppendLine("      <ows:PositionName>Support</ows:PositionName>");
        sb.AppendLine("      <ows:ContactInfo>");
        sb.AppendLine("        <ows:OnlineResource xlink:href=\"https://honua.io\" />");
        sb.AppendLine("      </ows:ContactInfo>");
        sb.AppendLine("      <ows:Role>pointOfContact</ows:Role>");
        sb.AppendLine("    </ows:ServiceContact>");
        sb.AppendLine("  </ows:ServiceProvider>");
        AppendWfs11OperationsMetadata(sb, wfsUrl);
        AppendWfs11FeatureTypeList(sb, featureTypes);
        AppendWfs11FilterCapabilities(sb);
        sb.AppendLine("</wfs:WFS_Capabilities>");
        return sb.ToString();
    }

    private static string BuildWfs10Capabilities(string wfsUrl, IReadOnlyList<FeatureType> featureTypes)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine($"""<WFS_Capabilities xmlns="{LegacyWfsNamespace}" xmlns:ogc="{OgcFilterNamespace}" xmlns:gml="{GmlLegacyNamespace}" xmlns:{FeatureNamespacePrefix}="{FeatureNamespaceUri}" xmlns:cdf="http://www.opengis.net/cite/data" xmlns:cgf="http://www.opengis.net/cite/geometry" xmlns:xlink="{Wfs20Utilities.XLinkNamespace}" xmlns:xsi="{Wfs20Utilities.XsiNamespace}" version="1.0.0" updateSequence="{Wfs20Utilities.CurrentUpdateSequence}" xsi:schemaLocation="{LegacyWfsNamespace} http://schemas.opengis.net/wfs/1.0.0/WFS-capabilities.xsd">""");
        sb.AppendLine("  <Service>");
        sb.AppendLine("    <Name>Honua WFS</Name>");
        sb.AppendLine("    <Title>Honua WFS 1.0.0</Title>");
        sb.AppendLine("    <Abstract>Honua WFS 1.0.0 compatibility endpoint.</Abstract>");
        sb.AppendLine("    <OnlineResource>http://honua.io</OnlineResource>");
        sb.AppendLine("    <Fees>NONE</Fees>");
        sb.AppendLine("    <AccessConstraints>NONE</AccessConstraints>");
        sb.AppendLine("  </Service>");
        sb.AppendLine("  <Capability>");
        sb.AppendLine("    <Request>");
        AppendWfs10Operation(sb, "GetCapabilities", wfsUrl, includeSchemaDescriptionLanguage: false, includeResultFormat: false);
        AppendWfs10Operation(sb, "DescribeFeatureType", wfsUrl, includeSchemaDescriptionLanguage: true, includeResultFormat: false);
        AppendWfs10Operation(sb, "GetFeature", wfsUrl, includeSchemaDescriptionLanguage: false, includeResultFormat: true);
        sb.AppendLine("    </Request>");
        sb.AppendLine("  </Capability>");
        AppendWfs10FeatureTypeList(sb, featureTypes);
        AppendWfs10FilterCapabilities(sb);
        sb.AppendLine("</WFS_Capabilities>");
        return sb.ToString();
    }

    private static void AppendWfs11OperationsMetadata(StringBuilder sb, string wfsUrl)
    {
        sb.AppendLine("  <ows:OperationsMetadata>");
        AppendWfs11Operation(sb, "GetCapabilities", wfsUrl);
        AppendWfs11Operation(sb, "DescribeFeatureType", wfsUrl);
        AppendWfs11Operation(sb, "GetFeature", wfsUrl);
        sb.AppendLine("  </ows:OperationsMetadata>");
    }

    private static void AppendWfs11Operation(StringBuilder sb, string name, string wfsUrl)
    {
        sb.Append("    <ows:Operation name=\"").Append(XmlEscape(name)).AppendLine("\">");
        sb.AppendLine("      <ows:DCP>");
        sb.AppendLine("        <ows:HTTP>");
        sb.Append("          <ows:Get xlink:href=\"").Append(XmlEscape(wfsUrl)).AppendLine("\" />");
        sb.Append("          <ows:Post xlink:href=\"").Append(XmlEscape(wfsUrl)).AppendLine("\" />");
        sb.AppendLine("        </ows:HTTP>");
        sb.AppendLine("      </ows:DCP>");
        sb.AppendLine("    </ows:Operation>");
    }

    private static void AppendWfs10Operation(
        StringBuilder sb,
        string name,
        string wfsUrl,
        bool includeSchemaDescriptionLanguage,
        bool includeResultFormat)
    {
        sb.Append("      <").Append(name).AppendLine(">");
        if (includeSchemaDescriptionLanguage)
        {
            sb.AppendLine("        <SchemaDescriptionLanguage>");
            sb.AppendLine("          <XMLSCHEMA />");
            sb.AppendLine("        </SchemaDescriptionLanguage>");
        }

        if (includeResultFormat)
        {
            sb.AppendLine("        <ResultFormat>");
            sb.AppendLine("          <GML2 />");
            sb.AppendLine("        </ResultFormat>");
        }

        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.Append("            <Get onlineResource=\"").Append(XmlEscape(wfsUrl)).AppendLine("\" />");
        sb.Append("            <Post onlineResource=\"").Append(XmlEscape(wfsUrl)).AppendLine("\" />");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.Append("      </").Append(name).AppendLine(">");
    }

    private static void AppendWfs11FeatureTypeList(StringBuilder sb, IReadOnlyList<FeatureType> featureTypes)
    {
        sb.AppendLine("  <wfs:FeatureTypeList>");
        foreach (var featureType in featureTypes)
        {
            sb.AppendLine("    <wfs:FeatureType>");
            sb.Append("      <wfs:Name>").Append(XmlEscape(featureType.Name)).AppendLine("</wfs:Name>");
            sb.Append("      <wfs:Title>").Append(XmlEscape(featureType.Title)).AppendLine("</wfs:Title>");
            if (!string.IsNullOrWhiteSpace(featureType.Abstract))
            {
                sb.Append("      <wfs:Abstract>").Append(XmlEscape(featureType.Abstract!)).AppendLine("</wfs:Abstract>");
            }

            sb.Append("      <wfs:DefaultSRS>").Append(XmlEscape(featureType.DefaultCRS)).AppendLine("</wfs:DefaultSRS>");
            if (featureType.OtherCRS is not null)
            {
                foreach (var otherCrs in featureType.OtherCRS)
                {
                    sb.Append("      <wfs:OtherSRS>").Append(XmlEscape(otherCrs)).AppendLine("</wfs:OtherSRS>");
                }
            }

            AppendWgs84BoundingBox(sb, featureType.WGS84BoundingBox, "      ", "ows");
            sb.AppendLine("    </wfs:FeatureType>");
        }

        sb.AppendLine("  </wfs:FeatureTypeList>");
    }

    private static void AppendWfs10FeatureTypeList(StringBuilder sb, IReadOnlyList<FeatureType> featureTypes)
    {
        sb.AppendLine("  <FeatureTypeList>");
        sb.AppendLine("    <Operations>");
        sb.AppendLine("      <Query />");
        sb.AppendLine("    </Operations>");
        foreach (var featureType in featureTypes)
        {
            sb.AppendLine("    <FeatureType>");
            sb.Append("      <Name>").Append(XmlEscape(featureType.Name)).AppendLine("</Name>");
            sb.Append("      <Title>").Append(XmlEscape(featureType.Title)).AppendLine("</Title>");
            if (!string.IsNullOrWhiteSpace(featureType.Abstract))
            {
                sb.Append("      <Abstract>").Append(XmlEscape(featureType.Abstract!)).AppendLine("</Abstract>");
            }

            sb.Append("      <SRS>").Append(XmlEscape(ToLegacyEpsgSrs(featureType.DefaultCRS))).AppendLine("</SRS>");
            AppendLatLongBoundingBox(sb, featureType.WGS84BoundingBox, "      ");
            sb.AppendLine("    </FeatureType>");
        }

        sb.AppendLine("  </FeatureTypeList>");
    }

    private static void AppendWfs10FilterCapabilities(StringBuilder sb)
    {
        sb.AppendLine("  <ogc:Filter_Capabilities>");
        sb.AppendLine("    <ogc:Spatial_Capabilities>");
        sb.AppendLine("      <ogc:Spatial_Operators>");
        sb.AppendLine("        <ogc:BBOX />");
        sb.AppendLine("        <ogc:Intersect />");
        sb.AppendLine("      </ogc:Spatial_Operators>");
        sb.AppendLine("    </ogc:Spatial_Capabilities>");
        sb.AppendLine("    <ogc:Scalar_Capabilities>");
        sb.AppendLine("      <ogc:Logical_Operators />");
        sb.AppendLine("      <ogc:Comparison_Operators>");
        sb.AppendLine("        <ogc:Simple_Comparisons />");
        sb.AppendLine("        <ogc:Like />");
        sb.AppendLine("        <ogc:Between />");
        sb.AppendLine("        <ogc:NullCheck />");
        sb.AppendLine("      </ogc:Comparison_Operators>");
        sb.AppendLine("    </ogc:Scalar_Capabilities>");
        sb.AppendLine("  </ogc:Filter_Capabilities>");
    }

    private static void AppendLegacyFilterCapabilities(StringBuilder sb, string prefix)
    {
        sb.Append("  <").Append(prefix).AppendLine(":Filter_Capabilities>");
        sb.Append("    <").Append(prefix).AppendLine(":Spatial_Capabilities>");
        sb.Append("      <").Append(prefix).AppendLine(":Spatial_Operators>");
        sb.Append("        <").Append(prefix).AppendLine(":BBOX/>");
        sb.Append("        <").Append(prefix).AppendLine(":Intersects/>");
        sb.Append("      </").Append(prefix).AppendLine(":Spatial_Operators>");
        sb.Append("    </").Append(prefix).AppendLine(":Spatial_Capabilities>");
        sb.Append("    <").Append(prefix).AppendLine(":Scalar_Capabilities>");
        sb.Append("      <").Append(prefix).AppendLine(":Logical_Operators/>");
        sb.Append("      <").Append(prefix).AppendLine(":Comparison_Operators>");
        sb.Append("        <").Append(prefix).AppendLine(":Simple_Comparisons/>");
        sb.Append("        <").Append(prefix).AppendLine(":Like/>");
        sb.Append("        <").Append(prefix).AppendLine(":Between/>");
        sb.Append("        <").Append(prefix).AppendLine(":NullCheck/>");
        sb.Append("      </").Append(prefix).AppendLine(":Comparison_Operators>");
        sb.Append("    </").Append(prefix).AppendLine(":Scalar_Capabilities>");
        sb.Append("  </").Append(prefix).AppendLine(":Filter_Capabilities>");
    }

    private static void AppendWfs11FilterCapabilities(StringBuilder sb)
    {
        sb.AppendLine("  <ogc:Filter_Capabilities>");
        sb.AppendLine("    <ogc:Spatial_Capabilities>");
        sb.AppendLine("      <ogc:GeometryOperands>");
        sb.AppendLine("        <ogc:GeometryOperand>gml:Envelope</ogc:GeometryOperand>");
        sb.AppendLine("        <ogc:GeometryOperand>gml:Point</ogc:GeometryOperand>");
        sb.AppendLine("        <ogc:GeometryOperand>gml:LineString</ogc:GeometryOperand>");
        sb.AppendLine("        <ogc:GeometryOperand>gml:Polygon</ogc:GeometryOperand>");
        sb.AppendLine("      </ogc:GeometryOperands>");
        sb.AppendLine("      <ogc:SpatialOperators>");
        sb.AppendLine("        <ogc:SpatialOperator name=\"BBOX\" />");
        sb.AppendLine("        <ogc:SpatialOperator name=\"Intersects\" />");
        sb.AppendLine("      </ogc:SpatialOperators>");
        sb.AppendLine("    </ogc:Spatial_Capabilities>");
        sb.AppendLine("    <ogc:Scalar_Capabilities>");
        sb.AppendLine("      <ogc:LogicalOperators />");
        sb.AppendLine("      <ogc:ComparisonOperators>");
        sb.AppendLine("        <ogc:ComparisonOperator>LessThan</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>GreaterThan</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>LessThanEqualTo</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>GreaterThanEqualTo</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>EqualTo</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>NotEqualTo</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>Like</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>Between</ogc:ComparisonOperator>");
        sb.AppendLine("        <ogc:ComparisonOperator>NullCheck</ogc:ComparisonOperator>");
        sb.AppendLine("      </ogc:ComparisonOperators>");
        sb.AppendLine("    </ogc:Scalar_Capabilities>");
        sb.AppendLine("    <ogc:Id_Capabilities>");
        sb.AppendLine("      <ogc:FID />");
        sb.AppendLine("      <ogc:EID />");
        sb.AppendLine("    </ogc:Id_Capabilities>");
        sb.AppendLine("  </ogc:Filter_Capabilities>");
    }

    private static void AppendWgs84BoundingBox(StringBuilder sb, WGS84BoundingBox? bbox, string indent, string prefix)
    {
        if (bbox is null)
        {
            return;
        }

        sb.Append(indent).Append('<').Append(prefix).AppendLine(":WGS84BoundingBox>");
        sb.Append(indent).Append("  <").Append(prefix).Append(":LowerCorner>").Append(XmlEscape(bbox.LowerCorner)).Append("</").Append(prefix).AppendLine(":LowerCorner>");
        sb.Append(indent).Append("  <").Append(prefix).Append(":UpperCorner>").Append(XmlEscape(bbox.UpperCorner)).Append("</").Append(prefix).AppendLine(":UpperCorner>");
        sb.Append(indent).Append("</").Append(prefix).AppendLine(":WGS84BoundingBox>");
    }

    private static void AppendLatLongBoundingBox(StringBuilder sb, WGS84BoundingBox? bbox, string indent)
    {
        if (bbox is null ||
            !TryParseCornerPair(bbox.LowerCorner, out var minX, out var minY) ||
            !TryParseCornerPair(bbox.UpperCorner, out var maxX, out var maxY))
        {
            return;
        }

        sb.Append(indent)
            .Append("<LatLongBoundingBox minx=\"")
            .Append(minX.ToString("0.###############", CultureInfo.InvariantCulture))
            .Append("\" miny=\"")
            .Append(minY.ToString("0.###############", CultureInfo.InvariantCulture))
            .Append("\" maxx=\"")
            .Append(maxX.ToString("0.###############", CultureInfo.InvariantCulture))
            .Append("\" maxy=\"")
            .Append(maxY.ToString("0.###############", CultureInfo.InvariantCulture))
            .AppendLine("\" />");
    }

    private async Task<IResult> BuildLegacyFeatureCollectionResultAsync(
        HttpContext context,
        string version,
        LayerQueryPlanSet planSet,
        CancellationToken cancellationToken)
    {
        var queryResults = new List<(LayerQueryPlan Plan, ImmutableArray<Feature> Features)>(planSet.Plans.Length);
        foreach (var plan in planSet.Plans)
        {
            var result = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken)
                .ConfigureAwait(false);
            queryResults.Add((plan, result.Items));
        }

        var returnedCount = queryResults.Sum(entry => entry.Features.Length);
        return CreateLegacyFeatureCollectionResult(context, version, queryResults, planSet.TotalMatched, returnedCount);
    }

    private static LegacyXmlResult CreateLegacyFeatureCollectionResult(
        HttpContext context,
        string version,
        IReadOnlyList<(LayerQueryPlan Plan, ImmutableArray<Feature> Features)> queryResults,
        long totalMatched,
        int? returnedCount = null,
        bool reportMatchedCount = false)
    {
        var count = returnedCount ?? queryResults.Sum(entry => entry.Features.Length);
        var numberOfFeatures = reportMatchedCount ? totalMatched : count;
        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "FeatureCollection", LegacyWfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, GmlLegacyNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            foreach (var descriptor in queryResults
                .Select(entry => entry.Plan.Descriptor)
                .Where(descriptor => !descriptor.NamespacePrefix.Equals(FeatureNamespacePrefix, StringComparison.Ordinal))
                .DistinctBy(descriptor => descriptor.NamespacePrefix))
            {
                writer.WriteAttributeString("xmlns", descriptor.NamespacePrefix, null, descriptor.NamespaceUri);
            }

            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
            writer.WriteAttributeString(
                "xsi",
                "schemaLocation",
                Wfs20Utilities.XsiNamespace,
                BuildLegacyFeatureCollectionSchemaLocation(context, version, queryResults.Select(entry => entry.Plan.Descriptor)));
            if (!IsWfs10(version))
            {
                writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("numberOfFeatures", numberOfFeatures.ToString(CultureInfo.InvariantCulture));
            }

            if (IsWfs10(version))
            {
                WriteLegacyNullBoundedBy(writer);
            }

            foreach (var queryResult in queryResults)
            {
                foreach (var feature in queryResult.Features)
                {
                    WriteLegacyFeatureMember(writer, version, queryResult.Plan, feature);
                }
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return new LegacyXmlResult(xml, GetLegacyGmlMediaType(version));
    }

    private static void WriteLegacyNullBoundedBy(XmlWriter writer)
    {
        writer.WriteStartElement("gml", "boundedBy", GmlLegacyNamespace);
        writer.WriteElementString("gml", "null", GmlLegacyNamespace, "unknown");
        writer.WriteEndElement();
    }

    private static string BuildLegacyFeatureCollectionSchemaLocation(
        HttpContext context,
        string version,
        IEnumerable<WfsFeatureTypeDescriptor> descriptors)
    {
        var wfsUrl = $"{BaseUrlResolver.GetBaseUrl(context)}/wfs";
        var wfsSchema = IsWfs10(version)
            ? "http://schemas.opengis.net/wfs/1.0.0/WFS-basic.xsd"
            : "http://schemas.opengis.net/wfs/1.1.0/wfs.xsd";

        var values = new List<string>();
        foreach (var group in descriptors
            .GroupBy(descriptor => descriptor.NamespaceUri)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var typeNames = string.Join(",", group.Select(descriptor => descriptor.QualifiedName));
            var schemaUrl = $"{wfsUrl}?service=WFS&version={Uri.EscapeDataString(version)}&request=DescribeFeatureType&typeName={Uri.EscapeDataString(typeNames)}";
            values.Add(group.Key);
            values.Add(schemaUrl);
        }

        values.Add(LegacyWfsNamespace);
        values.Add(wfsSchema);
        return string.Join(" ", values);
    }

    private static string GetLegacyGmlMediaType(string version)
        => IsWfs10(version)
            ? "text/xml;subtype=gml/2.1.2"
            : "text/xml;subtype=gml/3.1.1";

    private static void WriteLegacyFeatureMember(XmlWriter writer, string version, LayerQueryPlan plan, Feature feature)
    {
        writer.WriteStartElement("gml", "featureMember", GmlLegacyNamespace);
        writer.WriteStartElement(plan.Descriptor.NamespacePrefix, plan.Descriptor.LocalName, plan.Descriptor.NamespaceUri);
        if (IsWfs10(version))
        {
            writer.WriteAttributeString("fid", BuildFeatureId(plan.Descriptor, feature.Id));
        }
        else
        {
            writer.WriteAttributeString("gml", "id", GmlLegacyNamespace, BuildFeatureId(plan.Descriptor, feature.Id));
        }

        var writeGeometryAfterAttributes = ShouldWriteLegacyGeometryAfterAttributes(plan.Descriptor);
        if (IsWfs10(version))
        {
            WriteWfs10CiteBaseProperties(writer, plan.Descriptor);
        }

        if (!writeGeometryAfterAttributes)
        {
            WriteLegacyGeometryField(writer, version, plan, feature);
        }

        foreach (var field in GetLegacyProjectedAttributeFields(plan.Descriptor, plan.Query))
        {
            var hasValue = feature.Attributes.TryGetValue(field.Name, out var value) && value is not null;
            if (!hasValue && field.Nullable && IsCiteFeatureType(plan.Descriptor))
            {
                continue;
            }

            writer.WriteStartElement(
                GetFieldNamespacePrefix(plan.Descriptor, field),
                XmlConvert.EncodeLocalName(field.Name),
                GetFieldNamespaceUri(plan.Descriptor, field));
            if (!hasValue)
            {
                if (field.Nullable)
                {
                    writer.WriteAttributeString("xsi", "nil", Wfs20Utilities.XsiNamespace, XmlConvert.ToString(true));
                }
            }
            else
            {
                writer.WriteString(ConvertToInvariantString(value));
            }

            writer.WriteEndElement();
        }

        if (writeGeometryAfterAttributes)
        {
            WriteLegacyGeometryField(writer, version, plan, feature);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteLegacyGeometryField(XmlWriter writer, string version, LayerQueryPlan plan, Feature feature)
    {
        if (plan.Descriptor.Layer.GeometryField is null || feature.Geometry is null)
        {
            return;
        }

        writer.WriteStartElement(
            GetFieldNamespacePrefix(plan.Descriptor, plan.Descriptor.Layer.GeometryField),
            XmlConvert.EncodeLocalName(plan.Descriptor.Layer.GeometryField.Name),
            GetFieldNamespaceUri(plan.Descriptor, plan.Descriptor.Layer.GeometryField));
        WriteLegacyGeometry(writer, version, plan, feature.Geometry);
        writer.WriteEndElement();
    }

    private static IEnumerable<FieldDefinition> GetLegacyProjectedAttributeFields(
        WfsFeatureTypeDescriptor descriptor,
        FeatureQuery query)
    {
        var fields = GetProjectedAttributeFields(descriptor.Layer, query);
        return IsCiteFeatureType(descriptor)
            ? fields.Where(field =>
                !field.Name.Equals("objectid", StringComparison.OrdinalIgnoreCase) &&
                !field.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
            : fields;
    }

    private static void WriteWfs10CiteBaseProperties(XmlWriter writer, WfsFeatureTypeDescriptor descriptor)
    {
        if (!string.Equals(descriptor.NamespaceUri, "http://www.opengis.net/cite/data", StringComparison.Ordinal) ||
            !string.Equals(descriptor.LocalName, "Other", StringComparison.Ordinal))
        {
            return;
        }

        writer.WriteStartElement("gml", "boundedBy", GmlLegacyNamespace);
        writer.WriteStartElement("gml", "Box", GmlLegacyNamespace);
        writer.WriteAttributeString("srsName", "EPSG:32615");
        writer.WriteElementString("gml", "coordinates", GmlLegacyNamespace, "500000,500000 500100,500100");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static bool ShouldWriteLegacyGeometryAfterAttributes(WfsFeatureTypeDescriptor descriptor)
        => descriptor.NamespaceUri == "http://www.opengis.net/cite/geometry" ||
           (descriptor.NamespaceUri == "http://www.opengis.net/cite/data" &&
            descriptor.LocalName is "Nulls" or "Locks");

    private static string GetFieldNamespacePrefix(WfsFeatureTypeDescriptor descriptor, FieldDefinition field)
        => IsCiteFeatureType(descriptor) && field.Name.EndsWith("Property", StringComparison.OrdinalIgnoreCase)
            ? "gml"
            : descriptor.NamespacePrefix;

    private static string GetFieldNamespaceUri(WfsFeatureTypeDescriptor descriptor, FieldDefinition field)
        => IsCiteFeatureType(descriptor) && field.Name.EndsWith("Property", StringComparison.OrdinalIgnoreCase)
            ? GmlLegacyNamespace
            : descriptor.NamespaceUri;

    private static bool IsCiteFeatureType(WfsFeatureTypeDescriptor descriptor)
        => descriptor.NamespaceUri is "http://www.opengis.net/cite/data" or "http://www.opengis.net/cite/geometry";

    private static void WriteLegacyGeometry(XmlWriter writer, string version, LayerQueryPlan plan, byte[] geometryWkb)
    {
        var geometry = LegacyWkbReader.Read(geometryWkb);
        var outputSrid = plan.Query.OutputSrid ?? plan.Query.SpatialReferenceSrid ?? plan.Descriptor.Layer.SpatialReference.ToSrid();
        var srsName = IsWfs10(version) ? $"EPSG:{outputSrid.ToString(CultureInfo.InvariantCulture)}" : FormatCrs(outputSrid);
        var axisOrder = IsWfs10(version) ? AxisOrder.EastNorth : plan.Query.OutputAxisOrder ?? AxisOrder.EastNorth;

        if (IsWfs10(version))
        {
            WriteGml2Geometry(writer, geometry, srsName, axisOrder, includeSrsName: true);
        }
        else
        {
            WriteGml31Geometry(writer, geometry, srsName, axisOrder, includeSrsName: true);
        }
    }

    private static void WriteGml31Geometry(XmlWriter writer, Geometry geometry, string srsName, AxisOrder axisOrder, bool includeSrsName)
    {
        switch (geometry)
        {
            case Point point:
                writer.WriteStartElement("gml", "Point", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                writer.WriteElementString("gml", "pos", GmlLegacyNamespace, FormatCoordinatePair(point.Coordinate, axisOrder, separator: " "));
                writer.WriteEndElement();
                break;
            case LineString line:
                writer.WriteStartElement("gml", "LineString", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                writer.WriteElementString("gml", "posList", GmlLegacyNamespace, FormatPosList(line.Coordinates, axisOrder));
                writer.WriteEndElement();
                break;
            case Polygon polygon:
                writer.WriteStartElement("gml", "Polygon", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                writer.WriteStartElement("gml", "exterior", GmlLegacyNamespace);
                writer.WriteStartElement("gml", "LinearRing", GmlLegacyNamespace);
                writer.WriteElementString("gml", "posList", GmlLegacyNamespace, FormatPosList(polygon.ExteriorRing.Coordinates, axisOrder));
                writer.WriteEndElement();
                writer.WriteEndElement();
                foreach (var interior in Enumerable.Range(0, polygon.NumInteriorRings).Select(polygon.GetInteriorRingN))
                {
                    writer.WriteStartElement("gml", "interior", GmlLegacyNamespace);
                    writer.WriteStartElement("gml", "LinearRing", GmlLegacyNamespace);
                    writer.WriteElementString("gml", "posList", GmlLegacyNamespace, FormatPosList(interior.Coordinates, axisOrder));
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
            case GeometryCollection collection:
                writer.WriteStartElement("gml", "MultiGeometry", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                for (var i = 0; i < collection.NumGeometries; i++)
                {
                    writer.WriteStartElement("gml", "geometryMember", GmlLegacyNamespace);
                    WriteGml31Geometry(writer, collection.GetGeometryN(i), srsName, axisOrder, includeSrsName: false);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
        }
    }

    private static void WriteGml2Geometry(XmlWriter writer, Geometry geometry, string srsName, AxisOrder axisOrder, bool includeSrsName)
    {
        switch (geometry)
        {
            case Point point:
                writer.WriteStartElement("gml", "Point", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                writer.WriteElementString("gml", "coordinates", GmlLegacyNamespace, FormatCoordinatePair(point.Coordinate, axisOrder, separator: ","));
                writer.WriteEndElement();
                break;
            case LineString line:
                writer.WriteStartElement("gml", "LineString", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                writer.WriteElementString("gml", "coordinates", GmlLegacyNamespace, FormatCoordinates(line.Coordinates, axisOrder));
                writer.WriteEndElement();
                break;
            case Polygon polygon:
                writer.WriteStartElement("gml", "Polygon", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                writer.WriteStartElement("gml", "outerBoundaryIs", GmlLegacyNamespace);
                writer.WriteStartElement("gml", "LinearRing", GmlLegacyNamespace);
                writer.WriteElementString("gml", "coordinates", GmlLegacyNamespace, FormatCoordinates(polygon.ExteriorRing.Coordinates, axisOrder));
                writer.WriteEndElement();
                writer.WriteEndElement();
                foreach (var interior in Enumerable.Range(0, polygon.NumInteriorRings).Select(polygon.GetInteriorRingN))
                {
                    writer.WriteStartElement("gml", "innerBoundaryIs", GmlLegacyNamespace);
                    writer.WriteStartElement("gml", "LinearRing", GmlLegacyNamespace);
                    writer.WriteElementString("gml", "coordinates", GmlLegacyNamespace, FormatCoordinates(interior.Coordinates, axisOrder));
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
            case MultiPoint multiPoint:
                writer.WriteStartElement("gml", "MultiPoint", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                for (var i = 0; i < multiPoint.NumGeometries; i++)
                {
                    writer.WriteStartElement("gml", "pointMember", GmlLegacyNamespace);
                    WriteGml2Geometry(writer, multiPoint.GetGeometryN(i), srsName, axisOrder, includeSrsName: false);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
            case MultiLineString multiLineString:
                writer.WriteStartElement("gml", "MultiLineString", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                for (var i = 0; i < multiLineString.NumGeometries; i++)
                {
                    writer.WriteStartElement("gml", "lineStringMember", GmlLegacyNamespace);
                    WriteGml2Geometry(writer, multiLineString.GetGeometryN(i), srsName, axisOrder, includeSrsName: false);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
            case MultiPolygon multiPolygon:
                writer.WriteStartElement("gml", "MultiPolygon", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                for (var i = 0; i < multiPolygon.NumGeometries; i++)
                {
                    writer.WriteStartElement("gml", "polygonMember", GmlLegacyNamespace);
                    WriteGml2Geometry(writer, multiPolygon.GetGeometryN(i), srsName, axisOrder, includeSrsName: false);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
            case GeometryCollection collection:
                writer.WriteStartElement("gml", "MultiGeometry", GmlLegacyNamespace);
                WriteSrsName(writer, srsName, includeSrsName);
                for (var i = 0; i < collection.NumGeometries; i++)
                {
                    writer.WriteStartElement("gml", "geometryMember", GmlLegacyNamespace);
                    WriteGml2Geometry(writer, collection.GetGeometryN(i), srsName, axisOrder, includeSrsName: false);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
        }
    }

    private static string GenerateLegacySchemaForTypes(IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes, string version)
    {
        if (IsWfs10(version) &&
            featureTypes.Any(featureType => featureType.NamespaceUri.StartsWith("http://www.opengis.net/cite/", StringComparison.Ordinal)))
        {
            return GenerateWfs10CiteSchema(featureTypes);
        }

        var gmlSchema = IsWfs10(version)
            ? "http://schemas.opengis.net/gml/2.1.2/feature.xsd"
            : "http://schemas.opengis.net/gml/3.1.1/base/gml.xsd";
        var schema = new StringBuilder(4096);
        schema.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        schema.AppendLine($"""<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:gml="{GmlLegacyNamespace}" xmlns:{FeatureNamespacePrefix}="{FeatureNamespaceUri}" targetNamespace="{FeatureNamespaceUri}" elementFormDefault="qualified" version="1.0.0">""");
        schema.AppendLine($"""  <xsd:import namespace="{GmlLegacyNamespace}" schemaLocation="{gmlSchema}"/>""");
        schema.AppendLine();

        foreach (var featureType in featureTypes)
        {
            var typeName = XmlConvert.EncodeLocalName(featureType.LocalName);
            schema.AppendLine($"""  <xsd:element name="{typeName}" type="{FeatureNamespacePrefix}:{typeName}Type" substitutionGroup="gml:_Feature"/>""");
            schema.AppendLine($"""  <xsd:complexType name="{typeName}Type">""");
            schema.AppendLine("""    <xsd:complexContent>""");
            schema.AppendLine("""      <xsd:extension base="gml:AbstractFeatureType">""");
            schema.AppendLine("""        <xsd:sequence>""");

            if (featureType.Layer.GeometryField is not null)
            {
                schema.AppendLine($"""          <xsd:element name="{XmlConvert.EncodeLocalName(featureType.Layer.GeometryField.Name)}" type="gml:GeometryPropertyType" minOccurs="0" nillable="true"/>""");
            }

            foreach (var field in featureType.Layer.AttributeFields)
            {
                var minOccurs = field.Nullable ? " minOccurs=\"0\"" : string.Empty;
                var nillable = field.Nullable ? " nillable=\"true\"" : string.Empty;
                schema.AppendLine($"""          <xsd:element name="{XmlConvert.EncodeLocalName(field.Name)}" type="{MapXsdType(field.Type)}"{minOccurs}{nillable}/>""");
            }

            schema.AppendLine("""        </xsd:sequence>""");
            schema.AppendLine("""      </xsd:extension>""");
            schema.AppendLine("""    </xsd:complexContent>""");
            schema.AppendLine("""  </xsd:complexType>""");
            schema.AppendLine();
        }

        schema.AppendLine("</xsd:schema>");
        return schema.ToString();
    }

    private static string GenerateWfs10CiteSchema(IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes)
    {
        var useGeometrySchema = featureTypes.Count > 0 &&
            featureTypes.All(featureType => string.Equals(featureType.NamespaceUri, "http://www.opengis.net/cite/geometry", StringComparison.Ordinal));
        return useGeometrySchema ? Wfs10CiteGeometrySchema : Wfs10CiteDataSchema;
    }

    private const string Wfs10CiteDataSchema = """
<?xml version="1.0" encoding="UTF-8"?>
<xsd:schema targetNamespace="http://www.opengis.net/cite/data" xmlns:gml="http://www.opengis.net/gml" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:cdf="http://www.opengis.net/cite/data" elementFormDefault="qualified" attributeFormDefault="unqualified" version="0.2">
  <xsd:import namespace="http://www.opengis.net/gml" schemaLocation="http://schemas.opengis.net/gml/2.1.2/feature.xsd"/>
  <xsd:element name="Deletes" type="cdf:TransactionFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Inserts" type="cdf:TransactionFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Updates" type="cdf:TransactionFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Other" type="cdf:OtherFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Fifteen" type="cdf:EmptyFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Seven" type="cdf:EmptyFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Nulls" type="cdf:NullFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Locks" type="cdf:LockFeatureType" substitutionGroup="gml:_Feature"/>
  <xsd:complexType name="TransactionFeatureType">
    <xsd:complexContent>
      <xsd:extension base="gml:AbstractFeatureType">
        <xsd:sequence>
          <xsd:element name="id" type="xsd:string" minOccurs="0"/>
          <xsd:element ref="gml:pointProperty"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="OtherFeatureType">
    <xsd:complexContent>
      <xsd:extension base="gml:AbstractFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:pointProperty" minOccurs="0"/>
          <xsd:element name="string1" type="xsd:string"/>
          <xsd:element name="string2" type="xsd:string" minOccurs="0"/>
          <xsd:element name="integers" type="xsd:integer" minOccurs="0"/>
          <xsd:element name="dates" type="xsd:date" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="EmptyFeatureType">
    <xsd:complexContent>
      <xsd:extension base="gml:AbstractFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:pointProperty"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="NullFeatureType">
    <xsd:complexContent>
      <xsd:extension base="gml:AbstractFeatureType">
        <xsd:sequence>
          <xsd:element name="integers" type="xsd:integer" minOccurs="0"/>
          <xsd:element name="dates" type="xsd:date" minOccurs="0"/>
          <xsd:element ref="gml:pointProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="LockFeatureType">
    <xsd:complexContent>
      <xsd:extension base="gml:AbstractFeatureType">
        <xsd:sequence>
          <xsd:element name="id" type="xsd:string"/>
          <xsd:element ref="gml:pointProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
</xsd:schema>
""";

    private const string Wfs10CiteGeometrySchema = """
<?xml version="1.0" encoding="UTF-8"?>
<xsd:schema targetNamespace="http://www.opengis.net/cite/geometry" xmlns:gml="http://www.opengis.net/gml" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:cgf="http://www.opengis.net/cite/geometry" elementFormDefault="qualified" attributeFormDefault="unqualified" version="0.1">
  <xsd:import namespace="http://www.opengis.net/gml" schemaLocation="http://schemas.opengis.net/gml/2.1.2/feature.xsd"/>
  <xsd:element name="id" type="xsd:string"/>
  <xsd:element name="_SimpleFeature" type="cgf:AbstractSimpleFeatureType" abstract="true" substitutionGroup="gml:_Feature"/>
  <xsd:element name="Points" type="cgf:PointFeatureType" substitutionGroup="cgf:_SimpleFeature"/>
  <xsd:element name="Lines" type="cgf:LineStringFeatureType" substitutionGroup="cgf:_SimpleFeature"/>
  <xsd:element name="Polygons" type="cgf:PolygonFeatureType" substitutionGroup="cgf:_SimpleFeature"/>
  <xsd:element name="MPoints" type="cgf:MultiPointFeatureType" substitutionGroup="cgf:_SimpleFeature"/>
  <xsd:element name="MLines" type="cgf:MultiLineStringFeatureType" substitutionGroup="cgf:_SimpleFeature"/>
  <xsd:element name="MPolygons" type="cgf:MultiPolygonFeatureType" substitutionGroup="cgf:_SimpleFeature"/>
  <xsd:complexType name="AbstractSimpleFeatureType" abstract="true">
    <xsd:complexContent>
      <xsd:extension base="gml:AbstractFeatureType">
        <xsd:sequence>
          <xsd:element ref="cgf:id"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="PointFeatureType">
    <xsd:complexContent>
      <xsd:extension base="cgf:AbstractSimpleFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:pointProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="LineStringFeatureType">
    <xsd:complexContent>
      <xsd:extension base="cgf:AbstractSimpleFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:lineStringProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="PolygonFeatureType">
    <xsd:complexContent>
      <xsd:extension base="cgf:AbstractSimpleFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:polygonProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="MultiPointFeatureType">
    <xsd:complexContent>
      <xsd:extension base="cgf:AbstractSimpleFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:multiPointProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="MultiLineStringFeatureType">
    <xsd:complexContent>
      <xsd:extension base="cgf:AbstractSimpleFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:multiLineStringProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
  <xsd:complexType name="MultiPolygonFeatureType">
    <xsd:complexContent>
      <xsd:extension base="cgf:AbstractSimpleFeatureType">
        <xsd:sequence>
          <xsd:element ref="gml:multiPolygonProperty" minOccurs="0"/>
        </xsd:sequence>
      </xsd:extension>
    </xsd:complexContent>
  </xsd:complexType>
</xsd:schema>
""";

    private static string? NormalizeLegacyFilterXml(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        try
        {
            var document = SecureXmlDocumentParser.Parse(filter, LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new Fes20ParseException("FILTER must contain a root element.");
            return NormalizeLegacyFilterElement(root).ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException ex)
        {
            throw new Fes20ParseException("Invalid legacy WFS FILTER XML.", ex);
        }
    }

    private static XElement NormalizeLegacyFilterElement(XElement element)
    {
        if (TryCreateLegacyCoordinateGeometry(element, out var normalizedGeometry))
        {
            return normalizedGeometry;
        }

        var normalized = new XElement(GetNormalizedLegacyElementName(element));
        CopyNormalizedLegacyAttributes(element, normalized);

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    normalized.Add(NormalizeLegacyFilterElement(child));
                    break;
                case XCData cdata:
                    normalized.Add(new XCData(cdata.Value));
                    break;
                case XText text:
                    normalized.Add(new XText(text.Value));
                    break;
            }
        }

        return normalized;
    }

    private static XName GetNormalizedLegacyElementName(XElement element)
    {
        var localName = element.Name.LocalName switch
        {
            "PropertyName" => "ValueReference",
            "FeatureId" => "ResourceId",
            "outerBoundaryIs" => "exterior",
            "innerBoundaryIs" => "interior",
            _ => element.Name.LocalName
        };

        if (IsLegacyGmlElement(element) || IsKnownGmlElement(localName))
        {
            return XName.Get(localName, Wfs20Utilities.GmlNamespace);
        }

        if (IsLegacyFilterElement(element) || IsKnownFilterElement(localName))
        {
            return XName.Get(localName, Wfs20Utilities.FesNamespace);
        }

        return element.Name;
    }

    private static void CopyNormalizedLegacyAttributes(XElement source, XElement target)
    {
        foreach (var attribute in source.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            var attributeName = attribute.Name;
            if (string.Equals(source.Name.LocalName, "FeatureId", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(attribute.Name.LocalName, "fid", StringComparison.OrdinalIgnoreCase))
            {
                attributeName = "rid";
            }
            else if (string.Equals(attribute.Name.NamespaceName, GmlLegacyNamespace, StringComparison.Ordinal))
            {
                attributeName = XName.Get(attribute.Name.LocalName, Wfs20Utilities.GmlNamespace);
            }

            target.SetAttributeValue(attributeName, attribute.Value);
        }
    }

    private static bool TryCreateLegacyCoordinateGeometry(XElement element, out XElement normalizedGeometry)
    {
        normalizedGeometry = null!;
        if (!IsLegacyGmlElement(element) && !IsKnownGmlElement(element.Name.LocalName))
        {
            return false;
        }

        if (string.Equals(element.Name.LocalName, "Box", StringComparison.OrdinalIgnoreCase))
        {
            normalizedGeometry = CreateEnvelopeFromLegacyBox(element);
            return true;
        }

        if (!TryReadLegacyCoordinateTuples(element, out var coordinates))
        {
            return false;
        }

        if (string.Equals(element.Name.LocalName, "Point", StringComparison.OrdinalIgnoreCase))
        {
            normalizedGeometry = CreateNormalizedGmlElement(element, "Point");
            normalizedGeometry.Add(new XElement(
                XName.Get("pos", Wfs20Utilities.GmlNamespace),
                FormatLegacyCoordinate(coordinates[0])));
            return true;
        }

        if (string.Equals(element.Name.LocalName, "LineString", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(element.Name.LocalName, "LinearRing", StringComparison.OrdinalIgnoreCase))
        {
            normalizedGeometry = CreateNormalizedGmlElement(element, element.Name.LocalName);
            normalizedGeometry.Add(new XElement(
                XName.Get("posList", Wfs20Utilities.GmlNamespace),
                FormatLegacyCoordinateSequence(coordinates)));
            return true;
        }

        return false;
    }

    private static XElement CreateEnvelopeFromLegacyBox(XElement box)
    {
        if (!TryReadLegacyCoordinateTuples(box, out var coordinates) || coordinates.Length < 2)
        {
            throw new Fes20ParseException("GML Box must contain at least two coordinate tuples.");
        }

        var envelope = CreateNormalizedGmlElement(box, "Envelope");
        envelope.Add(new XElement(
            XName.Get("lowerCorner", Wfs20Utilities.GmlNamespace),
            FormatLegacyCoordinate(coordinates[0])));
        envelope.Add(new XElement(
            XName.Get("upperCorner", Wfs20Utilities.GmlNamespace),
            FormatLegacyCoordinate(coordinates[^1])));
        return envelope;
    }

    private static XElement CreateNormalizedGmlElement(XElement source, string localName)
    {
        var element = new XElement(XName.Get(localName, Wfs20Utilities.GmlNamespace));
        CopyNormalizedLegacyAttributes(source, element);
        return element;
    }

    private static bool TryReadLegacyCoordinateTuples(XElement element, out Coordinate[] coordinates)
    {
        var coordinatesElement = element.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, "coordinates", StringComparison.OrdinalIgnoreCase));
        if (coordinatesElement is not null)
        {
            coordinates = ParseLegacyCoordinatesElement(coordinatesElement);
            return true;
        }

        var coordElements = element.Elements()
            .Where(child => string.Equals(child.Name.LocalName, "coord", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (coordElements.Length > 0)
        {
            coordinates = coordElements.Select(ParseLegacyCoordElement).ToArray();
            return true;
        }

        coordinates = [];
        return false;
    }

    private static Coordinate[] ParseLegacyCoordinatesElement(XElement coordinatesElement)
    {
        var coordinateSeparator = coordinatesElement.Attribute("cs")?.Value ?? ",";
        var tupleSeparator = coordinatesElement.Attribute("ts")?.Value ?? " ";
        var decimalSeparator = coordinatesElement.Attribute("decimal")?.Value ?? ".";
        var tuples = SplitLegacyCoordinateTuples(coordinatesElement.Value, tupleSeparator);
        if (tuples.Length == 0)
        {
            throw new Fes20ParseException("GML coordinates must contain at least one coordinate tuple.");
        }

        var coordinates = new Coordinate[tuples.Length];
        for (var index = 0; index < tuples.Length; index++)
        {
            var ordinates = tuples[index].Split(
                coordinateSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ordinates.Length < 2)
            {
                throw new Fes20ParseException("GML coordinate tuple must contain at least two ordinates.");
            }

            coordinates[index] = new Coordinate(
                ParseLegacyOrdinate(ordinates[0], decimalSeparator),
                ParseLegacyOrdinate(ordinates[1], decimalSeparator));
        }

        return coordinates;
    }

    private static string[] SplitLegacyCoordinateTuples(string value, string tupleSeparator)
    {
        if (string.IsNullOrWhiteSpace(tupleSeparator))
        {
            return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return string.Equals(tupleSeparator, " ", StringComparison.Ordinal)
            ? value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : value.Split(tupleSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static Coordinate ParseLegacyCoordElement(XElement coordElement)
    {
        var x = coordElement.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, "X", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var y = coordElement.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, "Y", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y))
        {
            throw new Fes20ParseException("GML coord must contain X and Y ordinates.");
        }

        return new Coordinate(ParseLegacyOrdinate(x, "."), ParseLegacyOrdinate(y, "."));
    }

    private static double ParseLegacyOrdinate(string value, string decimalSeparator)
    {
        if (!string.Equals(decimalSeparator, ".", StringComparison.Ordinal))
        {
            value = value.Replace(decimalSeparator, ".", StringComparison.Ordinal);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ordinate) ||
            !double.IsFinite(ordinate))
        {
            throw new Fes20ParseException("GML coordinate ordinate must be a finite number.");
        }

        return ordinate;
    }

    private static string FormatLegacyCoordinate(Coordinate coordinate)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{coordinate.X:R} {coordinate.Y:R}");

    private static string FormatLegacyCoordinateSequence(Coordinate[] coordinates)
    {
        var builder = new StringBuilder(coordinates.Length * 24);
        for (var index = 0; index < coordinates.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(coordinates[index].X.ToString("R", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(coordinates[index].Y.ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool IsLegacyFilterElement(XElement element)
        => string.Equals(element.Name.NamespaceName, OgcFilterNamespace, StringComparison.Ordinal) ||
           string.Equals(element.Name.NamespaceName, Wfs20Utilities.FesNamespace, StringComparison.Ordinal);

    private static bool IsLegacyGmlElement(XElement element)
        => string.Equals(element.Name.NamespaceName, GmlLegacyNamespace, StringComparison.Ordinal) ||
           string.Equals(element.Name.NamespaceName, Wfs20Utilities.GmlNamespace, StringComparison.Ordinal);

    private static bool IsKnownFilterElement(string localName)
        => localName is
            "Filter" or
            "And" or
            "Or" or
            "Not" or
            "PropertyIsEqualTo" or
            "PropertyIsNotEqualTo" or
            "PropertyIsLessThan" or
            "PropertyIsGreaterThan" or
            "PropertyIsLessThanOrEqualTo" or
            "PropertyIsGreaterThanOrEqualTo" or
            "PropertyIsLike" or
            "PropertyIsNil" or
            "PropertyIsNull" or
            "PropertyIsBetween" or
            "LowerBoundary" or
            "UpperBoundary" or
            "BBOX" or
            "Intersects" or
            "Contains" or
            "Within" or
            "Crosses" or
            "Touches" or
            "Overlaps" or
            "Disjoint" or
            "Equals" or
            "DWithin" or
            "Beyond" or
            "Distance" or
            "ValueReference" or
            "Literal" or
            "ResourceId";

    private static bool IsKnownGmlElement(string localName)
        => localName is
            "Envelope" or
            "Box" or
            "lowerCorner" or
            "upperCorner" or
            "Point" or
            "LineString" or
            "Curve" or
            "Polygon" or
            "Surface" or
            "exterior" or
            "interior" or
            "outerBoundaryIs" or
            "innerBoundaryIs" or
            "LinearRing" or
            "pos" or
            "posList" or
            "coordinates" or
            "coord" or
            "X" or
            "Y";

    private static bool IsLegacyXmlOutputFormat(string version, string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
        {
            return true;
        }

        var normalized = outputFormat.Trim();
        if (string.Equals(normalized, "text/xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "application/xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetTextXmlGmlSubtype(normalized, out var subtype))
        {
            return IsWfs10(version)
                ? string.Equals(subtype, "gml/2.1.2", StringComparison.OrdinalIgnoreCase)
                : string.Equals(subtype, "gml/3.1.1", StringComparison.OrdinalIgnoreCase);
        }

        return IsWfs10(version)
            ? string.Equals(normalized, "GML2", StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalized, "GML3", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(normalized, "GML3.1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetTextXmlGmlSubtype(string outputFormat, out string subtype)
    {
        subtype = string.Empty;
        var parts = outputFormat.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "text/xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var part in parts[1..])
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == part.Length - 1)
            {
                continue;
            }

            var key = part[..separatorIndex].Trim();
            if (!string.Equals(key, "subtype", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            subtype = part[(separatorIndex + 1)..].Trim().Trim('"', '\'');
            return true;
        }

        return false;
    }

    private static void WriteSrsName(XmlWriter writer, string srsName, bool includeSrsName)
    {
        if (includeSrsName)
        {
            writer.WriteAttributeString("srsName", srsName);
        }
    }

    private static string FormatCoordinates(IEnumerable<Coordinate> coordinates, AxisOrder axisOrder)
        => string.Join(" ", coordinates.Select(coordinate => FormatCoordinatePair(coordinate, axisOrder, separator: ",")));

    private static string FormatPosList(IEnumerable<Coordinate> coordinates, AxisOrder axisOrder)
        => string.Join(" ", coordinates.Select(coordinate => FormatCoordinatePair(coordinate, axisOrder, separator: " ")));

    private static string FormatCoordinatePair(Coordinate coordinate, AxisOrder axisOrder, string separator)
    {
        var first = axisOrder == AxisOrder.NorthEast ? coordinate.Y : coordinate.X;
        var second = axisOrder == AxisOrder.NorthEast ? coordinate.X : coordinate.Y;
        return first.ToString("0.###############", CultureInfo.InvariantCulture) +
               separator +
               second.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static bool TryParseCornerPair(string value, out double first, out double second)
    {
        first = 0;
        second = 0;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out first) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out second);
    }

    private static string ToLegacyEpsgSrs(string crs)
    {
        var marker = crs.LastIndexOf(':');
        return marker >= 0 && marker + 1 < crs.Length
            ? "EPSG:" + crs[(marker + 1)..]
            : crs;
    }

    private static bool IsWfs10(string version)
        => string.Equals(version, Wfs10Version, StringComparison.OrdinalIgnoreCase);

    private static string BuildWfs11Exception(string code, string message, string? locator)
    {
        var locatorAttribute = string.IsNullOrWhiteSpace(locator)
            ? string.Empty
            : $" locator=\"{XmlEscape(locator!)}\"";
        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     version="1.0.0"
                     xsi:schemaLocation="http://www.opengis.net/ows http://schemas.opengis.net/ows/1.0.0/owsExceptionReport.xsd">
  <ows:Exception exceptionCode="{{XmlEscape(code)}}"{{locatorAttribute}}>
    <ows:ExceptionText>{{XmlEscape(message)}}</ows:ExceptionText>
  </ows:Exception>
</ows:ExceptionReport>
""";
    }

    private static string BuildWfs10Exception(string code, string message, string? locator)
    {
        var locatorAttribute = string.IsNullOrWhiteSpace(locator)
            ? string.Empty
            : $" locator=\"{XmlEscape(locator!)}\"";
        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<ogc:ServiceExceptionReport xmlns:ogc="http://www.opengis.net/ogc" version="1.2.0">
  <ogc:ServiceException code="{{XmlEscape(code)}}"{{locatorAttribute}}>{{XmlEscape(message)}}</ogc:ServiceException>
</ogc:ServiceExceptionReport>
""";
    }

    private sealed class LegacyXmlResult(string xml, string contentType, int statusCode = StatusCodes.Status200OK) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.Headers.ContentType = contentType;
            await httpContext.Response.WriteAsync(xml, Encoding.UTF8, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
