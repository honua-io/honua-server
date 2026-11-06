// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public interface ILayerSnapshotProgress
{
    Task ReportAsync(long rowsMaterialized, long rowsTotal, CancellationToken cancellationToken);
}

public static class LayerSnapshotProgressScope
{
    private static readonly AsyncLocal<ILayerSnapshotProgress?> CurrentSlot = new();

    public static ILayerSnapshotProgress? Current => CurrentSlot.Value;

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
