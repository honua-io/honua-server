// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const string InvalidGenerateKmlRequestMessage = "Invalid generateKml request parameters.";
    private const string KmlContentType = "application/vnd.google-earth.kml+xml";
    private const string KmzContentType = "application/vnd.google-earth.kmz";

    /// <summary>
    /// Handle MapServer generateKml requests.
    /// </summary>
    private static async Task<IResult> HandleGenerateKml(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            if (!TryNormalizeGenerateKmlOutputFormat(GetValue(values, "f"), out var outputFormat, out var formatError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Invalid output format.");
            }

            var layersValue = GetValue(values, "layers");
            if (HasEmptyLayerToken(layersValue))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter contains an empty layer id.");
            }

            if (HasNonIntegerExportLayerToken(layersValue))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter must contain integer layer ids.");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
            if (!serviceResult.IsValid)
            {
                var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
                if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }

                return StandardErrorHelpers.CreateNotFound(context, errorMessage);
            }

            var service = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
            if (accessError != null)
            {
                return accessError;
            }

            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            if (!TryParseLayerDefs(GetValue(values, "layerDefs"), queryValidator, out var layerDefs, out var layerDefsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, layerDefsError ?? "Invalid layerDefs parameter.");
            }

            if (!TryParseLayerTimeOptions(GetValue(values, "layerTimeOptions"), out var layerTimeOptions, out var layerTimeOptionsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, layerTimeOptionsError ?? "Invalid layerTimeOptions parameter.");
            }

            if (!TryParseDynamicLayers(GetValue(values, "dynamicLayers"), service, queryValidator, out var dynamicLayers, out var dynamicLayersError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, dynamicLayersError ?? "Invalid dynamicLayers parameter.");
            }

            var documentName = GetValue(values, "docName");
            if (string.IsNullOrWhiteSpace(documentName))
            {
                documentName = service.Name;
            }

            if (string.IsNullOrWhiteSpace(documentName))
            {
                documentName = "Map";
            }

            var mapConfig = service.Metadata?.MapServer;
            var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
            var maxFeaturesPerLayer = Math.Clamp(
                mapConfig?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer,
                1,
                limits.Query.MaxRecordCount);

            MapServerLog.GenerateKmlRequested(logger, serviceId, outputFormat);
            var stopwatch = Stopwatch.StartNew();
            using var activity = HonuaTelemetry.ActivitySource.StartActivity("MapServerGenerateKml", ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "generateKml");
            activity?.SetTag("honua.mapserver.output_format", outputFormat);

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var kmlFeatureStore = featureReader as IKmlFeatureStore;
            var wkbReader = new WKBReader();

            var renderLayers = ResolveRenderLayers(service, layersValue, dynamicLayers, context)
                .Where(static renderLayer => renderLayer.Layer.HasGeometry)
                .ToArray();

            var timeValue = GetValue(values, "time");
            var timeRelationValue = NormalizeTimeRelation(GetValue(values, "timeRelation"));

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = false,
                Indent = true
            };

            var totalFeatureCount = 0;
            var totalPlacemarkCount = 0;

            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
                writer.WriteStartElement("Document");
                writer.WriteElementString("name", documentName);

                foreach (var renderLayer in renderLayers)
                {
                    context.RequestAborted.ThrowIfCancellationRequested();

                    var layer = renderLayer.Layer;
                    layerDefs.TryGetValue(layer.Id, out var layerDef);
                    var combinedDefinition = CombineDefinitionExpressions(renderLayer.DefinitionExpression, layerDef);

                    if (!TryGetEffectiveTimeParameters(
                            timeValue,
                            timeRelationValue,
                            layer,
                            layerTimeOptions,
                            out var effectiveTime,
                            out var effectiveTimeRelation,
                            out var timeError))
                    {
                        return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time parameter.");
                    }

                    if (!TryBuildLayerSqlFilter(
                            filterExpressionService,
                            layer,
                            combinedDefinition,
                            effectiveTime,
                            effectiveTimeRelation,
                            out var sqlFilter,
                            out var filterError))
                    {
                        return StandardErrorHelpers.CreateBadRequest(context, filterError ?? "Invalid filter parameter.");
                    }

                    var featureQuery = new FeatureQuery
                    {
                        SpatialReferenceSrid = service.SpatialReference.Srid,
                        OutputSrid = 4326,
                        Limit = maxFeaturesPerLayer,
                        SqlFilter = sqlFilter
                    };

                    var objectIdField = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
                    var displayField = ResolveDisplayField(layer, objectIdField);

                    writer.WriteStartElement("Folder");
                    writer.WriteElementString("name", layer.Name);

                    if (kmlFeatureStore is not null)
                    {
                        var queryResult = await kmlFeatureStore.QueryKmlAsync(layer.Id, featureQuery, context.RequestAborted);
                        totalFeatureCount += queryResult.Items.Length;

                        foreach (var feature in queryResult.Items)
                        {
                            var displayValue = GetDisplayFieldValue(feature, displayField);
                            if (WritePlacemark(writer, feature, displayValue))
                            {
                                totalPlacemarkCount++;
                            }
                        }
                    }
                    else
                    {
                        var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, context.RequestAborted);
                        totalFeatureCount += queryResult.Items.Length;

                        foreach (var feature in queryResult.Items)
                        {
                            if (!TryReadGeometry(feature.Geometry, wkbReader, out var geometry))
                            {
                                continue;
                            }

                            var displayValue = GetDisplayFieldValue(feature, displayField);
                            if (WritePlacemark(writer, feature, displayValue, geometry!))
                            {
                                totalPlacemarkCount++;
                            }
                        }
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
                writer.Flush();
            }

            var kmlBytes = stream.ToArray();
            stopwatch.Stop();

            MapServerLog.GenerateKmlCompleted(logger, serviceId, totalPlacemarkCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, totalPlacemarkCount);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);
            activity?.SetTag("honua.mapserver.feature_count", totalFeatureCount);
            activity?.SetTag("honua.mapserver.placemark_count", totalPlacemarkCount);

            if (string.Equals(outputFormat, "kmz", StringComparison.OrdinalIgnoreCase))
            {
                var kmzBytes = CreateKmzArchive(kmlBytes);
                return Results.Bytes(kmzBytes, KmzContentType);
            }

            return Results.Bytes(kmlBytes, KmlContentType);
        }
        catch (ArgumentException ex)
        {
            MapServerLog.GenerateKmlFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateBadRequest(context, InvalidGenerateKmlRequestMessage);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.GenerateKmlFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer generateKml failed.");
        }
    }

    private static bool TryNormalizeGenerateKmlOutputFormat(string? format, out string normalized, out string? error)
    {
        normalized = "kml";
        error = null;

        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        var candidate = format.Trim().ToLowerInvariant();
        switch (candidate)
        {
            case "kml":
            case "kmz":
                normalized = candidate;
                return true;
            default:
                error = $"Output format '{format}' is not supported.";
                return false;
        }
    }

    private static bool TryReadGeometry(byte[]? wkb, WKBReader reader, out NtsGeometry? geometry)
    {
        geometry = null;
        if (wkb == null || wkb.Length < 5)
        {
            return false;
        }

        try
        {
            geometry = reader.Read(wkb);
            return geometry != null && !geometry.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    private static bool WritePlacemark(XmlWriter writer, Feature feature, string? displayValue, NtsGeometry geometry)
    {
        writer.WriteStartElement("Placemark");
        writer.WriteElementString(
            "name",
            string.IsNullOrWhiteSpace(displayValue)
                ? feature.Id.ToString(CultureInfo.InvariantCulture)
                : displayValue);

        WriteExtendedData(feature.Attributes, writer);

        var wroteGeometry = TryWriteKmlGeometry(writer, geometry);
        writer.WriteEndElement();
        return wroteGeometry;
    }

    private static bool WritePlacemark(XmlWriter writer, KmlFeature feature, string? displayValue)
    {
        writer.WriteStartElement("Placemark");
        writer.WriteElementString(
            "name",
            string.IsNullOrWhiteSpace(displayValue)
                ? feature.Id.ToString(CultureInfo.InvariantCulture)
                : displayValue);

        WriteExtendedData(feature.Attributes, writer);

        var wroteGeometry = !string.IsNullOrWhiteSpace(feature.GeometryKml);
        if (wroteGeometry)
        {
            writer.WriteRaw(feature.GeometryKml!);
        }

        writer.WriteEndElement();
        return wroteGeometry;
    }

    private static void WriteExtendedData(
        ImmutableDictionary<string, object?> attributes,
        XmlWriter writer)
    {
        if (attributes.Count == 0)
        {
            return;
        }

        writer.WriteStartElement("ExtendedData");
        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (FeatureAttributeVisibility.IsInternalAttribute(key))
            {
                continue;
            }

            writer.WriteStartElement("Data");
            writer.WriteAttributeString("name", key);
            writer.WriteElementString("value", ConvertToAttributeString(value));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string ConvertToAttributeString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string? GetDisplayFieldValue(KmlFeature feature, string displayField)
    {
        if (feature.Attributes.TryGetValue(displayField, out var value) && value is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        return feature.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryWriteKmlGeometry(XmlWriter writer, NtsGeometry geometry)
    {
        switch (geometry)
        {
            case Point point:
                WriteKmlPoint(writer, point);
                return true;
            case LineString lineString:
                WriteKmlLineString(writer, lineString);
                return true;
            case Polygon polygon:
                WriteKmlPolygon(writer, polygon);
                return true;
            case MultiPoint multiPoint:
                WriteKmlMultiPoint(writer, multiPoint);
                return true;
            case MultiLineString multiLineString:
                WriteKmlMultiLineString(writer, multiLineString);
                return true;
            case MultiPolygon multiPolygon:
                WriteKmlMultiPolygon(writer, multiPolygon);
                return true;
            case GeometryCollection geometryCollection:
                WriteKmlGeometryCollection(writer, geometryCollection);
                return true;
            default:
                return false;
        }
    }

    private static void WriteKmlPoint(XmlWriter writer, Point point)
    {
        writer.WriteStartElement("Point");
        WriteCoordinatesElement(writer, point.CoordinateSequence);
        writer.WriteEndElement();
    }

    private static void WriteKmlLineString(XmlWriter writer, LineString lineString)
    {
        writer.WriteStartElement("LineString");
        WriteCoordinatesElement(writer, lineString.CoordinateSequence);
        writer.WriteEndElement();
    }

    private static void WriteKmlPolygon(XmlWriter writer, Polygon polygon)
    {
        writer.WriteStartElement("Polygon");

        if (!polygon.ExteriorRing.IsEmpty)
        {
            writer.WriteStartElement("outerBoundaryIs");
            WriteKmlLinearRing(writer, polygon.ExteriorRing);
            writer.WriteEndElement();
        }

        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            var hole = polygon.GetInteriorRingN(i);
            if (hole.IsEmpty)
            {
                continue;
            }

            writer.WriteStartElement("innerBoundaryIs");
            WriteKmlLinearRing(writer, hole);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteKmlLinearRing(XmlWriter writer, LineString ring)
    {
        writer.WriteStartElement("LinearRing");
        WriteCoordinatesElement(writer, ring.CoordinateSequence);
        writer.WriteEndElement();
    }

    private static void WriteKmlMultiPoint(XmlWriter writer, MultiPoint multiPoint)
    {
        writer.WriteStartElement("MultiGeometry");
        foreach (var point in multiPoint.Geometries.OfType<Point>())
        {
            if (!point.IsEmpty)
            {
                WriteKmlPoint(writer, point);
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteKmlMultiLineString(XmlWriter writer, MultiLineString multiLineString)
    {
        writer.WriteStartElement("MultiGeometry");
        foreach (var lineString in multiLineString.Geometries.OfType<LineString>())
        {
            if (!lineString.IsEmpty)
            {
                WriteKmlLineString(writer, lineString);
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteKmlMultiPolygon(XmlWriter writer, MultiPolygon multiPolygon)
    {
        writer.WriteStartElement("MultiGeometry");
        foreach (var polygon in multiPolygon.Geometries.OfType<Polygon>())
        {
            if (!polygon.IsEmpty)
            {
                WriteKmlPolygon(writer, polygon);
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteKmlGeometryCollection(XmlWriter writer, GeometryCollection geometryCollection)
    {
        writer.WriteStartElement("MultiGeometry");
        foreach (var childGeometry in geometryCollection.Geometries)
        {
            if (childGeometry is { IsEmpty: false })
            {
                _ = TryWriteKmlGeometry(writer, childGeometry);
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteCoordinatesElement(XmlWriter writer, CoordinateSequence coordinateSequence)
    {
        if (coordinateSequence.Count == 0)
        {
            writer.WriteElementString("coordinates", string.Empty);
            return;
        }

        var coordinates = new StringBuilder();
        for (var i = 0; i < coordinateSequence.Count; i++)
        {
            if (i > 0)
            {
                coordinates.Append(' ');
            }

            coordinates.Append(coordinateSequence.GetX(i).ToString("G17", CultureInfo.InvariantCulture));
            coordinates.Append(',');
            coordinates.Append(coordinateSequence.GetY(i).ToString("G17", CultureInfo.InvariantCulture));

            if (coordinateSequence.HasZ)
            {
                var z = coordinateSequence.GetZ(i);
                if (!double.IsNaN(z))
                {
                    coordinates.Append(',');
                    coordinates.Append(z.ToString("G17", CultureInfo.InvariantCulture));
                }
            }
        }

        writer.WriteElementString("coordinates", coordinates.ToString());
    }

    private static byte[] CreateKmzArchive(byte[] kmlBytes)
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("doc.kml", CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            entryStream.Write(kmlBytes, 0, kmlBytes.Length);
        }

        return archiveStream.ToArray();
    }
}
