// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Alerts;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// A below/above/below burst committed before evaluation must keep one event per
/// transition, using the feature image that generation committed (#3864).
/// </summary>
[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.ContractTesting)]
[Collection("Database.Alerts")]
public sealed class AlertChangeGenerationEvaluationTests(DatabaseFixtureAdapter database)
{
    private const string ServiceId = "alert-burst-3864";
    private const int ThresholdLayerId = 386410;
    private const int GeofenceLayerId = 386411;
    private const long ThresholdRuleId = 38641001;
    private const long EnterRuleId = 38641101;
    private const long ExitRuleId = 38641102;

    [IntegrationTest(Timeout = 180000)]
    public async Task OrderedBursts_UseTheCommittedGeneration_AcrossBatchAndRestart()
    {
        var migrationSchema = await database.CreateIsolatedSchemaAsync(nameof(AlertChangeGenerationEvaluationTests));
        var migration = await database.RunEmbeddedMigrationsUnderLockAsync(
            migrationSchema,
            Assembly.GetAssembly(typeof(Program))!);
        migration.Successful.Should().BeTrue(migration.Error?.ToString());
        await database.DropSchemaAsync(migrationSchema);

        var features = await FindFeatureTableAsync();
        await CleanupAsync(features);
        try
        {
            var zoneId = await InsertZoneAsync();
            await InsertRulesAsync(zoneId);
            var provider = new TestConnectionProvider(database.DataSource);
            var reader = new PostgresAlertChangeReader(provider);
            var rules = new PostgresAlertRuleRepository(provider, NullLogger<PostgresAlertRuleRepository>.Instance);
            var states = new PostgresAlertStateStore(provider, NullLogger<PostgresAlertStateStore>.Instance);
            var checkpoints = new PostgresAlertCheckpointStore(provider);
            var writer = new AlertDispatchWriter(
                new PostgresAlertOutboxWriter(provider),
                TestTelemetry.CreateAlertPipelineMetrics(),
                NullLogger<AlertDispatchWriter>.Instance);
            var live = Substitute.For<IFeatureReader>();
            live.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("value", -1L)));
            var pipeline = CreatePipeline(reader, rules, states, live, writer);

            var fullThreshold = await EvaluateBurstAsync(features, pipeline, checkpoints, live, ThresholdLayerId, "threshold-full", 100, restartEachGeneration: false, ThresholdPoints());
            var batchThreshold = await EvaluateBurstAsync(features, pipeline, checkpoints, live, ThresholdLayerId, "threshold-batch", 1, restartEachGeneration: false, ThresholdPoints());
            var restartThreshold = await EvaluateBurstAsync(features, pipeline, checkpoints, live, ThresholdLayerId, "threshold-restart", 1, restartEachGeneration: true, ThresholdPoints());

            foreach (var run in new[] { fullThreshold, batchThreshold, restartThreshold })
            {
                run.Events.Select(static row => (row.Transition, row.IncidentStatus, row.Value)).Should().Equal(
                    ("threshold", "Started", 60L),
                    ("threshold_resolved", "Ended", 10L));
                run.Events[0].GeometryHex.Should().BeEquivalentTo(run.Images[1].GeometryHex);
                run.Events[1].GeometryHex.Should().BeEquivalentTo(run.Images[2].GeometryHex);
                run.Events[0].GeometryHex.Should().NotBeEquivalentTo(run.Events[1].GeometryHex);
                run.StateGeneration.Should().Be(run.Images[^1].Generation);
                run.CheckpointGeneration.Should().Be(run.Images[^1].Generation);
            }

            var fullGeofence = await EvaluateBurstAsync(features, pipeline, checkpoints, live, GeofenceLayerId, "geofence-full", 100, restartEachGeneration: false, GeofencePoints());
            var batchGeofence = await EvaluateBurstAsync(features, pipeline, checkpoints, live, GeofenceLayerId, "geofence-batch", 1, restartEachGeneration: false, GeofencePoints());
            var restartGeofence = await EvaluateBurstAsync(features, pipeline, checkpoints, live, GeofenceLayerId, "geofence-restart", 1, restartEachGeneration: true, GeofencePoints());

