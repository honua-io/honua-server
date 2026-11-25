// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Protocols.Zarr;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

[Protocol(TestProtocols.Zarr)]
public sealed class ZarrTileServiceAuthorizationTests
{
    private const int SharedLayerIndex = 3;
    private const string PublicPublicationId = "a-public-pub";
    private const string PrivatePublicationId = "b-private-pub";

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task HandleAsync_LayerIndexSharedWithPrivatePublication_RefusesAnonymousCaller()
    {
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ZarrRegistration>());
        var authorizer = CreateAuthorizer(allowedPublicationId: PublicPublicationId);
        using var provider = BuildServices(CreateSharedIndexGraph(), authorizer, store);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IZarrTileService>();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        var result = await service.HandleAsync(context, SharedLayerIndex, "WebMercatorQuad", 0, 0, 0, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToArray().Should().Equal(AuthenticationChallenge.ApiKeyChallenge);
        await store.DidNotReceive().ListByLayerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task HandleAsync_EveryPublicationSharingTheIndexAllows_ReachesTheStore()
    {
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ZarrRegistration>());
        var authorizer = CreateAuthorizer(allowedPublicationId: null);
        using var provider = BuildServices(CreateSharedIndexGraph(), authorizer, store);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IZarrTileService>();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        await service.HandleAsync(context, SharedLayerIndex, "WebMercatorQuad", 0, 0, 0, CancellationToken.None);

        await store.Received(1).ListByLayerAsync(SharedLayerIndex, Arg.Any<CancellationToken>());
        await authorizer.Received(2).AuthorizePublicationAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<MetadataV2Layer>(),
            Arg.Any<MetadataV2Object>(),
            Arg.Any<MetadataV2Service?>(),
            AuthorizationOperation.Query,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task HandleAsync_OtherTenantPrivatePublicationSharingIndex_FailsClosedAndDoesNotReadStore()
    {
        var store = Substitute.For<IZarrStore>();
        store.ListByLayerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ZarrRegistration>());
        var authorizer = CreateForbiddenExceptAuthorizer(PublicPublicationId);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");

        var services = CreateBaseServices(CreateCrossTenantSharedIndexGraph(), authorizer, store);
        services.AddSingleton(tenant);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IZarrTileService>();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        var result = await service.HandleAsync(context, SharedLayerIndex, "WebMercatorQuad", 0, 0, 0, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        await store.DidNotReceive().ListByLayerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await authorizer.Received(2).AuthorizePublicationAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<MetadataV2Layer>(),
            Arg.Any<MetadataV2Object>(),
            Arg.Any<MetadataV2Service?>(),
            AuthorizationOperation.Query,
            Arg.Any<CancellationToken>());
    }

    private static ILayerAccessAuthorizer CreateForbiddenExceptAuthorizer(string allowedPublicationId)
        => CreateAuthorizerWithPolicy(pub => pub.Metadata.Id == allowedPublicationId
            ? AccessDecision.Allowed()
            : AccessDecision.Forbidden());

    private static MetadataV2Graph CreateCrossTenantSharedIndexGraph()
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        return new MetadataV2Graph
        {
            Resources =
            [
                new() { Metadata = new() { Id = "res-a", Name = "a", Tenant = "tenant-a" }, Status = active, Type = MetadataV2ObjectType.RasterDataset },
                new() { Metadata = new() { Id = "res-b", Name = "b", Tenant = "tenant-b" }, Status = active, Type = MetadataV2ObjectType.RasterDataset }
            ],
            Services =
            [
                new() { Metadata = new() { Id = "svc-a", Name = "svc-a", Tenant = "tenant-a" }, Status = active },
                new() { Metadata = new() { Id = "svc-b", Name = "svc-b", Tenant = "tenant-b" }, Status = active }
            ],
            Publications =
            [
                new() { Metadata = new() { Id = PublicPublicationId, Tenant = "tenant-a" }, Status = active, ServiceId = "svc-a", ResourceId = "res-a", LayerIndex = SharedLayerIndex },
                new() { Metadata = new() { Id = PrivatePublicationId, Tenant = "tenant-b" }, Status = active, ServiceId = "svc-b", ResourceId = "res-b", LayerIndex = SharedLayerIndex }
            ]
        };
    }

    // A null id allows every publication; otherwise only that publication is allowed and
    // the others require authentication.
    private static ILayerAccessAuthorizer CreateAuthorizer(string? allowedPublicationId)
        => CreateAuthorizerWithPolicy(pub => allowedPublicationId is null || pub.Metadata.Id == allowedPublicationId
            ? new AccessDecision(true, false)
            : new AccessDecision(false, true));

    private static ILayerAccessAuthorizer CreateAuthorizerWithPolicy(Func<MetadataV2Layer, AccessDecision> decide)
    {
        var authorizer = Substitute.For<ILayerAccessAuthorizer>();
        authorizer
            .AuthorizePublicationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<MetadataV2Layer>(),
                Arg.Any<MetadataV2Object>(),
                Arg.Any<MetadataV2Service?>(),
                Arg.Any<AuthorizationOperation>(),
                Arg.Any<CancellationToken>())
            .Returns(call => decide(call.ArgAt<MetadataV2Layer>(1)));
        return authorizer;
    }

    private static MetadataV2Graph CreateSharedIndexGraph()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-public", "public-raster", MetadataV2ObjectType.RasterDataset)
            .AddResource("res-private", "private-raster", MetadataV2ObjectType.RasterDataset)
            .AddService("svc-public", "public-service")
            .AddService("svc-private", "private-service")
            .AddPublication(PublicPublicationId, "svc-public", "res-public", layerIndex: SharedLayerIndex)
            .AddPublication(PrivatePublicationId, "svc-private", "res-private", layerIndex: SharedLayerIndex)
            .Build();

    private static ServiceCollection CreateBaseServices(MetadataV2Graph graph, ILayerAccessAuthorizer authorizer, IZarrStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IMetadataV2GraphProvider>(_ => new TestMetadataV2GraphProvider(graph));
        services.AddSingleton(authorizer);
        services.AddSingleton(Substitute.For<ITileMatrixSetRegistry>());
        services.AddZarrServices();
        services.AddSingleton(store);
        return services;
    }

    private static ServiceProvider BuildServices(MetadataV2Graph graph, ILayerAccessAuthorizer authorizer, IZarrStore store)
        => CreateBaseServices(graph, authorizer, store).BuildServiceProvider();
}
