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
            "/tiles/3/*", "/ogc/*", "/stac/*", "/rest/services/Parcels/*", "/rest/services/parcels/*");
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

    [UnitTest]
    public async Task InvalidateServiceCatalogAsync_AccessChangeOnServiceWithLayers_InvalidatesEverythingOnce()
    {
        await Invalidation().InvalidateServiceCatalogAsync("Parcels", [3, 4], CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateServiceCatalogAsync_AccessChangeOnServiceWithLayers_ChecksPublicExportsOnce()
    {
        var exports = new CountingExportAccessListener();

        await Invalidation(exports).InvalidateServiceCatalogAsync("Parcels", [3, 4], CancellationToken.None);

        exports.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task InvalidateServiceCatalogAsync_RequestEndedBeforeEviction_StillPurgesAndChecksExportsOnce()
    {
        var exports = new CountingExportAccessListener();
        using var ended = new CancellationTokenSource();
        await ended.CancelAsync();

        var change = () => Invalidation(exports).InvalidateServiceCatalogAsync("Parcels", [3, 4], ended.Token);

        await change.Should().ThrowAsync<OperationCanceledException>();
        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
        exports.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task InvalidateEverythingAsync_OperatorPurge_InvalidatesEverythingOnceAndChecksPublicExportsOnce()
    {
        var exports = new CountingExportAccessListener();

        await Invalidation(exports).InvalidateEverythingAsync(CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
        exports.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task InvalidateNowAsync_AdminRequest_RecordsThePurgeWithTheRequestCorrelationId()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers["X-Correlation-ID"] = "change-42";
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(_cloudFront, logger, new CdnPurgeCoordination { TimeProvider = _time, HttpContextAccessor = new HttpContextAccessor { HttpContext = context } });

        await service.InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None);

        logger.Messages.Should().ContainSingle(message => message.StartsWith("CDN purge reason=admin change=change-42 ", StringComparison.Ordinal))
            .Which.Should().Contain("outcome=Submitted").And.Contain("paths=/*");
    }

    [UnitTest]
    public async Task InvalidateNowAsync_ReleaseCause_RecordsTheRelease()
    {
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(_cloudFront, logger, new CdnPurgeCoordination { TimeProvider = _time });

        await service.InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None, CdnPurgeCause.Release("sha256:abc"));

        logger.Messages.Should().ContainSingle(message => message.StartsWith("CDN purge reason=release change=sha256:abc ", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task InvalidateNowAsync_NoCdnConfigured_RecordsNothing()
    {
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(new NoCdnInvalidator(), logger, new CdnPurgeCoordination { TimeProvider = _time });

        await service.InvalidateNowAsync(CdnPaths.Everything, CancellationToken.None);

        logger.Messages.Should().BeEmpty();
    }

    [UnitTest]
    public async Task InvalidateAfterEditAsync_WhenBackgroundBatchThrows_LogsErrorAndCancelsPendingTimersOnShutdown()
    {
        var logger = new RecordingLogger();
        var invalidator = new ThrowOnceThenRecordInvalidator();
        var service = new CdnInvalidationService(
            invalidator,
            logger,
            new CdnPurgeCoordination { TimeProvider = _time });

        // First edit opens the window immediately.
        await service.InvalidateAfterEditAsync(["/tiles/1/*"], CancellationToken.None);
        // Second edit queues into the pending window.
        await service.InvalidateAfterEditAsync(["/tiles/2/*"], CancellationToken.None);

        // Trigger the window close: invalidator throws on the background call, must be logged rather than unobserved.
        invalidator.ThrowOnNextCall = true;
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await invalidator.AttemptCountReaches(2);

        var logDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!logger.Messages.Any(message =>
                   message.Contains("Background CDN invalidation batch failed unexpectedly", StringComparison.Ordinal))
               && DateTimeOffset.UtcNow < logDeadline)
        {
            await Task.Delay(10);
        }

        logger.Messages.Should().Contain(message =>
            message.Contains("Background CDN invalidation batch failed unexpectedly", StringComparison.Ordinal));

        // Queue another pending edit and flush on shutdown: pending timer is cancelled and flush runs cleanly.
        await service.InvalidateAfterEditAsync(["/tiles/3/*"], CancellationToken.None);
        await service.FlushPendingAsync();

        invalidator.RecordedPaths.Should().Contain(paths => paths.Contains("/tiles/3/*"));
    }

    private sealed class ThrowOnceThenRecordInvalidator : ICdnInvalidator
    {
        private readonly Lock _gate = new();
        private readonly List<string[]> _recorded = [];
        private int _attempts;
        private TaskCompletionSource _attemptSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ThrowOnNextCall { get; set; }

        public IReadOnlyList<string[]> RecordedPaths
        {
            get
            {
                lock (_gate)
                {
                    return [.. _recorded];
                }
            }
        }

        public Task<CdnInvalidationResult> InvalidateAsync(
            IReadOnlyCollection<string> paths,
            CancellationToken cancellationToken = default)
        {
            var shouldThrow = false;
            lock (_gate)
            {
                _attempts++;
                if (ThrowOnNextCall)
                {
                    ThrowOnNextCall = false;
                    shouldThrow = true;
                }
                else
                {
                    _recorded.Add([.. paths]);
                }

                _attemptSignal.TrySetResult();
                _attemptSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (shouldThrow)
            {
                throw new InvalidOperationException("unexpected transport error");
            }

            return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "inv-1"));
        }

        public async Task AttemptCountReaches(int target)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_attempts >= target)
                    {
                        return;
                    }

                    wait = _attemptSignal.Task;
                }

                await wait.WaitAsync(timeout.Token);
            }
        }
    }

    private OutputCacheInvalidationService Invalidation(IPublicExportAccessChangeListener? exports = null)
    {
        var services = new ServiceCollection();
        if (exports is not null)
        {
            services.AddSingleton(exports);
        }

        return new(
            null,
            null,
            null,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            null,
            NullLogger<OutputCacheInvalidationService>.Instance,
            Service());
    }

    private sealed class CountingExportAccessListener : IPublicExportAccessChangeListener
    {
        public int Calls { get; private set; }

        public Task OnAdministrativeAccessChangeAsync(
            string? serviceId,
            IReadOnlyCollection<int>? layerIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }

    private CdnInvalidationService Service(IHttpContextAccessor? accessor = null)
        => new(_cloudFront, NullLogger<CdnInvalidationService>.Instance, new CdnPurgeCoordination { TimeProvider = _time, HttpContextAccessor = accessor });
}
