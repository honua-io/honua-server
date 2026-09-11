// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for FileGDB advanced construct detection during preview.
///
/// The expected warning sets below are not guesses: each fixture's raw GDB_Items table
/// (a00000004.gdbtable) was independently grepped for the Esri type-name keywords that
/// <see cref="FileGdbAdvancedConstructs"/> scans for, and cross-checked with GDAL's
/// OpenFileGDB driver (ogrinfo). testopenfilegdb.gdb and sparse.gdb contain none of those
/// keywords, so they must report zero warnings. domain-coded.gdb, domain-range.gdb and
/// relationship-class.gdb were generated with GDAL's OpenFileGDB *write* driver specifically
/// to contain one real advanced construct each, confirmed present via the same raw-table grep
/// before being committed as fixtures.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class FileGdbAdvancedConstructTests : IAsyncLifetime
{
    private const string DomainWarning = "Geodatabase contains coded value or range domains. Domain constraints will not be imported.";
    private const string RelationshipWarning = "Geodatabase contains relationship classes. Relationships will not be imported.";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_WithFileGdb_ReportsNoAdvancedConstructWarnings()
    {
        var warnings = await PreviewWarningsAsync("testopenfilegdb.gdb.zip");

        // This fixture has multiple feature classes, so FileGdbReader legitimately adds its own
        // "single feature class required" warning to the same array - that warning is unrelated
        // to FileGdbAdvancedConstructs and out of scope here. What is independently verified is
        // that this fixture's GDB_Items table contains none of the domain/relationship/subtype/
        // topology/network type-name keywords, so neither construct warning may appear.
        warnings.Should().NotContain(DomainWarning);
        warnings.Should().NotContain(RelationshipWarning);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_WithSparseFileGdb_ReportsNoAdvancedConstructWarnings()
    {
        var warnings = await PreviewWarningsAsync("sparse.gdb.zip");

        warnings.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_WithCodedValueDomain_DetectsDomainWarning()
    {
        var warnings = await PreviewWarningsAsync("domain-coded.gdb.zip");

        warnings.Should().BeEquivalentTo([DomainWarning]);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_WithRangeDomain_DetectsDomainWarning()
    {
        var warnings = await PreviewWarningsAsync("domain-range.gdb.zip");

        warnings.Should().BeEquivalentTo([DomainWarning]);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_WithRelationshipClass_DetectsRelationshipWarning()
    {
        var warnings = await PreviewWarningsAsync("relationship-class.gdb.zip");

        warnings.Should().BeEquivalentTo([RelationshipWarning]);
    }

    private async Task<string[]> PreviewWarningsAsync(string fixtureFileName)
    {
        // Path.Combine args are relative test fixture fragments; no rooted-segment risk.
        var filePath = Path.Join(AppContext.BaseDirectory, "TestData", "FileGdb", fixtureFileName);
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = fixtureFileName
        };
        content.Add(fileContent);

        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();
        var root = payload!.RootElement;
        root.GetProperty("format").GetString().Should().Be("FileGdb");

        return root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
    }
}
