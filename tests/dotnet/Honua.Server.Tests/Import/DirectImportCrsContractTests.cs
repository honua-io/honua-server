// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed partial class DirectImportCrsContractTests : IAsyncLifetime
{
    private const string ProjectedPoint = "SRID=2193;POINT (1740000 5420000)";
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });
    private readonly List<(string Schema, string Table)> _importedTables = [];

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.Client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
    }

    public async Task DisposeAsync()
    {
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        {
            foreach (var (schema, table) in _importedTables)
            {
                await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {QualifiedTable(schema, table)}", connection);
                await command.ExecuteNonQueryAsync();
            }
        }
        await _fixture.DisposeAsync();
    }

    [IntegrationTheory]
    [InlineData("point.wkt", ProjectedPoint,
        "{\"detectedSrid\":2193,\"sourceCrsDeclared\":true,\"detectionMethod\":\"file-metadata\",\"effectiveSourceSrid\":2193,\"targetSrid\":null,\"storedSrid\":2193,\"reprojected\":false,\"requiresSourceOverride\":false}")]
    [InlineData("point.geojson", "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[174,-41]},\"properties\":{}}]}",
        "{\"detectedSrid\":4326,\"sourceCrsDeclared\":false,\"detectionMethod\":\"format-defined\",\"effectiveSourceSrid\":4326,\"targetSrid\":null,\"storedSrid\":4326,\"reprojected\":false,\"requiresSourceOverride\":false}")]
    [InlineData("point.csv", "geometry,name\n\"POINT (1740000 5420000)\",Unknown\n",
        "{\"detectedSrid\":null,\"sourceCrsDeclared\":false,\"detectionMethod\":\"unknown\",\"effectiveSourceSrid\":null,\"targetSrid\":null,\"storedSrid\":null,\"reprojected\":false,\"requiresSourceOverride\":true}")]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task PreviewFile_SourceMetadata_ReportsCoordinateProvenance(string fileName, string contents, string expectedJson)
    {
        using var preview = await PostFileAsync("preview", new(fileName, contents));
        using var expected = JsonDocument.Parse(expectedJson);

        var coordinateSystem = preview.RootElement.GetProperty("coordinateSystem");
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            coordinateSystem.GetProperty(property.Name).GetRawText().Should().Be(property.Value.GetRawText(), property.Name);
        }
    }

    [IntegrationTheory]
    [InlineData(null, 2193)]
    [InlineData("4326", 4326)]
    [InlineData("2193", 2193)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_TargetChoice_StoresRequestedCoordinateSystem(string? targetSrid, int expectedSrid)
    {
        var fields = NewImportFields();
        if (targetSrid is not null) fields["TargetSrid"] = targetSrid;
        using var result = await PostFileAsync("upload", new("point.wkt", ProjectedPoint, fields));
        var imported = result.RootElement;
        imported.GetProperty("success").GetBoolean().Should().BeTrue(imported.GetRawText());
        var schema = imported.GetProperty("schema").GetString()!;
        var table = imported.GetProperty("physicalTableName").GetString()!;
        _importedTables.Add((schema, table));
        imported.GetProperty("detectedSrid").GetInt32().Should().Be(2193);
        imported.GetProperty("sourceSrid").GetInt32().Should().Be(2193);
        imported.GetProperty("storedSrid").GetInt32().Should().Be(expectedSrid);
        imported.GetProperty("reprojected").GetBoolean().Should().Be(expectedSrid != 2193);
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT ST_SRID(geometry) FROM {QualifiedTable(schema, table)}", connection);
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(expectedSrid);
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("2193")]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_UnknownSourceWithoutOverride_RefusesEvenWithTarget(string? targetSrid)
    {
        var fields = NewImportFields();
        if (targetSrid is not null) fields["TargetSrid"] = targetSrid;
        using var result = await PostFileAsync("upload", new("point.csv", "geometry,name\n\"POINT (1740000 5420000)\",Unknown\n", fields));

        result.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("errorCode").GetString().Should().Be("import.source_srid_required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_SourceOverride_PreservesDetectionAndStoresOverride()
    {
        var fields = NewImportFields();
        fields["SourceSrid"] = "3857";
        using var result = await PostFileAsync("upload", new("point.wkt", ProjectedPoint, fields));
        var imported = result.RootElement;
        imported.GetProperty("success").GetBoolean().Should().BeTrue(imported.GetRawText());
        var schema = imported.GetProperty("schema").GetString()!;
        var table = imported.GetProperty("physicalTableName").GetString()!;
        _importedTables.Add((schema, table));
        imported.GetProperty("detectedSrid").GetInt32().Should().Be(2193);
        imported.GetProperty("sourceSrid").GetInt32().Should().Be(3857);
        imported.GetProperty("storedSrid").GetInt32().Should().Be(3857);
        imported.GetProperty("reprojected").GetBoolean().Should().BeFalse();
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT ST_SRID(geometry) FROM {QualifiedTable(schema, table)}", connection);
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(3857);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_UndeclaredSourceWithOverride_StoresOverride()
    {
        var fields = NewImportFields();
        fields["SourceSrid"] = "2193";
        using var result = await PostFileAsync("upload", new("point.csv", "geometry,name\n\"POINT (1740000 5420000)\",Known\n", fields));
        var imported = result.RootElement;
        imported.GetProperty("success").GetBoolean().Should().BeTrue(imported.GetRawText());
        _importedTables.Add((imported.GetProperty("schema").GetString()!, imported.GetProperty("physicalTableName").GetString()!));
        imported.TryGetProperty("detectedSrid", out var detected).Should().BeTrue();
        detected.ValueKind.Should().Be(JsonValueKind.Null);
        imported.GetProperty("sourceSrid").GetInt32().Should().Be(2193);
        imported.GetProperty("storedSrid").GetInt32().Should().Be(2193);
    }

    [IntegrationTheory]
    [InlineData("TargetSrid", "1.5")]
    [InlineData("TargetSrid", "not-a-code")]
    [InlineData("SourceSrid", "1.5")]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_MalformedSrid_ReturnsBadRequest(string field, string value)
    {
        var fields = NewImportFields();
        fields[field] = value;
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(ProjectedPoint), "File", "point.wkt");
        foreach (var (name, entry) in fields) content.Add(new StringContent(entry), name);
        using var response = await _fixture.Client.PostAsync("/api/v1/admin/import/upload", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [InlineData("2193")]
    [InlineData("EPSG:2193")]
    [InlineData("New Zealand Transverse Mercator")]
    [Endpoint("GET /api/v1/admin/coordinate-systems")]
    public async Task CoordinateSystems_CodeOrName_ReturnsSupportedEpsgEntry(string query)
    {
        using var response = await _fixture.Client.GetAsync($"/api/v1/admin/coordinate-systems?q={Uri.EscapeDataString(query)}&limit=20");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var result = JsonDocument.Parse(text);

        var items = result.RootElement.GetProperty("items").EnumerateArray().ToArray();
        items.Should().HaveCountLessThanOrEqualTo(20);
        var entry = items.Single(item => item.GetProperty("code").GetInt32() == 2193);
        entry.GetProperty("name").GetString().Should().Contain("New Zealand Transverse Mercator");
        entry.TryGetProperty("unit", out _).Should().BeTrue();
        entry.TryGetProperty("area", out _).Should().BeTrue();
        result.RootElement.TryGetProperty("hasMore", out _).Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("")]
    [InlineData("?q=%20")]
    [InlineData("?q=2193&limit=0")]
    [InlineData("?q=2193&limit=51")]
    [InlineData("?q=2193&limit=1.5")]
    [Endpoint("GET /api/v1/admin/coordinate-systems")]
    public async Task CoordinateSystems_InvalidBounds_ReturnsBadRequest(string query)
    {
        using var response = await _fixture.Client.GetAsync($"/api/v1/admin/coordinate-systems{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/coordinate-systems")]
    public async Task CoordinateSystems_Unauthenticated_ReturnsUnauthorized()
    {
        _fixture.Client.DefaultRequestHeaders.Remove("X-API-Key");

        using var response = await _fixture.Client.GetAsync("/api/v1/admin/coordinate-systems?q=2193");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/coordinate-systems")]
    public async Task CoordinateSystems_RegistryFailure_ReturnsSafeUnavailableResponse()
    {
        var registry = Substitute.For<ICrsRegistry>();
        // NSubstitute consumes the ValueTask to configure the call; it is not an asynchronous operation.
#pragma warning disable CA2012
        registry.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<CoordinateSystemSearchResult>(new InvalidOperationException("private-registry-details")));
#pragma warning restore CA2012
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<ICrsRegistry>();
            services.AddSingleton<ICrsRegistry>(registry);
        });
        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/admin/coordinate-systems?q=2193");
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("private-registry-details");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_MixedSourceDeclarations_RejectsBeforeCreatingDestination()
    {
        var fields = NewImportFields();
        using var result = await PostFileAsync("upload", new("mixed.wkt", "SRID=2193;POINT(1740000 5420000)\nSRID=3857;POINT(1 2)", fields));
        result.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("errorCode").GetString().Should().Be("import.source_srid_heterogeneous");
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_class WHERE relname = @logical OR relname = @physical", connection);
        query.Parameters.AddWithValue("logical", fields["TableName"]);
        query.Parameters.AddWithValue("physical", "imported_" + fields["TableName"]);
        Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(0);
    }

    private async Task<JsonDocument> PostFileAsync(string operation, FileUpload upload)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(upload.Contents, Encoding.UTF8), "File", upload.FileName);
        foreach (var (name, value) in upload.Fields ?? []) content.Add(new StringContent(value), name);
        using var response = await _fixture.Client.PostAsync($"/api/v1/admin/import/{operation}", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private static Dictionary<string, string> NewImportFields() => new()
    {
        ["TableName"] = $"crs_contract_{Guid.NewGuid():N}"[..40],
        ["OverwriteExisting"] = "true",
    };

    private static string QualifiedTable(string schema, string table)
    {
        using var builder = new NpgsqlCommandBuilder();
        return $"{builder.QuoteIdentifier(schema)}.{builder.QuoteIdentifier(table)}";
    }

    private sealed record FileUpload(string FileName, string Contents, Dictionary<string, string>? Fields = null);
}
