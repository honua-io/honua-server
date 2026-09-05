// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

using System.Security.Claims;
using System.Xml.Linq;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class Wfs20StoredQueryCancellationTests
{
    [Theory]
    [InlineData("ListStoredQueries", "SetMembers")]
    [InlineData("DescribeStoredQueries", "SetMembers")]
    [InlineData("GetFeature", "SetMembers")]
    [InlineData("CreateStoredQuery", "SetMembers")]
    [InlineData("DropStoredQuery", "SetMembers")]
    [InlineData("DescribeStoredQueries", "StringGet")]
    [InlineData("CreateStoredQuery", "StringSet")]
    [InlineData("CreateStoredQuery", "SetAdd")]
    [InlineData("DropStoredQuery", "KeyDelete")]
    [InlineData("DropStoredQuery", "SetRemove")]
    public async Task ManagedOperation_PendingRedis_ReturnsTaskAndObservesCancellation(string operation, string waitingOperation)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingBoolean = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RedisValue[] ids = operation == "CreateStoredQuery" ? [] : ["urn:test:budget"];
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            if (waitingOperation != "SetMembers") return Task.FromResult(ids);
            entered.TrySetResult();
            return pending.Task;
        });
        database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            if (waitingOperation != "StringGet")
                return Task.FromResult<RedisValue[]>(["{\"Id\":\"urn:test:budget\",\"Parameters\":[]}"]);
            entered.TrySetResult();
            return pending.Task;
        });
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(_ => BooleanResult("StringSet"));
        database.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(_ => BooleanResult("SetAdd"));
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(_ => BooleanResult("KeyDelete"));
        database.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(_ => BooleanResult("SetRemove"));

        Task<bool> BooleanResult(string name)
        {
            if (waitingOperation != name) return Task.FromResult(true);
            entered.TrySetResult();
            return pendingBoolean.Task;
        }
        var graph = new TestMetadataV2GraphBuilder().BuildProvider();
        var handler = CreateHandler(graph);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("stored-query-budget-" + Guid.NewGuid().ToString("N"));
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(tenant)
            .AddSingleton(redis).AddSingleton<IMetadataV2GraphProvider>(graph).BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"))
        };
        context.Request.Path = "/wfs";
        context.Items["LimitsTimeoutToken"] = cancellation.Token;
        context.Items[Wfs20DispatcherEndpoint.ParsedXmlDocumentItemKey] = XDocument.Parse("""
            <wfs:CreateStoredQuery xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:StoredQueryDefinition id="urn:test:budget">
                <wfs:QueryExpressionText language="urn:ogc:def:queryLanguage:OGC-WFS::WFSQueryExpression">
                  <wfs:Query typeNames="test" />
                </wfs:QueryExpressionText>
              </wfs:StoredQueryDefinition>
            </wfs:CreateStoredQuery>
            """);
        var parametersType = typeof(Wfs20DispatcherEndpoint).GetNestedType("WfsRequestParameters", BindingFlags.NonPublic)!;
        var parameters = Activator.CreateInstance(parametersType, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = "WFS",
            ["version"] = "2.0.0",
            ["request"] = operation,
            ["storedquery_id"] = "urn:test:budget"
        })!;
        var endpoint = typeof(Wfs20DispatcherEndpoint).GetMethod("Handle" + operation, BindingFlags.Static | BindingFlags.NonPublic)!;
        var returned = new TaskCompletionSource<Task<IResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                returned.SetResult((Task<IResult>)endpoint.Invoke(null,
                    [context, parameters, handler, NullLogger.Instance])!);
            }
            catch (Exception ex) { returned.SetException(ex); }
        })
        { IsBackground = true };
        thread.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            request.IsCompleted.Should().BeFalse("Redis has not completed");
            if (waitingOperation is "StringSet" or "SetAdd")
            {
                database.ReceivedCalls().Select(call => call.GetMethodInfo().Name)
                    .Should().Contain(["StringSetAsync", "SetAddAsync"],
                        "both mutations must be dispatched before exposing cancellation");
            }
            else if (waitingOperation is "KeyDelete" or "SetRemove")
            {
                database.ReceivedCalls().Select(call => call.GetMethodInfo().Name)
                    .Should().Contain(["KeyDeleteAsync", "SetRemoveAsync"],
                        "index cleanup must already be dispatched when deletion is pending");
            }

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await request.WaitAsync(TimeSpan.FromSeconds(1)));
            var issuedCalls = database.ReceivedCalls().Count();
            pending.TrySetResult([]);
            pendingBoolean.TrySetResult(true);
            await Task.Yield();
            database.ReceivedCalls().Count().Should().Be(issuedCalls,
                "cancellation must prevent subsequent Redis operations");
        }
        finally
        {
            pending.TrySetResult([]);
            pendingBoolean.TrySetResult(true);
            thread.Join(TimeSpan.FromSeconds(2)).Should().BeTrue();
            if (returned.Task.IsCompletedSuccessfully)
            {
                try { await (await returned.Task); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static Wfs20Handler CreateHandler(IMetadataV2GraphProvider metadataProvider)
    {
        var coordinateTransformService = Substitute.For<ICoordinateTransformService>();
        var queryServices = new Wfs20QueryServices(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IGmlFeatureStore>(),
            metadataProvider,
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            Substitute.For<IQueryProcessor>(),
            Options.Create(new Wfs20Options()),
            Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>());

        var editServices = new Wfs20EditServices(
            Substitute.For<IFeatureWriter>(),
            new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance),
            Substitute.For<IEditProcessor>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            new FeatureMutationEventService(
                Substitute.For<IFeatureChangeEventPublisher>(),
                outboxCapabilityProvider: Substitute.For<IOutboxCapabilityProvider>()),
            Options.Create(new LimitsOptions()));

        var spatialServices = new Wfs20SpatialServices(
            new OgcFeaturesGeometryServices(
                Substitute.For<IGeometryService>(),
                coordinateTransformService,
                Options.Create(new LimitsOptions()),
                NullLogger<OgcFeaturesGeometryServices>.Instance),
            coordinateTransformService,
            Substitute.For<ICrsRegistry>());

        return new Wfs20Handler(NullLogger<Wfs20Handler>.Instance, queryServices, editServices, spatialServices);
    }
}
