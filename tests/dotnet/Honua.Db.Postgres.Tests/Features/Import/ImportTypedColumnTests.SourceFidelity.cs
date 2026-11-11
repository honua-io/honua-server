// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class ImportTypedColumnTests
{
    [IntegrationTheory]
    [InlineData(12_477, 127, 1899)]
    [InlineData(12_152, 345, 2826)]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_DetailedZoningGeometry_PreservesGeometryAndSourceIdentity(
        int totalVertices, int ringCount, int sourceId)
    {
        var polygons = Enumerable.Range(0, ringCount).Select(offset =>
        {
            var column = offset % 20;
            var row = offset / 20; // Integer rows keep the polygons on a twenty-column grid.
            var vertexCount = totalVertices / ringCount + (offset < totalVertices % ringCount ? 1 : 0);
            var ring = Enumerable.Range(0, vertexCount).Select(index =>
            {
                var angle = index * Math.Tau / vertexCount;
                return new[]
                {
                    -157 + column * 0.004 + Math.Cos(angle) * 0.001,
                    20 + row * 0.004 + Math.Sin(angle) * 0.001
                };
            }).ToList();
            ring.Add(ring[0]);
            return new[] { ring.ToArray() };
        }).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = new[]
            {
                new
                {
                    type = "Feature",
                    geometry = new { type = "MultiPolygon", coordinates = polygons },
                    properties = new { objectid = sourceId, zone_code = "999" }
                }
            }
        });

        var imported = await ImportAsync("detailed_zoning", "zoning.geojson", Encoding.UTF8.GetBytes(json));
        imported.Result.FeatureCount.Should().Be(1);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT objectid, ST_NPoints(geometry), ST_NumGeometries(geometry),
                   ST_IsValid(geometry), ST_Area(geometry) > 0
            FROM "{imported.Schema}"."{imported.Table}"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(sourceId);
        reader.GetInt32(1).Should().Be(totalVertices + ringCount);
        reader.GetInt32(2).Should().Be(ringCount);
        reader.GetBoolean(3).Should().BeTrue();
        reader.GetBoolean(4).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(ImportLoadMode.Replace)]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Upsert)]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_SourceCreatedAt_RejectsBeforeChangingExistingData(ImportLoadMode mode)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportTypedColumnTests));
        try
        {
            await InstallImportFunctionsAsync();
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var service = new StreamingFileImportService(
                provider,
                new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
                new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
                new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
                schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));
            const string original = """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"original"}}]}
                """;
            var created = await ImportIntoAsync(service, schema, "timeline", original, ImportLoadMode.Replace, []);
            const string collision = """
                {"type":"FeatureCollection","features":[
                  {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},
                   "properties":{"name":"replacement","created_at":"2026-10-30T00:00:00.000Z"}}]}
                """;

            var result = await ImportIntoAsync(service, schema, "timeline", collision, mode,
                mode == ImportLoadMode.Upsert ? ["name"] : [], expectedSuccess: false);

            result.ValidationErrors.Should().Contain(issue => issue.Field == "properties.created_at");
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT string_agg(name, ',') FROM \"{schema}\".\"{created.PhysicalTableName}\"";
            (await command.ExecuteScalarAsync()).Should().Be("original");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

}
