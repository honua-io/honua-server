// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Tiles;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerExportTilesHandlerAuthorizationTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportTilesAsync_ReloadedRestrictedPublication_ReauthorizesSyncAndDurablePlans()
    {
        const int storageLayerId = 1;
        const string publicationId = "publication-1";
        var graphProvider = new TestMetadataV2GraphBuilder()
            .AddResource(
                "replacement-resource",
                "replacement",
                MetadataV2ResourceType.RasterDataset,
                accessPolicy: new AccessPolicy { AllowedRoles = ["imagery-admin"] })
            .AddStorageBinding(
                "replacement-binding",
                "replacement-resource",
                "replacement.rasters",
                storageLayerId: storageLayerId)
            .AddService("replacement-service", "replacement", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                publicationId,
                "replacement-service",
                "replacement-resource",
                layerIndex: 41,
                storageBindingId: "replacement-binding",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();
        var rasterStore = Substitute.For<IRasterStore>();
        var tileExportJobService = Substitute.For<ITileExportJobService>();
        var handler = new ImageServerExportTilesHandler(
            graphProvider,
            rasterStore,
            NullLogger<ImageServerExportTilesHandler>.Instance,
            tileExportJobService: tileExportJobService);

        var syncContext = CreateContext();
        var syncResult = await handler.ExportTilesAsync(
            syncContext,
            storageLayerId,
            new Dictionary<string, StringValues> { ["format"] = "cog" },
            publicationId,
            CancellationToken.None);
        await AssertGeoServicesErrorAsync(syncContext, syncResult, StatusCodes.Status403Forbidden);

        var durableContext = CreateContext();
        var durableResult = await handler.ExportTilesAsync(
            durableContext,
            storageLayerId,
            new Dictionary<string, StringValues>
            {
                ["storageFormatType"] = "esriMapCacheStorageModeCompactV2",
                ["format"] = "tiff",
            },
            publicationId,
            CancellationToken.None);
        await AssertGeoServicesErrorAsync(durableContext, durableResult, StatusCodes.Status403Forbidden);

        await rasterStore.DidNotReceiveWithAnyArgs().QueryRastersAsync(
            default,
            default!,
            default);
        await tileExportJobService.DidNotReceiveWithAnyArgs().SubmitAsync(
            default!,
            default,
            default,
            default!,
            default);
    }

    private static DefaultHttpContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddValidationServices();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
        };
        context.Request.Path = "/rest/services/replacement/ImageServer/exportTiles";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
