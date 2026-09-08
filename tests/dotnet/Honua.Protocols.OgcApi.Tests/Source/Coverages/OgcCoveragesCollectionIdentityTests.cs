// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;

/// <summary>
/// Discovery uses public collection identity when metadata resources share storage.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiCoverages)]
public sealed class OgcCoveragesCollectionIdentityTests
{
    [IntegrationTheory]
    [InlineData(false, false, false, 1)]
    [InlineData(false, false, true, 1)]
    [InlineData(true, false, false, 2)]
    [InlineData(false, true, true, 1)]
    [InlineData(true, true, true, 1)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/coverages/collections")]
    public async Task Collections_ResourceAliases_ReturnUniqueAccessibleCollectionIds(
        bool distinctStorage, bool privateAlias, bool primaryAlias, int expectedAnonymousCount)
    {
        var aliasLayer = distinctStorage ? 2001 : 2000;
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("base-resource", "Base coverage", MetadataV2ResourceType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddResource("alias-resource", "Alias coverage", MetadataV2ResourceType.RasterDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = !privateAlias })
            .AddStorageBinding("base-storage", "base-resource", "honua.raster_data", storageLayerId: 2000)
            .AddStorageBinding("alias-storage", "alias-resource", "honua.raster_data", storageLayerId: aliasLayer)
            .AddService("base-service", "base_coverage", protocols: ["OGC-API-Coverages"])
            .AddService("alias-service", "alias_coverage", protocols: ["OGC-API-Coverages"])
            .AddPublication("base-publication", "base-service", "base-resource",
                layerIndex: 2000, storageBindingId: "base-storage")
            .AddPublication("alias-publication", "alias-service", "alias-resource",
                layerIndex: aliasLayer, storageBindingId: "alias-storage", isPrimary: primaryAlias)
            .BuildProvider();
        var store = Substitute.For<IRasterStore>();
        store.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<RasterInfo?>(new RasterInfo
            {
                Id = call.ArgAt<int>(0),
                LayerId = call.ArgAt<int>(0),
                Name = "gradient",
                Width = 4,
                Height = 4,
                BandCount = 2,
                PixelType = "32BF",
                Srid = 4326,
                GeoTransform = [-122.44, 0.01, 0, 37.78, 0, -0.01],
                Extent = new RasterExtent { XMin = -122.44, YMin = 37.74, XMax = -122.40, YMax = 37.78, Srid = 4326 },
                CreatedAt = DateTimeOffset.UnixEpoch
            }));
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        }).ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.AddSingleton<IMetadataV2GraphProvider>(graph);
            services.RemoveAll<IRasterStore>();
            services.AddSingleton(store);
        });
        await fixture.InitializeAsync();
        try
        {
            using var anonymous = fixture.CreateClient();
            await AssertCollectionsAsync(anonymous, expectedAnonymousCount);
            using var admin = fixture.CreateAdminClient();
            await AssertCollectionsAsync(admin, distinctStorage ? 2 : 1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task AssertCollectionsAsync(HttpClient client, int expectedCount)
    {
        using var response = await client.GetAsync("/ogc/coverages/collections");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var collections = document.RootElement.GetProperty("collections").EnumerateArray().ToArray();
        var ids = collections.Select(collection => collection.GetProperty("id").GetString()).ToArray();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(expectedCount);
        ids.Should().Contain("2000");
    }
}
