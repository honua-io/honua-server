// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Data;
using System.Net.Sockets;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Security.Admission;
using Honua.TestKit.Attributes;
using Honua.TestKit;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Security;

[Collection("Database")]
public sealed partial class PostgresAiExecutionAdmissionStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;
    private readonly List<NpgsqlDataSource> _sources = [];

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAiExecutionAdmissionStoreTests));
        // Use the exact migration SQL, qualified into this isolated test schema.
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "CreateAiExecutionAdmission.sql"));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(migration.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
        var financial = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "CreateAiFinancialAccounting.sql"));
        await using var financialCommand = new NpgsqlCommand(financial.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal), connection);
        await financialCommand.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var source in _sources)
            await source.DisposeAsync();
        await fixture.DropSchemaAsync(_schema);
    }

    [IntegrationTest]
    public async Task TryAcquire_PersistedFinancialModeWithoutValidBasis_DeniesWithoutAttemptCharge()
    {
        var store = Store();
        var state = await InitializeAndResumeAsync(store, new AiAdmissionPolicy());
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.Stop, state), default)).Applied.Should().BeTrue();
        // A persisted financial transition must never be silently interpreted as
        // legacy attempt-only policy when its required price/bounds basis is absent.
        await ExecuteAsync($$"""
            UPDATE "{{_schema}}".ai_execution_policies
            SET policy_json = jsonb_set(policy_json, '{Financial}', '{}'::jsonb), epoch = epoch + 1
            WHERE deployment_id = 'deployment'
            """);

        var decision = await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None);

        decision.Granted.Should().BeFalse("financial mode cannot fall back to legacy admission");
        (await CountAttemptsAsync()).Should().Be(0, "a denied financial request must not partially charge attempts");
    }

    [IntegrationTest]
    public async Task TryAcquire_TwoIndependentPoolsRaceAllScopes_DenialNeverPartiallyCharges()
    {
        var first = Store();
        var second = Store();
        await InitializeAndResumeAsync(first, new AiAdmissionPolicy
        {
            DeploymentAttempts = 3,
            ActorAttempts = 2,
            CohortAttempts = 2,
            WorkflowAttempts = 1,
            DeploymentConcurrency = 3,
            ActorConcurrency = 2,
            CohortConcurrency = 2,
            WorkflowConcurrency = 1
        });
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(async index =>
        {
            await barrier.Task;
            var context = Context("actor-a", $"workflow-{index % 2}");
            return await (index % 2 == 0 ? first : second).TryAcquireAsync(context, Guid.NewGuid(), CancellationToken.None);
        }).ToArray();
        barrier.SetResult();
        var decisions = await Task.WhenAll(tasks);

        decisions.Count(result => result.Granted).Should().Be(2);
        var third = await second.TryAcquireAsync(Context("actor-b", "workflow-other"), Guid.NewGuid(), CancellationToken.None);
        third.Granted.Should().BeTrue("actor A denials cannot debit the remaining deployment allowance");
        (await first.TryAcquireAsync(Context("actor-c"), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("budget_exceeded");
        (await CountAttemptsAsync()).Should().Be(3);
    }

    [IntegrationTheory]
    [InlineData("deployment", false)]
    [InlineData("actor", false)]
    [InlineData("cohort", false)]
    [InlineData("workflow", false)]
    [InlineData("deployment", true)]
    [InlineData("actor", true)]
    [InlineData("cohort", true)]
    [InlineData("workflow", true)]
    public async Task TryAcquire_EachScopeIndependentlyBindsAcrossPools(string scope, bool concurrency)
    {
        // Exactly one dimension binds. A missing predicate for any individual scope
        // permits multiple winners and fails that scope's independent regression.
        var policy = new AiAdmissionPolicy
        {
            DeploymentAttempts = 100,
            ActorAttempts = 100,
            CohortAttempts = 100,
            WorkflowAttempts = 100,
            DeploymentConcurrency = 100,
            ActorConcurrency = 100,
            CohortConcurrency = 100,
            WorkflowConcurrency = 100
        };
        policy = (scope, concurrency) switch
        {
            ("deployment", false) => policy with { DeploymentAttempts = 1 },
            ("actor", false) => policy with { ActorAttempts = 1 },
            ("cohort", false) => policy with { CohortAttempts = 1 },
            ("workflow", false) => policy with { WorkflowAttempts = 1 },
            ("deployment", true) => policy with { DeploymentConcurrency = 1 },
            ("actor", true) => policy with { ActorConcurrency = 1 },
            ("cohort", true) => policy with { CohortConcurrency = 1 },
            ("workflow", true) => policy with { WorkflowConcurrency = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        AiExecutionContext ScopeContext(int index) => AiExecutionContext.Create("deployment",
            scope is "actor" or "cohort" ? "shared-actor" : $"actor-{index}",
            scope == "cohort" || index % 2 == 0 ? "studio-chat" : "reporting-narrative",
            scope == "workflow" ? "shared-workflow" : $"workflow-{index}");
        var first = Store();
        var second = Store();
        await InitializeAndResumeAsync(first, policy);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(async index =>
        {
            await barrier.Task;
            return await (index % 2 == 0 ? first : second).TryAcquireAsync(
                ScopeContext(index), Guid.NewGuid(), CancellationToken.None);
        }).ToArray();
        barrier.SetResult();
        var results = await Task.WhenAll(tasks);
        var winner = results.Should().ContainSingle(result => result.Granted).Subject;
        var code = concurrency ? "concurrency_exceeded" : "budget_exceeded";
        results.Where(result => !result.Granted).Should().OnlyContain(result => result.Code == code);
        (await CountAttemptsAsync()).Should().Be(1, "denied contenders cannot partially debit any scope");
        await second.ReleaseAsync(winner.Reservation!, CancellationToken.None);
        var afterRelease = await first.TryAcquireAsync(ScopeContext(0), Guid.NewGuid(), CancellationToken.None);
        if (concurrency)
        {
            afterRelease.Granted.Should().BeTrue("release restores concurrency capacity");
            (await CountAttemptsAsync()).Should().Be(2);
        }
        else
        {
            afterRelease.Code.Should().Be("budget_exceeded", "release never refunds an invocation attempt");
            (await CountAttemptsAsync()).Should().Be(1);
        }
    }

    [IntegrationTest]
    public async Task ReleaseAndStoreRestart_KeepAttemptChargeAndOtherReservations()
    {
        var store = Store();
        await InitializeAndResumeAsync(store, new AiAdmissionPolicy { ActorAttempts = 2, CohortAttempts = 2, ActorConcurrency = 1, CohortConcurrency = 1 });
        var first = await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None);
        first.Granted.Should().BeTrue();
        var restarted = Store();
        (await restarted.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("concurrency_exceeded");
        await restarted.ReleaseAsync(first.Reservation!, CancellationToken.None);
        await restarted.ReleaseAsync(first.Reservation!, CancellationToken.None);
        var second = await restarted.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None);
        second.Granted.Should().BeTrue();
        await store.ReleaseAsync(first.Reservation!, CancellationToken.None);
        (await restarted.IsCurrentAsync(second.Reservation!, CancellationToken.None)).Should().BeTrue();
        await restarted.ReleaseAsync(second.Reservation!, CancellationToken.None);
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("budget_exceeded");
        (await CountAttemptsAsync()).Should().Be(2);
    }

    [IntegrationTest]
    public async Task Controls_StopEpochPolicyWindowAndReplay_PreserveHistoryAndAudit()
    {
        var store = Store();
        var state = await InitializeAndResumeAsync(store, new AiAdmissionPolicy { ActorAttempts = 1, CohortAttempts = 1 });
        var admitted = await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None);
        var stop = Command(AiAdmissionControlAction.Stop, state);
        var stopped = await store.ApplyControlAsync(stop, CancellationToken.None);
        (await store.IsCurrentAsync(admitted.Reservation!, CancellationToken.None)).Should().BeFalse();
        (await Store().TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("stopped");
        (await store.ApplyControlAsync(stop, CancellationToken.None)).Should().BeEquivalentTo(stopped);
        (await store.ApplyControlAsync(stop with { ActorId = "different-actor" }, CancellationToken.None)).Code.Should().Be("invalid_control");
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, stopped.State!) with { ExpectedGeneration = Guid.NewGuid() }, CancellationToken.None)).Code.Should().Be("stale_control");
        var update = Command(AiAdmissionControlAction.UpdatePolicy, stopped.State!, state.Policy with { WindowSeconds = 300 });
        (await store.ApplyControlAsync(update, CancellationToken.None)).Code.Should().Be("policy_not_drained");
        await store.ReleaseAsync(admitted.Reservation!, CancellationToken.None);
        // The old 60-second policy no longer counts this attempt; the new 300-second window must.
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_attempts SET admitted_at = clock_timestamp() - INTERVAL '120 seconds'");
        var updated = await store.ApplyControlAsync(update, CancellationToken.None);
        updated.Applied.Should().BeTrue();
        var resumed = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, updated.State!), CancellationToken.None);
        resumed.Applied.Should().BeTrue();
        (await Store().TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("budget_exceeded");
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var audit = new NpgsqlCommand($"SELECT actor_id FROM \"{_schema}\".ai_execution_control_events WHERE invocation_id = @invocation", connection);
        audit.Parameters.AddWithValue("invocation", stop.InvocationId);
        (await audit.ExecuteScalarAsync()).Should().Be("test:canonical-admin");
    }

    [IntegrationTest]
    public async Task MissingStateCancellationAndRealConnectionOutage_FailClosedWithoutReset()
    {
        var store = Store();
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("uninitialized");
        var state = await InitializeAndResumeAsync(store, new AiAdmissionPolicy());
        var reservation = (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Reservation!;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var call = () => store.TryAcquireAsync(Context(), Guid.NewGuid(), cancelled.Token);
        await call.Should().ThrowAsync<OperationCanceledException>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var config = new NpgsqlConnectionStringBuilder(fixture.DataSource.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = port,
            Timeout = 1,
            Pooling = false
        };
        listener.Stop();
        await using var unavailableSource = NpgsqlDataSource.Create(config.ConnectionString);
        var unavailable = new PostgresAiExecutionAdmissionStore(new TestConnections(unavailableSource), _schema);
        (await unavailable.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("unavailable");
        (await unavailable.IsCurrentAsync(reservation, CancellationToken.None)).Should().BeFalse();
        (await Store().GetStateAsync("deployment", CancellationToken.None)).Should().BeEquivalentTo(state);
        (await CountAttemptsAsync()).Should().Be(1);
        var initialize = new AiAdmissionControlCommand("deployment", AiAdmissionControlAction.Initialize, Guid.Empty, 0, 0,
            "test:canonical-admin", Guid.NewGuid().ToString("N"), "correlation", new AiAdmissionPolicy());
        (await store.ApplyControlAsync(initialize, CancellationToken.None)).Code.Should().Be("already_initialized");
    }

    [IntegrationTest]
    public async Task AbandonedLeaseExpiryAndDuplicateAttempt_RecoverCapacityWithoutRefund()
    {
        var store = Store();
        await InitializeAndResumeAsync(store, new AiAdmissionPolicy { ActorConcurrency = 1, CohortConcurrency = 1 });
        var id = Guid.NewGuid();
        var first = await store.TryAcquireAsync(Context(), id, CancellationToken.None);
        (await store.TryAcquireAsync(Context(), id, CancellationToken.None)).Code.Should().Be("duplicate_attempt");
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_attempts SET admitted_at = clock_timestamp() - INTERVAL '3 seconds', deadline_at = clock_timestamp() - INTERVAL '2 seconds', lease_expires_at = clock_timestamp() - INTERVAL '1 second'");
        (await Store().IsCurrentAsync(first.Reservation!, CancellationToken.None)).Should().BeFalse();
        (await Store().TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Granted.Should().BeTrue();
        (await CountAttemptsAsync()).Should().Be(2);
    }

    [IntegrationTest]
    public async Task Reservation_ChangedTimestampCannotReadOrReleaseAnotherExactLease()
    {
        var store = Store();
        await InitializeAndResumeAsync(store, new AiAdmissionPolicy { ActorConcurrency = 1, CohortConcurrency = 1 });
        var admitted = await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None);
        var reservation = admitted.Reservation!;
        foreach (var changed in new[] { reservation with { DeadlineUtc = reservation.DeadlineUtc.AddTicks(10) },
                     reservation with { LeaseExpiresUtc = reservation.LeaseExpiresUtc.AddTicks(10) },
                     reservation with { Generation = Guid.NewGuid() } })
        {
            (await store.IsCurrentAsync(changed, CancellationToken.None)).Should().BeFalse();
            await store.ReleaseAsync(changed, CancellationToken.None);
            (await store.IsCurrentAsync(reservation, CancellationToken.None)).Should().BeTrue();
        }
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("concurrency_exceeded");
    }

    [IntegrationTest]
    public async Task InvalidPersistedPolicyOrGeneration_DeniesAdmissionAndExistingPermit()
    {
        var store = Store();
        await InitializeAndResumeAsync(store, new AiAdmissionPolicy());
        var reservation = (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Reservation!;
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_policies SET policy_json = jsonb_set(policy_json, '{{WindowSeconds}}', '0')");
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("invalid_policy");
        (await store.IsCurrentAsync(reservation, CancellationToken.None)).Should().BeFalse();
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_policies SET policy_json = '\"not-a-policy\"'::jsonb");
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("invalid_policy");
        (await store.IsCurrentAsync(reservation, CancellationToken.None)).Should().BeFalse();
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_policies SET policy_json = '{{}}'::jsonb, generation_id = '00000000-0000-0000-0000-000000000000'");
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("invalid_policy");
        (await store.IsCurrentAsync(reservation, CancellationToken.None)).Should().BeFalse();
        (await CountAttemptsAsync()).Should().Be(1);
    }

    [IntegrationTest]
    public async Task RetainedStateCapacity_RejectsRatherThanEvictingSafeHistory_ThenReclaimsExpiredHistory()
    {
        var store = Store();
        var state = await InitializeAndResumeAsync(store, new AiAdmissionPolicy());
        await ExecuteAsync($"""
            INSERT INTO "{_schema}".ai_execution_attempts
                (deployment_id, attempt_id, generation_id, actor_key, use_case, admitted_at, deadline_at, lease_expires_at, released_at, grant_epoch)
            SELECT 'deployment', md5(number::text)::uuid, '{state.Generation:D}', repeat('a', 64), 'studio-chat',
                clock_timestamp() - INTERVAL '120 seconds', clock_timestamp() + INTERVAL '1 second',
                clock_timestamp() + INTERVAL '31 seconds', clock_timestamp(), {state.Epoch}
            FROM generate_series(1, {AiAdmissionPolicy.MaximumLedgerEntries}) number
            """);
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Code.Should().Be("ledger_full");
        (await CountAttemptsAsync()).Should().Be(AiAdmissionPolicy.MaximumLedgerEntries);
        await ExecuteAsync($"""
            UPDATE "{_schema}".ai_execution_attempts SET admitted_at = clock_timestamp() - INTERVAL '3601 seconds',
                deadline_at = clock_timestamp() - INTERVAL '2 seconds', lease_expires_at = clock_timestamp() - INTERVAL '1 second'
            """);
        (await store.TryAcquireAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Granted.Should().BeTrue();
        (await CountAttemptsAsync()).Should().Be(1);
    }

    private PostgresAiExecutionAdmissionStore Store()
    {
        var source = NpgsqlDataSource.Create(fixture.ConnectionString);
        _sources.Add(source);
        return new(new TestConnections(source), _schema);
    }

    private static AiExecutionContext Context(string actor = "actor", string? workflow = null)
        => AiExecutionContext.Create("deployment", actor, "studio-chat", workflow);

    private static AiAdmissionControlCommand Command(AiAdmissionControlAction action, AiAdmissionState state, AiAdmissionPolicy? policy = null)
        => new("deployment", action, state.Generation, state.Revision, state.Epoch, "test:canonical-admin",
            Guid.NewGuid().ToString("N"), "correlation", policy);

    private static async Task<AiAdmissionState> InitializeAndResumeAsync(PostgresAiExecutionAdmissionStore store, AiAdmissionPolicy policy)
    {
        var initialized = await store.ApplyControlAsync(new("deployment", AiAdmissionControlAction.Initialize, Guid.Empty, 0, 0,
            "test:canonical-admin", Guid.NewGuid().ToString("N"), "correlation", policy), CancellationToken.None);
        initialized.Applied.Should().BeTrue();
        var resumed = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, initialized.State!), CancellationToken.None);
        resumed.Applied.Should().BeTrue();
        return resumed.State!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAttemptsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{_schema}\".ai_execution_attempts", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestConnections(NpgsqlDataSource source) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => source.ConnectionString;
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await source.OpenConnectionAsync(cancellationToken);
        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead, CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try { return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken)); }
            catch { await connection.DisposeAsync(); throw; }
        }
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}
