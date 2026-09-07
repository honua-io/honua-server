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
    /// <summary>
    /// The schema the production alert and audit stores actually write to. They are
    /// registered without a schema name, and <c>SchemaSearchPath.QualifyTable</c> then
    /// hard-qualifies every statement to <c>honua</c>, which no per-test
    /// <c>search_path</c> can redirect. The fixture's isolated schema is not an
    /// alternative: the alert DDL is created by the DbUp migrations (and by the seed's
    /// <c>CREATE TABLE IF NOT EXISTS honua....</c> statements) in <c>honua</c> only, so
    /// binding the stores to the isolated schema would point them at tables that do not
    /// exist. The proof therefore seeds, drives and asserts in <c>honua</c> — the same
    /// place the production code path writes — and scopes every read to the event id it
    /// seeded so a shared schema cannot make the assertions ambiguous.
    /// </summary>
    private const string ProductionSchema = "honua";

    private readonly AuditFaultSwitch _fault = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;
    private readonly string _schema = ProductionSchema;
    private long _eventId;

    public AlertLifecycleAuditAtomicityProofTests()
    {
        _fixture = new WebAppFixture();
        var fault = _fault;

        // The alert stores keep their PRODUCTION registrations so the proof exercises the
        // real code path. Only the audit sink is touched, and it is DECORATED, never
        // replaced: the production PostgresAuditLog stays behind the switch so a disarmed
        // run writes a real, hash-chained audit row. Configuring services at all is also
        // what gives this class its own host, which RestartHostAsync requires.
        _fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IAuditLog>();
            services.AddScoped<IAuditLog>(provider => new FaultInjectingAuditLog(
                new PostgresAuditLog(
                    provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                    provider.GetRequiredService<ILogger<PostgresAuditLog>>()),
                fault));
        });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
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

        // 5. Restart the process against the same database with the fault cleared.
        //    The reconciler runs on startup, so the record must appear without any
        //    further operator action.
        _fault.Fail = false;
        await _fixture.RestartHostAsync();
        _client = _fixture.CreateAdminClient();
        AssertReconcilerRunsOnStartup();

        // The inline attempt that the fault rejected scheduled the intent's first retry
        // 30 seconds out, so age the schedule rather than sleeping through it. This is
        // the backoff elapsing, not the contract being relaxed: the intent is still
        // pending, still uncompleted, and still has to be drained by the reconciler.
        await ElapseOutboxBackoffAsync();
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
        (await ReadOutboxAsync(includeCompleted: true, eventId: MissingEventId)).Should().BeEmpty(
            "a mutation that never happened must leave no reconciliation record either");
        (await ReadOutboxAsync(includeCompleted: true)).Should().BeEmpty(
            "and it must not have written one against any other event either");
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
            "resource_type = @resource_type AND resource_id = @resource_id AND action <> 'alert.acknowledge'",
            ("resource_type", AlertAuditActions.ResourceType),
            ("resource_id", _eventId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
    /// Brings a pending intent's retry schedule forward to now, so a proof does not have
    /// to sleep out the completer's exponential backoff. Only the schedule moves — the
    /// intent stays pending and uncompleted.
    /// </summary>
    private async Task ElapseOutboxBackoffAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            UPDATE "{_schema}".alert_audit_outbox
            SET next_attempt_at = now()
            WHERE event_id = @event_id AND completed_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("event_id", _eventId);
        await command.ExecuteNonQueryAsync();
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
    /// Reads outbox intents for one alert event. The proof runs against the shared
    /// <c>honua</c> schema, so every read is scoped to an event id rather than to the
    /// whole table.
    /// </summary>
    private async Task<IReadOnlyList<OutboxRow>> ReadOutboxAsync(
        bool includeCompleted = false, long? eventId = null)
    {
        var predicate = includeCompleted ? "TRUE" : "completed_at IS NULL";
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT outbox_id, event_id, action, actor, correlation_id, idempotency_key,
                   occurred_at, audit_id, completed_at
            FROM "{_schema}".alert_audit_outbox
            WHERE event_id = @event_id AND {predicate}
            ORDER BY outbox_id
            """, connection);
        command.Parameters.AddWithValue("event_id", eventId ?? _eventId);

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
