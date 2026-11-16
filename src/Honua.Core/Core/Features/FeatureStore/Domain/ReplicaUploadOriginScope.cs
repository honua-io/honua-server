// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public static class ReplicaUploadOriginScope
{
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current => _current.Value;

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
