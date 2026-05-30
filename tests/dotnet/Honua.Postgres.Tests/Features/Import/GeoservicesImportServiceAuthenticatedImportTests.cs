// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using FluentAssertions;
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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

[Collection("Database")]
public sealed class GeoservicesImportServiceAuthenticatedImportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ImportLayerAsync_WithTokenCredential_PagesPrivateLayerIntoPostgisWithoutPersistingSecrets()
    {
        const string accessToken = "private-import-token";
        const string secretReference = "env:HONUA_PRIVATE_ARCGIS_TOKEN";
        const string tableName = "private_geoservices_import";
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(GeoservicesImportServiceAuthenticatedImportTests));
        var handler = new PrivateFeatureServerHandler(accessToken);
        var service = CreateService(handler);

        try
        {
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Private/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 1,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = false,
                Credentials = new GeoservicesCredentialDescriptor
                {
                    Mode = GeoservicesAuthenticationModes.Token,
                    AccessToken = accessToken,
                    AccessTokenSecretReference = secretReference
                }
            });

            result.Success.Should().BeTrue();
            result.FeatureCount.Should().Be(2);
            result.SourceServiceUrl.Should().NotContain(accessToken);
            result.SourceServiceUrl.Should().NotContain(secretReference);
            handler.SanitizedPaths.Should().Equal(
                "/arcgis/rest/services/Private/FeatureServer/0?f=json",
                "/arcgis/rest/services/Private/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json",
                "/arcgis/rest/services/Private/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=0&resultRecordCount=1&outSR=4326",
                "/arcgis/rest/services/Private/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=1&resultRecordCount=1&outSR=4326",
                "/arcgis/rest/services/Private/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=2&resultRecordCount=1&outSR=4326");
            handler.SanitizedPaths.Should().NotContain(path => path.Contains(accessToken, StringComparison.Ordinal));

            var rows = await ReadImportedRowsAsync(schemaName, tableName);
            rows.Should().Equal(
                new ImportedRow("Alpha", 7, -157.1, 21.3),
                new ImportedRow("Beta", 11, -157.2, 21.4));
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_WhenPagingRequestReturns401_ReturnsCredentialDeniedFailureWithoutLeakingSecret()
    {
        const string accessToken = "expired-import-token";
        const string secretReference = "env:HONUA_PRIVATE_ARCGIS_TOKEN";
        const string tableName = "expired_token_geoservices_import";
        var schemaName = await fixture.CreateIsolatedSchemaAsync(
            nameof(GeoservicesImportServiceAuthenticatedImportTests) + "_Expired");
        var handler = new ExpiredTokenFeatureServerHandler(accessToken);
        var service = CreateService(handler);

        try
        {
            var result = await service.ImportLayerAsync(new GeoservicesImportRequest
            {
                ServiceUrl = "https://example.com/arcgis/rest/services/Private/FeatureServer",
                LayerId = 0,
                TableName = tableName,
                TargetSchema = schemaName,
                TargetSrid = 4326,
                BatchSize = 1,
                RequestTimeoutSeconds = 5,
                MaxRetries = 0,
                AutoPublish = false,
                Credentials = new GeoservicesCredentialDescriptor
                {
                    Mode = GeoservicesAuthenticationModes.Token,
                    AccessToken = accessToken,
                    AccessTokenSecretReference = secretReference
                }
            });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            result.ErrorMessage.Should().Contain(ImportCompatibilityCodes.ArcGisAccessDenied);
            result.ErrorMessage.Should().NotContain(accessToken);
            result.ErrorMessage.Should().NotContain(secretReference);
            result.SourceServiceUrl.Should().NotContain(accessToken);
            result.SourceServiceUrl.Should().NotContain(secretReference);
            handler.SanitizedPaths.Should().NotContain(path => path.Contains(accessToken, StringComparison.Ordinal));
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
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
            NullLogger<GeoservicesImportService>.Instance);
    }

    private async Task<ImportedRow[]> ReadImportedRowsAsync(string schemaName, string tableName)
    {
        var rows = new List<ImportedRow>();
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT name, value, ST_X(geom), ST_Y(geom)
            FROM {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}
            ORDER BY value;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ImportedRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetDouble(3)));
        }

        return rows.ToArray();
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed record ImportedRow(string Name, int Value, double X, double Y);

    private sealed class PrivateFeatureServerHandler(string expectedToken) : HttpMessageHandler
    {
        private readonly string _escapedToken = Uri.EscapeDataString(expectedToken);

        public List<string> SanitizedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            pathAndQuery.Should().Contain($"token={_escapedToken}");
            request.Headers.Authorization.Should().BeNull();

            var sanitizedPath = pathAndQuery.Replace($"&token={_escapedToken}", string.Empty, StringComparison.Ordinal);
            SanitizedPaths.Add(sanitizedPath);

            var payload = sanitizedPath switch
            {
                "/arcgis/rest/services/Private/FeatureServer/0?f=json" => """
                    {
                      "id": 0,
                      "name": "Private Parcels",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 1,
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "Name", "type": "esriFieldTypeString", "nullable": true },
                        { "name": "Value", "type": "esriFieldTypeInteger", "nullable": true }
                      ]
                    }
                    """,
                "/arcgis/rest/services/Private/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json" => """{"count":2}""",
                "/arcgis/rest/services/Private/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=0&resultRecordCount=1&outSR=4326" => """
                    {
                      "features": [
                        {
                          "attributes": { "OBJECTID": 1, "Name": "Alpha", "Value": 7 },
                          "geometry": { "x": -157.1, "y": 21.3 }
                        }
                      ],
                      "exceededTransferLimit": true,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """,
                "/arcgis/rest/services/Private/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=1&resultRecordCount=1&outSR=4326" => """
                    {
                      "features": [
                        {
                          "attributes": { "OBJECTID": 2, "Name": "Beta", "Value": 11 },
                          "geometry": { "x": -157.2, "y": 21.4 }
                        }
                      ],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """,
                "/arcgis/rest/services/Private/FeatureServer/0/query?f=json&where=1%3D1&outFields=%2A&returnGeometry=true&resultOffset=2&resultRecordCount=1&outSR=4326" => """
                    {
                      "features": [],
                      "exceededTransferLimit": false,
                      "spatialReference": { "wkid": 4326 }
                    }
                    """,
                _ => throw new InvalidOperationException($"Unexpected ArcGIS request path: {sanitizedPath}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ExpiredTokenFeatureServerHandler(string expectedToken) : HttpMessageHandler
    {
        private readonly string _escapedToken = Uri.EscapeDataString(expectedToken);

        public List<string> SanitizedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            pathAndQuery.Should().Contain($"token={_escapedToken}");
            request.Headers.Authorization.Should().BeNull();

            var sanitizedPath = pathAndQuery.Replace($"&token={_escapedToken}", string.Empty, StringComparison.Ordinal);
            SanitizedPaths.Add(sanitizedPath);

            // Metadata + count succeed; the first paged query rejects the token with 401.
            return sanitizedPath switch
            {
                "/arcgis/rest/services/Private/FeatureServer/0?f=json" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "id": 0,
                          "name": "Private Parcels",
                          "geometryType": "esriGeometryPoint",
                          "maxRecordCount": 1,
                          "fields": [
                            { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                            { "name": "Name", "type": "esriFieldTypeString", "nullable": true },
                            { "name": "Value", "type": "esriFieldTypeInteger", "nullable": true }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                }),
                "/arcgis/rest/services/Private/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"count":2}""", Encoding.UTF8, "application/json")
                }),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Unauthorized", Encoding.UTF8, "text/plain")
                })
            };
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
