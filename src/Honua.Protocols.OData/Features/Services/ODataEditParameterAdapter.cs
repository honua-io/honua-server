// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Protocol request shape for OData edit operations after payload parsing.
/// </summary>
internal sealed record ODataEditRequest
{
    public required ODataOperation Operation { get; init; }

    public required ParsedFeaturePayload Payload { get; init; }

    public byte[]? GeometryWkb { get; init; }

    public long? ObjectId { get; init; }

    public string? IfMatch { get; init; }

    public string? IfNoneMatch { get; init; }
}

/// <summary>
/// Supported OData entity mutation operations.
/// </summary>
internal enum ODataOperation
{
    Create,
    Update,
    Patch,
    Delete
}

/// <summary>
/// Converts parsed OData mutation requests into the shared unified edit model.
/// </summary>
internal sealed class ODataEditParameterAdapter(
    ILogger<ODataEditParameterAdapter> logger) : IEditParameterAdapter<ODataEditRequest>
{
    private readonly ILogger<ODataEditParameterAdapter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string ProtocolName => "OData";

    public ProtocolEditLimits DefaultLimits => ProtocolEditLimits.OData;

    public TransactionSemantics TransactionSemantics => Core.Features.Edit.TransactionSemantics.OData;

    /// <summary>
    /// Converts the parsed OData edit request into the shared edit model. The OData
    /// adapter does not need layer metadata to build the unified edit; field-level
    /// validation happens in the v2 edit processor after conversion.
    /// </summary>
    public Task<EditAdapterResult> ConvertAsync(
        ODataEditRequest protocolRequest,
        MetadataV2Resource resource,
        CancellationToken cancellationToken = default)
        => ConvertCoreAsync(protocolRequest);

    private Task<EditAdapterResult> ConvertCoreAsync(ODataEditRequest protocolRequest)
    {
        try
        {
            var attributes = protocolRequest.Payload.Attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            var geometry = protocolRequest.GeometryWkb;

            EditFeature? feature = protocolRequest.Operation switch
            {
                ODataOperation.Create => EditFeature.ForCreate(geometry, attributes),
                ODataOperation.Update => CreateUpdateFeature(protocolRequest, geometry, attributes, EditUpdateMode.Replace),
                ODataOperation.Patch => CreateUpdateFeature(protocolRequest, geometry, attributes, EditUpdateMode.Merge),
                ODataOperation.Delete => null,
                _ => throw new InvalidOperationException($"Unsupported OData operation {protocolRequest.Operation}.")
            };

            var editRequest = protocolRequest.Operation switch
            {
                ODataOperation.Create => UnifiedEditRequest.WithCreates(ImmutableArray.Create(feature!.Value)),
                ODataOperation.Update or ODataOperation.Patch => UnifiedEditRequest.WithUpdates(ImmutableArray.Create(feature!.Value)),
                ODataOperation.Delete => UnifiedEditRequest.WithDeletes(ImmutableArray.Create(
                    protocolRequest.ObjectId ?? throw new InvalidOperationException("Object ID is required for delete operations."))),
                _ => throw new InvalidOperationException($"Unsupported OData operation {protocolRequest.Operation}.")
            };

            var transaction = EditTransaction.Create(
                Guid.NewGuid().ToString(),
                ProtocolName,
                ImmutableArray.Create(editRequest),
                TransactionConfiguration.ForOData,
                new Dictionary<string, object>
                {
                    ["operation"] = protocolRequest.Operation.ToString()
                }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));

            return Task.FromResult(EditAdapterResult.Success(editRequest, transaction));
        }
        catch (Exception ex)
        {
            ODataPreparedAdaptersLog.EditParameterConversionFailed(_logger, ex);
            return Task.FromResult(EditAdapterResult.Failure("Invalid OData edit request."));
        }
    }

    private static EditFeature CreateUpdateFeature(
        ODataEditRequest request,
        byte[]? geometry,
        ImmutableDictionary<string, object?> attributes,
        EditUpdateMode updateMode)
    {
        var objectId = request.ObjectId
            ?? throw new InvalidOperationException("Object ID is required for update operations.");

        if (!string.IsNullOrWhiteSpace(request.IfMatch))
        {
            return EditFeature.ForConditionalUpdate(objectId, geometry, attributes, request.IfMatch!, updateMode);
        }

        return EditFeature.ForUpdate(objectId, geometry, attributes, updateMode);
    }
}
