// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features.Services;

// OGC API Features edits delegate to the canonical OGC edit gate, adapting the OGC Features
// request metadata onto OgcProtocolEditGate.Target.
internal static partial class OgcFeatureEditGate
{
    // LayerId is the storage layer the write goes to; hooks are told the published layer in Request.
    internal readonly record struct Target(HttpContext Context, MetadataV2Object Resource, OgcFeaturesEditRequest Request)
    {
        public required int LayerId { get; init; }

        public required IFeatureReader Reader { get; init; }
    }

    // Ended is the result that stops the edit before the write; any rejection fails the whole request.
    internal static async Task<(FeatureEditBatch Batch, FeatureEditResult? Ended)> PrepareAsync(
        Target target,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var prepared = await OgcProtocolEditGate.PrepareAsync(
            ToSharedTarget(target),
            batch,
            cancellationToken).ConfigureAwait(false);
        return (prepared.Batch, prepared.Ended);
    }

    internal static Task RunAfterAsync(Target target, FeatureEditBatch batch, FeatureEditResult result)
        => OgcProtocolEditGate.RunAfterAsync(ToSharedTarget(target), batch, result);

    private static OgcProtocolEditGate.Target ToSharedTarget(Target target) => new(target.Context, target.Resource)
    {
        LayerId = target.LayerId,
        Reader = target.Reader,
        ServiceName = target.Request.ServiceName,
        PublicLayerId = target.Request.PublicLayerId,
        StoredRows = StoredRows(target.Request),
        InvalidDataFallbackMessage = OgcFeatureEditFailureMapper.InvalidDataMessage,
        AlwaysLoadUpdateSnapshots = true,
        FailWholeBatchOnRejection = true,
    };
}
