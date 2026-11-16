// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    private async Task<FeatureProviderEditCapabilities> MetadataProviderEditsAsync(
        GrpcLayerContext layer, ServerCallContext context)
    {
        var router = context.GetHttpContext().RequestServices.GetService<FeatureProviderQueryRouter>();
        if (router is null || layer.Snapshot is null)
        {
            return FeatureProviderEditCapabilities.ReadOnly;
        }
        var provider = await router.TryResolveProviderAsync(layer.Snapshot,
            layer.Snapshot.ResolveStorageBinding(layer.Publication), context.CancellationToken).ConfigureAwait(false);
        return provider?.Writer is not null && await SupportsGlobalIdWritesAsync(layer, context).ConfigureAwait(false)
            ? provider.Capabilities.Edits : FeatureProviderEditCapabilities.ReadOnly;
    }

    private async Task<bool> SupportsGlobalIdWritesAsync(GrpcLayerContext layer, ServerCallContext context)
    {
        // Legacy resources have no metadata-declared GlobalID binding. For metadata-backed
        // resources, check the resolved reader so an explicit connection cannot bypass
        // the managed table's UUID uniqueness prerequisite.
        if (layer.Snapshot is null || string.IsNullOrWhiteSpace(layer.Resource.Editing?.GlobalIdField))
        {
            return true;
        }
        var reader = await ResolveReaderAsync(layer, FeatureProviderReadOperation.Query,
            context.GetHttpContext().RequestServices, context.CancellationToken).ConfigureAwait(false);
        return reader is IManagedFeatureWriteSupport managed
            && await managed.SupportsManagedWritesAsync(layer.StorageLayerId, context.CancellationToken).ConfigureAwait(false);
    }
}
