// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Converters;
using Microsoft.Extensions.Logging;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Core.Transport.Clients;

/// <summary>
/// Generic gRPC client implementation for feature services.
/// Supports different context types for various platforms (server, mobile, etc.).
/// </summary>
/// <typeparam name="TContext">Platform-specific context type</typeparam>
public class GrpcFeatureServiceClient<TContext> : IFeatureServiceClient<TContext>
{
    private readonly Func<TContext, Geospatial.V1.FeatureService.FeatureServiceClient> _clientFactory;
    private readonly ILogger<GrpcFeatureServiceClient<TContext>>? _logger;
    private readonly GrpcClientOptions _options;

    /// <summary>
    /// Initializes a new instance of the GrpcFeatureServiceClient.
    /// </summary>
    /// <param name="clientFactory">Factory function to create gRPC clients from context</param>
    /// <param name="options">Client configuration options</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public GrpcFeatureServiceClient(
        Func<TContext, Geospatial.V1.FeatureService.FeatureServiceClient> clientFactory,
        GrpcClientOptions? options = null,
        ILogger<GrpcFeatureServiceClient<TContext>>? logger = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger;
        _options = options ?? new GrpcClientOptions();
    }

    /// <inheritdoc />
    public async Task<Features.FeatureStore.Domain.QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        TContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_logger is not null)
            {
                GrpcFeatureServiceClientLog.LogExecutingFeatureQuery(_logger, serviceId, layerId);
            }

            var client = _clientFactory(context);
            var request = FeatureConverter.ToGrpcRequest(query, serviceId, layerId);

            var response = await ExecuteWithRetryAsync<Geospatial.V1.QueryFeaturesResponse>(
                () => client.QueryFeaturesAsync(request, deadline: DateTime.UtcNow.Add(_options.RequestTimeout), cancellationToken: cancellationToken).ResponseAsync,
                cancellationToken);

            var result = FeatureConverter.FromGrpcResponse(response);

            if (_logger is not null)
            {
                GrpcFeatureServiceClientLog.LogFeatureQueryReturned(_logger, result.Items.Length);
            }

            return result;
        }
        catch (RpcException ex)
        {
            if (_logger is not null)
            {
                GrpcFeatureServiceClientLog.LogFeatureQueryRpcError(_logger, ex.StatusCode, ex.Message, ex);
            }

            throw new FeatureServiceException($"Feature query failed: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                GrpcFeatureServiceClientLog.LogUnexpectedFeatureQueryError(_logger, ex);
            }

            throw new FeatureServiceException($"Feature query failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        TContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = _clientFactory(context);
        var request = FeatureConverter.ToGrpcRequest(query, serviceId, layerId);

        if (_logger is not null)
        {
            GrpcFeatureServiceClientLog.LogStartingStreamingFeatureQuery(_logger, serviceId, layerId);
        }

        using var call = client.QueryFeaturesStream(request, deadline: DateTime.UtcNow.Add(_options.StreamTimeout), cancellationToken: cancellationToken);

        var isFirstPage = true;
        await foreach (var grpcPage in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            FeaturePage page;
            try
            {
                page = new FeaturePage
                {
                    Features = ConvertFeatures(grpcPage.Features),
                    IsLastPage = grpcPage.IsLastPage
                };

                // Metadata is only provided on the first page
                if (isFirstPage && HasMetadata(grpcPage))
                {
                    page.Metadata = new PageMetadata
                    {
                        ObjectIdFieldName = grpcPage.ObjectIdFieldName,
                        GeometryType = grpcPage.GeometryType.ToString(),
                        SpatialReference = grpcPage.SpatialReference != null
                            ? SpatialReferenceConverter.FromGrpc(grpcPage.SpatialReference)
                            : null,
                        Fields = ConvertFieldDefinitions(grpcPage.Fields)
                    };
                    isFirstPage = false;
                }

                if (_logger is not null)
                {
                    GrpcFeatureServiceClientLog.LogReceivedStreamingPage(_logger, page.Features.Length, page.IsLastPage);
                }
            }
            catch (RpcException ex)
            {
                if (_logger is not null)
                {
                    GrpcFeatureServiceClientLog.LogStreamingFeatureQueryRpcError(_logger, ex.StatusCode, ex.Message, ex);
                }

                throw new FeatureServiceException($"Streaming feature query failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                if (_logger is not null)
                {
                    GrpcFeatureServiceClientLog.LogUnexpectedStreamingFeatureQueryError(_logger, ex);
                }

                throw new FeatureServiceException($"Streaming feature query failed: {ex.Message}", ex);
            }

            yield return page;

            if (page.IsLastPage)
                break;
        }

        if (_logger is not null)
        {
            GrpcFeatureServiceClientLog.LogStreamingFeatureQueryCompleted(_logger);
        }
    }

    /// <inheritdoc />
    public async Task<EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        TContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_logger is not null)
            {
                GrpcFeatureServiceClientLog.LogApplyingEdits(
                    _logger,
                    serviceId,
                    layerId,
                    edits.Adds.Length,
                    edits.Updates.Length,
                    edits.Deletes.Length);
            }

            var client = _clientFactory(context);
            var request = new Geospatial.V1.ApplyEditsRequest
            {
                ServiceId = serviceId,
                LayerId = layerId,
                RollbackOnFailure = edits.RollbackOnFailure,
                ForceWrite = edits.ForceWrite
            };

            // Convert features to gRPC messages
            request.Adds.AddRange(FeatureConverter.ToGrpcFeatures(edits.Adds));
            request.Updates.AddRange(FeatureConverter.ToGrpcFeatures(edits.Updates));
            request.Deletes.AddRange(edits.Deletes);

            var response = await ExecuteWithRetryAsync<Geospatial.V1.ApplyEditsResponse>(
                () => client.ApplyEditsAsync(request, deadline: DateTime.UtcNow.Add(_options.RequestTimeout), cancellationToken: cancellationToken).ResponseAsync,
                cancellationToken);

            var result = ConvertEditResponse(response);

            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                var successfulAdds = CountSuccessful(result.AddResults);
                var successfulUpdates = CountSuccessful(result.UpdateResults);
                var successfulDeletes = CountSuccessful(result.DeleteResults);

                GrpcFeatureServiceClientLog.LogApplyEditsCompleted(
                    _logger,
                    successfulAdds,
                    result.AddResults.Length,
                    successfulUpdates,
                    result.UpdateResults.Length,
                    successfulDeletes,
                    result.DeleteResults.Length);
            }

            return result;
        }
        catch (RpcException ex)
        {
            if (_logger is not null)
            {
                GrpcFeatureServiceClientLog.LogApplyEditsRpcError(_logger, ex.StatusCode, ex.Message, ex);
            }

            throw new FeatureServiceException($"Apply edits failed: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                GrpcFeatureServiceClientLog.LogUnexpectedApplyEditsError(_logger, ex);
            }

            throw new FeatureServiceException($"Apply edits failed: {ex.Message}", ex);
        }
    }

    #region Private Methods

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        var retryCount = 0;

        while (retryCount < _options.MaxRetries)
        {
            try
            {
                return await operation();
            }
            catch (RpcException ex) when (IsRetriableError(ex) && retryCount < _options.MaxRetries - 1)
            {
                retryCount++;
                var delay = TimeSpan.FromMilliseconds(_options.BaseRetryDelayMs * Math.Pow(2, retryCount - 1));

                if (_logger is not null)
                {
                    GrpcFeatureServiceClientLog.LogRetryingOperation(
                        _logger,
                        ex.StatusCode,
                        retryCount + 1,
                        _options.MaxRetries,
                        delay.TotalMilliseconds);
                }

                await Task.Delay(delay, cancellationToken);
            }
        }

        // Final attempt without retry
        return await operation();
    }

    private static bool IsRetriableError(RpcException ex)
    {
        return ex.StatusCode switch
        {
            StatusCode.Unavailable => true,
            StatusCode.DeadlineExceeded => true,
            StatusCode.Internal => true,
            StatusCode.Aborted => true,
            _ => false
        };
    }

    private static int CountSuccessful(ImmutableArray<OperationResult> results)
    {
        var successCount = 0;

        foreach (var result in results)
        {
            if (result.Success)
            {
                successCount++;
            }
        }

        return successCount;
    }

    private static ImmutableArray<DomainFeature> ConvertFeatures(IEnumerable<Geospatial.V1.Feature> grpcFeatures)
    {
        var features = new List<DomainFeature>();

        foreach (var grpcFeature in grpcFeatures)
        {
            byte[]? geometryWkb = null;
            if (grpcFeature.Geometry != null)
            {
                // Convert gRPC geometry to NTS geometry, then to WKB
                var ntsGeometry = GeometryConverter.FromGrpc(grpcFeature.Geometry);
                geometryWkb = GeometryConverter.ToWkb(ntsGeometry);
            }

            var feature = DomainFeature.Create(
                id: grpcFeature.Id,
                geometry: geometryWkb,
                attributes: AttributeConverter.FromGrpc(grpcFeature.Attributes));

            features.Add(feature);
        }

        return features.ToImmutableArray();
    }

    private static bool HasMetadata(Geospatial.V1.FeaturePage grpcPage)
    {
        return !string.IsNullOrEmpty(grpcPage.ObjectIdFieldName) ||
               grpcPage.GeometryType != Geospatial.V1.GeometryType.Unspecified ||
               grpcPage.SpatialReference != null ||
               grpcPage.Fields.Count > 0;
    }

    private static ImmutableArray<FieldDefinition> ConvertFieldDefinitions(IEnumerable<Geospatial.V1.FieldDefinition> grpcFields)
    {
        var fields = new List<FieldDefinition>();

        foreach (var grpcField in grpcFields)
        {
            var field = new FieldDefinition
            {
                Name = grpcField.Name,
                FieldType = grpcField.FieldType.ToString(),
                Length = grpcField.Length > 0 ? grpcField.Length : null,
                Nullable = grpcField.Nullable,
                Alias = null // gRPC FieldDefinition doesn't have Alias property
            };

            fields.Add(field);
        }

        return fields.ToImmutableArray();
    }

    private static EditResult ConvertEditResponse(Geospatial.V1.ApplyEditsResponse response)
    {
        return new EditResult
        {
            AddResults = ConvertOperationResults(response.AddResults),
            UpdateResults = ConvertOperationResults(response.UpdateResults),
            DeleteResults = ConvertOperationResults(response.DeleteResults),
            Error = response.Error != null ? ConvertEditError(response.Error) : null
        };
    }

    private static ImmutableArray<OperationResult> ConvertOperationResults(IEnumerable<Geospatial.V1.EditResult> grpcResults)
    {
        var results = new List<OperationResult>();

        foreach (var grpcResult in grpcResults)
        {
            var result = new OperationResult
            {
                ObjectId = grpcResult.ObjectId,
                Success = grpcResult.Success,
                Error = grpcResult.Error != null ? ConvertEditError(grpcResult.Error) : null
            };

            results.Add(result);
        }

        return results.ToImmutableArray();
    }

    private static EditError ConvertEditError(Geospatial.V1.EditError grpcError)
    {
        return new EditError
        {
            Code = grpcError.Code,
            Message = grpcError.Message
        };
    }

    #endregion
}

