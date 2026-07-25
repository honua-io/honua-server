// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Internal;

/// <summary>
/// Centralizes disposal for resources whose ownership is transferred, conditional, or
/// deliberately extends beyond the scope in which the resource was created.
/// </summary>
internal static class DeferredDisposal
{
    /// <summary>
    /// Disposes a resource after its owning operation has completed.
    /// </summary>
    /// <param name="resource">The resource to dispose, or <see langword="null"/>.</param>
    public static void Dispose(IDisposable? resource)
    {
        resource?.Dispose();
    }

    /// <summary>
    /// Disposes each resource after its owning operation has completed.
    /// </summary>
    /// <typeparam name="T">The disposable resource type.</typeparam>
    /// <param name="resources">The resources to dispose.</param>
    public static void DisposeAll<T>(IEnumerable<T> resources)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var resource in resources)
        {
            resource.Dispose();
        }
    }

    /// <summary>
    /// Asynchronously disposes a resource after its owning operation has completed.
    /// </summary>
    /// <param name="resource">The resource to dispose, or <see langword="null"/>.</param>
    /// <returns>A task that represents disposal completion.</returns>
    public static ValueTask DisposeAsync(IAsyncDisposable? resource)
    {
        return resource is null ? ValueTask.CompletedTask : resource.DisposeAsync();
    }
}
