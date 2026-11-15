// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.SharedCaching;

// Whether an edit made now would reach the CDN as a tag purge. Purge-backed lifetimes are only given while it would:
// otherwise an edit could stay hidden behind a day-long CDN copy. Every doubt answers no, and the answer is cheap
// because requests read it: the shared budget counter is read in the background at most once a minute.
internal sealed partial class CdnPurgeReliability(
    ICdnInvalidator invalidator,
    ILogger<CdnPurgeReliability> logger,
    CdnPurgeCoordination? coordination = null)
{
    internal static readonly TimeSpan BudgetReadInterval = TimeSpan.FromMinutes(1);

    // After a failed purge this task stops giving long lifetimes for this long; responses it already sent keep theirs.
    internal static readonly TimeSpan FailureHoldOff = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _clock = coordination?.TimeProvider ?? TimeProvider.System;
    private readonly IOptionsMonitor<SharedCacheOptions>? _options = coordination?.Options;
    private readonly ICdnPurgeBudget? _budget = coordination?.Budget;
    private readonly Lock _gate = new();
    private DateTimeOffset _budgetReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;
    private bool _budgetHasHeadroom;
    private Task? _budgetRead;

    internal bool PurgesReachCdn()
    {
        if (invalidator is NoCdnInvalidator || _options?.CurrentValue is not { CacheTagsEnabled: true } options)
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (now - _lastFailure < FailureHoldOff)
            {
                return false;
            }

            if (now - _budgetReadAt >= BudgetReadInterval && _budgetRead is null)
            {
                _budgetRead = ReadBudgetInBackgroundAsync(options.PurgeBudget);
            }

            return _budgetHasHeadroom;
        }
    }

    internal void RecordFailedPurge()
    {
        lock (_gate)
        {
            _lastFailure = _clock.GetUtcNow();
        }
    }

    // The budget was found spent by a purge on this task; no need to wait for the next read.
    internal void RecordBudgetSpent()
    {
        lock (_gate)
        {
            _budgetHasHeadroom = false;
            _budgetReadAt = _clock.GetUtcNow();
        }
    }

    // Awaited by tests and by the background read.
    internal async Task ReadBudgetAsync(CdnPurgeBudgetOptions budget, CancellationToken cancellationToken)
    {
        var hasHeadroom = await HasHeadroomAsync(budget, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _budgetHasHeadroom = hasHeadroom;
            _budgetReadAt = _clock.GetUtcNow();
        }
    }

    private async Task ReadBudgetInBackgroundAsync(CdnPurgeBudgetOptions budget)
    {
        await Task.Yield();
        try
        {
            await ReadBudgetAsync(budget, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _budgetRead = null;
            }
        }
    }

    private async Task<bool> HasHeadroomAsync(CdnPurgeBudgetOptions budget, CancellationToken cancellationToken)
    {
        // Without a counter or with the cap off, no edit purge is ever refused for budget.
        if (_budget is null || budget.MonthlyEditSubmissions <= 0)
        {
            return true;
        }

        try
        {
            var month = await _budget.ReadCurrentMonthAsync(cancellationToken).ConfigureAwait(false);
            if (month is { Decision: CdnPurgeBudgetDecision.Unavailable })
            {
                return false;
            }

            var reserve = (long)budget.MonthlyEditSubmissions * SharedCachePurgeBacked.BudgetHeadroomPercent / 100;
            var hasHeadroom = (month?.SubmittedCount ?? 0) < budget.MonthlyEditSubmissions - reserve;
            if (!hasHeadroom)
            {
                Log.PurgeBackedLifetimesPaused(logger, month?.SubmittedCount ?? 0, budget.MonthlyEditSubmissions);
            }

            return hasHeadroom;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.BudgetUnreadable(logger, exception);
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 10316,
            Level = LogLevel.Information,
            Message = "Purge-backed CDN lifetimes are paused: the monthly edit-purge budget is nearly spent. submitted={Submitted} limit={Limit}")]
        public static partial void PurgeBackedLifetimesPaused(ILogger logger, int submitted, int limit);

        [LoggerMessage(
            EventId = 10317,
            Level = LogLevel.Warning,
            Message = "The CDN purge budget could not be read; responses get the ordinary CDN lifetimes until it can.")]
        public static partial void BudgetUnreadable(ILogger logger, Exception exception);
    }
}
