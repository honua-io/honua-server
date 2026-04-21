// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Query;

/// <summary>
/// Unified query service that coordinates query processing across all protocols.
/// Provides a single entry point for all query operations with protocol-specific adapters.
/// </summary>
public sealed class UnifiedQueryService
{
    private readonly IQueryProcessor _queryProcessor;
    private readonly IFeatureReader _featureReader;
    private readonly ILogger<UnifiedQueryService> _logger;
    private readonly ConcurrentDictionary<Type, object> _adapters = new();

    public UnifiedQueryService(
        IQueryProcessor queryProcessor,
        IFeatureReader featureReader,
        ILogger<UnifiedQueryService> logger)
    {
        _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers a protocol-specific query parameter adapter.
    /// </summary>
    /// <typeparam name="TParams">Protocol parameter type</typeparam>
    /// <param name="adapter">Parameter adapter instance</param>
    public void RegisterAdapter<TParams>(IQueryParameterAdapter<TParams> adapter)
    {
        _adapters[typeof(TParams)] = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger.LogInformation("Registered query adapter for protocol {Protocol} with parameter type {ParamType}",
            adapter.ProtocolName, typeof(TParams).Name);
    }

    /// <summary>
    /// Executes a unified query using protocol-specific parameters.
    /// </summary>
    /// <typeparam name="TParams">Protocol parameter type</typeparam>
    /// <param name="parameters">Protocol-specific parameters</param>
    /// <param name="layer">Target layer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified query result</returns>
    public async Task<UnifiedQueryResult> ExecuteQueryAsync<TParams>(
        TParams parameters,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get the adapter for this parameter type
            if (!_adapters.TryGetValue(typeof(TParams), out var adapterObj) ||
                adapterObj is not IQueryParameterAdapter<TParams> adapter)
            {
                return UnifiedQueryResult.Failure($"No adapter registered for parameter type {typeof(TParams).Name}");
            }

            _logger.LogDebug("Converting {Protocol} parameters to unified query for layer {LayerId}",
                adapter.ProtocolName, layer.Id);

            // Convert protocol parameters to unified query
            var conversionResult = await adapter.ConvertAsync(parameters, layer, cancellationToken);
            if (!conversionResult.IsSuccess)
            {
                return UnifiedQueryResult.Failure(conversionResult.ErrorMessage!);
            }

            var unifiedQuery = conversionResult.Query!.Value;

            // Validate the unified query
            var validationResult = _queryProcessor.ValidateQuery(unifiedQuery, layer);
            if (!validationResult.IsValid)
            {
                return UnifiedQueryResult.Failure(validationResult.ErrorMessage!);
            }

            // Optimize the query
            var optimizedQuery = _queryProcessor.OptimizeQuery(unifiedQuery, layer);

            // Convert to feature query for data access
            var featureQuery = _queryProcessor.ToFeatureQuery(optimizedQuery, layer);

            _logger.LogDebug("Executing unified query for layer {LayerId} with protocol {Protocol}",
                layer.Id, adapter.ProtocolName);

            // Execute the query
            var result = await _featureReader.QueryAsync(layer.Id, featureQuery, cancellationToken);

            // Create successful result
            return UnifiedQueryResult.Success(
                result,
                optimizedQuery,
                adapter.ProtocolName,
                conversionResult.Metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute unified query for layer {LayerId}", layer.Id);
            return UnifiedQueryResult.Failure("An error occurred while executing the query.");
        }
    }

    /// <summary>
    /// Executes a count query using protocol-specific parameters.
    /// </summary>
    /// <typeparam name="TParams">Protocol parameter type</typeparam>
    /// <param name="parameters">Protocol-specific parameters</param>
    /// <param name="layer">Target layer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count result</returns>
    public async Task<UnifiedCountResult> ExecuteCountAsync<TParams>(
        TParams parameters,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get the adapter for this parameter type
            if (!_adapters.TryGetValue(typeof(TParams), out var adapterObj) ||
                adapterObj is not IQueryParameterAdapter<TParams> adapter)
            {
                return UnifiedCountResult.Failure($"No adapter registered for parameter type {typeof(TParams).Name}");
            }

            // Convert protocol parameters to unified query
            var conversionResult = await adapter.ConvertAsync(parameters, layer, cancellationToken);
            if (!conversionResult.IsSuccess)
            {
                return UnifiedCountResult.Failure(conversionResult.ErrorMessage!);
            }

            var unifiedQuery = conversionResult.Query!.Value;

            // Remove pagination for count queries
            var countQuery = unifiedQuery with { Offset = null, Limit = null, OrderBy = null };

            // Validate the unified query
            var validationResult = _queryProcessor.ValidateQuery(countQuery, layer);
            if (!validationResult.IsValid)
            {
                return UnifiedCountResult.Failure(validationResult.ErrorMessage!);
            }

            // Convert to feature query for data access
            var featureQuery = _queryProcessor.ToFeatureQuery(countQuery, layer);

            _logger.LogDebug("Executing count query for layer {LayerId} with protocol {Protocol}",
                layer.Id, adapter.ProtocolName);

            // Execute the count query
            var count = await _featureReader.CountAsync(layer.Id, featureQuery, cancellationToken);

            return UnifiedCountResult.Success(count, adapter.ProtocolName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute count query for layer {LayerId}", layer.Id);
            return UnifiedCountResult.Failure("An error occurred while executing the count query.");
        }
    }

    /// <summary>
    /// Builds a cache key for the given parameters and layer.
    /// </summary>
    /// <typeparam name="TParams">Protocol parameter type</typeparam>
    /// <param name="parameters">Protocol-specific parameters</param>
    /// <param name="layer">Target layer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cache key or null if caching not supported</returns>
    public async Task<string?> BuildCacheKeyAsync<TParams>(
        TParams parameters,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_adapters.TryGetValue(typeof(TParams), out var adapterObj) ||
                adapterObj is not IQueryParameterAdapter<TParams> adapter)
            {
                return null;
            }

            var conversionResult = await adapter.ConvertAsync(parameters, layer, cancellationToken);
            if (!conversionResult.IsSuccess)
            {
                return null;
            }

            return _queryProcessor.BuildCacheKey(conversionResult.Query!.Value, layer, adapter.ProtocolName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build cache key for layer {LayerId}", layer.Id);
            return null;
        }
    }

