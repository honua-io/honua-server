// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

internal sealed partial class ODataBatchHandler
{
    private sealed record BatchMutationRead(ODataBatchLayerContext Layer, long ObjectId,
        ODataBatchRequestItem Request, Dictionary<(int LayerId, long ObjectId), Feature?> PreparedFeatures);

    private async Task<(Feature? Existing, bool IsFirstMutation, ODataBatchResponseItem? Error)> ReadMutationSnapshotAsync(
        BatchMutationRead read, CancellationToken cancellationToken)
    {
        var isFirst = !read.PreparedFeatures.TryGetValue((read.Layer.PublicLayerId, read.ObjectId), out var existing);
        if (isFirst)
        {
            existing = await read.Layer.Reader.GetAsync(read.Layer.StorageLayerId, read.ObjectId, cancellationToken);
        }
        if (!existing.HasValue)
        {
            return (null, isFirst, CreateErrorResponse(read.Request.Id, 404, "ResourceNotFound",
                $"Feature {read.ObjectId} not found in layer {read.Layer.PublicLayerId}."));
        }
        var precondition = await ValidatePreconditionsAsync(read.Layer, existing.Value, read.Request.Headers, cancellationToken);
        return precondition.IsValid
            ? (existing, isFirst, null)
            : (null, isFirst, CreateErrorResponse(read.Request.Id, 412, "PreconditionFailed",
                precondition.ErrorMessage ?? "Precondition failed."));
    }
}
