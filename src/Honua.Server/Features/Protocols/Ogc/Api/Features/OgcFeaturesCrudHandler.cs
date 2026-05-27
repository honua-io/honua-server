// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Handler for OGC Features CRUD operations (Create, Update, Delete).
/// </summary>
internal sealed partial class OgcFeaturesCrudHandler(
    OgcFeaturesCrudDependencies dependencies,
    ILogger<OgcFeaturesCrudHandler> logger)
{
    private readonly IFeatureReader _featureReader = dependencies?.FeatureReader ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureWriter _featureWriter = dependencies.FeatureWriter;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly OgcFeaturesGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly IEditParameterAdapter<OgcFeaturesEditRequest> _editParameterAdapter = dependencies.EditParameterAdapter;
    private readonly IEditProcessor _editProcessor = dependencies.EditProcessor;
    private readonly IQueryProcessor _queryProcessor = dependencies.QueryProcessor;
    private readonly IETagService _etagService = dependencies.ETagService;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly FeatureMutationEventService _mutationEventService = dependencies.MutationEventService;
    private readonly ILogger<OgcFeaturesCrudHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private const string OgcFeaturesProtocolName = "OgcFeatures";

    /// <summary>
    /// Handles feature creation requests.
    /// </summary>
    public async Task<IResult> HandleCreateFeatureAsync(
        string collectionId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessV2Async(
                context, collectionId, cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var resource = layerValidation.Resource!;
            var publication = layerValidation.Publication!;

            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var storageLayerId = publication.LayerIndex
                ?? snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource);
            if (storageLayerId is not { } layerId)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' has no storage binding.");
            }

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "create");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            var contentTypeError = OgcFeaturePayloadReader.ValidateFeatureContentType(context);
            if (contentTypeError is not null)
            {
                return contentTypeError;
            }

            var (requestFeature, requestError) = await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
                context.Request,
                resource,
                requestFeature,
                _crsRegistry,
                _geometryServices,
                _mutationValidator,
                objectId: 0,
                isUpdate: false,
                cancellationToken);
            if (!buildResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    buildResult.ErrorMessage ?? "Invalid feature payload.");
            }

            var inputCrs = buildResult.InputCrs!.Value;
            var feature = buildResult.Feature
                ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");

            var editResult = await ExecuteEditAsync(
                context,
                layerId,
                resource,
                new OgcFeaturesEditRequest
                {
                    Operation = OgcFeaturesEditOperation.Create,
                    Feature = feature
                },
                cancellationToken);
            var createResult = editResult.CreateResults.FirstOrDefault();
            if (!createResult.IsSuccess || !createResult.ObjectId.HasValue)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, createResult.ErrorMessage ?? "An error occurred while creating the feature.");
            }

            var created = await _featureReader.GetAsync(layerId, createResult.ObjectId.Value, cancellationToken);
            if (!created.HasValue)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, "Created feature could not be reloaded.");
            }

            var responseFeature = await OgcFeaturesResponseHelpers.LoadFeatureForResponseAsync(
                _featureReader,
                layerId,
                resource,
                createResult.ObjectId.Value,
                inputCrs,
                cancellationToken).ConfigureAwait(false);
            if (!responseFeature.HasValue)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, "Created feature response could not be projected.");
            }

            await _mutationEventService.InvalidateLayerAsync(null, layerId, cancellationToken);
            await TryPublishFeatureChangeAsync(
                context,
                layerId,
                created.Value.Id,
                "create",
                created.Value).ConfigureAwait(false);
            var featureIdString = OgcFeatureIdentifierResolver.FormatPublicId(responseFeature.Value, resource);
            var createLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                context.Request,
                collectionId,
                featureIdString,
                MediaTypes.GeoJson,
                inputCrs.Uri);
            context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
            context.Response.Headers.ETag = OgcFeatureEntityTag.Compute(created.Value, _etagService);
            var locationUrl = OgcFeaturesUtilities.BuildFeatureSelfUrl(context.Request, collectionId, featureIdString);
            context.Response.Headers.Location = locationUrl;
            var response = ToOgcFeature(responseFeature.Value, resource, inputCrs.AxisOrder, createLinks);

            HonuaTelemetry.SetSuccess(activity);
            return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson, statusCode: StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResourceConflictException ex)
        {
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateFromException(context, ex);
        }
        catch (Exception ex)
        {
            Log.CreateFeatureFailed(_logger, collectionId, ex);
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while creating the feature.");
        }
    }

    /// <summary>
    /// Handles feature deletion requests.
    /// </summary>
    public async Task<IResult> HandleDeleteFeatureAsync(
        string collectionId,
        string featureId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessV2Async(
                context, collectionId, cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var resource = layerValidation.Resource!;
            var publication = layerValidation.Publication!;

            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var storageLayerId = publication.LayerIndex
                ?? snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource);
            if (storageLayerId is not { } layerId)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' has no storage binding.");
            }

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "delete");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            var resolvedFeature = await OgcFeatureIdentifierResolver.ResolveAsync(
                _featureReader,
                _queryProcessor,
                snapshot,
                publication,
                resource,
                featureId,
                cancellationToken).ConfigureAwait(false);
            if (!resolvedFeature.HasValue)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var objectId = resolvedFeature.Value.ObjectId;

            var editResult = await ExecuteEditAsync(
                context,
                layerId,
                resource,
                new OgcFeaturesEditRequest
                {
                    Operation = OgcFeaturesEditOperation.Delete,
                    ObjectId = objectId
                },
                cancellationToken);
            var deleteResult = editResult.DeleteResults.FirstOrDefault();
            if (!deleteResult.IsSuccess)
            {
                return IsNotFound(deleteResult)
                    ? StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.")
                    : StandardErrorHelpers.CreateInternalServerError(context, deleteResult.ErrorMessage ?? "An error occurred while deleting the feature.");
            }

            await _mutationEventService.InvalidateLayerAsync(null, layerId, cancellationToken);
            await TryPublishFeatureChangeAsync(
                context,
                layerId,
                objectId,
                "delete",
                mutationFeature: null).ConfigureAwait(false);
            HonuaTelemetry.SetSuccess(activity);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.DeleteFeatureFailed(_logger, collectionId, ex);
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while deleting the feature.");
        }
    }

    private GeoJsonFeature ToOgcFeature(
        Feature feature,
        MetadataV2Resource resource,
        AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
        => OgcGeoJsonFeatureBuilder.Create(
            feature,
            resource,
            axisOrder,
            _geometryServices,
            links: links);

    private async Task TryPublishFeatureChangeAsync(
        HttpContext context,
        int layerId,
        long objectId,
        string operation,
        Feature? mutationFeature)
    {
        try
        {
            await _mutationEventService.PublishAsync(
                context,
                layerId,
                objectId,
                operation,
                HonuaTelemetry.Protocols.OgcFeatures,
                CancellationToken.None,
                mutationFeature: mutationFeature,
                serviceProtocol: OgcFeaturesProtocolName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.FeatureChangePublishFailed(_logger, layerId, objectId, ex);
        }
    }

    /// <summary>
    /// Threads the V2 canonical resource through the edit pipeline:
    /// adapter conversion, edit-processor optimize/validate/to-batch, and
    /// outbox-scope resolution (the storage SRID is read from
    /// <see cref="MetadataV2SpatialExtensions.ReadSrid"/>).
    /// </summary>
    private async Task<FeatureEditResult> ExecuteEditAsync(
        HttpContext context,
        int layerId,
        MetadataV2Resource resource,
        OgcFeaturesEditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var editAdapterResult = await _editParameterAdapter.ConvertAsync(request, resource, cancellationToken);
        if (!editAdapterResult.IsSuccess || editAdapterResult.EditRequest == null)
        {
            throw new InvalidOperationException(editAdapterResult.ErrorMessage ?? "Invalid edit request.");
        }

        var optimizedEdit = _editProcessor.OptimizeEdit(editAdapterResult.EditRequest.Value, resource);
        var editValidation = _editProcessor.ValidateEdit(optimizedEdit, resource);
        if (!editValidation.IsValid)
        {
            throw new InvalidOperationException(editValidation.ErrorMessage ?? "Invalid edit request.");
        }

        var editBatch = _editProcessor.ToFeatureEditBatch(optimizedEdit, resource);
        // Geometry-change semantics mirror the OGC Features Transaction batch handler:
        // a row is considered to have changed geometry when the request feature carries
        // non-null WKB. Delete operations default to false. Without this, an attribute-
        // only PATCH would over-report as a geometry change because the post-mutation
        // snapshot still carries the prior WKB.
        var geometryChanged = request.Operation != OgcFeaturesEditOperation.Delete
            && request.Feature?.Geometry is { Length: > 0 };
        // Resolve outbox scope data, then activate it synchronously in this method's
        // ExecutionContext so the AsyncLocal flows into ApplyEditsAsync and each mutated
        // row writes its outbox entry inside the same DbTransaction. Activation must be
        // synchronous (caller-side) because AsyncLocal mutations from an async callee do
        // not propagate back to the caller.
        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            layerId,
            HonuaTelemetry.Protocols.OgcFeatures,
            serviceProtocol: OgcFeaturesProtocolName,
            // V2 storage SRID is read from MetadataV2SpatialExtensions.ReadSrid so the
            // outbox enrichment fallback matches the inline-publish path.
            layerSrid: resource.ReadSrid(),
            geometryChanged: geometryChanged,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var outboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);
        return await _featureWriter.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsNotFound(EditOperationResult result)
        => !string.IsNullOrWhiteSpace(result.ErrorMessage) &&
           result.ErrorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static partial class Log
    {
        [LoggerMessage(EventId = 5230, Level = LogLevel.Error, Message = "OGC create feature failed for collection {CollectionId}")]
        public static partial void CreateFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5231, Level = LogLevel.Error, Message = "OGC delete feature failed for collection {CollectionId}")]
        public static partial void DeleteFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5232, Level = LogLevel.Warning, Message = "Feature-change publish failed after OGC mutation succeeded for layer {LayerId}, object {ObjectId}.")]
        public static partial void FeatureChangePublishFailed(ILogger logger, int layerId, long objectId, Exception exception);
    }
}