    /// <summary>
    /// Determines if streaming should be used for the given parameters.
    /// </summary>
    /// <typeparam name="TParams">Protocol parameter type</typeparam>
    /// <param name="parameters">Protocol-specific parameters</param>
    /// <param name="layer">Target layer</param>
    /// <param name="outputFormat">Output format</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if streaming should be used</returns>
    public async Task<bool> ShouldUseStreamingAsync<TParams>(
        TParams parameters,
        LayerDefinition layer,
        string outputFormat,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_adapters.TryGetValue(typeof(TParams), out var adapterObj) ||
                adapterObj is not IQueryParameterAdapter<TParams> adapter)
            {
                return false;
            }

            var conversionResult = await adapter.ConvertAsync(parameters, layer, cancellationToken);
            if (!conversionResult.IsSuccess)
            {
                return false;
            }

            return _queryProcessor.ShouldUseStreaming(conversionResult.Query!.Value, layer, outputFormat);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine streaming preference for layer {LayerId}", layer.Id);
            return false;
        }
    }

    /// <summary>
    /// Gets registered adapters for diagnostic purposes.
    /// </summary>
    /// <returns>Dictionary of registered adapters</returns>
    public IReadOnlyDictionary<Type, string> GetRegisteredAdapters()
    {
        return _adapters.ToDictionary(
            kvp => kvp.Key,
            kvp => ((dynamic)kvp.Value).ProtocolName) as IReadOnlyDictionary<Type, string>;
    }
}

/// <summary>
/// Result of unified query execution.
/// </summary>
public sealed record UnifiedQueryResult
{
    /// <summary>
    /// Whether the query execution succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Query result if execution succeeded.
    /// </summary>
    public QueryResult<Feature>? Result { get; init; }

    /// <summary>
    /// The unified query that was executed.
    /// </summary>
    public UnifiedQuery? Query { get; init; }

    /// <summary>
    /// Protocol that initiated the query.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Protocol-specific metadata for response formatting.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful query result.
    /// </summary>
    /// <param name="result">Query result</param>
    /// <param name="query">Unified query</param>
    /// <param name="protocol">Source protocol</param>
    /// <param name="metadata">Protocol metadata</param>
    /// <returns>Successful result</returns>
    public static UnifiedQueryResult Success(
        QueryResult<Feature> result,
        UnifiedQuery query,
        string protocol,
        IDictionary<string, object>? metadata = null)
        => new() { IsSuccess = true, Result = result, Query = query, Protocol = protocol, Metadata = metadata };

    /// <summary>
    /// Creates a failed query result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed result</returns>
    public static UnifiedQueryResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of unified count query execution.
/// </summary>
public sealed record UnifiedCountResult
{
    /// <summary>
    /// Whether the count query execution succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Count result if execution succeeded.
    /// </summary>
    public long? Count { get; init; }

    /// <summary>
    /// Protocol that initiated the query.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful count result.
    /// </summary>
    /// <param name="count">Count value</param>
    /// <param name="protocol">Source protocol</param>
    /// <returns>Successful result</returns>
    public static UnifiedCountResult Success(long count, string protocol)
        => new() { IsSuccess = true, Count = count, Protocol = protocol };

    /// <summary>
    /// Creates a failed count result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed result</returns>
    public static UnifiedCountResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}