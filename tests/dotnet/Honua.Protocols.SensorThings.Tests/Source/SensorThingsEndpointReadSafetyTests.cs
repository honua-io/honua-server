// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Core.Queries.Filters;
using Honua.Protocols.SensorThings;
using Honua.Protocols.SensorThings.Services;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>Counterexamples for credential links, cyclic navigation and stream time ranges.</summary>
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsEndpointReadSafetyTests
{
    private static DefaultHttpContext Context(string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("sensor.example.test");
        context.Request.QueryString = new QueryString(query);
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["SensorThings:ConformantProfile"] = "true" }).Build())
            .BuildServiceProvider();
        return context;
    }

    private static StaFilterTranslator Translator() => new(Substitute.For<IFilterExpressionService>());

    private static string CyclicPath(int segments) => "/sta/v1.1/Things(1)/" + string.Join('/',
        Enumerable.Range(1, segments - 1).Select(index => index % 2 == 1 ? "Datastreams(1)" : "Thing"));

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourcePath_OverDepth_IsRejectedBeforeAnyStoreCall(bool write)
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var context = Context(CyclicPath(SensorThingsRelationships.MaxRelationshipDepth + 2));
        context.Request.Method = write ? "DELETE" : "GET";
        var result = write
            ? await SensorThingsEndpoints.HandleCoreWrite(context, store, NullLogger<SensorThingsEndpoints.SensorThingsEndpointsLog>.Instance)
            : await SensorThingsEndpoints.HandleCoreRead(context, store, Translator());
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(store.ReceivedCalls());
    }

    [UnitTest]
    public async Task ResourcePath_AtDepthBoundary_StillResolvesCyclicNavigation()
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var entity = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["@iot.id"] = 1 });
        store.GetEntityAsync(Arg.Any<string>(), 1, Arg.Any<CancellationToken>()).Returns((JsonElement?)entity);
        store.QueryEntitiesAsync(Arg.Any<string>(), Arg.Any<SensorThingsEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SensorThingsEntityPage([entity], 1));
        var result = await SensorThingsEndpoints.HandleCoreRead(Context(CyclicPath(SensorThingsRelationships.MaxRelationshipDepth + 1)), store, Translator());
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        await store.Received().GetEntityAsync("Datastreams", 1, Arg.Any<CancellationToken>());
        Assert.All(store.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ISensorThingsEntityStore.QueryEntitiesAsync)),
            call => Assert.False(((SensorThingsEntityQuery)call.GetArguments()[1]!).Count));
    }

    [UnitTheory]
    [InlineData("$access_token")]
    [InlineData("$api_key")]
    public async Task UnknownSystemOption_IsRejectedWithoutCredentialContinuation(string option)
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var result = await SensorThingsEndpoints.HandleCoreRead(
            Context("/sta/v1.1/Things", $"?$top=1&{option}=private-value"), store, Translator());
        Assert.Equal(StatusCodes.Status501NotImplemented, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.DoesNotContain(store.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ISensorThingsEntityStore.QueryEntitiesAsync));
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continuation_OnlyRetainsSupportedOptionsAndRequestsCountsWhenSelected(bool count)
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var first = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["@iot.id"] = 1 });
        var second = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["@iot.id"] = 2 });
        store.QueryEntitiesAsync(Arg.Any<string>(), Arg.Any<SensorThingsEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SensorThingsEntityPage([first, second], 2));
        var countValue = count ? "true" : "false";
        var context = Context("/sta/v1.1/Things",
            $"?$top=1&$skip=0&$count={countValue}&$select=@iot.id&$expand=Datastreams($top=1;$count={countValue})"
            + "&access_token=private-token&api_key=private-key&custom=private-value");
        var result = await SensorThingsEndpoints.HandleCoreRead(context, store, Translator());
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var body = Assert.IsType<JsonElement>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        var link = body.GetProperty("@iot.nextLink").GetString()!;
        var query = QueryHelpers.ParseQuery(new Uri(link).Query);
        Assert.Equal(5, query.Count);
        Assert.Equal("1", query["$top"].ToString());
        Assert.Equal("1", query["$skip"].ToString());
        Assert.Equal(countValue, query["$count"].ToString());
        Assert.Equal("@iot.id", query["$select"].ToString());
        Assert.Equal($"Datastreams($top=1;$count={countValue})", query["$expand"].ToString());
        Assert.DoesNotContain("private", link, StringComparison.Ordinal);
        Assert.All(store.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ISensorThingsEntityStore.QueryEntitiesAsync)),
            call => Assert.Equal(count, ((SensorThingsEntityQuery)call.GetArguments()[1]!).Count));
        Assert.Equal(count, body.TryGetProperty("@iot.count", out _));
        Assert.Equal(count, body.GetProperty("value")[0].TryGetProperty("Datastreams@iot.count", out _));
    }

    [UnitTheory]
    [InlineData(false, "2026-09-05T01:02:03.000Z")]
    [InlineData(true, "2026-09-05T01:02:03.000Z/2026-09-05T04:05:06.000Z")]
    public void StreamFrame_InstantAndInterval_PreserveRestTimeAndSerialize(bool interval, string expected)
    {
        var observation = new SensorThingsObservation
        {
            Id = 9,
            DatastreamId = 1,
            Result = 42.5,
            PhenomenonTime = DateTimeOffset.Parse("2026-09-04T15:02:03-10:00", CultureInfo.InvariantCulture),
            PhenomenonTimeEnd = interval ? DateTimeOffset.Parse("2026-09-05T04:05:06Z", CultureInfo.InvariantCulture) : null
        };
        var frame = ObservationStreamFrame.FromObservation(observation);
        var rest = StaEntityMapper.MapObservation(observation, "https://sensor.example.test/sta/v1.1");
        Assert.Equal(expected, rest.PhenomenonTime);
        Assert.Equal(expected, frame.PhenomenonTime);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame));
        Assert.Equal(expected, payload.RootElement.GetProperty("phenomenonTime").GetString());
        Assert.Equal(9, payload.RootElement.GetProperty("@iot.id").GetInt64());
        Assert.Equal(42.5, payload.RootElement.GetProperty("result").GetDouble());
    }
}
