// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class OutputCacheInvalidationServiceTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateStorageLayerAsync_MetadataUnavailable_ClearsQueryCachesWithoutThrowing()
    {
        var responseCache = Substitute.For<IResponseCache>();
        var invalidator = CreateInvalidatorWithMetadataFailure(new InvalidOperationException("Metadata is unavailable."), responseCache);

        var invalidate = () => invalidator.InvalidateStorageLayerAsync(741, CancellationToken.None);

        await invalidate.Should().NotThrowAsync();
        await responseCache.Received(1).RemoveByPatternAsync("response:query:featureserver:service:*", Arg.Any<CancellationToken>());
        await responseCache.Received(1).RemoveByPatternAsync("response:query:ogc:collection:*", Arg.Any<CancellationToken>());
        await responseCache.Received(1).RemoveByPatternAsync("response:query:odata:layer:*", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateStorageLayerAsync_MetadataReadCanceled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var responseCache = Substitute.For<IResponseCache>();
        var invalidator = CreateInvalidatorWithMetadataFailure(new OperationCanceledException(cancellation.Token), responseCache);

        var invalidate = () => invalidator.InvalidateStorageLayerAsync(741, cancellation.Token);

        var failure = await invalidate.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        await responseCache.DidNotReceive().RemoveByPatternAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static OutputCacheInvalidationService CreateInvalidatorWithMetadataFailure(Exception failure, IResponseCache responseCache)
    {
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns<MetadataV2GraphSnapshot>(_ => throw failure);
        var services = new ServiceCollection().AddSingleton(graphProvider).BuildServiceProvider();
        return new(null, responseCache, null, services.GetRequiredService<IServiceScopeFactory>(), null,
            NullLogger<OutputCacheInvalidationService>.Instance);
    }
}
