// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.SensorThings;
using Honua.Protocols.SensorThings.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>Proves one anonymous <c>$expand</c> cannot fan out without a request-wide budget.</summary>
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsExpansionBudgetTests
{
    private const int FanOutRows = 1000;
    private const int RunawayCeiling = 500;

    private static readonly IReadOnlyList<JsonElement> Rows = CreateRows();

    [UnitTest]
    public async Task Expand_CyclicLocations_Returns400WithinTheQueryBudget()
    {
        var store = new FanOutStore();
        var context = Context("/sta/v1.1/Things", "?$expand=Locations($expand=Things($expand=Locations))");

        var result = await SensorThingsEndpoints.HandleCoreRead(context, store, Translator());

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.InRange(store.Calls, 1, RunawayCeiling);
        Assert.True(store.Calls < SensorThingsEndpoints.MaxExpansionQueries);
        Assert.True(store.Calls < SensorThingsEndpoints.MaxExpandedEntities);
    }

    [UnitTest]
    public async Task NestedTop_AboveNestedMax_IsClampedBeforeTheStoreQuery()
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var entity = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["@iot.id"] = 1 });
        store.QueryEntitiesAsync(Arg.Any<string>(), Arg.Any<SensorThingsEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SensorThingsEntityPage([entity], 1));

        var result = await SensorThingsEndpoints.HandleCoreRead(
            Context("/sta/v1.1/Things", "?$top=1&$expand=Locations($top=1000)"), store, Translator());

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var tops = store.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ISensorThingsEntityStore.QueryEntitiesAsync))
            .Select(call => ((SensorThingsEntityQuery)call.GetArguments()[1]!).Top)
            .ToArray();
        tops.Should().Contain(StaQueryOptions.NestedMaxTop + 1);
        tops.Should().NotContain(StaQueryOptions.MaxTop + 1);
    }

    [UnitTest]
    public async Task DatastreamSelect_OmittingDerivedMembers_SkipsAggregateIntervals()
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var entity = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["@iot.id"] = 1, ["name"] = "gauge" });
        store.QueryEntitiesAsync(Arg.Any<string>(), Arg.Any<SensorThingsEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SensorThingsEntityPage([entity], 1));

        var result = await SensorThingsEndpoints.HandleCoreRead(
            Context("/sta/v1.1/Datastreams", "?$select=name&$top=1"), store, Translator());

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var query = store.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ISensorThingsEntityStore.QueryEntitiesAsync))
            .GetArguments()[1]
            .Should().BeOfType<SensorThingsEntityQuery>().Subject;
        query.IncludeDerivedDatastreamIntervals.Should().BeFalse();
    }

    [UnitTest]
    public async Task DatastreamSelect_PhenomenonTime_KeepsAggregateIntervals()
    {
        var store = Substitute.For<ISensorThingsEntityStore>();
        var entity = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["@iot.id"] = 1 });
        store.QueryEntitiesAsync(Arg.Any<string>(), Arg.Any<SensorThingsEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SensorThingsEntityPage([entity], 1));

        var result = await SensorThingsEndpoints.HandleCoreRead(
            Context("/sta/v1.1/Datastreams", "?$select=phenomenonTime&$top=1"), store, Translator());

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var query = (SensorThingsEntityQuery)store.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ISensorThingsEntityStore.QueryEntitiesAsync))
            .GetArguments()[1]!;
        query.IncludeDerivedDatastreamIntervals.Should().BeTrue();
    }

    private static DefaultHttpContext Context(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("sensor.example.test");
        context.Request.QueryString = new QueryString(query);
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["SensorThings:ConformantProfile"] = "false" }).Build())
            .BuildServiceProvider();
        return context;
    }

    private static StaFilterTranslator Translator() => new(Substitute.For<IFilterExpressionService>());

    private static JsonElement[] CreateRows()
    {
        var rows = new JsonElement[FanOutRows];
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index] = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["@iot.id"] = index + 1 });
        }

        return rows;
    }

    private sealed class FanOutStore : ISensorThingsEntityStore
    {
        public int Calls { get; private set; }

        public SqlFragment TranslateFilter(string entitySet, FilterExpression expression)
            => throw new NotSupportedException();

        public SqlFragment TranslateOrderExpression(string entitySet, FilterExpression expression)
            => throw new NotSupportedException();

        public Task<JsonElement?> GetEntityAsync(string entitySet, long id, CancellationToken cancellationToken)
            => Task.FromResult<JsonElement?>(Rows[0]);

        public Task<SensorThingsEntityPage> QueryEntitiesAsync(
            string entitySet,
            SensorThingsEntityQuery query,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls > RunawayCeiling)
            {
                throw new InvalidOperationException("SensorThings expansion exceeded the test query ceiling.");
            }

            return Task.FromResult(new SensorThingsEntityPage(Rows, Rows.Count));
        }

        public Task<long> CreateEntityAsync(string entitySet, JsonElement body, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<long>> CreateEntityBatchAsync(
            string entitySet,
            IReadOnlyList<JsonElement> bodies,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> PatchEntityAsync(string entitySet, long id, JsonElement body, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> DeleteEntityAsync(string entitySet, long id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<long> CountUnresolvedFeaturesAsync(CancellationToken cancellationToken)
            => Task.FromResult(0L);
    }
}
