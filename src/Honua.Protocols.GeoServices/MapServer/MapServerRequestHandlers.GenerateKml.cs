// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
using static Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const string InvalidGenerateKmlRequestMessage = "Invalid generateKml request parameters.";
    private const string KmlContentType = "application/vnd.google-earth.kml+xml";
    private const string KmzContentType = "application/vnd.google-earth.kmz";

    private sealed record GenerateKmlLayerDescriptor(
        int Id,
        int PublicLayerId,
        int StorageLayerId,
        string Name,
        MetadataV2Resource Resource,
        string? DefinitionExpression);

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
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

        try
        {
            var earlyServiceError = await TryValidateMapServerServiceAsync(serviceId, context);
            if (earlyServiceError is not null)
            {
                return earlyServiceError;
            }

            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

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
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveGenerateKmlPublishedLayers(snapshot, service);

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                publishedLayers.Select(static layer => layer.Resource),
                service);
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

            var knownLayerIds = publishedLayers
                .Select(static layer => layer.PublicLayerId)
                .ToHashSet();
            if (!TryParseDynamicLayers(GetValue(values, "dynamicLayers"), knownLayerIds, queryValidator, out var dynamicLayers, out var dynamicLayersError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, dynamicLayersError ?? "Invalid dynamicLayers parameter.");
            }

            var documentName = GetValue(values, "docName");
            if (string.IsNullOrWhiteSpace(documentName))
            {
                documentName = service.Metadata.Name;
            }

            if (string.IsNullOrWhiteSpace(documentName))
            {
                documentName = "Map";
            }

            var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
            var maxFeaturesPerLayer = Math.Clamp(
                service.Settings?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer,
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

            var (resolvedLayers, renderLayerError) = ResolveGenerateKmlLayers(
                publishedLayers,
                layersValue,
                dynamicLayers,
                context,
                service);
            if (renderLayerError != null)
            {
                return renderLayerError;
            }

            var renderLayers = resolvedLayers
                .Where(static renderLayer => HasGenerateKmlGeometry(renderLayer.Resource))
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
                    cancellationToken.ThrowIfCancellationRequested();

                    var resource = renderLayer.Resource;
                    layerDefs.TryGetValue(renderLayer.PublicLayerId, out var layerDef);
                    var combinedDefinition = CombineDefinitionExpressions(renderLayer.DefinitionExpression, layerDef);

                    if (!TryGetEffectiveTimeParameters(
                            timeValue,
                            timeRelationValue,
                            renderLayer,
                            layerTimeOptions,
                            out var effectiveTime,
                            out var effectiveTimeRelation,
                            out var timeError))
                    {
                        return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time parameter.");
                    }

                    if (!TryBuildLayerSqlFilter(
                            filterExpressionService,
                            resource,
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
                        SpatialReferenceSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid,
                        OutputSrid = 4326,
                        Limit = maxFeaturesPerLayer,
                        SqlFilter = sqlFilter
                    };

                    var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
                    var displayField = ResolveDisplayField(resource, objectIdField);

                    writer.WriteStartElement("Folder");
                    writer.WriteElementString("name", renderLayer.Name);

                    if (kmlFeatureStore is not null)
                    {
                        var queryResult = await kmlFeatureStore.QueryKmlAsync(renderLayer.StorageLayerId, featureQuery, cancellationToken);
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
                        var queryResult = await featureReader.QueryAsync(renderLayer.StorageLayerId, featureQuery, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.GenerateKmlFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer generateKml failed.");
        }
    }

    private static GenerateKmlLayerDescriptor[] ResolveGenerateKmlPublishedLayers(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var descriptors = new List<GenerateKmlLayerDescriptor>();
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (publication.LayerIndex is not int publicLayerId)
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? publicLayerId;
            var layerName = string.IsNullOrWhiteSpace(resource.Metadata.Name)
                ? publication.Metadata.Name
                : resource.Metadata.Name;

            descriptors.Add(new GenerateKmlLayerDescriptor(
                publicLayerId,
                publicLayerId,
                storageLayerId,
                string.IsNullOrWhiteSpace(layerName)
                    ? publicLayerId.ToString(CultureInfo.InvariantCulture)
                    : layerName,
                resource,
                null));
        }

        return [.. descriptors.OrderBy(static layer => layer.PublicLayerId)];
    }

    private static (GenerateKmlLayerDescriptor[] Layers, IResult? Error) ResolveGenerateKmlLayers(
        IReadOnlyList<GenerateKmlLayerDescriptor> publishedLayers,
        string? layersParam,
        IReadOnlyList<DynamicLayerDefinition> dynamicLayers,
        HttpContext context,
        MetadataV2Service service)
    {
        var layerLookup = publishedLayers.ToDictionary(static layer => layer.PublicLayerId);
        if (dynamicLayers.Count == 0)
        {
            var accessibleLayers = publishedLayers
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .ToArray();

            if (string.IsNullOrWhiteSpace(layersParam))
            {
                return (accessibleLayers
                    .Where(static layer => layer.Resource.Display?.DefaultVisibility ?? true)
                    .ToArray(), null);
            }

            var spec = layersParam.Trim();
            var requestedStaticLayerIds = ParseLayerIds(spec);
            if (requestedStaticLayerIds.Count == 0)
            {
                return (Array.Empty<GenerateKmlLayerDescriptor>(), StandardErrorHelpers.CreateBadRequest(context, "Invalid layers parameter."));
            }

            var accessibleLayerIds = accessibleLayers.Select(static layer => layer.PublicLayerId).ToHashSet();
            if (requestedStaticLayerIds.Any(id => !accessibleLayerIds.Contains(id)))
            {
                return (Array.Empty<GenerateKmlLayerDescriptor>(), StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter references an invalid or inaccessible layer."));
            }

            return (accessibleLayers
                .Where(layer => requestedStaticLayerIds.Contains(layer.PublicLayerId))
                .ToArray(), null);
        }

        IEnumerable<DynamicLayerDefinition> selected = dynamicLayers;
        HashSet<int>? requestedIds = null;

        if (!string.IsNullOrWhiteSpace(layersParam))
        {
            var spec = layersParam.Trim();
            if (spec.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["show:".Length..]);
                requestedIds = ids;
                selected = dynamicLayers.Where(layer => ids.Contains(layer.Id));
            }
            else if (spec.StartsWith("hide:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["hide:".Length..]);
                requestedIds = ids;
                selected = dynamicLayers.Where(layer => !ids.Contains(layer.Id));
            }
            else if (spec.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["include:".Length..]);
                requestedIds = ids;
                selected = dynamicLayers.Where(layer =>
                {
                    if (!layerLookup.TryGetValue(layer.MapLayerId, out var mapLayer))
                    {
                        return false;
                    }

                    return (mapLayer.Resource.Display?.DefaultVisibility ?? true) || ids.Contains(layer.Id);
                });
            }
            else if (spec.StartsWith("exclude:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["exclude:".Length..]);
                requestedIds = ids;
                selected = dynamicLayers.Where(layer =>
                {
                    if (!layerLookup.TryGetValue(layer.MapLayerId, out var mapLayer))
                    {
                        return false;
                    }

                    return (mapLayer.Resource.Display?.DefaultVisibility ?? true) && !ids.Contains(layer.Id);
                });
            }
            else
            {
                var ids = ParseLayerIds(spec);
                requestedIds = ids;
                selected = dynamicLayers.Where(layer => ids.Contains(layer.Id));
            }

            if (requestedIds is { Count: > 0 })
            {
                var dynamicLayerIds = dynamicLayers.Select(static layer => layer.Id).ToHashSet();
                if (requestedIds.Any(id => !dynamicLayerIds.Contains(id)))
                {
                    return (Array.Empty<GenerateKmlLayerDescriptor>(), StandardErrorHelpers.CreateBadRequest(
                        context,
                        "layers parameter references an invalid or inaccessible layer."));
                }

                foreach (var requestedId in requestedIds)
                {
                    var dynamicLayer = dynamicLayers.FirstOrDefault(layer => layer.Id == requestedId);
                    if (dynamicLayer is null ||
                        !layerLookup.TryGetValue(dynamicLayer.MapLayerId, out var requestedMapLayer) ||
                        !AccessPolicyHelpers.IsResourceAccessible(context, requestedMapLayer.Resource, service))
                    {
                        return (Array.Empty<GenerateKmlLayerDescriptor>(), StandardErrorHelpers.CreateBadRequest(
                            context,
                            "layers parameter references an invalid or inaccessible layer."));
                    }
                }
            }
        }

        var renderLayers = new List<GenerateKmlLayerDescriptor>();
        foreach (var dynamicLayer in selected)
        {
            if (!layerLookup.TryGetValue(dynamicLayer.MapLayerId, out var layer))
            {
                return (Array.Empty<GenerateKmlLayerDescriptor>(), StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter references an invalid or inaccessible layer."));
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
            {
                return (Array.Empty<GenerateKmlLayerDescriptor>(), StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter references an invalid or inaccessible layer."));
            }

            renderLayers.Add(layer with
            {
                Id = dynamicLayer.Id,
                DefinitionExpression = dynamicLayer.DefinitionExpression
            });
        }

        return (renderLayers.ToArray(), null);
    }

    private static bool HasGenerateKmlGeometry(MetadataV2Resource resource)
    {
        var geometryType = resource.ReadGeometryType();
        return geometryType != MetadataV2GeometryType.None ||
               resource.FindPrimaryGeometryField() is not null;
    }

    private static string ResolveDisplayField(MetadataV2Resource resource, string objectIdField)
    {
        var configuredDisplayField = resource.Display?.DisplayField;
        if (!string.IsNullOrWhiteSpace(configuredDisplayField))
        {
            var field = resource.SchemaFields.FirstOrDefault(field =>
                field.Name.Equals(configuredDisplayField, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field.Name;
            }
        }

        var stringField = resource.SchemaFields
            .FirstOrDefault(static field => field.Type == MetadataV2FieldType.String);
        return stringField?.Name ?? objectIdField;
    }

    private static bool TryGetEffectiveTimeParameters(
        string? globalTime,
        string? globalTimeRelation,
        GenerateKmlLayerDescriptor renderLayer,
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        out string? effectiveTime,
        out string? effectiveTimeRelation,
        out string? error)
    {
        effectiveTime = globalTime;
        effectiveTimeRelation = globalTimeRelation;
        error = null;

        LayerTimeOptions? options = null;
        if (TryResolveLayerTimeOptions(layerTimeOptions, renderLayer, out var resolvedOptions))
        {
            options = resolvedOptions;
            if (options.UseTime is false)
            {
                effectiveTime = null;
                effectiveTimeRelation = null;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(options.Time))
            {
                effectiveTime = options.Time;
            }

            if (!string.IsNullOrWhiteSpace(options.TimeRelation))
            {
                effectiveTimeRelation = options.TimeRelation;
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveTime))
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (!GeoServicesTemporalQueryBuilder.TryParseTimeParameter(effectiveTime, out var start, out var end))
        {
            error = InvalidTimeParameterMessage;
            return false;
        }

        if (start is null && end is null && options?.TimeDataCumulative != true)
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (options?.TimeDataCumulative == true)
        {
            start = null;
        }

        if (options?.TimeOffset.HasValue == true)
        {
            if (!TryApplyTimeOffset(
                    start,
                    end,
                    options.TimeOffset.Value,
                    options.TimeOffsetUnits,
                    out var adjustedStart,
                    out var adjustedEnd,
                    out var offsetError))
            {
                error = offsetError ?? "Invalid time offset.";
                return false;
            }

            start = adjustedStart;
            end = adjustedEnd;
        }

        effectiveTime = BuildTimeParameter(start, end);
        effectiveTimeRelation = NormalizeTimeRelation(effectiveTimeRelation);
        return true;
    }

    private static bool TryResolveLayerTimeOptions(
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        GenerateKmlLayerDescriptor renderLayer,
        out LayerTimeOptions resolvedOptions)
    {
        if (layerTimeOptions.TryGetValue(renderLayer.Id, out var directOptions) &&
            directOptions is not null)
        {
            resolvedOptions = directOptions;
            return true;
        }

        if (renderLayer.Id != renderLayer.PublicLayerId &&
            layerTimeOptions.TryGetValue(renderLayer.PublicLayerId, out var sourceLayerOptions) &&
            sourceLayerOptions is not null)
        {
            resolvedOptions = sourceLayerOptions;
            return true;
        }

        resolvedOptions = default!;
        return false;
    }

    private static bool TryBuildLayerSqlFilter(
        IFilterExpressionService filterExpressionService,
        MetadataV2Resource resource,
        string? where,
        string? time,
        string? timeRelation,
        out SqlFragment? sqlFilter,
        out string? error)
    {
        sqlFilter = null;
        error = null;

        FilterExpression? filterExpression = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var parseResult = filterExpressionService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                error = parseResult.ErrorMessage ?? "Invalid filter syntax.";
                return false;
            }

            filterExpression = parseResult.Expression;
            if (filterExpression != null && !FilterExpressionHelpers.IsBooleanFilterExpression(filterExpression))
            {
                error = "Invalid where clause.";
                return false;
            }
        }

        FilterExpression? temporalExpression = null;
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (!TryResolveTemporalFieldSelection(resource, out var applyTemporalFilter, out var temporalError))
            {
                error = temporalError ?? "Invalid temporal field configuration.";
                return false;
            }

            if (applyTemporalFilter)
            {
                try
                {
                    temporalExpression = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(time, timeRelation, resource);
                }
                catch (ArgumentException)
                {
                    error = InvalidTimeParameterMessage;
                    return false;
                }
            }
        }

        if (filterExpression != null && temporalExpression != null)
        {
            filterExpression = new BinaryExpression(filterExpression, BinaryOperator.And, temporalExpression);
        }
        else
        {
            filterExpression ??= temporalExpression;
        }

        if (filterExpression != null)
        {
            var translationResult = filterExpressionService.Translate(filterExpression, resource);
            if (!translationResult.IsSuccess)
            {
                error = translationResult.ErrorMessage ?? "Invalid filter syntax.";
                return false;
            }

            sqlFilter = translationResult.SqlFilter;
        }

        return true;
    }

    private static bool TryResolveTemporalFieldSelection(
        MetadataV2Resource resource,
        out bool applyTemporalFilter,
        out string? error)
    {
        applyTemporalFilter = false;
        error = null;

        var timeInfo = resource.ReadTemporalFields();
        if (string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
        {
            return true;
        }

        if (FindTemporalField(resource, timeInfo.StartTimeField) is null)
        {
            error = $"Temporal field '{timeInfo.StartTimeField}' is not defined on resource '{resource.Metadata.Name}'.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(timeInfo.EndTimeField) &&
            FindTemporalField(resource, timeInfo.EndTimeField) is null)
        {
            error = $"Temporal field '{timeInfo.EndTimeField}' is not defined on resource '{resource.Metadata.Name}'.";
            return false;
        }

        applyTemporalFilter = true;
        return true;
    }

    private static MetadataV2Field? FindTemporalField(MetadataV2Resource resource, string fieldName)
    {
        return resource.SchemaFields.FirstOrDefault(field =>
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
            field.Type is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time);
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