/// <summary>
/// Configuration options for gRPC feature service clients.
/// </summary>
public class GrpcClientOptions
{
    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds between retry attempts.
    /// </summary>
    public int BaseRetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Timeout for individual requests.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for streaming operations.
    /// </summary>
    public TimeSpan StreamTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Exception thrown when feature service operations fail.
/// </summary>
public class FeatureServiceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureServiceException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public FeatureServiceException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureServiceException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception that caused the failure.</param>
    public FeatureServiceException(string message, Exception innerException) : base(message, innerException) { }
}

internal static partial class GrpcFeatureServiceClientLog
{
    [LoggerMessage(EventId = 7600, Level = LogLevel.Debug, Message = "Executing feature query for service {ServiceId}, layer {LayerId}")]
    public static partial void LogExecutingFeatureQuery(ILogger logger, string serviceId, int layerId);

    [LoggerMessage(EventId = 7601, Level = LogLevel.Debug, Message = "Feature query returned {FeatureCount} features")]
    public static partial void LogFeatureQueryReturned(ILogger logger, int featureCount);

    [LoggerMessage(EventId = 7602, Level = LogLevel.Error, Message = "gRPC error during feature query: {StatusCode} - {Message}")]
    public static partial void LogFeatureQueryRpcError(ILogger logger, StatusCode statusCode, string message, Exception exception);

