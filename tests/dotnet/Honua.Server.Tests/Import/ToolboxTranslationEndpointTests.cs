// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
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

        // layerId/rasterId remain unmapped and defaultless, but supplying 'source' is exactly
        // what the source/layerId/rasterId rule asks for and no branch can require the other
        // two, so the mapping is certified rather than downgraded.
        tool.GetProperty("classification").GetString().Should().Be("translated");
        tool.GetProperty("issues").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_GenuinelyOptionalParameterOmitted_IsCertifiedTranslated()
    {
        // geometry.dissolve's 'groupKeys' is optional, defaultless, and required on no branch:
        // the canonical plan validator accepts a wkbs+srid plan that omits it, so the report
        // must not downgrade the tool. Downgrading unconditional omissions would mark most
        // executable mappings 'partially-translated' and tell migrating users to review a
        // mapping the submit path already accepts.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "DissolveToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "DissolveGeometries",
                  "targetProcessId": "geometry.dissolve",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "wkbs" },
                    { "sourceName": "spatial_ref", "targetParameter": "srid" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("translated");
        tool.GetProperty("processId").GetString().Should().Be("geometry.dissolve");
        tool.GetProperty("issues").GetArrayLength().Should().Be(0);
        report.RootElement.GetProperty("summary").GetProperty("translatedCount").GetInt32()
            .Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_UndeclaredDiscriminatorDomain_IsNotCertifiedExecutable()
    {
        // transform.computed-field requires 'fields' when op=concat and 'left'/'right' for the
        // arithmetic ops, but the catalog declares no allowedValues for 'op'. Its legal values
        // cannot be enumerated, so no branch can be ruled out and the mapping stays reported.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "FieldToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "CalculateField",
                  "targetProcessId": "transform.computed-field",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "input" },
                    { "sourceName": "field_name", "targetParameter": "target" },
                    { "sourceName": "operation", "targetParameter": "op" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("partially-translated");
        var issue = tool.GetProperty("issues").EnumerateArray()
            .Single(entry => entry.GetProperty("code").GetString() == "unverifiable-conditional-branches");
        issue.GetProperty("message").GetString().Should().Contain("fields");
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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_StructurallyInvalidManifest_ReturnsProblemDetailsBody()
    {
        // The handler writes 400s through the shared admin problem-details helper, so the
        // body is RFC 7807 application/problem+json, NOT the ErrorResponse envelope the
        // reusable BadRequest component advertises. The admin OpenAPI bundle documents this
        // operation's own 400 for that reason; assert the runtime shape the doc claims so a
        // regression in either direction is caught (#3040 review).
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "  ",
              "sourceFormat": "pyt",
              "tools": [{ "toolName": "Anything" }]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var problem = await ReadJsonAsync(response);
        var root = problem.RootElement;
        root.TryGetProperty("title", out _).Should().BeTrue();
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("detail").GetString().Should().Contain("toolboxName");
        root.TryGetProperty("type", out _).Should().BeTrue();
        root.TryGetProperty("instance", out _).Should().BeTrue();

        // The ErrorResponse envelope the shared BadRequest component describes is NOT what
        // this endpoint returns; generated clients binding to it would fail to parse.
        root.TryGetProperty("success", out _).Should().BeFalse();
        root.TryGetProperty("message", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_UnsatisfiedExactlyOneOfGroup_IsNotCertifiedExecutable()
    {
        // conversion.rasterize requires exactly one of burnValue/attribute, and the canonical
        // validator raises that as INVALID_PARAMETER_VALUE rather than
        // MISSING_REQUIRED_PARAMETER. Mapping neither is rejected at submit time, so the
        // report must not classify it 'translated' (#3040 review).
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ConversionToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "RasterizeWithoutBurnSource",
                  "targetProcessId": "conversion.rasterize",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "source" },
                    { "sourceName": "cell_size", "targetParameter": "cellSize" }
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
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("message").GetString())
            .Should().Contain(message => message!.Contains("exactly one of", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_SatisfiedExactlyOneOfGroup_IsCertifiedExecutable()
    {
        // The counterpart of the case above: mapping one burn source and the grid definition
        // satisfies every rule, so the report must still certify it. Guards the fix against
        // over-reporting every rasterize mapping.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ConversionToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "RasterizeWithBurnValue",
                  "targetProcessId": "conversion.rasterize",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "source" },
                    { "sourceName": "burn", "targetParameter": "burnValue" },
                    { "sourceName": "cell_size", "targetParameter": "cellSize" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("translated");
        tool.GetProperty("issues").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_ConstrainedFreeFormTextInput_IsCertifiedExecutable()
    {
        // raster.map-algebra's 'expression' is format-constrained (an allow-listed band
        // expression), not an undeclared discriminator: no rule branches on its value and
        // dataType/noData are optional on every branch. Treating the format rejection as a
        // token domain downgraded this ordinary mapping to 'partially-translated' (#3040
        // review).
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "RasterToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "MapAlgebra",
                  "targetProcessId": "raster.map-algebra",
                  "parameterMappings": [
                    { "sourceName": "in_rasters", "targetParameter": "sources" },
                    { "sourceName": "expr", "targetParameter": "expression" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("translated");
        tool.GetProperty("issues").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_ProcessWhoseExecutorAlwaysFails_IsNotCertifiedExecutable()
    {
        // raster.interpolate-kriging validates cleanly ('points' is its only required input)
        // and the submit path deliberately admits it, but no kriging backend is bundled so
        // every job fails. A report that certifies it tells a migrating user a tool works
        // when it can never execute (#3040 review).
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "RasterToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "Kriging",
                  "targetProcessId": "raster.interpolate-kriging",
                  "parameterMappings": [
                    { "sourceName": "in_points", "targetParameter": "points" },
                    { "sourceName": "z_field", "targetParameter": "zField" }
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
            .Should().Contain("process-not-job-executable");
        tool.GetProperty("issues").EnumerateArray()
            .Select(issue => issue.GetProperty("message").GetString())
            .Should().Contain(message => message!.Contains("raster.interpolate-idw", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_ExecutableSiblingOfUnsupportedProcess_IsCertifiedExecutable()
    {
        // raster.interpolate-idw is the supported sibling: the unavailability list must be
        // keyed tightly enough that its neighbour is still certified.
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "RasterToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "Idw",
                  "targetProcessId": "raster.interpolate-idw",
                  "parameterMappings": [
                    { "sourceName": "in_points", "targetParameter": "points" },
                    { "sourceName": "z_field", "targetParameter": "zField" },
                    { "sourceName": "search_radius", "targetParameter": "radius" },
                    { "sourceName": "out_width", "targetParameter": "width" },
                    { "sourceName": "out_height", "targetParameter": "height" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("translated");
        tool.GetProperty("issues").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_MappedDiscriminatorBranch_IsNotReportedUnsupported()
    {
        // Mapping analytics.cluster-managed's input/algorithm/k executes when the caller
        // supplies algorithm=kmeans. The probe cannot enumerate 'algorithm', so it pins the
        // catalog default (dbscan) and that branch wants eps/minPoints — a requirement the
        // caller avoids. The report must not declare the tool unsupported on the strength of
        // a branch the probe fabricated; it is branch-unverifiable, not impossible (#3040).
        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """
            {
              "toolboxName": "ClusterToolbox",
              "sourceFormat": "pyt",
              "tools": [
                {
                  "toolName": "ClusterKMeans",
                  "targetProcessId": "analytics.cluster-managed",
                  "parameterMappings": [
                    { "sourceName": "in_features", "targetParameter": "input" },
                    { "sourceName": "algo", "targetParameter": "algorithm" },
                    { "sourceName": "cluster_count", "targetParameter": "k" }
                  ]
                }
              ]
            }
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = await ReadJsonAsync(response);
        var tool = report.RootElement.GetProperty("tools")[0];

        tool.GetProperty("classification").GetString().Should().Be("partially-translated");
        tool.GetProperty("issues").EnumerateArray()
            .Should().Contain(issue =>
                issue.GetProperty("code").GetString() == "unverifiable-conditional-branches");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_EmitsTelemetrySpanWithOutcomeCounts()
    {
        // The validation outcome must be traceable, not log-only: a clean toolbox and a
        // degraded one are otherwise indistinguishable in OpenTelemetry (#3040).
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Honua",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var response = await PostFixtureAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            "partially-translatable-toolbox.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var span = activities.Should()
            .ContainSingle(activity =>
                activity.OperationName == "honua.import.toolbox_translation.validate")
            .Subject;

        span.GetTagItem("honua.operation").Should().Be("toolbox-translation-validate");
        span.GetTagItem("honua.import.toolbox.source_format").Should().NotBeNull();
        span.GetTagItem("honua.import.toolbox.tool_count").Should().NotBeNull();
        span.GetTagItem("honua.import.toolbox.translated_count").Should().NotBeNull();
        span.GetTagItem("honua.import.toolbox.partially_translated_count").Should().NotBeNull();
        span.GetTagItem("honua.import.toolbox.unsupported_count").Should().NotBeNull();
        span.GetTagItem("honua.import.toolbox.rejection_reason").Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/toolbox/translation/validate")]
    public async Task ValidateTranslation_RejectedManifest_EmitsErrorSpanWithReasonCode()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Honua",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var response = await PostJsonAsync(
            "/api/v1/admin/import/toolbox/translation/validate",
            """{ "toolboxName": "", "sourceFormat": "pyt", "tools": [] }""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var span = activities.Should()
            .ContainSingle(activity =>
                activity.OperationName == "honua.import.toolbox_translation.validate")
            .Subject;

        span.Status.Should().Be(ActivityStatusCode.Error);
        span.GetTagItem("honua.import.toolbox.rejection_reason").Should().Be("invalid-structure");
        // The caller-facing message interpolates manifest content, so it must not leak into
        // an unbounded span attribute.
        span.GetTagItem("honua.import.toolbox.tool_count").Should().BeNull();
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
