// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

// Writes each counted failure to the ledger and polls its refusals, so a source refused by any instance is refused
// by every instance within one poll interval. The first poll runs before the host starts serving, so a restart keeps
// the refusals already in force. A ledger fault leaves the per-instance limiter in charge; it never fails a request.
internal sealed class SharedAuthenticationFailureSync(
    SharedAuthenticationFailures? shared,
    TimeProvider timeProvider,
    ILogger<SharedAuthenticationFailureSync> logger) : BackgroundService
{
    private readonly AuthenticationFailureLimiterOptions _options = shared?.Options ?? new();

    private bool IsActive => shared is not null && _options.Enabled;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsActive)
        {
            await PollAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => IsActive ? Task.WhenAll(WriteFailuresAsync(stoppingToken), PollRefusalsAsync(stoppingToken)) : Task.CompletedTask;

    internal async Task RecordAsync(string source, CancellationToken cancellationToken)
    {
        var digest = SharedAuthenticationFailures.Digest(source);
        var now = timeProvider.GetUtcNow();
        try
        {
            var policy = new AuthenticationFailurePolicy(_options.Threshold, _options.Window, _options.BlockDuration);
            var window = await shared!.Ledger
                .RecordFailureAsync(new(digest, now, policy), cancellationToken)
                .ConfigureAwait(false);
            shared.Apply(digest, window, now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AuthenticationLog.SharedFailureStoreUnavailable(logger, exception);
        }
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            var refusals = await shared!.Ledger
                .ReadRefusalsAsync(now, _options.MaxTrackedSources, cancellationToken)
                .ConfigureAwait(false);
            shared.Prune(now);
            foreach (var (digest, window) in refusals)
            {
                shared.Apply(digest, window, now);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AuthenticationLog.SharedFailureStoreUnavailable(logger, exception);
        }
    }

    // On shutdown both loops end as cancelled tasks, which the host does not treat as a fault.
    private async Task WriteFailuresAsync(CancellationToken stoppingToken)
    {
        await foreach (var source in shared!.Recorded.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await RecordAsync(source, stoppingToken).ConfigureAwait(false);
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
