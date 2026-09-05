// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// End-to-end regression probe for the ArcGIS FeatureServer migration workflow. The
/// fixture advertises supportsPagination=false and only permits object-id windows;
/// the current importer instead advances resultOffset and cannot complete.
/// </summary>
[Collection("Database")]
public sealed class GeoservicesEsriProbeImportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WithoutPaginationSupport_UsesObjectIdWindows()
    {
        const string tableName = "esri_probe_no_pagination";
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(GeoservicesEsriProbeImportTests));
        var handler = new ObjectIdWindowOnlyHandler();

        try
        {
            var service = CreateService(handler);
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Probe/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schema,
                TargetSrid = 4326,
                BatchSize = 2,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = false
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(4);
            handler.Paths.Should().Contain(path => path.Contains("objectIds=", StringComparison.Ordinal));
            handler.Paths.Where(path => path.Contains("objectIds=", StringComparison.Ordinal))
                .Should().OnlyContain(path =>
                    !path.Contains("resultOffset=", StringComparison.Ordinal) &&
                    !path.Contains("resultRecordCount=", StringComparison.Ordinal));

            await using var verification = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT name, ST_AsEWKT(geom) FROM \"{schema}\".\"{tableName}\" ORDER BY name",
                verification);
            await using var rows = await command.ExecuteReaderAsync();
            var imported = new List<(string Name, string Geometry)>();
            while (await rows.ReadAsync())
            {
                imported.Add((rows.GetString(0), rows.GetString(1)));
            }

            imported.Should().Equal(
                ("four", "SRID=4326;POINT(4 4)"),
                ("one", "SRID=4326;POINT(1 1)"),
                ("three", "SRID=4326;POINT(3 3)"),
                ("two", "SRID=4326;POINT(2 2)"));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private GeoservicesImportService CreateService(HttpMessageHandler handler)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(handler),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        crsRegistry.Setup(registry => registry.ResolveBySridAsync(4326, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>(new CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/4326",
                4326,
                AxisOrder.EastNorth,
                true)));

        return new GeoservicesImportService(
            restClient,
            new FixtureConnectionProvider(fixture),
            crsRegistry.Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            new GeoservicesLayerPublicationService(NullLogger<GeoservicesLayerPublicationService>.Instance));
    }

    private sealed class ObjectIdWindowOnlyHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);

            if (path.EndsWith("/0?f=json", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": 0,
                      "name": "No pagination layer",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 2,
                      "supportsPagination": false,
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "NAME", "type": "esriFieldTypeString", "nullable": true }
                      ]
                    }
                    """);
            }

            if (path.Contains("returnCountOnly=true", StringComparison.Ordinal))
            {
                return Json("{\"count\":4}");
            }

            if (path.Contains("returnIdsOnly=true", StringComparison.Ordinal))
            {
                return Json("{\"objectIds\":[1,2,3,4]}");
            }

            if (path.Contains("objectIds=", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "features": [
                        { "attributes": { "OBJECTID": 1, "NAME": "one" }, "geometry": { "x": 1, "y": 1 } },
                        { "attributes": { "OBJECTID": 2, "NAME": "two" }, "geometry": { "x": 2, "y": 2 } },
                        { "attributes": { "OBJECTID": 3, "NAME": "three" }, "geometry": { "x": 3, "y": 3 } },
                        { "attributes": { "OBJECTID": 4, "NAME": "four" }, "geometry": { "x": 4, "y": 4 } }
                      ],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """);
            }

            // A source with supportsPagination=false rejects resultOffset rather than
            // returning the first page repeatedly. A correct importer must switch to
            // objectIds windows before reaching this request.
            throw new InvalidOperationException(
                "Probe source rejects resultOffset because supportsPagination=false.");
        }

        private static Task<HttpResponseMessage> Json(string payload)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture)
        : IAdoNetDatabaseConnectionProvider
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
