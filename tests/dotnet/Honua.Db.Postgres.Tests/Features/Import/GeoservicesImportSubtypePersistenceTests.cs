// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Integration coverage for honua-server#1378 (#1254): an Esri subtype set (a
/// <c>subtypeField</c> plus <c>subtypes</c> with per-subtype labels, default values,
/// and value domains) captured on import must persist through publish into the
/// Metadata v2 graph and survive the compat-compile (activated current snapshot), so
/// the FeatureServer layer metadata can serve <c>subtypeField</c> / <c>subtypes</c> /
/// <c>defaultSubtypeCode</c> (see <see cref="MetadataV2Resource.Subtypes"/>).
/// </summary>
[Collection("Database")]
public sealed partial class GeoservicesImportSubtypePersistenceTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportLayerAsync_WithSubtypeMetadata_PersistsThemOntoPublishedMetadataV2Resource(bool featureTypes)
    {
        const string tableName = "geoservices_import_subtypes";
        var serviceName = $"subtypes_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportSubtypes");
        var environment = $"SubtypeTest-{Guid.NewGuid():N}";

        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");

        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture),
            environment,
            FixtureBypassDatabaseSchemaGuard.Instance);

        try
        {
            var service = CreateService(graphStore, schemaName, featureTypes);

            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Subtypes/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 10,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName
            });

            result.Success.Should().BeTrue();
            result.Warnings.Should().NotContain(warning =>
                warning.Contains("publishing did not complete", StringComparison.OrdinalIgnoreCase));

            // Read the activated current snapshot â€” exactly the compat-compiled graph
            // the serving side reads. If subtypes were dropped at publish this fails.
            var snapshot = await graphStore.GetCurrentAsync();
            var resource = snapshot.Graph.Resources
                .SingleOrDefault(r => r.SchemaFields.Any(f =>
                    f.Name.Equals("buildingtype", StringComparison.OrdinalIgnoreCase)));
            resource.Should().NotBeNull("the imported layer should be projected into the Metadata v2 graph");

            resource!.Subtypes.Should().NotBeNull("the subtype set must survive import â†’ publish â†’ compat-compile");
            var subtypes = resource.Subtypes!;
            subtypes.SubtypeField.Should().Be("buildingtype");
            if (featureTypes)
            {
                subtypes.DefaultSubtypeCode.Should().BeNull();
            }
            else
            {
                subtypes.DefaultSubtypeCode.Should().NotBeNull();
                subtypes.DefaultSubtypeCode!.Value.GetInt32().Should().Be(1);
            }

            subtypes.Subtypes.Select(s => s.Name)
                .Should().BeEquivalentTo(["Commercial", "Residential"]);
            subtypes.Subtypes.Select(s => s.Code.GetInt32())
                .Should().BeEquivalentTo([1, 2]);

            // The 'Residential' subtype carried a per-subtype default value and a domain
            // override; both must survive on the canonical override.
            var residential = subtypes.Subtypes.Single(s => s.Name == "Residential");
            residential.FieldOverrides.Should().ContainKey("status");
            var statusOverride = residential.FieldOverrides["status"];
            statusOverride.DefaultValue.Should().NotBeNull();
            statusOverride.DefaultValue!.Value.GetString().Should().Be("occupied");
            statusOverride.Domain.Should().NotBeNull();
            statusOverride.Domain!.Type.Should().Be(EsriFieldDomainParser.CodedValueDomainType);
            statusOverride.Domain.CodedValues.Select(v => v.Name)
                .Should().BeEquivalentTo(["Occupied", "Vacant"]);

            // The subtype field itself must be a declared schema field (graph validation
            // requires this), confirming the publish path only attached the subtypes
            // because the column was actually published.
            resource.SchemaFields.Should().Contain(f =>
                f.Name.Equals("buildingtype", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 3)]
    public async Task ImportLayerAsync_WithDimensionalGeometry_PublishesStoredDimensions(bool hasZ, bool hasM, int zmFlag)
    {
        const string tableName = "geoservices_import_dimensions";
        var serviceName = $"dimensions_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportDimensions");
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture), $"Dimensions-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);

        try
        {
            var service = CreateService(graphStore, schemaName, false, hasZ, hasM);
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Subtypes/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 10,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName
            });

            result.Success.Should().BeTrue();
            result.Warnings.Should().NotContain(warning =>
                warning.Contains("publishing did not complete", StringComparison.OrdinalIgnoreCase));
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT ST_Zmflag(geom), ST_X(geom), ST_Y(geom), ST_Z(geom), ST_M(geom) FROM \"{schemaName}\".\"{tableName}\"";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt16(0).Should().Be((short)zmFlag);
            reader.GetDouble(1).Should().Be(-157.1);
            reader.GetDouble(2).Should().Be(21.3);
            if (hasZ)
                reader.GetDouble(3).Should().Be(125.5);
            else
                reader.IsDBNull(3).Should().BeTrue();
            if (hasM)
                reader.GetDouble(4).Should().Be(42.25);
            else
                reader.IsDBNull(4).Should().BeTrue();

            var snapshot = await graphStore.GetCurrentAsync();
            var resource = snapshot.Graph.Resources.Single(r => r.Metadata.Name == "Subtype Layer");
            resource.Display.Should().NotBeNull();
            resource.Display!.HasZ.Should().Be(hasZ);
            resource.Display.HasM.Should().Be(hasM);
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_WithDateOnlyFields_PreservesCalendarDatesAndRejectsMalformedValues()
    {
        const string tableName = "geoservices_import_dates";
        var serviceName = $"dates_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportDates");
        await EnsureCatalogSchemaAsync();
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, "honua");
        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture), $"Dates-{Guid.NewGuid():N}",
            FixtureBypassDatabaseSchemaGuard.Instance);
        try
        {
            var service = CreateService(graphStore, schemaName, false, temporalFields: true);
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Subtypes/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 10,
                MaxRetries = 0,
                AutoPublish = true,
                ServiceName = serviceName
            });
            result.FeatureCount.Should().Be(4);
            result.FailedFeatures.Should().Be(1, "a malformed non-null calendar date must not silently become text or null");

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT dateofflight, pg_typeof(dateofflight)::text FROM \"{schemaName}\".\"{tableName}\" ORDER BY dateofflight NULLS LAST";
            await using (var reader = await command.ExecuteReaderAsync())
            {
                foreach (var expected in new[] { new DateOnly(2024, 2, 28), new DateOnly(2024, 2, 29), new DateOnly(2024, 3, 1) })
                {
                    (await reader.ReadAsync()).Should().BeTrue();
                    reader.GetFieldValue<DateOnly>(0).Should().Be(expected);
                    reader.GetString(1).Should().Be("date");
                }
                (await reader.ReadAsync()).Should().BeTrue();
                reader.IsDBNull(0).Should().BeTrue();
                (await reader.ReadAsync()).Should().BeFalse();
            }
            command.CommandText = $"SELECT COUNT(*) FROM \"{schemaName}\".\"{tableName}\" WHERE dateofflight >= DATE '2024-02-29' AND dateofflight < DATE '2024-03-01'";
            (await command.ExecuteScalarAsync()).Should().Be(1L);
            var snapshot = await graphStore.GetCurrentAsync();
            var resource = snapshot.Graph.Resources.Single(r => r.Metadata.Name == "Subtype Layer");
            resource.SchemaFields.Single(f => f.Name == "dateofflight").Type.Should().Be(MetadataV2FieldType.Date);
            var dictionaryPool = new DefaultObjectPoolProvider().Create(
                new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());
            var mappedReader = new PostgresStorageMappedFeatureReader(
                new FixtureConnectionProvider(fixture), dictionaryPool, resource,
                new FeatureStorageMapping(TableName: tableName, SchemaName: schemaName,
                    PrimaryKeyColumn: "objectid", GeometryColumn: "geom", StorageSrid: 4326),
                connection: null, connectionEncryptionService: null);
            var calendar = await mappedReader.QueryStatisticsAsync(1, new FeatureQuery
            {
                GroupByFields = ["dateofflight"],
                OutStatistics = [new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = "objectid",
                    OutStatisticFieldName = "record_count"
                }]
            });
            calendar.Should().HaveCount(4);
            calendar.Select(row => row["dateofflight"]).Should().BeEquivalentTo(
                new object?[] { "2024-02-28", "2024-02-29", "2024-03-01", null });
            calendar.Should().OnlyContain(row => Equals(row["record_count"], 1L));
        }
        finally
        {
            await CleanupCatalogAsync(serviceName);
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private GeoservicesImportService CreateService(PostgresMetadataV2GraphStore graphStore, string dataSchema, bool featureTypes,
        bool hasZ = false, bool hasM = false, HttpMessageHandler? handler = null,
        Honua.Core.Features.Attachments.Abstractions.IAttachmentStore? attachmentStore = null,
        bool nonSpatial = false, bool temporalFields = false)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(handler ?? new SubtypeFeatureServerHandler(featureTypes, hasZ, hasM, nonSpatial, temporalFields)),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Loose);

        var connectionProvider = new FixtureConnectionProvider(fixture);

        var schemaConfiguration = new PostgresSchemaConfiguration(
            PostgresSchemaConfiguration.DefaultMetadataSchema,
            dataSchema,
            [dataSchema, "public"]);

        var publishingService = new PostgreSqlLayerPublishingService(
            new PostgreSqlTableDiscoveryService(
                NullLogger<PostgreSqlTableDiscoveryService>.Instance,
                schemaContext: null,
                schemaConfiguration: schemaConfiguration),
            graphStore,
            NullLogger<PostgreSqlLayerPublishingService>.Instance);

        return new GeoservicesImportService(
            restClient,
            connectionProvider,
            crsRegistry.Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            new GeoservicesLayerPublicationService(
                NullLogger<GeoservicesLayerPublicationService>.Instance,
                layerPublishingService: publishingService),
            attachmentStore: attachmentStore);
    }

    private async Task EnsureCatalogSchemaAsync()
    {
        var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "base-schema.sql"));
        // honua-server#1568 (signature 2): base-schema.sql creates/ALTERs the literal,
        // process-global honua.* catalog tables (search_path isolation does not scope them), so
        // apply it under the shared seed advisory lock to keep parallel [Collection("Database")]
        // tests off the 40P01 deadlock path. The seed's own SET search_path stays first.
        await fixture.ApplyGlobalSeedSqlAsync($"SET search_path TO honua, public;\n{sql}");
    }

    private async Task CleanupCatalogAsync(string serviceName)
    {
        // #2020: these DELETEs mutate the literal, process-global honua.* catalog
        // tables (search_path isolation does not scope them), so route them through the
        // shared seed advisory lock to keep parallel [Collection("Database")] tests off
        // the 40P01 deadlock path.
        const string sql = """
            DELETE FROM honua.layer_fields
            WHERE layer_id IN (
                SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
            DELETE FROM honua.service_layers WHERE service_name = @serviceName;
            DELETE FROM honua.services WHERE service_name = @serviceName;
            """;
        await fixture.ApplyGlobalSeedSqlAsync(sql, command =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "serviceName";
            parameter.Value = serviceName;
            command.Parameters.Add(parameter);
        });
    }

    // Minimal ArcGIS FeatureServer mock that advertises an integer subtype field
    // 'buildingtype' with two subtypes; the 'Residential' subtype carries a per-subtype
    // default value and a coded-value domain on 'status'.
    private sealed class SubtypeFeatureServerHandler(bool featureTypes, bool hasZ, bool hasM, bool nonSpatial, bool temporalFields) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;

            var payload = pathAndQuery switch
            {
                "/arcgis/rest/services/Subtypes/FeatureServer/0?f=json" => """
                    {
                      "id": 0,
                      "name": "Subtype Layer",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "subtypeField": "buildingtype",
                      "defaultSubtypeCode": 1,
                      "subtypes": [
                        {
                          "code": 1,
                          "name": "Commercial",
                          "defaultValues": { "status": "open" }
                        },
                        {
                          "code": 2,
                          "name": "Residential",
                          "defaultValues": { "status": "occupied" },
                          "domains": {
                            "status": {
                              "type": "codedValue",
                              "name": "OccupancyDomain",
                              "codedValues": [
                                { "name": "Occupied", "code": "occupied" },
                                { "name": "Vacant", "code": "vacant" }
                              ]
                            }
                          }
                        }
                      ],
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "buildingtype", "type": "esriFieldTypeInteger", "nullable": true },
                        { "name": "status", "type": "esriFieldTypeString", "nullable": true }
                      ]
                    }
                    """,
                "/arcgis/rest/services/Subtypes/FeatureServer/0/query?where=1%3D1&f=json&returnCountOnly=true" => """{"count":1}""",
                _ when pathAndQuery.Contains("resultOffset=0", StringComparison.Ordinal) => """
                    {
                      "features": [
                        {
                          "attributes": { "OBJECTID": 1, "buildingtype": 2, "status": "occupied" },
                          "geometry": { "x": -157.1, "y": 21.3 }
                        }
                      ],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """,
                _ when pathAndQuery.Contains("resultOffset=", StringComparison.Ordinal) => """
                    {
                      "features": [],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """,
                _ => throw new InvalidOperationException($"Unexpected ArcGIS request path: {pathAndQuery}")
            };

            if (featureTypes && pathAndQuery == "/arcgis/rest/services/Subtypes/FeatureServer/0?f=json")
            {
                var layer = JsonNode.Parse(payload)!.AsObject();
                var types = new JsonArray();
                foreach (var subtype in layer["subtypes"]!.AsArray())
                {
                    var attributes = new JsonObject { ["STATUS"] = subtype!["defaultValues"]!["status"]!.DeepClone() };
                    var domains = subtype["domains"]?.DeepClone();
                    types.Add(new JsonObject
                    {
                        ["id"] = subtype["code"]!.DeepClone(),
                        ["name"] = subtype["name"]!.DeepClone(),
                        ["domains"] = domains,
                        ["templates"] = new JsonArray(new JsonObject
                        {
                            ["prototype"] = new JsonObject { ["attributes"] = attributes }
                        })
                    });
                }
                layer.Remove("subtypeField");
                layer.Remove("subtypes");
                layer.Remove("defaultSubtypeCode");
                layer["typeIdField"] = "BUILDINGTYPE";
                layer["types"] = types;
                payload = layer.ToJsonString();
            }

            var response = JsonNode.Parse(payload)!.AsObject();
            if (temporalFields)
            {
                if (response["fields"] is JsonArray fields)
                    fields.Add(new JsonObject { ["name"] = "DateOfFlight", ["type"] = "esriFieldTypeDateOnly", ["nullable"] = true });
                if (response["count"] is not null)
                    response["count"] = 5;
                if (response["features"] is JsonArray { Count: > 0 } rows)
                {
                    var template = rows[0]!.DeepClone();
                    rows.Clear();
                    string?[] dates = ["2024-02-28", "2024-02-29", "2024-03-01", null, "2023-02-29"];
                    for (var index = 0; index < dates.Length; index++)
                    {
                        var row = template.DeepClone();
                        row["attributes"]!["OBJECTID"] = index + 1;
                        row["attributes"]!["DateOfFlight"] = dates[index];
                        rows.Add(row);
                    }
                }
            }
            if (pathAndQuery == "/arcgis/rest/services/Subtypes/FeatureServer/0?f=json")
            {
                response["hasZ"] = hasZ;
                response["hasM"] = hasM;
            }
            else if (response["features"] is JsonArray features)
            {
                foreach (var feature in features)
                {
                    var geometry = feature!["geometry"]!.AsObject();
                    if (hasZ && pathAndQuery.Contains("returnZ=true", StringComparison.Ordinal))
                        geometry["z"] = 125.5;
                    if (hasM && pathAndQuery.Contains("returnM=true", StringComparison.Ordinal))
                        geometry["m"] = 42.25;
                }
            }
            if (nonSpatial)
            {
                response.Remove("geometryType");
                response.Remove("spatialReference");
                response.Remove("hasZ");
                response.Remove("hasM");
                if (response["fields"] is JsonArray fields)
                {
                    response["type"] = "Table";
                    fields.Add(new JsonObject { ["name"] = "Join_ID", ["type"] = "esriFieldTypeString", ["nullable"] = true });
                }
                if (response.ContainsKey("count"))
                {
                    response["count"] = 2;
                }
                if (response["features"] is JsonArray rows && rows.Count > 0)
                {
                    var first = rows[0]!.AsObject();
                    first.Remove("geometry");
                    first["attributes"]!["Join_ID"] = "001-A";
                    first["attributes"]!["status"] = null;
                    var second = first.DeepClone().AsObject();
                    second["attributes"]!["OBJECTID"] = 2;
                    second["attributes"]!["Join_ID"] = "002-B";
                    second["attributes"]!["status"] = "occupied";
                    rows.Add(second);
                }
            }
            payload = response.ToJsonString();

            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that invokes
            // this handler; it is disposed by the caller, not here (cs/local-not-disposed false positive).
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString()
            => new Npgsql.NpgsqlConnectionStringBuilder(postgresFixture.ConnectionString)
            {
                SearchPath = "honua,public"
            }.ConnectionString;

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
