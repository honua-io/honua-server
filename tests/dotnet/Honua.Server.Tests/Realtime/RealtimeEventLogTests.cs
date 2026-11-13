// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class RealtimeEventLogTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly JsonElement _smallPayload = JsonSerializer.SerializeToElement(new { id = "evt-1", n = 1 });

    [IntegrationTest]
    public async Task Append_ConcurrentWriters_ProduceContiguousSequencesInCommitOrder()
    {
        await using var harness = await CreateHarnessAsync();
        const int writers = 8;
        const int transactionsPerWriter = 20;
        using var writing = new CancellationTokenSource();
        var gaps = new List<string>();

        // A reader that sees a higher sequence before a lower one has seen commit order differ from
        // sequence order; every snapshot must therefore be exactly 1..max.
        var observer = Task.Run(async () =>
        {
            while (!writing.IsCancellationRequested)
            {
                var seen = await harness.ReadSequencesAsync(RealtimeLogFamilies.Admin);
                if (seen.Count > 0 && seen[^1] != seen.Count)
                {
                    gaps.Add(string.Join(',', seen));
                }
            }
        });

        var appended = await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            var total = 0;
            for (var i = 0; i < transactionsPerWriter; i++)
            {
                var rows = Enumerable.Range(0, 1 + ((writer + i) % 3))
                    .Select(n => new RealtimeLogRow($"topic-{writer}", "test", _smallPayload)).ToArray();
                await using var connection = await harness.DataSource.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await harness.Log.AppendAsync(RealtimeLogFamilies.Admin, rows, transaction, CancellationToken.None);
                await Task.Delay((writer + i) % 4);
                await transaction.CommitAsync();
                total += rows.Length;
            }

            return total;
        })));
        await writing.CancelAsync();
        await observer;

        gaps.Should().BeEmpty("no reader may see a sequence before every lower one has committed");
        var expected = appended.Sum();
        (await harness.ReadSequencesAsync(RealtimeLogFamilies.Admin)).Should().Equal(Enumerable.Range(1, expected).Select(seq => (long)seq));
        (await harness.ScalarAsync<long>($"SELECT head FROM {harness.Table("realtime_log_heads")} WHERE family = 'admin'"))
            .Should().Be(expected);
    }

    [IntegrationTest]
    public async Task Append_Batch_StoresRowsInOrderAndReturnsTheHead()
    {
        await using var harness = await CreateHarnessAsync();
        var first = await harness.Log.AppendAsync(RealtimeLogFamilies.Jobs, [new("job-1", "status", _smallPayload)], null, CancellationToken.None);
        var head = await harness.Log.AppendAsync(
            RealtimeLogFamilies.Jobs,
            [new("job-2", "status", _smallPayload), new("job-3", "status", JsonSerializer.SerializeToElement(new { status = "done" }))],
            null,
            CancellationToken.None);

        first.Should().Be(1);
        head.Should().Be(3);
        (await harness.ScalarAsync<string>(
                $"SELECT string_agg(seq || ':' || scope_key || ':' || coalesce(payload->>'status', '-'), ',' ORDER BY seq) FROM {harness.Table("realtime_events")} WHERE family = 'jobs' AND seq > 1"))
            .Should().Be("2:job-2:-,3:job-3:done");
        (await harness.ScalarAsync<bool>(
                $"SELECT bool_and(expires_at = created_at + interval '24 hours') FROM {harness.Table("realtime_events")}"))
            .Should().BeTrue("the default retention is 24 hours");
    }

    [IntegrationTest]
    public async Task Append_InRolledBackTransaction_LeavesNoGap()
    {
        await using var harness = await CreateHarnessAsync();
        await using (var connection = await harness.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await harness.Log.AppendAsync(RealtimeLogFamilies.Mcp, [new("*", "list-changed", _smallPayload)], transaction, CancellationToken.None);
            await transaction.RollbackAsync();
        }

        (await harness.Log.AppendAsync(RealtimeLogFamilies.Mcp, [new("*", "list-changed", _smallPayload)], null, CancellationToken.None))
            .Should().Be(1);
    }

    [IntegrationTest]
    public async Task Append_NotifiesTheFamilyChannelWithoutPayload()
    {
        await using var harness = await CreateHarnessAsync();
        await using var listener = await harness.DataSource.OpenConnectionAsync();
        var received = new TaskCompletionSource<NpgsqlNotificationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, notification) => received.TrySetResult(notification);
        await using (var listen = new NpgsqlCommand("LISTEN \"honua_rt_collab-ops\"", listener))
        {
            await listen.ExecuteNonQueryAsync();
        }

        await harness.Log.AppendAsync(RealtimeLogFamilies.CollabOps, [new("map-1", "operation", _smallPayload)], null, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!received.Task.IsCompleted)
        {
            await listener.WaitAsync(timeout.Token);
        }

        var notification = await received.Task;
        notification.Channel.Should().Be("honua_rt_collab-ops");
        notification.Payload.Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task Append_PayloadOver64KiB_IsRefusedByWriterAndFunction()
    {
        await using var harness = await CreateHarnessAsync();
        var oversized = JsonSerializer.SerializeToElement(new { text = new string('x', PostgresRealtimeEventLog.MaxPayloadBytes) });

        var write = () => harness.Log.AppendAsync(RealtimeLogFamilies.Alerts, [new("svc-1", "alert", oversized)], null, CancellationToken.None);
        await write.Should().ThrowAsync<ArgumentException>().WithMessage("*over 65536 bytes*");

        var direct = () => harness.ExecuteAsync(
            $"SELECT {harness.Table("realtime_append")}('alerts', jsonb_build_array(jsonb_build_object('scope_key', 's', 'kind', 'k', 'payload', jsonb_build_object('text', repeat('x', 65536)))))");
        (await direct.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("54000");
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("realtime_events")}")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Append_ScopeKeyOverTheLimit_IsRefusedBeforeTheDatabase()
    {
        await using var harness = await CreateHarnessAsync();

        var write = () => harness.Log.AppendAsync(
            RealtimeLogFamilies.Jobs, [new(new string('k', PostgresRealtimeEventLog.MaxScopeKeyBytes + 1), "status", _smallPayload)], null, CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>().WithMessage("*scope key*");
        (await harness.ScalarAsync<long>($"SELECT head FROM {harness.Table("realtime_log_heads")} WHERE family = 'jobs'")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Append_UnknownFamily_IsRefused()
    {
        await using var harness = await CreateHarnessAsync();

        var write = () => harness.Log.AppendAsync("no-such-family", [new("s", "k", _smallPayload)], null, CancellationToken.None);

        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
    }

    [IntegrationTest]
    public async Task ApplyRetention_WritesOnlyChangedFamiliesAndAppendsUseIt()
    {
        await using var harness = await CreateHarnessAsync();
        var retention = new RealtimeOptions().LoggedRetention().ToDictionary(entry => entry.Key, entry => entry.Value);

        var firstStart = await harness.Log.ApplyRetentionAsync(retention, CancellationToken.None);
        var secondStart = await harness.Log.ApplyRetentionAsync(retention, CancellationToken.None);
        await harness.Log.AppendAsync(RealtimeLogFamilies.CollabPresence, [new("map-1", "presence", _smallPayload)], null, CancellationToken.None);

        firstStart.Should().Be(5, "collab-presence, observations, alerts, security and mcp differ from the 24 hour default");
        secondStart.Should().Be(0);
        (await harness.ScalarAsync<TimeSpan>(
                $"SELECT expires_at - created_at FROM {harness.Table("realtime_events")} WHERE family = 'collab-presence'"))
            .Should().Be(TimeSpan.FromMinutes(2));
    }

    [IntegrationTest]
    public async Task DeleteExpired_RemovesExpiredRowsInBoundedBatches()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.Log.AppendAsync(
            RealtimeLogFamilies.Security,
            Enumerable.Range(0, 5).Select(n => new RealtimeLogRow("*", "revoked", _smallPayload)).ToArray(),
            null,
            CancellationToken.None);
        await harness.ExecuteAsync($"UPDATE {harness.Table("realtime_events")} SET expires_at = now() - interval '1 second' WHERE seq <= 3");

        var firstBatch = await harness.Log.DeleteExpiredAsync(2, CancellationToken.None);
        var secondBatch = await harness.Log.DeleteExpiredAsync(2, CancellationToken.None);

        firstBatch.Should().Be(2);
        secondBatch.Should().Be(1);
        (await harness.ReadSequencesAsync(RealtimeLogFamilies.Security)).Should().Equal(4L, 5L);
    }

    [IntegrationTest]
    public async Task DeleteExpired_RemovesAPrefixSoTheOldestRetainedSequenceHasNoHoleBelowIt()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.Log.AppendAsync(
            RealtimeLogFamilies.Alerts,
            Enumerable.Range(0, 5).Select(n => new RealtimeLogRow("svc-1", "alert", _smallPayload)).ToArray(),
            null,
            CancellationToken.None);
        // Retention was lowered: row 4 expires while rows 1-3 have not yet.
        await harness.ExecuteAsync($"UPDATE {harness.Table("realtime_events")} SET expires_at = now() - interval '1 second' WHERE seq = 4");

        var removed = await harness.Log.DeleteExpiredAsync(100, CancellationToken.None);

        removed.Should().Be(4);
        (await harness.ReadSequencesAsync(RealtimeLogFamilies.Alerts)).Should().Equal(5L);
    }

    [IntegrationTest]
    public async Task Append_TimestampsFollowSequenceEvenInsideOneLongTransaction()
    {
        await using var harness = await CreateHarnessAsync();
        await using (var connection = await harness.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var wait = new NpgsqlCommand("SELECT pg_sleep(0.05)", connection, transaction))
            {
                await wait.ExecuteNonQueryAsync();
            }

            await harness.Log.AppendAsync(RealtimeLogFamilies.Admin, [new("t", "first", _smallPayload)], transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await harness.Log.AppendAsync(RealtimeLogFamilies.Admin, [new("t", "second", _smallPayload)], null, CancellationToken.None);

        (await harness.ScalarAsync<bool>(
                $"SELECT (SELECT expires_at FROM {harness.Table("realtime_events")} WHERE seq = 1) < (SELECT expires_at FROM {harness.Table("realtime_events")} WHERE seq = 2)"))
            .Should().BeTrue();
    }

    [IntegrationTest]
    public async Task RetentionWriter_TablesMissingAtStart_RetriesUntilWritten()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database, applyMigration: false);
        using var writer = new RealtimeRetentionStartupService(
            harness.Log,
            Options.Create(new RealtimeOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RealtimeRetentionStartupService>.Instance);

        await writer.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        writer.Written.IsCompleted.Should().BeFalse("the tables do not exist yet");
        await harness.ApplyMigrationAsync();
        await writer.Written.WaitAsync(TimeSpan.FromSeconds(15));
        await writer.StopAsync(CancellationToken.None);

        (await harness.ScalarAsync<TimeSpan>($"SELECT retention FROM {harness.Table("realtime_log_heads")} WHERE family = 'alerts'"))
            .Should().Be(TimeSpan.FromDays(7));
    }

    [UnitTest]
    public void Options_RetentionKeyInAnyCase_IsTheCanonicalFamily()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Retention:Alerts"] = "1.00:00:00" })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddRealtimeEventLog(configuration);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RealtimeOptions>>().Value;

        new RealtimeOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
        options.LoggedRetention().Should().ContainKey("alerts").WhoseValue.Should().Be(TimeSpan.FromDays(1));
        options.LoggedRetention().Keys.Should().OnlyContain(key => RealtimeLogFamilies.Logged.Contains(key));
    }

    [UnitTest]
    public void Options_UnknownFamilyOrNonPositiveRetention_FailValidation()
    {
        var options = new RealtimeOptions();
        options.Retention["colab-ops"] = TimeSpan.FromHours(1);
        options.Retention[RealtimeLogFamilies.Mcp] = TimeSpan.Zero;

        var result = new RealtimeOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(2);
        new RealtimeOptionsValidator().Validate(null, new RealtimeOptions()).Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void Options_BindRetentionOverDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:Retention:alerts"] = "1.00:00:00" })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddRealtimeEventLog(configuration);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RealtimeOptions>>().Value;

        options.Retention[RealtimeLogFamilies.Alerts].Should().Be(TimeSpan.FromDays(1));
        options.Retention[RealtimeLogFamilies.Mcp].Should().Be(TimeSpan.FromMinutes(10));
    }

    [UnitTest]
    public void Registration_EventHandler_KeepsLogWithoutBackgroundRetentionWrite()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_CONTROL_PLANE_LAMBDA_HANDLER"] = "execution-dispatch"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddRealtimeEventLog(configuration);

        services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(PostgresRealtimeEventLog));
        services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(IExpiredRowSweep));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [UnitTest]
    public void Registration_CalledTwice_RegistersOneSweepAndOneStartupWrite()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddRealtimeEventLog(configuration);
        services.AddRealtimeEventLog(configuration);

        services.Where(descriptor => descriptor.ServiceType == typeof(IExpiredRowSweep)).Should().ContainSingle();
        services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory is not null).Should().ContainSingle();
    }

    private Task<RealtimeLogHarness> CreateHarnessAsync() => RealtimeLogHarness.CreateAsync(database);
}
