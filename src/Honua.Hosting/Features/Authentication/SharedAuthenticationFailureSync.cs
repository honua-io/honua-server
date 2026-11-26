// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// Writes each counted failure to the ledger and reads active refusals on each poll. The first poll runs
// before the host starts serving, so a restart keeps the refusals already in force. A ledger fault leaves
// the per-instance limiter in charge.
internal sealed class SharedAuthenticationFailureSync(
    SharedAuthenticationFailures? shared,
    TimeProvider timeProvider,
    ILogger<SharedAuthenticationFailureSync> logger) : BackgroundService
{
    private readonly AuthenticationFailureLimiterOptions _options = shared?.Options ?? new();

    private readonly TaskCompletionSource _writerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool IsActive => shared is not null && _options.Enabled;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsActive)
        {
            await PollAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (IsActive && ExecuteTask is not null)
        {
            shared!.Complete();
            await _writerFinished.Task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!_writerFinished.Task.IsCompleted)
            {
                AuthenticationLog.SharedFailureDrainTimedOut(logger);
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => IsActive ? Task.WhenAll(WriteFailuresAsync(stoppingToken), PollRefusalsAsync(stoppingToken)) : Task.CompletedTask;

    internal async Task RecordAsync(PendingAuthenticationFailure recorded, CancellationToken cancellationToken)
    {
        var digest = SharedAuthenticationFailures.Digest(recorded.Source);
        SharedFailureWindow? window = null;
        try
        {
            var observedAt = shared!.Now;
            if (observedAt - recorded.At >= _options.Window)
            {
                return;
            }

            var policy = new AuthenticationFailurePolicy(_options.Threshold, _options.Window, _options.BlockDuration);
            var persisted = await shared.Ledger
                .RecordFailureAsync(new(digest, policy), cancellationToken)
                .ConfigureAwait(false);
            window = persisted?.At(observedAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AuthenticationLog.SharedFailureStoreUnavailable(logger, exception);
        }
        finally
        {
            shared!.Acknowledge(recorded.Source, window is { } persisted ? (digest, persisted) : null, shared.Now);
        }
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        var observedAt = shared!.Now;
        try
        {
            var refusals = await shared.Ledger
                .ReadRefusalsAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = shared.Now;
            shared.Prune(now);
            foreach (var (digest, window) in refusals)
            {
                shared.Apply(digest, window.At(observedAt), now);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AuthenticationLog.SharedFailureStoreUnavailable(logger, exception);
        }
    }

    private async Task WriteFailuresAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var recorded in shared!.Recorded.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RecordAsync(recorded, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writerFinished.TrySetResult();
        }
    }

    private async Task PollRefusalsAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SharedPollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
