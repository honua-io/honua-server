// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>Identifies replica uploads throughout the canonical feature write pipeline.</summary>
public static class ReplicaUploadOriginScope
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The replica whose edits the current async flow is applying.</summary>
    public static string? Current => _current.Value;

    /// <summary>Begins a scope, restoring the previous origin when disposed.</summary>
    public static IDisposable Begin(string replicaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        var previous = _current.Value;
        _current.Value = replicaId;
        return new Releaser(previous);
    }

    private sealed class Releaser(string? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (!_disposed)
            {
                _current.Value = previous;
                _disposed = true;
            }
        }
    }
}
