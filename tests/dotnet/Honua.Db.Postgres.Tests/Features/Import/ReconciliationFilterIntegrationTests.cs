// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Queries.Filters;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>Shared canonical filter compilation must execute against real imported columns.</summary>
[Collection("Database")]
public sealed class ReconciliationFilterIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(ReconciliationFilterIntegrationTests));
        await fixture.ExecuteAsync($"""
            CREATE TABLE {_schema}.flights (objectid bigint PRIMARY KEY, flight_date date, status text, geom geometry(Point,4326));
            INSERT INTO {_schema}.flights VALUES
                (1, DATE '2026-01-02', 'DateOfFlight', ST_SetSRID(ST_Point(1,1),4326)),
                (2, DATE '2026-01-03', 'dateofflight', ST_SetSRID(ST_Point(2,2),4326)),
                (3, DATE '2025-01-01', 'DateOfFlight', ST_SetSRID(ST_Point(50,50),4326)),
                (4, DATE '2026-01-02', 'other', ST_SetSRID(ST_Point(60,60),4326));
            """);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [Theory]
    [InlineData("DateOfFlight", false)]
    [InlineData("\"DateOfFlight\"", false)]
    [InlineData("[DateOfFlight]", false)]
    [InlineData("\"DateOfFlight\"", true)]
    public async Task Reconcile_DateAndMappedProperties_UsesCanonicalPredicateAcrossRealProbes(string dateField, bool dedicatedTarget)
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new() { Id = "flights" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["flights-binding"],
            Spatial = new()
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom"
            },
            SchemaFields =
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
                new() { Name = "flight_date", Type = MetadataV2FieldType.Date },
                new() { Name = "status", Type = MetadataV2FieldType.String }
            ]
        };
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Resources = [resource],
            StorageBindings = [new()
            {
                Metadata = new() { Id = "flights-binding" }, ResourceId = "flights", StorageLayerId = 17,
                StorageType = MetadataV2StorageType.RelationalTable, Locator = _schema + ".flights"
            }]
        }, "test", DateTimeOffset.UtcNow);
        var metadata = Substitute.For<IMetadataV2GraphProvider>();
        metadata.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var connections = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        connections.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(call.Arg<CancellationToken>());
            return (DbConnection)connection;
        });
        var reader = new PostgresStorageMappedFeatureReader(connections,
            new DefaultObjectPoolProvider().Create(new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy()),
            resource, new FeatureStorageMapping("flights", _schema, PrimaryKeyColumn: "objectid", GeometryColumn: "geom", StorageSrid: 4326),
            connection: null, connectionEncryptionService: null);
        var filters = new FilterExpressionService(new FilterExpressionTranslator(new PostgresSqlFilterTranslator(useJsonAttributes: true)));
        var queries = new ReconciliationQueryBuilder(metadata, filters);
        var service = new LayerReconciliationService(reader, TimeProvider.System,
            NullLogger<LayerReconciliationService>.Instance, queries);
        var layer = new LayerReconciliationLayerInput
        {
            SourceLayerId = "source-flights", SourceLayerName = "Flights", TargetHonuaLayerId = 17,
            SourceFeatureCount = 2, SourceExtent = BoundingBox.Create(1, 1, 2, 2, 4326),
            SourceFieldNames = ["objectid", "flight_date", "status"],
            FilterMirror = $"{dateField} >= DATE '2026-01-02' AND UPPER(\"StatusText\") = 'DATEOFFLIGHT'",
            FilterFieldMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dateofflight"] = "flight_date", ["StatusText"] = "status",
                ["UPPER"] = "not_a_function", ["DATE"] = "not_a_keyword"
            },
            TargetContainsOnlyImportedFeatures = dedicatedTarget
        };

        var artifact = await service.ReconcileAsync(new LayerReconciliationRequest
        {
            RunId = "filter-proof", SourceKind = "arcgis-geoservices-rest", Layers = [layer]
        });

        var report = artifact.Layers.Should().ContainSingle().Subject;
        report.Count.TargetCount.Should().Be(dedicatedTarget ? 4 : 2);
        report.Count.Classification.Should().Be(dedicatedTarget ? "fail" : "pass");
        report.Extent.Target!.Value.MinX.Should().Be(1);
        report.Extent.Target.Value.MaxX.Should().Be(dedicatedTarget ? 60 : 2);
        report.Geometry.Sampled.Should().Be(dedicatedTarget ? 4 : 2);
        report.Classification.Should().Be(dedicatedTarget ? "fail" : "pass");

        var query = await queries.BuildAsync(layer);
        query.Where.Should().BeNull();
        var rows = await reader.QueryAsync(17, query);
        rows.Items.Select(row => row.Id).Should().BeEquivalentTo(dedicatedTarget ? [1L, 2L, 3L, 4L] : new[] { 1L, 2L });
        if (!dedicatedTarget)
        {
            query.SqlFilter.Should().NotBeNull();
            query.SqlFilter!.Parameters.Should().Contain("DATEOFFLIGHT");
        }
    }
}
