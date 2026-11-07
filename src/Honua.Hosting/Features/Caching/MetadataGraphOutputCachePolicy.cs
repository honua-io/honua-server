// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

internal sealed class MetadataGraphOutputCachePolicy : IOutputCachePolicy
{
    public async ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await context.HttpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
                .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            context.CacheVaryByRules.VaryByValues["metadata-graph"] = snapshot.Etag;
        }
        catch (Exception exception) when (ServiceResourceValidationHelpers.IsCatalogStorageUnavailable(exception))
        {
            context.EnableOutputCaching = false;
            context.AllowCacheLookup = false;
            context.AllowCacheStorage = false;
        }
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
