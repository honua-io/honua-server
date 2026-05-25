// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.TemporalHistory.Domain;
using Honua.Postgres.Features.TemporalHistory;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.TemporalHistory;

/// <summary>
/// Integration tests for <see cref="PostgresTemporalHistorySource"/> against the shared Testcontainers
/// Postgres/PostGIS fixture. Exercises capability discovery, deterministic as-of reads, diff
/// classification, per-feature timelines with attribution masking, rollback planning, and append-only
/// rollback execution end-to-end over a seeded audit-log table and a system-versioned temporal table.
/// </summary>
[Collection("Database")]
public sealed class PostgresTemporalHistorySourceTests(PostgresFixture fixture)
{
    private const int LayerId = 42;
    private const string HistoryTable = "parcels_history";

    private static readonly DateTimeOffset T1 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T4 = new(2024, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOfEarly = new(2024, 2, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOfLate = new(2024, 4, 15, 0, 0, 0, TimeSpan.Zero);

    [IntegrationTest]
    public async Task GetCapabilities_AuditLogWithIndex_AdvertisesAsOfAndRollback()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);

            var capabilities = await source.GetCapabilitiesAsync(BuildAuditLayer(schema, allowRollback: true));

            capabilities.Should().NotBeNull();
            capabilities!.SourceKind.Should().Be(TemporalSourceKind.AuditLog);
            capabilities.SupportsAsOf.Should().BeTrue();
            capabilities.SupportsDiff.Should().BeTrue();
            capabilities.SupportsTimeline.Should().BeTrue();
            capabilities.SupportsRollbackExecution.Should().BeTrue();
            capabilities.SupportsAttribution.Should().BeTrue();
            capabilities.GeometrySrid.Should().Be(4326);
            capabilities.Warnings.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetCapabilities_MissingAsOfIndex_WithdrawsAsOfWithWarning()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: false);
            var source = CreateSource(schema);

            var capabilities = await source.GetCapabilitiesAsync(BuildAuditLayer(schema, allowRollback: true));

            capabilities!.SupportsAsOf.Should().BeFalse();
            capabilities.SupportsDiff.Should().BeFalse();
            capabilities.SupportsHistory.Should().BeTrue();
            capabilities.Warnings.Should().NotBeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetCapabilities_NonTemporalLayer_ReturnsNull()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            var source = CreateSource(schema);
            var layer = LayerDefinition.CreateBasic(LayerId, "plain", GeometryType.Point);

            var capabilities = await source.GetCapabilitiesAsync(layer);

