// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.IO;

namespace Honua.Server.Features.FileStorage;

internal sealed class CancellableReadStream : DelegatingStream
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _downloadTask;
    private bool _disposed;

    public CancellableReadStream(Stream inner, CancellationTokenSource cancellation, Task downloadTask)
        : base(inner)
    {
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _downloadTask = downloadTask ?? throw new ArgumentNullException(nameof(downloadTask));

        _ = _downloadTask.ContinueWith(
            static task => _ = task.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        _ = _downloadTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Download already completed and disposed its token source.
            }
            Inner.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Download already completed and disposed its token source.
            }
            await Inner.DisposeAsync();
            _disposed = true;
        }

        await base.DisposeAsync();
    }
}
