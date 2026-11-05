// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

// GeoServices errors are HTTP 200, so an error envelope must not be stored or a transient failure is replayed for the cache lifetime.
internal sealed class BypassOutputCacheOnErrorEnvelopePolicy : IOutputCachePolicy
{
    private static readonly object ErrorEnvelopeCodeKey = new();

    internal static void MarkErrorEnvelope(HttpContext context, int code)
        => context.Items[ErrorEnvelopeCodeKey] = code;

    internal static bool TryGetErrorEnvelopeCode(HttpContext context, out int code)
    {
        if (context.Items.TryGetValue(ErrorEnvelopeCodeKey, out var value) && value is int envelopeCode)
        {
            code = envelopeCode;
            return true;
        }

        code = 0;
        return false;
    }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (TryGetErrorEnvelopeCode(context.HttpContext, out _))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
