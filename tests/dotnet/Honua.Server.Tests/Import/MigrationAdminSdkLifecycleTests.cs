// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Migration;
using Honua.Sdk.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SdkModels = Honua.Sdk.Admin.Models;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Issue #4600, acceptance criterion 7 (SDK-driven half): the service import lifecycle is driven through the
/// published <c>Honua.Sdk.Admin</c> package a customer installs, against the real server host and PostGIS.
/// The SDK scans the source, starts the service (batch) migration, follows the batch and each layer's import
/// job to a terminal state, and the imported rows are then read back from PostGIS. Expected values come from
/// the fake FeatureServer's own records, never from the server's output.
/// </summary>
/// <remarks>
/// The published 1.7.0 models predate the server's fidelity fields, so three reads go over the wire with the same
/// authenticated client: the scan manifest (<c>artifactSet=all</c> is not on
/// <see cref="SdkModels.MigrationInventoryScanRequest"/>), the batch verdict and construct accounting (not on
/// <see cref="SdkModels.MigrationBatchResponse"/>), and each layer job's verdict and differences (not on
/// <see cref="SdkModels.GeoservicesImportProgress"/>).
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class MigrationAdminSdkLifecycleTests : IClassFixture<MigrationAdminSdkLifecycleTests.SdkMigrationHost>
{
    private const string SourceKind = "arcgis-geoservices-rest";
    private const string HydrantsId = "resource:FieldOps:layer:0";
    private const string InspectionsId = "resource:FieldOps:table:1";
    private const string TargetServiceName = "field_ops";
    private static readonly TimeSpan _lifecycleTimeout = TimeSpan.FromMinutes(3);

    private readonly SdkMigrationHost _host;

    public MigrationAdminSdkLifecycleTests(SdkMigrationHost host) => _host = host;

    /// <summary>
    /// Full selection of a service with a Z point layer and a nonspatial table. Every source record lands in
    /// PostGIS, and the verdict names exactly what the migration could not deliver instead of reporting success:
    /// the table imports but a table without a geometry column cannot be published yet (#4834), so it is
    /// incomplete; the layer publishes and its records reconcile, but this host has no activated Metadata v2
    /// snapshot for catalog reconciliation to read back, so the layer is unverified.
    /// </summary>
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    [Endpoint("POST /api/v1/admin/import/migrations")]
    [Endpoint("GET /api/v1/admin/import/migrations/{batchId}")]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task ServiceMigration_DrivenThroughThePublishedAdminSdk_LandsEveryRecordAndReportsWhatItCouldNotPublishOrVerify()
    {
        using var http = _host.Web.CreateAdminClient();
        var sdk = new HonuaAdminClient(http);
        var schema = _host.Schema;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hydrantsTable = $"hydrants_{suffix}";
        var inspectionsTable = $"hydrant_inspections_{suffix}";

        var inventory = await sdk.ScanMigrationSourceAsync(new SdkModels.MigrationInventoryScanRequest
        {
            SourceKind = SourceKind,
            SourceUrl = FieldOpsFeatureServer.ServiceUrl,
            TimeoutSeconds = 30
        });

        inventory.Resources.Select(static resource => (resource.Id, resource.FeatureCount))
            .Should().BeEquivalentTo(
                [(HydrantsId, (int?)FieldOpsFeatureServer.Hydrants.Length), (InspectionsId, (int?)FieldOpsFeatureServer.Inspections.Length)],
                "the source lists one point layer and one nonspatial table. {0}",
                _host.Source.Describe());

        var started = await sdk.StartMigrationBatchAsync(new SdkModels.MigrationBatchStartRequest
        {
            SourceKind = SourceKind,
            SourceUrl = FieldOpsFeatureServer.ServiceUrl,
            SourceDisplayName = "Field operations",
            ManifestBody = await ScanManifestBodyAsync(http),
            Layers =
            [
                Layer(HydrantsId, 0, hydrantsTable, schema),
                Layer(InspectionsId, 1, inspectionsTable, schema, dependsOn: [HydrantsId])
            ]
        });
        started.TotalChildren.Should().Be(2);

        var batch = await WaitForBatchAsync(sdk, started.BatchId);

        batch.Status.Should().Be("needs-review", _host.Source.Describe());
        batch.Children.Select(static child => (child.SourceResourceId, child.Status))
            .Should().Equal((HydrantsId, "succeeded"), (InspectionsId, "needs-review"));

        var hydrantsJob = await WaitForChildJobAsync(sdk, batch, HydrantsId, SdkModels.GeoservicesImportStatus.Completed);
        hydrantsJob.FeaturesProcessed.Should().Be(FieldOpsFeatureServer.Hydrants.Length);
        hydrantsJob.FailedFeatures.Should().Be(0);
        hydrantsJob.TableName.Should().Be(hydrantsTable);
        hydrantsJob.PublishedLayerId.Should().NotBeNull("the point layer is published into the target service");
        var hydrantsCount = hydrantsJob.ReconciliationArtifact.Should().NotBeNull().And.Subject
            .As<SdkModels.MigrationReconciliationArtifact>().Layers.Should().ContainSingle().Subject.Count;
        hydrantsCount.SourceCount.Should().Be(FieldOpsFeatureServer.Hydrants.Length);
        hydrantsCount.TargetCount.Should().Be(FieldOpsFeatureServer.Hydrants.Length);
        var hydrantsFidelity = await GetJobFidelityAsync(http, hydrantsJob.JobId);
        hydrantsFidelity.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        hydrantsFidelity.Differences.Should().Equal(
            (MigrationFidelityDifferenceCodes.CatalogReconciliationNotExecuted, MigrationFidelityDifferenceSeverities.Unverified));

        var inspectionsJob = await WaitForChildJobAsync(sdk, batch, InspectionsId, SdkModels.GeoservicesImportStatus.NeedsReview);
        inspectionsJob.FeaturesProcessed.Should().Be(FieldOpsFeatureServer.Inspections.Length);
        inspectionsJob.FailedFeatures.Should().Be(0);
        inspectionsJob.PublishedLayerId.Should().BeNull("a table without a geometry column cannot be published yet (#4834)");
        var inspectionsFidelity = await GetJobFidelityAsync(http, inspectionsJob.JobId);
        inspectionsFidelity.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);
        inspectionsFidelity.Differences.Should().Equal(
            (MigrationFidelityDifferenceCodes.PublishNotCompleted, MigrationFidelityDifferenceSeverities.Blocking));

        await AssertHydrantsReadBackAsync(schema, hydrantsTable);
        await AssertInspectionsReadBackAsync(schema, inspectionsTable);

        using var verdict = await GetBatchDocumentAsync(http, batch.BatchId);
        var accounting = verdict.RootElement.GetProperty("constructAccounting");
        accounting.GetProperty("executed").GetBoolean().Should().BeTrue();
        accounting.GetProperty("isBlocking").GetBoolean().Should().BeFalse(verdict.RootElement.GetRawText());
        accounting.GetProperty("discoveredResourceCount").GetInt32().Should().Be(2);
        accounting.GetProperty("selectedResourceCount").GetInt32().Should().Be(2);
        accounting.GetProperty("entries").EnumerateArray().Should().Contain(entry =>
            entry.GetProperty("sourceId").GetString() == InspectionsId &&
            entry.GetProperty("construct").GetString() == "service.resource" &&
            entry.GetProperty("disposition").GetString() == MigrationConstructDispositions.Migrated &&
            entry.GetProperty("targetTable").GetString() == $"{schema}.{inspectionsTable}");
        verdict.RootElement.GetProperty("fidelityVerdict").GetString()
            .Should().Be(MigrationFidelityVerdicts.Incomplete, verdict.RootElement.GetRawText());
        verdict.RootElement.GetProperty("fidelityDifferences").EnumerateArray()
            .Select(static difference => (
                difference.GetProperty("code").GetString(),
                difference.GetProperty("severity").GetString(),
                difference.GetProperty("subject").GetString()))
            .Should().Equal(
                (MigrationFidelityDifferenceCodes.ServiceLayerIncomplete, MigrationFidelityDifferenceSeverities.Blocking, InspectionsId),
                (MigrationFidelityDifferenceCodes.ServiceLayerUnverified, MigrationFidelityDifferenceSeverities.Unverified, HydrantsId));
    }

    /// <summary>
    /// A selection that leaves a discovered table out finishes without importing it and without claiming
    /// success: the batch routes to review and the service verdict is incomplete on the unselected table.
    /// </summary>
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/scan")]
    [Endpoint("POST /api/v1/admin/import/migrations")]
    [Endpoint("GET /api/v1/admin/import/migrations/{batchId}")]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task ServiceMigration_DrivenThroughThePublishedAdminSdkWithATableLeftOut_RoutesToReviewAsIncomplete()
    {
        using var http = _host.Web.CreateAdminClient();
        var sdk = new HonuaAdminClient(http);
        var schema = _host.Schema;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hydrantsTable = $"hydrants_{suffix}";

        var started = await sdk.StartMigrationBatchAsync(new SdkModels.MigrationBatchStartRequest
        {
            SourceKind = SourceKind,
            SourceUrl = FieldOpsFeatureServer.ServiceUrl,
            ManifestBody = await ScanManifestBodyAsync(http),
            Layers = [Layer(HydrantsId, 0, hydrantsTable, schema)]
        });

        var batch = await WaitForBatchAsync(sdk, started.BatchId);

        batch.Status.Should().Be("needs-review", _host.Source.Describe());
        batch.Children.Should().ContainSingle().Which.Status.Should().Be("succeeded");
        var hydrantsJob = await WaitForChildJobAsync(sdk, batch, HydrantsId);
        hydrantsJob.FeaturesProcessed.Should().Be(FieldOpsFeatureServer.Hydrants.Length);
        await AssertHydrantsReadBackAsync(schema, hydrantsTable);

        using var verdict = await GetBatchDocumentAsync(http, batch.BatchId);
        verdict.RootElement.GetProperty("fidelityVerdict").GetString()
            .Should().Be(MigrationFidelityVerdicts.Incomplete, verdict.RootElement.GetRawText());
        verdict.RootElement.GetProperty("fidelityDifferences").EnumerateArray()
            .Should().ContainSingle(difference => difference.GetProperty("severity").GetString() == MigrationFidelityDifferenceSeverities.Blocking)
            .Which.Should().Match<JsonElement>(difference =>
                difference.GetProperty("code").GetString() == MigrationFidelityDifferenceCodes.ServiceResourceUnselected &&
                difference.GetProperty("subject").GetString() == InspectionsId);
        verdict.RootElement.GetProperty("constructAccounting").GetProperty("isBlocking").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// The single-layer lifecycle through the SDK: start, wait, and the same rows read back from PostGIS.
    /// </summary>
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    public async Task LayerImport_DrivenThroughThePublishedAdminSdk_PreservesRecordsOrdinatesAndNulls()
    {
        using var http = _host.Web.CreateAdminClient();
        var sdk = new HonuaAdminClient(http);
        var schema = _host.Schema;
        var hydrantsTable = $"hydrants_single_{Guid.NewGuid().ToString("N")[..8]}";

        var job = await sdk.StartGeoservicesImportAsync(new SdkModels.GeoservicesStartImportRequest
        {
            ServiceUrl = FieldOpsFeatureServer.ServiceUrl,
            LayerId = 0,
            TableName = hydrantsTable,
            TargetSchema = schema,
            AutoPublish = false
        });

        var progress = await sdk.WaitForGeoservicesImportJobAsync(job.JobId, TimeSpan.FromMilliseconds(500), _lifecycleTimeout);

        progress.Status.Should().Be(SdkModels.GeoservicesImportStatus.Completed, $"{progress.ErrorMessage} {_host.Source.Describe()}");
        progress.FeaturesProcessed.Should().Be(FieldOpsFeatureServer.Hydrants.Length);
        progress.SourceLayerId.Should().Be(0);
        progress.TableName.Should().Be(hydrantsTable);
        await AssertHydrantsReadBackAsync(schema, hydrantsTable);
    }

    private static SdkModels.MigrationBatchLayerSpec Layer(
        string sourceResourceId,
        int layerId,
        string tableName,
        string schema,
        IReadOnlyList<string>? dependsOn = null) => new()
        {
            SourceResourceId = sourceResourceId,
            ServiceUrl = FieldOpsFeatureServer.ServiceUrl,
            LayerId = layerId,
            TableName = tableName,
            TargetSchema = schema,
            ServiceName = TargetServiceName,
            DependsOn = dependsOn
        };

    private async Task<SdkModels.MigrationBatchResponse> WaitForBatchAsync(HonuaAdminClient sdk, string batchId)
    {
        var deadline = DateTimeOffset.UtcNow + _lifecycleTimeout;
        while (true)
        {
            var batch = await sdk.GetMigrationBatchAsync(batchId);
            if (!string.Equals(batch.Status, "running", StringComparison.Ordinal))
            {
                return batch;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Batch {batchId} still running after {_lifecycleTimeout}: " +
                    string.Join(", ", batch.Children.Select(static child => $"{child.SourceResourceId}={child.Status} {child.StatusNote}")) +
                    $". {_host.Source.Describe()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    private static async Task<SdkModels.GeoservicesImportProgress> WaitForChildJobAsync(
        HonuaAdminClient sdk,
        SdkModels.MigrationBatchResponse batch,
        string sourceResourceId,
        SdkModels.GeoservicesImportStatus expectedStatus = SdkModels.GeoservicesImportStatus.Completed)
    {
        var child = batch.Children.Single(child => child.SourceResourceId == sourceResourceId);
        child.JobId.Should().NotBeNullOrWhiteSpace();
        var progress = await sdk.WaitForGeoservicesImportJobAsync(child.JobId!, TimeSpan.FromMilliseconds(500), _lifecycleTimeout);
        progress.Status.Should().Be(expectedStatus, $"{progress.ErrorMessage} {string.Join(" ", progress.Warnings)}");
        progress.SourceLayerId.Should().Be(child.SourceLayerId);
        return progress;
    }

    /// <summary>
    /// A layer job's verdict and its (code, severity) differences. Read over the wire because the published 1.7.0
    /// <see cref="SdkModels.GeoservicesImportProgress"/> has no fidelity members.
    /// </summary>
    private static async Task<(string? Verdict, (string? Code, string? Severity)[] Differences)> GetJobFidelityAsync(
        HttpClient http,
        string jobId)
    {
        using var response = await http.GetAsync($"/api/v1/admin/import/geoservices/jobs/{Uri.EscapeDataString(jobId)}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var verdict = root.TryGetProperty("fidelityVerdict", out var verdictElement) ? verdictElement.GetString() : null;
        var differences = root.TryGetProperty("fidelityDifferences", out var differencesElement)
            ? differencesElement.EnumerateArray()
                .Select(static difference => (difference.GetProperty("code").GetString(), difference.GetProperty("severity").GetString()))
                .ToArray()
            : [];
        return (verdict, differences);
    }

    /// <summary>
    /// The scan manifest the batch accounts its selection against. Requested over the wire because the
    /// published 1.7.0 scan request has no <c>artifactSet</c>.
    /// </summary>
    private static async Task<string> ScanManifestBodyAsync(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/v1/admin/import/scan",
            new { sourceKind = SourceKind, sourceUrl = FieldOpsFeatureServer.ServiceUrl, timeoutSeconds = 30, artifactSet = "all" });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("manifest").GetRawText();
    }

    private static async Task<JsonDocument> GetBatchDocumentAsync(HttpClient http, string batchId)
    {
        using var response = await http.GetAsync($"/api/v1/admin/import/migrations/{Uri.EscapeDataString(batchId)}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private async Task AssertHydrantsReadBackAsync(string schema, string table)
    {
        await using var connection = await _host.Web.Postgres.GetConnectionAsync(schema);

        await using (var geometryColumn = connection.CreateCommand())
        {
            geometryColumn.CommandText =
                "SELECT f_geometry_column, coord_dimension, srid, type FROM geometry_columns WHERE f_table_schema = @schema AND f_table_name = @table";
            geometryColumn.Parameters.AddWithValue("schema", schema);
            geometryColumn.Parameters.AddWithValue("table", table);
            await using var reader = await geometryColumn.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue($"{schema}.{table} has a registered geometry column");
            var columnName = reader.GetString(0);
            reader.GetInt32(1).Should().Be(3, "the source layer advertises hasZ and every record carries z");
            reader.GetInt32(2).Should().Be(4326);
            reader.GetString(3).Should().BeOneOf("POINTZ", "POINT", "GEOMETRY");
            await reader.CloseAsync();

            await using var rowsCommand = connection.CreateCommand();
            rowsCommand.CommandText =
                $"SELECT row_to_json(t)::text, ST_X(t.\"{columnName}\"), ST_Y(t.\"{columnName}\"), ST_Z(t.\"{columnName}\") FROM \"{schema}\".\"{table}\" t";
            var rows = new List<(JsonObject Attributes, double X, double Y, double Z)>();
            await using (var rowReader = await rowsCommand.ExecuteReaderAsync())
            {
                while (await rowReader.ReadAsync())
                {
                    rows.Add((
                        JsonNode.Parse(rowReader.GetString(0))!.AsObject(),
                        rowReader.GetDouble(1),
                        rowReader.GetDouble(2),
                        rowReader.GetDouble(3)));
                }
            }

            rows.Should().HaveCount(FieldOpsFeatureServer.Hydrants.Length);
            foreach (var expected in FieldOpsFeatureServer.Hydrants)
            {
                var row = rows.Where(row => Attribute(row.Attributes, "NAME")?.GetValue<string>() == expected.Name)
                    .Should().ContainSingle($"source hydrant {expected.Name} is imported exactly once. Row keys: {string.Join(",", rows[0].Attributes.Select(static pair => pair.Key))}")
                    .Subject;
                row.X.Should().BeApproximately(expected.X, 1e-9);
                row.Y.Should().BeApproximately(expected.Y, 1e-9);
                row.Z.Should().BeApproximately(expected.Z, 1e-9);
                var pressure = Attribute(row.Attributes, "PRESSURE_PSI");
                if (expected.PressurePsi is null)
                {
                    pressure.Should().BeNull("a null source attribute stays null, not zero");
                }
                else
                {
                    pressure!.GetValue<double>().Should().Be(expected.PressurePsi.Value);
                }
            }
        }
    }

    private async Task AssertInspectionsReadBackAsync(string schema, string table)
    {
        await using var connection = await _host.Web.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT row_to_json(t)::text FROM \"{schema}\".\"{table}\" t";
        var rows = new List<JsonObject>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(JsonNode.Parse(reader.GetString(0))!.AsObject());
            }
        }

        rows.Select(row => (Attribute(row, "HYDRANT_ID")!.GetValue<int>(), Attribute(row, "RESULT")!.GetValue<string>()))
            .Should().BeEquivalentTo(FieldOpsFeatureServer.Inspections.Select(static inspection => (inspection.HydrantId, inspection.Result)));
    }

    private static JsonNode? Attribute(JsonObject row, string sourceField)
        => row.FirstOrDefault(pair => string.Equals(pair.Key, sourceField, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// The real server host over PostGIS, with the ArcGIS REST client pointed at the in-process
    /// <see cref="FieldOpsFeatureServer"/> and the batch poller the WebAppFixture normally strips restored, so
    /// batches advance exactly as they do in a deployed server.
    /// </summary>
    public sealed class SdkMigrationHost : IAsyncLifetime
    {
        public SdkMigrationHost()
        {
            var source = Source;
            Web = new WebAppFixture()
                .WithTestLicense(HonuaEdition.Enterprise)
                .ConfigureServices(services =>
                {
                    services.AddHostedService<MigrationBatchBackgroundService>();
                    services.AddHttpClient<ArcGisRestClient>()
                        .ConfigurePrimaryHttpMessageHandler(() => new FieldOpsFeatureServerHandler(source));
                    services.AddTransient(serviceProvider => new ArcGisRestClient(
                        serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ArcGisRestClient)),
                        serviceProvider.GetRequiredService<ILogger<ArcGisRestClient>>(),
                        static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })));
                })
                // Auto-publish opens its own connection from the provider's connection string. A deployed
                // server's connection string reaches the catalog; the fixture's catalog lives in its
                // isolated schema, which the base connection string does not search.
                .DecorateService<IAdoNetDatabaseConnectionProvider>(inner =>
                    new FixtureSchemaConnectionProvider(inner, () => Web?.CurrentSchema));
        }

        public FieldOpsFeatureServer Source { get; } = new();

        public WebAppFixture Web { get; }

        public string Schema => Web.CurrentSchema ?? throw new InvalidOperationException("The fixture has no schema.");

        public async Task InitializeAsync()
        {
            await Web.InitializeAsync();

            // Test hosts skip application migrations. The batch catalog is the real Postgres one here, so
            // apply the production batch-run DDL (045, and 117 for the fidelity verdict columns) under the
            // shared seed lock: the tables are literal honua.* objects that search_path does not isolate.
            var assembly = typeof(Program).Assembly;
            foreach (var script in new[] { "045_CreateMigrationBatchRuns.sql", "117_AddMigrationBatchFidelityVerdict.sql" })
            {
                var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(script, StringComparison.Ordinal));
                await using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                await Web.Postgres.ApplyGlobalSeedSqlAsync("CREATE SCHEMA IF NOT EXISTS honua;\n" + await reader.ReadToEndAsync());
            }
        }

        public Task DisposeAsync() => Web.DisposeAsync();
    }

    /// <summary>A hydrant record served by the fake FeatureServer.</summary>
    public sealed record Hydrant(long ObjectId, string Name, double? PressurePsi, double X, double Y, double Z);

    /// <summary>An inspection row served from the fake FeatureServer's nonspatial table.</summary>
    public sealed record Inspection(long ObjectId, int HydrantId, string Result);

    /// <summary>
    /// A FeatureServer with one Z-enabled point layer (three records over two pages, one null attribute) and
    /// one nonspatial table, answering the ArcGIS REST requests an import makes. Every request is recorded so a
    /// failure names what the server actually asked for.
    /// </summary>
    public sealed class FieldOpsFeatureServer
    {
        public const string ServiceUrl = "https://example.com/arcgis/rest/services/FieldOps/FeatureServer";
        private const string ServicePath = "/arcgis/rest/services/FieldOps/FeatureServer";
        private const int MaxRecordCount = 2;

        public static readonly Hydrant[] Hydrants =
        [
            new(1, "H-001", 62.5, -157.8583, 21.3069, 4.25),
            new(2, "H-002", null, -157.8295, 21.2810, 12.5),
            new(3, "H-003", 48.0, -158.0001, 21.4389, 0.75)
        ];

        public static readonly Inspection[] Inspections =
        [
            new(1, 1, "pass"),
            new(2, 3, "fail")
        ];

        private readonly ConcurrentQueue<string> _requests = new();
        private readonly ConcurrentQueue<string> _unanswered = new();

        public string Describe()
            => $"Source requests: [{string.Join(" | ", _requests)}]; unanswered: [{string.Join(" | ", _unanswered)}]";

        public (HttpStatusCode Status, string Body) Answer(Uri uri)
        {
            _requests.Enqueue(uri.PathAndQuery);
            var query = QueryHelpers.ParseQuery(uri.Query);
            var path = uri.AbsolutePath;
            var answer = path switch
            {
                ServicePath => ServiceRoot(),
                ServicePath + "/0" => HydrantsLayer(),
                ServicePath + "/1" => InspectionsTable(),
                ServicePath + "/0/query" => Query(query, Hydrants.Select(HydrantFeature).ToArray(), Hydrants.Select(static h => h.ObjectId).ToArray(), spatial: true),
                ServicePath + "/1/query" => Query(query, Inspections.Select(InspectionFeature).ToArray(), Inspections.Select(static i => i.ObjectId).ToArray(), spatial: false),
                _ => null
            };

            if (answer is null)
            {
                _unanswered.Enqueue(uri.PathAndQuery);
                return (HttpStatusCode.OK, """{"error":{"code":400,"message":"Unsupported request for the FieldOps fixture."}}""");
            }

            return (HttpStatusCode.OK, answer.ToJsonString());
        }

        private static JsonObject ServiceRoot() => new()
        {
            ["currentVersion"] = 11.2,
            ["serviceDescription"] = "Field operations",
            ["capabilities"] = "Query",
            ["maxRecordCount"] = MaxRecordCount,
            ["spatialReference"] = new JsonObject { ["wkid"] = 4326 },
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 0, ["name"] = "Hydrants", ["geometryType"] = "esriGeometryPoint" }),
            ["tables"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "HydrantInspections" })
        };

        private static JsonObject HydrantsLayer() => new()
        {
            ["id"] = 0,
            ["name"] = "Hydrants",
            ["type"] = "Feature Layer",
            ["geometryType"] = "esriGeometryPoint",
            ["hasZ"] = true,
            ["hasM"] = false,
            ["capabilities"] = "Query",
            ["maxRecordCount"] = MaxRecordCount,
            ["objectIdField"] = "OBJECTID",
            ["spatialReference"] = new JsonObject { ["wkid"] = 4326 },
            ["extent"] = new JsonObject
            {
                ["xmin"] = Hydrants.Min(static h => h.X),
                ["ymin"] = Hydrants.Min(static h => h.Y),
                ["xmax"] = Hydrants.Max(static h => h.X),
                ["ymax"] = Hydrants.Max(static h => h.Y),
                ["spatialReference"] = new JsonObject { ["wkid"] = 4326 }
            },
            ["fields"] = HydrantFields()
        };

        private static JsonObject InspectionsTable() => new()
        {
            ["id"] = 1,
            ["name"] = "HydrantInspections",
            ["type"] = "Table",
            ["capabilities"] = "Query",
            ["maxRecordCount"] = MaxRecordCount,
            ["objectIdField"] = "OBJECTID",
            ["fields"] = InspectionFields()
        };

        private static JsonArray HydrantFields() => new(
            Field("OBJECTID", "esriFieldTypeOID", nullable: false),
            Field("NAME", "esriFieldTypeString", nullable: false, length: 16),
            Field("PRESSURE_PSI", "esriFieldTypeDouble", nullable: true));

        private static JsonArray InspectionFields() => new(
            Field("OBJECTID", "esriFieldTypeOID", nullable: false),
            Field("HYDRANT_ID", "esriFieldTypeInteger", nullable: false),
            Field("RESULT", "esriFieldTypeString", nullable: false, length: 8));

        private static JsonObject Field(string name, string type, bool nullable, int? length = null)
        {
            var field = new JsonObject { ["name"] = name, ["alias"] = name, ["type"] = type, ["nullable"] = nullable };
            if (length is not null)
            {
                field["length"] = length;
            }

            return field;
        }

        private static JsonObject HydrantFeature(Hydrant hydrant) => new()
        {
            ["attributes"] = new JsonObject
            {
                ["OBJECTID"] = hydrant.ObjectId,
                ["NAME"] = hydrant.Name,
                ["PRESSURE_PSI"] = hydrant.PressurePsi
            },
            ["geometry"] = new JsonObject { ["x"] = hydrant.X, ["y"] = hydrant.Y, ["z"] = hydrant.Z }
        };

        private static JsonObject InspectionFeature(Inspection inspection) => new()
        {
            ["attributes"] = new JsonObject
            {
                ["OBJECTID"] = inspection.ObjectId,
                ["HYDRANT_ID"] = inspection.HydrantId,
                ["RESULT"] = inspection.Result
            }
        };

        private static JsonObject? Query(
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query,
            JsonObject[] features,
            long[] objectIds,
            bool spatial)
        {
            var where = query.TryGetValue("where", out var whereValue) ? whereValue.ToString() : "1=1";
            if (!string.Equals(where, "1=1", StringComparison.Ordinal))
            {
                return null;
            }

            if (IsTrue(query, "returnCountOnly"))
            {
                return new JsonObject { ["count"] = features.Length };
            }

            if (IsTrue(query, "returnIdsOnly"))
            {
                return new JsonObject
                {
                    ["objectIdFieldName"] = "OBJECTID",
                    ["objectIds"] = new JsonArray(objectIds.Select(static id => (JsonNode)id).ToArray())
                };
            }

            IEnumerable<int> selected;
            var exceeded = false;
            if (query.TryGetValue("objectIds", out var requestedIds))
            {
                var wanted = requestedIds.ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static id => long.Parse(id, CultureInfo.InvariantCulture))
                    .ToHashSet();
                selected = Enumerable.Range(0, features.Length).Where(index => wanted.Contains(objectIds[index]));
            }
            else if (query.TryGetValue("resultOffset", out var offsetValue))
            {
                var offset = int.Parse(offsetValue.ToString(), CultureInfo.InvariantCulture);
                var count = query.TryGetValue("resultRecordCount", out var countValue)
                    ? Math.Min(int.Parse(countValue.ToString(), CultureInfo.InvariantCulture), MaxRecordCount)
                    : MaxRecordCount;
                selected = Enumerable.Range(offset, Math.Max(0, Math.Min(count, features.Length - offset)));
                exceeded = offset + count < features.Length;
            }
            else
            {
                selected = Enumerable.Range(0, Math.Min(MaxRecordCount, features.Length));
                exceeded = features.Length > MaxRecordCount;
            }

            var page = new JsonObject
            {
                ["objectIdFieldName"] = "OBJECTID",
                ["fields"] = spatial ? HydrantFields() : InspectionFields(),
                ["features"] = new JsonArray(selected.Select(index => features[index].DeepClone()).ToArray()),
                ["exceededTransferLimit"] = exceeded
            };
            if (spatial)
            {
                page["geometryType"] = "esriGeometryPoint";
                page["hasZ"] = true;
                page["spatialReference"] = new JsonObject { ["wkid"] = 4326 };
            }

            return page;
        }

        private static bool IsTrue(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, string name)
            => query.TryGetValue(name, out var value) && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fixture's connection provider, with the connection string it hands out searching the fixture schema
    /// first. Connections the provider opens itself already set that search path.
    /// </summary>
    private sealed class FixtureSchemaConnectionProvider(IAdoNetDatabaseConnectionProvider inner, Func<string?> schema)
        : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString()
        {
            var builder = new NpgsqlConnectionStringBuilder(inner.GetConnectionString());
            if (schema() is { Length: > 0 } current)
            {
                builder.SearchPath = $"{current},public";
            }

            return builder.ConnectionString;
        }

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => inner.OpenConnectionAsync(cancellationToken);

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => inner.OpenTransactionAsync(isolationLevel, cancellationToken);

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => inner.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => inner.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);
    }

    /// <summary>
    /// A fresh handler per HttpClientFactory rotation over the shared <see cref="FieldOpsFeatureServer"/> state,
    /// so an expired handler being disposed never tears down the source.
    /// </summary>
    private sealed class FieldOpsFeatureServerHandler(FieldOpsFeatureServer source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = source.Answer(request.RequestUri!);

            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that invokes
            // this handler; it is disposed by the caller, not here (cs/local-not-disposed false positive).
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
