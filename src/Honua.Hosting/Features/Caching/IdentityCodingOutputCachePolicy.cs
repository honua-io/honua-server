// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

// Binary tile routes never produce a compressible type, so their body and strong validator are the same for every
// coding. Sharing one entry keeps mixed clients from storing a copy of each tile per negotiated coding.
// Runs after the base policy and overwrites its content-coding key.
internal sealed class IdentityCodingOutputCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        context.CacheVaryByRules.VaryByValues[OutputCachingRegistration.ContentCodingKey] = OutputCachingRegistration.IdentityCoding;
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
