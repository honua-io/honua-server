// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Uploads the GDAL-authored fixtures in <c>tests/fixtures/external-format-corpus/v1</c> through
/// the real <c>POST /api/v1/admin/import/upload</c> endpoint and reads the created table back from
/// PostGIS, asserting the feature count, the exact ordinates, the stored SRID and an attribute
/// value for each format (honua-server#4419).
/// </summary>
/// <remarks>
/// <para>
/// The existing per-format HTTP import tests assert only that the JSON response echoes the table
/// name and the detected format; the GeoJSON case at <c>StreamingImportTests</c> is the sole one
/// that queries the created table. An importer that dropped every row after the first, transposed
/// longitude and latitude, or stored the wrong SRID passed all of the others.
/// </para>
/// <para>
/// Every fixture here was written by GDAL/OGR rather than by the library under test, so a shared
/// misinterpretation between Honua's writer and Honua's reader cannot hide inside a round trip.
/// </para>
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ExternalFormatCorpusImportTests : IAsyncLifetime
{
    /// <summary>The three source points, in source order, from <c>survey-sites.source.geojson</c>.</summary>
    private static readonly (double X, double Y, string Name)[] ExpectedSites =
    [
        (-122.4194, 37.7749, "San Francisco"),
        (-157.8583, 21.3069, "Hawaiʻi 東京"),
        (-68.3029, -54.8019, "Ushuaia")
    ];

    private static readonly CuratedCorpus Corpus = CuratedCorpus.LoadExternalFormats();

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredKml_StoresEveryPlacemarkWithCorrectOrdinates()
    {
        await ImportAndAssertSitesAsync(
            "survey-sites-kml", "survey-sites.kml", "application/vnd.google-earth.kml+xml",
            "ext_kml_sites", "Kml");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredKmz_StoresEveryPlacemarkWithCorrectOrdinates()
    {
        // KMZ had no unit test at all and its HTTP test asserted only that the response mentioned
        // the table. Wrap the same GDAL-authored KML so the archive path is exercised on content
        // whose expected values are already pinned.
        var kmz = BuildKmzArchive(Corpus.ReadAllBytes("survey-sites-kml"));
        await ImportAndAssertSitesAsync(
            kmz, "survey-sites.kmz", "application/vnd.google-earth.kmz", "ext_kmz_sites", "Kmz");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredFlatGeobuf_StoresEveryFeatureWithCorrectOrdinates()
    {
        await ImportAndAssertSitesAsync(
            "survey-sites-fgb", "survey-sites.fgb", "application/octet-stream",
            "ext_fgb_sites", "FlatGeobuf");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredGeoPackage_StoresEveryFeatureWithCorrectOrdinates()
    {
        // honua-server#4419: StreamingImportTests has 30 methods and none posts a .gpkg, so the
        // GeoPackage upload path had no HTTP coverage whatsoever.
        await ImportAndAssertSitesAsync(
            "survey-sites-gpkg", "survey-sites.gpkg", "application/geopackage+sqlite3",
            "ext_gpkg_sites", "GeoPackage");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredShapefile_StoresEveryFeatureWithCorrectOrdinates()
    {
        // Shapefile imports were never read back: no test in the repository queried a table an
        // imported shapefile created.
        await ImportAndAssertSitesAsync(
            "survey-sites-shapefile", "survey-sites.zip", "application/zip",
            "ext_shp_sites", "Shapefile");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredCsv_StoresEveryRowWithCorrectOrdinates()
    {
        await ImportAndAssertSitesAsync(
            "survey-sites-csv", "survey-sites.csv", "text/csv", "ext_csv_sites", "Csv");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredGpx_StoresEveryWaypointWithCorrectOrdinates()
    {
        await ImportAndAssertSitesAsync(
            "survey-sites-gpx", "survey-sites.gpx", "application/gpx+xml", "ext_gpx_sites", "Gpx");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GdalAuthoredPolygonWithHole_PreservesTheHole()
    {
        var response = await UploadAsync(
            Corpus.ReadAllBytes("polygon-with-hole-kml"),
            "polygon-with-hole.kml",
            "application/vnd.google-earth.kml+xml",
            "ext_kml_zones");
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        ReadSuccess(body).Should().BeTrue(body);

        var rows = await ReadRowsAsync("ext_kml_zones");
        rows.Should().ContainSingle();
        // Independently computed from polygon-with-hole.source.geojson: a 0.2 x 0.2 outer square
        // minus a 0.1 x 0.1 hole. A dropped inner ring gives 0.04 and fails here.
        rows[0].Area.Should().BeApproximately(0.03, 1e-9, "the inner boundary must survive as a hole");
        rows[0].NumInteriorRings.Should().Be(1);
    }

    private Task ImportAndAssertSitesAsync(
        string assetId, string fileName, string contentType, string tableName, string expectedFormat)
        => ImportAndAssertSitesAsync(Corpus.ReadAllBytes(assetId), fileName, contentType, tableName, expectedFormat);

    private async Task ImportAndAssertSitesAsync(
        byte[] payload, string fileName, string contentType, string tableName, string expectedFormat)
    {
        var response = await UploadAsync(payload, fileName, contentType, tableName);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"expected 2xx for {fileName}: {body}");
        ReadSuccess(body).Should().BeTrue(
            $"a response that merely echoes the table name is not proof the import succeeded: {body}");
        body.Should().Contain(expectedFormat, $"format detection must report {expectedFormat}: {body}");

        var rows = await ReadRowsAsync(tableName);
        rows.Should().HaveCount(
            ExpectedSites.Length,
            $"an importer that dropped rows would still have produced a success envelope: {body}");

        foreach (var expected in ExpectedSites)
        {
            var row = rows.Should().ContainSingle(
                candidate => Math.Abs(candidate.X - expected.X) < 1e-9,
                $"every source longitude must be stored exactly once: {body}").Subject;
            row.Y.Should().BeApproximately(
                expected.Y,
                1e-9,
                "a transposed or mis-signed latitude is the characteristic silent-wrong-data failure here");
            row.Srid.Should().Be(4326, "the import target SRID must be recorded on the stored geometry");
            row.Name.Should().Be(expected.Name, "attribute values must survive, including non-ASCII text");
        }
    }

    private async Task<HttpResponseMessage> UploadAsync(
        byte[] payload, string fileName, string contentType, string tableName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = fileName
        };
        content.Add(file);
        content.Add(new StringContent(tableName), "TableName");
        content.Add(new StringContent("4326"), "SourceSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");
        return await _fixture.Client.PostAsync("/api/v1/admin/import/upload", content);
    }

    private static bool ReadSuccess(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("success", out var success) && success.GetBoolean();
    }

    private async Task<ImportedRow[]> ReadRowsAsync(string tableName)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ST_X(ST_PointOnSurface(geometry)), ST_Y(ST_PointOnSurface(geometry)), ST_SRID(geometry),
                   ST_Area(geometry), ST_NumInteriorRings(CASE WHEN GeometryType(geometry) = 'POLYGON'
                                                               THEN geometry END),
                   attributes->>'site_name', attributes->>'zone_name'
            FROM {Quote("honua_data")}.{Quote("imported_" + tableName)}
            """;
        var rows = new List<ImportedRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ImportedRow(
                reader.GetDouble(0),
                reader.GetDouble(1),
                reader.GetInt32(2),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? (reader.IsDBNull(6) ? null : reader.GetString(6)) : reader.GetString(5)));
        }

        return [.. rows];
    }

    private static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static byte[] BuildKmzArchive(byte[] kml)
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("doc.kml");
            using var entryStream = entry.Open();
            entryStream.Write(kml);
        }

        return buffer.ToArray();
    }

    private sealed record ImportedRow(
        double X, double Y, int Srid, double Area, int NumInteriorRings, string? Name);
}
