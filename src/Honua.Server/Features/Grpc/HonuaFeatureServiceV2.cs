// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using Proto = Honua.Server.Features.Grpc.Proto.V2;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// Enhanced gRPC service implementation for v2 protocol features.
/// Supports multiple geometry encodings, structured errors, mobile optimizations, and bidirectional sync.
/// </summary>
internal sealed class HonuaFeatureServiceV2 : Proto.FeatureService.FeatureServiceBase
{
    private static readonly PaginationValidationOptions _grpcPaginationValidation =
        new(MinOffset: 0, MinLimit: 1, OffsetParameterName: "resultOffset", LimitParameterName: "resultRecordCount");

    private readonly IResourceValidator _resourceValidator;
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IStreamingFeatureStore _streamingFeatureStore;
    private readonly ICommonQueryValidator _queryValidator;
    private readonly SpatialReferenceResolver _spatialReferenceResolver;
    private readonly IGeometryEncodingService _geometryEncodingService;
    private readonly IMobileOptimizationService _mobileOptimizationService;
    private readonly ISyncService _syncService;
    private readonly ILogger<HonuaFeatureServiceV2> _logger;
    private readonly GeometryLimits _geometryLimits;
    private readonly int _streamBatchSize;

    public HonuaFeatureServiceV2(
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IStreamingFeatureStore streamingFeatureStore,
        ICommonQueryValidator queryValidator,
        SpatialReferenceResolver spatialReferenceResolver,
        IGeometryEncodingService geometryEncodingService,
        IMobileOptimizationService mobileOptimizationService,
        ISyncService syncService,
        IOptions<LimitsOptions> limitsOptions,
        IOptions<GrpcOptions> grpcOptions,
        ILogger<HonuaFeatureServiceV2> logger)
    {
        _resourceValidator = resourceValidator;
        _featureReader = featureReader;
        _featureWriter = featureWriter;
        _streamingFeatureStore = streamingFeatureStore;
        _queryValidator = queryValidator;
        _spatialReferenceResolver = spatialReferenceResolver;
        _geometryEncodingService = geometryEncodingService;
        _mobileOptimizationService = mobileOptimizationService;
        _syncService = syncService;
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        _streamBatchSize = Math.Max(grpcOptions?.Value?.StreamBatchSize ?? 1000, 1);
        _logger = logger;
    }

    #region Query Operations

