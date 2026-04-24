// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Prepared GeoServices edit inputs ready for protocol-adapter conversion.
/// </summary>
internal readonly record struct GeoServicesEditRequest
{
    public ImmutableArray<Feature> Creates { get; init; }

    public ImmutableArray<Feature> Updates { get; init; }

    public ImmutableArray<long> Deletes { get; init; }

    public bool RollbackOnFailure { get; init; }

    public bool UseGlobalIds { get; init; }
}

/// <summary>
/// Converts prepared GeoServices edits into the shared unified edit model.
/// </summary>
internal sealed class GeoServicesEditParameterAdapter(
    ILogger<GeoServicesEditParameterAdapter> logger) : IEditParameterAdapter<GeoServicesEditRequest>
{
    private readonly ILogger<GeoServicesEditParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "GeoServices";

    public ProtocolEditLimits DefaultLimits => ProtocolEditLimits.GeoServices;

    public TransactionSemantics TransactionSemantics => TransactionSemantics.GeoServices;

    public Task<EditAdapterResult> ConvertAsync(
        GeoServicesEditRequest protocolRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var creates = protocolRequest.Creates.IsDefaultOrEmpty
                ? (ImmutableArray<EditFeature>?)null
                : protocolRequest.Creates
                    .Select(static feature => EditFeature.ForCreate(feature.Geometry, feature.Attributes))
                    .ToImmutableArray();
            var updates = protocolRequest.Updates.IsDefaultOrEmpty
                ? (ImmutableArray<EditFeature>?)null
                : protocolRequest.Updates
                    .Select(static feature => EditFeature.ForUpdate(
                        feature.Id,
                        feature.Geometry,
                        feature.Attributes,
                        EditUpdateMode.Replace))
                    .ToImmutableArray();

            var editRequest = new UnifiedEditRequest
            {
                Creates = creates,
                Updates = updates,
                Deletes = protocolRequest.Deletes.IsDefaultOrEmpty ? null : protocolRequest.Deletes,
                TransactionOptions = new EditTransactionOptions
                {
                    RollbackOnFailure = protocolRequest.RollbackOnFailure,
                    UseExplicitTransaction = protocolRequest.RollbackOnFailure,
                    IsolationLevel = protocolRequest.RollbackOnFailure
                        ? TransactionIsolationLevel.Serializable
                        : TransactionIsolationLevel.ReadCommitted,
                    TimeoutMs = 600_000
                },
                ValidationOptions = EditValidationOptions.Strict()
            };

            var metadata = new Dictionary<string, object>
            {
                ["rollbackOnFailure"] = protocolRequest.RollbackOnFailure,
                ["useGlobalIds"] = protocolRequest.UseGlobalIds
            };

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
            GeoServicesPreparedAdaptersLog.EditParameterConversionFailed(_logger, ex);
            return Task.FromResult(EditAdapterResult.Failure("Invalid edit request."));
        }
    }
}
