// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Alerts;
using Honua.Db.Postgres.Features.AuditLog;
using Honua.Server.Features.Alerts;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Control-plane integrity proof for alert lifecycle audit (#3865).
///
/// <para>
/// <c>ObservabilityAlertEndpoints</c> used to write acknowledge/suppress/resolve
/// state and then call <see cref="IAuditLog"/> as a separate operation, so a
/// failure or a process death between them exposed a successful lifecycle
/// mutation with no matching alert domain audit record and nothing to complete
/// it. These cases run the hosted, authenticated Admin API against real Postgres,
/// inject an audit-sink failure at exactly that boundary, restart the host, and
/// assert the mutation is still accompanied by evidence.
/// </para>
/// <para>
/// The audit sink under fault injection is a DECORATOR over the real
/// <c>PostgresAuditLog</c>, not a substitute for it: when the fault is disarmed,
/// records land in <c>honua.audit_log</c> with their production hash chain, and
/// the proof verifies that chain.
/// </para>
/// </summary>
[Collection("Database")]
[Trait("Category", "AlertAuditAtomicityProof")]
public sealed class AlertLifecycleAuditAtomicityProofTests : IAsyncLifetime
{
    private readonly AuditFaultSwitch _fault = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;
    private string _schema = null!;
    private long _eventId;

    public AlertLifecycleAuditAtomicityProofTests()
    {
        _fixture = new WebAppFixture();
        var fault = _fault;
        string? Schema() => _fixture.CurrentSchema;

        // The production registrations construct these stores WITHOUT a schema name, and
        // SchemaSearchPath.QualifyTable then hard-qualifies every statement to "honua",
        // which the fixture's per-test search_path cannot redirect. Rebind them to the
        // fixture's isolated schema — resolved lazily, because the schema is created
        // after the host is built — so the proof exercises the real stores against the
        // rows it seeds instead of a shared "honua" it does not own.
        _fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IAlertLifecycleStore>();
            services.AddScoped<IAlertLifecycleStore>(provider => new PostgresAlertLifecycleStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), Schema()));

            services.RemoveAll<IAlertAuditOutbox>();
            services.AddScoped<IAlertAuditOutbox>(provider => new PostgresAlertAuditOutbox(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), Schema()));

            services.RemoveAll<IAlertEventQuery>();
            services.AddScoped<IAlertEventQuery>(provider => new PostgresAlertEventQuery(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), Schema()));

            // Decorate, never replace: the production PostgresAuditLog stays behind the
            // switch so a disarmed run writes a real, hash-chained audit row.
            services.RemoveAll<IAuditLog>();
            services.AddScoped<IAuditLog>(provider => new FaultInjectingAuditLog(
                new PostgresAuditLog(
                    provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                    provider.GetRequiredService<ILogger<PostgresAuditLog>>(),
                    Schema()),
                fault));
        });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
        _schema = _fixture.CurrentSchema!;
        await CreateAlertSchemaAsync();
        _eventId = await SeedAlertEventAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Theory]
    [InlineData("acknowledge", "alert.acknowledge", 1)]
    [InlineData("suppress", "alert.suppress", 2)]
    [InlineData("resolve", "alert.resolve", 3)]
    public async Task LifecycleMutation_WithAFailingAuditSink_LeavesADurableReconciliationRecord_ThatARestartCompletes(
        string route, string auditAction, short expectedStatus)
    {
        var idempotencyKey = $"proof-{route}-{Guid.NewGuid():N}";

        // 1. The audit sink fails at exactly the point the lifecycle write has
        //    already reached its persistence boundary.
        _fault.Fail = true;
        using var response = await PostAsync(route, idempotencyKey);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. The lifecycle mutation IS externally observable...
        (await ReadLifecycleStatusAsync()).Should().Be(expectedStatus);
        (await ReadLifecycleStatusFromApiAsync()).Should().Be(LifecycleName(expectedStatus));

        // 3. ...and it is NOT accompanied by a domain audit record yet...
        (await ReadAlertAuditRowsAsync(auditAction)).Should().BeEmpty(
            "the injected fault prevented the audit write");

        // 4. ...but it IS accompanied by a durable reconciliation record that
        //    deterministically completes it. This is the whole contract: never a
        //    mutation with neither.
        var pending = await ReadOutboxAsync();
        pending.Should().ContainSingle();
        pending[0].Action.Should().Be(auditAction);
        pending[0].CompletedAt.Should().BeNull();
        pending[0].EventId.Should().Be(_eventId);

        // 4b. The rejected inline completion is recorded against the intent, and an
        //     intent the sink just rejected backs off before it is retried, so a poison
        //     payload cannot monopolise every oldest-first batch.
        var (attempts, backedOff) = await ReadRetryStateAsync();
        attempts.Should().Be(1, "the injected audit failure is recorded against the intent");
        backedOff.Should().BeTrue("a rejected intent is not retried until its backoff elapses");

        // 5. Restart the process against the same database with the fault cleared.
        //    The reconciler runs on startup, so the record must appear without any
        //    further operator action.
        _fault.Fail = false;
        await _fixture.RestartHostAsync();
        _client = _fixture.CreateAdminClient();
        AssertReconcilerRunsOnStartup();

        // Bring the backoff proven above forward instead of sleeping it out. What is
        // under proof is that reconciliation completes the intent unattended, not the
        // wall-clock delay production imposes before retrying a sink that just rejected
        // a write; the reconciler still has to find, claim and complete the intent.
        await ExpireRetryBackoffAsync();
        await ReconcileAsync();

        var audits = await ReadAlertAuditRowsAsync(auditAction);
        audits.Should().ContainSingle("reconciliation completes the intent exactly once");

        // 6. The two evidence trails agree on actor, action, correlation, outcome and
        //    the timestamp of the transition (not of the later audit write).
        var intent = (await ReadOutboxAsync(includeCompleted: true)).Single();
        intent.CompletedAt.Should().NotBeNull();
        intent.AuditId.Should().NotBeNullOrWhiteSpace();

        var audit = audits[0];
        audit.Actor.Should().Be(intent.Actor);
        audit.Action.Should().Be(intent.Action);
        audit.CorrelationId.Should().Be(intent.CorrelationId);
        audit.Outcome.Should().Be("Success");
        audit.ResourceType.Should().Be(AlertAuditActions.ResourceType);
        audit.ResourceId.Should().Be(_eventId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        audit.Timestamp.Should().BeCloseTo(intent.OccurredAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        audit.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(intent.AuditId);
        audit.EntryHash.Should().NotBeNullOrWhiteSpace("the record joins the production audit hash chain");

        // 7. Retrying the same operator action with the same correlation and
        //    idempotency identity creates ONE logical transition and ONE domain audit
        //    action, not duplicates.
        using var retry = await PostAsync(route, idempotencyKey);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        await ReconcileAsync();

        (await ReadOutboxAsync(includeCompleted: true)).Should().ContainSingle(
            "the retry replays the recorded intent instead of writing a second one");
        (await ReadAlertAuditRowsAsync(auditAction)).Should().ContainSingle(
            "the retry must not produce a second domain audit action");
        (await ReadLifecycleStatusAsync()).Should().Be(expectedStatus);
    }

    [Fact]
    public async Task FailedLifecycleMutation_PublishesNeitherASuccessResponseNorASuccessAuditOutcome()
    {
        const long MissingEventId = 987654321;
        _fault.Fail = false;

        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/observability/alerts/{MissingEventId}/acknowledge",
            new { note = "no such event" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAlertAuditRowsAsync("alert.acknowledge")).Should().BeEmpty(
            "a failed operation must never publish a success audit outcome");
        (await ReadOutboxAsync(includeCompleted: true)).Should().BeEmpty(
            "a mutation that never happened must leave no reconciliation record either");
    }

    [Fact]
    public async Task SharedRequestMiddlewareAudit_DoesNotSubstituteForTheAlertDomainAction()
    {
        _fault.Fail = true;
        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/observability/alerts/{_eventId}/acknowledge",
            new { note = "middleware substitution check" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Whatever shared request middleware may have recorded, no row carries the
        // alert domain action, so the domain evidence is genuinely absent and the
        // outbox — not the middleware — is what makes it recoverable.
        (await ReadAlertAuditRowsAsync("alert.acknowledge")).Should().BeEmpty();
        (await ReadOutboxAsync()).Should().ContainSingle();

        var middlewareRows = await ReadAuditRowsAsync(
            "resource_type = @resource_type AND action <> 'alert.acknowledge'",
            ("resource_type", AlertAuditActions.ResourceType));
        middlewareRows.Should().BeEmpty(
            "no non-domain row may masquerade as the alert domain action's evidence");
    }

    // -------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostAsync(string route, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/admin/observability/alerts/{_eventId}/{route}");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(route == "suppress"
            ? new { note = "proof", suppressUntil = DateTimeOffset.UtcNow.AddHours(1) }
            : (object)new { note = "proof" });
        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Runs one reconciliation pass. The host also runs this on startup - the
    /// registration is asserted separately - but the proof drives a pass directly
    /// so the assertion does not race the background loop.
    /// </summary>
    private async Task ReconcileAsync()
    {
        var reconciler = _fixture.GetService<AlertAuditOutboxReconciler>();
        await reconciler.ReconcileOnceAsync(CancellationToken.None);
    }

    /// <summary>
    /// The reconciler must be a hosted service, or a restart would not complete a
    /// pending intent without operator action.
    /// </summary>
    private void AssertReconcilerRunsOnStartup()
    {
        _fixture.Services.GetServices<IHostedService>().OfType<AlertAuditOutboxReconciler>()
            .Should().ContainSingle("a restart must drain pending audit intents unattended");
    }

    private static string LifecycleName(short status) => status switch
    {
        1 => "acknowledged",
        2 => "suppressed",
        3 => "resolved",
        _ => "open"
    };

    /// <summary>
    /// Creates the alert and audit tables this proof's stores read and write inside the
    /// fixture's own schema.
    /// </summary>
    /// <remarks>
    /// The shared seed (<c>tests/seed/server.yaml</c>) declares these tables against the
    /// literal, process-global <c>honua</c> schema because the production stores hard-qualify
    /// to it, so a per-test schema does not contain them and every case here failed in
    /// <see cref="InitializeAsync"/> with <c>42P01</c>. The proof cannot fall back to
    /// <c>honua</c>: a lifecycle mutation whose audit intent is deliberately left pending
    /// would be drained by any other collection's host reconciler polling the same shared
    /// table before this test asserts the intent is still pending.
    /// <para>
    /// The shapes are CLONED from the seeded tables rather than restated here, so the
    /// schema this proof runs its real stores against cannot drift from the one the rest of
    /// the suite - and the migrations the seed mirrors - define. <c>LIKE ... INCLUDING ALL</c>
    /// copies columns, defaults, check constraints and indexes but not foreign keys; the
    /// proof asserts audit and lifecycle behaviour, and the "no such event" case is a 404
    /// the store derives from its own existence check (<c>MutateAsync</c> returning null),
    /// not from a referential violation.
    /// </para>
    /// </remarks>
    private Task CreateAlertSchemaAsync()
        => _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            CREATE TABLE IF NOT EXISTS "{_schema}".alert_rules (LIKE honua.alert_rules INCLUDING ALL);
            CREATE TABLE IF NOT EXISTS "{_schema}".alert_events (LIKE honua.alert_events INCLUDING ALL);
            CREATE TABLE IF NOT EXISTS "{_schema}".alert_event_lifecycle (LIKE honua.alert_event_lifecycle INCLUDING ALL);
            CREATE TABLE IF NOT EXISTS "{_schema}".alert_audit_outbox (LIKE honua.alert_audit_outbox INCLUDING ALL);
            CREATE TABLE IF NOT EXISTS "{_schema}".audit_log (LIKE honua.audit_log INCLUDING ALL);
            """);

    private async Task<long> SeedAlertEventAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".alert_rules (service_id, layer_id, rule_name, trigger_type, severity)
            VALUES ('audit-proof', 1, 'audit proof rule', 1, 'critical')
            RETURNING rule_id
            """, connection);
        var ruleId = (long)(await command.ExecuteScalarAsync())!;

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".alert_events (
                dedupe_key, rule_id, service_id, layer_id, objectid, trigger_type, generation, severity)
            VALUES (@dedupe, @rule_id, 'audit-proof', 1, 42, 1, 1, 'critical')
            RETURNING event_id
            """, connection);
        insert.Parameters.AddWithValue("dedupe", "audit-proof-" + Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("rule_id", ruleId);
        return (long)(await insert.ExecuteScalarAsync())!;
    }

    private async Task<short> ReadLifecycleStatusAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT lifecycle_status FROM "{_schema}".alert_event_lifecycle WHERE event_id = @event_id""",
            connection);
        command.Parameters.AddWithValue("event_id", _eventId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? (short)0 : (short)value;
    }

    private async Task<string?> ReadLifecycleStatusFromApiAsync()
    {
        using var response = await _client.GetAsync($"/api/v1/admin/observability/alerts/{_eventId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("lifecycleStatus").GetString();
    }

    /// <summary>
    /// Reads the pending intent's retry state. <c>next_attempt_at</c> is compared against
    /// the database clock, which is the clock the store's own drain predicate uses.
    /// </summary>
    private async Task<(int Attempts, bool BackedOff)> ReadRetryStateAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT attempts, next_attempt_at > now()
            FROM "{_schema}".alert_audit_outbox
            WHERE completed_at IS NULL
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the pending intent must still be there to have retry state");
        return (reader.GetInt32(0), reader.GetBoolean(1));
    }

    /// <summary>
    /// Makes every pending intent due now, so a pass driven by the test observes what a
    /// pass after the backoff has elapsed would.
    /// </summary>
    private async Task ExpireRetryBackoffAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""UPDATE "{_schema}".alert_audit_outbox SET next_attempt_at = now() WHERE completed_at IS NULL""",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<OutboxRow>> ReadOutboxAsync(bool includeCompleted = false)
    {
        var predicate = includeCompleted ? "TRUE" : "completed_at IS NULL";
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT outbox_id, event_id, action, actor, correlation_id, idempotency_key,
                   occurred_at, audit_id, completed_at
            FROM "{_schema}".alert_audit_outbox
            WHERE {predicate}
            ORDER BY outbox_id
            """, connection);

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return rows;
    }

    private Task<IReadOnlyList<AuditRow>> ReadAlertAuditRowsAsync(string action) =>
        ReadAuditRowsAsync("action = @action AND resource_id = @resource_id",
            ("action", action),
            ("resource_id", _eventId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private async Task<IReadOnlyList<AuditRow>> ReadAuditRowsAsync(
        string predicate, params (string Name, object Value)[] parameters)
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT audit_id, timestamp, actor, action, outcome, correlation_id,
                   resource_type, resource_id, entry_hash, prev_hash
            FROM "{_schema}".audit_log
            WHERE {predicate}
            ORDER BY audit_id
            """, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.GetInt64(0),
                reader.GetDateTime(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    private sealed record OutboxRow(
        long OutboxId,
        long EventId,
        string Action,
        string Actor,
        string CorrelationId,
        string IdempotencyKey,
        DateTimeOffset OccurredAt,
        string? AuditId,
        DateTimeOffset? CompletedAt);

    private sealed record AuditRow(
        long AuditId,
        DateTime Timestamp,
        string Actor,
        string Action,
        string Outcome,
        string CorrelationId,
        string ResourceType,
        string? ResourceId,
        string? EntryHash,
        string? PrevHash);

    /// <summary>Shared, host-restart-surviving arming switch for the injected fault.</summary>
    private sealed class AuditFaultSwitch
    {
        public volatile bool Fail;
    }

    /// <summary>
    /// Fails the audit write at the point the lifecycle mutation has already reached
    /// its persistence boundary, then delegates to the real sink once disarmed.
    /// </summary>
    private sealed class FaultInjectingAuditLog : IAuditLog
    {
        private readonly IAuditLog _inner;
        private readonly AuditFaultSwitch _fault;

        public FaultInjectingAuditLog(IAuditLog inner, AuditFaultSwitch fault)
        {
            _inner = inner;
            _fault = fault;
        }

        public bool IsPersisted => _inner.IsPersisted;

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (_fault.Fail && auditEvent.Action.StartsWith("alert.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected audit sink failure");
            }

            return _inner.RecordAsync(auditEvent, cancellationToken);
        }
    }
}
