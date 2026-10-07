// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class ServiceApplyEditsResponseTests
{
    [UnitTest]
    public async Task Issue5490_ServiceApplyEditsResponse_IsTheBarePerLayerArray()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var result = FeatureServerEndpoints.CreateServiceApplyEditsResult(
        [
            new ServiceLayerEditResult
            {
                Id = 0,
                AddResults = [new EditResult { ObjectId = 302271, Success = true }],
                UpdateResults = [],
                DeleteResults = []
            }
        ]);

        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);

        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        var layerResult = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        layerResult.GetProperty("id").GetInt32().Should().Be(0);
        layerResult.GetProperty("addResults")[0].GetProperty("objectId").GetInt64().Should().Be(302271);
        layerResult.GetProperty("updateResults").GetArrayLength().Should().Be(0);
        layerResult.GetProperty("deleteResults").GetArrayLength().Should().Be(0);
    }
}
