// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Import;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Integration tests for <see cref="OgcWfsImportService"/> backed by a real Postgres + PostGIS container.
/// </summary>
[Collection("Database")]
public sealed class OgcWfsImportServiceTests(PostgresFixture fixture)
{
    private const string DefaultServiceUrl = "https://example.com/geoserver/wfs";
    private const string DefaultFeatureType = "demo:cities";

    [Fact]
    public async Task ImportFeaturesAsync_HappyPath_CreatesTableAndCopiesFeatures()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("wfs_happy");
        try
        {
            var handler = new FakeWfsHandler(BuildPointFeatureCollection(
                ("Honolulu", 100, -157.85, 21.30),
                ("Hilo", 200, -155.08, 19.71)));
            using var httpClient = new HttpClient(handler);
            var service = CreateService(httpClient, BuildPointInventory());

            var result = await service.ImportFeaturesAsync(new OgcWfsImportRequest
            {
                ServiceUrl = DefaultServiceUrl,
                TargetSchema = schemaName,
                ApplyMode = true,
                AllowUnsafeLocalUrls = true,
                PageSize = 1000
            });

            result.Success.Should().BeTrue();
            result.WasDryRun.Should().BeFalse();
            result.FeatureTypesPlanned.Should().Be(1);
            result.FeatureTypesImported.Should().Be(1);
            result.FeatureTypesSkipped.Should().Be(0);
            result.FeaturesCopied.Should().Be(2);
            result.FeaturesFailed.Should().Be(0);

            var featureType = result.FeatureTypes.Should().ContainSingle().Subject;
            featureType.SourceName.Should().Be(DefaultFeatureType);
            featureType.TargetSchema.Should().Be(schemaName);
            featureType.TargetTable.Should().Be("wfs_cities");
            featureType.Classification.Should().Be(MigrationFidelityAutomationStatuses.Automated);
            featureType.Srid.Should().Be(4326);

            var rows = await ReadCityRowsAsync(schemaName, featureType.TargetTable!);
            rows.Should().BeEquivalentTo(new[]
            {
                new CityRow("Honolulu", 100, -157.85, 21.30),
                new CityRow("Hilo", 200, -155.08, 19.71)
            }, options => options.Using<double>(ctx => ctx.Subject.Should().BeApproximately(ctx.Expectation, 1e-6))
                .WhenTypeIs<double>());

            var indexLocations = await FindIndexSchemasAsync(featureType.TargetTable!);
            indexLocations.Should().NotBeEmpty($"spatial index for {featureType.TargetTable} should be created. Inserted rows: {string.Join(",", rows.Select(r => r.Name))}.");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportFeaturesAsync_DryRun_DoesNotCreateTableOrCopyFeatures()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("wfs_dry");
        try
        {
            var handler = new FakeWfsHandler("{ \"unused\": true }");
            using var httpClient = new HttpClient(handler);
            var service = CreateService(httpClient, BuildPointInventory());

            var result = await service.ImportFeaturesAsync(new OgcWfsImportRequest
            {
                ServiceUrl = DefaultServiceUrl,
                TargetSchema = schemaName,
                DryRun = true,
                AllowUnsafeLocalUrls = true
            });

            result.Success.Should().BeTrue();
            result.WasDryRun.Should().BeTrue();
            result.FeaturesCopied.Should().Be(0);
            result.FeatureTypes.Should().ContainSingle()
                .Which.Classification.Should().Be(MigrationFidelityAutomationStatuses.Automated);

            handler.RequestUris.Should().BeEmpty("dry-run must not hit the WFS GetFeature endpoint");
            (await TableExistsAsync(schemaName, "wfs_cities")).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportFeaturesAsync_RerunWithoutOverwrite_IsIdempotent()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("wfs_idem");
        try
        {
            var inventory = BuildPointInventory();
            var first = new FakeWfsHandler(BuildPointFeatureCollection(("Maui", 7, -156.33, 20.80)));
            using var firstClient = new HttpClient(first);
            var firstService = CreateService(firstClient, inventory);
            var firstResult = await firstService.ImportFeaturesAsync(new OgcWfsImportRequest
            {
                ServiceUrl = DefaultServiceUrl,
                TargetSchema = schemaName,
                ApplyMode = true,
                AllowUnsafeLocalUrls = true
            });
            firstResult.FeaturesCopied.Should().Be(1);
            var firstTable = firstResult.FeatureTypes.Should().ContainSingle().Subject.TargetTable!;
            var tableSchemas = await FindTableSchemasAsync(firstTable);
            tableSchemas.Should().Contain(schemaName, $"first run should create {firstTable} in {schemaName}");

            var second = new FakeWfsHandler(BuildPointFeatureCollection(("Lanai", 9, -156.92, 20.83)));
            using var secondClient = new HttpClient(second);
            var secondService = CreateService(secondClient, inventory);
            var secondResult = await secondService.ImportFeaturesAsync(new OgcWfsImportRequest
            {
                ServiceUrl = DefaultServiceUrl,
                TargetSchema = schemaName,
                ApplyMode = true,
                AllowUnsafeLocalUrls = true
            });

            secondResult.Success.Should().BeTrue();
            secondResult.FeaturesCopied.Should().Be(0);
            var featureType = secondResult.FeatureTypes.Should().ContainSingle().Subject;
            featureType.Warnings.Should().Contain(w => w.Contains("already exists", StringComparison.OrdinalIgnoreCase));
            second.RequestUris.Should().BeEmpty("idempotent re-run must skip paged GetFeature calls when the table is present");

            var rows = await ReadCityRowsAsync(schemaName, featureType.TargetTable!);
            rows.Should().ContainSingle(r => r.Name == "Maui");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private async Task<string[]> FindTableSchemasAsync(string tableName)
    {
        var schemas = new List<string>();
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_schema
            FROM information_schema.tables
            WHERE table_name = @table;
            """;
        command.Parameters.AddWithValue("table", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas.ToArray();
    }

    [Fact]
    public async Task ImportFeaturesAsync_ScannerFailure_ReturnsFailureResultWithoutCopyingFeatures()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("wfs_err");
        try
        {
            var handler = new FakeWfsHandler("{}");
            using var httpClient = new HttpClient(handler);
            var scanner = new ThrowingScanner();
            var service = new OgcWfsImportService(
                scanner,
                new FixtureConnectionProvider(fixture),
                httpClient,
                NullLogger<OgcWfsImportService>.Instance,
                new PostgresSchemaConfiguration(
                    PostgresSchemaConfiguration.DefaultMetadataSchema,
                    schemaName,
                    [schemaName, "public"]));

            var result = await service.ImportFeaturesAsync(new OgcWfsImportRequest
            {
                ServiceUrl = DefaultServiceUrl,
                TargetSchema = schemaName,
                ApplyMode = true,
                AllowUnsafeLocalUrls = true
            });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("scan WFS source");
            result.FeatureTypes.Should().BeEmpty();
            result.FeaturesCopied.Should().Be(0);
            handler.RequestUris.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportFeaturesAsync_PagesAcrossMultipleGetFeatureRequests()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("wfs_paged");
        try
        {
            var handler = new PagedWfsHandler(pageSize: 2, totalFeatures: 5);
            using var httpClient = new HttpClient(handler);
            var service = CreateService(httpClient, BuildPointInventory());

            var result = await service.ImportFeaturesAsync(new OgcWfsImportRequest
            {
                ServiceUrl = DefaultServiceUrl,
                TargetSchema = schemaName,
                ApplyMode = true,
                AllowUnsafeLocalUrls = true,
                PageSize = 2
            });

            result.Success.Should().BeTrue();
            result.FeaturesCopied.Should().Be(5);
            handler.RequestUris.Should().HaveCountGreaterOrEqualTo(3, "5 features at pageSize=2 paginates over at least 3 requests");
            handler.RequestUris[0].Query.Should().Contain("typeNames=demo%3Acities").And.Contain("count=2");
            handler.RequestUris.Should().AllSatisfy(uri => uri.Query.Should().Contain("outputFormat=application%2Fjson"));
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportFeaturesAsync_SchemaSurfacedFromInventoryDrivesPostgresColumnTypes()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("wfs_schema");
        try
        {
            var handler = new FakeWfsHandler(BuildPointFeatureCollection(("Kihei", 42, -156.46, 20.76)));
            using var httpClient = new HttpClient(handler);
            var service = CreateService(httpClient, BuildPointInventory());

            var result = await service.ImportFeaturesAsync(new OgcWfsImportRequest
            {
                ServiceUrl = DefaultServiceUrl,
                TargetSchema = schemaName,
                ApplyMode = true,
                AllowUnsafeLocalUrls = true
            });

            result.Success.Should().BeTrue();
            var resolvedSchema = result.FeatureTypes.Should().ContainSingle().Subject.TargetSchema!;
            var columns = await ReadColumnTypesAsync(resolvedSchema, "wfs_cities");
            columns.Should().ContainKey("name").WhoseValue.Should().Be("text");
            columns.Should().ContainKey("population").WhoseValue.Should().Be("bigint");
            columns.Should().ContainKey("geom").WhoseValue.Should().Be("USER-DEFINED");
            columns.Should().ContainKey("honua_objectid").WhoseValue.Should().Be("bigint");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private OgcWfsImportService CreateService(HttpClient httpClient, MigrationSourceInventoryArtifact inventory)
    {
        return new OgcWfsImportService(
            new InMemoryScanner(inventory),
            new FixtureConnectionProvider(fixture),
            httpClient,
            NullLogger<OgcWfsImportService>.Instance,
            new PostgresSchemaConfiguration(
                PostgresSchemaConfiguration.DefaultMetadataSchema,
                PostgresSchemaConfiguration.DefaultDataSchema,
                [PostgresSchemaConfiguration.DefaultDataSchema, "public"]));
    }

    private static MigrationSourceInventoryArtifact BuildPointInventory(string compatibilityLevel = "compatible")
    {
        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "ogc-wfs",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Demo WFS",
                BaseUrl = DefaultServiceUrl,
                ServiceType = "WFS",
                Version = "2.0.0"
            },
            AuthPosture = new MigrationInventoryAuthPosture { Mode = "anonymous" },
            ScanCompleteness = new MigrationInventoryCompleteness { Status = "complete" },
            Summary = new MigrationInventorySummary { ResourceCount = 1 },
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = compatibilityLevel,
                Reason = "Inventory ready for import."
            },
            Resources =
            [
                new MigrationInventoryResource
                {
                    Id = "feature-type:demo:cities",
                    ContainerId = "namespace:demo",
                    Kind = "feature-type",
                    Name = DefaultFeatureType,
                    GeometryType = "Point",
                    SpatialReferences =
                    [
                        new MigrationSpatialReferenceInfo
                        {
                            Role = "geometry",
                            SourceValue = "EPSG:4326",
                            Srid = 4326,
                            IsGeographic = true
                        }
                    ],
                    Fields =
                    [
                        new MigrationInventoryField { Name = "name", FieldType = "xsd:string" },
                        new MigrationInventoryField { Name = "population", FieldType = "xsd:int" }
                    ],
                    Compatibility = new MigrationCompatibilityAssessment
                    {
                        Level = compatibilityLevel,
                        Reason = "Automated GetFeature path available."
                    }
                }
            ]
        };
    }

    private static string BuildPointFeatureCollection(params (string Name, int Population, double X, double Y)[] features)
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"FeatureCollection\",\"numberMatched\":");
        sb.Append(features.Length);
        sb.Append(",\"features\":[");
        for (var i = 0; i < features.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var (name, pop, x, y) = features[i];
            sb.Append("{\"type\":\"Feature\",\"properties\":{\"name\":\"")
                .Append(name)
                .Append("\",\"population\":")
                .Append(pop)
                .Append("},\"geometry\":{\"type\":\"Point\",\"coordinates\":[")
                .Append(x.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(',')
                .Append(y.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("]}}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private async Task<CityRow[]> ReadCityRowsAsync(string schemaName, string tableName)
    {
        var rows = new List<CityRow>();
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT name, population, ST_X(geom), ST_Y(geom)
            FROM {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}
            ORDER BY population;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new CityRow(
                reader.GetString(0),
                (int)reader.GetInt64(1),
                reader.GetDouble(2),
                reader.GetDouble(3)));
        }

        return rows.ToArray();
    }

    private async Task<Dictionary<string, string>> ReadColumnTypesAsync(string schemaName, string tableName)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position;
            """;
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("table", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        return columns;
    }

    private async Task<string[]> FindIndexSchemasAsync(string tableName)
    {
        var schemas = new List<string>();
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schemaname
            FROM pg_indexes
            WHERE tablename = @table AND indexname LIKE @indexPattern;
            """;
        command.Parameters.AddWithValue("table", tableName);
        command.Parameters.AddWithValue("indexPattern", tableName + "_geom_idx%");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas.ToArray();
    }

    private async Task<bool> TableExistsAsync(string schemaName, string tableName)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table LIMIT 1;
            """;
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("table", tableName);
        var result = await command.ExecuteScalarAsync();
        return result != null && result != DBNull.Value;
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed record CityRow(string Name, int Population, double X, double Y);

    private sealed class InMemoryScanner(MigrationSourceInventoryArtifact artifact) : IOgcServiceMigrationScanner
    {
        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            OgcServiceScanRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(artifact);
    }

    private sealed class ThrowingScanner : IOgcServiceMigrationScanner
    {
        public Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
            OgcServiceScanRequest request,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("simulated WFS capabilities outage");
    }

    private sealed class FakeWfsHandler(string responsePayload) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responsePayload, Encoding.UTF8, "application/geo+json")
            });
        }
    }

