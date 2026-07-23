// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Api.Features;

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

    // Open-protocol feature mutations are Community (#1591): no entitlement gate here. The
    // Pro editing gate applies only to the Esri GeoServices FeatureServer write surface.

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
            // Per-operation authorization (BH3-001/BH3-014): a POST create authorizes as Insert.
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessV2Async(
                context, collectionId, operation: AuthorizationOperation.Insert, cancellationToken: cancellationToken);
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

            var (requestFeature, requestErrorResult, requestError) =
                await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestErrorResult is not null)
            {
                return requestErrorResult;
            }

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

            await _mutationEventService.InvalidateLayerAsync(null, layerId, CancellationToken.None);
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
        catch (ValidationException ex)
        {
            // Client-side edit-validation failure (e.g. a genuinely-missing required attribute):
            // map to a 4xx problem response rather than a 500.
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateValidationError(context, ex);
        }
        catch (ResourceConflictException ex)
        {
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateFromException(context, ex);
        }
        // Intentionally generic: this is the top-level request handler boundary; any
        // unanticipated failure not already handled by the specific catches above must map
        // to a generic 500 rather than crash the request.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        string? ifMatch,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            // Per-operation authorization (BH3-001/BH3-014): a DELETE authorizes as Delete.
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessV2Async(
                context, collectionId, operation: AuthorizationOperation.Delete, cancellationToken: cancellationToken);
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
            var existing = resolvedFeature.Value.Feature;

            // Validate If-Match precondition before executing the delete.  The pre-check
            // gives a fast 412 without an unnecessary write round-trip; the canonical state
            // token is re-validated inside the write transaction (see EnsurePreconditionSatisfiedAsync)
            // so a concurrent commit between this check and the write cannot be silently lost.
            string? expectedStateToken = null;
            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                var etag = OgcFeatureEntityTag.Compute(existing, _etagService);
                if (!OgcFeatureEntityTag.MatchesEntityOrRepresentation(ifMatch, etag, _etagService))
                {
                    return Results.Problem(
                        statusCode: 412,
                        title: "Precondition Failed",
                        detail: "The resource has been modified since the provided ETag.");
                }

                expectedStateToken = FeatureStateToken.Compute(existing);
            }

            var editResult = await ExecuteEditAsync(
                context,
                layerId,
                resource,
                new OgcFeaturesEditRequest
                {
                    Operation = OgcFeaturesEditOperation.Delete,
                    ObjectId = objectId,
                    IfMatch = ifMatch,
                    ExpectedStateToken = expectedStateToken
                },
                cancellationToken);
            var deleteResult = editResult.DeleteResults.FirstOrDefault();
            if (!deleteResult.IsSuccess)
            {
                if (deleteResult.IsPreconditionFailure)
                {
                    return Results.Problem(
                        statusCode: 412,
                        title: "Precondition Failed",
                        detail: "The resource has been modified since the provided ETag.");
                }

                return IsNotFound(deleteResult)
                    ? StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.")
                    : StandardErrorHelpers.CreateInternalServerError(context, deleteResult.ErrorMessage ?? "An error occurred while deleting the feature.");
            }

            await _mutationEventService.InvalidateLayerAsync(null, layerId, CancellationToken.None);
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
        catch (ValidationException ex)
        {
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateValidationError(context, ex);
        }
        // Intentionally generic: this is the top-level request handler boundary; any
        // unanticipated failure not already handled by the specific catches above must map
        // to a generic 500 rather than crash the request.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        // Intentionally generic: the edit is already committed, so a publish failure must
        // not surface as a request failure; log and swallow so the caller still sees a
        // successful create/delete response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
            // Adapter/validation failures are client-input errors: surface as ValidationException so
            // the handler maps them to a 4xx problem response rather than a generic 500.
            throw new ValidationException(editAdapterResult.ErrorMessage ?? "Invalid edit request.");
        }

        var optimizedEdit = _editProcessor.OptimizeEdit(editAdapterResult.EditRequest.Value, resource);
        var editValidation = _editProcessor.ValidateEdit(optimizedEdit, resource);
        if (!editValidation.IsValid)
        {
            throw new ValidationException(editValidation.ErrorMessage ?? "Invalid edit request.");
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
