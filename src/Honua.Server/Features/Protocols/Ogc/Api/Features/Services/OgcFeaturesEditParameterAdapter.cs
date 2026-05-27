// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Supported OGC API Features edit operations for the live adapter flow.
/// </summary>
internal enum OgcFeaturesEditOperation
{
    Create,
    Replace,
    Patch,
    Delete,
    Batch
}

/// <summary>
/// Prepared OGC API Features batch operation for adapter conversion.
/// </summary>
internal readonly record struct OgcFeaturesBatchEditOperation
{
    public required OgcFeaturesEditOperation Operation { get; init; }

    public Feature? Feature { get; init; }

    public long? ObjectId { get; init; }
}

/// <summary>
/// Prepared OGC API Features edit request for adapter conversion.
/// </summary>
internal readonly record struct OgcFeaturesEditRequest
{
    public required OgcFeaturesEditOperation Operation { get; init; }

    public Feature? Feature { get; init; }

    public long? ObjectId { get; init; }

    public ImmutableArray<OgcFeaturesBatchEditOperation> BatchOperations { get; init; }

    public bool RollbackOnFailure { get; init; }

    public string? IfMatch { get; init; }
}

/// <summary>
/// Converts prepared OGC API Features edits into the shared unified edit model.
/// </summary>
internal sealed class OgcFeaturesEditParameterAdapter(
    ILogger<OgcFeaturesEditParameterAdapter> logger) : IEditParameterAdapter<OgcFeaturesEditRequest>
{
    private readonly ILogger<OgcFeaturesEditParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "OgcFeatures";

    public ProtocolEditLimits DefaultLimits => ProtocolEditLimits.OgcFeatures;

    public TransactionSemantics TransactionSemantics => TransactionSemantics.OgcFeatures;

    public Task<EditAdapterResult> ConvertAsync(
        OgcFeaturesEditRequest protocolRequest,
        MetadataV2Resource resource,
        CancellationToken cancellationToken = default)
        => ConvertCoreAsync(protocolRequest, cancellationToken);

    private Task<EditAdapterResult> ConvertCoreAsync(
        OgcFeaturesEditRequest protocolRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            var editRequest = protocolRequest.Operation switch
            {
                OgcFeaturesEditOperation.Create => UnifiedEditRequest.WithCreates(
                    ImmutableArray.Create(ToCreateEditFeature(protocolRequest.Feature))),
                OgcFeaturesEditOperation.Replace => UnifiedEditRequest.WithUpdates(
                    ImmutableArray.Create(ToUpdateEditFeature(protocolRequest.ObjectId, protocolRequest.Feature))),
                OgcFeaturesEditOperation.Patch => UnifiedEditRequest.WithUpdates(
                    ImmutableArray.Create(ToUpdateEditFeature(protocolRequest.ObjectId, protocolRequest.Feature))),
                OgcFeaturesEditOperation.Delete => UnifiedEditRequest.WithDeletes(
                    ImmutableArray.Create(protocolRequest.ObjectId ?? throw new InvalidOperationException("Object ID is required for delete operations."))),
                OgcFeaturesEditOperation.Batch => CreateBatchEditRequest(protocolRequest.BatchOperations),
                _ => throw new InvalidOperationException($"Unsupported OGC edit operation '{protocolRequest.Operation}'.")
            };

            editRequest = editRequest with
            {
                TransactionOptions = new EditTransactionOptions
                {
                    RollbackOnFailure = protocolRequest.RollbackOnFailure,
                    UseExplicitTransaction = protocolRequest.RollbackOnFailure,
                    IsolationLevel = protocolRequest.RollbackOnFailure
                        ? TransactionIsolationLevel.Serializable
                        : TransactionIsolationLevel.ReadCommitted,
                    TimeoutMs = protocolRequest.RollbackOnFailure ? 300_000 : 180_000
                },
                ValidationOptions = EditValidationOptions.Strict()
            };

            var metadata = new Dictionary<string, object>
            {
                ["operation"] = protocolRequest.Operation.ToString(),
                ["rollbackOnFailure"] = protocolRequest.RollbackOnFailure
            };
            if (!string.IsNullOrWhiteSpace(protocolRequest.IfMatch))
            {
                metadata["ifMatch"] = protocolRequest.IfMatch!;
            }

            var transaction = EditTransaction.CreateSimple(
                Guid.NewGuid().ToString("n"),
                ProtocolName,
                editRequest,
                protocolRequest.RollbackOnFailure) with
            {
                Metadata = metadata.ToImmutableDictionary()
            };

            return Task.FromResult(EditAdapterResult.Success(editRequest, transaction, metadata));
        }
        catch (Exception ex)
        {
            OgcFeaturesPreparedAdaptersLog.EditParameterConversionFailed(_logger, ex);
            return Task.FromResult(EditAdapterResult.Failure("Invalid edit request."));
        }
    }

    private static UnifiedEditRequest CreateBatchEditRequest(ImmutableArray<OgcFeaturesBatchEditOperation> batchOperations)
    {
        if (batchOperations.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("Batch edit request cannot be empty.");
        }

        var operations = batchOperations
            .Select(static operation => operation.Operation switch
            {
                OgcFeaturesEditOperation.Create => UnifiedEditOperation.Create(ToCreateEditFeature(operation.Feature)),
                OgcFeaturesEditOperation.Replace or OgcFeaturesEditOperation.Patch =>
                    UnifiedEditOperation.Update(ToUpdateEditFeature(operation.ObjectId, operation.Feature)),
                OgcFeaturesEditOperation.Delete => UnifiedEditOperation.Delete(
                    operation.ObjectId ?? throw new InvalidOperationException("Batch delete operation is missing an object ID.")),
                _ => throw new InvalidOperationException($"Unsupported OGC batch edit operation '{operation.Operation}'.")
            })
            .ToImmutableArray();

        return UnifiedEditRequest.WithOperations(operations);
    }

    private static EditFeature ToCreateEditFeature(Feature? feature)
    {
        var value = feature ?? throw new InvalidOperationException("Feature payload is required.");
        return EditFeature.ForCreate(value.Geometry, value.Attributes);
    }

    private static EditFeature ToUpdateEditFeature(long? objectId, Feature? feature)
    {
        var value = feature ?? throw new InvalidOperationException("Feature payload is required.");
        return EditFeature.ForUpdate(
            objectId ?? value.Id,
            value.Geometry,
            value.Attributes,
            EditUpdateMode.Replace);
    }
}
