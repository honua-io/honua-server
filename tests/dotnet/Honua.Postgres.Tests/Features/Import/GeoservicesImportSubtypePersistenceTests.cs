// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Admin;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Metadata;
using Honua.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Integration coverage for honua-server#1378 (#1254): an Esri subtype set (a
/// <c>subtypeField</c> plus <c>subtypes</c> with per-subtype labels, default values,
/// and value domains) captured on import must persist through publish into the
/// Metadata v2 graph and survive the compat-compile (activated current snapshot), so
/// the FeatureServer layer metadata can serve <c>subtypeField</c> / <c>subtypes</c> /
/// <c>defaultSubtypeCode</c> (see <see cref="MetadataV2Resource.Subtypes"/>).
/// </summary>
[Collection("Database")]
public sealed class GeoservicesImportSubtypePersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WithSubtypeFieldAndSubtypes_PersistsThemOntoPublishedMetadataV2Resource()
    {
        const string tableName = "geoservices_import_subtypes";
        var serviceName = $"subtypes_{Guid.NewGuid():N}";
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportSubtypes");
        var environment = $"SubtypeTest-{Guid.NewGuid():N}";

        await EnsureCatalogSchemaAsync();

        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture),
            environment);

        try
        {
            var service = CreateService(graphStore, schemaName);

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

            // Read the activated current snapshot — exactly the compat-compiled graph
            // the serving side reads. If subtypes were dropped at publish this fails.
            var snapshot = await graphStore.GetCurrentAsync();
            var resource = snapshot.Graph.Resources
                .SingleOrDefault(r => r.SchemaFields.Any(f =>
                    f.Name.Equals("buildingtype", StringComparison.OrdinalIgnoreCase)));
            resource.Should().NotBeNull("the imported layer should be projected into the Metadata v2 graph");

            resource!.Subtypes.Should().NotBeNull("the subtype set must survive import → publish → compat-compile");
            var subtypes = resource.Subtypes!;
            subtypes.SubtypeField.Should().Be("buildingtype");
            subtypes.DefaultSubtypeCode.Should().NotBeNull();
            subtypes.DefaultSubtypeCode!.Value.GetInt32().Should().Be(1);

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

    private GeoservicesImportService CreateService(PostgresMetadataV2GraphStore graphStore, string dataSchema)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(new SubtypeFeatureServerHandler()),
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
            layerPublishingService: publishingService);
    }

    private async Task EnsureCatalogSchemaAsync()
    {
        var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "base-schema.sql"));
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO honua, public;\n{sql}";
        await command.ExecuteNonQueryAsync();
    }

    private async Task CleanupCatalogAsync(string serviceName)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM honua.layer_fields
            WHERE layer_id IN (
                SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
            DELETE FROM honua.service_layers WHERE service_name = @serviceName;
            DELETE FROM honua.services WHERE service_name = @serviceName;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "serviceName";
        parameter.Value = serviceName;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }

    // Minimal ArcGIS FeatureServer mock that advertises an integer subtype field
    // 'buildingtype' with two subtypes; the 'Residential' subtype carries a per-subtype
    // default value and a coded-value domain on 'status'.
    private sealed class SubtypeFeatureServerHandler : HttpMessageHandler
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
                "/arcgis/rest/services/Subtypes/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json" => """{"count":1}""",
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture) : IDatabaseConnectionProvider
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
