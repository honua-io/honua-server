// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Protocols.SensorThings;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>Object-valued <c>@iot.id</c> references are client errors, not store failures.</summary>
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsEntityIdValidationTests
{
    [UnitTheory]
    [InlineData("POST", "/sta/v1.1/Datastreams", """{"Thing":{"@iot.id":{"value":1}}}""")]
    [InlineData("POST", "/sta/v1.1/Datastreams", """{"Thing":{"@iot.id":1.5}}""")]
    [InlineData("POST", "/sta/v1.1/Datastreams", """{"Thing":{"@iot.id":"1"}}""")]
    [InlineData("PATCH", "/sta/v1.1/Datastreams(4)", """{"Thing":{"@iot.id":{"value":1}}}""")]
    public async Task Write_NonIntegerIotId_Returns400WithoutMutating(string method, string path, string json)
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("sensor.example.test");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await SensorThingsEndpoints.HandleCoreWrite(
            context, store, NullLogger<SensorThingsEndpoints.SensorThingsEndpointsLog>.Instance);

        var problem = Assert.IsType<JsonHttpResult<ProblemDetailsResponse>>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("@iot.id must be a positive integer.", problem.Value?.Detail);
        await store.DidNotReceive().CreateEntityAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().PatchEntityAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }
}
