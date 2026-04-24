// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Prepared WFS 2.0 query inputs ready for adapter conversion.
/// </summary>
internal readonly record struct Wfs20QueryRequest
{
    public SqlFragment? SqlFilter { get; init; }

    public ImmutableArray<long>? ObjectIds { get; init; }

    public ImmutableArray<string>? OutFields { get; init; }

    public SpatialFilter? SpatialFilter { get; init; }

    public QueryCrs? OutputCrs { get; init; }

    public ImmutableArray<OrderByClause>? OrderBy { get; init; }

    public int? Offset { get; init; }

    public int? Limit { get; init; }
}

/// <summary>
/// Prepared WFS 2.0 edit inputs ready for adapter conversion.
/// </summary>
internal readonly record struct Wfs20EditRequest
{
    public ImmutableArray<FeatureEditOperation> Operations { get; init; }

    public bool RollbackOnFailure { get; init; }
}

/// <summary>
/// Converts prepared WFS query inputs into the unified query model.
/// </summary>
internal sealed class Wfs20QueryParameterAdapter(
    ILogger<Wfs20QueryParameterAdapter> logger) : IQueryParameterAdapter<Wfs20QueryRequest>
{
    private readonly ILogger<Wfs20QueryParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "WFS-2.0";

    public ProtocolLimits DefaultLimits => ProtocolLimits.Wfs20;

    public Task<QueryAdapterResult> ConvertAsync(
        Wfs20QueryRequest parameters,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            QueryFilter? filter = null;
            if (parameters.SqlFilter != null)
            {
                filter = QueryFilter.FromSql(parameters.SqlFilter);
            }

            var query = new UnifiedQuery
            {
                Filter = filter,
                SpatialFilter = parameters.SpatialFilter,
                ObjectIds = parameters.ObjectIds,
                OutFields = parameters.OutFields,
                Offset = parameters.Offset,
                Limit = parameters.Limit,
                OrderBy = parameters.OrderBy,
                OutputCrs = parameters.OutputCrs,
                Hints = QueryHints.Create(
                    preferStreaming: (parameters.Limit ?? DefaultLimits.DefaultResultCount) > DefaultLimits.DefaultResultCount,
                    enableCaching: true,
                    requireExactCount: true)
            };

            return Task.FromResult(QueryAdapterResult.Success(query));
        }
        catch (Exception ex)
        {
            Wfs20PreparedAdaptersLog.QueryParameterConversionFailed(_logger, ex);
            return Task.FromResult(QueryAdapterResult.Failure("Invalid WFS query parameters."));
        }
    }
}

/// <summary>
/// Converts prepared WFS transaction operations into the unified edit model.
/// </summary>
internal sealed class Wfs20EditParameterAdapter(
    ILogger<Wfs20EditParameterAdapter> logger) : IEditParameterAdapter<Wfs20EditRequest>
{
    private readonly ILogger<Wfs20EditParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "WFS20";

    public ProtocolEditLimits DefaultLimits => ProtocolEditLimits.Wfs20;

    public TransactionSemantics TransactionSemantics => TransactionSemantics.Wfs20;

    public Task<EditAdapterResult> ConvertAsync(
        Wfs20EditRequest protocolRequest,
        LayerDefinition layer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (protocolRequest.Operations.IsDefaultOrEmpty)
            {
                return Task.FromResult(EditAdapterResult.Failure("WFS transaction cannot be empty."));
            }

            var operations = protocolRequest.Operations
                .Select(ToUnifiedOperation)
                .ToImmutableArray();

            var editRequest = UnifiedEditRequest.WithOperations(operations) with
            {
                TransactionOptions = new EditTransactionOptions
                {
                    RollbackOnFailure = protocolRequest.RollbackOnFailure,
                    UseExplicitTransaction = protocolRequest.RollbackOnFailure,
                    IsolationLevel = protocolRequest.RollbackOnFailure
                        ? TransactionIsolationLevel.Serializable
                        : TransactionIsolationLevel.ReadCommitted,
                    TimeoutMs = 300_000
                },
                ValidationOptions = EditValidationOptions.Strict()
            };

            var metadata = new Dictionary<string, object>
            {
                ["rollbackOnFailure"] = protocolRequest.RollbackOnFailure
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
            Wfs20PreparedAdaptersLog.TransactionConversionFailed(_logger, ex);
            return Task.FromResult(EditAdapterResult.Failure("Invalid WFS transaction."));
        }
    }

    private static UnifiedEditOperation ToUnifiedOperation(FeatureEditOperation operation)
    {
        return operation.Kind switch
        {
            FeatureEditOperationKind.Create => UnifiedEditOperation.Create(
                EditFeature.ForCreate(
                    operation.Feature?.Geometry,
                    operation.Feature?.Attributes ?? ImmutableDictionary<string, object?>.Empty)),
            FeatureEditOperationKind.Update => CreateUpdateOperation(operation),
            FeatureEditOperationKind.Delete => UnifiedEditOperation.Delete(
                operation.ObjectId ?? throw new InvalidOperationException("Delete operation is missing an object ID.")),
            _ => throw new InvalidOperationException($"Unsupported feature edit operation kind '{operation.Kind}'.")
        };
    }

    private static UnifiedEditOperation CreateUpdateOperation(FeatureEditOperation operation)
    {
        var feature = operation.Feature ?? throw new InvalidOperationException("Update operation is missing a feature payload.");
        return UnifiedEditOperation.Update(
            EditFeature.ForUpdate(
                feature.Id,
                feature.Geometry,
                feature.Attributes,
                EditUpdateMode.Replace));
    }
}
