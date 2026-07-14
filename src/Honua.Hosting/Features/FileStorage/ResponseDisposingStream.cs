// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.IO;

namespace Honua.FileStorage;

internal sealed class ResponseDisposingStream : DelegatingStream
{
    private readonly IDisposable _response;
    private bool _disposed;

    public ResponseDisposingStream(Stream inner, IDisposable response)
        : base(inner)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _disposed = true;

        using (_response)
        {
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Not a `using`/`await using` declaration: `_response` is a plain IDisposable field
        // that may also implement IAsyncDisposable at runtime, so the correct disposal path
        // (sync vs. async) can only be chosen here with a runtime type check.
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_response is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _response.Dispose();
            }
        }
    }
}