    private sealed class PagedWfsHandler(int pageSize, int totalFeatures) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var startIndex = ParseQueryInt(request.RequestUri!.Query, "startIndex");
            var remaining = Math.Max(0, totalFeatures - startIndex);
            var take = Math.Min(remaining, pageSize);
            var features = new (string Name, int Population, double X, double Y)[take];
            for (var i = 0; i < take; i++)
            {
                var index = startIndex + i;
                features[i] = ($"city-{index}", index, -157.0 + (index * 0.01), 21.0 + (index * 0.01));
            }

            var payload = BuildPagedPayload(features, totalFeatures);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/geo+json")
            });
        }

        private static int ParseQueryInt(string queryString, string key)
        {
            var trimmed = queryString.TrimStart('?');
            foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                if (separator < 0)
                {
                    continue;
                }

                var partKey = Uri.UnescapeDataString(part[..separator]);
                if (!string.Equals(partKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var partValue = Uri.UnescapeDataString(part[(separator + 1)..]);
                if (int.TryParse(partValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return 0;
        }

        private static string BuildPagedPayload((string Name, int Population, double X, double Y)[] features, int total)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"FeatureCollection\",\"numberMatched\":").Append(total).Append(",\"features\":[");
            for (var i = 0; i < features.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                var (name, pop, x, y) = features[i];
                sb.Append("{\"type\":\"Feature\",\"properties\":{\"name\":\"")
                    .Append(name)
                    .Append("\",\"population\":")
                    .Append(pop)
                    .Append("},\"geometry\":{\"type\":\"Point\",\"coordinates\":[")
                    .Append(x.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(y.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append("]}}");
            }

            sb.Append("]}");
            return sb.ToString();
        }
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => postgresFixture.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await postgresFixture.DataSource.OpenConnectionAsync(cancellationToken);

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

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}
