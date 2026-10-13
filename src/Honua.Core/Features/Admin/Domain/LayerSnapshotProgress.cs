// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

/// <summary>
/// Reports canonical snapshot-copy progress from inside the publication transaction
/// without changing <c>ILayerPublishingService</c> (honua-server#5948).
/// </summary>
public interface ILayerSnapshotProgress
{
    /// <summary>
    /// Records rows copied so far against the counted total.
    /// </summary>
    /// <param name="rowsMaterialized">Rows inserted by completed pages.</param>
    /// <param name="rowsTotal">Rows counted before the copy.</param>
    /// <param name="cancellationToken">Token for the publication budget. A store write should not use it.</param>
    Task ReportAsync(long rowsMaterialized, long rowsTotal, CancellationToken cancellationToken);
}

/// <summary>
/// Ambient snapshot-copy progress for the current async flow. A null current value
/// means the caller does not record progress (synchronous publish and feature refresh).
/// </summary>
public static class LayerSnapshotProgressScope
{
    private static readonly AsyncLocal<ILayerSnapshotProgress?> CurrentSlot = new();

    /// <summary>Progress sink for the current async flow, or null.</summary>
    public static ILayerSnapshotProgress? Current => CurrentSlot.Value;

    /// <summary>
    /// Installs <paramref name="progress"/> until the returned handle is disposed.
    /// </summary>
    /// <param name="progress">Sink that receives page reports.</param>
    /// <returns>A handle that restores the previous sink.</returns>
    public static IDisposable Begin(ILayerSnapshotProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var prior = CurrentSlot.Value;
        CurrentSlot.Value = progress;
        return new Restore(prior);
    }

    private sealed class Restore(ILayerSnapshotProgress? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentSlot.Value = prior;
        }
    }
}
