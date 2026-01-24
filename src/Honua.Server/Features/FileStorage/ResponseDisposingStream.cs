// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.IO;

namespace Honua.Server.Features.FileStorage;

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
        if (!_disposed && disposing)
        {
            Inner.Dispose();
            _response.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await Inner.DisposeAsync();
            _response.Dispose();
            _disposed = true;
        }

        await base.DisposeAsync();
    }
}
