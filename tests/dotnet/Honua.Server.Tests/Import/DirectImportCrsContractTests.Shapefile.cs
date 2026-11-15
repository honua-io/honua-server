// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using Npgsql;

namespace Honua.Server.Tests.Import;

public sealed partial class DirectImportCrsContractTests
{
    private const string StatePlanePrj = """
        PROJCS["NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Feet",GEOGCS["GCS_North_American_1983",DATUM["D_North_American_1983",SPHEROID["GRS_1980",6378137.0,298.257222101]],PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Lambert_Conformal_Conic"],PARAMETER["False_Easting",984250.0],PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",-74.0],PARAMETER["Standard_Parallel_1",41.0333333333333],PARAMETER["Standard_Parallel_2",40.6666666666667],PARAMETER["Latitude_Of_Origin",40.1666666666667],UNIT["US survey foot",0.304800609601219]]
        """;

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task PreviewFile_ProjectedEsriShapefile_DetectsStatePlane()
    {
        using var result = await PostProjectedShapefileAsync("preview", StatePlanePrj);

        var crs = result.RootElement.GetProperty("coordinateSystem");
        crs.GetProperty("detectedSrid").GetInt32().Should().Be(2263);
        crs.GetProperty("sourceCrsDeclared").GetBoolean().Should().BeTrue();
        crs.GetProperty("requiresSourceOverride").GetBoolean().Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(null, 2263)]
    [InlineData("4326", 4326)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_ProjectedEsriShapefile_StoresCorrectCoordinates(
        string? targetSrid, int storedSrid)
    {
        using var result = await PostProjectedShapefileAsync(
            "upload", StatePlanePrj, new ProjectedUploadOptions(NewImportFields()["TableName"], targetSrid));
        var imported = result.RootElement;
        imported.GetProperty("success").GetBoolean().Should().BeTrue(imported.GetRawText());
        var schema = imported.GetProperty("schema").GetString()!;
        var table = imported.GetProperty("physicalTableName").GetString()!;
        _importedTables.Add((schema, table));
        imported.GetProperty("detectedSrid").GetInt32().Should().Be(2263);
        imported.GetProperty("sourceSrid").GetInt32().Should().Be(2263);
        imported.GetProperty("storedSrid").GetInt32().Should().Be(storedSrid);
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var query = new NpgsqlCommand(
            $"SELECT ST_SRID(geometry), ST_X(geometry), ST_Y(geometry) FROM {QualifiedTable(schema, table)}", connection);
        await using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(storedSrid);
        reader.GetDouble(1).Should().BeApproximately(storedSrid == 2263 ? 971908.4588538355 : -74.044502, 0.000001);
        reader.GetDouble(2).Should().BeApproximately(storedSrid == 2263 ? 190390.83886990085 : 40.689247, 0.000001);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_UnrecognizedProjectedShapefile_ExplainsSourceOverride()
    {
        var unrecognized = StatePlanePrj.Replace("D_North_American_1983", "D_Unknown", StringComparison.Ordinal);
        using var result = await PostProjectedShapefileAsync(
            "upload", unrecognized, new ProjectedUploadOptions(NewImportFields()["TableName"]));

        result.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("errorCode").GetString().Should().Be("import.source_srid_required");
        result.RootElement.GetProperty("errorMessage").GetString().Should()
            .Contain("NAD_1983_StatePlane_New_York_Long_Island_FIPS_3104_Feet")
            .And.Contain("SourceSrid");
    }

    private async Task<JsonDocument> PostProjectedShapefileAsync(
        string operation, string prj, ProjectedUploadOptions? upload = null)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(CreateProjectedShapefileZip(prj));
        content.Add(fileContent, "File", "projected.zip");
        using var tableContent = upload is null ? null : new StringContent(upload.TableName);
        using var overwriteContent = upload is null ? null : new StringContent("true");
        using var targetContent = upload?.TargetSrid is null ? null : new StringContent(upload.TargetSrid);
        if (tableContent is not null) content.Add(tableContent, "TableName");
        if (overwriteContent is not null) content.Add(overwriteContent, "OverwriteExisting");
        if (targetContent is not null) content.Add(targetContent, "TargetSrid");
        using var response = await _fixture.Client.PostAsync($"/api/v1/admin/import/{operation}", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private sealed record ProjectedUploadOptions(string TableName, string? TargetSrid = null);

    private static byte[] CreateProjectedShapefileZip(string prj)
    {
        var directory = Path.Join(Path.GetTempPath(), $"honua-projected-shp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "point.shp");
            var factory = new GeometryFactory(new PrecisionModel(), 2263);
            IFeature[] features = [new Feature(
                factory.CreatePoint(new Coordinate(971908.4588538355, 190390.83886990085)),
                new AttributesTable { { "name", "Statue of Liberty" } })];
            Shapefile.WriteAllFeatures(features, path, prj, Encoding.UTF8);
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    archive.CreateEntryFromFile(file, Path.GetFileName(file));
                }
            }
            return stream.ToArray();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
