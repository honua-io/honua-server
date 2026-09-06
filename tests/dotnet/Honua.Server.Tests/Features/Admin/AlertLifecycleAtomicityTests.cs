// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Hosted, authenticated PostgreSQL proofs of the alert lifecycle/audit commit boundary.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AlertLifecycleAtomicityTests(ITestOutputHelper output)
{
    [IntegrationTheory]
    [InlineData("acknowledge", 1, false)]
    [InlineData("suppress", 2, false)]
    [InlineData("resolve", 3, false)]
    [InlineData("acknowledge", 1, true)]
    [InlineData("suppress", 2, true)]
    [InlineData("resolve", 3, true)]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/acknowledge")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/suppress")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/resolve")]
    public async Task Mutation_AuditFailureOrConnectionDeath_RollsBackAndRetriesOnceAfterHostRestart(
        string action, short expectedStatus, bool terminateConnection)
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
        });
        await fixture.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var correlation = $"alert-proof-{suffix}";
        var function = $"audit_fault_{suffix}";
        var sequence = $"audit_boundary_{suffix}";
        var eventId = (await fixture.GetService<IAlertEventStore>().TryAppendAsync(new AlertEventEnvelope
        {
            DedupeKey = $"proof-3865-{suffix}", RuleId = 0, ServiceId = "lifecycle-proof",
            LayerId = 0, ObjectId = 3865, TriggerType = AlertTriggerType.Threshold,
            Generation = 1, Severity = AlertSeverity.Warning, OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{\"independentFixtureValue\":3865}",
            IncidentStatus = AlertIncidentStatus.Started, Source = AlertEventSources.Ops
        }))!.Value;
        var note = "Reviewed valve 3865: operator note with \"quotes\" and Unicode Ω";
        var until = DateTimeOffset.UtcNow.AddHours(2);
        until = new DateTimeOffset(until.UtcTicks / 10 * 10, TimeSpan.Zero);
        var actionName = $"alert.{action}";
        var target = $"/api/v1/admin/observability/alerts/{eventId}/{action}";

        try
        {
            // A sequence is nontransactional: its value independently proves the fault
            // ran AFTER the lifecycle row existed, even though that row is rolled back.
            await Sql($"""
                CREATE SEQUENCE honua.{sequence};
                CREATE FUNCTION honua.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                  IF NEW.action = '{actionName}' AND NEW.resource_id = '{eventId.ToString(CultureInfo.InvariantCulture)}' THEN
                    IF NOT EXISTS (SELECT 1 FROM honua.alert_event_lifecycle
                                   WHERE event_id = {eventId} AND lifecycle_status = {expectedStatus}) THEN
                      RAISE EXCEPTION 'proof did not reach the post-lifecycle boundary';
                    END IF;
                    PERFORM nextval('honua.{sequence}');
                    {(terminateConnection ? "PERFORM pg_terminate_backend(pg_backend_pid());" : "RAISE EXCEPTION 'injected domain audit persistence failure';")}
                  END IF;
                  RETURN NEW;
                END $body$;
                CREATE TRIGGER {function} BEFORE INSERT ON honua.audit_log
                FOR EACH ROW EXECUTE FUNCTION honua.{function}();
                """);

            using (var failedClient = fixture.CreateAdminClient())
            using (var failed = await Send(failedClient, note))
            {
                failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                output.WriteLine($"fault HTTP {(int)failed.StatusCode}: {await failed.Content.ReadAsStringAsync()}");
            }
            (await Scalar($"SELECT last_value FROM honua.{sequence} WHERE is_called")).Should().Be(1);
            (await Scalar($"SELECT count(*) FROM honua.alert_event_lifecycle WHERE event_id = {eventId}")).Should().Be(0);
            (await AuditCount()).Should().Be(0, "a failed mutation must have no successful domain audit");

            await Sql($"DROP TRIGGER {function} ON honua.audit_log; DROP FUNCTION honua.{function}();");
            await fixture.RestartHostAsync();
            (await Scalar($"SELECT count(*) FROM honua.alert_event_lifecycle WHERE event_id = {eventId}")).Should().Be(0);
            using var client = fixture.CreateAdminClient();
            using (var success = await Send(client, note))
            {
                success.StatusCode.Should().Be(HttpStatusCode.OK);
                var raw = await success.Content.ReadAsStringAsync();
                output.WriteLine($"retry HTTP 200: {raw}");
                using var json = JsonDocument.Parse(raw);
                json.RootElement.GetProperty("eventId").GetInt64().Should().Be(eventId);
                json.RootElement.GetProperty("lifecycleStatus").GetString().Should().Be(action switch
                {
                    "acknowledge" => "acknowledged", "suppress" => "suppressed", _ => "resolved"
                });
            }
            var committed = await ReadAndAssertCommitted();
            using (var duplicate = await Send(client, note)) { duplicate.StatusCode.Should().Be(HttpStatusCode.OK); }
            (await ReadAndAssertCommitted()).Should().Be(committed, "retry cannot rewrite timestamp or insert another audit");
            using (var conflict = await Send(client, "different note")) { conflict.StatusCode.Should().Be(HttpStatusCode.Conflict); }
            (await ReadAndAssertCommitted()).Should().Be(committed);
            var integrity = await fixture.GetService<IAuditLogIntegrityVerifier>().VerifyAsync();
            integrity.Verified.Should().BeTrue(integrity.FailureReason);
            integrity.UnhashedRows.Should().Be(0);
            output.WriteLine($"SQL lifecycle/audit agreed; event={eventId}; action={actionName}; correlation={correlation}; timestamp={committed:O}; chain rows={integrity.RowsChecked}");
        }
        finally
        {
            await Sql($"DROP TRIGGER IF EXISTS {function} ON honua.audit_log; DROP FUNCTION IF EXISTS honua.{function}(); DROP SEQUENCE IF EXISTS honua.{sequence};");
            await fixture.DisposeAsync();
        }

        async Task<HttpResponseMessage> Send(HttpClient client, string operationNote)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("X-Correlation-ID", correlation);
            request.Content = action == "suppress"
                ? JsonContent.Create(new { note = operationNote, suppressUntil = until })
                : JsonContent.Create(new { note = operationNote });
            return await client.SendAsync(request);
        }

        async Task Sql(string sql)
        {
            await using var connection = await fixture.Postgres.GetConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        async Task<long> Scalar(string sql)
        {
            await using var connection = await fixture.Postgres.GetConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        Task<long> AuditCount() => Scalar($"SELECT count(*) FROM honua.audit_log WHERE resource_type = 'alert_event' AND resource_id = '{eventId}' AND action = '{actionName}'");

        async Task<DateTimeOffset> ReadAndAssertCommitted()
        {
            (await AuditCount()).Should().Be(1);
            await using var connection = await fixture.Postgres.GetConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT l.lifecycle_status, l.note, l.updated_at, l.acknowledged_at, l.acknowledged_by,
                  l.suppressed_until, l.suppressed_by, l.resolved_at, l.resolved_by,
                  a.timestamp, a.actor, a.correlation_id, a.action, a.outcome, a.details
                FROM honua.alert_event_lifecycle l JOIN honua.audit_log a
                  ON a.resource_type = 'alert_event' AND a.resource_id = l.event_id::text
                WHERE l.event_id = {eventId} AND a.action = '{actionName}'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt16(0).Should().Be(expectedStatus);
            reader.GetString(1).Should().Be(note);
            var timestamp = reader.GetFieldValue<DateTimeOffset>(2);
            reader.GetFieldValue<DateTimeOffset>(9).Should().Be(timestamp);
            reader.GetString(10).Should().Be(WebAppFixture.SharedAdminActorId);
            reader.GetString(11).Should().Be(correlation);
            reader.GetString(12).Should().Be(actionName);
            reader.GetString(13).Should().Be("Success");
            using var details = JsonDocument.Parse(reader.GetString(14));
            details.RootElement.GetProperty("note").GetString().Should().Be(note);
            foreach (var ordinal in new[] { 3, 4, 5, 6, 7, 8 })
            {
                var active = action == "acknowledge" ? ordinal is 3 or 4 : action == "suppress" ? ordinal is 5 or 6 : ordinal is 7 or 8;
                reader.IsDBNull(ordinal).Should().Be(!active);
            }
            reader.GetString(action == "acknowledge" ? 4 : action == "suppress" ? 6 : 8).Should().Be(WebAppFixture.SharedAdminActorId);
            reader.GetFieldValue<DateTimeOffset>(action == "acknowledge" ? 3 : action == "suppress" ? 5 : 7)
                .Should().Be(action == "suppress" ? until : timestamp);
            if (action == "suppress") { details.RootElement.GetProperty("suppressUntil").GetDateTimeOffset().Should().Be(until); }
            else { details.RootElement.GetProperty("suppressUntil").ValueKind.Should().Be(JsonValueKind.Null); }
            (await reader.ReadAsync()).Should().BeFalse();
            return timestamp;
        }
    }
}
