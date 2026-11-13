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

    private CdnInvalidationService Service(IHttpContextAccessor? accessor = null)
        => new(_cloudFront, NullLogger<CdnInvalidationService>.Instance, new CdnPurgeCoordination { TimeProvider = _time, HttpContextAccessor = accessor });
}
