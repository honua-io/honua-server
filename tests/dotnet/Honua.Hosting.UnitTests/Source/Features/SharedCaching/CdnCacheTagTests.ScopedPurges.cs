// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed partial class CdnCacheTagTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidateEditedOgcSurfaceAsync_TagsEnabled_PurgesOnlyTheOgcTag(bool metadata)
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time)));

        if (metadata)
        {
            await invalidation.InvalidateEditedOgcMetadataAsync(CancellationToken.None);
        }
        else
        {
            await invalidation.InvalidateEditedCollectionAsync("parcels", CancellationToken.None);
        }

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("#honua-scope:ogc");
        _cloudFront.WildcardPathsInProgress.Should().Be(0);
    }

    [UnitTheory]
    [InlineData("/stac/collections")]
    [InlineData("/stac/collections/parcels")]
    [InlineData("/STAC/collections/parcels")]
    [InlineData("/ogc/collections/parcels/items")]
    public async Task InvalidateEditedStorageLayerAsync_PublicCollectionResponse_CarriesATagTheEditPurges(string path)
    {
        var context = SharedCacheTestContexts.GrantedTile(new SharedCacheOptions { CacheTagHeader = TagHeader });
        context.Request.Path = path;
        SharedCacheResponse.Apply(context);
        SharedCacheGuard.Apply(context);
        var tags = context.Response.Headers[TagHeader].ToString().Split(',');
        tags.Should().ContainSingle();
        var invalidation = Invalidation(Service(new SharedLeases(_time)), PublishedStorageLayers());

        await invalidation.InvalidateEditedStorageLayerAsync(741, CancellationToken.None);

        _cloudFront.Accepted.SelectMany(paths => paths).Should().Contain(CdnCacheTags.InvalidationPrefix + tags.Single());
        _cloudFront.WildcardPathsInProgress.Should().Be(0);
    }

    [UnitTest]
    public async Task InvalidateEditedStorageLayerAsync_StorageHandleDiffersFromPublishedIndices_PurgesBoundServicesAndAliases()
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time)), PublishedStorageLayers());

        await invalidation.InvalidateEditedStorageLayerAsync(741, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().BeEquivalentTo(
            "#honua-scope:ogc", "#honua-scope:stac", "#honua-scope:tiles/741", "#honua-scope:tiles/3", "#honua-scope:tiles/9",
            "#honua-scope:rest/services/parcels", "#honua-scope:rest/services/parcels-public",
            "#honua-scope:rest/services/zoning");
        _cloudFront.WildcardPathsInProgress.Should().Be(0);
    }

    [UnitTest]
    public async Task InvalidateEditedStorageLayerAsync_BindinglessPublication_PurgesItsService()
    {
        var graph = PublishedStorageLayers() with { StorageBindings = [] };
        var invalidation = Invalidation(Service(new SharedLeases(_time)), graph);

        await invalidation.InvalidateEditedStorageLayerAsync(3, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().BeEquivalentTo(
            "#honua-scope:ogc", "#honua-scope:stac", "#honua-scope:tiles/3",
            "#honua-scope:rest/services/parcels", "#honua-scope:rest/services/parcels-public");
    }

    [UnitTest]
    public async Task InvalidateEditedStorageLayerAsync_CdnCatalogLookupCanceled_PurgesPublishedNamesInTheNextWindow()
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time)), new CatalogCancelingSecondRequest(PublishedStorageLayers() with { StorageBindings = [] }));
        using var request = new CancellationTokenSource();

        var edit = () => invalidation.InvalidateEditedStorageLayerAsync(3, request.Token);

        await edit.Should().ThrowAsync<OperationCanceledException>();
        _cloudFront.Accepted.Should().BeEmpty();
        _time.Advance(CdnInvalidationService.CoalescingWindow);
        await _cloudFront.AcceptedCountReaches(1);
        _cloudFront.Accepted.SelectMany(paths => paths).Should().BeEquivalentTo(
            "#honua-scope:ogc", "#honua-scope:stac", "#honua-scope:tiles/3",
            "#honua-scope:rest/services/parcels", "#honua-scope:rest/services/parcels-public");
    }

    [UnitTest]
    public async Task InvalidateEditedStorageLayerAsync_CatalogUnavailable_PreservesTheFullPurgeFallback()
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time)));

        await invalidation.InvalidateEditedStorageLayerAsync(741, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvalidateNowAsync_OneTagBatchFails_AttemptsEveryBatchAndReportsFailure(int failedBatch)
    {
        var invalidator = new FailingTagBatch(failedBatch);
        var logger = new RecordingLogger();
        var service = new CdnInvalidationService(invalidator, logger, new CdnPurgeCoordination { TimeProvider = _time });
        var paths = Enumerable.Range(0, CdnInvalidationService.MaxTagsPerInvalidation + 1)
            .Select(index => $"#honua-scope:tiles/{index}").Append("/ogc/*").ToArray();

        var result = await service.InvalidateNowAsync(paths, CancellationToken.None, CdnPurgeCause.Admin("change-17"));

        result.Failed.Should().BeTrue();
        result.Detail.Should().Be("batch failed");
        invalidator.Requests.Should().HaveCount(3);
        invalidator.Requests.SelectMany(batch => batch).Should().BeEquivalentTo(paths);
        logger.Messages.Should().HaveCount(3).And.AllSatisfy(message => message.Should().Contain("change=change-17"));
    }

    [UnitTest]
    public async Task InvalidateEditedStorageLayerAsync_TagsDisabled_PreservesTheFullPurgeFallback()
    {
        var invalidation = Invalidation(Service(new SharedLeases(_time), new SharedCacheOptions()), PublishedStorageLayers());

        await invalidation.InvalidateEditedStorageLayerAsync(741, CancellationToken.None);

        _cloudFront.Accepted.Should().ContainSingle().Which.Should().Equal("/*");
    }

    [UnitTest]
    public async Task InvalidateNowAsync_BusyBatchThenPermanentFailure_ReportsPermanentFailure()
    {
        var service = new CdnInvalidationService(new BusyThenFailedBatches(), new RecordingLogger(),
            new CdnPurgeCoordination { TimeProvider = _time });
        var paths = Enumerable.Range(0, CdnInvalidationService.MaxTagsPerInvalidation + 1)
            .Select(index => $"#honua-scope:tiles/{index}").ToArray();

        var pending = service.InvalidateNowAsync(paths, CancellationToken.None);
        foreach (var delay in CdnInvalidationService.BusyRetryDelays)
        {
            await _time.TimerIsWaiting();
            _time.Advance(delay);
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        result.Outcome.Should().Be(CdnInvalidationOutcome.Failed);
        result.Detail.Should().Be("permanent failure");
    }

    private sealed class BusyThenFailedBatches : ICdnInvalidator
    {
        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult(paths.Count == CdnInvalidationService.MaxTagsPerInvalidation
                ? new CdnInvalidationResult(CdnInvalidationOutcome.Busy, "busy")
                : new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "permanent failure"));
    }

    private sealed class FailingTagBatch(int failedBatch) : ICdnInvalidator
    {
        internal List<IReadOnlyCollection<string>> Requests { get; } = [];

        public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            Requests.Add(paths);
            return Task.FromResult(Requests.Count - 1 == failedBatch
                ? new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "batch failed")
                : new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
        }
    }

    private sealed class CatalogCancelingSecondRequest(MetadataV2Graph graph) : IMetadataV2GraphProvider
    {
        private readonly TestMetadataV2GraphProvider _catalog = new(graph);
        private int _requestReads;

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => cancellationToken.CanBeCanceled && ++_requestReads == 2
                ? ValueTask.FromException<MetadataV2GraphSnapshot>(new OperationCanceledException(cancellationToken))
                : _catalog.GetCurrentAsync(cancellationToken);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => _catalog.GetByRevisionAsync(revision, cancellationToken);
    }

    private static MetadataV2Graph PublishedStorageLayers()
    {
        var builder = new TestMetadataV2GraphBuilder()
            .AddResource("edited", "Edited dataset", MetadataV2ResourceType.FeatureDataset)
            .AddStorageBinding("edited-binding", "edited", "features:741", storageLayerId: 741)
            .AddResource("unrelated", "Unrelated dataset", MetadataV2ResourceType.FeatureDataset)
            .AddStorageBinding("unrelated-binding", "unrelated", "features:742", storageLayerId: 742);
        foreach (var (name, index, resource) in new[] { ("parcels", 3, "edited"), ("zoning", 9, "edited"), ("roads", 4, "unrelated") })
        {
            builder.AddService($"svc-{name}", name, protocols: [ServiceProtocols.FeatureServer])
                .AddPublication($"pub-{name}", $"svc-{name}", resource,
                    layerIndex: index, serviceLocalId: index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer);
        }

        var graph = builder.Build();
        return graph with
        {
            Services = [.. graph.Services.Select(service => service.Metadata.Name == "parcels"
                ? service with { PublicName = "parcels-public" }
                : service)],
        };
    }
}
