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
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Migration;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Issue #4600, acceptance criterion 5: a failed replacement must retain the old target. These run
/// against real PostGIS: the prior target is seeded with known rows, the import is pointed at it with
/// <c>OverwriteExisting</c>, and the surviving rows are read back from the database. The seeded rows
/// are the oracle — nothing here is derived from the importer's own output.
/// </summary>
/// <remarks>
/// The source layer declares <c>code</c> as a length-3 string, so the importer creates a
/// <c>VARCHAR(3)</c> column and a source record carrying a longer value genuinely fails to insert.
/// Before #4600 the importer committed the replacement anyway: the complete prior dataset was
/// dropped and the target held only the records that happened to load.
/// </remarks>
[Collection("Database")]
public sealed class GeoservicesImportReplacementTests(PostgresFixture fixture)
{
    private const string TableName = "geoservices_replace_target";

    [Fact]
    public async Task ImportLayerAsync_ReplacingExistingTargetWhenARecordFailsToLoad_RetainsPriorTargetAndFails()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportReplacementRefused");
        try
        {
            await SeedPriorTargetAsync(schemaName);
            var service = CreateService(new ReplacementFeatureServerHandler(["CCC", "TOOLONG"]));

            var result = await service.ImportLayerAsync(BuildRequest(schemaName, overwrite: true));

            (await ReadCodesAsync(schemaName)).Should().Equal(
                ["AAA", "BBB"],
                "a replacement that lost a source record must leave the complete prior target untouched");

            result.Success.Should().BeFalse();
            result.NeedsReview.Should().BeFalse("nothing was published or committed, so there is nothing to review");
            result.FeatureCount.Should().Be(0, "the loaded record was rolled back with the refused replacement");
            result.FailedFeatures.Should().Be(1);
            result.ErrorMessage.Should().Contain("Replacement refused").And.Contain("1 of 2").And.Contain("retained");
            result.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Incomplete);

