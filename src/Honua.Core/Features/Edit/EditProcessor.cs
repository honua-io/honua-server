// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Default implementation of unified edit processor.
/// Handles edit validation, optimization, and conversion across all protocols.
/// </summary>
public sealed class EditProcessor : IEditProcessor
{
    private readonly ILogger<EditProcessor> _logger;
    private readonly IMetadataV2GraphProvider? _v2GraphProvider;

    public EditProcessor(ILogger<EditProcessor> logger, IMetadataV2GraphProvider? v2GraphProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _v2GraphProvider = v2GraphProvider;
    }

    public EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        try
        {
            var warnings = new List<string>();

            // Check if edit request is empty
            if (editRequest.IsEmpty)
            {
                return EditValidationResult.Failure("Edit request cannot be empty");
            }

            // Validate operation counts against layer limits
            var totalOperations = editRequest.TotalOperations;
            if (totalOperations > GetMaxOperationsForLayer(layer))
            {
                return EditValidationResult.Failure(
                    $"Too many operations ({totalOperations}). Maximum allowed: {GetMaxOperationsForLayer(layer)}");
            }

            // Validate individual operations
            if (editRequest.Creates?.IsDefaultOrEmpty == false)
            {
                var createValidation = ValidateCreateOperations(editRequest.Creates.Value, layer);
                if (!createValidation.IsValid)
                {
                    return createValidation;
                }
                warnings.AddRange(createValidation.Warnings ?? []);
            }

            if (editRequest.Updates?.IsDefaultOrEmpty == false)
            {
                var updateValidation = ValidateUpdateOperations(editRequest.Updates.Value, layer);
                if (!updateValidation.IsValid)
                {
                    return updateValidation;
                }
                warnings.AddRange(updateValidation.Warnings ?? []);
            }

            if (editRequest.Deletes?.IsDefaultOrEmpty == false)
            {
                var deleteValidation = ValidateDeleteOperations(editRequest.Deletes.Value, layer);
                if (!deleteValidation.IsValid)
                {
                    return deleteValidation;
                }
                warnings.AddRange(deleteValidation.Warnings ?? []);
            }

            if (editRequest.Operations?.IsDefaultOrEmpty == false)
            {
                var operationsValidation = ValidateOrderedOperations(editRequest.Operations.Value, layer);
                if (!operationsValidation.IsValid)
                {
                    return operationsValidation;
                }
                warnings.AddRange(operationsValidation.Warnings ?? []);
            }

            // Validate transaction constraints
            if (editRequest.TransactionOptions.HasValue)
            {
                var transactionValidation = ValidateTransactionOptions(editRequest.TransactionOptions.Value, layer);
                if (!transactionValidation.IsValid)
                {
                    return transactionValidation;
                }
                warnings.AddRange(transactionValidation.Warnings ?? []);
            }

            return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
        }
        catch (Exception ex)
        {
            EditLog.ValidateEditFailed(_logger, layer.Id, ex);
            return EditValidationResult.Failure("Failed to validate edit request");
        }
    }

    public UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        try
        {
            var optimizedRequest = editRequest;

            // Optimize geometry handling
            optimizedRequest = OptimizeGeometryOperations(optimizedRequest, layer);

            // Optimize attribute operations
            optimizedRequest = OptimizeAttributeOperations(optimizedRequest, layer);

            // Optimize operation ordering
            optimizedRequest = OptimizeOperationOrder(optimizedRequest, layer);

            // Add performance hints
            optimizedRequest = AddPerformanceHints(optimizedRequest, layer);

            EditLog.EditRequestOptimized(_logger, layer.Id, editRequest.TotalOperations, optimizedRequest.TotalOperations);

            return optimizedRequest;
        }
        catch (Exception ex)
        {
            EditLog.OptimizeEditFailed(_logger, layer.Id, ex);
            return editRequest;
        }
    }

    public FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        try
        {
            var creates = ConvertToFeatures(editRequest.Creates, layer, isCreate: true);
            var updates = ConvertToFeatures(editRequest.Updates, layer, isCreate: false);
            var deletes = editRequest.Deletes ?? ImmutableArray<long>.Empty;

            var rollbackOnFailure = editRequest.TransactionOptions?.RollbackOnFailure ?? false;
            var useGlobalIds = HasGlobalIds(editRequest);

            // Handle ordered operations if present
            if (editRequest.Operations?.IsDefaultOrEmpty == false)
            {
                var operations = ConvertToFeatureOperations(editRequest.Operations.Value, layer);
                return FeatureEditBatch.Create(
                    operations: operations,
                    rollbackOnFailure: rollbackOnFailure,
                    useGlobalIds: useGlobalIds);
            }

            return FeatureEditBatch.Create(
                creates: creates,
                updates: updates,
                deletes: deletes,
                rollbackOnFailure: rollbackOnFailure,
                useGlobalIds: useGlobalIds);
        }
        catch (Exception ex)
        {
            EditLog.FeatureEditBatchConversionFailed(_logger, layer.Id, ex);
            throw;
        }
    }

    public TransactionValidationResult ValidateTransaction(EditTransaction transaction, LayerDefinition layer)
    {
        try
        {
            var warnings = new List<string>();

            // Validate transaction state
            if (transaction.State != TransactionState.Pending)
            {
                return TransactionValidationResult.Failure(
                    $"Transaction is in invalid state: {transaction.State}");
            }

            // Validate transaction configuration
            if (transaction.Configuration.TimeoutMs <= 0)
            {
                return TransactionValidationResult.Failure("Transaction timeout must be positive");
            }

            if (transaction.Configuration.MaxBatchSize <= 0)
            {
                return TransactionValidationResult.Failure("Transaction batch size must be positive");
            }

            // Validate total operations across all requests
            var totalOperations = transaction.TotalOperations;
            var maxOperations = transaction.Configuration.MaxBatchSize * 10; // Conservative limit

            if (totalOperations > maxOperations)
            {
                return TransactionValidationResult.Failure(
                    $"Transaction has too many operations ({totalOperations}). Maximum: {maxOperations}");
            }

            // Validate individual edit requests
            foreach (var editRequest in transaction.EditRequests)
            {
                var editValidation = ValidateEdit(editRequest, layer);
                if (!editValidation.IsValid)
                {
                    return TransactionValidationResult.Failure(
                        $"Invalid edit request in transaction: {editValidation.ErrorMessage}");
                }

                if (editValidation.Warnings != null)
                {
                    warnings.AddRange(editValidation.Warnings.Select(w => $"Edit request warning: {w}"));
                }
            }

            // Validate compatibility with layer capabilities
            var compatibilityValidation = ValidateLayerCompatibility(transaction, layer);
            if (!compatibilityValidation.IsValid)
            {
                return compatibilityValidation;
            }

            if (compatibilityValidation.Warnings != null)
            {
                warnings.AddRange(compatibilityValidation.Warnings);
            }

            return TransactionValidationResult.Success(warnings.Count > 0 ? warnings : null);
        }
        catch (Exception ex)
        {
            EditLog.ValidateTransactionFailed(_logger, transaction.TransactionId, layer.Id, ex);
            return TransactionValidationResult.Failure("Failed to validate transaction");
        }
    }

    public EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, LayerDefinition layer)
    {
        var totalOperations = editRequest.TotalOperations;
        var hasTransaction = editRequest.RequiresTransaction;

        // Determine batch size
        var recommendedBatchSize = totalOperations switch
        {
            <= 100 => (int?)null, // Execute in single batch
            <= 1000 => 100,
            <= 5000 => 250,
            _ => 500
        };

        // Determine parallelization
        var enableParallel = !hasTransaction && totalOperations > 50;

        // Determine validation strategy
        var validateBeforeExecution = editRequest.ValidationOptions?.Mode != ValidationMode.Skip;

        return new EditExecutionStrategy
        {
            UseTransaction = hasTransaction,
            EnableRollback = editRequest.TransactionOptions?.RollbackOnFailure ?? false,
            ValidateBeforeExecution = validateBeforeExecution,
            EnableParallelProcessing = enableParallel,
            RecommendedBatchSize = recommendedBatchSize
        };
    }

    public async Task<EditPerformanceEstimate> EstimatePerformanceAsync(
        UnifiedEditRequest editRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        try
        {
            var totalOperations = editRequest.TotalOperations;

            // Estimate execution time (rough heuristics)
            var baseTimePerOperation = 10; // 10ms base time per operation
            var geometryPenalty = HasComplexGeometry(editRequest) ? 5 : 0;
            var transactionPenalty = editRequest.RequiresTransaction ? 100 : 0;

            var estimatedTimeMs = (totalOperations * (baseTimePerOperation + geometryPenalty)) + transactionPenalty;

            // Estimate memory usage
            var baseMemoryPerOperation = 1024; // 1KB base memory per operation
            var geometryMemoryPenalty = HasComplexGeometry(editRequest) ? 4096 : 0;
            var estimatedMemoryBytes = totalOperations * (baseMemoryPerOperation + geometryMemoryPenalty);

            // Estimate database operations
            var dbOperations = totalOperations + (editRequest.RequiresTransaction ? 2 : 0); // +2 for begin/commit

            // Determine risk level
            var riskLevel = totalOperations switch
            {
                <= 100 => EditRiskLevel.Low,
                <= 1000 => EditRiskLevel.Medium,
                _ => EditRiskLevel.High
            };

            var recommendations = new List<string>();
            if (totalOperations > 1000)
            {
                recommendations.Add("Consider batching operations for better performance");
            }
            if (HasComplexGeometry(editRequest))
            {
                recommendations.Add("Complex geometries detected - consider geometry simplification");
            }
            if (editRequest.RequiresTransaction && totalOperations > 500)
            {
                recommendations.Add("Large transaction detected - monitor for lock contention");
            }

            return new EditPerformanceEstimate
            {
                EstimatedExecutionTimeMs = estimatedTimeMs,
                EstimatedMemoryUsageBytes = estimatedMemoryBytes,
                EstimatedDatabaseOperations = dbOperations,
                RiskLevel = riskLevel,
                Recommendations = recommendations.Count > 0 ? recommendations : null
            };
        }
        catch (Exception ex)
        {
            EditLog.EstimateEditPerformanceFailed(_logger, layer.Id, ex);
            throw;
        }
    }

    private static EditValidationResult ValidateCreateOperations(
        ImmutableArray<EditFeature> creates,
        LayerDefinition layer)
    {
        var warnings = new List<string>();

        foreach (var feature in creates)
        {
            if (feature.ObjectId.HasValue)
            {
                warnings.Add("Object ID specified for create operation will be ignored");
            }

            // Validate attributes exist if required
            if (feature.Attributes?.IsEmpty != false && RequiresAttributes(layer))
            {
                return EditValidationResult.Failure("Attributes required for create operation");
            }

        }

        return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private static EditValidationResult ValidateUpdateOperations(
        ImmutableArray<EditFeature> updates,
        LayerDefinition layer)
    {
        foreach (var feature in updates)
        {
            if (!feature.ObjectId.HasValue)
            {
                return EditValidationResult.Failure("Object ID required for update operation");
            }

            if (feature.ObjectId.Value <= 0)
            {
                return EditValidationResult.Failure("Invalid Object ID for update operation");
            }
        }

        return EditValidationResult.Success();
    }

    private static EditValidationResult ValidateDeleteOperations(
        ImmutableArray<long> deletes,
        LayerDefinition layer)
    {
        foreach (var objectId in deletes)
        {
            if (objectId <= 0)
            {
                return EditValidationResult.Failure("Invalid Object ID for delete operation");
            }
        }

        return EditValidationResult.Success();
    }

    private static EditValidationResult ValidateOrderedOperations(
        ImmutableArray<UnifiedEditOperation> operations,
        LayerDefinition layer)
    {
        foreach (var operation in operations)
        {
            switch (operation.Type)
            {
                case EditOperationType.Create:
                    if (operation.Feature == null)
                    {
                        return EditValidationResult.Failure("Feature required for create operation");
                    }
                    break;
                case EditOperationType.Update:
                    if (operation.Feature == null || !operation.Feature.Value.ObjectId.HasValue)
                    {
                        return EditValidationResult.Failure("Feature with Object ID required for update operation");
                    }
                    break;
                case EditOperationType.Delete:
                    if (!operation.ObjectId.HasValue || operation.ObjectId.Value <= 0)
                    {
                        return EditValidationResult.Failure("Valid Object ID required for delete operation");
                    }
                    break;
            }
        }

        return EditValidationResult.Success();
    }

    private static EditValidationResult ValidateTransactionOptions(
        EditTransactionOptions options,
        LayerDefinition layer)
    {
        var warnings = new List<string>();

        if (options.TimeoutMs.HasValue && options.TimeoutMs.Value <= 0)
        {
            return EditValidationResult.Failure("Transaction timeout must be positive");
        }

        if (options.TimeoutMs.HasValue && options.TimeoutMs.Value > 3_600_000) // 1 hour
        {
            warnings.Add("Transaction timeout is very long (> 1 hour)");
        }

        return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private static TransactionValidationResult ValidateLayerCompatibility(EditTransaction transaction, LayerDefinition layer)
    {
        var warnings = new List<string>();

        // Check for protocol-specific constraints
        if (transaction.Protocol == "WFS20" && !transaction.Configuration.RollbackOnFailure)
        {
            warnings.Add("WFS 2.0 typically requires rollback on failure for transaction semantics");
        }

        if (transaction.Protocol == "OData" && transaction.Configuration.EnableParallelExecution)
        {
            warnings.Add("OData may not benefit from parallel execution due to protocol constraints");
        }

        // Check layer-specific constraints
        // Note: For now we assume all layers support transactions
        // In the future, this could be checked via layer.Metadata or a dedicated capabilities system

        return TransactionValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private static UnifiedEditRequest OptimizeGeometryOperations(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would include geometry simplification, validation caching, etc.
        return request;
    }

    private static UnifiedEditRequest OptimizeAttributeOperations(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would include attribute validation caching, default value handling, etc.
        return request;
    }

    private static UnifiedEditRequest OptimizeOperationOrder(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would include reordering operations for optimal database performance
        return request;
    }

    private static UnifiedEditRequest AddPerformanceHints(UnifiedEditRequest request, LayerDefinition layer)
    {
        // Implementation would add hints for query optimization, caching, etc.
        return request;
    }

    private static ImmutableArray<Feature> ConvertToFeatures(
        ImmutableArray<EditFeature>? editFeatures,
        LayerDefinition layer,
        bool isCreate)
    {
        if (editFeatures?.IsDefaultOrEmpty != false)
        {
            return ImmutableArray<Feature>.Empty;
        }

        var features = new List<Feature>();
        foreach (var editFeature in editFeatures)
        {
            var objectId = isCreate ? 0 : (editFeature.ObjectId ?? 0);
            var attributes = editFeature.Attributes ?? ImmutableDictionary<string, object?>.Empty;

            features.Add(Feature.Create(objectId, editFeature.Geometry, attributes));
        }

        return features.ToImmutableArray();
    }

    private static ImmutableArray<FeatureEditOperation> ConvertToFeatureOperations(
        ImmutableArray<UnifiedEditOperation> operations,
        LayerDefinition layer)
    {
        var featureOperations = new List<FeatureEditOperation>();

        foreach (var operation in operations)
        {
            switch (operation.Type)
            {
                case EditOperationType.Create:
                    if (operation.Feature.HasValue)
                    {
                        var feature = ConvertEditFeatureToFeature(operation.Feature.Value, layer, isCreate: true);
                        featureOperations.Add(FeatureEditOperation.Create(feature));
                    }
                    break;

                case EditOperationType.Update:
                    if (operation.Feature.HasValue)
                    {
                        var feature = ConvertEditFeatureToFeature(operation.Feature.Value, layer, isCreate: false);
                        featureOperations.Add(FeatureEditOperation.Update(feature));
                    }
                    break;

                case EditOperationType.Delete:
                    if (operation.ObjectId.HasValue)
                    {
                        featureOperations.Add(FeatureEditOperation.Delete(operation.ObjectId.Value));
                    }
                    break;
            }
        }

        return featureOperations.ToImmutableArray();
    }

    private static Feature ConvertEditFeatureToFeature(EditFeature editFeature, LayerDefinition layer, bool isCreate)
    {
        var objectId = isCreate ? 0 : (editFeature.ObjectId ?? 0);
        var attributes = editFeature.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        return Feature.Create(objectId, editFeature.Geometry, attributes);
    }

    private static bool HasGlobalIds(UnifiedEditRequest request)
    {
        if (request.Creates?.IsDefaultOrEmpty == false)
        {
            return request.Creates.Value.Any(f => !string.IsNullOrEmpty(f.GlobalId));
        }

        if (request.Updates?.IsDefaultOrEmpty == false)
        {
            return request.Updates.Value.Any(f => !string.IsNullOrEmpty(f.GlobalId));
        }

        return false;
    }

    private static bool HasComplexGeometry(UnifiedEditRequest request)
    {
        // Simplified check - in reality would analyze geometry complexity
        var hasGeometry = false;

        if (request.Creates?.IsDefaultOrEmpty == false)
        {
            hasGeometry = request.Creates.Value.Any(f => f.Geometry != null);
        }

        if (request.Updates?.IsDefaultOrEmpty == false)
        {
            hasGeometry = hasGeometry || request.Updates.Value.Any(f => f.Geometry != null);
        }

        return hasGeometry;
    }

    private static int GetMaxOperationsForLayer(LayerDefinition layer)
    {
        // In reality, this would check layer configuration or capabilities
        return 10000; // Default maximum operations per layer
    }

    private static bool RequiresAttributes(LayerDefinition layer)
    {
        // Check if layer has non-nullable attribute fields without default values
        return layer.AttributeFields.Any(f => !f.Nullable && f.DefaultValue == null);
    }

    private static bool RequiresGeometry(LayerDefinition layer)
    {
        return layer.GeometryType != GeometryType.None;
    }

    /// <inheritdoc />
    public EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var warnings = new List<string>();

            // Check if edit request is empty
            if (editRequest.IsEmpty)
            {
                return EditValidationResult.Failure("Edit request cannot be empty");
            }

            // Validate operation counts against layer limits
            var totalOperations = editRequest.TotalOperations;
            if (totalOperations > GetMaxOperationsForResource(resource))
            {
                return EditValidationResult.Failure(
                    $"Too many operations ({totalOperations}). Maximum allowed: {GetMaxOperationsForResource(resource)}");
            }

            // Validate individual operations
            if (editRequest.Creates?.IsDefaultOrEmpty == false)
            {
                var createValidation = ValidateCreateOperations(editRequest.Creates.Value, resource);
                if (!createValidation.IsValid)
                {
                    return createValidation;
                }
                warnings.AddRange(createValidation.Warnings ?? []);
            }

            if (editRequest.Updates?.IsDefaultOrEmpty == false)
            {
                var updateValidation = ValidateUpdateOperations(editRequest.Updates.Value, resource);
                if (!updateValidation.IsValid)
                {
                    return updateValidation;
                }
                warnings.AddRange(updateValidation.Warnings ?? []);
            }

            if (editRequest.Deletes?.IsDefaultOrEmpty == false)
            {
                var deleteValidation = ValidateDeleteOperations(editRequest.Deletes.Value, resource);
                if (!deleteValidation.IsValid)
                {
                    return deleteValidation;
                }
                warnings.AddRange(deleteValidation.Warnings ?? []);
            }

            if (editRequest.Operations?.IsDefaultOrEmpty == false)
            {
                var operationsValidation = ValidateOrderedOperations(editRequest.Operations.Value, resource);
                if (!operationsValidation.IsValid)
                {
                    return operationsValidation;
                }
                warnings.AddRange(operationsValidation.Warnings ?? []);
            }

            // Validate transaction constraints
            if (editRequest.TransactionOptions.HasValue)
            {
                var transactionValidation = ValidateTransactionOptions(editRequest.TransactionOptions.Value, resource);
                if (!transactionValidation.IsValid)
                {
                    return transactionValidation;
                }
                warnings.AddRange(transactionValidation.Warnings ?? []);
            }

            return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
        }
        catch (Exception ex)
        {
            EditLog.ValidateEditFailed(_logger, ResolveStorageLayerIdForLog(resource), ex);
            return EditValidationResult.Failure("Failed to validate edit request");
        }
    }

    /// <inheritdoc />
    public UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var optimizedRequest = editRequest;

            // Optimize geometry handling
            optimizedRequest = OptimizeGeometryOperations(optimizedRequest, resource);

            // Optimize attribute operations
            optimizedRequest = OptimizeAttributeOperations(optimizedRequest, resource);

            // Optimize operation ordering
            optimizedRequest = OptimizeOperationOrder(optimizedRequest, resource);

            // Add performance hints
            optimizedRequest = AddPerformanceHints(optimizedRequest, resource);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
#pragma warning disable CA1873 // logger gated explicitly above; resolution is bounded and only runs in Debug
                EditLog.EditRequestOptimized(
                    _logger,
                    ResolveStorageLayerIdForLog(resource),
                    editRequest.TotalOperations,
                    optimizedRequest.TotalOperations);
#pragma warning restore CA1873
            }

            return optimizedRequest;
        }
        catch (Exception ex)
        {
            EditLog.OptimizeEditFailed(_logger, ResolveStorageLayerIdForLog(resource), ex);
            return editRequest;
        }
    }

    /// <inheritdoc />
    public FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var creates = ConvertToFeatures(editRequest.Creates, resource, isCreate: true);
            var updates = ConvertToFeatures(editRequest.Updates, resource, isCreate: false);
            var deletes = editRequest.Deletes ?? ImmutableArray<long>.Empty;

            var rollbackOnFailure = editRequest.TransactionOptions?.RollbackOnFailure ?? false;
            var useGlobalIds = HasGlobalIds(editRequest);

            // Handle ordered operations if present
            if (editRequest.Operations?.IsDefaultOrEmpty == false)
            {
                var operations = ConvertToFeatureOperations(editRequest.Operations.Value, resource);
                return FeatureEditBatch.Create(
                    operations: operations,
                    rollbackOnFailure: rollbackOnFailure,
                    useGlobalIds: useGlobalIds);
            }

            return FeatureEditBatch.Create(
                creates: creates,
                updates: updates,
                deletes: deletes,
                rollbackOnFailure: rollbackOnFailure,
                useGlobalIds: useGlobalIds);
        }
        catch (Exception ex)
        {
            EditLog.FeatureEditBatchConversionFailed(_logger, ResolveStorageLayerIdForLog(resource), ex);
            throw;
        }
    }

    /// <inheritdoc />
    public TransactionValidationResult ValidateTransaction(EditTransaction transaction, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var warnings = new List<string>();

            // Validate transaction state
            if (transaction.State != TransactionState.Pending)
            {
                return TransactionValidationResult.Failure(
                    $"Transaction is in invalid state: {transaction.State}");
            }

            // Validate transaction configuration
            if (transaction.Configuration.TimeoutMs <= 0)
            {
                return TransactionValidationResult.Failure("Transaction timeout must be positive");
            }

            if (transaction.Configuration.MaxBatchSize <= 0)
            {
                return TransactionValidationResult.Failure("Transaction batch size must be positive");
            }

            // Validate total operations across all requests
            var totalOperations = transaction.TotalOperations;
            var maxOperations = transaction.Configuration.MaxBatchSize * 10; // Conservative limit

            if (totalOperations > maxOperations)
            {
                return TransactionValidationResult.Failure(
                    $"Transaction has too many operations ({totalOperations}). Maximum: {maxOperations}");
            }

            // Validate individual edit requests
            foreach (var editRequest in transaction.EditRequests)
            {
                var editValidation = ValidateEdit(editRequest, resource);
                if (!editValidation.IsValid)
                {
                    return TransactionValidationResult.Failure(
                        $"Invalid edit request in transaction: {editValidation.ErrorMessage}");
                }

                if (editValidation.Warnings != null)
                {
                    warnings.AddRange(editValidation.Warnings.Select(w => $"Edit request warning: {w}"));
                }
            }

            // Validate compatibility with layer capabilities
            var compatibilityValidation = ValidateResourceCompatibility(transaction, resource);
            if (!compatibilityValidation.IsValid)
            {
                return compatibilityValidation;
            }

            if (compatibilityValidation.Warnings != null)
            {
                warnings.AddRange(compatibilityValidation.Warnings);
            }

            return TransactionValidationResult.Success(warnings.Count > 0 ? warnings : null);
        }
        catch (Exception ex)
        {
            EditLog.ValidateTransactionFailed(_logger, transaction.TransactionId, ResolveStorageLayerIdForLog(resource), ex);
            return TransactionValidationResult.Failure("Failed to validate transaction");
        }
    }

    /// <inheritdoc />
    public EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var totalOperations = editRequest.TotalOperations;
        var hasTransaction = editRequest.RequiresTransaction;

        // Determine batch size
        var recommendedBatchSize = totalOperations switch
        {
            <= 100 => (int?)null, // Execute in single batch
            <= 1000 => 100,
            <= 5000 => 250,
            _ => 500
        };

        // Determine parallelization
        var enableParallel = !hasTransaction && totalOperations > 50;

        // Determine validation strategy
        var validateBeforeExecution = editRequest.ValidationOptions?.Mode != ValidationMode.Skip;

        return new EditExecutionStrategy
        {
            UseTransaction = hasTransaction,
            EnableRollback = editRequest.TransactionOptions?.RollbackOnFailure ?? false,
            ValidateBeforeExecution = validateBeforeExecution,
            EnableParallelProcessing = enableParallel,
            RecommendedBatchSize = recommendedBatchSize
        };
    }

    /// <inheritdoc />
    public Task<EditPerformanceEstimate> EstimatePerformanceAsync(
        UnifiedEditRequest editRequest,
        MetadataV2Resource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        try
        {
            var totalOperations = editRequest.TotalOperations;

            // Estimate execution time (rough heuristics)
            var baseTimePerOperation = 10; // 10ms base time per operation
            var geometryPenalty = HasComplexGeometry(editRequest) ? 5 : 0;
            var transactionPenalty = editRequest.RequiresTransaction ? 100 : 0;

            var estimatedTimeMs = (totalOperations * (baseTimePerOperation + geometryPenalty)) + transactionPenalty;

            // Estimate memory usage
            var baseMemoryPerOperation = 1024; // 1KB base memory per operation
            var geometryMemoryPenalty = HasComplexGeometry(editRequest) ? 4096 : 0;
            var estimatedMemoryBytes = totalOperations * (baseMemoryPerOperation + geometryMemoryPenalty);

            // Estimate database operations
            var dbOperations = totalOperations + (editRequest.RequiresTransaction ? 2 : 0); // +2 for begin/commit

            // Determine risk level
            var riskLevel = totalOperations switch
            {
                <= 100 => EditRiskLevel.Low,
                <= 1000 => EditRiskLevel.Medium,
                _ => EditRiskLevel.High
            };

            var recommendations = new List<string>();
            if (totalOperations > 1000)
            {
                recommendations.Add("Consider batching operations for better performance");
            }
            if (HasComplexGeometry(editRequest))
            {
                recommendations.Add("Complex geometries detected - consider geometry simplification");
            }
            if (editRequest.RequiresTransaction && totalOperations > 500)
            {
                recommendations.Add("Large transaction detected - monitor for lock contention");
            }

            return Task.FromResult(new EditPerformanceEstimate
            {
                EstimatedExecutionTimeMs = estimatedTimeMs,
                EstimatedMemoryUsageBytes = estimatedMemoryBytes,
                EstimatedDatabaseOperations = dbOperations,
                RiskLevel = riskLevel,
                Recommendations = recommendations.Count > 0 ? recommendations : null
            });
        }
        catch (Exception ex)
        {
            EditLog.EstimateEditPerformanceFailed(_logger, ResolveStorageLayerIdForLog(resource), ex);
            throw;
        }
    }

    private static EditValidationResult ValidateCreateOperations(
        ImmutableArray<EditFeature> creates,
        MetadataV2Resource resource)
    {
        var warnings = new List<string>();

        foreach (var feature in creates)
        {
            if (feature.ObjectId.HasValue)
            {
                warnings.Add("Object ID specified for create operation will be ignored");
            }

            // Validate attributes exist if required
            if (feature.Attributes?.IsEmpty != false && RequiresAttributes(resource))
            {
                return EditValidationResult.Failure("Attributes required for create operation");
            }
        }

        return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private static EditValidationResult ValidateUpdateOperations(
        ImmutableArray<EditFeature> updates,
        MetadataV2Resource resource)
    {
        foreach (var feature in updates)
        {
            if (!feature.ObjectId.HasValue)
            {
                return EditValidationResult.Failure("Object ID required for update operation");
            }

            if (feature.ObjectId.Value <= 0)
            {
                return EditValidationResult.Failure("Invalid Object ID for update operation");
            }
        }

        return EditValidationResult.Success();
    }

    private static EditValidationResult ValidateDeleteOperations(
        ImmutableArray<long> deletes,
        MetadataV2Resource resource)
    {
        foreach (var objectId in deletes)
        {
            if (objectId <= 0)
            {
                return EditValidationResult.Failure("Invalid Object ID for delete operation");
            }
        }

        return EditValidationResult.Success();
    }

    private static EditValidationResult ValidateOrderedOperations(
        ImmutableArray<UnifiedEditOperation> operations,
        MetadataV2Resource resource)
    {
        foreach (var operation in operations)
        {
            switch (operation.Type)
            {
                case EditOperationType.Create:
                    if (operation.Feature == null)
                    {
                        return EditValidationResult.Failure("Feature required for create operation");
                    }
                    break;
                case EditOperationType.Update:
                    if (operation.Feature == null || !operation.Feature.Value.ObjectId.HasValue)
                    {
                        return EditValidationResult.Failure("Feature with Object ID required for update operation");
                    }
                    break;
                case EditOperationType.Delete:
                    if (!operation.ObjectId.HasValue || operation.ObjectId.Value <= 0)
                    {
                        return EditValidationResult.Failure("Valid Object ID required for delete operation");
                    }
                    break;
            }
        }

        return EditValidationResult.Success();
    }

    private static EditValidationResult ValidateTransactionOptions(
        EditTransactionOptions options,
        MetadataV2Resource resource)
    {
        var warnings = new List<string>();

        if (options.TimeoutMs.HasValue && options.TimeoutMs.Value <= 0)
        {
            return EditValidationResult.Failure("Transaction timeout must be positive");
        }

        if (options.TimeoutMs.HasValue && options.TimeoutMs.Value > 3_600_000) // 1 hour
        {
            warnings.Add("Transaction timeout is very long (> 1 hour)");
        }

        return EditValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private static TransactionValidationResult ValidateResourceCompatibility(EditTransaction transaction, MetadataV2Resource resource)
    {
        var warnings = new List<string>();

        // Check for protocol-specific constraints
        if (transaction.Protocol == "WFS20" && !transaction.Configuration.RollbackOnFailure)
        {
            warnings.Add("WFS 2.0 typically requires rollback on failure for transaction semantics");
        }

        if (transaction.Protocol == "OData" && transaction.Configuration.EnableParallelExecution)
        {
            warnings.Add("OData may not benefit from parallel execution due to protocol constraints");
        }

        // Resource-specific constraints: for now we assume all resources support transactions.

        return TransactionValidationResult.Success(warnings.Count > 0 ? warnings : null);
    }

    private static UnifiedEditRequest OptimizeGeometryOperations(UnifiedEditRequest request, MetadataV2Resource resource)
    {
        // Implementation would include geometry simplification, validation caching, etc.
        return request;
    }

    private static UnifiedEditRequest OptimizeAttributeOperations(UnifiedEditRequest request, MetadataV2Resource resource)
    {
        // Implementation would include attribute validation caching, default value handling, etc.
        return request;
    }

    private static UnifiedEditRequest OptimizeOperationOrder(UnifiedEditRequest request, MetadataV2Resource resource)
    {
        // Implementation would include reordering operations for optimal database performance
        return request;
    }

    private static UnifiedEditRequest AddPerformanceHints(UnifiedEditRequest request, MetadataV2Resource resource)
    {
        // Implementation would add hints for query optimization, caching, etc.
        return request;
    }

    private static ImmutableArray<Feature> ConvertToFeatures(
        ImmutableArray<EditFeature>? editFeatures,
        MetadataV2Resource resource,
        bool isCreate)
    {
        if (editFeatures?.IsDefaultOrEmpty != false)
        {
            return ImmutableArray<Feature>.Empty;
        }

        var features = new List<Feature>();
        foreach (var editFeature in editFeatures)
        {
            var objectId = isCreate ? 0 : (editFeature.ObjectId ?? 0);
            var attributes = editFeature.Attributes ?? ImmutableDictionary<string, object?>.Empty;

            features.Add(Feature.Create(objectId, editFeature.Geometry, attributes));
        }

        return features.ToImmutableArray();
    }

    private static ImmutableArray<FeatureEditOperation> ConvertToFeatureOperations(
        ImmutableArray<UnifiedEditOperation> operations,
        MetadataV2Resource resource)
    {
        var featureOperations = new List<FeatureEditOperation>();

        foreach (var operation in operations)
        {
            switch (operation.Type)
            {
                case EditOperationType.Create:
                    if (operation.Feature.HasValue)
                    {
                        var feature = ConvertEditFeatureToFeature(operation.Feature.Value, resource, isCreate: true);
                        featureOperations.Add(FeatureEditOperation.Create(feature));
                    }
                    break;

                case EditOperationType.Update:
                    if (operation.Feature.HasValue)
                    {
                        var feature = ConvertEditFeatureToFeature(operation.Feature.Value, resource, isCreate: false);
                        featureOperations.Add(FeatureEditOperation.Update(feature));
                    }
                    break;

                case EditOperationType.Delete:
                    if (operation.ObjectId.HasValue)
                    {
                        featureOperations.Add(FeatureEditOperation.Delete(operation.ObjectId.Value));
                    }
                    break;
            }
        }

        return featureOperations.ToImmutableArray();
    }

    private static Feature ConvertEditFeatureToFeature(EditFeature editFeature, MetadataV2Resource resource, bool isCreate)
    {
        var objectId = isCreate ? 0 : (editFeature.ObjectId ?? 0);
        var attributes = editFeature.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        return Feature.Create(objectId, editFeature.Geometry, attributes);
    }

    private static int GetMaxOperationsForResource(MetadataV2Resource resource)
    {
        // In reality, this would check resource configuration or capabilities.
        return 10000; // Default maximum operations per resource
    }

    private static bool RequiresAttributes(MetadataV2Resource resource)
    {
        // V2 fields carry Nullable but no DefaultValue — match the v1 contract by
        // treating any non-nullable, non-geometry, non-system field as required.
        foreach (var field in resource.SchemaFields)
        {
            if (field.Nullable)
            {
                continue;
            }
            if (field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
            {
                continue;
            }
            // Skip the primary id field — it is generated server-side on create.
            for (var i = 0; i < field.SemanticRoles.Count; i++)
            {
                if (string.Equals(field.SemanticRoles[i], "id.primary", StringComparison.OrdinalIgnoreCase))
                {
                    goto next;
                }
            }
            return true;
            next: ;
        }
        return false;
    }

    private static bool RequiresGeometry(MetadataV2Resource resource)
    {
        return resource.Spatial?.GeometryType is { } gt && gt != MetadataV2GeometryType.None;
    }

    private int ResolveStorageLayerIdForLog(MetadataV2Resource resource)
    {
        if (_v2GraphProvider is null)
        {
            return 0;
        }
        try
        {
            var snapshot = _v2GraphProvider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
            return snapshot.ResolveStorageLayerId(resource) ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
