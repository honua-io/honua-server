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
/// Integration coverage for honua-server#1255: Esri coded-value and range domains
/// captured on import must persist through publish into the Metadata v2 graph,
/// survive the compat-compile (activated current snapshot), and surface on the
/// canonical field the FeatureServer field <c>domain</c> and <c>queryDomains</c>
/// surfaces read from (<see cref="MetadataV2Field.Domain"/>).
/// </summary>
[Collection("Database")]
public sealed class GeoservicesImportDomainPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WithCodedValueAndRangeDomains_PersistsThemOntoPublishedMetadataV2Field()
    {
        const string tableName = "geoservices_import_domains";
        var serviceName = $"domains_{Guid.NewGuid():N}";
        // Keep the test-class component short: the generated schema name appends a GUID and
        // PostgreSQL truncates identifiers at 63 chars, which would otherwise desync the
        // publish-path schema lookup from the stored (truncated) schema name.
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportDomains");
        var environment = $"DomainTest-{Guid.NewGuid():N}";

        // Ensure the shared 'honua' catalog + metadata_v2 tables the publishing service
        // and graph store write to exist (idempotent; isolated-schema seeding does not
        // guarantee them in this collection).
        await EnsureCatalogSchemaAsync();

        var graphStore = new PostgresMetadataV2GraphStore(
            new FixtureConnectionProvider(fixture),
            environment);

        try
        {
            var service = CreateService(graphStore, schemaName);

            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Domains/FeatureServer",
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
            // the serving side reads. If domains were dropped at publish (the #1255
            // gap) this assertion fails.
            var snapshot = await graphStore.GetCurrentAsync();
            var resource = snapshot.Graph.Resources
                .SingleOrDefault(r => r.SchemaFields.Any(f =>
                    f.Name.Equals("status", StringComparison.OrdinalIgnoreCase)));
            resource.Should().NotBeNull("the imported layer should be projected into the Metadata v2 graph");

            var statusField = resource!.SchemaFields
                .Single(f => f.Name.Equals("status", StringComparison.OrdinalIgnoreCase));
            statusField.Domain.Should().NotBeNull("the coded-value domain must survive import → publish → compat-compile");
            statusField.Domain!.Type.Should().Be(EsriFieldDomainParser.CodedValueDomainType);
            statusField.Domain.Name.Should().Be("StatusDomain");
            statusField.Domain.CodedValues.Select(v => v.Name)
                .Should().BeEquivalentTo(["Active", "Closed", "Pending"]);

            var scoreField = resource.SchemaFields
                .Single(f => f.Name.Equals("score", StringComparison.OrdinalIgnoreCase));
            scoreField.Domain.Should().NotBeNull("the range domain must survive import → publish → compat-compile");
            scoreField.Domain!.Type.Should().Be(EsriFieldDomainParser.RangeDomainType);
            scoreField.Domain.Name.Should().Be("ScoreRange");
            scoreField.Domain.Range.Should().NotBeNull();
            scoreField.Domain.Range!.Should().HaveCount(2);
            scoreField.Domain.Range![0].GetInt32().Should().Be(0);
            scoreField.Domain.Range![1].GetInt32().Should().Be(100);

            // A field without a source domain must publish without one (no invented lookup).
            var nameField = resource.SchemaFields
                .Single(f => f.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
            nameField.Domain.Should().BeNull();

            // queryDomains projection: serving enumerates SchemaFields where Domain is set.
            resource.SchemaFields
                .Where(f => !f.Hidden && f.Domain is not null)
                .Select(f => f.Name)
                .Should().BeEquivalentTo(["status", "score"]);
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
            new HttpClient(new DomainFeatureServerHandler()),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Loose);

        var connectionProvider = new FixtureConnectionProvider(fixture);

        // The imported data table lives in the isolated test schema; include it in the
        // discovery operational schemas so the publish-path geometry discovery finds it.
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
        // base-schema.sql qualifies catalog tables as honua.* but creates 'features'
        // unqualified; route the unqualified create into the honua schema too.
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

    // Minimal ArcGIS FeatureServer mock that advertises a coded-value domain on
    // 'status' and a range domain on 'score', plus a plain 'name' field.
    private sealed class DomainFeatureServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;

            var payload = pathAndQuery switch
            {
                "/arcgis/rest/services/Domains/FeatureServer/0?f=json" => """
                    {
                      "id": 0,
                      "name": "Domain Layer",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "name", "type": "esriFieldTypeString", "nullable": true },
                        {
                          "name": "status",
                          "type": "esriFieldTypeString",
                          "nullable": true,
                          "domain": {
                            "type": "codedValue",
                            "name": "StatusDomain",
                            "codedValues": [
                              { "name": "Active", "code": "A" },
                              { "name": "Closed", "code": "C" },
                              { "name": "Pending", "code": "P" }
                            ]
                          }
                        },
                        {
                          "name": "score",
                          "type": "esriFieldTypeInteger",
                          "nullable": true,
                          "domain": {
                            "type": "range",
                            "name": "ScoreRange",
                            "range": [0, 100]
                          }
                        }
                      ]
                    }
                    """,
                "/arcgis/rest/services/Domains/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json" => """{"count":1}""",
                _ when pathAndQuery.Contains("resultOffset=0", StringComparison.Ordinal) => """
                    {
                      "features": [
                        {
                          "attributes": { "OBJECTID": 1, "name": "Alpha", "status": "A", "score": 42 },
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
        // The publishing service opens a raw connection from this string and references
        // the shared 'features' table unqualified; route the search path to the 'honua'
        // catalog schema so that resolves (the data table is always schema-qualified).
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