            capabilities.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task QueryAsOf_AuditLog_ReturnsDeterministicSnapshot()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);
            var layer = BuildAuditLayer(schema, allowRollback: false);

            var early = await source.QueryAsOfAsync(layer, TemporalCursor.AtTimestamp(AsOfEarly), new TemporalPageRequest());
            var late = await source.QueryAsOfAsync(layer, TemporalCursor.AtTimestamp(AsOfLate), new TemporalPageRequest());

            early.Items.Select(i => i.Id).Should().BeEquivalentTo("1", "2", "3", "5");
            FieldValue(early, "1", "val").Should().Be("1");

            // The deleted feature 3 is gone, feature 4 has appeared, feature 1 advanced.
            late.Items.Select(i => i.Id).Should().BeEquivalentTo("1", "2", "4", "5");
            FieldValue(late, "1", "val").Should().Be("2");

            // Re-running the same as-of cursor yields identical results.
            var earlyAgain = await source.QueryAsOfAsync(layer, TemporalCursor.AtTimestamp(AsOfEarly), new TemporalPageRequest());
            earlyAgain.Items.Select(i => i.Id).Should().Equal(early.Items.Select(i => i.Id));
            early.Items[0].Geometry.Should().NotBeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Diff_AuditLog_ClassifiesAddedRemovedAttributeAndGeometryChanges()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);
            var layer = BuildAuditLayer(schema, allowRollback: false);

            var diff = await source.DiffAsync(
                layer,
                TemporalCursor.AtTimestamp(AsOfEarly),
                TemporalCursor.AtTimestamp(AsOfLate),
                new TemporalPageRequest());

            diff.Summary.Added.Should().Be(1);
            diff.Summary.Removed.Should().Be(1);
            diff.Summary.AttributeChanged.Should().Be(1);
            diff.Summary.GeometryChanged.Should().Be(1);

            ChangeKind(diff, "4").Should().Be(TemporalChangeKind.Added);
            ChangeKind(diff, "3").Should().Be(TemporalChangeKind.Removed);
            ChangeKind(diff, "1").Should().Be(TemporalChangeKind.AttributeChanged);
            ChangeKind(diff, "5").Should().Be(TemporalChangeKind.GeometryChanged);

            var attributeChange = diff.Items.Single(i => i.FeatureId == "1");
            attributeChange.FieldChanges.Should().Contain(c => c.Field == "val");
            attributeChange.Attribution!.Actor.Should().Be("bob");

            diff.Items.Single(i => i.FeatureId == "5").GeometryChanged.Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetTimeline_AuditLog_ReturnsRevisionsWithAttributionAndFieldChanges()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);
            var layer = BuildAuditLayer(schema, allowRollback: false);

            var timeline = await source.GetTimelineAsync(layer, "1", new TemporalPageRequest());

            timeline.AttributionMasked.Should().BeFalse();
            timeline.Revisions.Should().HaveCount(2);
            // Newest first: the update precedes the insert.
            timeline.Revisions[0].Operation.Should().Be("UPDATE");
            timeline.Revisions[0].Attribution!.Actor.Should().Be("bob");
            timeline.Revisions[0].FieldChanges.Should().Contain(c => c.Field == "val");
            timeline.Revisions[1].Operation.Should().Be("INSERT");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetTimeline_WithMaskingPolicy_OmitsActorAttribution()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);
            var layer = BuildAuditLayer(schema, allowRollback: false, maskAttribution: true);

            var timeline = await source.GetTimelineAsync(layer, "1", new TemporalPageRequest());

            timeline.AttributionMasked.Should().BeTrue();
            timeline.Revisions.Should().NotBeEmpty();
            timeline.Revisions.Should().OnlyContain(r => r.Attribution == null || r.Attribution.Actor == null);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PlanRollback_ReflectsPolicyAndFeasibility()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);

            var enabledPlan = await source.PlanRollbackAsync(
                BuildAuditLayer(schema, allowRollback: true), TemporalCursor.AtTimestamp(AsOfEarly));
            enabledPlan.Mode.Should().Be(TemporalRollbackMode.Supported);
            enabledPlan.IsSupported.Should().BeTrue();
            enabledPlan.RequiresJob.Should().BeTrue();
            enabledPlan.RequiresApproval.Should().BeTrue();
            enabledPlan.AffectedCount.Should().Be(4);

            var disabledPlan = await source.PlanRollbackAsync(
                BuildAuditLayer(schema, allowRollback: false), TemporalCursor.AtTimestamp(AsOfEarly));
            disabledPlan.Mode.Should().Be(TemporalRollbackMode.Blocked);
            disabledPlan.IsSupported.Should().BeFalse();
            disabledPlan.ValidationFindings.Should().Contain(f => f.Code == "rollback-disabled");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task PlanRollback_WithSchemaEvolution_RequiresScript()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);
            var layer = BuildAuditLayer(
                schema,
                allowRollback: true,
                schemaEvolution: SchemaEvolutionPolicy.Compatible);

            var capabilities = await source.GetCapabilitiesAsync(layer);
            var plan = await source.PlanRollbackAsync(layer, TemporalCursor.AtTimestamp(AsOfEarly));

            capabilities!.SupportsRollbackExecution.Should().BeFalse();
            plan.Mode.Should().Be(TemporalRollbackMode.ScriptRequired);
            plan.RequiresScript.Should().BeTrue();
            plan.RequiresJob.Should().BeFalse();
            plan.CompatibilityFindings.Should().Contain(f => f.Code == "schema-evolution-script");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ExecuteRollback_AppendsCorrectiveRevisions_PreservingHistory()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedAuditLogAsync(schema, withAsOfIndex: true);
            var source = CreateSource(schema);
            var layer = BuildAuditLayer(schema, allowRollback: true);

            var rowsBefore = await CountHistoryRowsAsync(schema);

            var result = await source.ExecuteRollbackAsync(
                layer,
                TemporalCursor.AtTimestamp(AsOfEarly),
                new TemporalRollbackContext { JobId = "job-xyz", Actor = "operator", CorrelationId = "rb-1" });

            // Corrective forward operation: feature 1 (attr), 3 (re-add), 4 (remove), 5 (geometry).
            result.AppliedCount.Should().Be(4);

            // History is append-only: no rows were deleted, only appended.
            var rowsAfter = await CountHistoryRowsAsync(schema);
            rowsAfter.Should().Be(rowsBefore + 4);

            // The new checkpoint reflects the restored (early) state.
            TemporalCursor.TryParse(result.Checkpoint, out var checkpoint).Should().BeTrue();
            var restored = await source.QueryAsOfAsync(layer, checkpoint, new TemporalPageRequest());
            restored.Items.Select(i => i.Id).Should().BeEquivalentTo("1", "2", "3", "5");
            FieldValue(restored, "1", "val").Should().Be("1");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task QueryAsOf_TemporalTable_ResolvesSystemVersionedState()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresTemporalHistorySourceTests));
        try
        {
            await SeedTemporalTableAsync(schema);
            var source = CreateSource(schema);
            var layer = BuildTemporalTableLayer(schema);

            var capabilities = await source.GetCapabilitiesAsync(layer);
            capabilities!.SourceKind.Should().Be(TemporalSourceKind.TemporalTable);
            capabilities.SupportsAsOf.Should().BeTrue();

            var early = await source.QueryAsOfAsync(layer, TemporalCursor.AtTimestamp(AsOfEarly), new TemporalPageRequest());
            var late = await source.QueryAsOfAsync(layer, TemporalCursor.AtTimestamp(AsOfLate), new TemporalPageRequest());

            FieldValue(early, "1", "name").Should().Be("a1");
            FieldValue(late, "1", "name").Should().Be("a2");
            late.Items.Select(i => i.Id).Should().BeEquivalentTo("1", "2");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static string? FieldValue(TemporalSnapshot snapshot, string featureId, string field)
    {
        var feature = snapshot.Items.Single(i => i.Id == featureId);
        return feature.Attributes.TryGetValue(field, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
    }

    private static TemporalChangeKind ChangeKind(TemporalDiff diff, string featureId)
        => diff.Items.Single(i => i.FeatureId == featureId).ChangeKind;

    private PostgresTemporalHistorySource CreateSource(string schema)
        => new(
            new TestConnectionProvider(fixture.DataSource, schema),
            NullLogger<PostgresTemporalHistorySource>.Instance,
            TimeProvider.System);

    private static LayerDefinition BuildAuditLayer(
        string schema,
        bool allowRollback,
        bool maskAttribution = false,
        SchemaEvolutionPolicy schemaEvolution = SchemaEvolutionPolicy.Fixed)
        => LayerDefinition.CreateBasic(LayerId, "parcels", GeometryType.Point) with
        {
            StorageMapping = new LayerStorageMapping(
                TableName: "parcels",
                SchemaName: schema,
                StorageSrid: 4326),
            Metadata = new CatalogMetadata
            {
                TemporalSource = new TemporalSourceConfig
                {
                    SourceKind = TemporalSourceKind.AuditLog,
                    HistoryTableName = HistoryTable,
                    AllowRollback = allowRollback,
                    SchemaEvolution = schemaEvolution,
                    AccessPolicy = new TemporalAccessPolicy { MaskAttribution = maskAttribution }
                }
            }
        };

    private static LayerDefinition BuildTemporalTableLayer(string schema)
        => LayerDefinition.CreateBasic(LayerId, "assets", GeometryType.Point) with
        {
            StorageMapping = new LayerStorageMapping(
                TableName: "assets",
                SchemaName: schema,
                GeometryColumn: "geom",
                StorageSrid: 4326),
            Metadata = new CatalogMetadata
            {
                TemporalSource = new TemporalSourceConfig
                {
                    SourceKind = TemporalSourceKind.TemporalTable,
                    SystemPeriodColumn = "sys_period",
                    Attribution = new TemporalAttributionMapping
                    {
                        FeatureIdColumn = "asset_id",
                        GeometryColumn = "geom",
                        ActorColumn = null,
                        SourceRefColumn = null,
                        CorrelationIdColumn = null,
                        BeforeAttributesColumn = null,
                        AfterAttributesColumn = null
                    }
                }
            }
        };

    private async Task SeedAuditLogAsync(string schema, bool withAsOfIndex)
    {
        var index = withAsOfIndex
            ? $"CREATE INDEX idx_parcels_history_fid_changed ON \"{schema}\".{HistoryTable} (feature_id, changed_at);"
            : string.Empty;

        await fixture.ExecuteAsync($$"""
            CREATE TABLE "{{schema}}".{{HistoryTable}} (
                history_id bigserial PRIMARY KEY,
                feature_id bigint NOT NULL,
                operation text NOT NULL,
                changed_at timestamptz NOT NULL,
                actor text,
                source_ref text,
                correlation_id text,
                before_attrs jsonb,
                after_attrs jsonb,
                geometry geometry(Point, 4326)
            );
            {{index}}

            INSERT INTO "{{schema}}".{{HistoryTable}}
                (feature_id, operation, changed_at, actor, source_ref, correlation_id, before_attrs, after_attrs, geometry)
            VALUES
                (1, 'INSERT', '{{Iso(T1)}}', 'alice', 'rel-1', 'corr-1', NULL, '{"name":"a","val":1}', ST_SetSRID(ST_MakePoint(1, 1), 4326)),
                (1, 'UPDATE', '{{Iso(T3)}}', 'bob',   'rel-3', 'corr-3', '{"name":"a","val":1}', '{"name":"a","val":2}', ST_SetSRID(ST_MakePoint(1, 1), 4326)),
                (2, 'INSERT', '{{Iso(T1)}}', 'alice', 'rel-1', 'corr-1', NULL, '{"name":"b"}', ST_SetSRID(ST_MakePoint(2, 2), 4326)),
                (3, 'INSERT', '{{Iso(T2)}}', 'alice', 'rel-2', 'corr-2', NULL, '{"name":"c"}', ST_SetSRID(ST_MakePoint(3, 3), 4326)),
                (3, 'DELETE', '{{Iso(T4)}}', 'bob',   'rel-4', 'corr-4', '{"name":"c"}', NULL, NULL),
                (4, 'INSERT', '{{Iso(T3)}}', 'carol', 'rel-3', 'corr-3', NULL, '{"name":"d"}', ST_SetSRID(ST_MakePoint(4, 4), 4326)),
                (5, 'INSERT', '{{Iso(T1)}}', 'alice', 'rel-1', 'corr-1', NULL, '{"name":"e"}', ST_SetSRID(ST_MakePoint(5, 5), 4326)),
                (5, 'UPDATE', '{{Iso(T3)}}', 'carol', 'rel-3', 'corr-3', '{"name":"e"}', '{"name":"e"}', ST_SetSRID(ST_MakePoint(6, 6), 4326));
            """);
    }

    private async Task SeedTemporalTableAsync(string schema)
    {
        await fixture.ExecuteAsync($"""
            CREATE TABLE "{schema}".assets (
                asset_id bigint NOT NULL,
                name text,
                geom geometry(Point, 4326),
                sys_period tstzrange NOT NULL
            );

            INSERT INTO "{schema}".assets (asset_id, name, geom, sys_period) VALUES
                (1, 'a1', ST_SetSRID(ST_MakePoint(1, 1), 4326), tstzrange('{Iso(T1)}', '{Iso(T3)}')),
                (1, 'a2', ST_SetSRID(ST_MakePoint(1, 2), 4326), tstzrange('{Iso(T3)}', NULL)),
                (2, 'b1', ST_SetSRID(ST_MakePoint(2, 2), 4326), tstzrange('{Iso(T2)}', NULL));
            """);
    }

    private async Task<int> CountHistoryRowsAsync(string schema)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{schema}\".{HistoryTable};";
        var scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static string Iso(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Minimal <see cref="IDatabaseConnectionProvider"/> that pins the per-test isolated schema on
    /// every connection. Lives in the test assembly because the TestKit provider is IVT-scoped to
    /// <c>Honua.Server.Tests</c>.
    /// </summary>
    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => await operation();

        public async Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => await operation();
    }
}