    [LoggerMessage(EventId = 7603, Level = LogLevel.Error, Message = "Unexpected error during feature query")]
    public static partial void LogUnexpectedFeatureQueryError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7604, Level = LogLevel.Debug, Message = "Starting streaming feature query for service {ServiceId}, layer {LayerId}")]
    public static partial void LogStartingStreamingFeatureQuery(ILogger logger, string serviceId, int layerId);

    [LoggerMessage(EventId = 7605, Level = LogLevel.Trace, Message = "Received page with {FeatureCount} features, IsLastPage: {IsLastPage}")]
    public static partial void LogReceivedStreamingPage(ILogger logger, int featureCount, bool isLastPage);

    [LoggerMessage(EventId = 7606, Level = LogLevel.Error, Message = "gRPC error during streaming feature query: {StatusCode} - {Message}")]
    public static partial void LogStreamingFeatureQueryRpcError(ILogger logger, StatusCode statusCode, string message, Exception exception);

    [LoggerMessage(EventId = 7607, Level = LogLevel.Error, Message = "Unexpected error during streaming feature query")]
    public static partial void LogUnexpectedStreamingFeatureQueryError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7608, Level = LogLevel.Debug, Message = "Streaming feature query completed")]
    public static partial void LogStreamingFeatureQueryCompleted(ILogger logger);

    [LoggerMessage(EventId = 7609, Level = LogLevel.Debug, Message = "Applying edits for service {ServiceId}, layer {LayerId}: {AddCount} adds, {UpdateCount} updates, {DeleteCount} deletes")]
    public static partial void LogApplyingEdits(ILogger logger, string serviceId, int layerId, int addCount, int updateCount, int deleteCount);

    [LoggerMessage(EventId = 7610, Level = LogLevel.Debug, Message = "Edit operation completed with {SuccessfulAdds}/{TotalAdds} successful adds, {SuccessfulUpdates}/{TotalUpdates} successful updates, {SuccessfulDeletes}/{TotalDeletes} successful deletes")]
    public static partial void LogApplyEditsCompleted(
        ILogger logger,
        int successfulAdds,
        int totalAdds,
        int successfulUpdates,
        int totalUpdates,
        int successfulDeletes,
        int totalDeletes);

    [LoggerMessage(EventId = 7611, Level = LogLevel.Error, Message = "gRPC error during apply edits: {StatusCode} - {Message}")]
    public static partial void LogApplyEditsRpcError(ILogger logger, StatusCode statusCode, string message, Exception exception);

    [LoggerMessage(EventId = 7612, Level = LogLevel.Error, Message = "Unexpected error during apply edits")]
    public static partial void LogUnexpectedApplyEditsError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7613, Level = LogLevel.Warning, Message = "Retrying operation after {StatusCode} error (attempt {Attempt}/{MaxAttempts}), waiting {Delay}ms")]
    public static partial void LogRetryingOperation(ILogger logger, StatusCode statusCode, int attempt, int maxAttempts, double delay);
}
