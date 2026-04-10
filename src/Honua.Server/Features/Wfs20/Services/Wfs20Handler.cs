// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Honua.Server.Features.Wfs20.Models;
using Honua.ServiceDefaults;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Core handler for WFS 2.0 operations backed by the shared catalog and feature stores.
/// </summary>
internal sealed class Wfs20Handler
{
    private const string FeatureNamespacePrefix = "honua";
    private const string FeatureNamespaceUri = "http://honua.io/wfs";
    private const string GetFeatureByIdStoredQueryId = "urn:ogc:def:query:OGC-WFS::GetFeatureById";
    private const string GetFeatureByIdStoredQueryUri = "http://www.opengis.net/def/query/OGC-WFS/0/GetFeatureById";
    private static readonly WKBWriter BboxWkbWriter = new();
    private static readonly WKBWriter GeometryWkbWriter = new();
    private static readonly SqlFragment FalseSqlFilter = new("FALSE", Array.Empty<object?>());

    private readonly ILogger<Wfs20Handler> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IGmlFeatureStore _gmlFeatureStore;
    private readonly IFilterExpressionService _filterExpressionService;
    private readonly OgcFeaturesGeometryServices _geometryServices;
    private readonly Wfs20Options _wfs20Options;
    private readonly ICrsRegistry _crsRegistry;
    private readonly FeatureMutationValidator _mutationValidator;
    private readonly FeatureMutationEventService _mutationEventService;
    private readonly EditLimits _editLimits;

    public Wfs20Handler(
        ILogger<Wfs20Handler> logger,
        Wfs20QueryServices queryServices)
    {
        _logger = logger;
        _layerCatalog = queryServices.LayerCatalog;
        _featureReader = queryServices.FeatureReader;
        _featureWriter = queryServices.FeatureWriter;
        _gmlFeatureStore = queryServices.GmlFeatureStore;
        _filterExpressionService = queryServices.FilterExpressionService;
        _geometryServices = queryServices.GeometryServices;
        _crsRegistry = queryServices.CrsRegistry;
        _wfs20Options = queryServices.Wfs20Options;
        _mutationValidator = queryServices.MutationValidator;
        _mutationEventService = queryServices.MutationEventService;
        _editLimits = queryServices.EditLimits;
    }

    public async Task<WfsCapabilities> HandleGetCapabilitiesAsync(
        HttpContext context,
        string? acceptVersions,
        IReadOnlySet<string>? requestedSections,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_capabilities", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetCapabilities);

        Wfs20Log.GetCapabilitiesRequested(_logger);

        try
        {
            var featureTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var wfsUrl = $"{baseUrl}/wfs";

            var capabilities = new WfsCapabilities
            {
                UpdateSequence = Wfs20Utilities.CurrentUpdateSequence,
                ServiceIdentification = ShouldIncludeCapabilitiesSection(requestedSections, "ServiceIdentification")
                    ? new ServiceIdentification()
                    : null,
                ServiceProvider = ShouldIncludeCapabilitiesSection(requestedSections, "ServiceProvider")
                    ? new Models.ServiceProvider()
                    : null,
                OperationsMetadata = ShouldIncludeCapabilitiesSection(requestedSections, "OperationsMetadata")
                    ? BuildOperationsMetadata(wfsUrl)
                    : null,
                FeatureTypeList = ShouldIncludeCapabilitiesSection(requestedSections, "FeatureTypeList")
                    ? new FeatureTypeList
                    {
                        FeatureTypes = featureTypes.Select(BuildFeatureType).ToArray()
                    }
                    : null,
                FilterCapabilities = ShouldIncludeCapabilitiesSection(requestedSections, "Filter_Capabilities")
                    ? BuildFilterCapabilities()
                    : null
            };

            Wfs20Log.GetCapabilitiesReturned(_logger);
            return capabilities;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }

    public async Task<string> HandleDescribeFeatureTypeAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.describe_feature_type", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.DescribeFeatureType);
        activity?.SetTag("wfs.type_names", typeNames ?? "ALL");

        Wfs20Log.DescribeFeatureTypeRequested(_logger, typeNames ?? "ALL");

        try
        {
            var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (selectedTypes.Length == 0 && requestedTypes.Length > 0)
            {
                throw new ArgumentException($"Requested feature type(s) not found: {string.Join(", ", requestedTypes)}.");
            }

            var schema = GenerateSchemaForTypes(selectedTypes);

            Wfs20Log.DescribeFeatureTypeReturned(_logger, selectedTypes.Length == 0 ? requestedTypes.Length : selectedTypes.Length);
            return schema;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }

    public async Task<IResult> HandleListStoredQueriesAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var xml = BuildListStoredQueriesXml(descriptors);
        return Results.Content(xml, "application/xml", Encoding.UTF8);
    }

