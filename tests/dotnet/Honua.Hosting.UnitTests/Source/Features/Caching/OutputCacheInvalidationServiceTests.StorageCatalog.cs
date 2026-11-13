// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class OutputCacheInvalidationServiceTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task CommittedEdit_EvictsStacMetadataBeforeReturning(bool storageEdit)
    {
        var outputCache = Substitute.For<IOutputCacheStore>();
        var invalidator = new OutputCacheInvalidationService(outputCache, null, null,
            CreateScopeFactory(CreateFeatureServerGraph(("alpha", [3]))), null,
            NullLogger<OutputCacheInvalidationService>.Instance);

        if (storageEdit)
        {
            await invalidator.InvalidateEditedStorageLayerAsync(3, CancellationToken.None);
        }
        else
        {
            await invalidator.InvalidateEditedLayerAsync("alpha", 3, CancellationToken.None);
        }

        await outputCache.Received().EvictByTagAsync("stac-metadata", Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task CommittedEdit_OriginEvictionCanceled_CompletesOriginEvictionWithoutRequestToken(bool storageEdit)
    {
        using var request = new CancellationTokenSource();
        var outputCache = Substitute.For<IOutputCacheStore>();
        SubstituteExtensions.Returns(outputCache.EvictByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()),
            call => call.Arg<CancellationToken>().CanBeCanceled
                ? ValueTask.FromException(new OperationCanceledException(request.Token))
                : ValueTask.CompletedTask);
        var responseCache = Substitute.For<IResponseCache>();
        var invalidator = new OutputCacheInvalidationService(outputCache, responseCache, null,
            CreateScopeFactory(CreateFeatureServerGraph(("alpha", [3]))), null,
            NullLogger<OutputCacheInvalidationService>.Instance);

        var edit = () => storageEdit
            ? invalidator.InvalidateEditedStorageLayerAsync(3, request.Token)
            : invalidator.InvalidateEditedLayerAsync("alpha", 3, request.Token);

        await edit.Should().ThrowAsync<OperationCanceledException>();
        await outputCache.Received().EvictByTagAsync("collection:pub-alpha-3", CancellationToken.None);
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:alpha:layer:3:*", CancellationToken.None);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("alpha")]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_StorageHandleDiffersFromServiceIndices_EvictsEveryBoundPublication(string? serviceId)
    {
        var graph = CreateFeatureServerGraph(("alpha", [3]), ("beta", [9]));
        graph = graph with
        {
            StorageBindings = graph.StorageBindings.Select(binding => binding with { StorageLayerId = 741 }).ToArray(),
        };
        var responseCache = Substitute.For<IResponseCache>();
        var outputCache = Substitute.For<IOutputCacheStore>();
        var metadataCache = Substitute.For<ICacheService>();
        var invalidator = new OutputCacheInvalidationService(outputCache, responseCache, metadataCache,
            CreateScopeFactory(graph), null, NullLogger<OutputCacheInvalidationService>.Instance);

        await invalidator.InvalidateServiceCatalogAsync(serviceId, [741], CancellationToken.None);

        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:alpha:layer:3:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:beta:layer:9:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:pub-alpha-3:*", Arg.Any<CancellationToken>());
        await responseCache.Received().RemoveByPatternAsync("response:query:ogc:collection:pub-beta-9:*", Arg.Any<CancellationToken>());
        await outputCache.Received().EvictByTagAsync("collection:pub-alpha-3", Arg.Any<CancellationToken>());
        await outputCache.Received().EvictByTagAsync("collection:pub-beta-9", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync(ScopedKey("layer:741"), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateEditedStorageLayerAsync_BindinglessPublication_EvictsWhatTheCdnPurgeReaches()
    {
        var graph = CreateFeatureServerGraph(("alpha", [3]), ("beta", [9])) with { StorageBindings = [] };
        var responseCache = Substitute.For<IResponseCache>();
        var outputCache = Substitute.For<IOutputCacheStore>();
        var invalidator = new OutputCacheInvalidationService(outputCache, responseCache, Substitute.For<ICacheService>(),
            CreateScopeFactory(graph), null, NullLogger<OutputCacheInvalidationService>.Instance);

        await invalidator.InvalidateEditedStorageLayerAsync(3, CancellationToken.None);

        await responseCache.Received().RemoveByPatternAsync("response:query:featureserver:service:alpha:layer:3:*", Arg.Any<CancellationToken>());
        await outputCache.Received().EvictByTagAsync("collection:pub-alpha-3", Arg.Any<CancellationToken>());
        await outputCache.DidNotReceive().EvictByTagAsync("collection:pub-beta-9", Arg.Any<CancellationToken>());
    }
}
