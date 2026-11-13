// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class CdnInvalidationTests
{
    private readonly CloudFrontLimits _cloudFront = new();
    private readonly ManualTimeProvider _time = new();

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_OneEdit_SubmitsAFewWildcardPathsForThatLayer()
    {
        await Invalidation().InvalidateEditedLayerAsync("Parcels", 3, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().BeEquivalentTo(
            "/tiles/3/*", "/ogc/*", "/rest/services/Parcels/*", "/rest/services/parcels/*");
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_ManyEditsToOneLayer_TellTheCdnOnceNowAndOnceWhenTheWindowCloses()
    {
        var invalidation = Invalidation();

        for (var edit = 0; edit < 50; edit++)
        {
            await invalidation.InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);
        }

        _cloudFront.Accepted.Should().HaveCount(1);

        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(2);

        _cloudFront.Rejected.Should().Be(0);
    }

    [UnitTest]
    public async Task InvalidateEditedStorageLayerAsync_ManyEdits_CoalescesAllPublicationSurfaces()
    {
        var invalidation = Invalidation();
        for (var edit = 0; edit < 50; edit++)
        {
            await invalidation.InvalidateEditedStorageLayerAsync(741, CancellationToken.None);
        }
        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(2);
        _cloudFront.Accepted[1].Should().Equal("/*");
        _cloudFront.Rejected.Should().Be(0);
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_TwentyLayersEditedWhileNothingCompletes_StaysUnderTheWildcardLimit()
    {
        var invalidation = Invalidation();

        for (var layer = 0; layer < 20; layer++)
        {
            await invalidation.InvalidateEditedLayerAsync($"service{layer}", layer, CancellationToken.None);
        }

        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(2);

        _cloudFront.Rejected.Should().Be(0);
        _cloudFront.Accepted[1].Should().Equal("/*");
        _cloudFront.WildcardPathsInProgress.Should().BeLessThanOrEqualTo(CloudFrontLimits.MaxWildcardPathsInProgress);
    }

    [UnitTest]
    public async Task InvalidateNowAsync_CloudFrontBusyThenFree_IsRetriedAndSubmitted()
    {
        _cloudFront.OccupyEveryWildcardSlot();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var pending = Service(accessor).InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None);
        _cloudFront.CompleteEverythingInProgress();
        _time.Advance(CdnInvalidationService.BusyRetryDelays[0]);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        result.Outcome.Should().Be(CdnInvalidationOutcome.Submitted);
        _cloudFront.Rejected.Should().Be(1);
        accessor.HttpContext.Response.Headers[CdnInvalidationService.ResultHeader].ToString().Should().Be("submitted");
    }

    [UnitTest]
    public async Task InvalidateNowAsync_CloudFrontStaysBusy_GivesUpAfterBoundedRetriesAndReportsTheFailure()
    {
        _cloudFront.OccupyEveryWildcardSlot();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var pending = Service(accessor).InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None);
        foreach (var delay in CdnInvalidationService.BusyRetryDelays)
        {
            await _time.TimerIsWaiting();
            _time.Advance(delay);
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        result.Failed.Should().BeTrue();
        _cloudFront.Rejected.Should().Be(CdnInvalidationService.BusyRetryDelays.Length + 1);
        accessor.HttpContext.Response.Headers[CdnInvalidationService.ResultHeader].ToString().Should().Be("failed");
    }

    [UnitTheory]
    [InlineData("Parcels", 3)]
    [InlineData(null, null)]
    public async Task InvalidateLayerAsync_AdministrativeChange_InvalidatesEverythingBeforeReturning(string? serviceId, int? layerId)
    {
        await Invalidation().InvalidateLayerAsync(serviceId, layerId, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateCollectionAsync_OperatorPurge_InvalidatesEverythingBeforeReturning()
    {
        await Invalidation().InvalidateCollectionAsync("parcels", CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateOgcMetadataAsync_OperatorPurge_InvalidatesEverythingBeforeReturning()
    {
        await Invalidation().InvalidateOgcMetadataAsync(CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateEditedCollectionAsync_ManyEdits_InvalidateTheOgcSurfacesThroughTheEditWindow()
    {
        var invalidation = Invalidation();

        for (var edit = 0; edit < 20; edit++)
        {
            await invalidation.InvalidateEditedCollectionAsync("parcels", CancellationToken.None);
            await invalidation.InvalidateEditedOgcMetadataAsync(CancellationToken.None);
        }

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/ogc/*");
    }

    [UnitTest]
    public async Task InvalidateServiceCatalogAsync_ServiceSecured_InvalidatesEverythingBeforeReturning()
    {
        await Invalidation().InvalidateServiceCatalogAsync("Parcels", null, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    private OutputCacheInvalidationService Invalidation()
        => new(
            null,
            null,
            null,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            null,
            NullLogger<OutputCacheInvalidationService>.Instance,
            Service());

    [UnitTest]
    public async Task InvalidateNowAsync_AdminRequest_RecordsThePurgeWithTheRequestCorrelationId()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers["X-Correlation-ID"] = "change-42";
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(_cloudFront, _time, logger, new HttpContextAccessor { HttpContext = context });

        await service.InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None);

        logger.Messages.Should().ContainSingle(message => message.StartsWith("CDN purge reason=admin change=change-42 ", StringComparison.Ordinal))
            .Which.Should().Contain("outcome=Submitted").And.Contain("paths=/*");
    }

    [UnitTest]
    public async Task InvalidateNowAsync_ReleaseCause_RecordsTheRelease()
    {
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(_cloudFront, _time, logger);

        await service.InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None, CdnPurgeCause.Release("sha256:abc"));

        logger.Messages.Should().ContainSingle(message => message.StartsWith("CDN purge reason=release change=sha256:abc ", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task InvalidateNowAsync_NoCdnConfigured_RecordsNothing()
    {
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(new NoCdnInvalidator(), _time, logger);

        await service.InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None);

        logger.Messages.Should().BeEmpty();
    }

    private CdnInvalidationService Service(IHttpContextAccessor? accessor = null)
        => new(_cloudFront, _time, NullLogger<CdnInvalidationService>.Instance, accessor);

    // Enforces what CloudFront enforces: at most 15 wildcard paths in progress across the distribution, and 3000 paths in one request.
    private sealed class CloudFrontLimits : ICdnInvalidator
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

    private sealed class ManualTimeProvider : TimeProvider
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

    private sealed class RecordingLogger : ILogger<CdnInvalidationService>
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
}
