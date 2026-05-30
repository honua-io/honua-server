// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Issue #1015 slice 2: GeoServer data-source application and feature data copy.
///
/// Slice 1 (PR #1095) added idempotent catalog persistence of workspace and
/// layer-group entries. Slice 2 extends that to:
/// 1. Persist a deterministic record in <c>honua.migration_data_sources</c> for
///    each in-scope data store on the manifest. Each datastore produces a
///    create / already-applied / manual-review step result.
/// 2. Copy feature data from a source PostGIS table into <c>honua_data.&lt;layer&gt;</c>
///    when the source lives in the same Postgres instance, before publishing
///    the catalog layer. Idempotent re-apply does not duplicate rows.
/// 3. Respect the workspace-scope guard from issue #1098 / PR #1100 so a
///    manifest cannot cause cross-workspace mutations.
/// </summary>
public sealed class GeoServerImportServiceDataSourceApplyTests
{
    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeAndCatalogWriter_AppliesPostGisDataSourceEntry()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var publisher = new RecordingLayerPublishingService();
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            AutoPublishLayers = true,
            RequestTimeoutSeconds = 5
        };

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        result.ApplyExecution.Should().NotBeNull();

        var dataSourceStep = result.ApplyExecution!.StepResults.SingleOrDefault(step =>
            step.Kind == "datastore" && step.SourceId == "datastore:ops:pg");
        dataSourceStep.Should().NotBeNull("each in-scope data store produces a deterministic apply step result");
        dataSourceStep!.Outcome.Should().Be("applied");
        dataSourceStep.Action.Should().Be("apply-data-source");

        catalogWriter.DataSourceRequests.Should().ContainSingle(r =>
            r.SourceId == "datastore:ops:pg" &&
            r.WorkspaceName == "ops" &&
            r.DataSourceType.Equals("PostGIS", StringComparison.OrdinalIgnoreCase));
        catalogWriter.DataSourceRequests.Should().OnlyContain(r =>
            r.ConnectionSummary.Length > 0 &&
            !r.ConnectionSummary.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeReApply_IsIdempotentForDataSourceStep()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var publisher = new RecordingLayerPublishingService();
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            AutoPublishLayers = true,
            RequestTimeoutSeconds = 5
        };

        var firstService = CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter);
        await firstService.ImportConfigurationAsync(request);

        var secondService = CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter);
        var secondResult = await secondService.ImportConfigurationAsync(request);

        secondResult.Success.Should().BeTrue();
        var step = secondResult.ApplyExecution!.StepResults.Single(s =>
            s.Kind == "datastore" && s.SourceId == "datastore:ops:pg");
        step.Outcome.Should().Be("already-applied");

        // Idempotency contract: the writer is still invoked on re-apply, but
        // the underlying upsert reports "already-applied" rather than creating
        // a duplicate row.
        catalogWriter.DataSourceRequests
            .Count(r => r.SourceId == "datastore:ops:pg")
            .Should().Be(2);
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeButNoCatalogWriter_KeepsDataSourceStepAsManualReview()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            RequestTimeoutSeconds = 5
        };

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses))
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        result.ApplyExecution!.StepResults
            .Where(step => step.Kind == "datastore")
            .Should().OnlyContain(step => step.Outcome == "manual-review");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithApplyModeAndCopiedFeatureData_RecordsCopyEvidenceInStepMessage()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var publisher = new RecordingLayerPublishingService();
        var catalogWriter = new RecordingMigrationCatalogWriter
        {
            FeatureCopyResultFactory = (_, _) => new MigrationFeatureCopyOutcome
            {
                Status = MigrationFeatureCopyStatus.Copied,
                RowCount = 42
            }
        };
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = false,
            ApplyMode = true,
            AutoPublishLayers = true,
            WorkspaceNames = ["ops"],
            RequestTimeoutSeconds = 5
        };

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses), publisher, catalogWriter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        catalogWriter.FeatureCopyRequests.Should().ContainSingle(r =>
            r.SourceSchema == "public" &&
            r.SourceTable == "roads" &&
            r.TargetSchema == "honua_data" &&
            r.TargetTable == "roads");

        var layerStep = result.ApplyExecution!.StepResults.Single(s =>
            s.Kind == "layer" && s.SourceId == "layer:ops:roads");
        layerStep.Outcome.Should().Be("applied");
        layerStep.Message.Should().Contain("Copied 42 feature rows into honua_data.roads");

        publisher.Requests.Should().ContainSingle()
            .Which.Should().Match<LayerPublishRequest>(req =>
                req.Schema == "honua_data" && req.Table == "roads");
    }

    [Fact]
    public async Task ImportConfigurationAsync_WithDryRun_DoesNotApplyDataSources()
    {
        var fixture = LoadFixture("CatalogApplySlice");
        var catalogWriter = new RecordingMigrationCatalogWriter();
        var request = new GeoServerImportRequest
        {
            GeoServerRestUrl = fixture.ServiceUrl,
            TargetHonuaUrl = "https://honua.example.test",
            DryRun = true,
            ApplyMode = false,
            RequestTimeoutSeconds = 5
        };

        var result = await CreateService(new FixtureHttpHandler(fixture.Responses), catalogWriter: catalogWriter)
            .ImportConfigurationAsync(request);

        result.Success.Should().BeTrue();
        catalogWriter.DataSourceRequests.Should().BeEmpty(
            "dry-run does not exercise the apply path and must not write data-source rows");
        catalogWriter.FeatureCopyRequests.Should().BeEmpty();
    }

    [Collection("Database")]
    public sealed class DatabaseIntegration(PostgresFixture fixture)
    {
        [IntegrationTest]
        public async Task ImportConfigurationAsync_WithRealPostGis_AppliesDataSourceAndCopiesFeatureClass()
        {
            // Use a short label — PG identifiers are capped at 63 bytes, and the
            // computed copy-target name `<schemaName>_roads` exceeds the limit
            // (and gets silently truncated) when the test class label is long.
            var schemaName = await fixture.CreateIsolatedSchemaAsync("DsApply");
            try
            {
                await SetUpHonuaCatalogAsync(schemaName);
                await SetUpSourceRoadsTableAsync(schemaName);

                var fixtureScenario = WithDataStoreSchema(
                    LoadFixture("CatalogApplySlice"),
                    schemaName);
                var request = new GeoServerImportRequest
                {
                    GeoServerRestUrl = fixtureScenario.ServiceUrl,
                    TargetHonuaUrl = "https://honua.example.test",
                    DryRun = false,
                    ApplyMode = true,
                    AutoPublishLayers = false,
                    RequestTimeoutSeconds = 5
                };

                var service = CreateServiceForFixture(
                    new FixtureHttpHandler(fixtureScenario.Responses),
                    fixture.ConnectionString,
                    schemaName);

                var result = await service.ImportConfigurationAsync(request);

                result.Success.Should().BeTrue();

                var dataSourceStep = result.ApplyExecution!.StepResults.Single(step =>
                    step.Kind == "datastore" && step.SourceId == "datastore:ops:pg");
                dataSourceStep.Outcome.Should().Be("applied");

                // honua.migration_data_sources holds exactly one row for the datastore.
                var dataSourceRows = await CountAsync(
                    "SELECT COUNT(*) FROM honua.migration_data_sources WHERE source_id = 'datastore:ops:pg';");
                dataSourceRows.Should().Be(1);

                // The roads feature class was copied into honua_data.<table> and the
                // row count matches the seeded source.
                var copiedRows = await CountAsync($"SELECT COUNT(*) FROM honua_data.\"{schemaName}_roads\";");
                copiedRows.Should().Be(3);

                // Idempotent re-apply: no duplicate rows on the data-source table,
                // no duplicate rows in the copied feature class.
                var reApplyResult = await service.ImportConfigurationAsync(request);
                reApplyResult.Success.Should().BeTrue();
                var reAppliedStep = reApplyResult.ApplyExecution!.StepResults.Single(step =>
                    step.Kind == "datastore" && step.SourceId == "datastore:ops:pg");
                reAppliedStep.Outcome.Should().Be("already-applied");

                (await CountAsync(
                    "SELECT COUNT(*) FROM honua.migration_data_sources WHERE source_id = 'datastore:ops:pg';"))
                    .Should().Be(1);
                (await CountAsync($"SELECT COUNT(*) FROM honua_data.\"{schemaName}_roads\";")).Should().Be(3);
            }
            finally
            {
                await TryDropHonuaDataTableAsync($"{schemaName}_roads");
                await fixture.DropSchemaAsync(schemaName);
            }

            async Task<long> CountAsync(string sql)
            {
                await using var conn = await fixture.GetConnectionAsync(schemaName);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                var raw = await cmd.ExecuteScalarAsync();
                return raw switch
                {
                    long l => l,
                    int i => i,
                    _ => Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture)
                };
            }

            async Task TryDropHonuaDataTableAsync(string table)
            {
                try
                {
                    await using var conn = await fixture.GetConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DROP TABLE IF EXISTS honua_data.\"{table}\";";
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (NpgsqlException)
                {
                    // Best effort: the test owns its own honua_data table per run.
                }
            }
        }

        /// <summary>
        /// Apply the honua.services + honua.migration_data_sources schema we
        /// need without running the full 029_ migration runner. Tests are
        /// schema-isolated, so the honua.services table is shared across the
        /// container but the per-test source table lives in an isolated schema.
        /// </summary>
        private async Task SetUpHonuaCatalogAsync(string schemaName)
        {
            await using var conn = await fixture.GetConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE SCHEMA IF NOT EXISTS honua;
                CREATE SCHEMA IF NOT EXISTS honua_data;
                -- Schema MUST stay column-compatible with the canonical
                -- honua.services from tests/seed/server.yaml — the
                -- Postgres testcontainer is shared, so a CREATE TABLE IF
                -- NOT EXISTS without service_extent/metadata/connection_id
                -- would shadow the seeded schema for any test that runs
                -- after this one and break inserts referencing those
                -- columns (same bug we fixed for #1098 in f49097d12).
                CREATE TABLE IF NOT EXISTS honua.services (
                    service_name VARCHAR(64) PRIMARY KEY,
                    description TEXT NOT NULL DEFAULT '',
                    srid INT NOT NULL DEFAULT 4326,
                    supported_formats TEXT[] NOT NULL DEFAULT '{JSON,GeoJSON}',
                    capabilities TEXT[] NOT NULL DEFAULT '{Query,Extract}',
                    service_extent GEOMETRY,
                    metadata JSONB,
                    connection_id UUID,
                    created_at TIMESTAMPTZ DEFAULT NOW(),
                    updated_at TIMESTAMPTZ DEFAULT NOW()
                );
                ALTER TABLE honua.services
                    ADD COLUMN IF NOT EXISTS service_extent GEOMETRY;
                ALTER TABLE honua.services
                    ADD COLUMN IF NOT EXISTS metadata JSONB;
                ALTER TABLE honua.services
                    ADD COLUMN IF NOT EXISTS connection_id UUID;
                CREATE TABLE IF NOT EXISTS honua.migration_data_sources (
                    source_kind     VARCHAR(64)  NOT NULL,
                    source_id       VARCHAR(256) NOT NULL,
                    data_source_type VARCHAR(64) NOT NULL,
                    workspace_name  VARCHAR(128),
                    display_name    TEXT NOT NULL DEFAULT '',
                    connection_summary TEXT NOT NULL DEFAULT '',
                    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    PRIMARY KEY (source_kind, source_id)
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Seed the source roads table in the isolated schema. The GeoServer
        /// fixture's PostGIS datastore declares schema=public; we override that
        /// via the override schema/table the service resolves so the copy reads
        /// from our isolated schema.
        /// </summary>
        private async Task SetUpSourceRoadsTableAsync(string schemaName)
        {
            await using var conn = await fixture.GetConnectionAsync(schemaName);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {schemaName}.roads (
                    objectid SERIAL PRIMARY KEY,
                    road_name TEXT NOT NULL,
                    geom geometry(LineString, 4326)
                );
                INSERT INTO {schemaName}.roads (road_name, geom) VALUES
                    ('Kalanianaole Hwy', ST_GeomFromText('LINESTRING(-157.7 21.3, -157.6 21.4)', 4326)),
                    ('H1 Freeway',        ST_GeomFromText('LINESTRING(-157.9 21.3, -157.8 21.3)', 4326)),
                    ('Pali Hwy',          ST_GeomFromText('LINESTRING(-157.8 21.3, -157.8 21.4)', 4326));
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Build a GeoServer import service whose connection provider uses the
        /// Testcontainers connection string and a recording publisher that does
        /// not need a fully-migrated honua.layers schema.
        /// </summary>
        private static GeoServerImportService CreateServiceForFixture(
            HttpMessageHandler handler,
            string connectionString,
            string schemaOverride)
        {
            var httpClient = new HttpClient(handler);
            var restClient = new GeoServerRestClient(
                httpClient,
                NullLogger<GeoServerRestClient>.Instance,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
            connectionProvider.Setup(provider => provider.GetConnectionString())
                .Returns(connectionString);

            var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);
            crsRegistry.Setup(registry => registry.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int srid, CancellationToken _) => new ValueTask<CrsDefinition?>(
                    srid switch
                    {
                        3857 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false),
                        4326 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/4326", 4326, AxisOrder.EastNorth, true),
                        _ => null
                    }));

            var catalogWriter = new PostgresMigrationCatalogWriter(
                NullLogger<PostgresMigrationCatalogWriter>.Instance);

            return new GeoServerImportService(
                restClient,
                connectionProvider.Object,
                crsRegistry.Object,
                NullLogger<GeoServerImportService>.Instance,
                layerPublishingService: new SchemaRedirectingLayerPublisher(schemaOverride),
                catalogWriter: catalogWriter);
        }
    }

    private static FixtureScenario LoadFixture(string scenario)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "Import",
            "Fixtures",
            "GeoServer",
            $"{scenario}.json");

        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = document.RootElement;
        var serviceUrl = root.GetProperty("serviceUrl").GetString()
            ?? throw new InvalidDataException($"Fixture {scenario} is missing serviceUrl.");
        var responses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("responses").EnumerateObject())
        {
            responses[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString() ?? string.Empty
                : entry.Value.GetRawText();
        }

        return new FixtureScenario(serviceUrl, responses);
    }

    private static FixtureScenario WithDataStoreSchema(FixtureScenario fixture, string schemaName)
    {
        const string dataStorePath = "/geoserver/rest/workspaces/ops/datastores/pg.json";
        var responses = fixture.Responses.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value,
            StringComparer.Ordinal);
        responses[dataStorePath] = AddSchemaConnectionParameter(responses[dataStorePath], schemaName);
        return fixture with { Responses = responses };
    }

    private static string AddSchemaConnectionParameter(string dataStoreJson, string schemaName)
    {
        var document = JsonNode.Parse(dataStoreJson)?.AsObject()
            ?? throw new InvalidDataException("Datastore fixture response must be a JSON object.");
        var entries = document["dataStore"]?["connectionParameters"]?["entry"]?.AsArray()
            ?? throw new InvalidDataException("Datastore fixture response is missing connectionParameters.entry.");

        foreach (var entry in entries.OfType<JsonObject>())
        {
            if (string.Equals(entry["@key"]?.GetValue<string>(), "schema", StringComparison.Ordinal))
            {
                entry["$"] = schemaName;
                return document.ToJsonString();
            }
        }

        entries.Add(new JsonObject
        {
            ["@key"] = "schema",
            ["$"] = schemaName
        });
        return document.ToJsonString();
    }

    private static GeoServerImportService CreateService(
        HttpMessageHandler handler,
        ILayerPublishingService? layerPublishingService = null,
        IMigrationCatalogWriter? catalogWriter = null)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

        if (layerPublishingService != null || catalogWriter != null)
        {
            connectionProvider.Setup(provider => provider.GetConnectionString())
                .Returns("Host=localhost;Database=honua");
        }

        crsRegistry.Setup(registry => registry.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int srid, CancellationToken _) => new ValueTask<CrsDefinition?>(
                srid switch
                {
                    3857 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false),
                    4326 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/4326", 4326, AxisOrder.EastNorth, true),
                    _ => null
                }));

        return new GeoServerImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            NullLogger<GeoServerImportService>.Instance,
            layerPublishingService: layerPublishingService,
            catalogWriter: catalogWriter);
    }

    private sealed record FixtureScenario(string ServiceUrl, IReadOnlyDictionary<string, string> Responses);

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public FixtureHttpHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (!_responses.TryGetValue(pathAndQuery, out var body))
            {
                throw new InvalidOperationException(
                    $"Fixture has no response for {pathAndQuery}. Add it to the fixture JSON or correct the request path.");
            }

            var contentType = pathAndQuery.EndsWith(".xml", StringComparison.Ordinal)
                ? "application/xml"
                : pathAndQuery.EndsWith(".sld", StringComparison.Ordinal)
                    ? "application/vnd.ogc.sld+xml"
                    : "application/json";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }

    private sealed class RecordingLayerPublishingService : ILayerPublishingService
    {
        public List<LayerPublishRequest> Requests { get; } = [];

        public Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<PublishedLayerSummary> PublishLayerAsync(
            string connectionString,
            LayerPublishRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new PublishedLayerSummary
            {
                LayerId = 100 + Requests.Count - 1,
                LayerName = request.LayerName ?? request.Table,
                Schema = request.Schema,
                Table = request.Table,
                Description = request.Description,
                GeometryType = request.GeometryType ?? "LineString",
                Srid = request.Srid ?? 4326,
                PrimaryKey = request.PrimaryKey,
                FieldCount = 0,
                Enabled = request.Enabled,
                ServiceName = request.ServiceName ?? "default"
            });
        }

        public Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<TablePublishValidationResult> ValidateTableForPublishAsync(
            string connectionString,
            TablePublishValidationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = request.Schema,
                Table = request.Table,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> SetLayerEnabledAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
            string connectionString,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<LayerExtentRefreshResult?>(null);
    }

    /// <summary>
    /// For the Testcontainers integration test, redirects layer-publish calls to
    /// a no-op (we exercise the data-source apply and feature-copy paths, not
    /// the full honua.layers publisher). The publisher returns success so the
    /// step result is "applied".
    /// </summary>
    private sealed class SchemaRedirectingLayerPublisher : ILayerPublishingService
    {
        private readonly string _schemaOverride;

        public SchemaRedirectingLayerPublisher(string schemaOverride)
        {
            _schemaOverride = schemaOverride;
        }

        public Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<PublishedLayerSummary> PublishLayerAsync(
            string connectionString,
            LayerPublishRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PublishedLayerSummary
            {
                LayerId = 1,
                LayerName = request.LayerName ?? request.Table,
                Schema = request.Schema,
                Table = request.Table,
                GeometryType = "LineString",
                Srid = request.Srid ?? 4326,
                FieldCount = 0,
                Enabled = request.Enabled,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<TablePublishValidationResult> ValidateTableForPublishAsync(
            string connectionString,
            TablePublishValidationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = request.Schema,
                Table = request.Table,
                ServiceName = request.ServiceName ?? "default"
            });

        public Task<PublishedLayerSummary?> SetLayerEnabledAsync(
            string connectionString,
            int layerId,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PublishedLayerSummary?>(null);

        public Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
            string connectionString,
            string serviceName,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>([]);

        public Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
            string connectionString,
            string serviceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<LayerExtentRefreshResult?>(null);
    }

    private sealed class RecordingMigrationCatalogWriter : IMigrationCatalogWriter
    {
        private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _existingDataSources = new(StringComparer.OrdinalIgnoreCase);

        public List<MigrationCatalogServiceRequest> Requests { get; } = [];

        public List<MigrationDataSourceRequest> DataSourceRequests { get; } = [];

        public List<MigrationFeatureCopyRequest> FeatureCopyRequests { get; } = [];

        public Func<string, MigrationFeatureCopyRequest, MigrationFeatureCopyOutcome>? FeatureCopyResultFactory { get; init; }

        public Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
            string connectionString,
            MigrationCatalogServiceRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var outcome = _existing.Add(request.ServiceName)
                ? MigrationCatalogWriteOutcome.Created
                : MigrationCatalogWriteOutcome.AlreadyExists;
            return Task.FromResult(outcome);
        }

        public Task<MigrationCatalogWriteOutcome> EnsureDataSourceAsync(
            string connectionString,
            MigrationDataSourceRequest request,
            CancellationToken cancellationToken = default)
        {
            DataSourceRequests.Add(request);
            var key = $"{request.SourceKind}:{request.SourceId}";
            var outcome = _existingDataSources.Add(key)
                ? MigrationCatalogWriteOutcome.Created
                : MigrationCatalogWriteOutcome.AlreadyExists;
            return Task.FromResult(outcome);
        }

        public Task<MigrationFeatureCopyOutcome> CopyFeatureDataAsync(
            string connectionString,
            MigrationFeatureCopyRequest request,
            CancellationToken cancellationToken = default)
        {
            FeatureCopyRequests.Add(request);
            if (FeatureCopyResultFactory != null)
            {
                return Task.FromResult(FeatureCopyResultFactory(connectionString, request));
            }
            return Task.FromResult(new MigrationFeatureCopyOutcome
            {
                Status = MigrationFeatureCopyStatus.SourceMissing,
                RowCount = 0
            });
        }

        // Slice 3 (#1015): the data-source apply tests do not exercise styles,
        // but the interface now requires EnsureStyleAsync. Record-and-noop so
        // slice 2 fixtures with style entries do not throw.
        public Task<MigrationCatalogWriteOutcome> EnsureStyleAsync(
            string connectionString,
            MigrationStyleRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(MigrationCatalogWriteOutcome.Created);
    }
}
