// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

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
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // A small edit payload cap keeps the oversized-body case cheap to exercise.
        _fixture.ReplaceService<IOptions<LimitsOptions>>(Options.Create(new LimitsOptions
        {
            Edits = new EditLimits { MaxPayloadSize = 1048576 }
        }));

        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_TranslatableToolbox_RoundTripsAllParameterSignatures()
    {
        var response = await PostFixtureAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            "translatable-toolbox.json");

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
        var response = await PostFixtureAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            "partially-translatable-toolbox.json");

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
        var response = await PostFixtureAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            "unsupported-toolbox.json");

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
            "/api/v1/admin/import/toolbox/translation/validate",
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
            "/api/v1/admin/import/toolbox/translation/validate",
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
            "/api/v1/admin/import/toolbox/translation/validate",
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
    public async Task ValidateTranslation_ForeignArtifactKind_ReturnsBadRequest()
    {
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "artifactKind": "honua.migration.manifest",
              "artifactVersion": "1.0",
              "toolboxName": "WrongKind",
              "sourceFormat": "pyt",
              "tools": [{ "toolName": "Anything" }]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("artifactKind");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_UnsupportedArtifactVersion_ReturnsBadRequest()
    {
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "artifactVersion": "2.0",
              "toolboxName": "FutureSchema",
              "sourceFormat": "pyt",
              "tools": [{ "toolName": "Anything" }]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("artifactVersion");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_EmptyMappingOnOptionalParameterProcess_IsNotClaimedTranslated()
    {
        // surface.aspect declares source/layerId/rasterId as individually optional, but the
        // canonical plan validator requires one of them. Neither an empty mapping nor one
        // that maps only an unrelated optional parameter may be reported as executable.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ConditionalInputs",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "AspectNoInputs",
                  "targetProcessId": "surface.aspect",
                  "parameterMappings": []
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().NotBe("translated");
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .Should().Contain("unsatisfied-conditional-inputs");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_UnrelatedOptionalMappingOnly_IsNotClaimedTranslated()
    {
        // Mapping only 'azimuth' to surface.hillshade satisfies every statically-Required
        // parameter, but ValidateSharedRasterSourceSemantics still rejects the plan for
        // supplying none of source/layerId/rasterId.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ConditionalInputs",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "HillshadeAzimuthOnly",
                  "targetProcessId": "surface.hillshade",
                  "parameterMappings": [
                    { "sourceName": "azimuth", "targetParameter": "azimuth" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("unsupported");
        tool.GetProperty("parameterBindings").GetArrayLength().Should().Be(1);
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .Should().Contain("unsatisfied-conditional-inputs");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_SatisfiedConditionalInput_HasNoUnsatisfiedInputIssue()
    {
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ConditionalInputs",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "HillshadeWithSource",
                  "targetProcessId": "surface.hillshade",
                  "parameterMappings": [
                    { "sourceName": "in_raster", "targetParameter": "source" },
                    { "sourceName": "azimuth", "targetParameter": "azimuth" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        // layerId/rasterId remain unmapped and defaultless, so the mapping is reported for
        // review rather than certified; the conditional-input requirement itself is satisfied.
        tool.GetProperty("classification").GetString().Should().Be("partially-translated");
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .Should().OnlyContain(code => code == "unverifiable-conditional-branches");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_ExplicitNullArtifactKind_ReturnsBadRequest()
    {
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "artifactKind": null,
              "toolboxName": "NullKind",
              "sourceFormat": "pyt",
              "tools": [{ "toolName": "Anything" }]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("artifactKind");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_BlankArtifactVersion_ReturnsBadRequest()
    {
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "artifactVersion": "   ",
              "toolboxName": "BlankVersion",
              "sourceFormat": "pyt",
              "tools": [{ "toolName": "Anything" }]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("artifactVersion");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_SyncOnlyProcess_IsUnsupportedRegardlessOfMapping()
    {
        // analytics.cluster runs only through the synchronous layer-scoped analytics surface;
        // the job runtime rejects it with SYNC_ONLY_PROCESS. A complete parameter mapping
        // must therefore still be unsupported, pointing at the -managed counterpart.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ClusterToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "ClusterSyncOnly",
                  "targetProcessId": "analytics.cluster",
                  "parameterMappings": [
                    { "sourceName": "in_layer", "targetParameter": "layerId" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("unsupported");
        var issue = tool.GetProperty("issues").EnumerateArray()
            .Single(entry => entry.GetProperty("code").GetString() == "process-not-job-executable");
        issue.GetProperty("message").GetString().Should().Contain("job-dispatchable");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_MappedFlagSatisfiesRequirement_IsNotReportedUnsupported()
    {
        // transform.dedup needs 'keys' OR 'geometry=true'. 'geometry' is a mapped Flag, so
        // the caller can supply true; probing only its "false" default would wrongly report a
        // missing input. It stays uncertified (keys is undetermined) but must not be
        // classified unsupported.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "DedupToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "DedupByGeometry",
                  "targetProcessId": "transform.dedup",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "input" },
                    { "sourceName": "use_geometry", "targetParameter": "geometry" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().NotBe("unsupported");
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .Should().NotContain("unsatisfied-conditional-inputs");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_UnmappedFlagCannotSatisfyRequirement_IsUnsupported()
    {
        // Without 'geometry' mapped, its "false" default is the only possible value, so no
        // admissible assignment satisfies the keys-or-geometry requirement.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "DedupToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "DedupNoKeys",
                  "targetProcessId": "transform.dedup",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "input" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("unsupported");
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("code").GetString())
            .Should().Contain("unsatisfied-conditional-inputs");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_MutuallyExclusiveInputsMapped_IsNotCertifiedExecutable()
    {
        // sink.external-postgis accepts a registered connectionName XOR connectionId.
        // Mapping both is rejected by ProcessPlanValidator, so it must not be certified even
        // though every defaultless parameter is mapped.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "SinkToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "ExportBothConnections",
                  "targetProcessId": "sink.external-postgis",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "input" },
                    { "sourceName": "conn_name", "targetParameter": "connectionName" },
                    { "sourceName": "conn_id", "targetParameter": "connectionId" },
                    { "sourceName": "out_table", "targetParameter": "table" },
                    { "sourceName": "out_srid", "targetParameter": "targetSrid" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().NotBe("translated");
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("message").GetString())
            .Should().Contain(message => message!.Contains("exactly one of", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_SingleConnectionMapped_ReportsNoValueArtifact()
    {
        // Mapping only connectionId must not surface the probe's own substituted value as a
        // violation (the validator would otherwise complain the placeholder is not a GUID).
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "SinkToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "ExportByConnectionId",
                  "targetProcessId": "sink.external-postgis",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "input" },
                    { "sourceName": "conn_id", "targetParameter": "connectionId" },
                    { "sourceName": "out_table", "targetParameter": "table" },
                    { "sourceName": "out_srid", "targetParameter": "targetSrid" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("message").GetString())
            .Should().NotContain(message => message!.Contains("GUID", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_BranchDependentRequirement_IsNotCertifiedExecutable()
    {
        // analytics.cluster-managed requires 'k' only when algorithm=kmeans. The probe can
        // only exercise the branch its substituted values select (the catalog default,
        // dbscan), and the catalog does not enumerate 'algorithm'. Because 'algorithm' is
        // caller-supplied and 'k' is neither mapped nor defaulted, the mapping must not be
        // certified translated.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ClusterToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "ClusterDbscanOnly",
                  "targetProcessId": "analytics.cluster-managed",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "input" },
                    { "sourceName": "algo", "targetParameter": "algorithm" },
                    { "sourceName": "eps", "targetParameter": "eps" },
                    { "sourceName": "min_points", "targetParameter": "minPoints" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().NotBe("translated");
        var issue = tool.GetProperty("issues").EnumerateArray()
            .Single(entry => entry.GetProperty("code").GetString() == "unverifiable-conditional-branches");
        issue.GetProperty("message").GetString().Should().Contain("k");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_OversizedStreamedBody_Returns413NotBadRequest()
    {
        // Streamed without Content-Length, so the size cannot be pre-checked: the limit
        // trips mid-read and surfaces as BadHttpRequestException(413). The handler must let
        // that reach the shared ExceptionMapper instead of flattening it to 400.
        using var oversized = new StreamContent(
            new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024))));
        oversized.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        oversized.Headers.ContentLength = null;

        var response = await _client.PostAsync(
            "/api/v1/admin/import/toolbox/translation/validate", oversized);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_MalformedBody_ReturnsBadRequest()
    {
        var response = await PostJsonAsync("/api/v1/admin/import/toolbox/translation/validate", "{ not json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> PostFixtureAsync(string route, string fixtureName)
        => await PostJsonAsync(route, await File.ReadAllTextAsync(
            ResolveRepoFile("tests", "fixtures", "toolbox-translation", fixtureName)));

    private async Task<HttpResponseMessage> PostJsonAsync(string route, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.PostAsync(route, content);
    }

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
