// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.Cog;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Cog;

public sealed class CatalogRasterSourceResolverTests
{
    [UnitTest]
    public async Task ResolveLayerIdAsync_RasterIdOnly_ReturnsOwningLayerWithoutCloudReader()
    {
        var store = Substitute.For<ICogStore>();
        store.GetAsync(91, Arg.Any<CancellationToken>()).Returns(CreateRegistration(91, layerId: 42));
        await using var services = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 91));

        result.Should().Be(RasterSourceLayerResolution.Success(42));
    }

    [UnitTest]
    public async Task ResolveLayerIdAsync_MismatchedHintAndUnknownRaster_AreIndistinguishable()
    {
        var store = Substitute.For<ICogStore>();
        store.GetAsync(91, Arg.Any<CancellationToken>()).Returns(CreateRegistration(91, layerId: 42));
        store.GetAsync(404, Arg.Any<CancellationToken>()).Returns((CogRegistration?)null);
        await using var services = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();
        var resolver = new CatalogRasterSourceResolver(services.GetRequiredService<IServiceScopeFactory>());

        var mismatched = await resolver.ResolveLayerIdAsync(new RasterSourceReference(7, 91));
        var unknown = await resolver.ResolveLayerIdAsync(new RasterSourceReference(null, 404));

        mismatched.Should().Be(RasterSourceLayerResolution.NotFound());
        unknown.Should().Be(mismatched);
    }

    private static CogRegistration CreateRegistration(long rasterId, int layerId) => new()
    {
        Id = rasterId,
        LayerId = layerId,
        Name = "test-raster",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "test-bucket",
        ObjectKey = "test.tif",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
