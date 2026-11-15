// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private static async Task<FeatureProviderEditCapabilities> MetadataProviderEditsAsync(
        GrpcLayerContext layer, ServerCallContext context)
    {
        var router = context.GetHttpContext().RequestServices.GetService<FeatureProviderQueryRouter>();
        if (router is null || layer.Snapshot is null)
        {
            return FeatureProviderEditCapabilities.ReadOnly;
        }
        var provider = await router.TryResolveProviderAsync(layer.Snapshot,
            layer.Snapshot.ResolveStorageBinding(layer.Publication), context.CancellationToken).ConfigureAwait(false);
        return provider?.Writer is not null ? provider.Capabilities.Edits : FeatureProviderEditCapabilities.ReadOnly;
    }
}
