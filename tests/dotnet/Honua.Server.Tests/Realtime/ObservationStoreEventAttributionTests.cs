// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Server.Features.Realtime;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Realtime;

[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
[Operation(Operations.Create)]
public sealed class ObservationStoreEventAttributionTests
{
    [IntegrationTest]
    public async Task CreateEntity_PreservesRequestTenantAndSchemaInCommittedEvents()
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");
        var fixture = new WebAppFixture().ReplaceService(tenant);
        await fixture.InitializeAsync();
        try
        {
            await ApplyCaptureMigrationsAsync(fixture);
            var store = fixture.GetService<ISensorThingsEntityStore>();
            using var feature = JsonDocument.Parse("""
                {"name":"Sampling site","description":"Synthetic test site",
                 "encodingType":"application/vnd.geo+json",
                 "feature":{"type":"Point","coordinates":[0,0]}}
                """);
            var featureId = await store.CreateEntityAsync("FeaturesOfInterest", feature.RootElement, default);
            using var observation = JsonDocument.Parse($$$"""
                {"phenomenonTime":"2026-01-01T00:00:00Z","result":42,
                 "Datastream":{"@iot.id":1},"FeatureOfInterest":{"@iot.id":{{{featureId}}}}}
                """);
            var first = await store.CreateEntityAsync("Observations", observation.RootElement, default);
            tenant.TenantId.Returns("tenant-b");
            var second = await store.CreateEntityAsync("Observations", observation.RootElement, default);

            await using var command = fixture.Postgres.DataSource.CreateCommand($"""
                SELECT payload->'scope'->>'tenantId', payload->'scope'->>'schema'
                FROM "{fixture.CurrentSchema}".realtime_events
                WHERE family='observations' AND EXISTS (
                    SELECT 1 FROM jsonb_array_elements(payload->'observations') AS observation
                    WHERE (observation->>'id')::bigint = ANY(@ids))
                ORDER BY seq
                """);
            command.Parameters.AddWithValue("ids", new[] { first, second });
            await using var reader = await command.ExecuteReaderAsync();
            var scopes = new List<(string Tenant, string Schema)>();
            while (await reader.ReadAsync()) scopes.Add((reader.GetString(0), reader.GetString(1)));
            scopes.Should().Equal(("tenant-a", fixture.CurrentSchema!), ("tenant-b", fixture.CurrentSchema!));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task ApplyCaptureMigrationsAsync(WebAppFixture fixture)
    {
        foreach (var name in new[] { "158_CreateRealtimeEventLog.sql", "171_CaptureObservationRealtimeEvents.sql" })
        {
            await using var stream = typeof(PostgresRealtimeEventLog).Assembly
                .GetManifestResourceStream($"Honua.Server.Migrations.{name}")
                ?? throw new InvalidOperationException($"Missing migration {name}");
            using var reader = new StreamReader(stream);
            var sql = (await reader.ReadToEndAsync())
                .Replace("$HonuaSchema$", $"\"{fixture.CurrentSchema}\"", StringComparison.Ordinal);
            await fixture.Postgres.ExecuteDdlUnderLockAsync(sql, fixture.CurrentSchema);
        }
    }

}
