// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// The numeric ImageServer route is keyed by storage layer id. A feature publication that
/// shares that id must not authorize the raster pixels (#6452).
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerNumericLayerAuthorizationTests
{
    private const int StorageLayerId = 7;

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ValidateLayerAsync_OpenFeaturePublication_AuthorizesTheRasterResource()
    {
        var graph = BuildCollidingGraph(
            featurePolicy: new AccessPolicy { AllowAnonymous = true },
            rasterPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["imagery-reader"] });
        var resolver = CreateResolver(graph);

        var anonymousContext = CreateContext();
        var anonymous = await resolver.ValidateLayerAsync(
            StorageLayerId, anonymousContext, AuthorizationOperation.Query, CancellationToken.None);
        anonymous.ErrorResult.Should().NotBeNull();
        anonymous.PublicationId.Should().BeNull();
        await AssertGeoServicesErrorAsync(anonymousContext, anonymous.ErrorResult!, 499);

        var wrongRoleContext = CreateContext(new Claim(ClaimTypes.Role, "cartography-reader"));
        var wrongRole = await resolver.ValidateLayerAsync(
            StorageLayerId,
            wrongRoleContext,
            AuthorizationOperation.Query,
            CancellationToken.None);
        wrongRole.ErrorResult.Should().NotBeNull();
        wrongRole.PublicationId.Should().BeNull();
        await AssertGeoServicesErrorAsync(wrongRoleContext, wrongRole.ErrorResult!, StatusCodes.Status403Forbidden);

        var entitled = await resolver.ValidateLayerAsync(
            StorageLayerId,
            CreateContext(new Claim(ClaimTypes.Role, "imagery-reader")),
            AuthorizationOperation.Query,
            CancellationToken.None);
        entitled.ErrorResult.Should().BeNull();
        entitled.LayerId.Should().Be(StorageLayerId);
        entitled.PublicationId.Should().Be("pub-raster");
        entitled.ServiceId.Should().Be("svc-imagery");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ValidateLayerAsync_RestrictedFeaturePublication_DoesNotHideAnOpenRaster()
    {
        var graph = BuildCollidingGraph(
            featurePolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["cartography-reader"] },
            rasterPolicy: new AccessPolicy { AllowAnonymous = true });
        var resolver = CreateResolver(graph);

        var resolution = await resolver.ValidateLayerAsync(
            StorageLayerId, CreateContext(), AuthorizationOperation.Query, CancellationToken.None);

        resolution.ErrorResult.Should().BeNull();
        resolution.PublicationId.Should().Be("pub-raster");
        resolution.LayerId.Should().Be(StorageLayerId);
    }

    private static MetadataV2ImageServerLayerResolver CreateResolver(TestMetadataV2GraphProvider graph)
        => new(new ResourceValidator(graph), graph, Substitute.For<IRasterStore>());

    private static TestMetadataV2GraphProvider BuildCollidingGraph(AccessPolicy featurePolicy, AccessPolicy rasterPolicy)
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-features", "features", MetadataV2ResourceType.FeatureDataset, accessPolicy: featurePolicy)
            .AddStorageBinding("binding-features", "res-features", "features", storageLayerId: StorageLayerId)
            .AddService(
                "svc-features",
                "aaa-features",
                protocols: [ServiceProtocols.ImageServer, ServiceProtocols.FeatureServer])
            .AddPublication(
                "pub-features",
                "svc-features",
                "res-features",
                storageBindingId: "binding-features",
                layerIndex: StorageLayerId,
                publicationType: MetadataV2PublicationType.EsriFeatureLayer,
                isPrimary: true)
            .AddResource("res-raster", "imagery", MetadataV2ResourceType.RasterDataset, accessPolicy: rasterPolicy)
            .AddStorageBinding("binding-raster", "res-raster", "rasters", storageLayerId: StorageLayerId)
            .AddService("svc-imagery", "zzz-imagery", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                "pub-raster",
                "svc-imagery",
                "res-raster",
                storageBindingId: "binding-raster",
                layerIndex: StorageLayerId,
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();

    private static DefaultHttpContext CreateContext(Claim? role = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddValidationServices();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };
        context.Request.Path = "/rest/services/7/ImageServer";
        if (role is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([role], authenticationType: "test"));
        }

        return context;
    }
}
