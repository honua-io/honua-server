// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.TestKit.Attributes;
using NSubstitute;
using StackExchange.Redis;
using TestProtocols = Honua.TestKit.Constants.ProtocolNames;

namespace Honua.Hosting.ComponentTests;

/// <summary>Routing and observation failures cannot turn an ambiguous Redis debit into permission to send.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisAiExecutionAdmissionRoutingTests
{
    [UnitTest]
    public async Task TryAcquireAsync_OwnerChangesAfterScript_DeniesAnOtherwiseGrantedReservation()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var server = Substitute.For<IServer>();
        connection.GetDatabase().Returns(database);
        connection.GetServer(Arg.Any<EndPoint>()).Returns(server);
        database.IdentifyEndpointAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(
            Task.FromResult<EndPoint?>(new IPEndPoint(IPAddress.Loopback, 6379)),
            Task.FromResult<EndPoint?>(new IPEndPoint(IPAddress.Loopback, 6380)));
        ConfigureObservedAuthority(server);
        var generation = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(new
        {
            code = "admitted",
            reservation = new { generation = generation.ToString("N"), epoch = 2, deadline, lease = deadline + 30_000 }
        });
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)json)));
        var store = new RedisAiExecutionAdmissionStore(connection);

        var result = await store.TryAcquireAsync(AiExecutionContext.Create("deployment", "canonical-actor", "studio-chat"), Guid.NewGuid(), default);

        result.Granted.Should().BeFalse();
        result.Code.Should().Be("unavailable");
        await database.Received(1).ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(),
            CommandFlags.DemandMaster | CommandFlags.NoRedirect);
    }

    [UnitTest]
    public async Task TryAcquireAsync_UnobservableStrictConfiguration_DeniesBeforeScript()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        var server = Substitute.For<IServer>();
        connection.GetDatabase().Returns(database);
        connection.GetServer(Arg.Any<EndPoint>()).Returns(server);
        database.IdentifyEndpointAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<EndPoint?>(new IPEndPoint(IPAddress.Loopback, 6379)));
        ConfigureObservedAuthority(server);
        server.ConfigGetAsync("appendfsync").Returns(Task.FromResult(Array.Empty<KeyValuePair<string, string>>()));
        var store = new RedisAiExecutionAdmissionStore(connection);

        var result = await store.TryAcquireAsync(AiExecutionContext.Create("deployment", "canonical-actor", "studio-chat"), Guid.NewGuid(), default);

        result.Code.Should().Be("unavailable");
        await database.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }

    private static void ConfigureObservedAuthority(IServer server)
    {
        var settings = new Dictionary<string, string>
        {
            ["appendonly"] = "yes",
            ["appendfsync"] = "always",
            ["no-appendfsync-on-rewrite"] = "no",
            ["maxmemory-policy"] = "noeviction"
        };
        server.ConfigGetAsync(Arg.Any<string>(), Arg.Any<CommandFlags>()).Returns(call =>
        {
            var name = call.ArgAt<string>(0);
            return Task.FromResult(new[] { new KeyValuePair<string, string>(name, settings[name]) });
        });
        server.InfoAsync("server").Returns(Task.FromResult(Info(("run_id", new string('a', 40)))));
        server.InfoAsync("replication").Returns(Task.FromResult(Info(("role", "master"), ("master_replid", new string('b', 40)))));
        server.InfoAsync("persistence").Returns(Task.FromResult(Info(("aof_enabled", "1"), ("aof_last_write_status", "ok"))));
    }

    private static IGrouping<string, KeyValuePair<string, string>>[] Info(params (string Name, string Value)[] pairs)
        => pairs.Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value)).GroupBy(_ => "section").ToArray();
}
