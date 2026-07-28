// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the arcpy/toolbox translation lane validation endpoint (#2145).
/// The fixtures under <c>tests/fixtures/toolbox-translation/</c> are the shared contract
/// with the honua-sdk-python <c>honua-migrate</c> translator: a fully translatable toolbox,
/// a partially translatable toolbox, and a fully unsupported toolbox, each producing the
/// documented report.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ToolboxTranslationEndpointTests : IAsyncLifetime
{
    private const string Route = "/api/v1/admin/import/toolbox/translation/validate";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_TranslatableToolbox_RoundTripsAllParameterSignatures()
    {
        var response = await PostFixtureAsync("translatable-toolbox.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var root = report.RootElement;

        root.GetProperty("artifactKind").GetString()
            .Should().Be("honua.migration.toolbox-translation-report");
        root.GetProperty("toolboxName").GetString().Should().Be("VectorAnalysisToolbox");
        root.GetProperty("sourceFormat").GetString().Should().Be("pyt");

        var summary = root.GetProperty("summary");
        summary.GetProperty("toolCount").GetInt32().Should().Be(2);
        summary.GetProperty("translatedCount").GetInt32().Should().Be(2);
        summary.GetProperty("partiallyTranslatedCount").GetInt32().Should().Be(0);
        summary.GetProperty("unsupportedCount").GetInt32().Should().Be(0);

        var buffer = root.GetProperty("tools")[0];
        buffer.GetProperty("toolName").GetString().Should().Be("BufferGeometry");
        buffer.GetProperty("classification").GetString().Should().Be("translated");
        buffer.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        buffer.GetProperty("issues").GetArrayLength().Should().Be(0);

        var bindings = buffer.GetProperty("parameterBindings");
        bindings.GetArrayLength().Should().Be(3);
        bindings[0].GetProperty("sourceName").GetString().Should().Be("in_geometry");
        bindings[0].GetProperty("targetParameter").GetString().Should().Be("wkb");
        bindings[0].GetProperty("valueType").GetString().Should().Be("Wkb");
        bindings[0].GetProperty("required").GetBoolean().Should().BeTrue();
        bindings[2].GetProperty("targetParameter").GetString().Should().Be("distance");
        bindings[2].GetProperty("valueType").GetString().Should().Be("FloatingPoint");

        var clip = root.GetProperty("tools")[1];
        clip.GetProperty("classification").GetString().Should().Be("translated");
        clip.GetProperty("processId").GetString().Should().Be("overlay.clip");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_PartiallyTranslatableToolbox_ReportsUnsupportedConstructs()
    {
        var response = await PostFixtureAsync("partially-translatable-toolbox.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var root = report.RootElement;

        var summary = root.GetProperty("summary");
        summary.GetProperty("toolCount").GetInt32().Should().Be(1);
        summary.GetProperty("partiallyTranslatedCount").GetInt32().Should().Be(1);

        var tool = root.GetProperty("tools")[0];
        tool.GetProperty("classification").GetString().Should().Be("partially-translated");
        tool.GetProperty("processId").GetString().Should().Be("overlay.intersect");

        // Both required canonical parameters still round-trip.
        var bindings = tool.GetProperty("parameterBindings");
        bindings.GetArrayLength().Should().Be(2);
        bindings[0].GetProperty("targetParameter").GetString().Should().Be("input");
        bindings[1].GetProperty("targetParameter").GetString().Should().Be("overlay");

        var issueCodes = tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .ToArray();
        issueCodes.Should().Contain("unsupported-construct");
        issueCodes.Should().Contain("unknown-target-parameter");

        var unknownParameter = tool.GetProperty("issues").EnumerateArray()
            .Single(issue => issue.GetProperty("code").GetString() == "unknown-target-parameter");
        unknownParameter.GetProperty("parameterName").GetString().Should().Be("report_units");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_UnsupportedToolbox_MarksEveryToolUnsupportedWithExplicitReasons()
    {
        var response = await PostFixtureAsync("unsupported-toolbox.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var root = report.RootElement;

        var summary = root.GetProperty("summary");
        summary.GetProperty("toolCount").GetInt32().Should().Be(3);
        summary.GetProperty("unsupportedCount").GetInt32().Should().Be(3);
        summary.GetProperty("translatedCount").GetInt32().Should().Be(0);

        var tools = root.GetProperty("tools");

        var script = tools[0];
        script.GetProperty("classification").GetString().Should().Be("unsupported");
        // The context serializes with WhenWritingNull, so a null processId is omitted.
        if (script.TryGetProperty("processId", out var scriptProcessId))
        {
            scriptProcessId.ValueKind.Should().Be(JsonValueKind.Null);
        }
        script.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .Should().Contain(["no-native-executor", "unsupported-construct"]);

        var unknownProcess = tools[1];
        unknownProcess.GetProperty("classification").GetString().Should().Be("unsupported");
        unknownProcess.GetProperty("issues")[0].GetProperty("code").GetString()
            .Should().Be("unknown-process");

        var missingRequired = tools[2];
        missingRequired.GetProperty("classification").GetString().Should().Be("unsupported");
        missingRequired.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        var issue = missingRequired.GetProperty("issues").EnumerateArray()
            .Single(entry => entry.GetProperty("code").GetString() == "missing-required-parameter");
        issue.GetProperty("parameterName").GetString().Should().Be("distance");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_UnknownSourceFormat_ReturnsBadRequest()
    {
        var response = await PostJsonAsync(
            """
            {
              "toolboxName": "BadFormat",
              "sourceFormat": "mxd",
              "tools": [{ "toolName": "Anything" }]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("sourceFormat");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_NullTools_ReturnsBadRequest()
    {
        var response = await PostJsonAsync(
            """
            {
              "toolboxName": "NullTools",
              "sourceFormat": "pyt",
              "tools": null
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("tools");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_NullToolCollections_NormalizesToEmptyAndReports()
    {
        // Explicit JSON nulls for the per-tool collections are normalized to "none
        // declared": the tool still validates against the catalog (and is unsupported
        // here because overlay.clip's required parameters are unmapped) instead of
        // producing a 500.
        var response = await PostJsonAsync(
            """
            {
              "toolboxName": "NullCollections",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "ClipWithNullCollections",
                  "targetProcessId": "overlay.clip",
                  "parameterMappings": null,
                  "unsupportedConstructs": null
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var root = report.RootElement;

        root.GetProperty("summary").GetProperty("toolCount").GetInt32().Should().Be(1);
        root.GetProperty("summary").GetProperty("unsupportedCount").GetInt32().Should().Be(1);

        var tool = root.GetProperty("tools")[0];
        tool.GetProperty("classification").GetString().Should().Be("unsupported");
        tool.GetProperty("processId").GetString().Should().Be("overlay.clip");
        tool.GetProperty("parameterBindings").GetArrayLength().Should().Be(0);
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .Should().OnlyContain(code => code == "missing-required-parameter");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_MalformedBody_ReturnsBadRequest()
    {
        var response = await PostJsonAsync("{ not json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> PostFixtureAsync(string fixtureName)
        => await PostJsonAsync(await File.ReadAllTextAsync(
            ResolveRepoFile("tests", "fixtures", "toolbox-translation", fixtureName)));

    private async Task<HttpResponseMessage> PostJsonAsync(string json)
        => await _client.PostAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            // Path.Join args are directory.FullName (the walk-up root) followed only by
            // literal relative fixture-path segments from call sites; no rooted-segment risk.
            var candidate = Path.Join(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{string.Join("/", segments)}'.");
    }
}
