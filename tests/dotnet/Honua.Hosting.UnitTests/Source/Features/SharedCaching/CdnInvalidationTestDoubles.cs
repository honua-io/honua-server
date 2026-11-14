// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

// Enforces what CloudFront enforces: at most 15 wildcard paths in progress across the distribution, and 3000 paths in one request.
internal sealed class CloudFrontLimits : ICdnInvalidator
{
    internal const int MaxWildcardPathsInProgress = 15;
    private const int MaxPathsPerRequest = 3000;

    private readonly Lock _gate = new();
    private readonly List<IReadOnlyCollection<string>> _accepted = [];
    private TaskCompletionSource _acceptedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<IReadOnlyCollection<string>> Accepted
    {
        get
        {
            lock (_gate)
            {
                return [.. _accepted];
            }
        }
    }

    public int Rejected { get; private set; }

    public int WildcardPathsInProgress { get; private set; }

    public void OccupyEveryWildcardSlot() => WildcardPathsInProgress = MaxWildcardPathsInProgress;

    public void CompleteEverythingInProgress() => WildcardPathsInProgress = 0;

    public async Task AcceptedCountReaches(int count)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_accepted.Count >= count)
                {
                    return;
                }

                changed = _acceptedChanged.Task;
            }

            await changed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public Task<CdnInvalidationResult> InvalidateAsync(
        IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var wildcards = paths.Count(path => path.Contains('*'));
            if (paths.Count > MaxPathsPerRequest)
            {
                Rejected++;
                return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "TooManyInvalidationPaths"));
            }

            if (WildcardPathsInProgress + wildcards > MaxWildcardPathsInProgress)
            {
                Rejected++;
                return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Busy, "TooManyInvalidationsInProgress"));
            }

            WildcardPathsInProgress += wildcards;
            _accepted.Add(paths);
            _acceptedChanged.TrySetResult();
            _acceptedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "I1"));
        }
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, _now + dueTime);
        lock (_gate)
        {
            _timers.Add(timer);
            _timerCreated.TrySetResult();
        }

        return timer;
    }

    public async Task TimerIsWaiting()
    {
        Task created;
        lock (_gate)
        {
            if (_timers.Count > 0)
            {
                return;
            }

            _timerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            created = _timerCreated.Task;
        }

        await created.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Advance(TimeSpan elapsed)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += elapsed;
            due = [.. _timers.Where(timer => timer.DueAt <= _now)];
            _timers.RemoveAll(due.Contains);
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
    {
        public DateTimeOffset DueAt { get; } = dueAt;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class RecordingLogger : ILogger<CdnInvalidationService>
{
    private readonly Lock _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}

internal sealed class StaticOptions(SharedCacheOptions value) : IOptionsMonitor<SharedCacheOptions>
{
    public SharedCacheOptions CurrentValue => value;

    public SharedCacheOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<SharedCacheOptions, string?> listener) => null;
}

// The leases table as the tasks of one deployment share it: expiry by the store's clock, and a holder cannot take its own lease again.
internal sealed class SharedLeases(TimeProvider clock) : ILeaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _expiries = new(StringComparer.Ordinal);

    public int Claims { get; private set; }

    public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Claims++;
            var now = clock.GetUtcNow();
            if (_expiries.TryGetValue(claim.LeaseKey, out var expiresAt) && expiresAt > now)
            {
                return Task.FromResult(false);
            }

            _expiries[claim.LeaseKey] = now + duration;
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

// The catalog answers lookups made without a request and times out those made for one, as a slow catalog does at the edit's deadline.
internal sealed class CatalogTimingOutForRequests(MetadataV2Graph graph) : IMetadataV2GraphProvider
{
    private readonly TestMetadataV2GraphProvider _catalog = new(graph);

    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        => cancellationToken.CanBeCanceled
            ? ValueTask.FromException<MetadataV2GraphSnapshot>(new OperationCanceledException(cancellationToken))
            : _catalog.GetCurrentAsync(cancellationToken);

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
        => _catalog.GetByRevisionAsync(revision, cancellationToken);
}

internal sealed class UnreachableLeases : ILeaseStore
{
    public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        => throw new TimeoutException("database unreachable");

    public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
