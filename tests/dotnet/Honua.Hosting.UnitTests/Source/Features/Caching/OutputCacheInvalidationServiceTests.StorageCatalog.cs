// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
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
}
