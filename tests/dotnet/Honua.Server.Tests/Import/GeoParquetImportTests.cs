// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for GeoParquet import (upload) endpoint.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoParquetImportTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private string? _publishedServiceName;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_publishedServiceName is not null)
            {
                // This fixture reseeds shared catalog connections before services. Remove
                // only our publication first so its connection FK cannot poison the next test.
                await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM honua.services WHERE service_name = @service";
                command.Parameters.AddWithValue("service", _publishedServiceName);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await _fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(ImportLoadMode.Append, null, false)]
    [InlineData(ImportLoadMode.Append, 2022.5, false)]
    [InlineData(ImportLoadMode.Append, 2021.25, true)]
    [InlineData(ImportLoadMode.Upsert, null, false)]
    [InlineData(ImportLoadMode.Upsert, 2022.5, false)]
    [InlineData(ImportLoadMode.Upsert, 2021.25, true)]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    public async Task Import_IntoEpochBoundTarget_ValidatesEpochBeforeAppendOrUpsertWrites(
        ImportLoadMode mode, double? incomingEpoch, bool allowed)
    {
        var logicalName = "epoch_guard_" + Guid.NewGuid().ToString("N")[..12];
        var service = _fixture.GetService<IFileImportService>();
        await using var initial = new MemoryStream(await CreateEpochParquetAsync(2021.25, 10));
        var created = await service.ImportFileAsync(EpochRequest(initial, logicalName, ImportLoadMode.Upsert));
        created.Success.Should().BeTrue(created.ErrorMessage);
        var before = await ReadImportedTableSnapshotAsync(created);
        await using var incoming = new MemoryStream(await CreateEpochParquetAsync(incomingEpoch, 40, mode == ImportLoadMode.Append ? "B" : "A"));
        var result = await service.ImportFileAsync(EpochRequest(incoming, logicalName, mode));
        result.Success.Should().Be(allowed, result.ErrorMessage);
        var after = await ReadImportedTableSnapshotAsync(created);
        after.RelationOid.Should().Be(before.RelationOid, "append and upsert preserve the existing target identity");
        if (allowed)
        {
            var coordinates = ReadEpochRowCoordinates(after.Rows);
            if (mode == ImportLoadMode.Append)
            {
                coordinates.Should().Equal(("A", 10d), ("B", 40d));
            }
            else
            {
                coordinates.Should().Equal(("A", 40d));
            }
        }
        else
        {
            result.ErrorMessage.Should().Contain("epoch");
            after.Rows.Should().Be(before.Rows, "rejected epoch semantics must not insert, update, or repair rows");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_FailedEpochReplacement_RollsBackRowsIdentityAndEpochRegistration()
    {
        var logicalName = "epoch_rollback_" + Guid.NewGuid().ToString("N")[..12];
        var service = _fixture.GetService<IFileImportService>();
        await using var initial = new MemoryStream(await CreateEpochParquetAsync(2021.25, 10));
        var created = await service.ImportFileAsync(EpochRequest(initial, logicalName, ImportLoadMode.Upsert));
        created.Success.Should().BeTrue(created.ErrorMessage);
        var before = await ReadImportedTableSnapshotAsync(created);
        var commitReached = false;
        await using var replacement = new MemoryStream(await CreateEpochParquetAsync(2022.5, 40));
        var request = EpochRequest(replacement, logicalName, ImportLoadMode.Replace);
        var result = await service.ImportFileAsync(request with
        {
            CommitParticipant = (_, _) =>
            {
                commitReached = true;
                throw new InvalidOperationException("Induced epoch replacement rollback.");
            }
        });
        commitReached.Should().BeTrue("failure must occur inside the actual row/promotion commit transaction");
        result.Success.Should().BeFalse();
        (await ReadImportedTableSnapshotAsync(created)).Should().Be(before);
        foreach (var mode in new[] { ImportLoadMode.Append, ImportLoadMode.Upsert })
        {
            await using var mismatched = new MemoryStream(await CreateEpochParquetAsync(
                2022.5, 40, mode == ImportLoadMode.Append ? "B" : "A"));
            var rejected = await service.ImportFileAsync(EpochRequest(mismatched, logicalName, mode));
            rejected.Success.Should().BeFalse("rollback must retain the original epoch binding for {0}", mode);
            rejected.ErrorMessage.Should().Contain("epoch");
            (await ReadImportedTableSnapshotAsync(created)).Should().Be(before);
        }
        await using var append = new MemoryStream(await CreateEpochParquetAsync(2021.25, 50, "B"));
        var appended = await service.ImportFileAsync(EpochRequest(append, logicalName, ImportLoadMode.Append));
        appended.Success.Should().BeTrue(appended.ErrorMessage);
        var afterAppend = await ReadImportedTableSnapshotAsync(created);
        afterAppend.RelationOid.Should().Be(before.RelationOid);
        ReadEpochRowCoordinates(afterAppend.Rows).Should().Equal(("A", 10d), ("B", 50d));
    }

    private static (string Key, double Longitude)[] ReadEpochRowCoordinates(string rows)
    {
        using var document = JsonDocument.Parse(rows);
        return document.RootElement.EnumerateArray().Select(row =>
            (Key: row[2].GetProperty("key").GetString()!,
             Longitude: new WKBReader().Read(Convert.FromHexString(row[1].GetString()!)).Coordinate.X))
            .OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
    }

    private static ImportRequest EpochRequest(Stream source, string table, ImportLoadMode mode) => new()
    {
        FileStream = source,
        FileName = "dynamic-epoch.parquet",
        TableName = table,
        TargetSchema = "honua_data",
        TargetSrid = 7912,
        LoadMode = mode,
        OverwriteExisting = true,
        KeyColumns = ["key"]
    };

    private static async Task<byte[]> CreateEpochParquetAsync(double? epoch, double longitude, string propertyKey = "A")
    {
        GeoParquetProjJsonCatalog.TryGetProjJson(7912, out var definition).Should().BeTrue();
        using var crs = JsonDocument.Parse(definition!);
        var point = new Point(longitude, 20, 30);
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false).Write(point);
        return await CreateGeometryParquetAsync(wkb, "Point Z", sourceCrs: crs.RootElement, coordinateEpoch: epoch, propertyKey: propertyKey);
    }

    private async Task<(long RelationOid, string Rows)> ReadImportedTableSnapshotAsync(ImportResult imported)
    {
        using var identifiers = new Npgsql.NpgsqlCommandBuilder();
        var qualified = identifiers.QuoteIdentifier(imported.Schema!) + "." + identifiers.QuoteIdentifier(imported.PhysicalTableName!);
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT @qualified::regclass::oid::bigint, COALESCE(jsonb_agg(jsonb_build_array(id, encode(ST_AsEWKB(geometry), 'hex'), properties) ORDER BY id)::text, '[]') FROM {qualified}";
        command.Parameters.AddWithValue("qualified", qualified);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt64(0), reader.GetString(1));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{connectionId}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Upload_WithDynamicCrsEpoch_PreservesEpochThroughPublicationAndSameCrsExport()
    {
        const int srid = 7912; // Authoritative ITRF2014 geographic 3D dynamic reference frame.
        const double epoch = 2021.25;
        GeoParquetProjJsonCatalog.TryGetProjJson(srid, out var definition).Should().BeTrue();
        using var crs = JsonDocument.Parse(definition!);
        var point = new WKTReader().Read("POINT Z (10 20 30)");
        var bytes = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false).Write(point);
        var payload = await CreateGeometryParquetAsync(bytes, "Point Z", sourceCrs: crs.RootElement, coordinateEpoch: epoch);
        using var content = CreateUploadContent(payload, "epoch.parquet", "dynamic_epoch_roundtrip", srid);
        using var response = await _client.PostAsync("/api/v1/admin/import/upload", content);
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, json);
        using var imported = JsonDocument.Parse(json);
        imported.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(json);

        var connectionId = await _fixture.GetTestSecureConnectionIdAsync();
        connectionId.Should().NotBeNull();
        _publishedServiceName = $"geoparquet_epoch_{Guid.NewGuid():N}";
        using var publishResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/connections/{connectionId}/layers",
            new
            {
                Schema = imported.RootElement.GetProperty("schema").GetString(),
                Table = imported.RootElement.GetProperty("physicalTableName").GetString(),
                LayerName = "Dynamic CRS epoch readback",
                ServiceName = _publishedServiceName,
                GeometryColumn = "geometry",
                GeometryType = "Point",
                Srid = srid,
                HasZ = true,
                PrimaryKey = "id",
                Fields = new[] { "id", "properties" },
                Enabled = true
            });
        var publishJson = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created, publishJson);
        using var published = JsonDocument.Parse(publishJson);
        var layerId = published.RootElement.GetProperty("data").GetProperty("layerId").GetInt32();
        using var exportResponse = await _client.GetAsync(
            $"/rest/services/{_publishedServiceName}/FeatureServer/{layerId}/query?where=1%3D1&outFields=*&returnGeometry=true&returnZ=true&outSR={srid}&f=parquet");
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync();
        var exportMediaType = exportResponse.Content.Headers.ContentType?.MediaType;
        var exportDiagnostic = exportMediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
            ? System.Text.Encoding.UTF8.GetString(exportBytes.AsSpan(0, Math.Min(exportBytes.Length, 4096)))
            : $"Response media type: {exportMediaType}";
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK, exportDiagnostic);
        exportMediaType.Should().Be("application/vnd.apache.parquet", exportDiagnostic);
        if (Environment.GetEnvironmentVariable("HONUA_CNG_VECTOR_ARTIFACT_DIRECTORY") is { Length: > 0 } artifactDirectory)
        {
            Directory.CreateDirectory(artifactDirectory);
            await File.WriteAllBytesAsync(Path.Join(artifactDirectory, "dynamic-epoch-source.parquet"), payload);
            await File.WriteAllBytesAsync(Path.Join(artifactDirectory, "dynamic-epoch-export.parquet"), exportBytes);
        }
        await using var exported = new MemoryStream(exportBytes);
        using var parquet = await Parquet.ParquetReader.CreateAsync(exported);
        using var metadata = JsonDocument.Parse(parquet.CustomMetadata["geo"]);
        var geometryMetadata = metadata.RootElement.GetProperty("columns").GetProperty(
            metadata.RootElement.GetProperty("primary_column").GetString()!);
        geometryMetadata.TryGetProperty("epoch", out var actualEpoch).Should().BeTrue("the durable imported epoch must survive publication and same-CRS export");
        actualEpoch.GetDouble().Should().Be(epoch);
        geometryMetadata.GetProperty("crs").GetProperty("id").GetProperty("code").GetInt32().Should().Be(srid);
        using var rows = parquet.OpenRowGroupReader(0);
        var field = parquet.Schema.GetDataFields().Single(field => field.Name == metadata.RootElement.GetProperty("primary_column").GetString());
        var geometries = await rows.ReadColumnAsync(field);
        var roundtrip = new WKBReader().Read(((byte[][])geometries.Data)[0]);
        roundtrip.Coordinate.X.Should().Be(10);
        roundtrip.Coordinate.Y.Should().Be(20);
        roundtrip.Coordinate.Z.Should().Be(30);
    }

    [Theory]
    [InlineData(1, false, "Point")]
    [InlineData(1, true, "Point")]
    [InlineData(3, false, "Polygon")]
    [InlineData(3, true, "Polygon")]
    [InlineData(7, false, "GeometryCollection")]
    [InlineData(7, true, "GeometryCollection")]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithEmptyDeclaredWkbDimensions_PreservesDatabaseDimensions(int type, bool hasZ, string geometryType)
    {
        var rawType = (uint)(type + (hasZ ? 1000 : 0));
        var wkb = new byte[type == 1 ? 5 + (hasZ ? 24 : 16) : 9];
        wkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), rawType);
        if (type == 1)
        {
            for (var ordinate = 0; ordinate < (hasZ ? 3 : 2); ordinate++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5 + ordinate * sizeof(double)), double.NaN);
            }
        }
        var payload = await CreateGeometryParquetAsync(wkb, geometryType + (hasZ ? " Z" : string.Empty));
        using var content = CreateUploadContent(payload, "empty-dimensions.parquet", $"empty_dimensions_{type}_{hasZ}");
        using var response = await _client.PostAsync("/api/v1/admin/import/upload", content);
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, json);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(json);
        var schema = document.RootElement.GetProperty("schema").GetString()!;
        var table = document.RootElement.GetProperty("physicalTableName").GetString()!;
        using var identifiers = new Npgsql.NpgsqlCommandBuilder();
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ST_NDims(geometry), ST_IsEmpty(geometry), ST_AsBinary(geometry, 'NDR') FROM {identifiers.QuoteIdentifier(schema)}.{identifiers.QuoteIdentifier(table)}";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue(json);
        reader.GetInt32(0).Should().Be(hasZ ? 3 : 2);
        reader.GetBoolean(1).Should().BeTrue();
        BinaryPrimitives.ReadUInt32LittleEndian(reader.GetFieldValue<byte[]>(2).AsSpan(1)).Should()
            .BeOneOf(rawType, (uint)type | (hasZ ? 0x80000000u : 0u));
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    [Trait("Tier", "Integration")]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithPlanarAntimeridianPolygon_PreservesStoredCartesianCoverage(bool includePlanarEdges)
    {
        var expected = new WKTReader().Read("POLYGON ((179 10, -179 10, -179 11, 179 11, 179 10))");
        expected.SRID = 4326;
        expected.Area.Should().Be(358d);
        var payload = await CreateGeometryParquetAsync(new WKBWriter().Write(expected), "Polygon", includePlanarEdges);
        using var content = CreateUploadContent(payload, "planar-polygon.parquet", $"planar_polygon_{includePlanarEdges}");
        using var response = await _client.PostAsync("/api/v1/admin/import/upload", content);
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, json);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(json);
        var schema = document.RootElement.GetProperty("schema").GetString()!;
        var table = document.RootElement.GetProperty("physicalTableName").GetString()!;
        using var identifiers = new Npgsql.NpgsqlCommandBuilder();
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ST_Area(geometry), ST_AsBinary(geometry, 'NDR'), ST_SRID(geometry) FROM {identifiers.QuoteIdentifier(schema)}.{identifiers.QuoteIdentifier(table)}";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue(json);
        reader.GetDouble(0).Should().BeApproximately(358d, 1e-9, "planar edges retain the long-side Cartesian polygon");
        var actualWkb = reader.GetFieldValue<byte[]>(1);
        BinaryPrimitives.ReadUInt32LittleEndian(actualWkb.AsSpan(1)).Should().Be(3u);
        new WKBReader().Read(actualWkb).EqualsExact(expected).Should().BeTrue();
        reader.GetInt32(2).Should().Be(4326);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{connectionId}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Upload_WithBoundedQueryWriterOutput_PreservesRowsAcrossRowGroupBoundaries()
    {
        const int rowCount = 10000;
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "query-export", Name = "query_export" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "geometry", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geometry"
            }
        };
        var writer = new WKBWriter();
        var features = Enumerable.Range(1, rowCount).Select(id => Feature.Create(id,
            writer.Write(new Point(-120 + id * 0.001, 35 + id * 0.0001)),
            ImmutableDictionary<string, object?>.Empty.Add("name", $"export-{id}"))).ToImmutableArray();
        var (payload, _) = GeoParquetFeatureWriter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(rowCount, features), resource, "objectid", true, 4326,
            false, false, new GeometryLimits());
        using var content = CreateUploadContent(payload, "query-export.parquet", "geoparquet_query_roundtrip");
        using var response = await _client.PostAsync("/api/v1/admin/import/upload", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseJson = await response.Content.ReadAsStringAsync();
        using var result = JsonDocument.Parse(responseJson);
        result.RootElement.GetProperty("success").GetBoolean().Should().BeTrue(responseJson);

        var rows = await ReadImportedRowsAsync(responseJson, rowCount, "id IN (1,1024,1025,9999,10000)");
        rows.Select(row => row.ObjectId).Should().BeEquivalentTo(new long?[] { 1, 1024, 1025, 9999, 10000 });
        foreach (var row in rows)
        {
            var id = row.ObjectId!.Value;
            row.Name.Should().Be($"export-{id}");
            var point = row.Geometry.Should().BeOfType<Point>().Subject;
            point.X.Should().BeApproximately(-120 + id * 0.001, 1e-9);
            point.Y.Should().BeApproximately(35 + id * 0.0001, 1e-9);
            point.SRID.Should().Be(4326);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithGeoParquet_ImportsFeaturesToTable()
    {
        // Arrange
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            encoding: "WKB",
            crs: GeoParquetTestFactory.CrsStyle.ProjJson);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "sample.parquet", "geoparquet_import_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_import_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");

        // honua-server#4396: the response substring above proves the import *reported* success.
        // Read the persisted rows back and assert the values the fixture actually wrote, so an
        // import that committed the wrong geometry, dropped the attributes or reprojected the
        // point cannot pass. The import stages into a provider-chosen physical table
        // (honua_data.imported_geoparquet_import_test), which the response reports through
        // physicalTableName/schema -- use those rather than the caller-supplied logical name.
        var rows = await ReadImportedRowsAsync(responseContent);
        var row = rows.Should().ContainSingle().Subject;
        row.ObjectId.Should().Be(1L);
        row.Name.Should().Be("Test Feature");

        // GeoParquetTestFactory writes POINT(-122.4194 37.7749) in EPSG:4326 and the upload
        // requests TargetSrid=4326, so the stored point must be that point, unmoved.
        row.Geometry.Should().BeOfType<Point>();
        var point = (Point)row.Geometry!;
        point.X.Should().BeApproximately(-122.4194, 1e-9);
        point.Y.Should().BeApproximately(37.7749, 1e-9);
        point.SRID.Should().Be(4326);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithNestedColumn_PreservesNestedProperties()
    {
        // Arrange - create file with a StructField column
        await using var stream = await GeoParquetTestFactory.CreateWithNestedColumnAsync(
            crs: GeoParquetTestFactory.CrsStyle.ProjJson);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "nested.parquet", "geoparquet_nested_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_nested_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().NotContain("will be skipped");
        var rows = await ReadImportedRowsAsync(responseContent);
        var row = rows.Should().ContainSingle().Subject;
        row.Properties.GetProperty("metadata").GetProperty("key").GetString().Should().Be("test_key");
        row.Properties.GetProperty("metadata").GetProperty("value").GetString().Should().Be("test_value");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithNonWkbEncoding_RejectsWithError()
    {
        // Arrange - create file with "point" encoding
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            encoding: "point",
            crs: GeoParquetTestFactory.CrsStyle.ProjJson);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "point_encoded.parquet", "geoparquet_reject_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_reject_test");
        responseContent.Should().Contain("\"success\":false");
        responseContent.Should().Contain("native geometry encodings");
        responseContent.Should().Contain("WKB");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithNullGeometryRow_PreservesEveryRowAndProperties()
    {
        // Arrange - 2 rows: one with geometry, one with null geometry
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            crs: GeoParquetTestFactory.CrsStyle.ProjJson,
            includeNullGeometryRow: true);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "null_geom.parquet", "geoparquet_null_geom_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_null_geom_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":2");
        responseContent.Should().NotContain("skipped because geometry was null");
        var rows = await ReadImportedRowsAsync(responseContent, expectedCount: 2);
        rows.Should().HaveCount(2);
        var withoutGeometry = rows.Single(item => item.ObjectId == 2);
        withoutGeometry.Name.Should().Be("No Geometry");
        withoutGeometry.Geometry.Should().BeNull();
        var row = rows.Single(item => item.ObjectId == 1);
        row.ObjectId.Should().Be(1);
        row.Name.Should().Be("Test Feature");
        var point = row.Geometry.Should().BeOfType<Point>().Which;
        point.X.Should().BeApproximately(-122.4194, 1e-9);
        point.Y.Should().BeApproximately(37.7749, 1e-9);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithTimeAndBinaryColumns_ImportsSuccessfully()
    {
        // Arrange - create file with TimeOnly and byte[] attribute columns
        await using var stream = await GeoParquetTestFactory.CreateWithTimeAndBinaryColumnsAsync(
            crs: GeoParquetTestFactory.CrsStyle.ProjJson);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "time_binary.parquet", "geoparquet_time_binary_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_time_binary_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":1");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithInt16Column_ImportsSuccessfully()
    {
        // Arrange - create file with an INT16 (short) attribute column
        await using var stream = await GeoParquetTestFactory.CreateWithInt16ColumnAsync(
            crs: GeoParquetTestFactory.CrsStyle.ProjJson);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "int16.parquet", "geoparquet_int16_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_int16_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":1");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Upload_WithLargeSingleRowGroup_ImportsFeatures()
    {
        // pyarrow/GeoPandas writes a 150,000-row table as one row group by default.
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            rowCount: 150_000,
            crs: GeoParquetTestFactory.CrsStyle.ProjJson);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "large_rg.parquet", "geoparquet_large_rg_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_large_rg_test");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":150000");
        var rows = await ReadImportedRowsAsync(responseContent, 150_000, "id IN (1,75000,150000)", expectAsyncPublish: true);
        rows.Select(row => row.ObjectId).Should().BeEquivalentTo(new long?[] { 1, 75000, 150000 });
        foreach (var row in rows)
        {
            row.Name.Should().Be($"Feature {row.ObjectId}");
            var point = row.Geometry.Should().BeOfType<Point>().Which;
            point.X.Should().BeApproximately(-122.4194, 1e-9);
            point.Y.Should().BeApproximately(37.7749, 1e-9);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithMissingGeoMetadata_RejectsWithSpecificError()
    {
        // Arrange - a plain Parquet file with no "geo" metadata key.
        // The import path should surface the specific validation message
        // instead of collapsing to the generic "Import failed."
        await using var stream = await GeoParquetTestFactory.CreateWithoutGeoMetadataAsync();
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "plain.parquet", "geoparquet_no_geo_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_no_geo_test");
        responseContent.Should().Contain("\"success\":false");
        // Must surface the specific validation reason, not "Import failed."
        responseContent.Should().Contain("geo");
    }

    private static async Task<byte[]> CreateGeometryParquetAsync(
        byte[] wkb, string geometryType, bool includePlanarEdges = false, JsonElement? sourceCrs = null, double? coordinateEpoch = null, string? propertyKey = null)
    {
        var field = new Parquet.Schema.DataField<byte[]>("geometry", true);
        var key = new Parquet.Schema.DataField<string>("key");
        var schema = propertyKey is null ? new Parquet.Schema.ParquetSchema(field) : new Parquet.Schema.ParquetSchema(field, key);
        var geometryMetadata = new Dictionary<string, object?>
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = new[] { geometryType }
        };
        if (sourceCrs.HasValue)
        {
            geometryMetadata["crs"] = sourceCrs.Value;
        }
        if (coordinateEpoch.HasValue)
        {
            geometryMetadata["epoch"] = coordinateEpoch.Value;
        }
        if (includePlanarEdges)
        {
            geometryMetadata["edges"] = "planar";
        }
        await using var stream = new MemoryStream();
        using (var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = new Dictionary<string, string>
            {
                ["geo"] = JsonSerializer.Serialize(new
                {
                    version = "1.1.0",
                    primary_column = "geometry",
                    columns = new Dictionary<string, object?> { ["geometry"] = geometryMetadata }
                })
            };
            using var rows = writer.CreateRowGroup();
            await rows.WriteColumnAsync(new Parquet.Data.DataColumn(field, new byte[]?[] { wkb }));
            if (propertyKey is not null)
            {
                await rows.WriteColumnAsync(new Parquet.Data.DataColumn(key, new[] { propertyKey }));
            }
        }
        return stream.ToArray();
    }

    // Publish the actual imported table, then read it through the same public query route
    // a client uses. No copied rows or response-reported feature counts serve as the oracle.
    private async Task<IReadOnlyList<ImportedRow>> ReadImportedRowsAsync(
        string importResponseJson, int expectedCount = 1, string where = "1=1", bool expectAsyncPublish = false)
    {
        using var document = JsonDocument.Parse(importResponseJson);
        var root = document.RootElement;
        var connectionId = await _fixture.GetTestSecureConnectionIdAsync();
        connectionId.Should().NotBeNull();
        _publishedServiceName = $"geoparquet_readback_{Guid.NewGuid():N}";
        using var publishResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/connections/{connectionId}/layers",
            new
            {
                Schema = root.GetProperty("schema").GetString(),
                Table = root.GetProperty("physicalTableName").GetString(),
                LayerName = "GeoParquet readback",
                ServiceName = _publishedServiceName,
                GeometryColumn = "geometry",
                PrimaryKey = "id",
                Fields = new[] { "id", "properties" },
                Enabled = true
            });
        var publishJson = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(
            expectAsyncPublish ? HttpStatusCode.Accepted : HttpStatusCode.Created, publishJson);
        using var published = JsonDocument.Parse(publishJson);
        var layer = published.RootElement.GetProperty("data");
        var serviceName = _publishedServiceName;
        int layerId;
        if (expectAsyncPublish)
        {
            var jobId = layer.GetProperty("jobId").GetString();
            Guid.TryParseExact(jobId, "N", out _).Should().BeTrue(publishJson);
            var statusUrl = $"/api/v1/admin/connections/{connectionId}/layers/jobs/{jobId}";
            layer.GetProperty("statusUrl").GetString().Should().Be(statusUrl);
            publishResponse.Headers.Location.Should().NotBeNull();
            publishResponse.Headers.Location!.OriginalString.Should().Be(statusUrl);
            layer.GetProperty("rowsTotal").GetInt64().Should().Be(expectedCount);
            layer.GetProperty("status").GetString().Should().Be("Queued");
            layerId = await WaitForPublishedLayerAsync(statusUrl, jobId!, expectedCount);
        }
        else
        {
            layer.GetProperty("serviceName").GetString().Should().Be(serviceName);
            layerId = layer.GetProperty("layerId").GetInt32();
        }

        var queryPath = $"/rest/services/{serviceName}/FeatureServer/{layerId}/query";
        using var countResponse = await _client.GetAsync($"{queryPath}?where=1%3D1&returnCountOnly=true&f=json");
        countResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var countDocument = JsonDocument.Parse(await countResponse.Content.ReadAsStringAsync());
        countDocument.RootElement.GetProperty("count").GetInt32().Should().Be(expectedCount);

        using var response = await _client.GetAsync(
            $"{queryPath}?where={Uri.EscapeDataString(where)}&outFields=*&returnGeometry=true&outSR=4326&f=json");
        var responseJson = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, responseJson);
        using var queried = JsonDocument.Parse(responseJson);
        queried.RootElement.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        var rows = new List<ImportedRow>();
        foreach (var feature in queried.RootElement.GetProperty("features").EnumerateArray())
        {
            var propertiesValue = feature.GetProperty("attributes").GetProperty("properties");
            using var propertiesDocument = JsonDocument.Parse(propertiesValue.ValueKind == JsonValueKind.String
                ? propertiesValue.GetString()!
                : propertiesValue.GetRawText());
            var properties = propertiesDocument.RootElement;
            Point? point = null;
            if (feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind != JsonValueKind.Null)
            {
                point = new Point(geometry.GetProperty("x").GetDouble(), geometry.GetProperty("y").GetDouble()) { SRID = 4326 };
            }

            rows.Add(new ImportedRow(properties.GetProperty("objectid").GetInt64(), properties.GetProperty("name").GetString(), point, properties.Clone()));
        }

        return rows;
    }

    private async Task<int> WaitForPublishedLayerAsync(string statusUrl, string jobId, int expectedCount)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            using var response = await _client.GetAsync(statusUrl, deadline.Token);
            var json = await response.Content.ReadAsStringAsync(deadline.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK, json);
            using var document = JsonDocument.Parse(json);
            var progress = document.RootElement.GetProperty("data");
            progress.GetProperty("jobId").GetString().Should().Be(jobId);
            progress.GetProperty("rowsTotal").GetInt64().Should().Be(expectedCount);
            var materialized = progress.GetProperty("rowsMaterialized").GetInt64();
            materialized.Should().BeInRange(0, expectedCount);
            var status = progress.GetProperty("status").GetString();
            status.Should().BeOneOf(["Queued", "Processing", "Completed"], json);
            if (status == "Completed")
            {
                materialized.Should().Be(expectedCount);
                var layerId = progress.GetProperty("layerId").GetInt32();
                layerId.Should().BeGreaterThan(0);
                return layerId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }
    }

    private sealed record ImportedRow(long? ObjectId, string? Name, Geometry? Geometry, JsonElement Properties);

    private static MultipartFormDataContent CreateUploadContent(byte[] fileBytes, string fileName, string tableName, int targetSrid = 4326)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = fileName
        };
        content.Add(fileContent);
        content.Add(new StringContent(tableName), "TableName");
        content.Add(new StringContent(targetSrid.ToString(System.Globalization.CultureInfo.InvariantCulture)), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");
        return content;
    }
}