    public override async Task<Proto.QueryFeaturesResponse> QueryFeatures(
        Proto.QueryFeaturesRequest request,
        ServerCallContext context)
    {
        using var activity = CreateActivity("query_features_v2", request.ServiceId, request.LayerId);
        var startTime = DateTime.UtcNow;

        try
        {
            var validation = await ValidateResourceAsync(request.ServiceId, request.LayerId, context.CancellationToken);
            var (service, layer) = validation.Resource!;

            var queryContext = await CreateQueryContextAsync(request, layer, context.CancellationToken);
            var query = queryContext.Query;
            var pkField = layer.PrimaryKeyField?.Name ?? "objectid";

            var response = new Proto.QueryFeaturesResponse
            {
                ObjectIdFieldName = pkField,
                GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType),
                SpatialReference = await CreateEnhancedSpatialReferenceAsync(queryContext.ResponseSpatialReference),
                Metadata = new Proto.QueryMetadata
                {
                    ExecutionTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(startTime),
                    UsedSpatialIndex = queryContext.UsedSpatialIndex
                }
            };

            // Handle specialized queries
            if (request.CountOnly)
            {
                response.Count = await _featureReader.CountAsync(request.LayerId, query, context.CancellationToken);
                response.Metadata.ExecutionDuration = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
                return response;
            }

            if (request.IdsOnly)
            {
                var objectIds = await _featureReader.QueryObjectIdsAsync(request.LayerId, query, context.CancellationToken);
                response.ObjectIds.AddRange(objectIds);
                response.Metadata.ExecutionDuration = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
                return response;
            }

            if (request.ExtentOnly)
            {
                var extent = await _featureReader.GetExtentAsync(request.LayerId, query, context.CancellationToken);
                if (extent.HasValue)
                {
                    response.Extent = GrpcConversionHelpers.ToProtoExtent(extent.Value, queryContext.ResponseSpatialReference);
                }
                response.Metadata.ExecutionDuration = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
                return response;
            }

            // Standard feature query with mobile optimizations
            await PopulateFieldsAsync(response, layer, request.MobileOptions);

            var result = await _featureReader.QueryAsync(request.LayerId, query, context.CancellationToken);

            // Apply mobile optimizations to result
            var optimizedResult = await _mobileOptimizationService.OptimizeResultAsync(result, request.MobileOptions);

            foreach (var feature in optimizedResult.Items)
            {
                var protoFeature = await CreateEnhancedFeatureAsync(
                    feature,
                    queryContext.ReturnGeometry,
                    request.GeometryEncoding,
                    queryContext.GeometryLimits);
                response.Features.Add(protoFeature);
            }

            response.ExceededTransferLimit = result.HasMoreResults;
            response.Metadata.ExecutionDuration = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            response.Metadata.GeometrySimplificationLevel = queryContext.SimplificationLevel;

            return response;
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex, startTime);
        }
    }

    public override async Task QueryFeaturesStream(
        Proto.QueryFeaturesRequest request,
        IServerStreamWriter<Proto.FeaturePage> responseStream,
        ServerCallContext context)
    {
        using var activity = CreateActivity("query_features_stream_v2", request.ServiceId, request.LayerId);

        try
        {
            EnsureStreamingFlagsSupported(request);
            var validation = await ValidateResourceAsync(request.ServiceId, request.LayerId, context.CancellationToken);
            var (service, layer) = validation.Resource!;

            var queryContext = await CreateQueryContextAsync(request, layer, context.CancellationToken);
            var pkField = layer.PrimaryKeyField?.Name ?? "objectid";

            var isFirstPage = true;
            var pageNumber = 0;
            var batch = new List<Proto.Feature>(_streamBatchSize);

            await using var enumerator = _streamingFeatureStore
                .StreamFeaturesAsync(request.LayerId, queryContext.Query, context.CancellationToken)
                .GetAsyncEnumerator(context.CancellationToken);

            var hasCurrent = await enumerator.MoveNextAsync();
            while (hasCurrent)
            {
                var protoFeature = await CreateEnhancedFeatureAsync(
                    enumerator.Current,
                    queryContext.ReturnGeometry,
                    request.GeometryEncoding,
                    queryContext.GeometryLimits);
                batch.Add(protoFeature);

                if (batch.Count < _streamBatchSize)
                {
                    hasCurrent = await enumerator.MoveNextAsync();
                    continue;
                }

                hasCurrent = await enumerator.MoveNextAsync();
                var page = await CreateEnhancedPageAsync(
                    batch, layer, queryContext, pkField, isFirstPage, !hasCurrent, pageNumber);

                await responseStream.WriteAsync(page, context.CancellationToken);
                isFirstPage = false;
                pageNumber++;
                batch.Clear();
            }

            // Send final page
            if (batch.Any() || isFirstPage)
            {
                var lastPage = await CreateEnhancedPageAsync(
                    batch, layer, queryContext, pkField, isFirstPage, true, pageNumber);
                await responseStream.WriteAsync(lastPage, context.CancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in streaming query for service {ServiceId}, layer {LayerId}",
                request.ServiceId, request.LayerId);
            throw CreateRpcException(ex);
        }
    }

    #endregion

    #region Edit Operations

    public override async Task<Proto.ApplyEditsResponse> ApplyEdits(
        Proto.ApplyEditsRequest request,
        ServerCallContext context)
    {
        using var activity = CreateActivity("apply_edits_v2", request.ServiceId, request.LayerId);
        var startTime = DateTime.UtcNow;

        try
        {
            var validation = await ValidateResourceAsync(request.ServiceId, request.LayerId, context.CancellationToken);
            var (service, layer) = validation.Resource!;

            var editBatch = await CreateEnhancedEditBatchAsync(request);
            var result = await _featureWriter.ApplyEditsAsync(request.LayerId, editBatch, context.CancellationToken);

            var response = new Proto.ApplyEditsResponse
            {
                Summary = new Proto.EditSummary
                {
                    TotalEdits = request.Adds.Count + request.Updates.Count + request.Deletes.Count,
                    SuccessfulEdits = result.SuccessfulEdits.Count(),
                    FailedEdits = result.FailedEdits.Count(),
                    ServerTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                    NewGeneration = await GetNextGenerationAsync(request.LayerId)
                }
            };

            // Convert results with enhanced error information
            foreach (var addResult in result.AddResults)
            {
                response.AddResults.Add(CreateEnhancedEditResult(addResult));
            }

            foreach (var updateResult in result.UpdateResults)
            {
                response.UpdateResults.Add(CreateEnhancedEditResult(updateResult));
            }

            foreach (var deleteResult in result.DeleteResults)
            {
                response.DeleteResults.Add(CreateEnhancedEditResult(deleteResult));
            }

            return response;
        }
        catch (Exception ex)
        {
            return new Proto.ApplyEditsResponse
            {
                Error = CreateStructuredError(ex, context.GetHttpContext()?.TraceIdentifier)
            };
        }
    }

    public override async Task<IAsyncEnumerable<Proto.EditResults>> ApplyEditsStream(
        IAsyncEnumerable<Proto.EditBatch> requestStream,
        ServerCallContext context)
    {
        return ApplyEditsStreamInternal(requestStream, context);
    }

    private async IAsyncEnumerable<Proto.EditResults> ApplyEditsStreamInternal(
        IAsyncEnumerable<Proto.EditBatch> requestStream,
        ServerCallContext context)
    {
        using var activity = CreateActivity("apply_edits_stream_v2", null, 0);

        var batchCount = 0;
        await foreach (var editBatch in requestStream.WithCancellation(context.CancellationToken))
        {
            batchCount++;
            var startTime = DateTime.UtcNow;

            try
            {
                // Process each batch
                var editRequest = new Proto.ApplyEditsRequest
                {
                    ServiceId = "streaming", // Will be determined from first batch
                    LayerId = 0, // Will be determined from first batch
                    RollbackOnFailure = editBatch.RollbackOnFailure
                };

                editRequest.Adds.AddRange(editBatch.Adds);
                editRequest.Updates.AddRange(editBatch.Updates);
                editRequest.Deletes.AddRange(editBatch.Deletes);

                var response = await ApplyEdits(editRequest, context);

                yield return new Proto.EditResults
                {
                    BatchId = editBatch.BatchId,
                    AddResults = { response.AddResults },
                    UpdateResults = { response.UpdateResults },
                    DeleteResults = { response.DeleteResults },
                    Error = response.Error,
                    IsFinalResult = editBatch.IsFinalBatch
                };
            }
            catch (Exception ex)
            {
                yield return new Proto.EditResults
                {
                    BatchId = editBatch.BatchId,
                    Error = CreateStructuredError(ex, context.GetHttpContext()?.TraceIdentifier),
                    IsFinalResult = editBatch.IsFinalBatch
                };
            }
        }
    }

    #endregion

    #region Sync Operations

    public override async Task<Proto.SyncResponse> SyncFeatures(
        IAsyncEnumerable<Proto.SyncRequest> requestStream,
        IServerStreamWriter<Proto.SyncResponse> responseStream,
        ServerCallContext context)
    {
        using var activity = CreateActivity("sync_features_v2", null, 0);

        var syncSession = await _syncService.CreateSyncSessionAsync(context.CancellationToken);
        try
        {
            await foreach (var request in requestStream.WithCancellation(context.CancellationToken))
            {
                var response = await ProcessSyncRequestAsync(request, syncSession, context.CancellationToken);
                await responseStream.WriteAsync(response, context.CancellationToken);

                // If this was a completion request, break the loop
                if (request.SyncComplete != null)
                {
                    break;
                }
            }

            return new Proto.SyncResponse
            {
                Complete = new Proto.SyncComplete
                {
                    FinalGeneration = await syncSession.GetFinalGenerationAsync(),
                    ChangesApplied = syncSession.ChangesApplied,
                    ConflictsResolved = syncSession.ConflictsResolved,
                    CompletionTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
                }
            };
        }
        catch (Exception ex)
        {
            return new Proto.SyncResponse
            {
                Error = CreateStructuredError(ex, context.GetHttpContext()?.TraceIdentifier)
            };
        }
        finally
        {
            await syncSession.DisposeAsync();
        }
    }

    #endregion

    #region Metadata Operations

    public override async Task<Proto.ServiceMetadataResponse> GetServiceMetadata(
        Proto.ServiceMetadataRequest request,
        ServerCallContext context)
    {
        using var activity = CreateActivity("get_service_metadata_v2", request.ServiceId, 0);

        try
        {
            var validation = await _resourceValidator.ValidateServiceAsync(request.ServiceId, context.CancellationToken);
            if (!validation.IsValid)
            {
                return new Proto.ServiceMetadataResponse
                {
                    Error = new Proto.Error
                    {
                        Code = Proto.ErrorCode.ServiceUnavailable,
                        Message = validation.ErrorMessage ?? "Service not found"
                    }
                };
            }

            var service = validation.Resource!;
            var response = new Proto.ServiceMetadataResponse
            {
                ServiceInfo = new Proto.ServiceInfo
                {
                    ServiceId = service.Id,
                    Name = service.Name,
                    Description = service.Description ?? "",
                    DefaultSpatialReference = await CreateEnhancedSpatialReferenceAsync(service.SpatialReference),
                    Capabilities = new Proto.ServiceCapabilities
                    {
                        SupportsEditing = service.IsEditable,
                        SupportsStreaming = true,
                        SupportsSync = true,
                        MaxRecordCount = 1000,
                        MaxBatchSize = 100
                    }
                }
            };

            // Add supported encodings
            response.ServiceInfo.Capabilities.SupportedEncodings.AddRange(new[]
            {
                Proto.GeometryEncoding.Structured,
                Proto.GeometryEncoding.Wkb,
                Proto.GeometryEncoding.Wkt,
                Proto.GeometryEncoding.Geojson,
                Proto.GeometryEncoding.EsriShape
            });

            // Add layer information
            foreach (var layerId in request.LayerIds.DefaultIfEmpty(0))
            {
                var layerValidation = await _resourceValidator.ValidateServiceLayerAsync(
                    request.ServiceId, layerId, context.CancellationToken);

                if (layerValidation.IsValid)
                {
                    var (_, layer) = layerValidation.Resource!;
                    response.Layers.Add(await CreateLayerInfoAsync(layer));
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            return new Proto.ServiceMetadataResponse
            {
                Error = CreateStructuredError(ex, context.GetHttpContext()?.TraceIdentifier)
            };
        }
    }

    #endregion

    #region Helper Methods

    private async Task<ResourceValidationResult<(ServiceDefinition Service, LayerDefinition Layer)>>
        ValidateResourceAsync(string serviceId, int layerId, CancellationToken cancellationToken)
    {
        var validation = await _resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!validation.IsValid)
        {
            var statusCode = validation.ErrorCode == ResourceValidationError.NotFound
                ? StatusCode.NotFound
                : StatusCode.InvalidArgument;
            throw new RpcException(new Status(statusCode, validation.ErrorMessage ?? "Resource validation failed"));
        }

        var (service, layer) = validation.Resource!;
        EnsureGrpcEnabled(service);
        return validation;
    }

    private async Task<EnhancedQueryContext> CreateQueryContextAsync(
        Proto.QueryFeaturesRequest request,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        // Validate query filter if present
        if (request.Filter != null)
        {
            await ValidateQueryFilterAsync(request.Filter);
        }

        // Legacy where clause support
        if (!string.IsNullOrEmpty(request.Where))
        {
            var whereValidation = _queryValidator.ValidateWhereClause(request.Where);
            if (!whereValidation.IsValid)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    whereValidation.ErrorMessage ?? "Invalid where clause"));
            }
        }

        var query = await GrpcConversionHelpers.ToEnhancedFeatureQueryAsync(request, layer);

        // Apply mobile optimizations
        if (request.MobileOptions != null)
        {
            query = await _mobileOptimizationService.OptimizeQueryAsync(query, request.MobileOptions);
        }

        var responseSpatialReference = await ResolveSpatialReferenceAsync(
            request.OutputSpatialReference, layer.SpatialReference, cancellationToken);

        return new EnhancedQueryContext(
            query,
            responseSpatialReference,
            GrpcConversionHelpers.CreateEffectiveGeometryLimits(_geometryLimits, request),
            request.ReturnGeometry,
            UsedSpatialIndex: query.SpatialFilter.HasValue,
            SimplificationLevel: CalculateSimplificationLevel(request.LevelOfDetail));
    }

    private async Task<Proto.Feature> CreateEnhancedFeatureAsync(
        FeatureRecord feature,
        bool returnGeometry,
        Proto.GeometryEncoding encoding,
        GeometryLimits geometryLimits)
    {
        var protoFeature = new Proto.Feature
        {
            Id = feature.Id,
            Metadata = new Proto.FeatureMetadata
            {
                CreatedAt = feature.CreatedAt.HasValue
                    ? Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(feature.CreatedAt.Value)
                    : null,
                ModifiedAt = feature.ModifiedAt.HasValue
                    ? Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(feature.ModifiedAt.Value)
                    : null,
                Generation = feature.Generation ?? 0
            }
        };

        // Add attributes
        foreach (var attr in feature.Attributes)
        {
            protoFeature.Attributes[attr.Key] = GrpcConversionHelpers.ToProtoAttributeValue(attr.Value);
        }

        // Add geometry with specified encoding
        if (returnGeometry && feature.Geometry.HasValue)
        {
            protoFeature.Geometry = await _geometryEncodingService.EncodeGeometryAsync(
                feature.Geometry.Value, encoding, geometryLimits);
        }

        return protoFeature;
    }

    private async Task<Proto.FeaturePage> CreateEnhancedPageAsync(
        List<Proto.Feature> features,
        LayerDefinition layer,
        EnhancedQueryContext queryContext,
        string pkField,
        bool isFirstPage,
        bool isLastPage,
        int pageNumber)
    {
        var page = new Proto.FeaturePage
        {
            IsLastPage = isLastPage,
            PageNumber = pageNumber,
            PageSize = features.Count,
            Metadata = new Proto.QueryMetadata
            {
                ExecutionTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                UsedSpatialIndex = queryContext.UsedSpatialIndex,
                GeometrySimplificationLevel = queryContext.SimplificationLevel
            }
        };

        if (isFirstPage)
        {
            page.ObjectIdFieldName = pkField;
            page.GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType);
            page.SpatialReference = await CreateEnhancedSpatialReferenceAsync(queryContext.ResponseSpatialReference);

            foreach (var field in layer.AttributeFields)
            {
                page.Fields.Add(GrpcConversionHelpers.ToProtoField(field));
            }
        }

        page.Features.AddRange(features);
        return page;
    }

    private async Task<Proto.SpatialReference> CreateEnhancedSpatialReferenceAsync(SpatialReference spatialReference)
    {
        var proto = GrpcConversionHelpers.ToProtoSpatialReference(spatialReference);

        // Enhance with additional metadata
        var enhanced = new Proto.SpatialReference
        {
            Wkid = proto.Wkid,
            LatestWkid = proto.LatestWkid,
            Wkt = proto.Wkt,
            AuthorityCode = $"EPSG:{proto.Wkid}",
            Type = DetermineCoordinateSystemType(spatialReference),
            DisplayName = await GetSpatialReferenceDisplayNameAsync(spatialReference)
        };

        // Add bounds for validation and optimization
        enhanced.Bounds = CreateGeographicBounds(spatialReference);
        enhanced.LinearUnitScale = GetLinearUnitScale(spatialReference);
        enhanced.AngularUnitScale = GetAngularUnitScale(spatialReference);

        return enhanced;
    }

    private static Proto.CoordinateSystemType DetermineCoordinateSystemType(SpatialReference sr)
    {
        // This would be enhanced with proper coordinate system analysis
        return sr.IsGeographic() ? Proto.CoordinateSystemType.Geographic : Proto.CoordinateSystemType.Projected;
    }

    private async Task<string> GetSpatialReferenceDisplayNameAsync(SpatialReference spatialReference)
    {
        // This would lookup friendly names for common coordinate systems
        return spatialReference.Wkid switch
        {
            4326 => "WGS 84",
            3857 => "WGS 84 / Pseudo-Mercator",
            _ => $"EPSG:{spatialReference.Wkid}"
        };
    }

    private static Proto.GeographicBounds CreateGeographicBounds(SpatialReference spatialReference)
    {
        // This would be enhanced with proper bounds calculation
        return new Proto.GeographicBounds
        {
            WestLongitude = -180,
            EastLongitude = 180,
            SouthLatitude = -90,
            NorthLatitude = 90
        };
    }

    private static double GetLinearUnitScale(SpatialReference spatialReference)
    {
        // Return scale factor to convert to meters
        return 1.0; // Default to meters
    }

    private static double GetAngularUnitScale(SpatialReference spatialReference)
    {
        // Return scale factor to convert to radians
        return Math.PI / 180.0; // Default to degrees
    }

    private static Proto.Error CreateStructuredError(Exception ex, string? requestId)
    {
        var errorCode = ex switch
        {
            ArgumentException => Proto.ErrorCode.InvalidParameters,
            UnauthorizedAccessException => Proto.ErrorCode.AuthenticationError,
            TimeoutException => Proto.ErrorCode.Timeout,
            _ => Proto.ErrorCode.ServiceUnavailable
        };

        var error = new Proto.Error
        {
            Code = errorCode,
            Message = ex.Message,
            RequestId = requestId,
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
        };

        // Add specific error details
        if (ex is ArgumentException argEx && !string.IsNullOrEmpty(argEx.ParamName))
        {
            error.Details.Add(new Proto.ErrorDetail
            {
                FieldName = argEx.ParamName,
                Violation = "invalid_value",
                Description = argEx.Message,
                HelpUrl = "https://docs.honua.com/api/errors"
            });
        }

        return error;
    }

    private static RpcException CreateRpcException(Exception ex)
    {
        var statusCode = ex switch
        {
            ArgumentException => StatusCode.InvalidArgument,
            UnauthorizedAccessException => StatusCode.Unauthenticated,
            TimeoutException => StatusCode.DeadlineExceeded,
            _ => StatusCode.Internal
        };

        return new RpcException(new Status(statusCode, ex.Message));
    }

    private static System.Diagnostics.Activity? CreateActivity(string operationName, string? serviceId, int layerId)
    {
        var activity = System.Diagnostics.Activity.Current;
        if (activity != null)
        {
            activity.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.GrpcV2);
            activity.SetTag(HonuaTelemetry.Tags.Operation, operationName);
            if (!string.IsNullOrEmpty(serviceId))
            {
                activity.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            }
            activity.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString());
        }
        return activity;
    }

    // Additional helper methods would be implemented here...
    // Including geometry encoding, mobile optimization, sync processing, etc.

    private readonly record struct EnhancedQueryContext(
        FeatureQuery Query,
        SpatialReference ResponseSpatialReference,
        GeometryLimits GeometryLimits,
        bool ReturnGeometry,
        bool UsedSpatialIndex,
        int SimplificationLevel);

    #endregion
}
