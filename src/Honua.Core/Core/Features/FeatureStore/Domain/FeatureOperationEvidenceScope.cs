// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public static class FeatureOperationEvidenceScope
{
    private static readonly AsyncLocal<object?> _current = new();

    public static bool IsActive => _current.Value is not null;

    public static IDisposable Begin()
    {
        var previous = _current.Value;
        _current.Value = new object();
        return new Releaser(previous);
    }

    private sealed class Releaser(object? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _current.Value = previous;
            _disposed = true;
        }
    }
}
