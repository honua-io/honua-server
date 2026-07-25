// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.IO;

namespace Honua.FileStorage;

internal sealed class ResponseDisposingStream : DelegatingStream
{
    private readonly Action _disposeResponse;
    private readonly Func<ValueTask>? _disposeResponseAsync;
    private bool _disposed;

    public ResponseDisposingStream(Stream inner, IDisposable response)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(response);
        _disposeResponse = response.Dispose;
        _disposeResponseAsync = response is IAsyncDisposable asyncDisposable
            ? asyncDisposable.DisposeAsync
            : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            _disposeResponse();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_disposeResponseAsync is not null)
            {
                await _disposeResponseAsync().ConfigureAwait(false);
            }
            else
            {
                _disposeResponse();
            }
        }
    }
}
