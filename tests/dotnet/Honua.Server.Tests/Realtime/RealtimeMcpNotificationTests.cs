// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.Mcp)]
[Operation(Operations.TestInfrastructure)]
public sealed class RealtimeMcpNotificationTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task PublishProgress_SessionOnAnotherTask_PersistsJsonRpcNotification()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var remoteSessions = new McpSessionManager();
        var sessionId = remoteSessions.CreateSession();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new McpSessionManager())
            .AddSingleton<IRealtimeFanoutProvider>(new PostgresRealtimeFanoutProvider(
                harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log)))
            .BuildServiceProvider();
        var publisher = ActivatorUtilities.CreateInstance<McpNotificationPublisher>(services);
        var host = (object)publisher as IHostedService;
        if (host is not null)
        {
            await host.StartAsync(CancellationToken.None);
        }

        try
        {
            publisher.PublishProgress(sessionId, "job-remote", 42, 100, "Running").Should().BeTrue();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            IReadOnlyList<RealtimeEvent> events;
            do
            {
                events = await harness.Log.ReadAfterAsync(new RealtimeLogRead("mcp", 0, 10), timeout.Token);
                if (events.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
                }
            } while (events.Count == 0);

            var persisted = events.Should().ContainSingle().Subject;
            persisted.ScopeKey.Should().Be(sessionId);
            persisted.Payload.GetProperty("jsonrpc").GetString().Should().Be("2.0");
            persisted.Payload.GetProperty("method").GetString().Should().Be("notifications/progress");
            var parameters = persisted.Payload.GetProperty("params");
            parameters.GetProperty("progressToken").GetString().Should().Be("job-remote");
            parameters.GetProperty("progress").GetDouble().Should().Be(42);
        }
        finally
        {
            if (host is not null)
            {
                await host.StopAsync(CancellationToken.None);
            }

            ((object)publisher as IDisposable)?.Dispose();
        }
    }
}
