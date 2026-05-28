// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// WFS 2.0 GetPropertyValue operation handling. Split from <see cref="Wfs20Handler"/> as part of audit #1144.
/// </summary>
internal sealed partial class Wfs20Handler
{
    public async Task<IResult> HandleGetPropertyValueAsync(
        HttpContext context,
        string? typeNames,
        string? valueReference,
        bool valueReferenceSpecified,
        string? outputFormat,
        string? count,
        string? startIndex,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_property_value", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetPropertyValue);
        activity?.SetTag("wfs.type_names", typeNames ?? "ALL");
        activity?.SetTag("wfs.value_reference", valueReference ?? "unknown");

        if (!valueReferenceSpecified)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Missing required 'valueReference' parameter for GetPropertyValue.",
                "valueReference");
        }

        if (string.IsNullOrWhiteSpace(valueReference))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                "The 'valueReference' parameter must not be empty for GetPropertyValue.",
                "valueReference");
        }

        var normalizedOutputFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        if (!IsSupportedValueOutputFormat(normalizedOutputFormat))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Unsupported output format '{outputFormat}'. GetPropertyValue supports application/gml+xml, application/json, and application/geo+json.",
                "outputFormat");
        }

        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        if (!Wfs20Utilities.TryParseCount(count, out var maxFeatures))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid COUNT parameter '{count}'. COUNT must be a non-negative integer.",
                "count");
        }

        if (!Wfs20Utilities.TryParseStartIndex(startIndex, out var offset))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid STARTINDEX parameter '{startIndex}'. STARTINDEX must be a non-negative integer.",
                "startIndex");
        }

        Wfs20Log.GetPropertyValueRequested(_logger, valueReference, typeNames ?? "ALL");

        try
        {
            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var unknownTypes = GetUnknownRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (unknownTypes.Length > 0)
            {
                var requestedTypeMessage = unknownTypes.Length == 1
                    ? $"Unknown feature type '{unknownTypes[0]}'."
                    : $"Unknown feature types: {string.Join(", ", unknownTypes.Select(type => $"'{type}'"))}.";
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    requestedTypeMessage,
                    "typeNames");
            }

            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (selectedTypes.Length == 0)
            {
                return CreateEmptyValueCollectionResult(normalizedOutputFormat);
            }

            var planSet = await BuildLayerValuePlansAsync(
                selectedTypes,
                valueReference,
                bbox,
                filter,
                resourceId,
                srsName,
                offset,
                maxFeatures,
                cancellationToken);

            if (planSet.TotalMatched == 0)
            {
                return CreateEmptyValueCollectionResult(normalizedOutputFormat);
            }

            var (result, returnedCount) = string.Equals(normalizedOutputFormat, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase)
                ? await BuildValueCollectionResultAsync(planSet, cancellationToken)
                : await BuildValueJsonResultAsync(planSet, normalizedOutputFormat, cancellationToken);
            Wfs20Log.GetPropertyValueReturned(_logger, returnedCount);
            return result;
        }
        catch (WfsQueryException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                ex.ExceptionCode,
                ex.Message,
                ex.Locator);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Fes20ParseException)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                "Invalid WFS parameter value; see logs for details.");
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.GetPropertyValue, ex.Message);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process GetPropertyValue request.");
        }
    }

    private async Task<LayerValuePlanSet> BuildLayerValuePlansAsync(
        IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes,
        string valueReference,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var plans = ImmutableArray.CreateBuilder<LayerValuePlan>(featureTypes.Count);
        var remainingOffset = offset;
        var remainingCount = count;
        long totalMatched = 0;

        foreach (var featureType in featureTypes)
        {
            var resolvedValueReference = ResolveValueReference(featureType.Resource, valueReference);
            var query = await BuildValueQueryAsync(
                featureType,
                resolvedValueReference,
                bbox,
                filter,
                resourceId,
                srsName,
                enforceResourceIdTypeMatch: true,
                requireResourceIdQualifier: featureTypes.Count > 1,
                cancellationToken: cancellationToken);

            var layerMatched = await _featureReader.CountAsync(featureType.StorageLayerId, query, cancellationToken);
            totalMatched += layerMatched;

            if (layerMatched == 0)
            {
                continue;
            }

            if (remainingOffset >= layerMatched)
            {
                remainingOffset -= (int)Math.Min(remainingOffset, layerMatched);
                continue;
            }

            if (remainingCount <= 0)
            {
                continue;
            }

            var layerOffset = remainingOffset;
            remainingOffset = 0;
            var availableCount = layerMatched - layerOffset;
            var layerLimit = (int)Math.Min(remainingCount, Math.Min(availableCount, int.MaxValue));

            if (layerLimit <= 0)
            {
                continue;
            }

            plans.Add(new LayerValuePlan(
                featureType,
                query with { Offset = layerOffset, Limit = layerLimit },
                layerMatched,
                resolvedValueReference));

            remainingCount -= layerLimit;
        }

        return new LayerValuePlanSet(plans.ToImmutable(), totalMatched);
    }

    private async ValueTask<FeatureQuery> BuildValueQueryAsync(
        WfsFeatureTypeDescriptor descriptor,
        ValueReferenceResolution valueReference,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        bool enforceResourceIdTypeMatch,
        bool requireResourceIdQualifier,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string>? outFields = valueReference.IsGeometry || valueReference.IsFeatureId
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(valueReference.CanonicalName);

        var (normalizedFilter, normalizedResourceId) = NormalizeFilterInputs(filter, resourceId);
        var sqlFilter = TranslateFesFilter(descriptor.Resource, normalizedFilter);
        var spatialFilter = ParseBboxFilterForResource(bbox, descriptor.Resource);
        var resourceIds = ParseResourceIds(normalizedResourceId, descriptor, enforceResourceIdTypeMatch, requireResourceIdQualifier);
        sqlFilter = resourceIds.MatchesNothing
            ? CombineSqlFilters(sqlFilter, FalseSqlFilter)
            : sqlFilter;
        var outputSrid = await ResolveRequestedOutputSridAsync(descriptor.Resource, srsName, cancellationToken).ConfigureAwait(false);
        var outputAxisOrder = await ResolveOutputAxisOrderAsync(srsName, outputSrid, cancellationToken).ConfigureAwait(false);
        var queryAdapterResult = await _queryParameterAdapter.ConvertAsync(
            new Wfs20QueryRequest
            {
                SqlFilter = sqlFilter,
                ObjectIds = resourceIds.ObjectIds,
                OutFields = outFields,
                SpatialFilter = spatialFilter,
                OutputCrs = QueryCrs.Create(outputSrid, outputAxisOrder)
            },
            cancellationToken).ConfigureAwait(false);
        if (!queryAdapterResult.IsSuccess || queryAdapterResult.Query == null)
        {
            throw new ArgumentException(queryAdapterResult.ErrorMessage ?? "Invalid WFS query parameters.");
        }

        var unifiedQuery = _queryProcessor.OptimizeQuery(queryAdapterResult.Query.Value, descriptor.Resource);
        var validation = _queryProcessor.ValidateQuery(unifiedQuery, descriptor.Resource);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage ?? "Invalid WFS query parameters.");
        }

        return _queryProcessor.ToFeatureQuery(unifiedQuery, descriptor.Resource);
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildValueCollectionResultAsync(
        LayerValuePlanSet planSet,
        CancellationToken cancellationToken)
    {
        var queryResults = new List<(LayerValuePlan Plan, ImmutableArray<GmlFeature>? GmlFeatures, ImmutableArray<Feature>? Features)>(planSet.Plans.Length);
        var returnedCount = 0;

        foreach (var plan in planSet.Plans)
        {
            if (plan.ValueReference.IsGeometry)
            {
                var result = await _gmlFeatureStore.QueryGmlAsync(plan.Descriptor.StorageLayerId, plan.Query, cancellationToken);
                queryResults.Add((plan, result.Items, null));
                returnedCount += result.Items.Length;
                continue;
            }

            var features = await _featureReader.QueryAsync(plan.Descriptor.StorageLayerId, plan.Query, cancellationToken);
            queryResults.Add((plan, null, features.Items));
            returnedCount += features.Items.Length;
        }

        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "ValueCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", planSet.TotalMatched.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberReturned", returnedCount.ToString(CultureInfo.InvariantCulture));

            foreach (var queryResult in queryResults)
            {
                if (queryResult.Plan.ValueReference.IsGeometry)
                {
                    foreach (var feature in queryResult.GmlFeatures ?? ImmutableArray<GmlFeature>.Empty)
                    {
                        writer.WriteStartElement("wfs", "member", Wfs20Utilities.WfsNamespace);
                        if (!string.IsNullOrWhiteSpace(feature.GeometryGml))
                        {
                            writer.WriteRaw(feature.GeometryGml);
                        }
                        writer.WriteEndElement();
                    }

                    continue;
                }

                foreach (var feature in queryResult.Features ?? ImmutableArray<Feature>.Empty)
                {
                    writer.WriteStartElement("wfs", "member", Wfs20Utilities.WfsNamespace);
                    var value = ExtractValue(feature, queryResult.Plan.ValueReference);
                    if (value is not null)
                    {
                        writer.WriteString(ConvertValueReferenceToInvariantString(value, queryResult.Plan.ValueReference));
                    }
                    writer.WriteEndElement();
                }
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return (Results.Content(xml, MediaTypes.Gml, Encoding.UTF8), returnedCount);
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildValueJsonResultAsync(
        LayerValuePlanSet planSet,
        string normalizedFormat,
        CancellationToken cancellationToken)
    {
        var features = new List<GeoJsonFeature>();

        foreach (var plan in planSet.Plans)
        {
            var axisOrder = plan.Query.OutputAxisOrder ?? AxisOrder.EastNorth;
            var featureResult = await _featureReader.QueryAsync(plan.Descriptor.StorageLayerId, plan.Query, cancellationToken);
            foreach (var feature in featureResult.Items)
            {
                var featureId = BuildFeatureId(plan.Descriptor, feature.Id);
                var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                SimpleGeoJsonGeometry? geometry = null;

                if (plan.ValueReference.IsGeometry)
                {
                    geometry = feature.Geometry is null
                        ? null
                        : _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
                }
                else if (plan.ValueReference.IsFeatureId)
                {
                    properties["id"] = featureId;
                }
                else
                {
                    properties[plan.ValueReference.CanonicalName] = ExtractValue(feature, plan.ValueReference);
                }

                features.Add(OgcGeoJsonFeatureBuilder.Create(featureId, properties, geometry));
            }
        }

        var payload = OgcGeoJsonFeatureBuilder.CreateCollection(features, planSet.TotalMatched);

        var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.Json
            : MediaTypes.GeoJson;

        return (Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType), features.Count);
    }

    private static ValueReferenceResolution ResolveValueReference(MetadataV2Resource resource, string valueReference)
    {
        var resolvedName = FilterExpressionHelpers.ResolveFieldName(resource, valueReference, allowGeometryAlias: true)
            ?? throw new ArgumentException($"Unknown valueReference '{valueReference}' for feature type '{resource.Metadata.Name}'.");

        var geometryField = resource.FindPrimaryGeometryField();
        var primaryKeyField = resource.FindPrimaryIdField();
        var isGeometry = geometryField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true;
        var isFeatureId = primaryKeyField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true ||
                          resolvedName.Equals("objectid", StringComparison.OrdinalIgnoreCase);
        var field = resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography) &&
            candidate.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase));

        return new ValueReferenceResolution(valueReference, resolvedName, isGeometry, isFeatureId, field);
    }

    private static object? ExtractValue(Feature feature, ValueReferenceResolution valueReference)
    {
        if (valueReference.IsFeatureId)
        {
            return feature.Id;
        }

        return feature.Attributes.TryGetValue(valueReference.CanonicalName, out var value)
            ? value
            : null;
    }

    private static string ConvertValueReferenceToInvariantString(object? value, ValueReferenceResolution valueReference)
        => valueReference.Field is { } field
            ? ConvertFieldValueToInvariantString(value, field)
            : ConvertToInvariantString(value);

    private static bool IsSupportedValueOutputFormat(string format)
    {
        return string.Equals(format, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Gml32Simplified, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult CreateEmptyValueCollectionResult(string normalizedFormat)
    {
        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase))
        {
            var payload = OgcGeoJsonFeatureBuilder.CreateCollection(Array.Empty<GeoJsonFeature>(), 0);

            var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
                ? MediaTypes.Json
                : MediaTypes.GeoJson;

            return Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType);
        }

        return CreateEmptyValueCollectionXmlResult();
    }

    private static IResult CreateEmptyValueCollectionXmlResult()
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:ValueCollection xmlns:wfs="{{Wfs20Utilities.WfsNamespace}}" xmlns:gml="{{Wfs20Utilities.GmlNamespace}}" timeStamp="{{DateTimeOffset.UtcNow:O}}" numberMatched="0" numberReturned="0" />
            """;

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private sealed record LayerValuePlan(
        WfsFeatureTypeDescriptor Descriptor,
        FeatureQuery Query,
        long MatchedCount,
        ValueReferenceResolution ValueReference);

    private readonly record struct LayerValuePlanSet(
        ImmutableArray<LayerValuePlan> Plans,
        long TotalMatched);

    private readonly record struct ValueReferenceResolution(
        string RequestedName,
        string CanonicalName,
        bool IsGeometry,
        bool IsFeatureId,
        MetadataV2Field? Field);
}
