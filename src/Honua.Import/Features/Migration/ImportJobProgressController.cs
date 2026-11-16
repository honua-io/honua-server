// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;

namespace Honua.Migration;

internal sealed class ImportJobProgressController<TProgress> : IDisposable
    where TProgress : class
{
    private readonly string _jobId;
    private readonly IDistributedProgressStore<TProgress> _progressStore;
    private readonly TimeSpan? _finalProgressTtl;
    private readonly SemaphoreSlim _progressGate = new(1, 1);
    private int _finalized;

    public ImportJobProgressController(
        string jobId,
        IDistributedProgressStore<TProgress> progressStore,
        TimeSpan? finalProgressTtl = null)
    {
        _jobId = jobId;
        _progressStore = progressStore;
        _finalProgressTtl = finalProgressTtl;
    }

    public TProgress? CurrentProgress { get; private set; }

    public bool IsFinalized => Volatile.Read(ref _finalized) != 0;

    public void Seed(TProgress? progress)
    {
        CurrentProgress = progress;
    }

    public async Task SetProgressAsync(TProgress progress, CancellationToken cancellationToken)
    {
        if (IsFinalized)
        {
            return;
        }

        await _progressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsFinalized)
            {
                return;
            }

            CurrentProgress = progress;
            await _progressStore.SetProgressAsync(_jobId, progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progressGate.Release();
        }
    }

    public async Task SetFinalProgressAsync(TProgress progress, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _finalized, 1);
        await _progressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            CurrentProgress = progress;
            await _progressStore.SetProgressAsync(_jobId, progress, _finalProgressTtl, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progressGate.Release();
        }
    }

    public async Task TryReportProgressAsync(
        TProgress progress,
        ILogger logger,
        Action<ILogger, string, Exception> logProgressUpdateFailed)
    {
        if (IsFinalized)
        {
            return;
        }

        try
        {
            await SetProgressAsync(progress, CancellationToken.None).ConfigureAwait(false);
        }
        // Intentionally generic: this is a best-effort progress update (see method name);
        // a failure to publish progress must not fail the import job itself.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logProgressUpdateFailed(logger, _jobId, ex);
        }
    }

    public void Dispose()
    {
        _progressGate.Dispose();
    }
}