            foreach (var run in new[] { fullGeofence, batchGeofence, restartGeofence })
            {
                run.Events.Select(static row => (row.Transition, row.IncidentStatus)).Should().Equal(
                    ("enter", "Started"),
                    ("exit", "Ended"));
                run.Events[0].GeometryHex.Should().BeEquivalentTo(run.Images[1].GeometryHex);
                run.Events[1].GeometryHex.Should().BeEquivalentTo(run.Images[2].GeometryHex);
                run.Events[0].GeometryHex.Should().NotBeEquivalentTo(run.Images[^1].GeometryHex);
                run.StateGeneration.Should().Be(run.Images[^1].Generation);
                run.CheckpointGeneration.Should().Be(run.Images[^1].Generation);
            }

            await live.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await CleanupAsync(features);
        }
    }

    [IntegrationTest(Timeout = 180000)]
    public async Task CommittedSnapshots_ProjectVisibility_RestoreObjectId_AndSkipVersionedRows()
    {
        const int layerId = 386412;
        const long objectId = 38641201;
        const long branchObjectId = 38641299;
        var migrationSchema = await database.CreateIsolatedSchemaAsync(nameof(CommittedSnapshots_ProjectVisibility_RestoreObjectId_AndSkipVersionedRows));
        var migration = await database.RunEmbeddedMigrationsUnderLockAsync(
            migrationSchema,
            Assembly.GetAssembly(typeof(Program))!);
        migration.Successful.Should().BeTrue(migration.Error?.ToString());
        await database.DropSchemaAsync(migrationSchema);

        var features = await FindFeatureTableAsync();
        var provider = new TestConnectionProvider(database.DataSource);
        await CleanupLayerAsync(features, layerId);
        try
        {
            await InsertJsonFeatureAsync(features, layerId, objectId, """{"value":60,"secret":"leak","classification":"secret"}""");
            var hiddenGeneration = await ScalarAsync<long>(
                "SELECT MAX(generation) FROM honua.feature_changes WHERE layer_id = @layer AND objectid = @object AND version_id IS NULL",
                ("layer", layerId),
                ("object", objectId));

            var reader = new PostgresAlertChangeReader(provider);
            var listed = await reader.GetChangesAfterAsync(hiddenGeneration - 1, 20);
            var hidden = listed.Should().ContainSingle(change => change.ObjectId == objectId).Subject;
            hidden.HasCommittedImage.Should().BeTrue();
            hidden.CommittedFeature.HasValue.Should().BeFalse("the list read must not buffer committed images");

            var unprojected = await reader.LoadProjectedSnapshotAsync(hiddenGeneration, layerId)
                ?? throw new InvalidOperationException("The list read must return the committed image.");
            unprojected.Value.Attributes["objectid"].Should().Be(objectId);
            unprojected.Value.Attributes.Should().ContainKey("secret");

            var policy = new FixedSnapshotPolicy(
                new SqlFragment("attributes ->> 'classification' = @p0", ["public"]),
                "secret");
            var projectedReader = new PostgresAlertChangeReader(provider, policy);
            var hiddenSnapshot = await projectedReader.LoadProjectedSnapshotAsync(hiddenGeneration, layerId);
            hiddenSnapshot.Should().BeNull("a permanent filter must hide the committed image before evaluation");

            await InsertJsonFeatureAsync(features, layerId, objectId + 1, """{"value":60,"secret":"leak","classification":"public"}""");
            var visibleGeneration = await ScalarAsync<long>(
                "SELECT MAX(generation) FROM honua.feature_changes WHERE layer_id = @layer AND objectid = @object AND version_id IS NULL",
                ("layer", layerId),
                ("object", objectId + 1));
            var visible = await projectedReader.LoadProjectedSnapshotAsync(visibleGeneration, layerId)
                ?? throw new InvalidOperationException("The public row must return a committed image.");
            visible.Value.Attributes.Should().NotContainKey("secret");
            visible.Value.Attributes["objectid"].Should().Be(objectId + 1);
            visible.Value.Attributes["value"].Should().Be(60L);

            var branchGeneration = await ScalarAsync<long>("""
                INSERT INTO honua.feature_changes
                    (generation, layer_id, objectid, operation, version_id, committed_attributes)
                VALUES
                    (nextval('honua.sync_generation'), @layer, @object, 2, gen_random_uuid(), '{"value":999}'::jsonb)
                RETURNING generation
                """,
                ("layer", layerId),
                ("object", branchObjectId));
            var afterBranch = await reader.GetChangesAfterAsync(branchGeneration - 1, 20);
            afterBranch.Should().NotContain(change => change.ObjectId == branchObjectId);

            await InsertSweepRulesAsync(layerId);
            var states = new PostgresAlertStateStore(provider, NullLogger<PostgresAlertStateStore>.Instance);
            var due = DateTimeOffset.Parse("2026-03-08T10:05:00.0000000+00:00", CultureInfo.InvariantCulture);
            await states.UpsertAsync(new AlertStateSnapshot
            {
                RuleId = 38641201,
                LayerId = layerId,
                ObjectId = 1,
                Inside = false,
                LastGeneration = 8,
                ThresholdStateJson = """{"breached":true,"pendingEdge":"resolve","pendingDueAt":"2026-03-08T10:05:00.0000000+00:00"}"""
            });
            await states.UpsertAsync(new AlertStateSnapshot
            {
                RuleId = 38641202,
                LayerId = layerId,
                ObjectId = 2,
                Inside = true,
                EnteredAt = due.AddMinutes(-30),
                LastGeneration = 8,
                ThresholdStateJson = """{"breached":true}"""
            });
            await states.UpsertAsync(new AlertStateSnapshot
            {
                RuleId = 38641203,
                LayerId = layerId,
                ObjectId = 3,
                Inside = true,
                EnteredAt = due.AddMinutes(-30),
                LastGeneration = 8,
                ThresholdStateJson = "{}"
            });

            var candidates = await states.GetDwellCandidatesAsync(due, 5000);
            candidates.Where(state => state.LayerId == layerId).Select(static state => state.ObjectId)
                .Should().BeEquivalentTo(new long[] { 1, 3 });
        }
        finally
        {
            await CleanupLayerAsync(features, layerId);
        }
    }

    private async Task InsertJsonFeatureAsync(FeatureTable features, int layerId, long objectId, string attributes)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {features.Qualified} (objectid, layer_id, geometry, attributes)
            VALUES (@object, @layer, ST_SetSRID(ST_MakePoint(1, 2), 4326), @attributes::jsonb)
            """, connection);
        command.Parameters.AddWithValue("object", objectId);
        command.Parameters.AddWithValue("layer", layerId);
        command.Parameters.AddWithValue("attributes", attributes);
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task InsertSweepRulesAsync(int layerId)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO honua.alert_rules
                (rule_id, service_id, layer_id, zone_id, rule_name, trigger_type, conditions,
                 cooldown_seconds, severity, edition_required, channels, is_active)
            VALUES
                (38641201, 'alert-review-3864', @layer, NULL, 'pending threshold', 4, '{}'::jsonb, 300, 'warning', 1, '{}'::text[], TRUE),
                (38641202, 'alert-review-3864', @layer, NULL, 'idle threshold', 4, '{}'::jsonb, 300, 'warning', 1, '{}'::text[], TRUE),
                (38641203, 'alert-review-3864', @layer, NULL, 'dwell', 3, '{}'::jsonb, 0, 'warning', 1, '{}'::text[], TRUE)
            """, connection);
        command.Parameters.AddWithValue("layer", layerId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task CleanupLayerAsync(FeatureTable? features, int layerId)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM honua.alert_state WHERE layer_id = @layer;
            DELETE FROM honua.alert_rules WHERE layer_id = @layer;
            DELETE FROM honua.feature_changes WHERE layer_id = @layer;
            """, connection);
        command.Parameters.AddWithValue("layer", layerId);
        _ = await command.ExecuteNonQueryAsync();
        if (features is null)
        {
            return;
        }

        await using var featureCommand = new NpgsqlCommand(
            $"DELETE FROM {features.Value.Qualified} WHERE layer_id = @layer",
            connection);
        featureCommand.Parameters.AddWithValue("layer", layerId);
        _ = await featureCommand.ExecuteNonQueryAsync();
    }

    private sealed class FixedSnapshotPolicy(SqlFragment? filter, params string[] masks) : ICommittedSnapshotPolicy
    {
        public Task<CommittedSnapshotPolicy> ResolveAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CommittedSnapshotPolicy(filter, masks.ToImmutableArray()));
    }

    private static long ObjectIdFor(string name) => name switch
    {
        "threshold-full" => 38641011,
        "threshold-batch" => 38641012,
        "threshold-restart" => 38641013,
        "geofence-full" => 38641111,
        "geofence-batch" => 38641112,
        "geofence-restart" => 38641113,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown burst variant."),
    };

    private static (double X, double Y, long Value)[] ThresholdPoints()
        => [(10, 10, 10), (11, 10, 60), (12, 10, 10)];

    private static (double X, double Y, long Value)[] GeofencePoints()
        => [(10, 10, 0), (0, 0, 1), (20, 20, 0)];

    private async Task<BurstResult> EvaluateBurstAsync(
        FeatureTable features,
        AlertPipeline pipeline,
        PostgresAlertCheckpointStore checkpoints,
        IFeatureReader live,
        int layerId,
        string name,
        int batchSize,
        bool restartEachGeneration,
        (double X, double Y, long Value)[] updates)
    {
        var objectId = ObjectIdFor(name);
        await InsertFeatureAsync(features, layerId, objectId, updates[0].X, updates[0].Y, updates[0].Value);
        var watermark = await ScalarAsync<long>(
            $"SELECT COALESCE(MAX(generation), 0) FROM honua.feature_changes WHERE layer_id = {layerId.ToString(CultureInfo.InvariantCulture)} AND objectid = {objectId.ToString(CultureInfo.InvariantCulture)}");
        for (var index = 0; index < updates.Length; index++)
        {
            var update = updates[index];
            await UpdateFeatureAsync(features, layerId, objectId, update.X, update.Y, update.Value);
        }

        var worker = "evaluator-" + name;
        await checkpoints.SetAsync(new AlertWorkerCheckpoint { WorkerName = worker, LastGeneration = watermark });
        var cursor = watermark;
        while (true)
        {
            var active = restartEachGeneration
                ? CreatePipeline(
                    new PostgresAlertChangeReader(new TestConnectionProvider(database.DataSource)),
                    new PostgresAlertRuleRepository(new TestConnectionProvider(database.DataSource), NullLogger<PostgresAlertRuleRepository>.Instance),
                    new PostgresAlertStateStore(new TestConnectionProvider(database.DataSource), NullLogger<PostgresAlertStateStore>.Instance),
                    live,
                    new AlertDispatchWriter(
                        new PostgresAlertOutboxWriter(new TestConnectionProvider(database.DataSource)),
                        TestTelemetry.CreateAlertPipelineMetrics(),
                        NullLogger<AlertDispatchWriter>.Instance))
                : pipeline;
            if (restartEachGeneration)
            {
                cursor = (await checkpoints.GetAsync(worker)).LastGeneration;
            }

            var next = await active.ProcessChangesAsync(cursor, batchSize);
            if (next == cursor)
            {
                break;
            }

            cursor = next;
            await checkpoints.SetAsync(new AlertWorkerCheckpoint { WorkerName = worker, LastGeneration = cursor });
            if (!restartEachGeneration && batchSize >= 100)
            {
                break;
            }
        }

        var images = await ReadImagesAsync(layerId, objectId, watermark);
        var events = await ReadEventsAsync(layerId, objectId);
        var stateGeneration = await ScalarAsync<long>(
            "SELECT COALESCE(MAX(last_generation), 0) FROM honua.alert_state WHERE layer_id = @layer AND objectid = @object",
            ("layer", layerId),
            ("object", objectId));
        var checkpoint = await checkpoints.GetAsync(worker);
        images.Should().HaveCount(updates.Length);
        return new BurstResult(images, events, stateGeneration, checkpoint.LastGeneration);
    }

    private static AlertPipeline CreatePipeline(
        IAlertChangeReader reader,
        IAlertRuleRepository rules,
        IAlertStateStore states,
        IFeatureReader features,
        AlertDispatchWriter writer)
    {
        var edition = Substitute.For<IAlertEditionPolicy>();
        edition.IsRuleAllowed(Arg.Any<AlertRuleDefinition>()).Returns(true);
        edition.IsChannelAllowed(Arg.Any<AlertChannelType>()).Returns(true);
        edition.IsChannelConfigured(Arg.Any<AlertChannelType>()).Returns(true);
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource.alert-burst", "alerts")
            .AddService("service.alert-burst", ServiceId)
            .AddPublication("publication.threshold", "service.alert-burst", "resource.alert-burst", layerIndex: ThresholdLayerId)
            .AddPublication("publication.geofence", "service.alert-burst", "resource.alert-burst", layerIndex: GeofenceLayerId)
            .Build();
        return new AlertPipeline(
            reader,
            rules,
            states,
            features,
            new TestMetadataV2GraphProvider(graph),
            new DefaultAlertEvaluator(),
            edition,
            writer,
            NullLogger<AlertPipeline>.Instance);
    }

    private async Task<long> InsertZoneAsync()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO honua.alert_zones (service_id, zone_name, geometry)
            VALUES (@service, 'burst yard', ST_Multi(ST_SetSRID(ST_MakeEnvelope(-1, -1, 1, 1), 4326)))
            RETURNING zone_id
            """, connection);
        command.Parameters.AddWithValue("service", ServiceId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task InsertRulesAsync(long zoneId)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO honua.alert_rules
                (rule_id, service_id, layer_id, zone_id, rule_name, trigger_type, conditions,
                 cooldown_seconds, severity, edition_required, channels, is_active)
            VALUES
                (@threshold, @service, @threshold_layer, NULL, 'burst threshold', 4,
                 '{"field":"value","operator":">","value":50}'::jsonb, 0, 'warning', 1, '{}'::text[], TRUE),
                (@enter, @service, @geofence_layer, @zone, 'burst enter', 1,
                 '{}'::jsonb, 0, 'warning', 1, '{}'::text[], TRUE),
                (@exit, @service, @geofence_layer, @zone, 'burst exit', 2,
                 '{}'::jsonb, 0, 'warning', 1, '{}'::text[], TRUE)
            """, connection);
        command.Parameters.AddWithValue("threshold", ThresholdRuleId);
        command.Parameters.AddWithValue("enter", EnterRuleId);
        command.Parameters.AddWithValue("exit", ExitRuleId);
        command.Parameters.AddWithValue("service", ServiceId);
        command.Parameters.AddWithValue("threshold_layer", ThresholdLayerId);
        command.Parameters.AddWithValue("geofence_layer", GeofenceLayerId);
        command.Parameters.AddWithValue("zone", zoneId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task InsertFeatureAsync(FeatureTable features, int layerId, long objectId, double x, double y, long value)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {features.Qualified} (objectid, layer_id, geometry, attributes)
            VALUES (@object, @layer, ST_SetSRID(ST_MakePoint(@x, @y), 4326), @attributes::jsonb)
            """, connection);
        BindFeature(command, layerId, objectId, x, y, value);
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task UpdateFeatureAsync(FeatureTable features, int layerId, long objectId, double x, double y, long value)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            UPDATE {features.Qualified}
            SET geometry = ST_SetSRID(ST_MakePoint(@x, @y), 4326),
                attributes = @attributes::jsonb,
                updated_at = clock_timestamp()
            WHERE objectid = @object AND layer_id = @layer
            """, connection);
        BindFeature(command, layerId, objectId, x, y, value);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static void BindFeature(NpgsqlCommand command, int layerId, long objectId, double x, double y, long value)
    {
        command.Parameters.AddWithValue("object", objectId);
        command.Parameters.AddWithValue("layer", layerId);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("attributes", JsonSerializer.Serialize(new Dictionary<string, long> { ["value"] = value }));
    }

    private async Task<List<CommittedImage>> ReadImagesAsync(int layerId, long objectId, long watermark)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT generation, encode(ST_AsEWKB(committed_geometry), 'hex'), committed_attributes->>'value'
            FROM honua.feature_changes
            WHERE layer_id = @layer AND objectid = @object AND generation > @watermark
            ORDER BY generation
            """, connection);
        command.Parameters.AddWithValue("layer", layerId);
        command.Parameters.AddWithValue("object", objectId);
        command.Parameters.AddWithValue("watermark", watermark);
        var images = new List<CommittedImage>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            images.Add(new CommittedImage(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? null : long.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        }

        return images;
    }

    private async Task<List<ObservedEvent>> ReadEventsAsync(int layerId, long objectId)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT generation, payload->>'transition', payload->>'incidentStatus',
                   payload->'attributes'->>'value', payload->>'geometryWkbHex'
            FROM honua.alert_events
            WHERE layer_id = @layer AND objectid = @object
            ORDER BY generation, event_id
            """, connection);
        command.Parameters.AddWithValue("layer", layerId);
        command.Parameters.AddWithValue("object", objectId);
        var events = new List<ObservedEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new ObservedEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : long.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? "" : reader.GetString(4)));
        }

        return events;
    }

    private async Task<FeatureTable> FindFeatureTableAsync()
    {
        // Migrations create the unqualified features table on the migration search_path,
        // which the isolated-schema cleanup then drops. The runtime table is honua.features.
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS honua.features (
                objectid BIGSERIAL PRIMARY KEY,
                layer_id INT NOT NULL,
                geometry GEOMETRY,
                attributes JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW(),
                updated_at TIMESTAMPTZ DEFAULT NOW()
            );
            DROP TRIGGER IF EXISTS trigger_track_feature_changes ON honua.features;
            CREATE TRIGGER trigger_track_feature_changes
                AFTER INSERT OR UPDATE OR DELETE ON honua.features
                FOR EACH ROW
                EXECUTE FUNCTION honua.track_feature_changes();
            """, connection);
        _ = await command.ExecuteNonQueryAsync();
        return new FeatureTable("honua", "features");
    }

    private async Task CleanupAsync(FeatureTable? features)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM honua.alert_event_lifecycle
            WHERE event_id IN (SELECT event_id FROM honua.alert_events WHERE layer_id IN (@threshold_layer, @geofence_layer));
            DELETE FROM honua.alert_dispatch
            WHERE event_id IN (SELECT event_id FROM honua.alert_events WHERE layer_id IN (@threshold_layer, @geofence_layer));
            DELETE FROM honua.alert_events WHERE layer_id IN (@threshold_layer, @geofence_layer);
            DELETE FROM honua.alert_state WHERE layer_id IN (@threshold_layer, @geofence_layer);
            DELETE FROM honua.alert_rules WHERE layer_id IN (@threshold_layer, @geofence_layer);
            DELETE FROM honua.alert_zones WHERE service_id = @service;
            DELETE FROM honua.feature_changes WHERE layer_id IN (@threshold_layer, @geofence_layer);
            DELETE FROM honua.alert_worker_checkpoint WHERE worker_name LIKE 'evaluator-%';
            """, connection);
        command.Parameters.AddWithValue("threshold_layer", ThresholdLayerId);
        command.Parameters.AddWithValue("geofence_layer", GeofenceLayerId);
        command.Parameters.AddWithValue("service", ServiceId);
        _ = await command.ExecuteNonQueryAsync();
        if (features is null)
        {
            return;
        }

        await using var featureCommand = new NpgsqlCommand(
            $"DELETE FROM {features.Value.Qualified} WHERE layer_id IN (@threshold_layer, @geofence_layer)",
            connection);
        featureCommand.Parameters.AddWithValue("threshold_layer", ThresholdLayerId);
        featureCommand.Parameters.AddWithValue("geofence_layer", GeofenceLayerId);
        _ = await featureCommand.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture)!;
    }

    private readonly record struct FeatureTable(string Schema, string Table)
    {
        public string Qualified => Quote(Schema) + "." + Quote(Table);

        private static string Quote(string identifier)
        {
            if (identifier is not ("public" or "honua" or "features"))
            {
                throw new InvalidOperationException("Refusing to interpolate an unexpected feature table name.");
            }

            return "\"" + identifier + "\"";
        }
    }

    private sealed record CommittedImage(long Generation, string GeometryHex, long? Value);

    private sealed record ObservedEvent(long Generation, string Transition, string IncidentStatus, long? Value, string GeometryHex);

    private sealed record BurstResult(
        IReadOnlyList<CommittedImage> Images,
        IReadOnlyList<ObservedEvent> Events,
        long StateGeneration,
        long CheckpointGeneration);

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}
