// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Unified edit service that coordinates edit processing across all protocols.
/// Provides a single entry point for all edit operations with protocol-specific adapters.
/// </summary>
public sealed class UnifiedEditService
{
    private readonly IEditProcessor _editProcessor;
    private readonly IFeatureWriter _featureWriter;
    private readonly ILogger<UnifiedEditService> _logger;
    private readonly ConcurrentDictionary<Type, object> _adapters = new();
    private readonly ConcurrentDictionary<Type, string> _adapterProtocolNames = new();

    public UnifiedEditService(
        IEditProcessor editProcessor,
        IFeatureWriter featureWriter,
        ILogger<UnifiedEditService> logger)
    {
        _editProcessor = editProcessor ?? throw new ArgumentNullException(nameof(editProcessor));
        _featureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers a protocol-specific edit parameter adapter.
    /// </summary>
    /// <typeparam name="TRequest">Protocol edit request type</typeparam>
    /// <param name="adapter">Parameter adapter instance</param>
    public void RegisterAdapter<TRequest>(IEditParameterAdapter<TRequest> adapter)
    {
        _adapters[typeof(TRequest)] = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _adapterProtocolNames[typeof(TRequest)] = adapter.ProtocolName;
        EditLog.RegisteredEditAdapter(_logger, adapter.ProtocolName, typeof(TRequest).Name);
    }

    /// <summary>
    /// Executes a unified edit operation using protocol-specific parameters.
    /// </summary>
    /// <typeparam name="TRequest">Protocol edit request type</typeparam>
    /// <param name="protocolRequest">Protocol-specific edit request</param>
    /// <param name="layer">Target layer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified edit result</returns>
    public async Task<UnifiedEditResult> ExecuteEditAsync<TRequest>(
        TRequest protocolRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get the adapter for this request type
            if (!_adapters.TryGetValue(typeof(TRequest), out var adapterObj) ||
                adapterObj is not IEditParameterAdapter<TRequest> adapter)
            {
                return UnifiedEditResult.Failure($"No adapter registered for request type {typeof(TRequest).Name}");
            }

            EditLog.ConvertingEditRequest(_logger, adapter.ProtocolName, layer.Id);

            // Convert protocol request to unified edit
            var conversionResult = await adapter.ConvertAsync(protocolRequest, layer, cancellationToken);
            if (!conversionResult.IsSuccess)
            {
                return UnifiedEditResult.Failure(conversionResult.ErrorMessage!);
            }

            var unifiedRequest = conversionResult.EditRequest!.Value;
            var transaction = conversionResult.Transaction!.Value;

            // Validate the unified edit request
            var editValidationResult = _editProcessor.ValidateEdit(unifiedRequest, layer);
            if (!editValidationResult.IsValid)
            {
                return UnifiedEditResult.Failure(editValidationResult.ErrorMessage!);
            }

            // Validate transaction semantics
            var transactionValidationResult = _editProcessor.ValidateTransaction(transaction, layer);
            if (!transactionValidationResult.IsValid)
            {
                return UnifiedEditResult.Failure(transactionValidationResult.ErrorMessage!);
            }

            // Optimize the edit request
            var optimizedRequest = _editProcessor.OptimizeEdit(unifiedRequest, layer);

            // Convert to feature edit batch for data access
            var editBatch = _editProcessor.ToFeatureEditBatch(optimizedRequest, layer);

            EditLog.ExecutingUnifiedEdit(
                _logger,
                layer.Id,
                adapter.ProtocolName,
                editBatch.Creates.Length,
                editBatch.Updates.Length,
                editBatch.Deletes.Length);

            // Execute the edit operation
            var editResult = await _featureWriter.ApplyEditsAsync(layer.Id, editBatch, cancellationToken);

            // Create successful result
            return UnifiedEditResult.Success(
                editResult,
                optimizedRequest,
                transaction,
                adapter.ProtocolName,
                conversionResult.Metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            EditLog.ExecuteUnifiedEditFailed(_logger, layer.Id, ex);
            return UnifiedEditResult.Failure("An error occurred while executing the edit operation.");
        }
    }

    /// <summary>
    /// Executes a batch edit operation with transaction semantics.
    /// </summary>
    /// <typeparam name="TRequest">Protocol edit request type</typeparam>
    /// <param name="protocolRequests">Protocol-specific edit requests</param>
    /// <param name="layer">Target layer</param>
    /// <param name="transactionConfiguration">Transaction configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Unified batch edit result</returns>
    public async Task<UnifiedBatchEditResult> ExecuteBatchEditAsync<TRequest>(
        IEnumerable<TRequest> protocolRequests,
        LayerDefinition layer,
        TransactionConfiguration transactionConfiguration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get the adapter for this request type
            if (!_adapters.TryGetValue(typeof(TRequest), out var adapterObj) ||
                adapterObj is not IEditParameterAdapter<TRequest> adapter)
            {
                return UnifiedBatchEditResult.Failure($"No adapter registered for request type {typeof(TRequest).Name}");
            }

            var requests = protocolRequests.ToList();
            EditLog.ExecutingBatchEdit(_logger, adapter.ProtocolName, requests.Count, layer.Id);

            var unifiedRequests = new List<UnifiedEditRequest>();
            var allMetadata = new Dictionary<string, object>();

            // Convert all protocol requests
            foreach (var request in requests)
            {
                var conversionResult = await adapter.ConvertAsync(request, layer, cancellationToken);
                if (!conversionResult.IsSuccess)
                {
                    return UnifiedBatchEditResult.Failure(conversionResult.ErrorMessage!);
                }

                unifiedRequests.Add(conversionResult.EditRequest!.Value);

                if (conversionResult.Metadata != null)
                {
                    foreach (var kvp in conversionResult.Metadata)
                    {
                        allMetadata[$"request_{unifiedRequests.Count - 1}_{kvp.Key}"] = kvp.Value;
                    }
                }
            }

            // Create batch transaction
            var transactionId = Guid.NewGuid().ToString();
            var transaction = EditTransaction.Create(
                transactionId,
                adapter.ProtocolName,
                unifiedRequests.ToImmutableArray(),
                transactionConfiguration);

            // Execute batch with transaction semantics
            return await ExecuteBatchTransactionAsync(transaction, layer, allMetadata, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            EditLog.ExecuteBatchEditFailed(_logger, layer.Id, ex);
            return UnifiedBatchEditResult.Failure("An error occurred while executing the batch edit operation.");
        }
    }

    /// <summary>
    /// Estimates the performance impact of an edit operation.
    /// </summary>
    /// <typeparam name="TRequest">Protocol edit request type</typeparam>
    /// <param name="protocolRequest">Protocol-specific edit request</param>
    /// <param name="layer">Target layer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Performance estimate or null if not available</returns>
    public async Task<EditPerformanceEstimate?> EstimatePerformanceAsync<TRequest>(
        TRequest protocolRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_adapters.TryGetValue(typeof(TRequest), out var adapterObj) ||
                adapterObj is not IEditParameterAdapter<TRequest> adapter)
            {
                return null;
            }

            var conversionResult = await adapter.ConvertAsync(protocolRequest, layer, cancellationToken);
            if (!conversionResult.IsSuccess)
            {
                return null;
            }

            return await _editProcessor.EstimatePerformanceAsync(
                conversionResult.EditRequest!.Value, layer, cancellationToken);
        }
        catch (Exception ex)
        {
            EditLog.EstimateEditPerformanceFailed(_logger, layer.Id, ex);
            return null;
        }
    }

    /// <summary>
    /// Validates an edit request without executing it.
    /// </summary>
    /// <typeparam name="TRequest">Protocol edit request type</typeparam>
    /// <param name="protocolRequest">Protocol-specific edit request</param>
    /// <param name="layer">Target layer</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result</returns>
    public async Task<EditValidationResult> ValidateEditAsync<TRequest>(
        TRequest protocolRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_adapters.TryGetValue(typeof(TRequest), out var adapterObj) ||
                adapterObj is not IEditParameterAdapter<TRequest> adapter)
            {
                return EditValidationResult.Failure($"No adapter registered for request type {typeof(TRequest).Name}");
            }

            var conversionResult = await adapter.ConvertAsync(protocolRequest, layer, cancellationToken);
            if (!conversionResult.IsSuccess)
            {
                return EditValidationResult.Failure(conversionResult.ErrorMessage!);
            }

            return _editProcessor.ValidateEdit(conversionResult.EditRequest!.Value, layer);
        }
        catch (Exception ex)
        {
            EditLog.ValidateEditRequestFailed(_logger, layer.Id, ex);
            return EditValidationResult.Failure("An error occurred while validating the edit request.");
        }
    }

    /// <summary>
    /// Gets registered adapters for diagnostic purposes.
    /// </summary>
    /// <returns>Dictionary of registered adapters</returns>
    public IReadOnlyDictionary<Type, string> GetRegisteredAdapters()
    {
        return _adapterProtocolNames.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value);
    }

    private async Task<UnifiedBatchEditResult> ExecuteBatchTransactionAsync(
        EditTransaction transaction,
        LayerDefinition layer,
        IDictionary<string, object> metadata,
        CancellationToken cancellationToken)
    {
        var results = new List<FeatureEditResult>();
        var allSuccessful = true;

        if (transaction.Configuration.RollbackOnFailure)
        {
            // Execute all operations in a single transaction
            var combinedEditBatch = CombineEditRequests(transaction.EditRequests, layer);
            var result = await _featureWriter.ApplyEditsAsync(layer.Id, combinedEditBatch, cancellationToken);
            results.Add(result);
            allSuccessful = result.IsSuccess;
        }
        else
        {
            // Execute each request separately
            foreach (var editRequest in transaction.EditRequests)
            {
                var editBatch = _editProcessor.ToFeatureEditBatch(editRequest, layer);
                var result = await _featureWriter.ApplyEditsAsync(layer.Id, editBatch, cancellationToken);
                results.Add(result);

                if (!result.IsSuccess)
                {
                    allSuccessful = false;
                }
            }
        }

        return UnifiedBatchEditResult.Success(
            results.ToImmutableArray(),
            transaction,
            allSuccessful,
            transaction.Protocol,
            metadata);
    }

    private FeatureEditBatch CombineEditRequests(
        ImmutableArray<UnifiedEditRequest> requests,
        LayerDefinition layer)
    {
        var allCreates = new List<Feature>();
        var allUpdates = new List<Feature>();
        var allDeletes = new List<long>();

        foreach (var request in requests)
        {
            var batch = _editProcessor.ToFeatureEditBatch(request, layer);

            allCreates.AddRange(batch.Creates);
            allUpdates.AddRange(batch.Updates);
            allDeletes.AddRange(batch.Deletes);
        }

        return FeatureEditBatch.Create(
            allCreates.ToImmutableArray(),
            allUpdates.ToImmutableArray(),
            allDeletes.ToImmutableArray(),
            rollbackOnFailure: true);
    }
}

