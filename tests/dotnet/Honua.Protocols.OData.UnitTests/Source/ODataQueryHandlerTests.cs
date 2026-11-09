// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using NSubstitute;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Protocols.OData.Services;
using Honua.Infrastructure.Authentication;
using TestProtocols = Honua.TestKit.Constants.ProtocolNames;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Unit tests for OData metadata-v2 publication visibility.
/// </summary>
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataQueryHandlerTests
{
    [UnitTest]
    public async Task ResolveVisibleODataPublications_WithNonODataService_DoesNotUseResourceProtocolFallback()
    {
        using var provider = CreateProvider(["FeatureServer"]);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ResolveVisibleODataPublications_WithODataService_ReturnsPublication()
    {
        using var provider = CreateProvider(["OData"]);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().ContainSingle();
        visible[0].LayerIndex.Should().Be(1);
        visible[0].StorageLayerId.Should().Be(101);
        visible[0].Resource.Metadata.Name.Should().Be("Layer1");
    }

    [UnitTest]
    public async Task ResolveODataPublications_WithRetiredPublication_OmitsDiscoveryEntry()
    {
        using var provider = CreateProvider(
            ["OData"],
            publicationLifecycle: MetadataV2LifecycleStatus.Retired);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);
        var all = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().BeEmpty();
        all.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ResolveODataPublications_WithCrossProtocolPublication_SelectsODataEntitySetOnce()
    {
        using var provider = CreateProvider(["OData"], includeFeatureServerPublication: true);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);
        var all = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().ContainSingle()
            .Which.Publication.PublicationType.Should().Be(MetadataV2LayerType.ODataEntitySet);
        all.Should().ContainSingle()
            .Which.Publication.PublicationType.Should().Be(MetadataV2LayerType.ODataEntitySet);
    }

    [UnitTheory]
    [InlineData(null, 0)]
    [InlineData("tenant-a", 0)]
    [InlineData("tenant-b", 1)]
    public async Task ResolveODataPublications_PublicationTenant_OnlyMatchingTenantSeesEntry(string? tenantId, int expected)
    {
        using var provider = await CreateTenantProviderAsync(tenantId, includeVisibleSibling: false);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);
        var all = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().HaveCount(expected);
        all.Should().HaveCount(expected);
    }

    [UnitTest]
    public async Task ResolveODataPublications_HiddenPrimary_ChoosesVisibleSibling()
    {
        using var provider = await CreateTenantProviderAsync("tenant-a", includeVisibleSibling: true);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);
        var all = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().ContainSingle().Which.Service.Metadata.Name.Should().Be("visible-service");
        all.Should().ContainSingle().Which.Service.Metadata.Name.Should().Be("visible-service");
    }

    [UnitTest]
    public async Task ResolveODataPublications_HiddenPreferredPublication_UsesVisibleSameServiceFallback()
    {
        using var baseline = CreateProvider(["OData"], includeFeatureServerPublication: true);
        var snapshot = await baseline.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var graph = snapshot.Graph with
        {
            Publications = snapshot.Graph.Publications.Select(publication =>
                publication.PublicationType == MetadataV2LayerType.ODataEntitySet
                    ? publication with { Metadata = publication.Metadata with { Tenant = "tenant-b" } }
                    : publication).ToArray()
        };
        using var provider = CreateScopedProvider(graph, "tenant-a");
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);
        var all = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().ContainSingle().Which.Publication.PublicationType.Should().Be(MetadataV2LayerType.EsriFeatureLayer);
        all.Should().ContainSingle().Which.Publication.PublicationType.Should().Be(MetadataV2LayerType.EsriFeatureLayer);
    }

    [UnitTest]
    public async Task ResolveODataPublications_SameTenantPolicyDenied_RetainsAllForDenialResponse()
    {
        using var baseline = CreateProvider(["OData"]);
        var snapshot = await baseline.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var graph = snapshot.Graph with
        {
            Resources = snapshot.Graph.Resources.Select(resource => resource with
            {
                AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["catalog-owner"] }
            }).ToArray(),
            Publications = snapshot.Graph.Publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Tenant = "tenant-b" }
            }).ToArray()
        };
        using var provider = CreateScopedProvider(graph, "tenant-b");
        var context = CreateContext(provider);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "other-role")], "test"));

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);
        var all = await ODataV2Lookups.ResolveAllODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().BeEmpty();
        all.Should().ContainSingle();
    }

    private static async Task<ServiceProvider> CreateTenantProviderAsync(string? tenantId, bool includeVisibleSibling)
    {
        using var baseline = CreateProvider(["OData"]);
        var snapshot = await baseline.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var publication = snapshot.Graph.Publications.Single();
        var graph = snapshot.Graph with
        {
            Publications = [publication with
            {
                Metadata = publication.Metadata with { Tenant = "tenant-b" }, IsPrimary = true
            }]
        };
        if (includeVisibleSibling)
        {
            var service = graph.Services.Single();
            graph = graph with
            {
                Services = [.. graph.Services, service with
                {
                    Metadata = service.Metadata with { Id = "visible-service", Name = "visible-service" }
                }],
                Publications = [.. graph.Publications, publication with
                {
                    Metadata = publication.Metadata with { Id = "visible-publication" }, ServiceId = "visible-service"
                }]
            };
        }
        return CreateScopedProvider(graph, tenantId);
    }

    private static ServiceProvider CreateScopedProvider(MetadataV2Graph graph, string? tenantId)
    {
        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeTrue();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        return new ServiceCollection()
            .AddSingleton<ITenantContext>(tenant)
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .BuildServiceProvider();
    }

    private static ServiceProvider CreateProvider(
        IReadOnlyList<string> protocols,
        MetadataV2LifecycleStatus publicationLifecycle = MetadataV2LifecycleStatus.Active,
        bool includeFeatureServerPublication = false)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource(
                "res-layer-1",
                "Layer1",
                fields:
                [
                    new MetadataV2Field
                    {
                        Name = "objectid",
                        Type = MetadataV2FieldType.Integer,
                        Nullable = false
                    },
                    new MetadataV2Field
                    {
                        Name = "name",
                        Type = MetadataV2FieldType.String
                    }
                ],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding(
                "binding-layer-1",
                "res-layer-1",
                "test.layers.1",
                storageLayerId: 101)
            .AddService(
                "svc-alpha",
                "alpha",
                protocols: protocols,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddPublication(
                "pub-alpha-layer-1",
                "svc-alpha",
                "res-layer-1",
                layerIndex: 1,
                storageBindingId: "binding-layer-1",
                publicationType: MetadataV2LayerType.ODataEntitySet)
            .Build();
        graph = graph with
        {
            Publications = graph.Publications
                .Select(publication => publication with
                {
                    Status = publication.Status with { Lifecycle = publicationLifecycle }
                })
                .ToArray()
        };
        if (includeFeatureServerPublication)
        {
            var odataPublication = graph.Publications.Single();
            graph = graph with
            {
                Publications =
                [
                    .. graph.Publications,
                    odataPublication with
                    {
                        Metadata = odataPublication.Metadata with { Id = "pub-alpha-layer-1-feature" },
                        PublicationType = MetadataV2LayerType.EsriFeatureLayer,
                    },
                ],
            };
        }
        var graphProvider = new TestMetadataV2GraphProvider(graph);

        return new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton<IMetadataV2GraphProvider>(graphProvider)
            .BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(ServiceProvider provider)
        => new DefaultHttpContext
        {
            RequestServices = provider
        };
}
