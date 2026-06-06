// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Handler for FeatureServer edit operations with explicit geometry validation.
/// </summary>
internal sealed class FeatureServerEditsHandler(
    FeatureServerEditsDependencies dependencies,
    ILogger<FeatureServerEditsHandler> logger)
{
    private const int MaxSafeEditErrorMessageLength = 240;
    private const string InvalidFeatureDataMessage = "Invalid feature data.";
    private const string InvalidGeometryPayloadMessage = "Invalid geometry payload.";

    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly IFeatureWriter _featureWriter = dependencies.FeatureWriter;
    private readonly IFeatureServerGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly IEditParameterAdapter<GeoServicesEditRequest> _editParameterAdapter = dependencies.EditParameterAdapter;
    private readonly IEditProcessor _editProcessor = dependencies.EditProcessor;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly IFilterExpressionService _filterExpressionService = dependencies.FilterExpressionService;
    private readonly IHttpContextAccessor _httpContextAccessor = dependencies.HttpContextAccessor;
    private readonly FeatureMutationEventService _mutationEventService = dependencies.MutationEventService;
    private readonly ILogger<FeatureServerEditsHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles applyEdits requests for adding, updating, and deleting features.
    /// </summary>
    public async Task<IResult> HandleApplyEditsAsync(
        string serviceId,
        int layerId,
        ApplyEditsRequest request,
        Honua.Core.Configuration.EditLimits editLimits,
        CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext!;
        using var scope = HonuaTelemetryScope.StartFeature(
            "applyEdits",
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            httpContext.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        try
        {
            FeatureServerLog.ApplyEditsRequested(_logger, serviceId, layerId,
                request.Adds?.Length ?? 0,
                request.Updates?.Length ?? 0,
                request.Deletes?.Length ?? 0);

            // Validate service and layer exist
            var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
                _resourceValidator,
                serviceId,
                layerId,
                httpContext,
                _logger,
                cancellationToken);
            if (!validationResult.IsValid)
            {
                return validationResult.ErrorResult!;
            }

            var service = validationResult.Service!;
            var publication = validationResult.Publication!;
            var resource = validationResult.Resource!;
            var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
                httpContext, resource, AuthorizationOperation.Update, service, cancellationToken).ConfigureAwait(false);
            if (accessError != null)
            {
                return accessError;
            }

            var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                httpContext,
                resource,
                service,
                cancellationToken);
            if (rbacError != null)
            {
                return rbacError;
            }

            var snapshotProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var storageLayerId = ResolveStorageLayerIdV2(snapshot, publication, resource);
            if (storageLayerId is null)
            {
                return StandardErrorHelpers.CreateNotFound(httpContext,
                    $"Layer '{resource.Metadata.Name ?? layerId.ToString(CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
            }

            // Branch-versioned editing (#1272): gdbVersion routes the edit to the branch's
            // isolated storage layer id. DEFAULT (null/empty/"DEFAULT"/"sde.DEFAULT")
            // resolves to the base storage layer id and preserves existing behavior. An
            // unknown named version is rejected. Only the storage layer id is remapped; the
            // resource (schema, fields, validation) is shared with DEFAULT so the canonical
            // edit/transaction pipeline and change tracking are reused unchanged — branch
            // edits are recorded in the same feature_changes log under the branch layer id
            // and so flow through the incremental replication path automatically.
            if (!IBranchVersionStore.IsDefaultVersion(request.GdbVersion))
            {
                var branchVersionStore = httpContext.RequestServices.GetRequiredService<IBranchVersionStore>();
                var branchLayerId = await branchVersionStore.ResolveBranchLayerIdAsync(
                    serviceId,
                    request.GdbVersion,
                    storageLayerId.Value,
                    cancellationToken).ConfigureAwait(false);
                if (branchLayerId is null)
                {
                    return StandardErrorHelpers.CreateBadRequest(httpContext,
                        "Invalid gdbVersion",
                        [$"Branch version '{request.GdbVersion}' is not registered for this service."]);
                }

                storageLayerId = branchLayerId.Value;
            }

            // Validate edit limits
            var limitsValidationResult = ValidateEditLimits(request, editLimits, httpContext);
            if (limitsValidationResult != null)
            {
                return limitsValidationResult;
            }

            var totalCount = (request.Adds?.Length ?? 0) + (request.Updates?.Length ?? 0) + (request.Deletes?.Length ?? 0);
            if (totalCount == 0)
            {
                return Results.Json(new ApplyEditsResponse { Success = true },
                    FeatureServerJsonContext.Default.ApplyEditsResponse,
                    contentType: "application/json");
            }

            // Process edit operations
            var editContext = await ProcessEditOperationsAsync(request, resource, storageLayerId.Value, cancellationToken);

            // Handle validation errors with rollback if needed
            if (editContext.HasValidationErrors && request.RollbackOnFailure)
            {
                return CreateRollbackResponse(editContext, serviceId, layerId);
            }

            // Execute edits in the database
            var editResult = await ExecuteEdits(storageLayerId.Value, resource, editContext, request, serviceId, cancellationToken);

            if (!editResult.WasRolledBack &&
                (editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount) > 0)
            {
                await _mutationEventService.InvalidateLayerAsync(serviceId, layerId, CancellationToken.None);
                await PublishFeatureChangeEventsAsync(serviceId, layerId, editContext, CancellationToken.None);
            }

            // Build and return final response
            var featureCount = editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount;
            scope.SetSuccess(featureCount);
            return CreateFinalResponse(editContext, editResult, serviceId, layerId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FeatureServerLog.ApplyEditsFailed(_logger, serviceId, layerId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(httpContext, "Apply edits failed");
        }
    }

    /// <summary>
    /// Validates edit operation counts against configured limits
    /// </summary>
    private static IResult? ValidateEditLimits(ApplyEditsRequest request, Honua.Core.Configuration.EditLimits editLimits, HttpContext context)
    {
        var addCount = request.Adds?.Length ?? 0;
        var updateCount = request.Updates?.Length ?? 0;
        var deleteCount = request.Deletes?.Length ?? 0;
        var totalCount = addCount + updateCount + deleteCount;

        if (addCount > editLimits.MaxFeaturesPerEdit ||
            updateCount > editLimits.MaxFeaturesPerEdit ||
            deleteCount > editLimits.MaxFeaturesPerEdit)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Too many features in a single edit operation",
                [$"Maximum per operation: {editLimits.MaxFeaturesPerEdit}"]);
        }

        if (totalCount > editLimits.MaxEditsPerTransaction)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Too many edits in a single request",
                [$"Maximum per request: {editLimits.MaxEditsPerTransaction}"]);
        }

        return null;
    }

    private static int? ResolveStorageLayerIdV2(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Resource resource)
        => snapshot.ResolveStorageLayerId(publication)
           ?? snapshot.ResolveStorageLayerId(resource)
           ?? publication.LayerIndex;

    /// <summary>
    /// Processes add, update, and delete operations from the request
    /// </summary>
    private async Task<EditOperationContext> ProcessEditOperationsAsync(
        ApplyEditsRequest request,
        MetadataV2Resource resource,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        var context = new EditOperationContext
        {
            AddResults = request.Adds is { Length: > 0 } ? new EditResult?[request.Adds.Length] : null,
            UpdateResults = request.Updates is { Length: > 0 } ? new EditResult?[request.Updates.Length] : null,
            DeleteResults = request.Deletes is { Length: > 0 } ? new EditResult?[request.Deletes.Length] : null
        };

        await ProcessAddOperationsAsync(request, context, resource, cancellationToken);
        await ProcessUpdateOperationsAsync(request, context, resource, storageLayerId, cancellationToken);
        await ProcessDeleteOperationsAsync(request, resource, storageLayerId, context, cancellationToken);

        return context;
    }

    /// <summary>
    /// Processes add operations and tracks features to create
    /// </summary>
    private async Task ProcessAddOperationsAsync(
        ApplyEditsRequest request,
        EditOperationContext context,
        MetadataV2Resource resource,
        CancellationToken cancellationToken)
    {
        if (request.Adds == null)
            return;

        for (var i = 0; i < request.Adds.Length; i++)
        {
            try
            {
                // Capture request intent before BuildFeatureFromGeoServicesAsync runs;
                // for adds existingFeature is null so the post-merge geometry equals the
                // request's, but using request.Adds[i].Geometry directly keeps the rule
                // identical to the update path.
                var requestHasGeometry = request.Adds[i].Geometry != null;
                var newFeature = await BuildFeatureFromGeoServicesAsync(request.Adds[i], 0, resource, cancellationToken);
                context.CreateFeatures.Add(newFeature);
                context.CreateIndexes.Add(i);
                context.CreateGeometryChanged.Add(requestHasGeometry);
                context.CreateResponseObjectIds.Add(TryGetObjectId(newFeature.Attributes.ToDictionary(), resource, out var responseObjectId)
                    ? responseObjectId
                    : null);
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: 1000,
                    description: SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage));
            }
            catch (Exception ex)
            {
                FeatureServerLog.FeatureAddFailed(logger, i, ex.Message, ex);
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: 1000,
                    description: "Failed to add feature");
            }
        }
    }

    /// <summary>
    /// Processes update operations and tracks features to update
    /// </summary>
    private async Task ProcessUpdateOperationsAsync(
        ApplyEditsRequest request,
        EditOperationContext context,
        MetadataV2Resource resource,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        if (request.Updates == null)
            return;

        for (var i = 0; i < request.Updates.Length; i++)
        {
            var update = request.Updates[i];
            if (!TryGetObjectId(update.Attributes, resource, out var objectId))
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: 1001,
                    description: "ObjectId is required for update operations");
                continue;
            }

            try
            {
                var existingFeature = await ResolveFeatureByGeoServicesObjectIdAsync(resource, storageLayerId, objectId, cancellationToken)
                    .ConfigureAwait(false);
                if (!ShouldUseInternalObjectIdFastPath(resource) && existingFeature is null)
                {
                    context.HasValidationErrors = true;
                    context.UpdateResults![i] = CreateFailureResult(
                        code: 1002,
                        description: "Feature not found",
                        objectId: objectId);
                    continue;
                }

                var internalObjectId = existingFeature?.Id ?? objectId;
                // Capture request intent BEFORE BuildFeatureFromGeoServicesAsync runs;
                // that helper preserves existingFeature.Geometry when update.Geometry is
                // null, so the post-merge feature's WKB cannot distinguish an attribute-
                // only update on a spatial row from a geometry change.
                var requestHasGeometry = update.Geometry != null;
                var updateFeature = await BuildFeatureFromGeoServicesAsync(
                    update,
                    internalObjectId,
                    resource,
                    cancellationToken,
                    existingFeature).ConfigureAwait(false);
                context.UpdateFeatures.Add(updateFeature);
                context.UpdateIndexes.Add(i);
                context.UpdateObjectIds.Add(objectId);
                context.UpdateGeometryChanged.Add(requestHasGeometry);
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: 1002,
                    description: SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage),
                    objectId: objectId);
            }
            catch (Exception ex)
            {
                FeatureServerLog.FeatureUpdateFailed(logger, i, ex.Message, ex);
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: 1002,
                    description: "Failed to update feature",
                    objectId: objectId);
            }
        }
    }

    /// <summary>
    /// Processes delete operations and tracks features to delete
    /// </summary>
    private async Task ProcessDeleteOperationsAsync(
        ApplyEditsRequest request,
        MetadataV2Resource resource,
        int storageLayerId,
        EditOperationContext context,
        CancellationToken cancellationToken)
    {
        if (request.Deletes == null)
            return;

        for (var i = 0; i < request.Deletes.Length; i++)
        {
            if (!FeatureServerValueParser.TryConvertToLong(request.Deletes[i], out var objectId))
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: 1003,
                    description: "Invalid ObjectId for delete operation");
                continue;
            }

            var existingFeature = await ResolveFeatureByGeoServicesObjectIdAsync(resource, storageLayerId, objectId, cancellationToken)
                .ConfigureAwait(false);
            if (!ShouldUseInternalObjectIdFastPath(resource) && existingFeature is null)
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: 1003,
                    description: "Feature not found",
                    objectId: objectId);
                continue;
            }

            var internalObjectId = existingFeature?.Id ?? objectId;
            context.DeleteIds.Add(internalObjectId);
            context.DeleteResponseObjectIds.Add(objectId);
            context.DeleteIndexes.Add(i);
            context.DeleteFeatures.Add(existingFeature ?? await ReadDeleteFeatureSnapshotAsync(storageLayerId, internalObjectId, cancellationToken));
        }
    }

    private async Task<Feature?> ReadDeleteFeatureSnapshotAsync(
        int layerId,
        long objectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _featureReader.GetAsync(layerId, objectId, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executes the validated edit operations in the database
    /// </summary>
    private async Task<FeatureEditResult> ExecuteEdits(
        int layerId,
        MetadataV2Resource resource,
        EditOperationContext context,
        ApplyEditsRequest request,
        string serviceId,
        CancellationToken cancellationToken)
    {
        if (context.CreateFeatures.Count == 0 && context.UpdateFeatures.Count == 0 && context.DeleteIds.Count == 0)
        {
            return FeatureEditResult.Success(0, 0, 0);
        }

        var editAdapterResult = await _editParameterAdapter.ConvertAsync(
            new GeoServicesEditRequest
            {
                Creates = context.CreateFeatures.ToImmutableArray(),
                Updates = context.UpdateFeatures.ToImmutableArray(),
                Deletes = context.DeleteIds.ToImmutableArray(),
                RollbackOnFailure = request.RollbackOnFailure,
                UseGlobalIds = request.UseGlobalIds
            },
            resource,
            cancellationToken);
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
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext is required for FeatureServer edit dispatch.");
        // Per-row geometry-change semantics: read the request-intent flags captured by
        // ProcessAdd/UpdateOperationsAsync BEFORE BuildFeatureFromGeoServicesAsync merged
        // the request with the existing row. Deriving from editBatch.Updates[i].Geometry
        // would over-report attribute-only updates as geometry changes because that
        // helper preserves the prior WKB when the request omits geometry.
        var perOperationGeometryChanged = BuildPerOperationGeometryChanged(context);
        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            httpContext,
            layerId,
            HonuaTelemetry.Protocols.FeatureServer,
            serviceId: serviceId,
            serviceProtocol: HonuaTelemetry.Protocols.FeatureServer,
            // ToSrid() picks LatestWkid when set so the outbox enrichment fallback
            // matches the inline post-commit path on layers like
            // Wkid=102100/LatestWkid=3857.
            layerSrid: resource.ReadSrid(),
            perOperationGeometryChanged: perOperationGeometryChanged,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var outboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);
        var editResult = await _featureWriter.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);

        ApplyResults(context.AddResults, context.CreateIndexes, editResult.CreateResults);
        ApplyCreateResponseObjectIds(context);
        CaptureCreateEventObjectIds(context, editResult.CreateResults);
        ApplyResults(context.UpdateResults, context.UpdateIndexes, editResult.UpdateResults, context.UpdateObjectIds);
        ApplyResults(context.DeleteResults, context.DeleteIndexes, editResult.DeleteResults, context.DeleteResponseObjectIds);

        return editResult;
    }

    /// <summary>
    /// Build per-operation-kind queues of geometry-change flags from the request-intent
    /// flags captured during request parsing (before merging with existing rows). The
    /// queues match the order ApplyEditsAsync iterates rows for each kind so each
    /// outbox row's <c>GeometryChanged</c> tracks the originating request's intent
    /// rather than the post-merge feature's WKB. Deletes default to false (the inline
    /// publish path also defaults to false for delete events).
    /// </summary>
    private static Dictionary<string, IReadOnlyList<bool>>? BuildPerOperationGeometryChanged(EditOperationContext context)
    {
        if (context.CreateGeometryChanged.Count == 0
            && context.UpdateGeometryChanged.Count == 0
            && context.DeleteIds.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal);
        if (context.CreateGeometryChanged.Count > 0)
        {
            result["create"] = context.CreateGeometryChanged.ToImmutableArray();
        }
        if (context.UpdateGeometryChanged.Count > 0)
        {
            result["update"] = context.UpdateGeometryChanged.ToImmutableArray();
        }
        if (context.DeleteIds.Count > 0)
        {
            result["delete"] = Enumerable.Repeat(false, context.DeleteIds.Count).ToImmutableArray();
        }
        return result;
    }

    /// <summary>
    /// Creates a rollback response when validation errors occur
    /// </summary>
    private IResult CreateRollbackResponse(EditOperationContext context, string serviceId, int layerId)
    {
        var rollbackError = new EditError
        {
            Code = 1000,
            Description = "Operation rolled back due to validation failure"
        };

        ApplyRollbackResults(context.AddResults, context.CreateIndexes, null, rollbackError);
        ApplyRollbackResults(context.UpdateResults, context.UpdateIndexes, context.UpdateObjectIds, rollbackError);
        ApplyRollbackResults(context.DeleteResults, context.DeleteIndexes, context.DeleteResponseObjectIds, rollbackError);

        var response = new ApplyEditsResponse
        {
            AddResults = FinalizeResults(context.AddResults),
            UpdateResults = FinalizeResults(context.UpdateResults),
            DeleteResults = FinalizeResults(context.DeleteResults),
            Success = false
        };

        FeatureServerLog.ApplyEditsCompleted(_logger, serviceId, layerId, false);

        return Results.Json(response, FeatureServerJsonContext.Default.ApplyEditsResponse,
            contentType: "application/json");
    }

    /// <summary>
    /// Creates the final response after all operations complete
    /// </summary>
    private IResult CreateFinalResponse(EditOperationContext context, FeatureEditResult editResult, string serviceId, int layerId)
    {
        var finalAddResults = FinalizeResults(context.AddResults);
        var finalUpdateResults = FinalizeResults(context.UpdateResults);
        var finalDeleteResults = FinalizeResults(context.DeleteResults);
        var allSuccess = AreAllResultsSuccessful(finalAddResults) &&
                         AreAllResultsSuccessful(finalUpdateResults) &&
                         AreAllResultsSuccessful(finalDeleteResults) &&
                         !editResult.WasRolledBack &&
                         !context.HasValidationErrors;

        var finalResponse = new ApplyEditsResponse
        {
            AddResults = finalAddResults,
            UpdateResults = finalUpdateResults,
            DeleteResults = finalDeleteResults,
            Success = allSuccess
        };

        FeatureServerLog.ApplyEditsCompleted(_logger, serviceId, layerId, allSuccess);

        return Results.Json(finalResponse, FeatureServerJsonContext.Default.ApplyEditsResponse,
            contentType: "application/json");
    }

    private async Task PublishFeatureChangeEventsAsync(
        string serviceId,
        int layerId,
        EditOperationContext context,
        CancellationToken cancellationToken)
    {
        var requestId = _httpContextAccessor.HttpContext?.TraceIdentifier ?? "unknown";

        for (var i = 0; i < context.CreateFeatures.Count; i++)
        {
            var resultIndex = context.CreateIndexes[i];
            var result = context.AddResults?[resultIndex];
            if (result is not { Success: true, ObjectId: { } objectId })
            {
                continue;
            }

            var eventObjectId = context.CreateEventObjectIds.Count > i
                ? context.CreateEventObjectIds[i] ?? objectId
                : objectId;
            await _mutationEventService.PublishAsync(
                _httpContextAccessor.HttpContext!,
                layerId,
                eventObjectId,
                "create",
                HonuaTelemetry.Protocols.FeatureServer,
                cancellationToken,
                mutationFeature: context.CreateFeatures[i],
                serviceId: serviceId,
                requestId: requestId).ConfigureAwait(false);
        }

        for (var i = 0; i < context.UpdateFeatures.Count; i++)
        {
            var resultIndex = context.UpdateIndexes[i];
            var result = context.UpdateResults?[resultIndex];
            if (result is not { Success: true })
            {
                continue;
            }

            await _mutationEventService.PublishAsync(
                _httpContextAccessor.HttpContext!,
                layerId,
                context.UpdateFeatures[i].Id,
                "update",
                HonuaTelemetry.Protocols.FeatureServer,
                cancellationToken,
                mutationFeature: context.UpdateFeatures[i],
                serviceId: serviceId,
                requestId: requestId).ConfigureAwait(false);
        }

        for (var i = 0; i < context.DeleteIndexes.Count; i++)
        {
            var resultIndex = context.DeleteIndexes[i];
            var result = context.DeleteResults?[resultIndex];
            if (result is not { Success: true })
            {
                continue;
            }

            var deleteFeature = context.DeleteFeatures.Count > i ? context.DeleteFeatures[i] : null;
            await _mutationEventService.PublishAsync(
                _httpContextAccessor.HttpContext!,
                layerId,
                context.DeleteIds[i],
                "delete",
                HonuaTelemetry.Protocols.FeatureServer,
                cancellationToken,
                mutationFeature: deleteFeature,
                serviceId: serviceId,
                requestId: requestId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Context object to track edit operations state
    /// </summary>
    private sealed class EditOperationContext
    {
        public EditResult?[]? AddResults { get; init; }
        public EditResult?[]? UpdateResults { get; init; }
        public EditResult?[]? DeleteResults { get; init; }
        public List<Feature> CreateFeatures { get; } = new();
        public List<int> CreateIndexes { get; } = new();
        public List<long?> CreateResponseObjectIds { get; } = new();
        public List<long?> CreateEventObjectIds { get; } = new();
        /// <summary>
        /// Per-create flag: true when the originating request body included a Geometry;
        /// captured before merging so the outbox payload's GeometryChanged tracks the
        /// request's intent rather than the post-merge feature's WKB.
        /// </summary>
        public List<bool> CreateGeometryChanged { get; } = new();
        public List<Feature> UpdateFeatures { get; } = new();
        public List<int> UpdateIndexes { get; } = new();
        public List<long> UpdateObjectIds { get; } = new();
        /// <summary>
        /// Per-update flag: true when the originating request body included a Geometry;
        /// captured before <c>BuildFeatureFromGeoServicesAsync</c> merges with the existing
        /// row, since BuildFeatureFromGeoServicesAsync preserves <c>existingFeature.Geometry</c>
        /// when the request omits geometry, otherwise an attribute-only update on a spatial
        /// row would be reported as a geometry change.
        /// </summary>
        public List<bool> UpdateGeometryChanged { get; } = new();
        public List<long> DeleteIds { get; } = new();
        public List<long> DeleteResponseObjectIds { get; } = new();
        public List<Feature?> DeleteFeatures { get; } = new();
        public List<int> DeleteIndexes { get; } = new();
        public bool HasValidationErrors { get; set; }
    }

    private async Task<Feature?> ResolveFeatureByGeoServicesObjectIdAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        long objectId,
        CancellationToken cancellationToken)
    {
        if (ShouldUseInternalObjectIdFastPath(resource))
        {
            return await _featureReader.GetAsync(storageLayerId, objectId, cancellationToken).ConfigureAwait(false);
        }

        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource);
        if (objectIdField is null)
        {
            return null;
        }

        var expression = new BinaryExpression(
            new PropertyReference(objectIdField.Name),
            BinaryOperator.Equal,
            new Literal(objectId, LiteralType.Number));
        var translation = _filterExpressionService.Translate(expression, resource);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid ObjectId field.");
        }

        var result = await _featureReader.QueryAsync(
            storageLayerId,
            new FeatureQuery
            {
                SqlFilter = translation.SqlFilter,
                Limit = 1
            },
            cancellationToken).ConfigureAwait(false);

        return result.Items.IsDefaultOrEmpty ? null : result.Items[0];
    }

    private static bool ShouldUseInternalObjectIdFastPath(MetadataV2Resource resource)
        => GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource)?.Name.Equals(
            FieldNames.ObjectId,
            StringComparison.OrdinalIgnoreCase) != false;

    private async Task<Feature> BuildFeatureFromGeoServicesAsync(
        GeoServicesFeature feature,
        long objectId,
        MetadataV2Resource resource,
        CancellationToken cancellationToken,
        Feature? existingFeature = null)
    {
        byte[]? geometry = existingFeature?.Geometry;
        if (feature.Geometry != null)
        {
            // Enforce the layer's declared geometry type. ArcGIS rejects a feature whose
            // geometry shape does not match the layer's geometryType (e.g. a polygon sent to
            // an esriGeometryPoint layer). Without this the WKB converter would happily store
            // the mismatched shape, silently corrupting the layer's geometry homogeneity.
            var layerGeometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
            if (!IsGeometryTypeCompatible(feature.Geometry, layerGeometryType, out var geometryTypeError))
            {
                throw new ArgumentException(geometryTypeError);
            }

            // Layer 1: Validate Esri JSON input
            var esriValidation = _geometryServices.ValidateEsriJson(feature.Geometry);
            if (!esriValidation.IsValid)
            {
                var errorMessages = string.Join("; ", esriValidation.Errors.Select(e => e.Message));
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry validation failed: {errorMessages}",
                    "Geometry validation failed.");
                throw new ArgumentException(safeError);
            }

            var layerSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
            var geometrySrid = feature.Geometry.SpatialReference?.Wkid
                ?? feature.Geometry.SpatialReference?.LatestWkid;
            if (geometrySrid.HasValue && geometrySrid.Value != layerSrid)
            {
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry spatial reference {geometrySrid.Value} does not match layer SRID {layerSrid}.",
                    "Geometry spatial reference does not match layer SRID.");
                throw new ArgumentException(safeError);
            }

            geometrySrid ??= layerSrid;
            try
            {
                geometry = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(feature.Geometry, geometrySrid);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    SanitizeEditErrorMessage(ex.Message, InvalidGeometryPayloadMessage),
                    ex);
            }

            var geometryValidation = await _mutationValidator.ValidateGeometryAsync(geometry, cancellationToken);
            if (!geometryValidation.IsValid)
            {
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry validation failed: {geometryValidation.ErrorMessage}",
                    "Geometry validation failed.");
                throw new ArgumentException(safeError);
            }

            geometry = geometryValidation.Geometry;
        }

        var attributesResult = _mutationValidator.ValidateAttributes(
            resource,
            feature.Attributes,
            ValidationExtensions.AttributeValidationMode.GeoServices,
            isUpdate: existingFeature is not null);
        if (!attributesResult.IsValid)
        {
            throw new ArgumentException(
                SanitizeEditErrorMessage(attributesResult.ErrorMessage, "Invalid attributes."));
        }

        var attributes = existingFeature?.Attributes.ToBuilder()
            ?? ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in attributesResult.Value!)
        {
            attributes[key] = value;
        }

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        if (objectIdFieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            attributes.Remove(objectIdFieldName);
        }

        return Feature.Create(objectId, geometry, attributes.ToImmutable());
    }

    /// <summary>
    /// Verifies that an inbound GeoServices geometry's shape is compatible with the layer's
    /// declared <see cref="MetadataV2GeometryType"/>. ArcGIS feature layers are geometry-type
    /// homogeneous, so an add/update whose geometry shape disagrees with the layer must fail
    /// per-feature rather than be silently stored. The check classifies the geometry by its
    /// populated coordinate members (the GeoServices JSON shape is discriminated by which of
    /// x/y, points, paths, rings, or the envelope bounds are present). Point and multipoint
    /// inputs are accepted on a point layer (a single coordinate is a degenerate multipoint),
    /// and multi-* layer types accept their single-part equivalents.
    /// </summary>
    private static bool IsGeometryTypeCompatible(
        GeoServicesGeometry geometry,
        MetadataV2GeometryType layerGeometryType,
        out string? error)
    {
        error = null;

        // Mixed / collection / unspecified layers do not constrain the geometry shape.
        if (layerGeometryType is MetadataV2GeometryType.None
            or MetadataV2GeometryType.Mixed
            or MetadataV2GeometryType.GeometryCollection)
        {
            return true;
        }

        var inputType = ClassifyGeoServicesGeometry(geometry);
        if (inputType is null)
        {
            // Could not classify (e.g. empty geometry object); leave shape validation to the
            // downstream Esri-JSON / WKB validators rather than rejecting here.
            return true;
        }

        if (IsGeometryShapeCompatible(layerGeometryType, inputType.Value))
        {
            return true;
        }

        error = $"Geometry type {DescribeGeometryType(inputType.Value)} does not match the layer geometry type {DescribeGeometryType(layerGeometryType)}.";
        return false;
    }

    /// <summary>
    /// Classifies a GeoServices geometry object into the canonical geometry-shape family it
    /// represents based on which coordinate members are populated. Returns <c>null</c> when no
    /// recognizable geometry members are present.
    /// </summary>
    private static MetadataV2GeometryType? ClassifyGeoServicesGeometry(GeoServicesGeometry geometry)
    {
        if (geometry.Rings != null)
        {
            return MetadataV2GeometryType.Polygon;
        }

        if (geometry.Paths != null)
        {
            return MetadataV2GeometryType.LineString;
        }

        if (geometry.Points != null)
        {
            return MetadataV2GeometryType.MultiPoint;
        }

        if (geometry.X.HasValue || geometry.Y.HasValue)
        {
            return MetadataV2GeometryType.Point;
        }

        if (geometry.Xmin.HasValue || geometry.Ymin.HasValue
            || geometry.Xmax.HasValue || geometry.Ymax.HasValue)
        {
            // Envelopes are polygonal in shape; only meaningful on polygon layers.
            return MetadataV2GeometryType.Polygon;
        }

        return null;
    }

    /// <summary>
    /// Returns true when an input geometry shape may be stored on a layer of the given type.
    /// A point may be stored on a multipoint layer and vice-versa; line/polygon single- and
    /// multi-part variants are interchangeable, matching how the GeoServices JSON encodes both
    /// single- and multi-part polylines/polygons with the same paths/rings members.
    /// </summary>
    private static bool IsGeometryShapeCompatible(MetadataV2GeometryType layerType, MetadataV2GeometryType inputType)
    {
        return layerType switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint
                => inputType is MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint,
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString
                => inputType is MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString,
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon
                => inputType is MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon,
            _ => false
        };
    }

    private static string DescribeGeometryType(MetadataV2GeometryType geometryType)
        => geometryType switch
        {
            MetadataV2GeometryType.Point => "esriGeometryPoint",
            MetadataV2GeometryType.MultiPoint => "esriGeometryMultipoint",
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => "esriGeometryPolyline",
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => "esriGeometryPolygon",
            _ => "esriGeometryNull"
        };

    private static bool TryGetObjectId(Dictionary<string, object?>? attributes, MetadataV2Resource resource, out long objectId)
    {
        objectId = 0;

        if (attributes == null || attributes.Count == 0)
        {
            return false;
        }

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        foreach (var entry in attributes)
        {
            if (string.Equals(entry.Key, objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return FeatureServerValueParser.TryConvertToLong(entry.Value, out objectId);
            }
        }

        return false;
    }

    private static EditResult CreateFailureResult(int code, string description, long? objectId = null, string? globalId = null)
    {
        return new EditResult
        {
            ObjectId = objectId,
            GlobalId = globalId,
            Success = false,
            Error = new EditError
            {
                Code = code,
                Description = description
            }
        };
    }

    private static EditResult ConvertEditOperationResult(EditOperationResult result, long? responseObjectId = null)
    {
        if (result.IsSuccess)
        {
            return new EditResult
            {
                ObjectId = responseObjectId ?? result.ObjectId,
                GlobalId = result.GlobalId,
                Success = true
            };
        }

        return CreateFailureResult(
            result.ErrorCode,
            SanitizeEditErrorMessage(result.ErrorMessage, "Operation failed"),
            responseObjectId ?? result.ObjectId,
            result.GlobalId);
    }

    private static void ApplyRollbackResults(EditResult?[]? results, List<int> indexes, List<long>? objectIds, EditError rollbackError)
    {
        if (results == null)
        {
            return;
        }

        for (var i = 0; i < indexes.Count; i++)
        {
            long? objectId = null;
            if (objectIds != null && i < objectIds.Count)
            {
                objectId = objectIds[i];
            }

            results[indexes[i]] = CreateFailureResult(rollbackError.Code, rollbackError.Description, objectId);
        }
    }

    private static void ApplyResults(
        EditResult?[]? results,
        List<int> indexes,
        ImmutableArray<EditOperationResult> operationResults,
        List<long>? responseObjectIds = null)
    {
        if (results == null)
        {
            return;
        }

        var count = Math.Min(indexes.Count, operationResults.Length);
        for (var i = 0; i < count; i++)
        {
            var responseObjectId = responseObjectIds != null && i < responseObjectIds.Count
                ? responseObjectIds[i]
                : (long?)null;
            results[indexes[i]] = ConvertEditOperationResult(operationResults[i], responseObjectId);
        }

        for (var i = count; i < indexes.Count; i++)
        {
            results[indexes[i]] ??= CreateFailureResult(1000, "Operation failed");
        }
    }

    private static void ApplyCreateResponseObjectIds(EditOperationContext context)
    {
        if (context.AddResults == null)
        {
            return;
        }

        for (var i = 0; i < context.CreateIndexes.Count && i < context.CreateResponseObjectIds.Count; i++)
        {
            var responseObjectId = context.CreateResponseObjectIds[i];
            if (!responseObjectId.HasValue)
            {
                continue;
            }

            var result = context.AddResults[context.CreateIndexes[i]];
            if (result is { Success: true })
            {
                result.ObjectId = responseObjectId.Value;
            }
        }
    }

    private static void CaptureCreateEventObjectIds(
        EditOperationContext context,
        ImmutableArray<EditOperationResult> operationResults)
    {
        context.CreateEventObjectIds.Clear();
        for (var i = 0; i < context.CreateIndexes.Count; i++)
        {
            context.CreateEventObjectIds.Add(i < operationResults.Length
                ? operationResults[i].ObjectId
                : null);
        }
    }

    private static EditResult[]? FinalizeResults(EditResult?[]? results)
    {
        if (results == null)
        {
            return null;
        }

        var finalized = new EditResult[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            finalized[i] = results[i] ?? CreateFailureResult(1000, "Operation failed");
        }

        return finalized;
    }

    private static bool AreAllResultsSuccessful(EditResult[]? results)
    {
        if (results == null)
        {
            return true;
        }

        return results.All(result => result.Success);
    }

    private static string SanitizeEditErrorMessage(string? message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        var trimmed = message.Trim();
        if (trimmed.Length > MaxSafeEditErrorMessageLength || ContainsUnsafeEditMessagePattern(trimmed))
        {
            return fallback;
        }

        return trimmed;
    }

    private static bool ContainsUnsafeEditMessagePattern(string message)
    {
        return message.Contains('\r') ||
               message.Contains('\n') ||
               message.Contains("BytePositionInLine", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("LineNumber", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("System.", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("StackTrace", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("SQLSTATE", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("password", StringComparison.OrdinalIgnoreCase);
    }


}
