// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(Protocols.TestQuality)]
public sealed class OutputCacheInvalidationServiceTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_RemovesMetadataKeysAndPatterns()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var metadataCache = Substitute.For<ICacheService>();
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(outputCacheStore, responseCache, metadataCache, logger);

        await sut.InvalidateServiceCatalogAsync("TestService", [1, 2, 2], CancellationToken.None);

        await outputCacheStore.Received().EvictByTagAsync("service-directory", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("service:testservice", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("layer:1", Arg.Any<CancellationToken>());
        await outputCacheStore.Received().EvictByTagAsync("layer:2", Arg.Any<CancellationToken>());

        await metadataCache.Received().RemoveAsync("services:all", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("layers:all", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("service:testservice", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("service:exists:testservice", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("layer:1", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveAsync("layer:2", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("relationship:1:*", Arg.Any<CancellationToken>());
        await metadataCache.Received().RemoveByPatternAsync("relationship:2:*", Arg.Any<CancellationToken>());

        await responseCache.Received().RemoveByPatternAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateServiceCatalogAsync_WithNullCaches_DoesNotThrow()
    {
        var logger = NullLogger<OutputCacheInvalidationService>.Instance;
        var sut = new OutputCacheInvalidationService(null, null, null, logger);

        var act = async () => await sut.InvalidateServiceCatalogAsync("svc", [3], CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
