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
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class BranchVersioningMetadataTests(ITestOutputHelper output) : IAsyncLifetime
{
    private WebAppFixture? _fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _fixture?.DisposeAsync() ?? Task.CompletedTask;

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    public Task EnabledProviderAndLicense_AdvertiseBranchVersioningAtEveryLevel()
        => AssertMetadataAsync(HonuaEdition.Enterprise, experimentalEnabled: true, providerSupported: true, expectedData: true, expectedManagement: true);

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    public Task DisabledExperimentalCapability_KeepsVersionedDataWithoutVersionManagement()
        => AssertMetadataAsync(HonuaEdition.Enterprise, experimentalEnabled: false, providerSupported: true, expectedData: true, expectedManagement: false);

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    public Task UnlicensedProvider_DoesNotAdvertiseBranchVersioning()
        => AssertMetadataAsync(HonuaEdition.Community, experimentalEnabled: true, providerSupported: true, expectedData: false, expectedManagement: false);

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    public Task UnsupportedProvider_DoesNotAdvertiseBranchVersioning()
        => AssertMetadataAsync(HonuaEdition.Enterprise, experimentalEnabled: true, providerSupported: false, expectedData: false, expectedManagement: false);

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("POST /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/admin/{serviceName}.{serviceType}")]
    [Endpoint("POST /rest/admin/{serviceName}.{serviceType}")]
    public async Task AdminService_GetAndPostReturnTheSameBranchMetadataAtBothRouteSpellings()
    {
        await AssertMetadataAsync(HonuaEdition.Enterprise, experimentalEnabled: true,
            providerSupported: true, expectedData: true, expectedManagement: true);
        var fixture = _fixture ?? throw new InvalidOperationException("The metadata fixture was not initialized.");
        var url = $"/rest/admin/{BranchVersioningPublicationFixture.ServiceName}.MapServer";
        using var get = await fixture.Client.GetAsync($"{url}?f=json");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" });
        using var post = await fixture.Client.PostAsync(url, form);
        var canonicalUrl = $"/admin/services/{BranchVersioningPublicationFixture.ServiceName}.MapServer";
        using var canonicalGet = await fixture.Client.GetAsync($"{canonicalUrl}?f=json");
        using var canonicalForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" });
        using var canonicalPost = await fixture.Client.PostAsync(canonicalUrl, canonicalForm);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        canonicalGet.StatusCode.Should().Be(HttpStatusCode.OK);
        canonicalPost.StatusCode.Should().Be(HttpStatusCode.OK);
        using var getPayload = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        using var postPayload = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        using var canonicalGetPayload = JsonDocument.Parse(await canonicalGet.Content.ReadAsStringAsync());
        using var canonicalPostPayload = JsonDocument.Parse(await canonicalPost.Content.ReadAsStringAsync());
        getPayload.RootElement.GetProperty("properties").GetProperty("isBranchVersioned").GetString().Should().Be("true");
        postPayload.RootElement.GetRawText().Should().Be(getPayload.RootElement.GetRawText());
        canonicalGetPayload.RootElement.GetRawText().Should().Be(getPayload.RootElement.GetRawText());
        canonicalPostPayload.RootElement.GetRawText().Should().Be(getPayload.RootElement.GetRawText());
    }

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
        BranchVersioningPublicationFixture.ConfigureManagedPublications(fixture);
        using var service = await ReadAsync($"/rest/services/{BranchVersioningPublicationFixture.ServiceName}/FeatureServer?f=json");
        service.RootElement.GetProperty("supportsBranchVersioning").GetBoolean().Should().Be(expectedManagement);
        service.RootElement.GetProperty("hasVersionedData").GetBoolean().Should().Be(expectedData);
        service.RootElement.GetProperty("hasBranchVersionedData").GetBoolean().Should().Be(expectedData);
        service.RootElement.GetProperty("isDataVersioned").GetBoolean().Should().Be(expectedData);
        service.RootElement.TryGetProperty("versionManagementServerUrl", out _).Should().Be(expectedManagement);
        service.RootElement.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0,
            "the host-gate fixture must exercise representative managed publications");

        foreach (var layer in service.RootElement.GetProperty("layers").EnumerateArray())
        {
            using var metadata = await ReadAsync(
                $"/rest/services/{BranchVersioningPublicationFixture.ServiceName}/FeatureServer/{layer.GetProperty("id").GetInt32()}?f=json");
            metadata.RootElement.GetProperty("isDataVersioned").GetBoolean().Should().Be(expectedData);
            metadata.RootElement.GetProperty("isDataBranchVersioned").GetBoolean().Should().Be(expectedData,
                "native ArcPy distinguishes branch-versioned feature classes using layer metadata");
        }

        using var admin = await ReadAsync($"/admin/services/{BranchVersioningPublicationFixture.ServiceName}.MapServer?f=json");
        admin.RootElement.GetProperty("properties").GetProperty("isBranchVersioned").GetString()
            .Should().Be(expectedData ? "true" : "false");
        admin.RootElement.GetProperty("extensions").EnumerateArray()
            .Any(extension => extension.GetProperty("typeName").GetString() == "VersionManagementServer")
            .Should().Be(expectedManagement);

        using var map = await ReadAsync($"/rest/services/{BranchVersioningPublicationFixture.ServiceName}/MapServer?f=json");
        var mapExtensions = map.RootElement.GetProperty("supportedExtensions").GetString()!.Split(',');
        mapExtensions.Should().Contain("FeatureServer");
        mapExtensions.Contains("VersionManagementServer").Should().Be(expectedManagement);

        var vmsUrl = $"/rest/services/{BranchVersioningPublicationFixture.ServiceName}/VersionManagementServer?f=json";
        using var vmsGet = await fixture.Client.GetAsync(vmsUrl);
        using var vmsForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" });
        using var vmsPost = await fixture.Client.PostAsync(vmsUrl, vmsForm);
        if (!experimentalEnabled)
        {
            vmsGet.StatusCode.Should().Be(HttpStatusCode.NotFound);
            vmsPost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        else if (edition == HonuaEdition.Community || !providerSupported)
        {
            var expectedCode = edition == HonuaEdition.Community ? 402 : 501;
            await BranchVersioningPublicationFixture.AssertVersionManagementErrorAsync(vmsGet, expectedCode, output);
            await BranchVersioningPublicationFixture.AssertVersionManagementErrorAsync(vmsPost, expectedCode, output);
        }
        else
        {
            await BranchVersioningPublicationFixture.AssertVersionManagementSuccessAsync(vmsGet, vmsPost);
        }

        async Task<JsonDocument> ReadAsync(string url)
        {
            using var response = await fixture.Client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
                "owned test metadata must not return a protocol error: {0}", body);
            var requiredProperty = url.Contains("/admin/", StringComparison.Ordinal) ? "properties"
                : url.Contains("/FeatureServer?", StringComparison.Ordinal) ? "hasVersionedData"
                : url.Contains("/MapServer?", StringComparison.Ordinal) ? "supportedExtensions" : "isDataVersioned";
            document.RootElement.TryGetProperty(requiredProperty, out _).Should().BeTrue(
                "owned test metadata must contain {0}; response: {1}", requiredProperty, body);
            return document;
        }
    }
}
