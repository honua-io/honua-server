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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Honua.Core.Models;
using Honua.Core.Transport.Clients;
using Honua.Core.Transport.Converters;
using Honua.Core.Features.FeatureStore.Domain;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Api.Sdk.Clients;

/// <summary>
/// High-performance server-optimized client for Honua feature services.
/// Implements IFeatureServiceClient with ServerContext for server-to-server scenarios.
/// </summary>
public class HonuaApiClient : IFeatureServiceClient<ServerContext>, IDisposable
{
    private static readonly System.Diagnostics.ActivitySource ActivitySource = new("Honua.Api.Sdk");
    private readonly IFeatureServiceClient<ServerContext> _grpcClient;
    private readonly ILogger<HonuaApiClient> _logger;
    private readonly HonuaApiClientOptions _options;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the HonuaApiClient.
    /// </summary>
    /// <param name="grpcClient">Underlying gRPC client</param>
    /// <param name="options">Client configuration options</param>
    /// <param name="logger">Logger instance</param>
    public HonuaApiClient(
        IFeatureServiceClient<ServerContext> grpcClient,
        IOptions<HonuaApiClientOptions> options,
        ILogger<HonuaApiClient> logger)
    {
        _grpcClient = grpcClient ?? throw new ArgumentNullException(nameof(grpcClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes a feature query and returns all results in a single response.
    /// Optimized for server scenarios with connection pooling and retry policies.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Server context with cancellation and tracing</param>
    /// <param name="cancellationToken">Cancellation token (merged with context)</param>
    /// <returns>Query results with features</returns>
    public async Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        ServerContext context,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HonuaApiClient));

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var effectiveContext = new ServerContext
        {
            CancellationToken = combinedCts.Token,
            Activity = context.Activity,
            Headers = context.Headers,
            Timeout = context.Timeout,
            BypassCache = context.BypassCache,
            Priority = context.Priority
        };

        using var activity = CreateActivity("QueryFeatures", serviceId, layerId);

        try
        {
            _logger.LogDebug("Executing feature query for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);

            var result = await _grpcClient.QueryFeaturesAsync(
                serviceId, layerId, query, effectiveContext, combinedCts.Token);

            _logger.LogDebug("Query completed successfully with {FeatureCount} features",
                result.Items.Length);

            return result;
        }
        catch (OperationCanceledException) when (combinedCts.Token.IsCancellationRequested)
        {
            _logger.LogDebug("Query was cancelled for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query failed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Executes a feature query and streams results as pages.
    /// Optimized for large datasets with configurable page sizes and timeouts.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Server context with cancellation and tracing</param>
    /// <param name="cancellationToken">Cancellation token (merged with context)</param>
    /// <returns>Async enumerable of feature pages</returns>
    public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        ServerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HonuaApiClient));

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var effectiveContext = new ServerContext
        {
            CancellationToken = combinedCts.Token,
            Activity = context.Activity,
            Headers = context.Headers,
            Timeout = context.Timeout ?? _options.StreamingTimeout,
            BypassCache = context.BypassCache,
            Priority = context.Priority
        };

        using var activity = CreateActivity("QueryFeaturesStream", serviceId, layerId);

        _logger.LogDebug("Starting streaming query for service {ServiceId}, layer {LayerId}",
            serviceId, layerId);

        var pageCount = 0;
        await foreach (var page in _grpcClient.QueryFeaturesStreamAsync(
            serviceId, layerId, query, effectiveContext, combinedCts.Token))
        {
            pageCount++;
            _logger.LogTrace("Yielding page {PageNumber} with {FeatureCount} features",
                pageCount, page.Features.Length);

            yield return page;

            if (page.IsLastPage)
            {
                _logger.LogDebug("Streaming query completed with {PageCount} pages", pageCount);
                break;
            }
        }
    }

    /// <summary>
    /// Applies feature edits with optimized server-side batch processing.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="edits">Edit operations to apply</param>
    /// <param name="context">Server context with cancellation and tracing</param>
    /// <param name="cancellationToken">Cancellation token (merged with context)</param>
    /// <returns>Edit results with success/failure status for each operation</returns>
    public async Task<EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        ServerContext context,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HonuaApiClient));

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var effectiveContext = new ServerContext
        {
            CancellationToken = combinedCts.Token,
            Activity = context.Activity,
            Headers = context.Headers,
            Timeout = context.Timeout,
            BypassCache = context.BypassCache,
            Priority = context.Priority
        };

        using var activity = CreateActivity("ApplyEdits", serviceId, layerId);

        try
        {
            var totalOperations = edits.Adds.Length + edits.Updates.Length + edits.Deletes.Length;

            _logger.LogDebug("Applying {TotalOperations} edit operations for service {ServiceId}, layer {LayerId} " +
                           "(Adds: {AddCount}, Updates: {UpdateCount}, Deletes: {DeleteCount})",
                totalOperations, serviceId, layerId,
                edits.Adds.Length, edits.Updates.Length, edits.Deletes.Length);

            var result = await _grpcClient.ApplyEditsAsync(
                serviceId, layerId, edits, effectiveContext, combinedCts.Token);

            var successCount = result.AddResults.Count(r => r.Success) +
                             result.UpdateResults.Count(r => r.Success) +
                             result.DeleteResults.Count(r => r.Success);

            _logger.LogDebug("Edit operations completed: {SuccessCount}/{TotalOperations} successful",
                successCount, totalOperations);

            return result;
        }
        catch (OperationCanceledException) when (combinedCts.Token.IsCancellationRequested)
        {
            _logger.LogDebug("Edit operation was cancelled for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Edit operation failed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Convenience method for querying features with just a cancellation token.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results with features</returns>
    public Task<QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        return QueryFeaturesAsync(serviceId, layerId, query,
            ServerContext.WithCancellation(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Convenience method for streaming features with just a cancellation token.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of feature pages</returns>
    public IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        return QueryFeaturesStreamAsync(serviceId, layerId, query,
            ServerContext.WithCancellation(cancellationToken), cancellationToken);
    }

    private System.Diagnostics.Activity? CreateActivity(string operationName, string serviceId, int layerId)
    {
        var activity = ActivitySource.StartActivity($"HonuaApi.{operationName}");
        activity?.SetTag("service.id", serviceId);
        activity?.SetTag("layer.id", layerId.ToString());
        return activity;
    }

    /// <summary>
    /// Disposes the client and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_grpcClient is IDisposable disposableClient)
            {
                disposableClient.Dispose();
            }
            _disposed = true;
        }
    }
}