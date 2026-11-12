// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class CdnCacheTagTests
{
    private const string TagHeader = "x-amz-meta-cache-tag";

    private readonly CloudFrontLimits _cloudFront = new();
    private readonly ManualTimeProvider _time = new();

    [UnitTest]
    public void Apply_TagHeaderConfigured_TagsAPublicTileWithItsServiceAndLayer()
    {
        var context = SharedCacheTestContexts.GrantedTile(new SharedCacheOptions { CacheTagHeader = TagHeader });
        context.Request.Path = "/tiles/3/4/5/6.mvt";
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("Parcels", "3"));

        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);

        context.Response.Headers[TagHeader].ToString().Should().Be("honua-service:parcels,honua-layer:parcels/3,honua-scope:tiles/3");
    }

    [UnitTest]
    public void Apply_NoTagHeaderConfigured_SendsNoTags()
    {
        var context = SharedCacheTestContexts.GrantedTile();
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("Parcels", "3"));

        SharedCacheResponse.Apply(context);

        context.Response.Headers.Should().NotContainKey(TagHeader);
    }

    [UnitTest]
    public void Guard_ResponseTurnsOutNotShareable_RemovesTheTags()
    {
        var context = SharedCacheTestContexts.GrantedTile(new SharedCacheOptions { CacheTagHeader = TagHeader });
        SharedCacheResponse.UseSubject(context, new SharedCacheSubject("Parcels", "3"));
        SharedCacheResponse.Apply(context);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        SharedCacheGuard.Apply(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(SharedCacheGuard.NotShared);
        context.Response.Headers.Should().NotContainKey(TagHeader);
    }

    [UnitTheory]
    [InlineData("/rest/services/Parcels/MapServer/tile/4/5/6")]
    [InlineData("/rest/services/PARCELS/FeatureServer/3/query")]
    [InlineData("/tiles/3/4/5/6.mvt")]
    [InlineData("/tiles/03/tile.json")]
    [InlineData("/ogc/tiles/tiles/WebMercatorQuad/4/5/6")]
    [InlineData("/ogc/collections/parcels/items")]
    public void ForResponse_AnyResponseAnEditUsedToInvalidateByPath_CarriesATagTheEditInvalidates(string path)
    {
        // A MapServer tile is drawn from every layer of its service and a dataset tile may have no single subject; both are reached by scope.
        var tags = CdnCacheTags.ForResponse(subject: null, path);

        var invalidations = CdnCacheTags.InvalidationsForEditedLayer(3, ["parcels"]);

        tags.Select(tag => CdnCacheTags.InvalidationPrefix + tag).Should().ContainSingle(tag => invalidations.Contains(tag));
    }

    [UnitTheory]
    [InlineData("/rest/services/Ops/parcels-public/FeatureServer/3/query")]
    [InlineData("/rest/services/old-parcels/MapServer/tile/4/5/6")]
    [InlineData("/services/parcels-public/FeatureServer/3")]
    public void ForResponse_GeoServicesFolderOrAliasAddress_IsScopedByTheResolvedServiceTheEditNames(string path)
    {
        var tags = CdnCacheTags.ForResponse(subject: null, path, resolvedServiceId: "parcels");

        var invalidations = CdnCacheTags.InvalidationsForEditedLayer(3, ["parcels"]);

        tags.Select(tag => CdnCacheTags.InvalidationPrefix + tag).Should().ContainSingle(tag => invalidations.Contains(tag));
    }

    [UnitTheory]
    [InlineData("/rest/services/roads/MapServer/tile/4/5/6")]
    [InlineData("/tiles/4/4/5/6.mvt")]
    [InlineData("/scenes/city/layers/0")]
    public void ForResponse_ResponseOfAnotherLayerOrSurface_IsNotInvalidatedByTheEdit(string path)
    {
        var tags = CdnCacheTags.ForResponse(new SharedCacheSubject("roads", "4"), path);

        var invalidations = CdnCacheTags.InvalidationsForEditedLayer(3, ["parcels"]);

        tags.Select(tag => CdnCacheTags.InvalidationPrefix + tag).Should().NotContain(tag => invalidations.Contains(tag));
    }

    [UnitTheory]
    [InlineData("Zoning, 2026 édition")]
    [InlineData("a/b?c=d#e")]
    public void ForResponse_UnusualServiceName_GivesTagsTheCdnAccepts(string serviceName)
    {
        var longName = serviceName + new string('x', 400);

        var tags = CdnCacheTags.ForResponse(new SharedCacheSubject(serviceName, "3", "Night"), $"/rest/services/{serviceName}/MapServer")
            .Concat(CdnCacheTags.ForResponse(new SharedCacheSubject(longName, longName), $"/rest/services/{longName}/MapServer"));

        tags.Should().AllSatisfy(tag =>
        {
            tag.Length.Should().BeLessThanOrEqualTo(CdnCacheTags.MaxTagLength);
            tag.Should().MatchRegex("^[!-+--~]+$", "visible ASCII without commas or spaces");
        });
    }

    [UnitTest]
    public void ForResponse_LongServiceName_IsTheSameTagTheEditInvalidates()
    {
        var longName = new string('p', 400);

        var tags = CdnCacheTags.ForResponse(null, $"/rest/services/{longName}/MapServer/tile/1/2/3");

        CdnCacheTags.InvalidationsForEditedLayer(3, [longName.ToUpperInvariant()])
            .Should().Contain(CdnCacheTags.InvalidationPrefix + tags.Single());
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_TagsEnabled_InvalidatesTheLayerTagWithoutWildcards()
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time)));

        await invalidation.InvalidateEditedLayerAsync("Parcels", 3, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().BeEquivalentTo(
            "#honua-scope:tiles/3", "#honua-scope:ogc", "#honua-scope:rest/services/parcels");
        _cloudFront.WildcardPathsInProgress.Should().Be(0);
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_TwoTasksEditingOneLayer_PurgeItOncePerWindowBetweenThem()
    {
        var leases = new SharedLeases(_time);
        var first = Invalidation(Service(leases));
        var second = Invalidation(Service(leases));

        for (var edit = 0; edit < 50; edit++)
        {
            await first.InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);
            await second.InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);
        }

        _cloudFront.Accepted.Should().HaveCount(1);

        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(2);
        await Task.Delay(100);

        // Both tasks flush at once and claim tag by tag, so the next window's purge may leave in one request or two; each tag leaves once.
        _cloudFront.Accepted.Skip(1).SelectMany(paths => paths).Should().BeEquivalentTo(_cloudFront.Accepted[0]);
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_EditOnAnotherTaskJustAfterAPurge_IsStillPurgedWhenTheWindowCloses()
    {
        var leases = new SharedLeases(_time);
        await Invalidation(Service(leases)).InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));

        await Invalidation(Service(leases)).InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);
        _cloudFront.Accepted.Should().HaveCount(1);

        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(2);

        _cloudFront.Accepted[1].Should().Contain("#honua-scope:tiles/3");
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_EditNamesItsService_LeavesOtherServicesWithALayerOfThatNumberAlone()
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time)), TwoServicesWithLayerZero());

        await invalidation.InvalidateEditedLayerAsync("parcels", 0, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().BeEquivalentTo(
            "#honua-scope:tiles/0", "#honua-scope:ogc", "#honua-scope:rest/services/parcels", "#honua-scope:rest/services/parcels-public");
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_EditRequestTimesOutInTheCatalogLookup_PurgesEveryPublishedNameWithTheNextWindow()
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time)), new CatalogTimingOutForRequests(TwoServicesWithLayerZero()));
        using var request = new CancellationTokenSource();

        var edit = () => invalidation.InvalidateEditedLayerAsync("parcels", 0, request.Token);

        await edit.Should().ThrowAsync<OperationCanceledException>();
        _cloudFront.Accepted.Should().BeEmpty();
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(1);
        _cloudFront.Accepted[0].Should().BeEquivalentTo(
            "#honua-scope:tiles/0", "#honua-scope:ogc", "#honua-scope:rest/services/parcels", "#honua-scope:rest/services/parcels-public");
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_EditRequestCancelledWhileTheCdnIsBusy_PurgesWithTheNextWindow()
    {
        var invalidation = Invalidation(new CdnInvalidationService(
            _cloudFront, _time, NullLogger<CdnInvalidationService>.Instance, leaseStore: new SharedLeases(_time)));
        _cloudFront.OccupyEveryWildcardSlot();
        using var request = new CancellationTokenSource();

        var edit = invalidation.InvalidateEditedLayerAsync("parcels", 3, request.Token);
        await _time.TimerIsWaiting();
        await request.CancelAsync();

        var awaitEdit = () => edit;
        await awaitEdit.Should().ThrowAsync<OperationCanceledException>();
        _cloudFront.Rejected.Should().Be(1);
        _cloudFront.CompleteEverythingInProgress();
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(1);
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_NoCdnConfigured_TakesNoLease()
    {
        var leases = new SharedLeases(_time);
        var invalidation = Invalidation(new CdnInvalidationService(
            new NoCdnInvalidator(), _time, NullLogger<CdnInvalidationService>.Instance, leaseStore: leases));

        await invalidation.InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);

        leases.Claims.Should().Be(0);
    }

    [UnitTest]
    public async Task InvalidateNowAsync_MoreTagsThanOneRequestShouldName_InvalidatesEverything()
    {
        var tags = Enumerable.Range(0, CdnInvalidationService.MaxTagsPerInvalidation + 1).Select(index => $"#honua-scope:tiles/{index}");

        await Service(new SharedLeases(_time)).InvalidateNowAsync(tags, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_LeaseStoreUnreachable_StillPurges()
    {
        var invalidation = Invalidation(Service(new UnreachableLeases()));

        await invalidation.InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle();
    }

    [UnitTest]
    public async Task InvalidateEditedLayerAsync_EmergencyOn_TheNextPurgeComesAfterTheShorterWindow()
    {
        var leases = new SharedLeases(_time);
        var options = new SharedCacheOptions { CacheTagHeader = TagHeader };
        options.Emergency.AllPublicLayers = true;
        var invalidation = Invalidation(Service(leases, options));

        await invalidation.InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);
        await invalidation.InvalidateEditedLayerAsync("parcels", 3, CancellationToken.None);
        _time.Advance(CdnInvalidationService.EmergencyCoalescingWindow);

        await _cloudFront.AcceptedCountReaches(2);
    }

    private CdnInvalidationService Service(ILeaseStore leases, SharedCacheOptions? options = null)
        => new(
            _cloudFront,
            _time,
            NullLogger<CdnInvalidationService>.Instance,
            options: new StaticOptions(options ?? new SharedCacheOptions { CacheTagHeader = TagHeader }),
            leaseStore: leases);

    private static OutputCacheInvalidationService Invalidation(CdnInvalidationService service, MetadataV2Graph? graph = null)
        => Invalidation(service, graph is null ? null : new TestMetadataV2GraphProvider(graph));

    private static OutputCacheInvalidationService Invalidation(CdnInvalidationService service, IMetadataV2GraphProvider? catalog)
    {
        var services = new ServiceCollection();
        if (catalog is not null)
        {
            services.AddSingleton(catalog);
        }

        return new(
            null,
            null,
            null,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            null,
            NullLogger<OutputCacheInvalidationService>.Instance,
            service);
    }

    // Layer numbers are per service: both services have a layer 0, and parcels is published under another name.
    private static MetadataV2Graph TwoServicesWithLayerZero()
    {
        var builder = new TestMetadataV2GraphBuilder()
            .AddResource("res-0", "Layer 0", MetadataV2ResourceType.FeatureDataset)
            .AddStorageBinding("binding-0", "res-0", "features:0", storageLayerId: 0);
        foreach (var name in new[] { "parcels", "roads" })
        {
            builder
                .AddService($"svc-{name}", name, protocols: [ServiceProtocols.FeatureServer])
                .AddPublication(
                    id: $"pub-{name}-0",
                    serviceId: $"svc-{name}",
                    resourceId: "res-0",
                    layerIndex: 0,
                    serviceLocalId: "0",
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer);
        }

        var graph = builder.Build();
        return graph with
        {
            Services = [.. graph.Services.Select(service => service.Metadata.Name == "parcels" ? service with { PublicName = "parcels-public" } : service)],
        };
    }
}
