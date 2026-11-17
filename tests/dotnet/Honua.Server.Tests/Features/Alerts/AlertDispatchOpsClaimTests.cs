// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Alerts;

// With the Preview geofence pipeline off, the dispatcher claims ops-source
// rows only, so a rule-sourced row left from an earlier opt-in is never delivered.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class AlertDispatchOpsClaimTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    public async Task ClaimPendingOpsAsync_SkipsRuleSourcedRows_ClaimPendingAsyncTakesBoth()
    {
        var store = _fixture.GetService<IAlertDispatchStore>();
        var opsEventId = await SeedDispatchAsync(AlertEventSources.Ops, ruleId: 0);
        var ruleEventId = await SeedDispatchAsync(AlertEventSources.GisRule, await SeedRuleAsync());

        var opsClaims = await store.ClaimPendingOpsAsync(5_000, DateTimeOffset.UtcNow.AddMinutes(1));

        opsClaims.Should().Contain(item => item.EventId == opsEventId);
        opsClaims.Should().NotContain(item => item.EventId == ruleEventId,
            "an ops-only claim must not deliver geofence alert traffic");

        var allClaims = await store.ClaimPendingAsync(5_000, DateTimeOffset.UtcNow.AddMinutes(1));

        allClaims.Should().Contain(item => item.EventId == ruleEventId,
            "the full claim still takes rule-sourced rows when the geofence pipeline is on");
    }

    private async Task<long> SeedRuleAsync()
    {
        await using var connection = await _fixture.GetService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO honua.alert_rules
                (service_id, layer_id, rule_name, trigger_type, conditions,
                 severity, edition_required, channels, is_active)
            VALUES
                ('alert-dispatch-ops-claim-tests', 7, 'speed threshold', 4,
                 '{"field":"speed","operator":">","value":50}'::jsonb,
                 'warning', 1, ARRAY['webhook'], TRUE)
            RETURNING rule_id;
            """, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> SeedDispatchAsync(string source, long ruleId)
    {
        var eventStore = _fixture.GetService<IAlertEventStore>();
        var eventId = await eventStore.TryAppendAsync(new AlertEventEnvelope
        {
            DedupeKey = $"{source}:ops-claim-test:{Guid.NewGuid():N}",
            RuleId = ruleId,
            ServiceId = "alert-dispatch-ops-claim-tests",
            LayerId = 0,
            ObjectId = 0,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 0,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{\"test\":\"ops-claim\"}",
            IncidentStatus = AlertIncidentStatus.Started,
            Source = source,
        });

        eventId.Should().NotBeNull();
        await _fixture.GetService<IAlertDispatchStore>()
            .EnqueueAsync(eventId!.Value, ImmutableArray.Create(AlertChannelType.Webhook));
        return eventId.Value;
    }
}
