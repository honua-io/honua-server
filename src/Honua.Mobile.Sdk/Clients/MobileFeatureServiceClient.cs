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
using Honua.Core.Features.FeatureStore.Domain;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Clients;

/// <summary>
/// Mobile-optimized adapter that wraps standard gRPC clients from Honua.Core.Sdk
/// with mobile-specific context handling and optimizations.
/// </summary>
public class MobileFeatureServiceClient : IFeatureServiceClient<MobileContext>
{
    private readonly IFeatureServiceClient<object> _coreClient;
    private readonly ILogger<MobileFeatureServiceClient> _logger;
    private readonly HonuaMobileClientOptions _options;

    /// <summary>
    /// Initializes a new instance of the MobileFeatureServiceClient.
    /// </summary>
    /// <param name="coreClient">Core gRPC client from Honua.Core.Sdk</param>
    /// <param name="options">Mobile client configuration</param>
    /// <param name="logger">Logger instance</param>
    public MobileFeatureServiceClient(
        IFeatureServiceClient<object> coreClient,
        IOptions<HonuaMobileClientOptions> options,
        ILogger<MobileFeatureServiceClient> logger)
    {
        _coreClient = coreClient ?? throw new ArgumentNullException(nameof(coreClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes a feature query with mobile context conversion.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Mobile context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results with features</returns>
    public async Task<Features.FeatureStore.Domain.QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var token = combinedCts.Token;

        try
        {
            _logger.LogDebug("Executing mobile query for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);

            // Apply mobile-specific query optimizations
            var mobileOptimizedQuery = OptimizeQueryForMobile(query);

            // Convert mobile context to core context
            var coreContext = ConvertToCoreContext(context);

            // Execute query using core client
            var result = await _coreClient.QueryFeaturesAsync(
                serviceId, layerId, mobileOptimizedQuery, coreContext, token);

            _logger.LogDebug("Mobile query completed with {FeatureCount} features",
                result.Features.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mobile query failed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Executes a streaming feature query with mobile optimizations.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="query">Feature query parameters</param>
    /// <param name="context">Mobile context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of feature pages</returns>
    public async IAsyncEnumerable<FeaturePage> QueryFeaturesStreamAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        MobileContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var token = combinedCts.Token;

        try
        {
            _logger.LogDebug("Starting mobile streaming query for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);

            // Apply mobile-specific query optimizations
            var mobileOptimizedQuery = OptimizeQueryForMobile(query);

            // Convert mobile context to core context
            var coreContext = ConvertToCoreContext(context);

            var pageCount = 0;
            var totalFeatures = 0;

            // Stream from core client with mobile-specific buffering
            await foreach (var page in _coreClient.QueryFeaturesStreamAsync(
                serviceId, layerId, mobileOptimizedQuery, coreContext, token))
            {
                pageCount++;
                totalFeatures += page.Features.Length;

                _logger.LogDebug("Received page {PageCount} with {FeatureCount} features",
                    pageCount, page.Features.Length);

                // Report progress if context supports it
                context.ProgressReporter?.Report(SyncProgress.Step("Stream",
                    totalFeatures, totalFeatures + 100, // Estimate total
                    $"Streaming page {pageCount} ({page.Features.Length} features)"));

                yield return page;

                // Mobile-specific: Add small delays to prevent overwhelming the device
                if (pageCount % 3 == 0) // Every 3 pages for mobile (more frequent than desktop)
                {
                    await Task.Delay(30, token); // Brief pause
                }

                if (page.IsLastPage)
                {
                    _logger.LogDebug("Mobile streaming completed - {TotalFeatures} features in {PageCount} pages",
                        totalFeatures, pageCount);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mobile streaming query failed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Applies feature edits with mobile context conversion.
    /// </summary>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="edits">Edit operations to apply</param>
    /// <param name="context">Mobile context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Edit results</returns>
    public async Task<EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        FeatureEdits edits,
        MobileContext context,
        CancellationToken cancellationToken = default)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);

        var token = combinedCts.Token;

        try
        {
            var totalOperations = edits.Adds.Length + edits.Updates.Length + edits.Deletes.Length;
            _logger.LogDebug("Executing mobile edits for service {ServiceId}, layer {LayerId}: {TotalOperations} operations",
                serviceId, layerId, totalOperations);

            // Convert mobile context to core context
            var coreContext = ConvertToCoreContext(context);

            // Execute edits using core client
            var result = await _coreClient.ApplyEditsAsync(
                serviceId, layerId, edits, coreContext, token);

            _logger.LogDebug("Mobile edits completed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mobile edits failed for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw;
        }
    }

    /// <summary>
    /// Optimizes feature query for mobile environments.
    /// </summary>
    /// <param name="query">Original query</param>
    /// <returns>Mobile-optimized query</returns>
    private FeatureQuery OptimizeQueryForMobile(FeatureQuery query)
    {
        // Limit result count for mobile to prevent memory issues
        var optimizedRecordCount = query.ResultRecordCount.HasValue
            ? Math.Min(query.ResultRecordCount.Value, _options.MobilePageSize)
            : _options.MobilePageSize;

        // Ensure we don't request too much data at once on mobile
        return query with
        {
            ResultRecordCount = optimizedRecordCount,
            // Add mobile-specific optimizations here
            // For example: prefer lighter geometry types, limit field selection, etc.
        };
    }

    /// <summary>
    /// Converts mobile context to core context for gRPC client usage.
    /// </summary>
    /// <param name="mobileContext">Mobile context</param>
    /// <returns>Core context</returns>
    private object ConvertToCoreContext(MobileContext mobileContext)
    {
        // Create a dictionary-based context that core clients can understand
        var coreContext = new Dictionary<string, object>();

        if (mobileContext.Headers != null)
        {
            coreContext["headers"] = mobileContext.Headers;
        }

        if (mobileContext.Timeout.HasValue)
        {
            coreContext["timeout"] = mobileContext.Timeout.Value;
        }

        // Add mobile-specific context information
        coreContext["mobile"] = true;
        coreContext["priority"] = mobileContext.Priority.ToString().ToLowerInvariant();
        coreContext["networkPolicy"] = mobileContext.NetworkPolicy.ToString().ToLowerInvariant();
        coreContext["batteryPolicy"] = mobileContext.BatteryPolicy.ToString().ToLowerInvariant();

        return coreContext;
    }

    /// <summary>
    /// Convenience method for querying features without context.
    /// </summary>
    public Task<Features.FeatureStore.Domain.QueryResult<DomainFeature>> QueryFeaturesAsync(
        string serviceId,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = new MobileContext { CancellationToken = cancellationToken };
        return QueryFeaturesAsync(serviceId, layerId, query, context, cancellationToken);
    }
}