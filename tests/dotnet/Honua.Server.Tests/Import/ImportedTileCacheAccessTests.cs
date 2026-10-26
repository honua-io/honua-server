// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Import.TileCachePackage;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Read authorization for tiles served from the tile catalog (SEC-15). The decision binds a
/// catalog row's layer identifier to the published layer it names and applies that layer's
/// query authorization. Rows written by the package importer and by the WMTS tile-cache
/// exporter carry the same identifier column and are decided the same way.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ImportedTileCacheAccessTests
{
    private const string EditorRole = "parcel-editors";

    [UnitTheory]
    [InlineData("roads")]
    [InlineData("pub-roads")]
    [InlineData("res-roads")]
    public async Task CanRead_PublicLayer_AllowsAnonymous(string layerIdentifier)
    {
        var context = Context(SampleGraph(), Anonymous());

        (await ImportedTileCacheAccess.CanReadAsync(context, layerIdentifier, CancellationToken.None))
            .Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("parcels")]
    [InlineData("pub-parcels")]
    [InlineData("res-parcels")]
    public async Task CanRead_ProtectedLayer_DeniesAnonymous(string layerIdentifier)
    {
        var context = Context(SampleGraph(), Anonymous());

        (await ImportedTileCacheAccess.CanReadAsync(context, layerIdentifier, CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task CanRead_ProtectedLayer_AllowsPrincipalThatCanReadTheLayer()
    {
        var context = Context(SampleGraph(), User(EditorRole));

        (await ImportedTileCacheAccess.CanReadAsync(context, "parcels", CancellationToken.None))
            .Should().BeTrue();
    }

    [UnitTest]
    public async Task CanRead_ProtectedLayer_DeniesPrincipalWithoutTheLayerRole()
    {
        var context = Context(SampleGraph(), User("viewers"));

        (await ImportedTileCacheAccess.CanReadAsync(context, "parcels", CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("served-demo")]
    public async Task CanRead_NoResolvableOrigin_Denies(string? layerIdentifier)
    {
        var context = Context(SampleGraph(), User(EditorRole));

        (await ImportedTileCacheAccess.CanReadAsync(context, layerIdentifier, CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task CanRead_ExportedWmtsCacheNamingNoPublishedLayer_Denies()
    {
        // The WMTS exporter records the source service's layer identifier. When no
        // published layer carries it, the cache has no origin to authorize against.
        var context = Context(SampleGraph(), Anonymous());

        (await ImportedTileCacheAccess.CanReadAsync(context, "topp:states", CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task CanRead_ExportedCacheNamingAProtectedLayer_AppliesThatLayersPolicy()
    {
        var anonymous = Context(SampleGraph(), Anonymous());
        var editor = Context(SampleGraph(), User(EditorRole));

        (await ImportedTileCacheAccess.CanReadAsync(anonymous, "parcels", CancellationToken.None)).Should().BeFalse();
        (await ImportedTileCacheAccess.CanReadAsync(editor, "parcels", CancellationToken.None)).Should().BeTrue();
    }

    [UnitTest]
    public async Task CanRead_TitleIsNotAnIdentifier_Denies()
    {
        var graph = SampleGraph();
        graph = graph with
        {
            Resources = graph.Resources
                .Select(resource => resource.Metadata.Id == "res-roads"
                    ? resource with { Metadata = resource.Metadata with { Title = "Road Network" } }
                    : resource)
                .ToArray()
        };
        var context = Context(graph, Anonymous());

        (await ImportedTileCacheAccess.CanReadAsync(context, "Road Network", CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task CanRead_IdentifierNamingTwoLayers_Denies()
    {
        // A public and a protected layer both advertise "shared"; the cache cannot be
        // attributed to either, so it is not served.
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-open", "open", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddResource("res-closed", "closed", accessPolicy: new AccessPolicy { AllowedRoles = [EditorRole] })
            .AddService("svc-open", "open-service")
            .AddService("svc-closed", "closed-service")
            .AddPublication("pub-open", "svc-open", "res-open", serviceLocalId: "shared")
            .AddPublication("pub-closed", "svc-closed", "res-closed", serviceLocalId: "shared")
            .Build();
        var context = Context(graph, Anonymous());

        (await ImportedTileCacheAccess.CanReadAsync(context, "shared", CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task CanRead_LayerPublishedThroughAProtectedService_DeniesAnonymous()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-roads", "roads", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("svc-open", "open-service", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("svc-closed", "closed-service", accessPolicy: new AccessPolicy { AllowedRoles = [EditorRole] })
            .AddPublication("pub-open", "svc-open", "res-roads")
            .AddPublication("pub-closed", "svc-closed", "res-roads")
            .Build();

        (await ImportedTileCacheAccess.CanReadAsync(Context(graph, Anonymous()), "res-roads", CancellationToken.None))
            .Should().BeFalse("every publication the identifier binds to must allow the caller");
        (await ImportedTileCacheAccess.CanReadAsync(Context(graph, User(EditorRole)), "res-roads", CancellationToken.None))
            .Should().BeTrue();
    }

    [UnitTest]
    public async Task CanRead_RetiredPublication_Denies()
    {
        var graph = SampleGraph();
        graph = graph with
        {
            Publications = graph.Publications
                .Select(publication => publication with
                {
                    Status = publication.Status with { Lifecycle = MetadataV2LifecycleStatus.Retired }
                })
                .ToArray()
        };
        var context = Context(graph, Anonymous());

        (await ImportedTileCacheAccess.CanReadAsync(context, "roads", CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("tenant-b")]
    public async Task CanRead_LayerOwnedByAnotherTenant_DoesNotResolve(string? requestTenant)
    {
        var context = Context(TenantScopedGraph("tenant-a"), User(EditorRole), requestTenant);

        (await ImportedTileCacheAccess.CanReadAsync(context, "roads", CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task CanRead_LayerOwnedByTheRequestTenant_Allows()
    {
        var context = Context(TenantScopedGraph("tenant-a"), Anonymous(), requestTenant: "tenant-a");

        (await ImportedTileCacheAccess.CanReadAsync(context, "roads", CancellationToken.None))
            .Should().BeTrue();
    }

    [UnitTest]
    public async Task CanRead_SameIdentifierInAnotherTenant_IsAmbiguousAndDenies()
    {
        // The catalog row carries no tenant, so a visible public layer must not stand in
        // for a same-named layer owned by another tenant.
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-a", "roads", accessPolicy: new AccessPolicy { AllowedRoles = [EditorRole] })
            .AddResource("res-b", "roads", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("svc-a", "service-a")
            .AddService("svc-b", "service-b")
            .AddPublication("pub-a", "svc-a", "res-a")
            .AddPublication("pub-b", "svc-b", "res-b")
            .Build();
        graph = graph with
        {
            Resources = graph.Resources
                .Select(resource => resource.Metadata.Id == "res-a"
                    ? resource with { Metadata = resource.Metadata with { Tenant = "tenant-a" } }
                    : resource)
                .ToArray()
        };
        var context = Context(graph, Anonymous(), requestTenant: "tenant-b");

        (await ImportedTileCacheAccess.CanReadAsync(context, "roads", CancellationToken.None))
            .Should().BeFalse();
    }

    [UnitTest]
    public async Task CanRead_NoMetadataGraph_Denies()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = Anonymous() };

        (await ImportedTileCacheAccess.CanReadAsync(context, "roads", CancellationToken.None))
            .Should().BeFalse();
    }

    private static MetadataV2Graph SampleGraph()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-roads", "roads", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddResource("res-parcels", "parcels", accessPolicy: new AccessPolicy { AllowedRoles = [EditorRole] })
            .AddService("svc-city", "city")
            .AddPublication("pub-roads", "svc-city", "res-roads")
            .AddPublication("pub-parcels", "svc-city", "res-parcels")
            .Build();

    private static MetadataV2Graph TenantScopedGraph(string tenant)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-roads", "roads", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService("svc-city", "city")
            .AddPublication("pub-roads", "svc-city", "res-roads")
            .Build();
        return graph with
        {
            Resources = graph.Resources
                .Select(resource => resource with { Metadata = resource.Metadata with { Tenant = tenant } })
                .ToArray()
        };
    }

    private static DefaultHttpContext Context(MetadataV2Graph graph, ClaimsPrincipal user, string? requestTenant = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
        if (requestTenant is not null)
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns(requestTenant);
            services.AddSingleton(tenant);
        }

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = user };
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal User(string role)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, role)], "Test"));
}
