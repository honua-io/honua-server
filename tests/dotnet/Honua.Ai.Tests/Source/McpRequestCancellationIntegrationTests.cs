// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Real loopback HTTP and PostGIS proof. An independent transaction blocks the
/// production query; the original HTTP request remains open while a second POST
/// cancels its captured wire id. No client abort or substituted feature reader
/// can satisfy the cessation assertions.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpRequestCancellationIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().UseKestrel()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        })
        .ConfigureServices(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, QueryFeaturesTool>());
            services.RemoveAll<IOperatorAuthorizationEvaluator>();
            services.AddSingleton<IOperatorAuthorizationEvaluator, AdmitOperator>();
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task CancelNotification_EndsBlockedDatabaseWork_WhileOriginalHttpRequestStaysOpen()
    {
        using var client = _fixture.CreateAdminClient();
        var session = await InitializeSessionAsync(client);
        var otherSession = await InitializeSessionAsync(client);
        using var warmup = await PostAsync(client, Query("warmup"), session);
        using var warmupJson = JsonDocument.Parse(await warmup.Content.ReadAsStringAsync());
        Assert.False(warmupJson.RootElement.TryGetProperty("error", out _));
        Assert.False(warmupJson.RootElement.GetProperty("result").TryGetProperty("isError", out var isError)
            && isError.GetBoolean());

        Assert.Equal(5, warmupJson.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("count").GetInt64());

        await using var blocker = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var observer = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        var schema = _fixture.CurrentSchema!;
        var table = '"' + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\".features";
        var baseline = await ReadSnapshotAsync(observer, table);
        await using var transaction = await blocker.BeginTransactionAsync();
        var blockerReleased = false;
        await using (var command = new NpgsqlCommand($"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE", blocker, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        // SendAsync uses CancellationToken.None. WaitAsync below only bounds the
        // test's observation; it never aborts the original HTTP request.
        var original = PostAsync(client, Query("blocked-query"), session);
        try
        {
            var pid = await WaitForBlockedQueryAsync(observer, blocker.ProcessID);
            Assert.False(original.IsCompleted, "The original request must still be open at cancellation.");

            // Same principal and exact id, but another authoritative session:
            // no cancellation authority over the blocked work.
            using var wrongSession = await PostAsync(client, Cancellation("blocked-query"), otherSession);
            Assert.Equal(HttpStatusCode.Accepted, wrongSession.StatusCode);
            // A wrong-session cancellation bug only becomes visible when the
            // active replica's monitor observes it. Keep observing the actual
            // provider and open HTTP request across three complete poll intervals.
            var isolationObservation = Stopwatch.StartNew();
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                Assert.True(await IsBlockedAsync(observer, pid, blocker.ProcessID));
                Assert.False(original.IsCompleted);
            }
            while (isolationObservation.Elapsed < McpRequestBounds.PollInterval * 3);

            using var cancel = await PostAsync(client, Cancellation("blocked-query"), session);
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
            Assert.Empty(await cancel.Content.ReadAsStringAsync());

            using var response = await original.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("blocked-query", result.RootElement.GetProperty("id").GetString());
            Assert.Equal(-32800, result.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal("cancelled", result.RootElement.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
            Assert.False(result.RootElement.TryGetProperty("result", out _));
            Assert.False(await IsBlockedAsync(observer, pid, blocker.ProcessID),
                "The database must stop waiting while the independent blocking transaction is STILL held.");

            await using (var released = new NpgsqlCommand("""
                SELECT NOT EXISTS(SELECT 1 FROM pg_locks
                  WHERE pid = @pid AND locktype = 'relation' AND relation = to_regclass(@table))
                """, observer))
            {
                released.Parameters.AddWithValue("pid", pid);
                released.Parameters.AddWithValue("table", table);
                Assert.True((bool)(await released.ExecuteScalarAsync())!, "The cancelled provider command must release its table locks.");
            }

            await transaction.RollbackAsync();
            blockerReleased = true;
            Assert.Equal(baseline, await ReadSnapshotAsync(observer, table));

            // Reuse the exact id after completion: cleanup must release authority,
            // and a cancelled generation must not poison a subsequent real query.
            using var repeated = await PostAsync(client, Cancellation("blocked-query"), session);
            Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
            using var next = await PostAsync(client, Query("blocked-query"), session);
            using var nextJson = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
            Assert.False(nextJson.RootElement.TryGetProperty("error", out _));
            Assert.False(nextJson.RootElement.GetProperty("result").TryGetProperty("isError", out var failed)
                && failed.GetBoolean());
            Assert.Equal(5, nextJson.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("count").GetInt64());
            Assert.Equal(baseline, await ReadSnapshotAsync(observer, table));
        }
        finally
        {
            // On a failed assertion release our own lock, then drain the still-open
            // request naturally. Client disposal is never the cancellation proof.
            if (!blockerReleased && transaction.Connection is not null)
            {
                await transaction.RollbackAsync();
            }

            if (!original.IsCompleted)
            {
                using var drained = await original.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static async Task<int> WaitForBlockedQueryAsync(NpgsqlConnection observer, int blocker)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT pid FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND @blocker = ANY(pg_blocking_pids(pid))
                  AND query ILIKE '%features%'
                """, observer);
            command.Parameters.AddWithValue("blocker", blocker);
            var pid = await command.ExecuteScalarAsync(budget.Token);
            if (pid is int value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), budget.Token);
        }
    }

    private static async Task<bool> IsBlockedAsync(NpgsqlConnection observer, int pid, int blocker)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_stat_activity
              WHERE pid = @pid AND @blocker = ANY(pg_blocking_pids(pid)))
            """, observer);
        command.Parameters.AddWithValue("pid", pid);
        command.Parameters.AddWithValue("blocker", blocker);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadSnapshotAsync(NpgsqlConnection observer, string table)
    {
        await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(f) ORDER BY objectid)::text, '[]') FROM {table} f", observer);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string Query(string id) => $$$$"""
        {"jsonrpc":"2.0","id":"{{{{id}}}}","method":"tools/call","params":{
          "name":"honua_query_features","arguments":{"serviceId":"test","layerId":0,"returnCountOnly":true}}}
        """;

    private static string Cancellation(string id) => $$$$"""
        {"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"{{{{id}}}}","reason":"test cancellation"}}
        """;

    private static async Task<string> InitializeSessionAsync(HttpClient client)
    {
        using var response = await PostAsync(client, """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
              "protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"cancellation-integration","version":"1"}}}
            """, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues(McpSessionManager.SessionHeaderName).Single();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string? session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (session is not null)
        {
            request.Headers.Add(McpSessionManager.SessionHeaderName, session);
        }

        return await client.SendAsync(request, CancellationToken.None);
    }

    private sealed class AdmitOperator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal,
            OperatorAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("real query cancellation fixture"));
    }
}
