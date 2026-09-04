// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Protocols.Ogc.Api.Maps;
using Honua.Protocols.Ogc.Api.Maps.Handlers;
using Honua.Protocols.Ogc.Api.Maps.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

public sealed class OgcMapsTimeoutRegressionTests
{
    [Fact]
    public async Task DatasetMap_ExpiredRequestBudget_ReachesDownstreamAsCanceled()
    {
        using var budget = new CancellationTokenSource();
        budget.Cancel();
        var context = new DefaultHttpContext();
        context.Items["LimitsTimeoutToken"] = budget.Token;
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        CancellationToken? observed = null;
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            return ValueTask.FromResult(new MetadataV2GraphSnapshot(
                new MetadataV2Graph(), "\"empty\"", DateTimeOffset.UnixEpoch));
        });
        var handler = new OgcMapsRenderingHandler(
            graphProvider,
            Substitute.For<IRasterMapRenderer>(),
            Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);
        var endpoint = typeof(OgcMapsEndpoints).GetMethod(
            "GetDatasetMap", BindingFlags.Static | BindingFlags.NonPublic)!;

        // Invoke the real route adapter with the token ASP.NET binds when the client
        // has not disconnected. The middleware's independent budget is already expired.
        await (Task<IResult>)endpoint.Invoke(null,
            [new OgcMapRequest(), context, handler, CancellationToken.None])!;

        observed.Should().NotBeNull();
        observed!.Value.IsCancellationRequested.Should().BeTrue(
            "Maps must propagate the configured end-to-end timeout, like Features and STAC");
    }
}
