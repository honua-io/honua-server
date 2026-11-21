// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
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
public sealed class ImageServerExportTilesBoundsTests
{
    private const int StorageLayerId = 1;

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task EstimateAsync_WideExtentAcrossManyLevels_BuildsOnlyMaxTilesAndReportsTheLimit()
    {
        var handler = new ImageServerExportTilesHandler(
            ImageServerTestFixtures.BuildGraphWithLayer(StorageLayerId),
            Substitute.For<IRasterStore>(),
            NullLogger<ImageServerExportTilesHandler>.Instance);
        var context = CreateContext();
        var values = new Dictionary<string, StringValues>
        {
            ["f"] = "json",
            ["format"] = "png",
            ["levels"] = "0-11",
            ["maxTiles"] = "10",
        };

        var (result, allocated) = EstimateOnThisThread(handler, context, values);

        var (status, body) = await ExecuteAsync(context, result);
        status.Should().Be(StatusCodes.Status200OK, body);
        var estimate = JsonSerializer.Deserialize(body, ImageServerJsonContext.Default.ImageServerExportTilesEstimateResponse)!;
        estimate.TileCount.Should().Be(10);
        estimate.ExceededTransferLimit.Should().BeTrue("the whole world at levels 0-11 holds about 5.6 million tiles");
        allocated.Should().BeLessThan(8 * 1024 * 1024, "the plan must not hold the full tile grid in memory");
    }

    // The plan is built synchronously on the calling thread, so its allocations are measurable there.
    private static (IResult Result, long AllocatedBytes) EstimateOnThisThread(
        ImageServerExportTilesHandler handler,
        HttpContext context,
        Dictionary<string, StringValues> values)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = handler.EstimateAsync(context, StorageLayerId, values, CancellationToken.None).GetAwaiter().GetResult();
        return (result, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static DefaultHttpContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddOptions();
        services.AddValidationServices();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([], "test")),
        };
        context.Request.Path = "/rest/services/imagery/ImageServer/estimateExportTilesSize";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(HttpContext context, IResult result)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }
}