/// <summary>
/// Result of unified edit execution.
/// </summary>
public sealed record UnifiedEditResult
{
    /// <summary>
    /// Whether the edit execution succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Edit result if execution succeeded.
    /// </summary>
    public FeatureEditResult? Result { get; init; }

    /// <summary>
    /// The unified edit request that was executed.
    /// </summary>
    public UnifiedEditRequest? EditRequest { get; init; }

    /// <summary>
    /// The transaction that was executed.
    /// </summary>
    public EditTransaction? Transaction { get; init; }

    /// <summary>
    /// Protocol that initiated the edit.
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
    /// Creates a successful edit result.
    /// </summary>
    /// <param name="result">Edit result</param>
    /// <param name="editRequest">Unified edit request</param>
    /// <param name="transaction">Edit transaction</param>
    /// <param name="protocol">Source protocol</param>
    /// <param name="metadata">Protocol metadata</param>
    /// <returns>Successful result</returns>
    public static UnifiedEditResult Success(
        FeatureEditResult result,
        UnifiedEditRequest editRequest,
        EditTransaction transaction,
        string protocol,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            Result = result,
            EditRequest = editRequest,
            Transaction = transaction,
            Protocol = protocol,
            Metadata = metadata
        };

    /// <summary>
    /// Creates a failed edit result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed result</returns>
    public static UnifiedEditResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Result of unified batch edit execution.
/// </summary>
public sealed record UnifiedBatchEditResult
{
    /// <summary>
    /// Whether the batch edit execution succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Results of individual edit operations.
    /// </summary>
    public ImmutableArray<FeatureEditResult>? Results { get; init; }

    /// <summary>
    /// The transaction that was executed.
    /// </summary>
    public EditTransaction? Transaction { get; init; }

    /// <summary>
    /// Whether all operations succeeded.
    /// </summary>
    public bool AllOperationsSucceeded { get; init; }

    /// <summary>
    /// Protocol that initiated the batch edit.
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
    /// Creates a successful batch edit result.
    /// </summary>
    /// <param name="results">Edit results</param>
    /// <param name="transaction">Edit transaction</param>
    /// <param name="allSucceeded">Whether all operations succeeded</param>
    /// <param name="protocol">Source protocol</param>
    /// <param name="metadata">Protocol metadata</param>
    /// <returns>Successful result</returns>
    public static UnifiedBatchEditResult Success(
        ImmutableArray<FeatureEditResult> results,
        EditTransaction transaction,
        bool allSucceeded,
        string protocol,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            Results = results,
            Transaction = transaction,
            AllOperationsSucceeded = allSucceeded,
            Protocol = protocol,
            Metadata = metadata
        };

    /// <summary>
    /// Creates a failed batch edit result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed result</returns>
    public static UnifiedBatchEditResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}
