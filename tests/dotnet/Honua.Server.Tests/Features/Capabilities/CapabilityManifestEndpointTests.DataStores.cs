// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Capabilities;

public sealed partial class CapabilityManifestEndpointTests
{
    [IntegrationTheory]
    [InlineData("postgis")]
    [InlineData("fixture-store,postgis")]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_RegisteredDataStores_AdvertisesOnlyRuntimeProviders(string registeredProviders)
    {
        var expected = registeredProviders.Split(',');
        await using var fixture = CreateManifestFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IFeatureDataProvider>();
            foreach (var name in expected.Reverse().Append(expected[0]))
            {
                var provider = Substitute.For<IFeatureDataProvider>();
                provider.ProviderName.Returns(name);
                services.AddSingleton(provider);
            }
        });
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        var stores = document.RootElement.GetProperty("dataStores");
        stores.GetProperty("installedProviders").EnumerateArray().Select(value => value.GetString())
            .Should().Equal(expected);
        stores.GetProperty("fullImageTagSuffix").GetString().Should().Be("-full");
        stores.GetProperty("documentationHref").GetString().Should().Be(
            "https://github.com/honua-io/honua-server/blob/trunk/docs/ops/nightly-images.md#data-store-variants");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_NoRegisteredDataStores_AdvertisesEmptyInventory()
    {
        await using var fixture = CreateManifestFixture()
            .ConfigureServices(services => services.RemoveAll<IFeatureDataProvider>());
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        document.RootElement.GetProperty("dataStores").GetProperty("installedProviders")
            .GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_AnonymousCaller_OmitsDataStoreInventory()
    {
        using var response = await _anonymousClient.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        document.RootElement.TryGetProperty("dataStores", out _).Should().BeFalse();
    }
}