            var difference = result.FidelityDifferences.Should().ContainSingle().Subject;
            difference.Code.Should().Be(MigrationFidelityDifferenceCodes.RecordsLost);
            difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
            difference.Actual.Should().Be("1 dropped source records");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// Positive control: when every source record loads, the replacement is committed and the prior
    /// rows are gone. Without this the retention assertion above could pass merely because the
    /// importer never replaced anything.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_ReplacingExistingTargetWhenEveryRecordLoads_ReplacesPriorTarget()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportReplacementCommitted");
        try
        {
            await SeedPriorTargetAsync(schemaName);
            var service = CreateService(new ReplacementFeatureServerHandler(["CCC", "DDD"]));

            var result = await service.ImportLayerAsync(BuildRequest(schemaName, overwrite: true));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(2);
            result.FailedFeatures.Should().Be(0);
            (await ReadCodesAsync(schemaName)).Should().Equal("CCC", "DDD");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// Boundary: the refusal protects an existing dataset. A first import has no prior target, so the
    /// records that loaded are kept and the run is routed to review by the fidelity verdict (#4661),
    /// matching the file-import replace behavior from #4006.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_FirstImportWhenARecordFailsToLoad_KeepsLoadedRecordsAndRoutesToReview()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportReplacementFirst");
        try
        {
            var service = CreateService(new ReplacementFeatureServerHandler(["CCC", "TOOLONG"]));

            var result = await service.ImportLayerAsync(BuildRequest(schemaName, overwrite: true));

            (await ReadCodesAsync(schemaName)).Should().Equal("CCC");
            result.Success.Should().BeFalse();
            result.NeedsReview.Should().BeTrue();
            result.FeatureCount.Should().Be(1);
            result.FailedFeatures.Should().Be(1);
            result.FidelityVerdict.Should().Be(MigrationFidelityVerdicts.Incomplete);
            result.FidelityDifferences.Should().Contain(d => d.Code == MigrationFidelityDifferenceCodes.RecordsLost);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    /// <summary>
    /// Cancellation contract: a replacement cancelled part way through the transfer surfaces as a
    /// cancellation and leaves the prior target untouched. The source serves object-id windows of one
    /// record, and the job is cancelled while the second window is being fetched.
    /// </summary>
    [Fact]
    public async Task ImportLayerAsync_ReplacingExistingTargetWhenCancelledMidTransfer_RetainsPriorTargetAndSurfacesCancellation()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync("ImportReplacementCancelled");
        try
        {
            await SeedPriorTargetAsync(schemaName);
            using var cancellation = new CancellationTokenSource();
            var handler = new ReplacementFeatureServerHandler(["EEE", "FFF"])
            {
                SupportsPagination = false,
                OnObjectIdWindow = objectId =>
                {
                    if (objectId == 2)
                    {
                        cancellation.Cancel();
                    }
                }
            };
            var service = CreateService(handler);

            var act = () => service.ImportLayerAsync(
                BuildRequest(schemaName, overwrite: true) with { BatchSize = 1 },
                progress: null,
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            handler.ObjectIdWindowsServed.Should().Contain(1, "the first window must have loaded before cancellation");
            (await ReadCodesAsync(schemaName)).Should().Equal("AAA", "BBB");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private static GeoservicesImportRequest BuildRequest(string schemaName, bool overwrite) => new()
    {
        ServiceUrl = "https://example.com/arcgis/rest/services/Replacement/FeatureServer",
        LayerId = 0,
        TableName = TableName,
        TargetSchema = schemaName,
        TargetSrid = 4326,
        BatchSize = 10,
        RequestTimeoutSeconds = 5,
        MaxRetries = 0,
        OverwriteExisting = overwrite,
        AutoPublish = false,
        ImportAttachments = false
    };

    private GeoservicesImportService CreateService(HttpMessageHandler handler)
    {
        var restClient = new ArcGisRestClient(
            new HttpClient(handler),
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        return new GeoservicesImportService(
            restClient,
            new FixtureConnectionProvider(fixture),
            new Mock<ICrsRegistry>(MockBehavior.Loose).Object,
            new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors),
            NullLogger<GeoservicesImportService>.Instance,
            new GeoservicesLayerPublicationService(NullLogger<GeoservicesLayerPublicationService>.Instance));
    }

    private async Task SeedPriorTargetAsync(string schemaName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            CREATE TABLE "{schemaName}"."{TableName}" (
                objectid BIGSERIAL PRIMARY KEY,
                code VARCHAR(3),
                geom geometry(Point, 4326));
            INSERT INTO "{schemaName}"."{TableName}" (code, geom) VALUES
                ('AAA', ST_SetSRID(ST_MakePoint(-157.0, 21.0), 4326)),
                ('BBB', ST_SetSRID(ST_MakePoint(-157.5, 21.5), 4326));
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> ReadCodesAsync(string schemaName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT code FROM "{schemaName}"."{TableName}" ORDER BY code""",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var codes = new List<string>();
        while (await reader.ReadAsync())
        {
            codes.Add(reader.GetString(0));
        }

        return codes.ToArray();
    }

    /// <summary>
    /// ArcGIS FeatureServer mock serving one point layer whose records carry the supplied
    /// <c>code</c> values, with OBJECTIDs 1..n. Serves offset pages or object-id windows depending
    /// on <see cref="SupportsPagination"/>.
    /// </summary>
    private sealed class ReplacementFeatureServerHandler(string[] codes) : HttpMessageHandler
    {
        public bool SupportsPagination { get; init; } = true;

        public Action<long>? OnObjectIdWindow { get; init; }

        public List<long> ObjectIdWindowsServed { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;

            string payload;
            if (pathAndQuery.EndsWith("/FeatureServer/0?f=json", StringComparison.Ordinal))
            {
                payload = $$"""
                    {
                      "id": 0,
                      "name": "Replacement Layer",
                      "geometryType": "esriGeometryPoint",
                      "maxRecordCount": 10,
                      "hasAttachments": false,
                      "advancedQueryCapabilities": { "supportsPagination": {{(SupportsPagination ? "true" : "false")}} },
                      "extent": { "xmin": -158, "ymin": 21, "xmax": -157, "ymax": 22, "spatialReference": { "wkid": 4326 } },
                      "fields": [
                        { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
                        { "name": "code", "type": "esriFieldTypeString", "length": 3, "nullable": true }
                      ]
                    }
                    """;
            }
            else if (pathAndQuery.Contains("returnIdsOnly=true", StringComparison.Ordinal))
            {
                var ids = string.Join(",", Enumerable.Range(1, codes.Length));
                payload = $$"""{ "objectIdFieldName": "OBJECTID", "objectIds": [{{ids}}] }""";
            }
            else if (TryReadObjectIdWindow(pathAndQuery, out var objectId))
            {
                ObjectIdWindowsServed.Add(objectId);
                OnObjectIdWindow?.Invoke(objectId);
                payload = FeaturePage([objectId]);
            }
            else if (pathAndQuery.Contains("resultOffset=0", StringComparison.Ordinal))
            {
                payload = FeaturePage(Enumerable.Range(1, codes.Length).Select(static id => (long)id).ToArray());
            }
            else if (pathAndQuery.Contains("resultOffset=", StringComparison.Ordinal))
            {
                payload = FeaturePage([]);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected ArcGIS request path: {pathAndQuery}");
            }

            // Ownership of the HttpResponseMessage transfers to the HttpClient pipeline that invokes
            // this handler; it is disposed by the caller, not here (cs/local-not-disposed false positive).
            return Task.FromResult<HttpResponseMessage>(
                new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
        }

        private string FeaturePage(long[] objectIds)
        {
            var features = objectIds.Select(id => $$"""
                {
                  "attributes": { "OBJECTID": {{id}}, "code": "{{codes[id - 1]}}" },
                  "geometry": { "x": -157.{{id}}, "y": 21.{{id}} }
                }
                """);
            return $$"""
                {
                  "features": [{{string.Join(",", features)}}],
                  "exceededTransferLimit": false,
                  "spatialReference": { "wkid": 4326 }
                }
                """;
        }

        private static bool TryReadObjectIdWindow(string pathAndQuery, out long objectId)
        {
            objectId = 0;
            const string marker = "objectIds=";
            var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            var end = pathAndQuery.IndexOf('&', start);
            var value = Uri.UnescapeDataString(end < 0 ? pathAndQuery[start..] : pathAndQuery[start..end]);
            return long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out objectId);
        }
    }

    private sealed class FixtureConnectionProvider(PostgresFixture postgresFixture) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString()
            => new NpgsqlConnectionStringBuilder(postgresFixture.ConnectionString)
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
