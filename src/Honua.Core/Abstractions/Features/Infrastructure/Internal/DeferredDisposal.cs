// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Internal;

internal static class DeferredDisposal
{
    public static void Dispose(IDisposable? resource)
    {
        resource?.Dispose();
    }

    public static void DisposeAll<T>(IEnumerable<T> resources)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var resource in resources)
        {
            resource.Dispose();
        }
    }

    public static ValueTask DisposeAsync(IAsyncDisposable? resource)
    {
        return resource is null ? ValueTask.CompletedTask : resource.DisposeAsync();
    }
}
