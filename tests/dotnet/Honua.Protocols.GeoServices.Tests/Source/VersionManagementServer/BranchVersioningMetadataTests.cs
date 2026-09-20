// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class BranchVersioningMetadataTests : IAsyncLifetime
{
    private WebAppFixture? _fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _fixture?.DisposeAsync() ?? Task.CompletedTask;

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    public Task EnabledProviderAndLicense_AdvertiseBranchVersioningAtEveryLevel()
        => AssertMetadataAsync(HonuaEdition.Enterprise, experimentalEnabled: true, providerSupported: true, expectedData: true, expectedManagement: true);

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    public Task DisabledExperimentalCapability_KeepsVersionedDataWithoutVersionManagement()
        => AssertMetadataAsync(HonuaEdition.Enterprise, experimentalEnabled: false, providerSupported: true, expectedData: true, expectedManagement: false);

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    public Task UnlicensedProvider_DoesNotAdvertiseBranchVersioning()
        => AssertMetadataAsync(HonuaEdition.Community, experimentalEnabled: true, providerSupported: true, expectedData: false, expectedManagement: false);

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    public Task UnsupportedProvider_DoesNotAdvertiseBranchVersioning()
        => AssertMetadataAsync(HonuaEdition.Enterprise, experimentalEnabled: true, providerSupported: false, expectedData: false, expectedManagement: false);

    private async Task AssertMetadataAsync(
        HonuaEdition edition, bool experimentalEnabled, bool providerSupported, bool expectedData, bool expectedManagement)
    {
        var fixture = new WebAppFixture().WithTestLicense(edition);
        _fixture = fixture;
        fixture.ConfigureWebHost(builder =>
        {
            builder.UseSetting("Capabilities:Experimental:Enabled", "false");
            builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", experimentalEnabled.ToString());
        });
        if (!providerSupported)
        {
            fixture.ConfigureServices(services =>
            {
                services.RemoveAll<IVersionManager>();
                var manager = Substitute.For<IVersionManager>();
                manager.SupportsVersioning.Returns(false);
                services.AddSingleton(manager);
            });
        }

        await fixture.InitializeAsync();
        using var service = await ReadAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer?f=json");
        service.RootElement.GetProperty("supportsBranchVersioning").GetBoolean().Should().Be(expectedManagement);
        service.RootElement.GetProperty("hasVersionedData").GetBoolean().Should().Be(expectedData);
        service.RootElement.GetProperty("isDataVersioned").GetBoolean().Should().Be(expectedData);
        service.RootElement.TryGetProperty("versionManagementServerUrl", out _).Should().Be(expectedManagement);

        foreach (var layer in service.RootElement.GetProperty("layers").EnumerateArray())
        {
            using var metadata = await ReadAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{layer.GetProperty("id").GetInt32()}?f=json");
            metadata.RootElement.GetProperty("isDataVersioned").GetBoolean().Should().Be(expectedData);
            metadata.RootElement.GetProperty("isDataBranchVersioned").GetBoolean().Should().Be(expectedData,
                "native ArcPy distinguishes branch-versioned feature classes using layer metadata");
        }

        using var admin = await ReadAsync($"/admin/services/{WebAppFixture.TestServiceId}.MapServer?f=json");
        admin.RootElement.GetProperty("properties").GetProperty("isBranchVersioned").GetString()
            .Should().Be(expectedData ? "true" : "false");
        admin.RootElement.GetProperty("extensions").EnumerateArray()
            .Any(extension => extension.GetProperty("typeName").GetString() == "VersionManagementServer")
            .Should().Be(expectedManagement);

        async Task<JsonDocument> ReadAsync(string url)
        {
            using var response = await fixture.Client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
    }
}
