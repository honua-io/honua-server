// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal sealed partial class FeatureServerQueryHandler
{
    private sealed record StreamingQueryDispatch
    {
        public bool UsePbfStreaming { get; init; }
        public required FeatureQuery Query { get; init; }
        public required QueryParameters ValidatedParams { get; init; }
        public int? OutputSrid { get; init; }
        public QuantizationTransform? QuantizationTransform { get; init; }
        public required HttpContext Context { get; init; }
        public required string Format { get; init; }
    }

    private Task ExecuteStreamingQueryAsync(
        QueryLayerContext queryLayer,
        StreamingQueryDispatch dispatch,
        CancellationToken cancellationToken)
    {
        if (dispatch.UsePbfStreaming)
        {
            return _queryExecutor.StreamPbfQueryAsync(
                new FeatureServerQueryExecutor.PbfStreamQueryRequest
                {
                    Service = queryLayer.Service,
                    Resource = queryLayer.Resource,
                    Publication = queryLayer.Publication,
                    StorageLayerId = queryLayer.StorageLayerId,
                    Query = dispatch.Query,
                    QueryParams = dispatch.ValidatedParams,
                    OutputSrid = dispatch.OutputSrid,
                    QuantizationTransform = dispatch.QuantizationTransform,
                    Context = dispatch.Context
                },
                cancellationToken);
        }

        return _queryExecutor.StreamQueryAsync(
            queryLayer.Service,
            queryLayer.Resource,
            queryLayer.Publication,
            queryLayer.StorageLayerId,
            dispatch.Query,
            dispatch.ValidatedParams,
            dispatch.OutputSrid,
            dispatch.Context,
            dispatch.Format,
            cancellationToken);
    }

    private bool CanStreamPbf(MetadataV2Object resource, QueryParameters validatedParams, int effectiveLimit)
    {
        if (effectiveLimit <= StreamingThreshold
            || validatedParams.ReturnZ
            || validatedParams.ReturnM
            || validatedParams.ReturnDistance
            || validatedParams.ReturnDistinctValues
            || _queryExecutor.HasComputedFields
            || resource.SchemaFields.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(validatedParams.OutFields))
        {
            return true;
        }

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        return !validatedParams.OutFields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(field =>
                !string.Equals(field, "*", StringComparison.Ordinal) &&
                !field.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) &&
                !resource.SchemaFields.Any(schemaField =>
                    schemaField.Name.Equals(field, StringComparison.OrdinalIgnoreCase)));
    }
}
