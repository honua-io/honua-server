// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

// GeoJSON features with a null geometry are imported without geometry and reported as warnings.
public partial class StreamingImportTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GeoJsonNullGeometryFeature_ImportsEveryFeatureAndWarnsWithItsIndex()
    {
        const string geoJsonContent = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[-157,21]},"properties":{"name":"a"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[-156,21]},"properties":{"name":"b"}},
          {"type":"Feature","geometry":null,"properties":{"name":"unlocated"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[-155,20]},"properties":{"name":"d"}}]}
        """;
        using var content = new MultipartFormDataContent();
        using var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "null-geometry.geojson"
        };
        using var tableName = new StringContent("null_geometry_table");
        using var overwrite = new StringContent("true");
        content.Add(fileContent);
        content.Add(tableName, "TableName");
        content.Add(overwrite, "OverwriteExisting");

        using var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = DeserializeImportResult(responseContent);
        result.Success.Should().BeTrue(responseContent);
        result.FeatureCount.Should().Be(4, responseContent);
        result.ValidationErrors.Should().ContainSingle(issue =>
            issue.Code == ImportValidationErrorCodes.GeometryMissing &&
            issue.Severity == ImportValidationSeverity.Warning &&
            issue.FeatureIndex == 3);
        (await CountNullGeometryRowsAsync("null_geometry_table")).Should().Be((4, 1));
    }

    private async Task<(long Rows, long NullGeometries)> CountNullGeometryRowsAsync(string tableName)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*), count(*) FILTER (WHERE geometry IS NULL) " +
            $"FROM {QuoteIdentifier("honua_data")}.{QuoteIdentifier("imported_" + tableName)}";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
