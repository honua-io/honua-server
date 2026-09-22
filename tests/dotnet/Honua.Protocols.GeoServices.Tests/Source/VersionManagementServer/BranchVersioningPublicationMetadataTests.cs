// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class BranchVersioningPublicationMetadataTests(BranchVersioningPublicationFixture fixture, ITestOutputHelper output)
    : IClassFixture<BranchVersioningPublicationFixture>
{
    private WebAppFixture App => fixture.App;
    private MetadataV2Graph Baseline => fixture.Baseline;
    private MetadataV2Publication ManagedPublication => Baseline.Publications.Single(publication =>
        publication.ServiceId == fixture.Service.Metadata.Id && publication.LayerIndex == 0
        && ServiceProtocols.IsPreferredPublicationType(ServiceProtocols.FeatureServer, publication.PublicationType));
    private MetadataV2Resource ManagedResource => Baseline.Resources.Single(resource => resource.Metadata.Id == ManagedPublication.ResourceId);
    private MetadataV2StorageBinding ManagedBinding => new MetadataV2GraphSnapshot(Baseline, "test", DateTimeOffset.UtcNow)
        .ResolveStorageBinding(ManagedPublication)!;

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task ManagedPublication_AdvertisesAtLayerServiceAndAdmin()
    {
        SetGraph(WithPublications(ManagedPublication));
        await AssertLayerAsync(0, true);
        await AssertServiceAndAdminAsync(true);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task ExplicitPublicationBinding_OverridesSameResourcesManagedPrimary()
    {
        var (externalPublication, externalBinding) = ExternalPublication();
        var graph = WithPublications(ManagedPublication, externalPublication);
        SetGraph(graph with { StorageBindings = [.. graph.StorageBindings, externalBinding] });
        await AssertLayerAsync(0, true);
        await AssertLayerAsync(17, false);
        await AssertServiceAndAdminAsync(true);
        ManagedResource.PrimaryStorageBindingId.Should().Be(ManagedBinding.Metadata.Id,
            "the external publication override must not change the resource primary binding");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task ExternalOnlyService_DoesNotAdvertiseRegardlessOfSourceBackedFlag(bool sourceBacked)
    {
        var (publication, binding) = ExternalPublication(sourceBacked);
        var graph = WithPublications(publication);
        SetGraph(graph with { StorageBindings = [.. graph.StorageBindings, binding] });
        await AssertLayerAsync(17, false);
        await AssertServiceAndAdminAsync(false);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    public async Task EmptyService_DoesNotInheritHostVersioningCapability()
    {
        SetGraph(WithPublications());
        await AssertServiceAndAdminAsync(false);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    public async Task RetiredManagedPublication_DoesNotMakeExternalServiceVersioned()
    {
        var (publication, binding) = ExternalPublication();
        var retired = ManagedPublication with { Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired } };
        var graph = WithPublications(retired, publication);
        SetGraph(graph with { StorageBindings = [.. graph.StorageBindings, binding] });
        await AssertServiceAndAdminAsync(false);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task InaccessibleManagedResource_DoesNotLeakVersioningThroughServiceOrAdmin()
    {
        var (publication, binding) = ExternalPublication();
        var externalResource = ManagedResource with
        {
            Metadata = ManagedResource.Metadata with { Id = "versioning-external-resource", Name = "external", Tenant = null },
            PrimaryStorageBindingId = binding.Metadata.Id,
            StorageBindingIds = [binding.Metadata.Id]
        };
        publication = publication with { ResourceId = externalResource.Metadata.Id };
        binding = binding with { ResourceId = externalResource.Metadata.Id };
        var graph = WithPublications(ManagedPublication, publication);
        SetGraph(graph with
        {
            Resources = [.. graph.Resources.Select(resource => resource.Metadata.Id == ManagedResource.Metadata.Id
                ? resource with { Metadata = resource.Metadata with { Tenant = "another-tenant" } } : resource), externalResource],
            StorageBindings = [.. graph.StorageBindings, binding]
        });
        await AssertLayerAsync(17, false);
        await AssertServiceAndAdminAsync(false);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task SelectedReaderWithoutBranchCapability_DoesNotInheritPostgresHostCapability()
    {
        const string connectionId = "be8ac38b-40e2-4775-951d-b98f361f307f";
        var binding = ManagedBinding with { ConnectionId = connectionId };
        var graph = WithPublications(ManagedPublication);
        SetGraph(graph with
        {
            StorageBindings = graph.StorageBindings.Select(item => item.Metadata.Id == binding.Metadata.Id ? binding : item).ToArray(),
            Connections = [.. graph.Connections, new MetadataV2Connection
            {
                Metadata = new MetadataV2ObjectMetadata { Id = connectionId, Name = "non-versioned-test" },
                Provider = "non-versioned-test"
            }]
        });
        await AssertLayerAsync(0, false);
        await AssertServiceAndAdminAsync(false);
        _ = fixture.UnversionedProvider.Received().Reader;
        fixture.UnversionedReader.ReceivedCalls().Should().BeEmpty("metadata must never execute feature reads");
    }

    private MetadataV2Graph WithPublications(params MetadataV2Publication[] publications)
        => Baseline with
        {
            Publications = [.. Baseline.Publications.Where(publication => publication.ServiceId != fixture.Service.Metadata.Id), .. publications]
        };

    private (MetadataV2Publication Publication, MetadataV2StorageBinding Binding) ExternalPublication(bool sourceBacked = true)
    {
        var options = new Dictionary<string, JsonElement>(ManagedBinding.Options)
        {
            ["tableName"] = JsonSerializer.SerializeToElement("external_features"),
            [FeatureStorageMapping.SourceBackedOption] = JsonSerializer.SerializeToElement(sourceBacked)
        };
        var binding = ManagedBinding with
        {
            Metadata = ManagedBinding.Metadata with { Id = "versioning-external-binding", Name = "external" },
            Options = options,
            Locator = "external_features"
        };
        return (ManagedPublication with
        {
            Metadata = ManagedPublication.Metadata with { Id = "versioning-external-publication", Name = "external" },
            LayerIndex = 17,
            StorageBindingId = binding.Metadata.Id
        }, binding);
    }

    private void SetGraph(MetadataV2Graph graph)
    {
        var provider = (TestMetadataV2GraphProvider)App.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph with { Revision = App.GetCurrentV2GraphSnapshot().Graph.Revision + 1 }, schema: App.CurrentSchema);
    }

    private async Task AssertLayerAsync(int layerId, bool expected)
    {
        using var metadata = await ReadAsync($"/rest/services/{fixture.Service.Metadata.Name}/FeatureServer/{layerId}?f=json");
        metadata.RootElement.GetProperty("isDataVersioned").GetBoolean().Should().Be(expected);
        metadata.RootElement.GetProperty("isDataBranchVersioned").GetBoolean().Should().Be(expected);
    }

    private async Task AssertServiceAndAdminAsync(bool expected)
    {
        using var service = await ReadAsync($"/rest/services/{fixture.Service.Metadata.Name}/FeatureServer?f=json");
        service.RootElement.GetProperty("hasVersionedData").GetBoolean().Should().Be(expected);
        service.RootElement.GetProperty("hasBranchVersionedData").GetBoolean().Should().Be(expected);
        service.RootElement.GetProperty("isDataVersioned").GetBoolean().Should().Be(expected);
        service.RootElement.GetProperty("supportsBranchVersioning").GetBoolean().Should().Be(expected);
        service.RootElement.TryGetProperty("versionManagementServerUrl", out _).Should().Be(expected);
        using var admin = await ReadAsync($"/admin/services/{fixture.Service.Metadata.Name}.MapServer?f=json");
        admin.RootElement.GetProperty("properties").GetProperty("isBranchVersioned").GetString().Should().Be(expected ? "true" : "false");
        var extensions = admin.RootElement.GetProperty("extensions").EnumerateArray().ToArray();
        extensions.Single(extension => extension.GetProperty("typeName").GetString() == "FeatureServer")
            .GetProperty("properties").GetProperty("isBranchVersioned").GetString().Should().Be(expected ? "true" : "false");
        extensions.Any(extension => extension.GetProperty("typeName").GetString() == "VersionManagementServer").Should().Be(expected);
        using var map = await ReadAsync($"/rest/services/{fixture.Service.Metadata.Name}/MapServer?f=json");
        var mapExtensions = map.RootElement.GetProperty("supportedExtensions").GetString()!.Split(',');
        mapExtensions.Contains("VersionManagementServer").Should().Be(expected);
        var vmsUrl = $"/rest/services/{fixture.Service.Metadata.Name}/VersionManagementServer?f=json";
        using var vmsGet = await App.Client.GetAsync(vmsUrl);
        using var vmsForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" });
        using var vmsPost = await App.Client.PostAsync(vmsUrl, vmsForm);
        if (expected)
        {
            await BranchVersioningPublicationFixture.AssertVersionManagementSuccessAsync(vmsGet, vmsPost);
        }
        else
        {
            await BranchVersioningPublicationFixture.AssertVersionManagementErrorAsync(vmsGet, 501, output);
            await BranchVersioningPublicationFixture.AssertVersionManagementErrorAsync(vmsPost, 501, output);
        }
    }

    private async Task<JsonDocument> ReadAsync(string path)
    {
        using var response = await App.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "owned test metadata must not return a protocol error: {0}", body);
        var requiredProperty = path.Contains("/admin/", StringComparison.Ordinal) ? "properties"
            : path.Contains("/FeatureServer?", StringComparison.Ordinal) ? "hasVersionedData"
                : path.Contains("/MapServer?", StringComparison.Ordinal) ? "supportedExtensions" : "isDataVersioned";
        document.RootElement.TryGetProperty(requiredProperty, out _).Should().BeTrue(
            "owned test metadata must contain {0}; response: {1}", requiredProperty, body);
        return document;
    }
}

public sealed class BranchVersioningPublicationFixture : IAsyncLifetime
{
    private static readonly string[] _errorPropertyNames = ["error"];

    internal const string ServiceName = "branch-publication-capability";
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise);
    public MetadataV2Graph Baseline { get; private set; } = null!;
    public MetadataV2Service Service { get; private set; } = null!;
    public IFeatureReader UnversionedReader { get; } = Substitute.For<IFeatureReader>();
    public IFeatureDataProvider UnversionedProvider { get; } = Substitute.For<IFeatureDataProvider>();

    public async Task InitializeAsync()
    {
        UnversionedProvider.ProviderName.Returns("non-versioned-test");
        UnversionedProvider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        UnversionedProvider.Reader.Returns(UnversionedReader);
        App.ConfigureServices(services => services.AddSingleton(UnversionedProvider));
        App.ConfigureWebHost(builder => builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", "true"));
        await App.InitializeAsync();
        ConfigureManagedPublications(App);
        var snapshot = App.GetCurrentV2GraphSnapshot();
        // No time-extent discovery is needed for these metadata capability tests.
        Baseline = snapshot.Graph with { Resources = snapshot.Graph.Resources.Select(resource => resource with { Temporal = null }).ToArray() };
        Service = snapshot.Index.ServicesByName[ServiceName];
    }

    internal static async Task AssertVersionManagementSuccessAsync(HttpResponseMessage get, HttpResponseMessage post)
    {
        var getBody = await get.Content.ReadAsStringAsync();
        var postBody = await post.Content.ReadAsStringAsync();
        get.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        post.StatusCode.Should().Be(HttpStatusCode.OK, postBody);
        postBody.Should().Be(getBody);
        using var document = JsonDocument.Parse(getBody);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(getBody);
        document.RootElement.GetProperty("defaultVersionName").GetString().Should().Be("sde.DEFAULT");
        document.RootElement.GetProperty("capabilities").GetString().Should().Contain("Create");
    }

    internal static async Task AssertVersionManagementErrorAsync(HttpResponseMessage response, int expectedCode, ITestOutputHelper output)
    {
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine("{0} {1}: HTTP {2}; {3}", response.RequestMessage!.Method,
            response.RequestMessage.RequestUri!.AbsolutePath, (int)response.StatusCode, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(_errorPropertyNames,
            "a denied discovery request must not contain successful version metadata: {0}", body);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(expectedCode, body);
        error.GetProperty("message").GetString().Should().Be(expectedCode == 501 ? "Not Implemented" : "Payment Required", body);
        var expectedDetail = expectedCode == 501
            ? "Branch versioning is not supported by the service's accessible publications."
            : $"entitlement: {FeatureCatalog.BranchVersioningKey}";
        error.GetProperty("details").EnumerateArray().Select(detail => detail.GetString()).Should().Contain(expectedDetail, body);
    }

    internal static void ConfigureManagedPublications(WebAppFixture app)
    {
        var snapshot = app.GetCurrentV2GraphSnapshot();
        var service = snapshot.Index.ServicesByName[WebAppFixture.TestServiceId];
        var publications = snapshot.Graph.Publications.Select(publication => publication.ServiceId == service.Metadata.Id
            ? publication with { PublicationType = MetadataV2PublicationType.EsriFeatureLayer } : publication).ToArray();
        var bindingIds = publications.Where(publication => publication.ServiceId == service.Metadata.Id)
            .Select(publication => snapshot.ResolveStorageBinding(publication)!.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var bindings = snapshot.Graph.StorageBindings.Select(binding => bindingIds.Contains(binding.Metadata.Id)
            ? binding with
            {
                Options = new Dictionary<string, JsonElement>(binding.Options)
                {
                    ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
                }
            } : binding).ToArray();
        // The default multi-protocol test graph has generic OGC publications and omits
        // the layer discriminator. These versioning cases explicitly publish the real
        // seeded shared-table layout, so positive host-gate assertions exercise layers.
        var provider = (TestMetadataV2GraphProvider)app.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Services = snapshot.Graph.Services.Select(item => item.Metadata.Id == service.Metadata.Id
                ? item with { Metadata = item.Metadata with { Name = ServiceName } } : item).ToArray(),
            Publications = publications,
            StorageBindings = bindings
        }, schema: app.CurrentSchema);
    }

    public Task DisposeAsync() => App.DisposeAsync();
}