    public async Task<IResult> HandleDescribeStoredQueriesAsync(
        HttpContext context,
        string? storedQueryIds,
        CancellationToken cancellationToken = default)
    {
        var requestedIds = ParseQualifiedList(storedQueryIds);
        foreach (var requestedId in requestedIds)
        {
            if (!IsGetFeatureByIdStoredQuery(requestedId))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Stored query '{requestedId}' is not supported.",
                    "storedquery_id");
            }
        }

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var xml = BuildDescribeStoredQueriesXml(descriptors);
        return Results.Content(xml, "application/xml", Encoding.UTF8);
    }

    public async Task<IResult> HandleStoredQueryGetFeatureAsync(
        HttpContext context,
        string storedQueryId,
        string? featureId,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken = default)
    {
        if (!IsGetFeatureByIdStoredQuery(storedQueryId))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "OperationParsingFailed",
                $"Stored query '{storedQueryId}' is not supported.",
                "storedquery_id");
        }

        if (string.IsNullOrWhiteSpace(featureId))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Stored query 'GetFeatureById' requires an 'id' parameter.",
                "id");
        }

        var normalizedFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        if (!string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Stored query 'GetFeatureById' supports only '{Wfs20Utilities.OutputFormats.Gml32}'.",
                "outputFormat");
        }

        if (!Wfs20Utilities.TryParseCount(count, out _))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid COUNT parameter '{count}'. COUNT must be a non-negative integer.",
                "count");
        }

        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var descriptor = ResolveStoredQueryFeatureTypeDescriptor(descriptors, featureId);
        if (descriptor is null)
        {
            return CreateStoredQueryFeatureNotFoundResult(context, featureId);
        }

        var query = await BuildFeatureQueryAsync(
            descriptor.Layer,
            propertyName: null,
            sortBy: null,
            bbox: null,
            filter: null,
            resourceId: featureId,
            srsName: null,
            enforceResourceIdTypeMatch: true,
            cancellationToken).ConfigureAwait(false);

        var result = await _gmlFeatureStore.QueryGmlAsync(
            descriptor.Layer.Id,
            query with { Limit = 1 },
            cancellationToken).ConfigureAwait(false);
        if (result.Items.IsDefaultOrEmpty)
        {
            return CreateStoredQueryFeatureNotFoundResult(context, featureId);
        }

        var plan = new LayerQueryPlan(descriptor, query with { Limit = 1 }, 1);
        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            WriteFeature(writer, plan, result.Items[0], includeMemberWrapper: false);
            writer.WriteEndDocument();
        });

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private static IResult CreateStoredQueryFeatureNotFoundResult(HttpContext context, string featureId)
    {
        return Wfs20ErrorResults.CreateNotFound(
            context,
            "NotFound",
            $"Feature '{featureId}' was not found.",
            "id");
    }

    public async Task<IResult> HandleGetFeatureAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        string? count,
        string? startIndex,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_feature", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetFeature);
        activity?.SetTag("wfs.type_names", typeNames ?? "ALL");
        activity?.SetTag("wfs.output_format", outputFormat ?? Wfs20Utilities.OutputFormats.Default);

        var normalizedFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        if (!Wfs20Utilities.TryNormalizeResultType(resultType, out var normalizedResultType))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid RESULTTYPE parameter '{resultType}'. Supported values are 'results' and 'hits'.",
                "resultType");
        }

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

        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        var isHitsRequest = string.Equals(normalizedResultType, "hits", StringComparison.OrdinalIgnoreCase);

        Wfs20Log.GetFeatureRequested(_logger, typeNames ?? "ALL", normalizedFormat);

        try
        {
            if (!IsSupportedFeatureOutputFormat(normalizedFormat))
            {
                Wfs20Log.UnsupportedOutputFormatRequested(_logger, outputFormat ?? normalizedFormat);
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Unsupported output format '{outputFormat}'. Supported formats: {string.Join(", ", Wfs20Utilities.OutputFormats.All)}",
                    "outputFormat");
            }

            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            var wfsUrl = $"{BaseUrlResolver.GetBaseUrl(context)}/wfs";
            if (selectedTypes.Length == 0)
            {
                return isHitsRequest
                    ? CreateHitsFeatureCollectionResult(0)
                    : CreateEmptyFeatureCollectionResult(normalizedFormat);
            }

            if (ShouldUsePagedGetFeatureFastPath(selectedTypes, normalizedFormat, isHitsRequest, maxFeatures))
            {
                var descriptor = selectedTypes[0];
                var query = (await BuildFeatureQueryAsync(
                    descriptor.Layer,
                    propertyName,
                    sortBy,
                    bbox,
                    filter,
                    resourceId,
                    srsName,
                    selectedTypes.Length == 1,
                    cancellationToken).ConfigureAwait(false)) with
                {
                    Offset = offset,
                    Limit = maxFeatures
                };

                var pagedResult = await BuildPagedGetFeatureResultAsync(
                    descriptor,
                    query,
                    normalizedFormat,
                    cancellationToken);

                Wfs20Log.GetFeatureReturned(
                    _logger,
                    pagedResult.ReturnedCount,
                    pagedResult.NumberMatchedSummary);

                return pagedResult.Result;
            }

            var planSet = await BuildLayerQueryPlansAsync(
                selectedTypes,
                propertyName,
                sortBy,
                bbox,
                filter,
                resourceId,
                srsName,
                offset,
                maxFeatures,
                cancellationToken);

            if (planSet.TotalMatched == 0)
            {
                var emptyMetadata = BuildFeatureCollectionResponseMetadata(
                    wfsUrl,
                    selectedTypes,
                    outputFormat,
                    count,
                    sortBy,
                    bbox,
                    filter,
                    resourceId,
                    propertyName,
                    srsName,
                    normalizedResultType,
                    offset,
                    maxFeatures,
                    0,
                    0);
                return isHitsRequest
                    ? CreateHitsFeatureCollectionResult(0, emptyMetadata.SchemaLocation, emptyMetadata.PagingLinks.Next, emptyMetadata.PagingLinks.Previous)
                    : CreateEmptyFeatureCollectionResult(normalizedFormat, emptyMetadata.SchemaLocation, emptyMetadata.PagingLinks.Next, emptyMetadata.PagingLinks.Previous);
            }

            if (isHitsRequest)
            {
                var hitsMetadata = BuildFeatureCollectionResponseMetadata(
                    wfsUrl,
                    selectedTypes,
                    outputFormat,
                    count,
                    sortBy,
                    bbox,
                    filter,
                    resourceId,
                    propertyName,
                    srsName,
                    normalizedResultType,
                    offset,
                    maxFeatures,
                    planSet.TotalMatched,
                    0);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var matchedSummary = planSet.TotalMatched.ToString(CultureInfo.InvariantCulture);
                    Wfs20Log.GetFeatureReturned(_logger, 0, matchedSummary);
                }
                return CreateHitsFeatureCollectionResult(
                    planSet.TotalMatched,
                    hitsMetadata.SchemaLocation,
                    hitsMetadata.PagingLinks.Next,
                    hitsMetadata.PagingLinks.Previous);
            }

            var expectedReturnedCount = planSet.Plans.Sum(plan => plan.Query.Limit ?? 0);
            var responseMetadata = BuildFeatureCollectionResponseMetadata(
                wfsUrl,
                selectedTypes,
                outputFormat,
                count,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                normalizedResultType,
                offset,
                maxFeatures,
                planSet.TotalMatched,
                expectedReturnedCount);

            var (result, returnedCount) = normalizedFormat switch
            {
                Wfs20Utilities.OutputFormats.Csv => await BuildCsvResultAsync(planSet, cancellationToken),
                Wfs20Utilities.OutputFormats.GeoJson or Wfs20Utilities.OutputFormats.Json => await BuildJsonResultAsync(planSet, normalizedFormat, cancellationToken),
                _ => await BuildGmlResultAsync(
                    planSet,
                    responseMetadata.SchemaLocation,
                    responseMetadata.PagingLinks.Next,
                    responseMetadata.PagingLinks.Previous,
                    cancellationToken)
            };

            if (_logger.IsEnabled(LogLevel.Information))
            {
                var matchedSummary = planSet.TotalMatched.ToString(CultureInfo.InvariantCulture);
                Wfs20Log.GetFeatureReturned(
                    _logger,
                    returnedCount,
                    matchedSummary);
            }

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
            var exceptionCode = ex.Message.Contains("boundedBy", StringComparison.OrdinalIgnoreCase)
                ? "OperationProcessingFailed"
                : "InvalidParameterValue";
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                exceptionCode,
                "Invalid WFS parameter value; see logs for details.");
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.GetFeature, ex.Message);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process GetFeature request.");
        }
    }

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

    /// <summary>
    /// Processes WFS 2.0 Transaction requests by translating XML actions into the shared edit pipeline.
    /// Behavior reference: ../Honua.Server/src/domain/ogc/wfs/WfsTransactionHandlers.cs
    /// Preserves request order and carries insert handles into InsertResults.
    /// </summary>
    public async Task<IResult> HandleTransactionAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.transaction", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.Transaction);

        try
        {
            var document = await ReadTransactionDocumentAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, Wfs20Utilities.Operations.Transaction, StringComparison.OrdinalIgnoreCase))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "OperationParsingFailed",
                    "Transaction payload must contain a wfs:Transaction root element.",
                    "request");
            }

            var rollbackOnFailure = ResolveRollbackOnFailure(context.Request, root);
            var prepared = await PrepareTransactionAsync(
                context,
                root,
                rollbackOnFailure,
                cancellationToken).ConfigureAwait(false);

            if (!prepared.IsValid)
            {
                return prepared.ErrorResult!;
            }

            if (prepared.Operations.IsDefaultOrEmpty)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "MissingParameterValue",
                    "Transaction request did not contain any supported operations.",
                    "request");
            }

            if (prepared.LayerIdCount > 1)
            {
                return Wfs20ErrorResults.CreateNotImplemented(
                    context,
                    "OperationNotSupported",
                    "Transactions spanning multiple feature types are not yet supported.",
                    "typeName");
            }

            Wfs20Log.TransactionRequested(_logger, prepared.InsertCount, prepared.UpdateCount, prepared.DeleteCount);

            var editBatch = FeatureEditBatch.Create(
                rollbackOnFailure: rollbackOnFailure,
                operations: prepared.Operations
                    .Select(static operation => operation.EditOperation)
                    .ToImmutableArray());

            var editResult = await _featureWriter.ApplyEditsAsync(
                prepared.LayerId,
                editBatch,
                cancellationToken).ConfigureAwait(false);

            if (editResult.HasErrors)
            {
                var firstError = GetFirstTransactionError(editResult);
                if (editResult.WasRolledBack)
                {
                    Wfs20Log.TransactionRolledBack(_logger, firstError);
                }

                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "OperationProcessingFailed",
                    firstError,
                    "Transaction");
            }

            var serviceId = await FeatureMutationEventService.ResolveServiceIdAsync(
                context,
                prepared.LayerId,
                ServiceProtocols.Wfs20,
                cancellationToken).ConfigureAwait(false);

            if ((editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount) > 0)
            {
                await _mutationEventService.InvalidateLayerAsync(serviceId, prepared.LayerId, CancellationToken.None).ConfigureAwait(false);
                await PublishTransactionEventsAsync(
                    context,
                    serviceId,
                    prepared,
                    editResult,
                    CancellationToken.None).ConfigureAwait(false);
            }

            Wfs20Log.TransactionReturned(_logger, editResult.CreatedCount, editResult.UpdatedCount, editResult.DeletedCount);

            var responseXml = BuildTransactionResponseXml(prepared, editResult);
            return Results.Content(responseXml, "application/xml", Encoding.UTF8);
        }
        catch (InvalidDataException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "OperationParsingFailed",
                ex.Message,
                "request");
        }
        catch (WfsTransactionException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                ex.ExceptionCode,
                ex.Message,
                ex.Locator);
        }
        catch (ArgumentException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                ex.Message,
                "request");
        }
        catch (NotSupportedException ex)
        {
            Wfs20Log.UnsupportedOperationRequested(_logger, ex.Message);
            return Wfs20ErrorResults.CreateNotImplemented(
                context,
                "OperationNotSupported",
                ex.Message,
                "request");
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.Transaction, ex.Message);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process Transaction request.");
        }
    }

    private async Task<TransactionPreparationResult> PrepareTransactionAsync(
        HttpContext context,
        XElement root,
        bool rollbackOnFailure,
        CancellationToken cancellationToken)
    {
        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var operations = ImmutableArray.CreateBuilder<PreparedTransactionOperation>();
        var distinctLayerIds = new HashSet<int>();
        var validatedLayerIds = new HashSet<int>();

        foreach (var actionElement in root.Elements())
        {
            IResult? errorResult = actionElement.Name.LocalName switch
            {
                "Insert" => await PrepareInsertOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Update" => await PrepareUpdateOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Delete" => await PrepareDeleteOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Replace" => await PrepareReplaceOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Native" => Wfs20ErrorResults.CreateNotImplemented(
                    context,
                    "OperationNotSupported",
                    "Native transaction actions are not supported.",
                    "request"),
                _ => Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Unsupported transaction action '{actionElement.Name.LocalName}'.",
                    "request")
            };

            if (errorResult != null)
            {
                return TransactionPreparationResult.Failure(errorResult);
            }
        }

        if (operations.Count > _editLimits.MaxEditsPerTransaction)
        {
            return TransactionPreparationResult.Failure(Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Transaction contains {operations.Count.ToString(CultureInfo.InvariantCulture)} operations, exceeding the maximum of {_editLimits.MaxEditsPerTransaction.ToString(CultureInfo.InvariantCulture)}.",
                "request"));
        }

        if (rollbackOnFailure && distinctLayerIds.Count > 1)
        {
            return TransactionPreparationResult.Failure(Wfs20ErrorResults.CreateNotImplemented(
                context,
                "OperationNotSupported",
                "Atomic transactions spanning multiple feature types are not supported.",
                "typeName"));
        }

        var layerId = operations.Count == 0
            ? 0
            : operations[0].Descriptor.Layer.Id;

        return TransactionPreparationResult.Success(
            operations.ToImmutable(),
            layerId,
            distinctLayerIds.Count);
    }

    private async Task<IResult?> PrepareInsertOperationsAsync(
        HttpContext context,
        XElement insertElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = insertElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var featureElements = insertElement.Elements().ToArray();
        if (featureElements.Length == 0)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Insert action must contain at least one feature element.",
                "Insert");
        }

        if (featureElements.Length > _editLimits.MaxFeaturesPerEdit)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Insert action exceeds the maximum of {_editLimits.MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture)} features.",
                "Insert");
        }

        foreach (var featureElement in featureElements)
        {
            var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, featureElement.Name.LocalName);
            var validationError = await ValidateTransactionLayerWriteAccessAsync(
                context,
                descriptor.Layer.Id,
                validatedLayerIds,
                cancellationToken).ConfigureAwait(false);
            if (validationError != null)
            {
                return validationError;
            }

            distinctLayerIds.Add(descriptor.Layer.Id);
            var feature = await BuildTransactionInsertFeatureAsync(
                descriptor.Layer,
                featureElement,
                cancellationToken).ConfigureAwait(false);
            operations.Add(new PreparedTransactionOperation(
                descriptor,
                TransactionActionKind.Insert,
                FeatureEditOperation.Create(feature),
                handle,
                feature,
                null));
        }

        return null;
    }

    private async Task<IResult?> PrepareUpdateOperationsAsync(
        HttpContext context,
        XElement updateElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = updateElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var typeName = updateElement.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "typeName", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attribute.Name.LocalName, "typeNames", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Update action requires a typeName attribute.",
                "typeName");
        }

        var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, typeName);
        var validationError = await ValidateTransactionLayerWriteAccessAsync(
            context,
            descriptor.Layer.Id,
            validatedLayerIds,
            cancellationToken).ConfigureAwait(false);
        if (validationError != null)
        {
            return validationError;
        }

        distinctLayerIds.Add(descriptor.Layer.Id);

        var changes = ParseTransactionUpdateChanges(descriptor.Layer, updateElement);
        var targetIds = await ResolveTransactionTargetObjectIdsAsync(
            descriptor.Layer,
            updateElement,
            cancellationToken).ConfigureAwait(false);

        if (targetIds.Length > _editLimits.MaxFeaturesPerEdit)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Update action exceeds the maximum of {_editLimits.MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture)} matched features.",
                "Filter");
        }

        foreach (var objectId in targetIds)
        {
            var existing = await _featureReader.GetAsync(
                descriptor.Layer.Id,
                objectId,
                cancellationToken).ConfigureAwait(false);
            if (!existing.HasValue)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Feature '{BuildFeatureId(descriptor, objectId)}' not found.",
                    "Filter");
            }

            var updatedFeature = await BuildTransactionUpdatedFeatureAsync(
                existing.Value,
                descriptor.Layer,
                changes,
                cancellationToken).ConfigureAwait(false);
            operations.Add(new PreparedTransactionOperation(
                descriptor,
                TransactionActionKind.Update,
                FeatureEditOperation.Update(updatedFeature),
                handle,
                updatedFeature,
                null));
        }

        return null;
    }

    private async Task<IResult?> PrepareDeleteOperationsAsync(
        HttpContext context,
        XElement deleteElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = deleteElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var typeName = deleteElement.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "typeName", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attribute.Name.LocalName, "typeNames", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Delete action requires a typeName attribute.",
                "typeName");
        }

        var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, typeName);
        var validationError = await ValidateTransactionLayerWriteAccessAsync(
            context,
            descriptor.Layer.Id,
            validatedLayerIds,
            cancellationToken).ConfigureAwait(false);
        if (validationError != null)
        {
            return validationError;
        }

        distinctLayerIds.Add(descriptor.Layer.Id);

        var targetIds = await ResolveTransactionTargetObjectIdsAsync(
            descriptor.Layer,
            deleteElement,
            cancellationToken).ConfigureAwait(false);

        if (targetIds.Length > _editLimits.MaxFeaturesPerEdit)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Delete action exceeds the maximum of {_editLimits.MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture)} matched features.",
                "Filter");
        }

        foreach (var objectId in targetIds)
        {
            var existing = await _featureReader.GetAsync(
                descriptor.Layer.Id,
                objectId,
                cancellationToken).ConfigureAwait(false);
            if (!existing.HasValue)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Feature '{BuildFeatureId(descriptor, objectId)}' not found.",
                    "Filter");
            }

            operations.Add(new PreparedTransactionOperation(
                descriptor,
                TransactionActionKind.Delete,
                FeatureEditOperation.Delete(objectId),
                handle,
                null,
                existing.Value));
        }

        return null;
    }

    private async Task<IResult?> PrepareReplaceOperationsAsync(
        HttpContext context,
        XElement replaceElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = replaceElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var featureElement = replaceElement.Elements()
            .FirstOrDefault(element => !string.Equals(element.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase));
        if (featureElement == null)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Replace action must contain a replacement feature element.",
                "Replace");
        }

        var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, featureElement.Name.LocalName);
        var validationError = await ValidateTransactionLayerWriteAccessAsync(
            context,
            descriptor.Layer.Id,
            validatedLayerIds,
            cancellationToken).ConfigureAwait(false);
        if (validationError != null)
        {
            return validationError;
        }

        distinctLayerIds.Add(descriptor.Layer.Id);

        var targetIds = await ResolveTransactionTargetObjectIdsAsync(
            descriptor.Layer,
            replaceElement,
            cancellationToken).ConfigureAwait(false);
        if (targetIds.Length != 1)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                "Replace action must identify exactly one target feature.",
                "Filter");
        }

        var existing = await _featureReader.GetAsync(
            descriptor.Layer.Id,
            targetIds[0],
            cancellationToken).ConfigureAwait(false);
        if (!existing.HasValue)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Feature '{BuildFeatureId(descriptor, targetIds[0])}' not found.",
                "Filter");
        }

        var replacement = await BuildTransactionReplaceFeatureAsync(
            descriptor.Layer,
            targetIds[0],
            featureElement,
            cancellationToken).ConfigureAwait(false);
        operations.Add(new PreparedTransactionOperation(
            descriptor,
            TransactionActionKind.Replace,
            FeatureEditOperation.Update(replacement),
            handle,
            replacement,
            null));

        return null;
    }

    private async Task<Feature> BuildTransactionInsertFeatureAsync(
        LayerDefinition layer,
        XElement featureElement,
        CancellationToken cancellationToken)
    {
        var payload = ReadTransactionFeaturePayload(layer, featureElement);

        return await CreateTransactionFeatureAsync(
            layer,
            objectId: 0,
            payload.Geometry,
            payload.Attributes,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Feature> BuildTransactionReplaceFeatureAsync(
        LayerDefinition layer,
        long objectId,
        XElement featureElement,
        CancellationToken cancellationToken)
    {
        var payload = ReadTransactionFeaturePayload(layer, featureElement);

        return await CreateTransactionFeatureAsync(
            layer,
            objectId,
            payload.Geometry,
            payload.Attributes,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Feature> BuildTransactionUpdatedFeatureAsync(
        Feature existing,
        LayerDefinition layer,
        TransactionFeatureChanges changes,
        CancellationToken cancellationToken)
    {
        var attributes = existing.Attributes.ToBuilder();
        foreach (var (key, value) in changes.Attributes)
        {
            attributes[key] = value;
        }

        var geometry = changes.GeometrySpecified
            ? changes.Geometry
            : existing.Geometry;

        return await CreateTransactionFeatureAsync(
            layer,
            existing.Id,
            geometry,
            attributes.ToImmutable(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Feature> CreateTransactionFeatureAsync(
        LayerDefinition layer,
        long objectId,
        byte[]? geometry,
        ImmutableDictionary<string, object?> attributes,
        CancellationToken cancellationToken)
    {
        var geometryValidation = await _mutationValidator.ValidateGeometryAsync(
            geometry,
            cancellationToken).ConfigureAwait(false);
        if (!geometryValidation.IsValid)
        {
            throw new ArgumentException($"Geometry validation failed: {geometryValidation.ErrorMessage}");
        }

        var attributesResult = _mutationValidator.ValidateAttributes(
            layer,
            attributes,
            ValidationExtensions.AttributeValidationMode.Strict);
        if (!attributesResult.IsValid)
        {
            throw new ArgumentException(attributesResult.ErrorMessage ?? "Invalid attributes.");
        }

        return Feature.Create(objectId, geometryValidation.Geometry, attributesResult.Value!);
    }

    private TransactionFeaturePayload ReadTransactionFeaturePayload(
        LayerDefinition layer,
        XElement featureElement)
    {
        var attributes = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        var gmlAssignedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? geometry = null;

        foreach (var propertyElement in featureElement.Elements())
        {
            if (TryResolveReservedGmlTransactionAttributeName(
                layer,
                propertyElement.Name.LocalName,
                propertyElement.Name.NamespaceName,
                out var reservedAttributeName))
            {
                attributes[reservedAttributeName] = ParseTransactionReservedAttributeValue(propertyElement);
                continue;
            }

            var resolution = ResolveTransactionField(
                layer,
                propertyElement.Name.LocalName,
                propertyElement.Name.NamespaceName);
            if (!resolution.HasValue)
            {
                continue;
            }

            var resolvedField = resolution.Value;

            if (layer.GeometryField != null &&
                resolvedField.Field.Name.Equals(layer.GeometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                geometry = ParseTransactionGeometryProperty(propertyElement, layer);
                continue;
            }

            if (resolvedField.IsGmlProperty)
            {
                gmlAssignedFields.Add(resolvedField.Field.Name);
                attributes[resolvedField.Field.Name] = ParseTransactionFieldValue(resolvedField.Field, propertyElement);
                continue;
            }

            if (gmlAssignedFields.Contains(resolvedField.Field.Name))
            {
                continue;
            }

            attributes[resolvedField.Field.Name] = ParseTransactionFieldValue(resolvedField.Field, propertyElement);
        }

        return new TransactionFeaturePayload(geometry, attributes.ToImmutable());
    }

    private TransactionFeatureChanges ParseTransactionUpdateChanges(
        LayerDefinition layer,
        XElement updateElement)
    {
        var attributes = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        var propertyElements = updateElement.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Property", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (propertyElements.Length == 0)
        {
            throw new ArgumentException("Update action must include at least one Property element.");
        }

        byte[]? geometry = null;
        var geometrySpecified = false;
        var gmlAssignedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var propertyElement in propertyElements)
        {
            var nameElement = propertyElement.Elements()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "ValueReference", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase));
            if (nameElement == null)
            {
                continue;
            }

            var normalizedPropertyName = NormalizeTransactionPropertyReference(nameElement.Value);
            if (normalizedPropertyName.Equals("boundedBy", StringComparison.OrdinalIgnoreCase))
            {
                throw new WfsTransactionException(
                    "InvalidValue",
                    "Property 'gml:boundedBy' cannot be updated with the supplied value.",
                    "ValueReference");
            }

            var valueElement = propertyElement.Elements()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Value", StringComparison.OrdinalIgnoreCase));

            if (TryResolveReservedGmlTransactionAttributeName(
                layer,
                nameElement.Value.Trim(),
                namespaceName: null,
                out var reservedAttributeName))
            {
                attributes[reservedAttributeName] = valueElement == null
                    ? null
                    : ParseTransactionReservedAttributeValue(valueElement);
                continue;
            }

            var fieldResolution = ResolveTransactionField(layer, nameElement.Value.Trim());
            if (!fieldResolution.HasValue)
            {
                continue;
            }

            var resolvedField = fieldResolution.Value;

            if (layer.GeometryField != null &&
                resolvedField.Field.Name.Equals(layer.GeometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                geometrySpecified = true;
                geometry = valueElement == null
                    ? null
                    : ParseTransactionGeometryProperty(valueElement, layer);
                continue;
            }

            if (resolvedField.IsGmlProperty)
            {
                gmlAssignedFields.Add(resolvedField.Field.Name);
            }
            else if (gmlAssignedFields.Contains(resolvedField.Field.Name))
            {
                continue;
            }

            attributes[resolvedField.Field.Name] = valueElement == null
                ? null
                : ParseTransactionFieldValue(resolvedField.Field, valueElement);
        }

        if (attributes.Count == 0 && !geometrySpecified)
        {
            throw new ArgumentException("Update action must include at least one mutable property.");
        }

        return new TransactionFeatureChanges(geometrySpecified, geometry, attributes.ToImmutable());
    }

    private async Task<ImmutableArray<long>> ResolveTransactionTargetObjectIdsAsync(
        LayerDefinition layer,
        XElement actionElement,
        CancellationToken cancellationToken)
    {
        var filterElement = actionElement.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase));
        var filterChildren = filterElement?.Elements().ToArray() ?? [];
        var resourceIdValues = filterElement == null
            ? []
            : filterElement
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Attributes()
                    .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "rid", StringComparison.OrdinalIgnoreCase))
                    ?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        var resourceIds = resourceIdValues.Length == 0
            ? null
            : string.Join(',', resourceIdValues);
        var hasOnlyResourceIdPredicates = filterChildren.Length > 0 &&
            filterChildren.All(element => string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase));
        var filterXml = hasOnlyResourceIdPredicates
            ? null
            : filterElement?.ToString(SaveOptions.DisableFormatting);

        if (string.IsNullOrWhiteSpace(filterXml) && string.IsNullOrWhiteSpace(resourceIds))
        {
            throw new ArgumentException("Update and Delete actions must include a Filter or ResourceId.");
        }

        if (!hasOnlyResourceIdPredicates &&
            resourceIdValues.Length > 0)
        {
            throw new NotSupportedException("Transaction filters that combine ResourceId with other predicates are not yet supported.");
        }

        var query = await BuildFeatureQueryAsync(
            layer,
            propertyName: null,
            sortBy: null,
            bbox: null,
            filter: filterXml,
            resourceId: resourceIds,
            srsName: null,
            enforceResourceIdTypeMatch: true,
            cancellationToken).ConfigureAwait(false);

        var objectIds = await _featureReader.QueryObjectIdsAsync(
            layer.Id,
            query,
            cancellationToken).ConfigureAwait(false);

        if (objectIds.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Transaction filter did not match any features.");
        }

        return objectIds;
    }

    private async Task<IResult?> ValidateTransactionLayerWriteAccessAsync(
        HttpContext context,
        int layerId,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        if (!validatedLayerIds.Add(layerId))
        {
            return null;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            scope: AccessScope.Write,
            requiredProtocol: ServiceProtocols.Wfs20,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult;
        }

        return await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
            context,
            layerId,
            cancellationToken).ConfigureAwait(false);
    }

    private static WfsFeatureTypeDescriptor ResolveTransactionFeatureTypeDescriptor(
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        string rawTypeName)
    {
        if (string.IsNullOrWhiteSpace(rawTypeName))
        {
            throw new WfsTransactionException("MissingParameterValue", "Transaction action is missing a feature type name.", "typeName");
        }

        var trimmedTypeName = rawTypeName.Trim();
        var localName = trimmedTypeName.Contains(':', StringComparison.Ordinal)
            ? trimmedTypeName[(trimmedTypeName.LastIndexOf(':') + 1)..]
            : trimmedTypeName;

        foreach (var descriptor in descriptors)
        {
            if (descriptor.QualifiedName.Equals(trimmedTypeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(trimmedTypeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor;
            }
        }

        throw new WfsTransactionException("InvalidValue", $"Unknown feature type '{rawTypeName}'.", "typeName");
    }

    private static TransactionFieldResolution? ResolveTransactionField(
        LayerDefinition layer,
        string rawName,
        string? namespaceName = null)
    {
        var normalizedName = NormalizeTransactionPropertyReference(rawName);
        if (TryResolveGmlTransactionField(layer, normalizedName, rawName, namespaceName, out var gmlField))
        {
            return gmlField == null
                ? null
                : new TransactionFieldResolution(gmlField, IsGmlProperty: true);
        }

        var resolvedName = FilterExpressionHelpers.ResolveFieldName(
            layer,
            normalizedName,
            allowGeometryAlias: true);
        if (resolvedName == null)
        {
            throw new ArgumentException($"Unknown property '{rawName}' for feature type '{layer.Name}'.");
        }

        if (layer.PrimaryKeyField != null &&
            resolvedName.Equals(layer.PrimaryKeyField.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var resolvedField = layer.Fields.FirstOrDefault(field =>
            field.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown property '{rawName}' for feature type '{layer.Name}'.");

        return new TransactionFieldResolution(resolvedField, IsGmlProperty: false);
    }

    private static string NormalizeTransactionPropertyReference(string rawName)
    {
        var normalized = rawName.Trim();
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < normalized.Length - 1)
        {
            normalized = normalized[(lastSlash + 1)..];
        }

        if (normalized.StartsWith('@'))
        {
            normalized = normalized[1..];
        }

        var predicateIndex = normalized.IndexOf('[');
        if (predicateIndex > 0)
        {
            normalized = normalized[..predicateIndex];
        }

        var colonIndex = normalized.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < normalized.Length - 1)
        {
            normalized = normalized[(colonIndex + 1)..];
        }

        return normalized.Trim();
    }

    private static bool TryResolveReservedGmlTransactionAttributeName(
        LayerDefinition layer,
        string rawName,
        string? namespaceName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? attributeName)
    {
        attributeName = null;

        var isGmlProperty = string.Equals(namespaceName, Wfs20Utilities.GmlNamespace, StringComparison.Ordinal) ||
            rawName.StartsWith("gml:", StringComparison.OrdinalIgnoreCase);
        if (!isGmlProperty)
        {
            return false;
        }

        var normalizedName = NormalizeTransactionPropertyReference(rawName);
        var localName = normalizedName.Contains(':', StringComparison.Ordinal)
            ? normalizedName[(normalizedName.LastIndexOf(':') + 1)..]
            : normalizedName;

        attributeName = localName.ToLowerInvariant() switch
        {
            "identifier" => ValidationExtensions.WfsGmlIdentifierAttributeName,
            "name" when !HasTransactionAttributeField(layer, "name") => ValidationExtensions.WfsGmlNameAttributeName,
            "description" when !HasTransactionAttributeField(layer, "description") => ValidationExtensions.WfsGmlDescriptionAttributeName,
            _ => null
        };

        return attributeName != null;
    }

    private static bool HasTransactionAttributeField(LayerDefinition layer, string fieldName)
    {
        return layer.AttributeFields.Any(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveGmlTransactionField(
        LayerDefinition layer,
        string normalizedName,
        string rawName,
        string? namespaceName,
        out FieldDefinition? field)
    {
        field = null;

        var isGmlProperty = string.Equals(namespaceName, Wfs20Utilities.GmlNamespace, StringComparison.Ordinal) ||
            rawName.StartsWith("gml:", StringComparison.OrdinalIgnoreCase);
        if (!isGmlProperty)
        {
            return false;
        }

        var localName = normalizedName.Contains(':', StringComparison.Ordinal)
            ? normalizedName[(normalizedName.LastIndexOf(':') + 1)..]
            : normalizedName;

        var mappedFieldName = localName.ToLowerInvariant() switch
        {
            "name" => "name",
            "description" => "description",
            "identifier" => null,
            _ => null
        };
        if (mappedFieldName == null)
        {
            return true;
        }

        field = layer.Fields.FirstOrDefault(candidate =>
            candidate.Name.Equals(mappedFieldName, StringComparison.OrdinalIgnoreCase));
        if (field != null &&
            layer.PrimaryKeyField != null &&
            field.Name.Equals(layer.PrimaryKeyField.Name, StringComparison.OrdinalIgnoreCase))
        {
            field = null;
        }

        return true;
    }

    private static object? ParseTransactionFieldValue(FieldDefinition field, XElement valueElement)
    {
        if (HasNilAttribute(valueElement))
        {
            return null;
        }

        var rawValue = valueElement.Value.Trim();
        return field.Type switch
        {
            FieldType.Integer or FieldType.BigInteger
                => long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue)
                    ? integerValue
                    : rawValue,
            FieldType.Double or FieldType.Float
                => double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue)
                    ? numericValue
                    : rawValue,
            FieldType.Boolean
                => bool.TryParse(rawValue, out var booleanValue)
                    ? booleanValue
                    : rawValue,
            _ => rawValue
        };
    }

    private static string? ParseTransactionReservedAttributeValue(XElement valueElement)
    {
        if (HasNilAttribute(valueElement))
        {
            return null;
        }

        return valueElement.Value.Trim();
    }

    private static byte[]? ParseTransactionGeometryProperty(
        XElement propertyElement,
        LayerDefinition layer)
    {
        if (HasNilAttribute(propertyElement))
        {
            return null;
        }

        var geometryElement = propertyElement.Name.NamespaceName == Wfs20Utilities.GmlNamespace
            ? propertyElement
            : propertyElement.Elements()
                .FirstOrDefault(element => string.Equals(element.Name.NamespaceName, Wfs20Utilities.GmlNamespace, StringComparison.Ordinal));

        if (geometryElement == null)
        {
            if (propertyElement.IsEmpty)
            {
                return null;
            }

            throw new ArgumentException($"Geometry property '{propertyElement.Name.LocalName}' must contain a GML geometry.");
        }

        var defaultSrid = layer.SpatialReference.ToSrid();
        var geometry = ParseTransactionGeometry(geometryElement, defaultSrid);
        return GeometryWkbWriter.Write(geometry);
    }

    private static Geometry ParseTransactionGeometry(XElement geometryElement, int defaultSrid)
    {
        var crsDefinition = geometryElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "srsName", StringComparison.OrdinalIgnoreCase))
            ?.Value is { Length: > 0 } srsName &&
            SpatialReferenceHelpers.TryParseCrsDefinition(srsName, out var parsedDefinition)
            ? parsedDefinition
            : SpatialReferenceHelpers.TryParseCrsDefinition(defaultSrid.ToString(CultureInfo.InvariantCulture), out var defaultDefinition)
                ? defaultDefinition
                : new CrsDefinition(
                    FormattableString.Invariant($"http://www.opengis.net/def/crs/EPSG/0/{defaultSrid}"),
                    defaultSrid,
                    AxisOrder.EastNorth,
                    IsGeographic: false);

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(crsDefinition.Srid);

        return geometryElement.Name.LocalName switch
        {
            "Point" => ParseTransactionPointGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "MultiPoint" => ParseTransactionMultiPointGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "LineString" or "Curve" => ParseTransactionLineStringGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "MultiLineString" or "MultiCurve" => ParseTransactionMultiLineStringGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "Polygon" or "Surface" => ParseTransactionPolygonGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "MultiPolygon" or "MultiSurface" => ParseTransactionMultiPolygonGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            _ => throw new NotSupportedException($"Unsupported GML geometry type '{geometryElement.Name.LocalName}'.")
        };
    }

    private static Point ParseTransactionPointGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var positionElement = geometryElement.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "pos", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Point geometry must contain a gml:pos element.");

        return geometryFactory.CreatePoint(ParseTransactionCoordinate(positionElement.Value, axisOrder));
    }

    private static MultiPoint ParseTransactionMultiPointGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var points = geometryElement
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Point", StringComparison.OrdinalIgnoreCase))
            .Select(pointElement => ParseTransactionPointGeometry(pointElement, geometryFactory, axisOrder))
            .ToArray();

        if (points.Length == 0)
        {
            throw new ArgumentException("MultiPoint geometry must contain at least one gml:Point element.");
        }

        return geometryFactory.CreateMultiPoint(points);
    }

    private static LineString ParseTransactionLineStringGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var coordinates = ParseTransactionCoordinateSequence(geometryElement, axisOrder);
        if (coordinates.Length < 2)
        {
            throw new ArgumentException("LineString geometry must contain at least two positions.");
        }

        return geometryFactory.CreateLineString(coordinates);
    }

    private static MultiLineString ParseTransactionMultiLineStringGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var lineStrings = geometryElement
            .Descendants()
            .Where(element =>
                string.Equals(element.Name.LocalName, "LineString", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Name.LocalName, "Curve", StringComparison.OrdinalIgnoreCase))
            .Select(lineElement => ParseTransactionLineStringGeometry(lineElement, geometryFactory, axisOrder))
            .ToArray();

        if (lineStrings.Length == 0)
        {
            throw new ArgumentException("MultiLineString geometry must contain at least one line geometry.");
        }

        return geometryFactory.CreateMultiLineString(lineStrings);
    }

    private static Polygon ParseTransactionPolygonGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        XElement? polygonSource = geometryElement;
        if (string.Equals(geometryElement.Name.LocalName, "Surface", StringComparison.OrdinalIgnoreCase))
        {
            polygonSource = geometryElement
                .Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "PolygonPatch", StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Surface geometry must contain a PolygonPatch element.");
        }

        var exteriorElement = polygonSource
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "exterior", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Polygon geometry must contain an exterior ring.");

        var shell = geometryFactory.CreateLinearRing(ParseTransactionLinearRingCoordinates(exteriorElement, axisOrder));
        var holes = polygonSource
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "interior", StringComparison.OrdinalIgnoreCase))
            .Select(interiorElement => geometryFactory.CreateLinearRing(ParseTransactionLinearRingCoordinates(interiorElement, axisOrder)))
            .ToArray();

        return geometryFactory.CreatePolygon(shell, holes);
    }

    private static MultiPolygon ParseTransactionMultiPolygonGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var polygons = geometryElement
            .Descendants()
            .Where(element =>
                string.Equals(element.Name.LocalName, "Polygon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Name.LocalName, "Surface", StringComparison.OrdinalIgnoreCase))
            .Select(polygonElement => ParseTransactionPolygonGeometry(polygonElement, geometryFactory, axisOrder))
            .ToArray();

        if (polygons.Length == 0)
        {
            throw new ArgumentException("MultiPolygon geometry must contain at least one polygon geometry.");
        }

        return geometryFactory.CreateMultiPolygon(polygons);
    }

    private static Coordinate[] ParseTransactionLinearRingCoordinates(XElement ringContainer, AxisOrder axisOrder)
    {
        var linearRingElement = ringContainer.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "LinearRing", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Polygon ring must contain a LinearRing element.");
        var coordinates = ParseTransactionCoordinateSequence(linearRingElement, axisOrder);
        if (coordinates.Length < 4)
        {
            throw new ArgumentException("Polygon rings must contain at least four positions.");
        }

        return coordinates;
    }

    private static Coordinate[] ParseTransactionCoordinateSequence(XElement geometryElement, AxisOrder axisOrder)
    {
        var posListElement = geometryElement.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "posList", StringComparison.OrdinalIgnoreCase));
        if (posListElement != null)
        {
            return ParseTransactionPosList(posListElement.Value, axisOrder);
        }

        var positions = geometryElement.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "pos", StringComparison.OrdinalIgnoreCase))
            .Select(element => ParseTransactionCoordinate(element.Value, axisOrder))
            .ToArray();
        if (positions.Length > 0)
        {
            return positions;
        }

        throw new ArgumentException($"Geometry '{geometryElement.Name.LocalName}' must contain gml:pos or gml:posList coordinates.");
    }

    private static Coordinate[] ParseTransactionPosList(string rawPosList, AxisOrder axisOrder)
    {
        var ordinates = rawPosList
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();

        if (ordinates.Length < 2 || ordinates.Length % 2 != 0)
        {
            throw new ArgumentException("GML posList must contain an even number of ordinates.");
        }

        var coordinates = new Coordinate[ordinates.Length / 2];
        for (var index = 0; index < ordinates.Length; index += 2)
        {
            coordinates[index / 2] = CreateTransactionCoordinate(ordinates[index], ordinates[index + 1], axisOrder);
        }

        return coordinates;
    }

    private static Coordinate ParseTransactionCoordinate(string rawPosition, AxisOrder axisOrder)
    {
        var ordinates = rawPosition
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        if (ordinates.Length < 2)
        {
            throw new ArgumentException("GML position must contain at least two ordinates.");
        }

        return CreateTransactionCoordinate(ordinates[0], ordinates[1], axisOrder);
    }

    private static Coordinate CreateTransactionCoordinate(double first, double second, AxisOrder axisOrder)
    {
        return axisOrder == AxisOrder.NorthEast
            ? new Coordinate(second, first)
            : new Coordinate(first, second);
    }

    private static bool HasNilAttribute(XElement element)
    {
        return element.Attributes().Any(attribute =>
            string.Equals(attribute.Name.LocalName, "nil", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(attribute.Value, out var isNil) &&
            isNil);
    }

    private static async Task<XDocument> ReadTransactionDocumentAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        using var reader = new StreamReader(
            request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidDataException("Transaction requests require an XML request body.");
        }

        try
        {
            return XDocument.Parse(body, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Invalid WFS Transaction XML request body.", ex);
        }
    }

    private static bool ResolveRollbackOnFailure(HttpRequest request, XElement root)
    {
        var rawValue = request.Query["rollbackOnFailure"].FirstOrDefault()
            ?? request.Query["ROLLBACKONFAILURE"].FirstOrDefault()
            ?? root.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "rollbackOnFailure", StringComparison.OrdinalIgnoreCase))
                ?.Value;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        return bool.TryParse(rawValue, out var parsed)
            ? parsed
            : throw new ArgumentException("rollbackOnFailure must be a boolean value.");
    }

    private static string GetFirstTransactionError(FeatureEditResult editResult)
    {
        return editResult.CreateResults.Concat(editResult.UpdateResults).Concat(editResult.DeleteResults)
            .FirstOrDefault(result => !result.IsSuccess)
            .ErrorMessage
            ?? "Transaction failed.";
    }

    private async Task PublishTransactionEventsAsync(
        HttpContext context,
        string serviceId,
        TransactionPreparationResult prepared,
        FeatureEditResult editResult,
        CancellationToken cancellationToken)
    {
        var createIndex = 0;
        var updateIndex = 0;
        var deleteIndex = 0;

        foreach (var operation in prepared.Operations)
        {
            switch (operation.EditOperation.Kind)
            {
                case FeatureEditOperationKind.Create:
                    {
                        var result = createIndex < editResult.CreateResults.Length
                            ? editResult.CreateResults[createIndex]
                            : default;
                        createIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue || operation.MutationFeature == null)
                        {
                            break;
                        }

                        var createdFeature = operation.MutationFeature.Value with { Id = result.ObjectId.Value };
                        await _mutationEventService.PublishAsync(
                            context,
                            prepared.LayerId,
                            result.ObjectId.Value,
                            "create",
                            HonuaTelemetry.Protocols.Wfs20,
                            cancellationToken,
                            mutationFeature: createdFeature,
                            serviceId: serviceId,
                            serviceProtocol: ServiceProtocols.Wfs20).ConfigureAwait(false);
                        break;
                    }

                case FeatureEditOperationKind.Update:
                    {
                        var result = updateIndex < editResult.UpdateResults.Length
                            ? editResult.UpdateResults[updateIndex]
                            : default;
                        updateIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue || operation.MutationFeature == null)
                        {
                            break;
                        }

                        await _mutationEventService.PublishAsync(
                            context,
                            prepared.LayerId,
                            result.ObjectId.Value,
                            "update",
                            HonuaTelemetry.Protocols.Wfs20,
                            cancellationToken,
                            mutationFeature: operation.MutationFeature.Value,
                            serviceId: serviceId,
                            serviceProtocol: ServiceProtocols.Wfs20).ConfigureAwait(false);
                        break;
                    }

                case FeatureEditOperationKind.Delete:
                    {
                        var result = deleteIndex < editResult.DeleteResults.Length
                            ? editResult.DeleteResults[deleteIndex]
                            : default;
                        deleteIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue)
                        {
                            break;
                        }

                        await _mutationEventService.PublishAsync(
                            context,
                            prepared.LayerId,
                            result.ObjectId.Value,
                            "delete",
                            HonuaTelemetry.Protocols.Wfs20,
                            cancellationToken,
                            mutationFeature: operation.DeleteSnapshot,
                            serviceId: serviceId,
                            serviceProtocol: ServiceProtocols.Wfs20).ConfigureAwait(false);
                        break;
                    }
            }
        }
    }

    private static string BuildTransactionResponseXml(
        TransactionPreparationResult prepared,
        FeatureEditResult editResult)
    {
        var inserted = new List<(PreparedTransactionOperation Operation, EditOperationResult Result)>();
        var replaced = new List<(PreparedTransactionOperation Operation, EditOperationResult Result)>();
        var updatedCount = 0;
        var deletedCount = 0;
        var createResultIndex = 0;
        var updateResultIndex = 0;
        var deleteResultIndex = 0;

        foreach (var operation in prepared.Operations)
        {
            switch (operation.EditOperation.Kind)
            {
                case FeatureEditOperationKind.Create:
                    {
                        var result = createResultIndex < editResult.CreateResults.Length
                            ? editResult.CreateResults[createResultIndex]
                            : default;
                        createResultIndex++;

                        if (result.IsSuccess && result.ObjectId.HasValue)
                        {
                            inserted.Add((operation, result));
                        }

                        break;
                    }

                case FeatureEditOperationKind.Update:
                    {
                        var result = updateResultIndex < editResult.UpdateResults.Length
                            ? editResult.UpdateResults[updateResultIndex]
                            : default;
                        updateResultIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue)
                        {
                            break;
                        }

                        if (operation.ActionKind == TransactionActionKind.Replace)
                        {
                            replaced.Add((operation, result));
                        }
                        else
                        {
                            updatedCount++;
                        }

                        break;
                    }

                case FeatureEditOperationKind.Delete:
                    {
                        var result = deleteResultIndex < editResult.DeleteResults.Length
                            ? editResult.DeleteResults[deleteResultIndex]
                            : default;
                        deleteResultIndex++;

                        if (result.IsSuccess)
                        {
                            deletedCount++;
                        }

                        break;
                    }
            }
        }

        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "TransactionResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "fes", null, Wfs20Utilities.FesNamespace);
            writer.WriteAttributeString("version", Wfs20Utilities.Version);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            writer.WriteStartElement("wfs", "TransactionSummary", Wfs20Utilities.WfsNamespace);
            if (prepared.InsertCount > 0)
            {
                writer.WriteElementString("wfs", "totalInserted", Wfs20Utilities.WfsNamespace, inserted.Count.ToString(CultureInfo.InvariantCulture));
            }

            if (prepared.UpdateCount > 0)
            {
                writer.WriteElementString("wfs", "totalUpdated", Wfs20Utilities.WfsNamespace, updatedCount.ToString(CultureInfo.InvariantCulture));
            }

            if (prepared.ReplaceCount > 0)
            {
                writer.WriteElementString("wfs", "totalReplaced", Wfs20Utilities.WfsNamespace, replaced.Count.ToString(CultureInfo.InvariantCulture));
            }

            if (prepared.DeleteCount > 0)
            {
                writer.WriteElementString("wfs", "totalDeleted", Wfs20Utilities.WfsNamespace, deletedCount.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndElement();

            if (inserted.Count > 0)
            {
                writer.WriteStartElement("wfs", "InsertResults", Wfs20Utilities.WfsNamespace);
                foreach (var (operation, result) in inserted)
                {
                    var objectId = result.ObjectId ?? 0;
                    writer.WriteStartElement("wfs", "Feature", Wfs20Utilities.WfsNamespace);
                    if (!string.IsNullOrWhiteSpace(operation.Handle))
                    {
                        writer.WriteAttributeString("handle", operation.Handle);
                    }

                    WriteTransactionResourceId(writer, operation.Descriptor, objectId);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            if (replaced.Count > 0)
            {
                writer.WriteStartElement("wfs", "ReplaceResults", Wfs20Utilities.WfsNamespace);
                foreach (var (operation, result) in replaced)
                {
                    var objectId = result.ObjectId ?? 0;
                    writer.WriteStartElement("wfs", "Feature", Wfs20Utilities.WfsNamespace);
                    if (!string.IsNullOrWhiteSpace(operation.Handle))
                    {
                        writer.WriteAttributeString("handle", operation.Handle);
                    }

                    WriteTransactionResourceId(writer, operation.Descriptor, objectId);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
    }

    private static void WriteTransactionResourceId(
        XmlWriter writer,
        WfsFeatureTypeDescriptor descriptor,
        long objectId)
    {
        writer.WriteStartElement("fes", "ResourceId", Wfs20Utilities.FesNamespace);
        writer.WriteAttributeString("rid", BuildFeatureId(descriptor, objectId));
        writer.WriteEndElement();
    }

    private static string BuildListStoredQueriesXml(IReadOnlyList<WfsFeatureTypeDescriptor> descriptors)
    {
        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "ListStoredQueriesResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);

            writer.WriteStartElement("wfs", "StoredQuery", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("id", GetFeatureByIdStoredQueryId);
            WriteStoredQueryTitle(writer, "Get feature by identifier");

            foreach (var descriptor in descriptors)
            {
                writer.WriteElementString("wfs", "ReturnFeatureType", Wfs20Utilities.WfsNamespace, descriptor.QualifiedName);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
    }

    private static string BuildDescribeStoredQueriesXml(IReadOnlyList<WfsFeatureTypeDescriptor> descriptors)
    {
        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "DescribeStoredQueriesResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "fes", null, Wfs20Utilities.FesNamespace);
            writer.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);

            writer.WriteStartElement("wfs", "StoredQueryDescription", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("id", GetFeatureByIdStoredQueryId);
            WriteStoredQueryTitle(writer, "Get feature by identifier");
            WriteStoredQueryAbstract(writer, "Returns a single feature that matches the supplied identifier.");
            writer.WriteStartElement("wfs", "Parameter", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("name", "id");
            writer.WriteAttributeString("type", "xsd:string");
            writer.WriteEndElement();

            foreach (var descriptor in descriptors)
            {
                writer.WriteStartElement("wfs", "QueryExpressionText", Wfs20Utilities.WfsNamespace);
                writer.WriteAttributeString("returnFeatureTypes", descriptor.QualifiedName);
                writer.WriteAttributeString("language", "urn:ogc:def:queryLanguage:OGC-WFS::WFS_QueryExpression");
                writer.WriteAttributeString("isPrivate", XmlConvert.ToString(false));

                writer.WriteStartElement("wfs", "Query", Wfs20Utilities.WfsNamespace);
                writer.WriteAttributeString("typeNames", descriptor.QualifiedName);
                writer.WriteStartElement("fes", "Filter", Wfs20Utilities.FesNamespace);
                writer.WriteStartElement("fes", "ResourceId", Wfs20Utilities.FesNamespace);
                writer.WriteAttributeString("rid", "${id}");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
    }

    private static void WriteStoredQueryTitle(XmlWriter writer, string value)
    {
        writer.WriteStartElement("wfs", "Title", Wfs20Utilities.WfsNamespace);
        writer.WriteAttributeString("xml", "lang", "http://www.w3.org/XML/1998/namespace", "en");
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void WriteStoredQueryAbstract(XmlWriter writer, string value)
    {
        writer.WriteStartElement("wfs", "Abstract", Wfs20Utilities.WfsNamespace);
        writer.WriteAttributeString("xml", "lang", "http://www.w3.org/XML/1998/namespace", "en");
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static bool IsGetFeatureByIdStoredQuery(string storedQueryId)
        => string.Equals(storedQueryId, GetFeatureByIdStoredQueryId, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(storedQueryId, GetFeatureByIdStoredQueryUri, StringComparison.OrdinalIgnoreCase);

    private static WfsFeatureTypeDescriptor? ResolveStoredQueryFeatureTypeDescriptor(
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        string featureId)
    {
        var trimmedFeatureId = featureId.Trim();
        var lastDot = trimmedFeatureId.LastIndexOf('.');
        if (lastDot > 0)
        {
            var typeName = trimmedFeatureId[..lastDot];
            var localName = typeName.Contains(':', StringComparison.Ordinal)
                ? typeName[(typeName.LastIndexOf(':') + 1)..]
                : typeName;

            return descriptors.FirstOrDefault(descriptor =>
                descriptor.QualifiedName.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        }

        return descriptors.Count == 1
            ? descriptors[0]
            : null;
    }

    private async Task<ImmutableArray<WfsFeatureTypeDescriptor>> GetPublishedFeatureTypesAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var layers = await _layerCatalog.ListLayersAsync(cancellationToken);
        var services = await _layerCatalog.ListServicesAsync(cancellationToken);
        var visibleLayers = layers
            .Where(layer => IsPublishedForWfs(context, layer, services))
            .OrderBy(layer => layer.Id)
            .ToArray();

        var descriptors = ImmutableArray.CreateBuilder<WfsFeatureTypeDescriptor>(visibleLayers.Length);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in visibleLayers)
        {
            var baseName = BuildTypeLocalName(layer);
            var localName = baseName;
            if (!usedNames.Add(localName))
            {
                localName = $"{baseName}_{layer.Id}";
                usedNames.Add(localName);
            }

            descriptors.Add(new WfsFeatureTypeDescriptor(
                layer,
                $"{FeatureNamespacePrefix}:{localName}",
                localName));
        }

        return descriptors.ToImmutable();
    }

    private async Task<LayerQueryPlanSet> BuildLayerQueryPlansAsync(
        IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes,
        string? propertyName,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var plans = ImmutableArray.CreateBuilder<LayerQueryPlan>(featureTypes.Count);
        var remainingOffset = offset;
        var remainingCount = count;
        long totalMatched = 0;

        foreach (var featureType in featureTypes)
        {
            var query = await BuildFeatureQueryAsync(
                featureType.Layer,
                propertyName,
                sortBy,
                bbox,
                filter,
                resourceId,
                srsName,
                featureTypes.Count == 1,
                cancellationToken);

            var layerMatched = await _featureReader.CountAsync(featureType.Layer.Id, query, cancellationToken);
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

            plans.Add(new LayerQueryPlan(
                featureType,
                query with { Offset = layerOffset, Limit = layerLimit },
                layerMatched));

            remainingCount -= layerLimit;
        }

        return new LayerQueryPlanSet(plans.ToImmutable(), totalMatched);
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
            var resolvedValueReference = ResolveValueReference(featureType.Layer, valueReference);
            var query = await BuildValueQueryAsync(
                featureType.Layer,
                resolvedValueReference,
                bbox,
                filter,
                resourceId,
                srsName,
                featureTypes.Count == 1,
                cancellationToken);

            var layerMatched = await _featureReader.CountAsync(featureType.Layer.Id, query, cancellationToken);
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

    private async ValueTask<FeatureQuery> BuildFeatureQueryAsync(
        LayerDefinition layer,
        string? propertyName,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        bool enforceResourceIdTypeMatch,
        CancellationToken cancellationToken)
    {
        var projectedFields = ResolveProjectedFields(layer, propertyName);
        var (normalizedFilter, normalizedResourceId) = NormalizeFilterInputs(filter, resourceId);
        var sqlFilter = TranslateFesFilter(layer, normalizedFilter);
        var spatialFilter = ParseBboxFilter(bbox, layer);
        var resourceIds = ParseResourceIds(normalizedResourceId, layer, enforceResourceIdTypeMatch);
        sqlFilter = resourceIds.MatchesNothing
            ? CombineSqlFilters(sqlFilter, FalseSqlFilter)
            : sqlFilter;
        var orderBy = ParseSortBy(layer, sortBy);
        var outputSrid = await ResolveRequestedOutputSridAsync(layer, srsName, cancellationToken).ConfigureAwait(false);
        var outputAxisOrder = await ResolveOutputAxisOrderAsync(srsName, outputSrid, cancellationToken).ConfigureAwait(false);

        return new FeatureQuery
        {
            SqlFilter = sqlFilter,
            ObjectIds = resourceIds.ObjectIds,
            OutFields = projectedFields,
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OutputSrid = outputSrid,
            OutputAxisOrder = outputAxisOrder,
            OrderBy = orderBy
        };
    }

    private async ValueTask<FeatureQuery> BuildValueQueryAsync(
        LayerDefinition layer,
        ValueReferenceResolution valueReference,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        bool enforceResourceIdTypeMatch,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string>? outFields = valueReference.IsGeometry || valueReference.IsFeatureId
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(valueReference.CanonicalName);

        var (normalizedFilter, normalizedResourceId) = NormalizeFilterInputs(filter, resourceId);
        var sqlFilter = TranslateFesFilter(layer, normalizedFilter);
        var spatialFilter = ParseBboxFilter(bbox, layer);
        var resourceIds = ParseResourceIds(normalizedResourceId, layer, enforceResourceIdTypeMatch);
        sqlFilter = resourceIds.MatchesNothing
            ? CombineSqlFilters(sqlFilter, FalseSqlFilter)
            : sqlFilter;
        var outputSrid = await ResolveRequestedOutputSridAsync(layer, srsName, cancellationToken).ConfigureAwait(false);
        var outputAxisOrder = await ResolveOutputAxisOrderAsync(srsName, outputSrid, cancellationToken).ConfigureAwait(false);

        return new FeatureQuery
        {
            SqlFilter = sqlFilter,
            ObjectIds = resourceIds.ObjectIds,
            OutFields = outFields,
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OutputSrid = outputSrid,
            OutputAxisOrder = outputAxisOrder
        };
    }

    private SqlFragment? TranslateFesFilter(LayerDefinition layer, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var expression = Fes20Parser.ParseFilter(filter);
        expression = FilterExpressionHelpers.NormalizeFilterPropertyReferences(expression, layer);

        if (!FilterExpressionHelpers.IsBooleanFilterExpression(expression))
        {
            throw new ArgumentException("FILTER expression must be a boolean predicate.");
        }

        var translation = _filterExpressionService.Translate(expression, layer);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid filter expression.");
        }

        return translation.SqlFilter;
    }

    private static (string? Filter, string? ResourceId) NormalizeFilterInputs(
        string? filter,
        string? resourceId)
    {
        if (!TryExtractStandaloneResourceIds(filter, out var normalizedFilter, out var filterResourceIds))
        {
            return (filter, resourceId);
        }

        var combinedResourceIds = string.IsNullOrWhiteSpace(resourceId)
            ? filterResourceIds
            : string.IsNullOrWhiteSpace(filterResourceIds)
                ? resourceId
                : $"{resourceId},{filterResourceIds}";

        return (normalizedFilter, combinedResourceIds);
    }

    private static bool TryExtractStandaloneResourceIds(
        string? filter,
        out string? normalizedFilter,
        out string? resourceIds)
    {
        normalizedFilter = filter;
        resourceIds = null;

        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(filter, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return false;
        }

        var root = document.Root;
        if (root == null || !string.Equals(root.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filterChildren = root.Elements().ToArray();
        if (filterChildren.Length == 0)
        {
            return false;
        }

        var hasOnlyResourceIdPredicates = filterChildren.All(element =>
            string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase));
        if (!hasOnlyResourceIdPredicates)
        {
            if (root.Descendants().Any(element =>
                    string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase)))
            {
                throw new NotSupportedException("Filters that combine ResourceId with other predicates are not yet supported.");
            }

            return false;
        }

        var values = filterChildren
            .Select(element => element.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "rid", StringComparison.OrdinalIgnoreCase))
                ?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        if (values.Length == 0)
        {
            return false;
        }

        normalizedFilter = null;
        resourceIds = string.Join(',', values);
        return true;
    }

    private static SqlFragment CombineSqlFilters(SqlFragment? left, SqlFragment right)
    {
        if (left is null)
        {
            return right;
        }

        return new SqlFragment(
            $"({left.Sql}) AND ({right.Sql})",
            left.Parameters.Concat(right.Parameters).ToArray());
    }

    private static ImmutableArray<string>? ResolveProjectedFields(LayerDefinition layer, string? propertyName)
    {
        var requestedProperties = ParseQualifiedList(propertyName);
        if (requestedProperties.Length == 0)
        {
            return null;
        }

        var resolved = ImmutableArray.CreateBuilder<string>();
        foreach (var requestedProperty in requestedProperties)
        {
            var fieldName = FilterExpressionHelpers.ResolveFieldName(layer, requestedProperty, allowGeometryAlias: true)
                ?? throw new ArgumentException($"Unknown property '{requestedProperty}' for feature type '{layer.Name}'.");

            if (layer.GeometryField != null &&
                fieldName.Equals(layer.GeometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!resolved.Any(existing => existing.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
            {
                resolved.Add(fieldName);
            }
        }

        return resolved.ToImmutable();
    }

    private static ImmutableArray<OrderByClause>? ParseSortBy(LayerDefinition layer, string? sortBy)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return null;
        }

        var clauses = ImmutableArray.CreateBuilder<OrderByClause>();

        foreach (var rawClause in sortBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = rawClause.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var fieldName = FilterExpressionHelpers.ResolveFieldName(layer, tokens[0], allowGeometryAlias: false)
                ?? throw new ArgumentException($"Unknown sort field '{tokens[0]}' for feature type '{layer.Name}'.");

            var fieldDefinition = layer.Fields.FirstOrDefault(field =>
                field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

            var ascending = true;
            if (tokens.Length > 1)
            {
                ascending = tokens[1].ToUpperInvariant() switch
                {
                    "A" or "ASC" => true,
                    "D" or "DESC" => false,
                    _ => throw new ArgumentException($"Unsupported sort direction '{tokens[1]}' in sortBy parameter.")
                };
            }

            clauses.Add(new OrderByClause(fieldName, ascending, fieldDefinition?.Type));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutable();
    }

    private static SpatialFilter? ParseBboxFilter(string? bbox, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return null;
        }

        var parts = bbox.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not 4 and not 5)
        {
            throw new ArgumentException("BBOX must contain 4 coordinates and an optional CRS identifier.");
        }

        var crsDefinition = parts.Length == 5
            ? SpatialReferenceHelpers.TryParseCrsDefinition(parts[4], out var bboxCrs)
                ? bboxCrs
                : throw new ArgumentException($"Unsupported BBOX CRS '{parts[4]}'.")
            : SpatialReferenceHelpers.TryParseCrsDefinition(
                layer.SpatialReference.ToSrid().ToString(CultureInfo.InvariantCulture),
                out var layerCrs)
                ? layerCrs
                : throw new ArgumentException($"Unsupported layer spatial reference '{layer.SpatialReference.ToSrid()}'.");
        var axisOrder = parts.Length == 5
            ? crsDefinition.AxisOrder
            : AxisOrder.EastNorth;

        if (!RasterParsingHelpers.TryParseBoundingBox(
                bbox,
                axisOrder,
                crsDefinition.IsGeographic,
                out var minX,
                out var minY,
                out var maxX,
                out var maxY))
        {
            throw new ArgumentException("BBOX contains invalid numeric coordinates or is outside supported CRS bounds.");
        }

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(crsDefinition.Srid);
        var polygon = geometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        ]);

        return new SpatialFilter
        {
            Geometry = BboxWkbWriter.Write(polygon),
            Srid = crsDefinition.Srid,
            SpatialRelationship = SpatialRelationship.Intersects
        };
    }

    private static ResourceIdResolution ParseResourceIds(
        string? resourceId,
        LayerDefinition layer,
        bool enforceTypeMatch)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return new ResourceIdResolution(null, false);
        }

        var ids = ImmutableArray.CreateBuilder<long>();
        var sawCandidate = false;
        var localTypeName = BuildTypeLocalName(layer);
        foreach (var rawResourceId in resourceId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            sawCandidate = true;
            var candidate = rawResourceId;
            var prefix = string.Empty;
            var lastDot = candidate.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < candidate.Length - 1)
            {
                prefix = candidate[..lastDot];
                candidate = candidate[(lastDot + 1)..];
            }

            if (prefix.Length > 0 &&
                !FilterExpressionHelpers.NormalizeIdentifier(prefix).Equals(localTypeName, StringComparison.OrdinalIgnoreCase) &&
                !FilterExpressionHelpers.NormalizeIdentifier(prefix).Equals(layer.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (enforceTypeMatch)
                {
                    throw new WfsQueryException(
                        "InvalidParameterValue",
                        $"resourceId '{rawResourceId}' does not match queried feature type '{localTypeName}'.",
                        Wfs20Utilities.ParameterNames.ResourceId);
                }

                continue;
            }

            if (!long.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            ids.Add(parsed);
        }

        if (ids.Count > 0)
        {
            return new ResourceIdResolution(ids.ToImmutable(), false);
        }

        return new ResourceIdResolution(null, sawCandidate);
    }

    private static int? ParseSrid(string? srsName)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return null;
        }

        if (srsName.Contains("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return SpatialReference.WGS84.Wkid;
        }

        if (srsName.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(srsName[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epsg))
        {
            return epsg;
        }

        if (srsName.StartsWith("urn:ogc:def:crs:EPSG::", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(srsName["urn:ogc:def:crs:EPSG::".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var urn))
        {
            return urn;
        }

        var lastSlash = srsName.LastIndexOf('/');
        if (lastSlash >= 0 &&
            int.TryParse(srsName[(lastSlash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uri))
        {
            return uri;
        }

        return null;
    }

    private async ValueTask<int> ResolveRequestedOutputSridAsync(
        LayerDefinition layer,
        string? srsName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return layer.SpatialReference.ToSrid();
        }

        if (srsName.Contains("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return SpatialReference.WGS84.Wkid;
        }

        if (!SpatialReferenceHelpers.TryParseCrsDefinition(srsName, out var parsedDefinition))
        {
            throw new WfsQueryException(
                "InvalidParameterValue",
                $"Unsupported srsName '{srsName}'.",
                Wfs20Utilities.ParameterNames.SrsName);
        }

        var resolvedDefinition = await _crsRegistry.ResolveAsync(srsName, cancellationToken).ConfigureAwait(false);
        if (resolvedDefinition.HasValue)
        {
            return resolvedDefinition.Value.Srid;
        }

        if (parsedDefinition.Srid == SpatialReference.WGS84.Wkid || parsedDefinition.Srid == 3857)
        {
            return parsedDefinition.Srid;
        }

        throw new WfsQueryException(
            "InvalidParameterValue",
            $"Unsupported srsName '{srsName}'.",
            Wfs20Utilities.ParameterNames.SrsName);
    }

    private async ValueTask<AxisOrder> ResolveOutputAxisOrderAsync(
        string? srsName,
        int outputSrid,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(srsName) &&
            srsName.Contains("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return AxisOrder.EastNorth;
        }

        var definition = await _crsRegistry.ResolveBySridAsync(outputSrid, cancellationToken).ConfigureAwait(false);
        if (definition.HasValue)
        {
            return definition.Value.AxisOrder;
        }

        return SpatialReference.Create(outputSrid).IsGeographic
            ? AxisOrder.NorthEast
            : AxisOrder.EastNorth;
    }

    private static ImmutableArray<WfsFeatureTypeDescriptor> ResolveRequestedFeatureTypes(
        ImmutableArray<WfsFeatureTypeDescriptor> publishedTypes,
        string[] requestedTypes)
    {
        if (requestedTypes.Length == 0)
        {
            return publishedTypes;
        }

        var matches = ImmutableArray.CreateBuilder<WfsFeatureTypeDescriptor>();
        var seenLayerIds = new HashSet<int>();

        foreach (var requestedType in requestedTypes)
        {
            foreach (var featureType in publishedTypes)
            {
                if (!MatchesRequestedType(featureType, requestedType) ||
                    !seenLayerIds.Add(featureType.Layer.Id))
                {
                    continue;
                }

                matches.Add(featureType);
            }
        }

        return matches.ToImmutable();
    }

    private static bool MatchesRequestedType(WfsFeatureTypeDescriptor featureType, string requestedType)
    {
        var normalizedRequested = FilterExpressionHelpers.NormalizeIdentifier(requestedType);
        return string.Equals(requestedType, featureType.QualifiedName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.LocalName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.Layer.Name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.Layer.Id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublishedForWfs(HttpContext context, LayerDefinition layer, ServiceDefinition[] services)
    {
        if (!ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.Wfs20))
        {
            return false;
        }

        var relatedServices = services
            .Where(service => service.Layers.Any(candidate => candidate.Id == layer.Id))
            .ToArray();

        if (relatedServices.Length == 0)
        {
            return AccessPolicyHelpers.IsLayerAccessible(context, layer);
        }

        return relatedServices
            .Where(service => ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Wfs20))
            .Any(service => AccessPolicyHelpers.IsLayerAccessible(context, layer, service));
    }

    private static FeatureType BuildFeatureType(WfsFeatureTypeDescriptor featureType)
    {
        var layer = featureType.Layer;
        string[]? otherCrs = layer.SpatialReference.ToSrid() == 3857
            ? null
            : [FormatCrs(3857)];

        return new FeatureType
        {
            Name = featureType.QualifiedName,
            Title = layer.Name,
            Abstract = layer.Description,
            Keywords = BuildKeywords(layer),
            DefaultCRS = FormatCrs(layer.SpatialReference.ToSrid()),
            OtherCRS = otherCrs,
            OutputFormats = new OutputFormats
            {
                Formats = Wfs20Utilities.OutputFormats.All.ToArray()
            },
            WGS84BoundingBox = BuildWgs84BoundingBox(layer)
        };
    }

    private static string[] BuildKeywords(LayerDefinition layer)
    {
        var keywords = new List<string>
        {
            "wfs",
            "feature"
        };

        if (!string.IsNullOrWhiteSpace(layer.Name))
        {
            keywords.AddRange(layer.Name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.ToLowerInvariant()));
        }

        if (layer.HasGeometry)
        {
            keywords.Add(layer.GeometryType.ToString().ToLowerInvariant());
        }

        return keywords
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static WGS84BoundingBox? BuildWgs84BoundingBox(LayerDefinition layer)
    {
        if (layer.Extent == null)
        {
            return null;
        }

        var extent = layer.Extent.Value;
        if (!OgcExtentTransformer.TryTransformToCrs84(extent.MinX, extent.MinY, extent.SpatialReference, out var min) ||
            !OgcExtentTransformer.TryTransformToCrs84(extent.MaxX, extent.MaxY, extent.SpatialReference, out var max))
        {
            return null;
        }

        return new WGS84BoundingBox
        {
            LowerCorner = $"{FormatCoordinate(min.Lon)} {FormatCoordinate(min.Lat)}",
            UpperCorner = $"{FormatCoordinate(max.Lon)} {FormatCoordinate(max.Lat)}"
        };
    }

    private static string FormatCoordinate(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    private static string FormatCrs(int srid) => $"urn:ogc:def:crs:EPSG::{srid}";

    private static OperationsMetadata BuildOperationsMetadata(string wfsUrl)
    {
        return new OperationsMetadata
        {
            Operations =
            [
                CreateOperation(Wfs20Utilities.Operations.GetCapabilities, wfsUrl,
                [
                    CreateParameter("AcceptVersions", Wfs20Utilities.Version),
                    CreateParameter("Sections", "ServiceIdentification", "ServiceProvider", "OperationsMetadata", "FeatureTypeList", "Filter_Capabilities"),
                    CreateParameter("AcceptFormats", "application/xml")
                ]),
                CreateOperation(Wfs20Utilities.Operations.DescribeFeatureType, wfsUrl,
                [
                    CreateParameter("outputFormat", "application/xml"),
                    CreateParameter("typeNames", allowAnyValue: true)
                ]),
                CreateOperation(Wfs20Utilities.Operations.GetFeature, wfsUrl,
                [
                    CreateParameter("outputFormat", Wfs20Utilities.OutputFormats.All.ToArray()),
                    CreateParameter("typeNames", allowAnyValue: true),
                    CreateParameter("count", allowAnyValue: true),
                    CreateParameter("startIndex", allowAnyValue: true),
                    CreateParameter("sortBy", allowAnyValue: true),
                    CreateParameter("filter", allowAnyValue: true),
                    CreateParameter("bbox", allowAnyValue: true),
                    CreateParameter("resourceId", allowAnyValue: true),
                    CreateParameter("propertyName", allowAnyValue: true),
                    CreateParameter("srsName", allowAnyValue: true),
                    CreateParameter("resolve", "none", "local", "remote", "all")
                ]),
                CreateOperation(Wfs20Utilities.Operations.GetPropertyValue, wfsUrl,
                [
                    CreateParameter(
                        "outputFormat",
                        Wfs20Utilities.OutputFormats.Gml32,
                        Wfs20Utilities.OutputFormats.GeoJson,
                        Wfs20Utilities.OutputFormats.Json),
                    CreateParameter("typeNames", allowAnyValue: true),
                    CreateParameter("valueReference", allowAnyValue: true),
                    CreateParameter("count", allowAnyValue: true),
                    CreateParameter("startIndex", allowAnyValue: true),
                    CreateParameter("filter", allowAnyValue: true),
                    CreateParameter("bbox", allowAnyValue: true),
                    CreateParameter("resourceId", allowAnyValue: true),
                    CreateParameter("srsName", allowAnyValue: true),
                    CreateParameter("resolve", "none", "local", "remote", "all")
                ]),
                CreateOperation(Wfs20Utilities.Operations.Transaction, wfsUrl,
                [
                    CreateParameter("typeName", allowAnyValue: true),
                    CreateParameter("typeNames", allowAnyValue: true)
                ]),
                CreateOperation(Wfs20Utilities.Operations.ListStoredQueries, wfsUrl),
                CreateOperation(Wfs20Utilities.Operations.DescribeStoredQueries, wfsUrl,
                [
                    CreateParameter("storedquery_id", allowAnyValue: true)
                ])
            ],
            Parameters =
            [
                CreateParameter("version", Wfs20Utilities.Version),
                CreateParameter("service", Wfs20Utilities.ServiceType)
            ],
            Constraints =
            [
                CreateBooleanConstraint("ImplementsBasicWFS", true),
                CreateBooleanConstraint("ImplementsTransactionalWFS", true),
                CreateBooleanConstraint("ImplementsLockingWFS", false),
                CreateBooleanConstraint("ImplementsInheritance", false),
                CreateBooleanConstraint("ImplementsRemoteResolve", false),
                CreateBooleanConstraint("ImplementsResultPaging", true),
                CreateBooleanConstraint("ImplementsStandardJoins", false),
                CreateBooleanConstraint("ImplementsSpatialJoins", false),
                CreateBooleanConstraint("ImplementsTemporalJoins", false),
                CreateBooleanConstraint("ImplementsFeatureVersioning", false),
                CreateBooleanConstraint("ManageStoredQueries", false),
                CreateBooleanConstraint("KVPEncoding", true),
                CreateBooleanConstraint("XMLEncoding", true),
                CreateBooleanConstraint("SOAPEncoding", false),
                CreateOpenConstraint("DefaultMaxFeatures", Wfs20Utilities.DefaultMaxFeatures.ToString(CultureInfo.InvariantCulture)),
                CreateOpenConstraint("CountDefault", Wfs20Utilities.DefaultMaxFeatures.ToString(CultureInfo.InvariantCulture))
            ]
        };
    }

    private static Operation CreateOperation(string name, string url, Parameter[]? parameters = null)
    {
        return new Operation
        {
            Name = name,
            DCP =
            [
                new DCP
                {
                    Http = new Http
                    {
                        Get = [new Models.HttpMethod { Href = url }],
                        Post = [new Models.HttpMethod { Href = url }]
                    }
                }
            ],
            Parameters = parameters
        };
    }

    private static Parameter CreateParameter(string name, params string[] allowedValues)
    {
        return new Parameter
        {
            Name = name,
            AllowedValues = allowedValues.Length > 0 ? new AllowedValues { Values = allowedValues } : null,
            AnyValue = allowedValues.Length == 0 ? new object() : null
        };
    }

    private static Parameter CreateParameter(string name, bool allowAnyValue)
    {
        return new Parameter
        {
            Name = name,
            AnyValue = allowAnyValue ? new object() : null
        };
    }

    private static Constraint CreateBooleanConstraint(string name, bool defaultValue)
    {
        return new Constraint
        {
            Name = name,
            AllowedValues = new AllowedValues { Values = ["TRUE", "FALSE"] },
            DefaultValue = defaultValue ? "TRUE" : "FALSE"
        };
    }

    private static Constraint CreateOpenConstraint(string name, string defaultValue)
    {
        return new Constraint
        {
            Name = name,
            AnyValue = new object(),
            DefaultValue = defaultValue
        };
    }

    private static FilterCapabilities BuildFilterCapabilities()
    {
        return new FilterCapabilities
        {
            Conformance = new FesConformance
            {
                Constraints =
                [
                    CreateBooleanFesConstraint("ImplementsQuery", true),
                    CreateBooleanFesConstraint("ImplementsAdHocQuery", true),
                    CreateBooleanFesConstraint("ImplementsResourceId", true),
                    CreateBooleanFesConstraint("ImplementsMinStandardFilter", true),
                    CreateBooleanFesConstraint("ImplementsStandardFilter", true),
                    CreateBooleanFesConstraint("ImplementsMinimumXPath", true),
                    CreateBooleanFesConstraint("ImplementsMinSpatialFilter", true),
                    CreateBooleanFesConstraint("ImplementsSpatialFilter", true),
                    CreateBooleanFesConstraint("ImplementsMinTemporalFilter", true),
                    CreateBooleanFesConstraint("ImplementsTemporalFilter", true),
                    CreateBooleanFesConstraint("ImplementsVersionNav", false),
                    CreateBooleanFesConstraint("ImplementsSorting", true),
                    CreateBooleanFesConstraint("ImplementsExtendedOperators", false)
                ]
            },
            IdCapabilities = new IdCapabilities
            {
                ResourceIdentifiers =
                [
                    new ResourceIdentifier { Name = "id" },
                    new ResourceIdentifier { Name = "objectid" },
                    new ResourceIdentifier { Name = "fid" }
                ]
            },
            ScalarCapabilities = new ScalarCapabilities
            {
                LogicalOperators = new object(),
                ComparisonOperators = new ComparisonOperators
                {
                    Operators =
                    [
                        new ComparisonOperator { Name = "PropertyIsEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsNotEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsLessThan" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThan" },
                        new ComparisonOperator { Name = "PropertyIsLessThanOrEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThanOrEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsLike" },
                        new ComparisonOperator { Name = "PropertyIsNil" },
                        new ComparisonOperator { Name = "PropertyIsNull" },
                        new ComparisonOperator { Name = "PropertyIsBetween" }
                    ]
                }
            },
            SpatialCapabilities = new SpatialCapabilities
            {
                GeometryOperands = new GeometryOperands
                {
                    Operands =
                    [
                        new GeometryOperand { Name = new XmlQualifiedName("Envelope", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Point", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("LineString", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Curve", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Polygon", Wfs20Utilities.GmlNamespace) },
                        new GeometryOperand { Name = new XmlQualifiedName("Surface", Wfs20Utilities.GmlNamespace) }
                    ]
                },
                SpatialOperators = new SpatialOperators
                {
                    Operators =
                    [
                        new Models.SpatialOperator { Name = "BBOX" },
                        new Models.SpatialOperator { Name = "Intersects" },
                        new Models.SpatialOperator { Name = "Contains" },
                        new Models.SpatialOperator { Name = "Within" },
                        new Models.SpatialOperator { Name = "Crosses" },
                        new Models.SpatialOperator { Name = "Touches" },
                        new Models.SpatialOperator { Name = "Overlaps" },
                        new Models.SpatialOperator { Name = "Disjoint" },
                        new Models.SpatialOperator { Name = "Equals" },
                        new Models.SpatialOperator { Name = "DWithin" },
                        new Models.SpatialOperator { Name = "Beyond" }
                    ]
                }
            },
            TemporalCapabilities = new TemporalCapabilities
            {
                TemporalOperands = new TemporalOperands
                {
                    Operands =
                    [
                        new TemporalOperand { Name = new XmlQualifiedName("TimeInstant", Wfs20Utilities.GmlNamespace) },
                        new TemporalOperand { Name = new XmlQualifiedName("TimePeriod", Wfs20Utilities.GmlNamespace) }
                    ]
                },
                TemporalOperators = new TemporalOperators
                {
                    Operators =
                    [
                        new Models.TemporalOperator { Name = "After" },
                        new Models.TemporalOperator { Name = "Before" },
                        new Models.TemporalOperator { Name = "During" }
                    ]
                }
            }
        };
    }

    private static FesConstraint CreateBooleanFesConstraint(string name, bool defaultValue)
    {
        return new FesConstraint
        {
            Name = name,
            AllowedValues = new AllowedValues { Values = ["TRUE", "FALSE"] },
            DefaultValue = defaultValue ? "TRUE" : "FALSE"
        };
    }

    private static bool ShouldIncludeCapabilitiesSection(
        IReadOnlySet<string>? requestedSections,
        string sectionName)
    {
        return requestedSections is null ||
               requestedSections.Count == 0 ||
               requestedSections.Contains(sectionName);
    }

    private static FeatureCollectionResponseMetadata BuildFeatureCollectionResponseMetadata(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string? outputFormat,
        string? count,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        int offset,
        int pageSize,
        long totalMatched,
        int returnedCount)
    {
        var pagingResultType = string.Equals(resultType, "hits", StringComparison.OrdinalIgnoreCase)
            ? null
            : resultType;

        return new FeatureCollectionResponseMetadata(
            BuildFeatureCollectionSchemaLocation(wfsUrl, selectedTypes),
            BuildPagingLinks(
                wfsUrl,
                selectedTypes,
                outputFormat,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                pagingResultType,
                offset,
                pageSize,
                totalMatched,
                returnedCount));
    }

    private static string BuildFeatureCollectionSchemaLocation(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes)
    {
        var values = new List<string>
        {
            Wfs20Utilities.WfsNamespace,
            "http://schemas.opengis.net/wfs/2.0/wfs.xsd"
        };

        if (selectedTypes.Count > 0)
        {
            var typeNames = string.Join(
                ",",
                selectedTypes.Select(descriptor => descriptor.LocalName));
            values.Add(FeatureNamespaceUri);
            values.Add(
                $"{wfsUrl}?SERVICE=WFS&VERSION={Wfs20Utilities.Version}&REQUEST=DescribeFeatureType&TYPENAMES={Uri.EscapeDataString(typeNames)}");
        }

        return string.Join(" ", values);
    }

    private static PagingLinks BuildPagingLinks(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string? outputFormat,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        int offset,
        int pageSize,
        long totalMatched,
        int returnedCount)
    {
        if (pageSize <= 0 || selectedTypes.Count == 0)
        {
            return new PagingLinks(null, null);
        }

        var next = offset + pageSize < totalMatched
            ? BuildGetFeaturePagingLink(
                wfsUrl,
                selectedTypes,
                outputFormat,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                resultType,
                offset + pageSize,
                pageSize)
            : null;
        var previous = offset > 0
            ? BuildGetFeaturePagingLink(
                wfsUrl,
                selectedTypes,
                outputFormat,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                resultType,
                Math.Max(offset - pageSize, 0),
                pageSize)
            : null;

        return new PagingLinks(next, previous);
    }

    private static string BuildGetFeaturePagingLink(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string? outputFormat,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        int offset,
        int count)
    {
        var queryParts = new List<string>
        {
            $"SERVICE={Uri.EscapeDataString(Wfs20Utilities.ServiceType)}",
            $"VERSION={Uri.EscapeDataString(Wfs20Utilities.Version)}",
            $"REQUEST={Uri.EscapeDataString(Wfs20Utilities.Operations.GetFeature)}",
            $"TYPENAMES={Uri.EscapeDataString(string.Join(',', selectedTypes.Select(descriptor => descriptor.LocalName)))}",
            $"COUNT={count.ToString(CultureInfo.InvariantCulture)}",
            $"STARTINDEX={offset.ToString(CultureInfo.InvariantCulture)}"
        };

        AppendQueryPart(queryParts, "OUTPUTFORMAT", outputFormat);
        AppendQueryPart(queryParts, "SORTBY", sortBy);
        AppendQueryPart(queryParts, "BBOX", bbox);
        AppendQueryPart(queryParts, "FILTER", filter);
        AppendQueryPart(queryParts, "RESOURCEID", resourceId);
        AppendQueryPart(queryParts, "PROPERTYNAME", propertyName);
        AppendQueryPart(queryParts, "SRSNAME", srsName);
        if (!string.IsNullOrWhiteSpace(resultType) &&
            !string.Equals(resultType, "results", StringComparison.OrdinalIgnoreCase))
        {
            AppendQueryPart(queryParts, "RESULTTYPE", resultType);
        }

        return $"{wfsUrl}?{string.Join("&", queryParts)}";
    }

    private static void AppendQueryPart(List<string> queryParts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            queryParts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static string GenerateSchemaForTypes(IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes)
    {
        if (featureTypes.Count == 0)
        {
            return GenerateEmptySchema();
        }

        var schema = new StringBuilder();
        schema.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        schema.AppendLine($"""<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:gml="{Wfs20Utilities.GmlNamespace}" xmlns:{FeatureNamespacePrefix}="{FeatureNamespaceUri}" targetNamespace="{FeatureNamespaceUri}" elementFormDefault="qualified" version="1.0.0">""");
        schema.AppendLine($"""  <xsd:import namespace="{Wfs20Utilities.GmlNamespace}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>""");
        schema.AppendLine();

        foreach (var featureType in featureTypes)
        {
            var typeName = XmlConvert.EncodeLocalName(featureType.LocalName);
            schema.AppendLine($"""  <xsd:element name="{typeName}" type="{FeatureNamespacePrefix}:{typeName}Type" substitutionGroup="gml:AbstractFeature"/>""");
            schema.AppendLine($"""  <xsd:complexType name="{typeName}Type">""");
            schema.AppendLine("""    <xsd:complexContent>""");
            schema.AppendLine("""      <xsd:extension base="gml:AbstractFeatureType">""");
            schema.AppendLine("""        <xsd:sequence>""");

            if (featureType.Layer.GeometryField is not null)
            {
                var geometryFieldName = XmlConvert.EncodeLocalName(featureType.Layer.GeometryField.Name);
                schema.AppendLine($"""          <xsd:element name="{geometryFieldName}" type="{MapGeometryPropertyType(featureType.Layer.GeometryType)}" minOccurs="0" nillable="true"/>""");
            }

            foreach (var field in featureType.Layer.AttributeFields)
            {
                var fieldName = XmlConvert.EncodeLocalName(field.Name);
                var minOccurs = field.Nullable ? " minOccurs=\"0\"" : string.Empty;
                var nillable = field.Nullable ? " nillable=\"true\"" : string.Empty;
                schema.AppendLine($"""          <xsd:element name="{fieldName}" type="{MapXsdType(field.Type)}"{minOccurs}{nillable}/>""");
            }

            schema.AppendLine("""        </xsd:sequence>""");
            schema.AppendLine("""      </xsd:extension>""");
            schema.AppendLine("""    </xsd:complexContent>""");
            schema.AppendLine("""  </xsd:complexType>""");
            schema.AppendLine();
        }

        schema.AppendLine("""</xsd:schema>""");
        return schema.ToString();
    }

    private static string GenerateEmptySchema()
    {
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xsd:schema
                xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                xmlns:gml="{{Wfs20Utilities.GmlNamespace}}"
                xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}"
                targetNamespace="{{FeatureNamespaceUri}}"
                elementFormDefault="qualified"
                version="1.0.0">
              <xsd:import namespace="{{Wfs20Utilities.GmlNamespace}}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>
            </xsd:schema>
            """;
    }

    private static string GenerateEmptySchemaForTypes(IReadOnlyList<string> requestedTypes)
    {
        var requested = string.Join(", ", requestedTypes);
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xsd:schema
                xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                xmlns:gml="{{Wfs20Utilities.GmlNamespace}}"
                xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}"
                targetNamespace="{{FeatureNamespaceUri}}"
                elementFormDefault="qualified"
                version="1.0.0">
              <xsd:import namespace="{{Wfs20Utilities.GmlNamespace}}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>
              <xsd:annotation>
                <xsd:documentation>Requested feature types were not found: {{XmlEscape(requested)}}</xsd:documentation>
              </xsd:annotation>
            </xsd:schema>
            """;
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildGmlResultAsync(
        LayerQueryPlanSet planSet,
        string? schemaLocation,
        string? next,
        string? previous,
        CancellationToken cancellationToken)
    {
        var queryResults = new List<(LayerQueryPlan Plan, ImmutableArray<GmlFeature> Features)>(planSet.Plans.Length);

        foreach (var plan in planSet.Plans)
        {
            var result = await _gmlFeatureStore.QueryGmlAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            queryResults.Add((plan, result.Items));
        }

        var returnedCount = queryResults.Sum(entry => entry.Features.Length);
        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "FeatureCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", planSet.TotalMatched.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberReturned", returnedCount.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(schemaLocation))
            {
                writer.WriteAttributeString("xsi", "schemaLocation", Wfs20Utilities.XsiNamespace, schemaLocation);
            }

            WritePagingAttributes(writer, next, previous);

            foreach (var queryResult in queryResults)
            {
                foreach (var feature in queryResult.Features)
                {
                    WriteFeature(writer, queryResult.Plan, feature, includeMemberWrapper: true);
                }
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return (Results.Content(xml, MediaTypes.Gml, Encoding.UTF8), returnedCount);
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildJsonResultAsync(
        LayerQueryPlanSet planSet,
        string normalizedFormat,
        CancellationToken cancellationToken)
    {
        var features = new List<GeoJsonFeature>();
        var geoJsonFeatureStore = _featureReader as IGeoJsonFeatureStore;

        foreach (var plan in planSet.Plans)
        {
            var axisOrder = plan.Query.OutputAxisOrder ?? AxisOrder.EastNorth;
            var projectedProperties = GetProjectedProperties(plan.Query);

            if (geoJsonFeatureStore is not null)
            {
                var result = await geoJsonFeatureStore.QueryGeoJsonAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
                foreach (var feature in result.Items)
                {
                    features.Add(OgcGeoJsonFeatureBuilder.Create(
                        feature,
                        plan.Descriptor.Layer,
                        axisOrder,
                        _geometryServices,
                        projectedProperties,
                        featureId => BuildFeatureId(plan.Descriptor, featureId)));
                }

                continue;
            }

            var fallbackResult = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            foreach (var feature in fallbackResult.Items)
            {
                features.Add(OgcGeoJsonFeatureBuilder.Create(
                    feature,
                    plan.Descriptor.Layer,
                    axisOrder,
                    _geometryServices,
                    projectedProperties,
                    featureId => BuildFeatureId(plan.Descriptor, featureId)));
            }
        }

        var payload = OgcGeoJsonFeatureBuilder.CreateCollection(features, planSet.TotalMatched);

        var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.Json
            : MediaTypes.GeoJson;

        return (Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType), features.Count);
    }

    private async Task<PagedGetFeatureResult> BuildPagedGetFeatureResultAsync(
        WfsFeatureTypeDescriptor descriptor,
        FeatureQuery query,
        string normalizedFormat,
        CancellationToken cancellationToken)
    {
        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase))
        {
            if (_featureReader is not IPagedFeatureReader pagedFeatureReader)
            {
                throw new InvalidOperationException("Paged feature queries are not supported by the configured feature store.");
            }

            var result = await pagedFeatureReader.QueryPageAsync(descriptor.Layer.Id, query, cancellationToken);
            var rows = new List<Dictionary<string, string?>>();
            var attributeHeaders = GetProjectedAttributeFields(descriptor.Layer, query)
                .Select(field => field.Name)
                .ToArray();
            var axisOrder = query.OutputAxisOrder ?? AxisOrder.EastNorth;

            foreach (var feature in result.Items)
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["typeName"] = descriptor.QualifiedName,
                    ["id"] = BuildFeatureId(descriptor, feature.Id)
                };

                foreach (var field in GetProjectedAttributeFields(descriptor.Layer, query))
                {
                    row[field.Name] = feature.Attributes.TryGetValue(field.Name, out var value)
                        ? ConvertToInvariantString(value)
                        : null;
                }

                if (descriptor.Layer.HasGeometry)
                {
                    row["geometry"] = SerializeGeometryAsJson(feature.Geometry, axisOrder);
                }

                rows.Add(row);
            }

            var headers = new List<string> { "typeName", "id" };
            headers.AddRange(attributeHeaders);
            if (descriptor.Layer.HasGeometry)
            {
                headers.Add("geometry");
            }

            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
            foreach (var row in rows)
            {
                csv.AppendLine(string.Join(",", headers.Select(header =>
                    EscapeCsv(row.TryGetValue(header, out var value) ? value : null))));
            }

            return new PagedGetFeatureResult(
                Results.Content(csv.ToString(), MediaTypes.Csv, Encoding.UTF8),
                rows.Count,
                FormatNumberMatched(result.TotalCount));
        }

        var features = new List<GeoJsonFeature>();
        long? totalCount = null;

        if (_featureReader is IPagedGeoJsonFeatureStore pagedGeoJsonFeatureStore)
        {
            var result = await pagedGeoJsonFeatureStore.QueryGeoJsonPageAsync(descriptor.Layer.Id, query, cancellationToken);
            totalCount = result.TotalCount;
            var projectedProperties = GetProjectedProperties(query);
            foreach (var feature in result.Items)
            {
                features.Add(OgcGeoJsonFeatureBuilder.Create(
                    feature,
                    descriptor.Layer,
                    AxisOrder.EastNorth,
                    _geometryServices,
                    projectedProperties,
                    featureId => BuildFeatureId(descriptor, featureId)));
            }
        }
        else if (_featureReader is IPagedFeatureReader pagedFeatureReader)
        {
            var result = await pagedFeatureReader.QueryPageAsync(descriptor.Layer.Id, query, cancellationToken);
            totalCount = result.TotalCount;
            var projectedProperties = GetProjectedProperties(query);
            foreach (var feature in result.Items)
            {
                features.Add(OgcGeoJsonFeatureBuilder.Create(
                    feature,
                    descriptor.Layer,
                    AxisOrder.EastNorth,
                    _geometryServices,
                    projectedProperties,
                    featureId => BuildFeatureId(descriptor, featureId)));
            }
        }
        else
        {
            throw new InvalidOperationException("Paged feature queries are not supported by the configured feature store.");
        }

        var payload = OgcGeoJsonFeatureBuilder.CreateCollection(features, totalCount);

        var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.Json
            : MediaTypes.GeoJson;

        return new PagedGetFeatureResult(
            Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType),
            features.Count,
            FormatNumberMatched(totalCount));
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildCsvResultAsync(
        LayerQueryPlanSet planSet,
        CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, string?>>();
        var attributeHeaders = new List<string>();
        var geometryRequested = false;

        foreach (var plan in planSet.Plans)
        {
            geometryRequested |= plan.Descriptor.Layer.HasGeometry;

            foreach (var field in GetProjectedAttributeFields(plan.Descriptor.Layer, plan.Query))
            {
                if (!attributeHeaders.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                {
                    attributeHeaders.Add(field.Name);
                }
            }

            var axisOrder = plan.Query.OutputAxisOrder ?? AxisOrder.EastNorth;
            var result = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            foreach (var feature in result.Items)
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["typeName"] = plan.Descriptor.QualifiedName,
                    ["id"] = BuildFeatureId(plan.Descriptor, feature.Id)
                };

                foreach (var field in GetProjectedAttributeFields(plan.Descriptor.Layer, plan.Query))
                {
                    row[field.Name] = feature.Attributes.TryGetValue(field.Name, out var value)
                        ? ConvertToInvariantString(value)
                        : null;
                }

                if (geometryRequested)
                {
                    row["geometry"] = SerializeGeometryAsJson(feature.Geometry, axisOrder);
                }

                rows.Add(row);
            }
        }

        var headers = new List<string> { "typeName", "id" };
        headers.AddRange(attributeHeaders);
        if (geometryRequested)
        {
            headers.Add("geometry");
        }

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            csv.AppendLine(string.Join(",", headers.Select(header =>
                EscapeCsv(row.TryGetValue(header, out var value) ? value : null))));
        }

        return (Results.Content(csv.ToString(), MediaTypes.Csv, Encoding.UTF8), rows.Count);
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
                var result = await _gmlFeatureStore.QueryGmlAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
                queryResults.Add((plan, result.Items, null));
                returnedCount += result.Items.Length;
                continue;
            }

            var features = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
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
                        writer.WriteString(ConvertToInvariantString(value));
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
            var featureResult = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
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

    private static void WriteFeature(XmlWriter writer, LayerQueryPlan plan, GmlFeature feature, bool includeMemberWrapper)
    {
        if (includeMemberWrapper)
        {
            writer.WriteStartElement("wfs", "member", Wfs20Utilities.WfsNamespace);
        }

        writer.WriteStartElement(FeatureNamespacePrefix, plan.Descriptor.LocalName, FeatureNamespaceUri);
        if (!includeMemberWrapper)
        {
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
        }

        writer.WriteAttributeString("gml", "id", Wfs20Utilities.GmlNamespace, BuildFeatureId(plan.Descriptor, feature.Id));

        // Keep collection members aligned to the application schema; duplicating gml:name/description
        // alongside matching feature properties causes GDAL/OGR to surface list-valued fields.
        if (!includeMemberWrapper &&
            TryGetGmlDescriptionValue(plan.Descriptor.Layer, feature.Attributes, out var gmlDescription))
        {
            writer.WriteElementString("gml", "description", Wfs20Utilities.GmlNamespace, gmlDescription);
        }

        if (TryGetGmlIdentifierValue(feature.Attributes, out var gmlIdentifier))
        {
            writer.WriteElementString("gml", "identifier", Wfs20Utilities.GmlNamespace, gmlIdentifier);
        }

        if (!includeMemberWrapper &&
            TryGetGmlNameValue(plan.Descriptor.Layer, feature.Attributes, out var gmlName))
        {
            writer.WriteElementString("gml", "name", Wfs20Utilities.GmlNamespace, gmlName);
        }

        if (plan.Descriptor.Layer.GeometryField is not null &&
            !string.IsNullOrWhiteSpace(feature.GeometryGml))
        {
            writer.WriteStartElement(
                FeatureNamespacePrefix,
                XmlConvert.EncodeLocalName(plan.Descriptor.Layer.GeometryField.Name),
                FeatureNamespaceUri);
            writer.WriteRaw(feature.GeometryGml);
            writer.WriteEndElement();
        }

        foreach (var field in GetProjectedAttributeFields(plan.Descriptor.Layer, plan.Query))
        {
            if (!feature.Attributes.TryGetValue(field.Name, out var value) || value is null)
            {
                if (field.Nullable)
                {
                    writer.WriteStartElement(
                        FeatureNamespacePrefix,
                        XmlConvert.EncodeLocalName(field.Name),
                        FeatureNamespaceUri);
                    writer.WriteAttributeString("xsi", "nil", Wfs20Utilities.XsiNamespace, XmlConvert.ToString(true));
                    writer.WriteEndElement();
                }

                continue;
            }

            writer.WriteStartElement(
                FeatureNamespacePrefix,
                XmlConvert.EncodeLocalName(field.Name),
                FeatureNamespaceUri);
            writer.WriteString(ConvertToInvariantString(value));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        if (includeMemberWrapper)
        {
            writer.WriteEndElement();
        }
    }

    private static bool TryGetGmlNameValue(
        LayerDefinition layer,
        ImmutableDictionary<string, object?> attributes,
        out string? gmlName)
    {
        return TryGetTransactionMappedValue(
            layer,
            attributes,
            "name",
            ValidationExtensions.WfsGmlNameAttributeName,
            out gmlName);
    }

    private static bool TryGetGmlDescriptionValue(
        LayerDefinition layer,
        ImmutableDictionary<string, object?> attributes,
        out string? gmlDescription)
    {
        return TryGetTransactionMappedValue(
            layer,
            attributes,
            "description",
            ValidationExtensions.WfsGmlDescriptionAttributeName,
            out gmlDescription);
    }

    private static bool TryGetGmlIdentifierValue(
        ImmutableDictionary<string, object?> attributes,
        out string? gmlIdentifier)
    {
        return TryGetReservedTransactionValue(
            attributes,
            ValidationExtensions.WfsGmlIdentifierAttributeName,
            out gmlIdentifier);
    }

    private static bool TryGetTransactionMappedValue(
        LayerDefinition layer,
        ImmutableDictionary<string, object?> attributes,
        string fieldName,
        string reservedAttributeName,
        out string? valueText)
    {
        var field = layer.AttributeFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field != null &&
            attributes.TryGetValue(field.Name, out var value) &&
            value is not null)
        {
            valueText = ConvertToInvariantString(value);
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                return true;
            }
        }

        return TryGetReservedTransactionValue(attributes, reservedAttributeName, out valueText);
    }

    private static bool TryGetReservedTransactionValue(
        ImmutableDictionary<string, object?> attributes,
        string reservedAttributeName,
        out string? valueText)
    {
        if (attributes.TryGetValue(reservedAttributeName, out var value) &&
            value is not null)
        {
            valueText = ConvertToInvariantString(value);
            return !string.IsNullOrWhiteSpace(valueText);
        }

        valueText = null;
        return false;
    }

    private static string BuildFeatureId(WfsFeatureTypeDescriptor descriptor, long featureId)
        => $"{descriptor.LocalName}.{featureId.ToString(CultureInfo.InvariantCulture)}";

    private static ImmutableHashSet<string>? GetProjectedProperties(FeatureQuery query)
    {
        if (query.OutFields is not { } outFields)
        {
            return null;
        }

        return outFields.IsDefaultOrEmpty
            ? ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase)
            : outFields.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? SerializeGeometryAsJson(byte[]? geometry, AxisOrder axisOrder)
    {
        var simpleGeometry = _geometryServices.ConvertWkbToSimpleGeometry(geometry, axisOrder);
        return simpleGeometry is null
            ? null
            : JsonSerializer.Serialize(simpleGeometry, OgcJsonContext.Default.SimpleGeoJsonGeometry);
    }

    private static IEnumerable<FieldDefinition> GetProjectedAttributeFields(LayerDefinition layer, FeatureQuery query)
    {
        if (query.OutFields is not { } outFields)
        {
            return layer.AttributeFields;
        }

        if (outFields.IsDefaultOrEmpty)
        {
            return Array.Empty<FieldDefinition>();
        }

        return layer.AttributeFields.Where(field =>
            outFields.Any(candidate => candidate.Equals(field.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static ValueReferenceResolution ResolveValueReference(LayerDefinition layer, string valueReference)
    {
        var resolvedName = FilterExpressionHelpers.ResolveFieldName(layer, valueReference, allowGeometryAlias: true)
            ?? throw new ArgumentException($"Unknown valueReference '{valueReference}' for feature type '{layer.Name}'.");

        var isGeometry = layer.GeometryField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true;
        var isFeatureId = layer.PrimaryKeyField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true ||
                          resolvedName.Equals("objectid", StringComparison.OrdinalIgnoreCase);

        return new ValueReferenceResolution(valueReference, resolvedName, isGeometry, isFeatureId);
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

    private static bool IsSupportedFeatureOutputFormat(string format)
    {
        return string.Equals(format, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Gml32Simplified, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedValueOutputFormat(string format)
    {
        return string.Equals(format, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Gml32Simplified, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult CreateEmptyFeatureCollectionResult(
        string normalizedFormat,
        string? schemaLocation = null,
        string? next = null,
        string? previous = null)
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

        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Content("typeName,id\n", MediaTypes.Csv, Encoding.UTF8);
        }

        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "FeatureCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", "0");
            writer.WriteAttributeString("numberReturned", "0");
            if (!string.IsNullOrWhiteSpace(schemaLocation))
            {
                writer.WriteAttributeString("xsi", "schemaLocation", Wfs20Utilities.XsiNamespace, schemaLocation);
            }

            WritePagingAttributes(writer, next, previous);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private static IResult CreateHitsFeatureCollectionResult(
        long totalMatched,
        string? schemaLocation = null,
        string? next = null,
        string? previous = null)
    {
        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "FeatureCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", totalMatched.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberReturned", "0");
            if (!string.IsNullOrWhiteSpace(schemaLocation))
            {
                writer.WriteAttributeString("xsi", "schemaLocation", Wfs20Utilities.XsiNamespace, schemaLocation);
            }

            WritePagingAttributes(writer, next, previous);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
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

    private static string WriteXmlDocument(Action<XmlWriter> writeAction)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false
        };

        using var stream = new MemoryStream();
        using var writer = XmlWriter.Create(stream, settings);
        writeAction(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePagingAttributes(XmlWriter writer, string? next, string? previous)
    {
        if (!string.IsNullOrWhiteSpace(next))
        {
            writer.WriteAttributeString("next", next);
        }

        if (!string.IsNullOrWhiteSpace(previous))
        {
            writer.WriteAttributeString("previous", previous);
        }
    }

    private static string BuildTypeLocalName(LayerDefinition layer)
    {
        var input = string.IsNullOrWhiteSpace(layer.Name)
            ? $"layer_{layer.Id}"
            : layer.Name;

        var builder = new StringBuilder(input.Length);
        var lastWasUnderscore = false;

        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasUnderscore = false;
                continue;
            }

            if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }

        var result = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = $"layer_{layer.Id}";
        }

        if (char.IsDigit(result[0]))
        {
            result = $"layer_{result}";
        }

        return result;
    }

    private static string[] ParseQualifiedList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string MapGeometryPropertyType(Honua.Core.Features.Catalog.Domain.GeometryType geometryType)
    {
        return geometryType switch
        {
            Honua.Core.Features.Catalog.Domain.GeometryType.Point => "gml:PointPropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPoint => "gml:MultiPointPropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.LineString => "gml:CurvePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiLineString => "gml:MultiCurvePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.Polygon => "gml:SurfacePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPolygon => "gml:MultiSurfacePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.GeometryCollection => "gml:GeometryPropertyType",
            _ => "gml:GeometryPropertyType"
        };
    }

    private static string MapXsdType(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.Integer => "xsd:int",
            FieldType.BigInteger => "xsd:long",
            FieldType.Double => "xsd:double",
            FieldType.Float => "xsd:float",
            FieldType.Boolean => "xsd:boolean",
            FieldType.DateTime => "xsd:dateTime",
            FieldType.Date => "xsd:date",
            FieldType.Time => "xsd:time",
            FieldType.Binary => "xsd:base64Binary",
            FieldType.Json => "xsd:anyType",
            _ => "xsd:string"
        };
    }

    private static string ConvertToInvariantString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            bool boolean => XmlConvert.ToString(boolean),
            DateTimeOffset dateTimeOffset => FormatXmlDateTimeOffset(dateTimeOffset),
            DateTime dateTime => FormatXmlDateTime(dateTime),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => ConvertStructuredValueToJson(value)
        };
    }

    private static string FormatXmlDateTime(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return FormatXmlDateTimeOffset(new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero));
    }

    private static string FormatXmlDateTimeOffset(DateTimeOffset value)
    {
        var normalized = value.ToUniversalTime();
        var format = normalized.Millisecond == 0
            ? "yyyy-MM-dd'T'HH:mm:sszzz"
            : "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
        return normalized.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string ConvertStructuredValueToJson(object value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteStructuredJsonValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStructuredJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                return;
            case sbyte signedByteValue:
                writer.WriteNumberValue(signedByteValue);
                return;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                return;
            case ushort ushortValue:
                writer.WriteNumberValue(ushortValue);
                return;
            case int intValue:
                writer.WriteNumberValue(intValue);
                return;
            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                return;
            case long longValue:
                writer.WriteNumberValue(longValue);
                return;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                return;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                return;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                return;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                return;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                return;
            case DateOnly dateOnly:
                writer.WriteStringValue(dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return;
            case TimeOnly timeOnly:
                writer.WriteStringValue(timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                return;
            case Guid guid:
                writer.WriteStringValue(guid);
                return;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                writer.WriteStartObject();
                foreach (var (key, nestedValue) in pairs)
                {
                    writer.WritePropertyName(key);
                    WriteStructuredJsonValue(writer, nestedValue);
                }
                writer.WriteEndObject();
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    writer.WritePropertyName(key);
                    WriteStructuredJsonValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    WriteStructuredJsonValue(writer, item);
                }
                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    private static string EscapeCsv(string? value)
    {
        var safeValue = value ?? string.Empty;
        if (!safeValue.Contains(',') &&
            !safeValue.Contains('"') &&
            !safeValue.Contains('\n') &&
            !safeValue.Contains('\r'))
        {
            return safeValue;
        }

        return $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string XmlEscape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private bool ShouldUsePagedGetFeatureFastPath(
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string normalizedFormat,
        bool isHitsRequest,
        int maxFeatures)
    {
        if (_wfs20Options.NumberMatchedPolicy != Wfs20NumberMatchedPolicy.UnknownWhenExpensive)
        {
            return false;
        }

        if (isHitsRequest || selectedTypes.Count != 1 || maxFeatures <= 0)
        {
            return false;
        }

        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase))
        {
            return _featureReader is IPagedFeatureReader;
        }

        var isJsonFormat =
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase);

        if (!isJsonFormat)
        {
            return false;
        }

        return _featureReader is IPagedGeoJsonFeatureStore || _featureReader is IPagedFeatureReader;
    }

    private static string FormatNumberMatched(long? totalCount)
        => totalCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private sealed record WfsFeatureTypeDescriptor(
        LayerDefinition Layer,
        string QualifiedName,
        string LocalName);

    private sealed record LayerQueryPlan(
        WfsFeatureTypeDescriptor Descriptor,
        FeatureQuery Query,
        long MatchedCount);

    private sealed record LayerValuePlan(
        WfsFeatureTypeDescriptor Descriptor,
        FeatureQuery Query,
        long MatchedCount,
        ValueReferenceResolution ValueReference);

    private readonly record struct LayerQueryPlanSet(
        ImmutableArray<LayerQueryPlan> Plans,
        long TotalMatched);

    private readonly record struct LayerValuePlanSet(
        ImmutableArray<LayerValuePlan> Plans,
        long TotalMatched);

    private readonly record struct ResourceIdResolution(
        ImmutableArray<long>? ObjectIds,
        bool MatchesNothing);

    private readonly record struct PagingLinks(
        string? Next,
        string? Previous);

    private readonly record struct FeatureCollectionResponseMetadata(
        string SchemaLocation,
        PagingLinks PagingLinks);

    private readonly record struct PagedGetFeatureResult(
        IResult Result,
        int ReturnedCount,
        string NumberMatchedSummary);

    private readonly record struct ValueReferenceResolution(
        string RequestedName,
        string CanonicalName,
        bool IsGeometry,
        bool IsFeatureId);

    private sealed class WfsTransactionException(
        string exceptionCode,
        string message,
        string? locator = null) : Exception(message)
    {
        public string ExceptionCode { get; } = exceptionCode;

        public string? Locator { get; } = locator;
    }

    private sealed class WfsQueryException(
        string exceptionCode,
        string message,
        string? locator = null) : Exception(message)
    {
        public string ExceptionCode { get; } = exceptionCode;

        public string? Locator { get; } = locator;
    }

    private enum TransactionActionKind
    {
        Insert,
        Update,
        Replace,
        Delete
    }

    private sealed record PreparedTransactionOperation(
        WfsFeatureTypeDescriptor Descriptor,
        TransactionActionKind ActionKind,
        FeatureEditOperation EditOperation,
        string? Handle,
        Feature? MutationFeature,
        Feature? DeleteSnapshot);

    private readonly record struct TransactionFieldResolution(
        FieldDefinition Field,
        bool IsGmlProperty);

    private readonly record struct TransactionFeaturePayload(
        byte[]? Geometry,
        ImmutableDictionary<string, object?> Attributes);

    private readonly record struct TransactionFeatureChanges(
        bool GeometrySpecified,
        byte[]? Geometry,
        ImmutableDictionary<string, object?> Attributes);

    private readonly record struct TransactionPreparationResult(
        ImmutableArray<PreparedTransactionOperation> Operations,
        int LayerId,
        int LayerIdCount,
        IResult? ErrorResult,
        int InsertCount,
        int UpdateCount,
        int ReplaceCount,
        int DeleteCount)
    {
        public bool IsValid => ErrorResult is null;

        public static TransactionPreparationResult Success(
            ImmutableArray<PreparedTransactionOperation> operations,
            int layerId,
            int layerIdCount)
        {
            var insertCount = 0;
            var updateCount = 0;
            var replaceCount = 0;
            var deleteCount = 0;

            foreach (var operation in operations)
            {
                switch (operation.ActionKind)
                {
                    case TransactionActionKind.Insert:
                        insertCount++;
                        break;
                    case TransactionActionKind.Update:
                        updateCount++;
                        break;
                    case TransactionActionKind.Replace:
                        replaceCount++;
                        break;
                    case TransactionActionKind.Delete:
                        deleteCount++;
                        break;
                }
            }

            return new TransactionPreparationResult(
                operations,
                layerId,
                layerIdCount,
                null,
                insertCount,
                updateCount,
                replaceCount,
                deleteCount);
        }

        public static TransactionPreparationResult Failure(IResult errorResult)
            => new(
                ImmutableArray<PreparedTransactionOperation>.Empty,
                0,
                0,
                errorResult,
                0,
                0,
                0,
                0);
    }
}
