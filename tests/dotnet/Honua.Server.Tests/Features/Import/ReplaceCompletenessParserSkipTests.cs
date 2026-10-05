// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Verifies completeness accounting for parser skips and preservation of existing import targets.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ReplaceCompletenessParserSkipTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly string _tableName = "parser_skip_" + Guid.NewGuid().ToString("N")[..12];

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public async Task DisposeAsync()
    {
        try
        {
            await using var connection = await _fixture.Postgres.GetConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS honua_data.\"imported_{_tableName}\", honua_data.\"imported_{_tableName}__staging\"";
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await _fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [InlineData("POINT (1 2)\nPOINT (invalid)\n", 1)]
    [InlineData("POINT (1 2)\nPOINT (3\n", 1)]
    [InlineData("POINT (1 2)\nPOINT (invalid)\nPOINT (invalid)\n", 2)]
    [InlineData("POINT (invalid)\n", 1)]
    public async Task Upload_ReplaceWithParserSkips_PreservesTargetAndReportsCount(string source, int skipped)
    {
        var seed = await UploadAsync("POINT (10 20)\nPOINT (30 40)\n");
        seed.Success.Should().BeTrue(seed.ErrorMessage);

        var result = await UploadAsync(source);
        var rows = await ReadGeometriesAsync();
        var stagingExists = await StagingExistsAsync();

        using (new AssertionScope())
        {
            result.Success.Should().BeFalse();
            result.FeatureCount.Should().Be(0, "no replacement rows were applied");
            result.ErrorMessage.Should().Contain($"{skipped} feature(s) could not be imported")
                .And.Contain("prior target is unchanged");
            result.Warnings.Should().Contain($"{skipped} record(s) could not be parsed as WKT, EWKT, or WKB and were skipped.");
            rows.Should().Equal("POINT(10 20)", "POINT(30 40)");
            stagingExists.Should().BeFalse();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_CompleteReplace_PromotesAllSourceRows()
    {
        var seed = await UploadAsync("POINT (10 20)\nPOINT (30 40)\n");
        seed.Success.Should().BeTrue(seed.ErrorMessage);

        var result = await UploadAsync("POINT (1 2)\nPOINT (3 4)\n");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FeatureCount.Should().Be(2);
        (await ReadGeometriesAsync()).Should().Equal("POINT(1 2)", "POINT(3 4)");
        (await StagingExistsAsync()).Should().BeFalse();
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [InlineData("Replace", false)]
    [InlineData("Append", true)]
    public async Task Upload_PartialNonDestructiveImport_RetainsRowsAndReportsSkips(string loadMode, bool seedTarget)
    {
        if (seedTarget)
        {
            var seed = await UploadAsync("POINT (10 20)\n");
            seed.Success.Should().BeTrue(seed.ErrorMessage);
        }

        var result = await UploadAsync("POINT (1 2)\nPOINT (invalid)\n", loadMode);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FeatureCount.Should().Be(1);
        result.Warnings.Should().Contain("1 record(s) could not be parsed as WKT, EWKT, or WKB and were skipped.");
        var expected = seedTarget ? new[] { "POINT(10 20)", "POINT(1 2)" } : ["POINT(1 2)"];
        (await ReadGeometriesAsync()).Should().Equal(expected);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_CsvWithUnparseableGeometry_RetainsBothRowsOnReplace()
    {
        var seed = await UploadAsync("POINT (10 20)\nPOINT (30 40)\n");
        seed.Success.Should().BeTrue(seed.ErrorMessage);

        var result = await UploadAsync(
            "name,wkt\nvalid,POINT (1 2)\nretained,POINT (invalid)\n", fileName: "records.csv");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FeatureCount.Should().Be(2, "CSV retains the row and its raw geometry value");
        result.Warnings.Should().Contain(warning => warning.Contains("1 row(s) had a geometry value", StringComparison.Ordinal));
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)::int FROM honua_data."imported_{_tableName}"
            WHERE geometry IS NULL AND properties->>'name' = 'retained'
                AND properties->>'wkt' = 'POINT (invalid)'
            """;
        ((int)(await command.ExecuteScalarAsync())!).Should().Be(1);
    }

    private async Task<ImportResult> UploadAsync(string source, string loadMode = "Replace", string fileName = "records.wkt")
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(source, Encoding.UTF8, "text/plain"), "File", fileName);
        content.Add(new StringContent(_tableName), "TableName");
        content.Add(new StringContent("4326"), "SourceSrid");
        content.Add(new StringContent(loadMode == "Replace" ? "true" : "false"), "OverwriteExisting");
        using var response = await _fixture.Client.PostAsync("/api/v1/admin/import/upload", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        result.Should().NotBeNull();
        return result!;
    }

    private async Task<List<string>> ReadGeometriesAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ST_AsText(geometry) FROM honua_data.\"imported_{_tableName}\" ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<bool> StagingExistsAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = @schema AND tablename = @table)";
        command.Parameters.AddWithValue("schema", "honua_data");
        command.Parameters.AddWithValue("table", $"imported_{_tableName}__staging");
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
