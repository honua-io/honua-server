// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class McpGetJobResultsToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_SucceededJob_ReturnsResultPackage()
    {
        ClaimsPrincipal? captured = null;
        _jobService
            .GetJobResultsAsync("gp-done", Arg.Do<ClaimsPrincipal>(principal => captured = principal), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage());

        var response = await Dispatch(ToolCall("results-1", """{"jobId":"gp-done"}"""));

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("jobId").GetString().Should().Be("gp-done");
        structured.GetProperty("resultPackageId").GetString().Should().Be("pkg-123");
        structured.GetProperty("status").GetString().Should().Be(nameof(GeoprocessingWorkflowStatus.Completed));
        structured.GetProperty("summary").GetProperty("title").GetString().Should().Be("Buffered parcels");
        structured.GetProperty("artifacts")[0].GetProperty("artifactId").GetString().Should().Be("artifact-1");
        structured.GetProperty("artifacts")[0].GetProperty("referenceSha256").GetString().Should().Be(
            "77f060ef7da5b14247281f6dcef6e97f7438771d975254291c659cc84c96f5ec");
        structured.GetProperty("artifactsTruncated").GetBoolean().Should().BeFalse();
        var jsonText = result.GetProperty("content").EnumerateArray()
            .Last(block => block.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
        using var textDocument = JsonDocument.Parse(jsonText);
        JsonElement.DeepEquals(structured, textDocument.RootElement).Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Identity!.Name.Should().Be("test-user");
        ServiceCalls().Should().Equal(nameof(IGeoprocessingJobService.GetJobResultsAsync));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_MissingJob_UsesServiceNotFoundRefusal()
    {
        _jobService
            .GetJobResultsAsync("missing-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnalysisResultPackage>>(_ => throw new GeoprocessingNotFoundException("Job 'missing-job' not found."));

        var structured = await ErrorContent(ToolCall("results-missing", """{"jobId":"missing-job"}"""));

        structured.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.NotFound);
        structured.GetProperty("message").GetString().Should().Be("Job 'missing-job' not found.");
        structured.GetProperty("error").GetProperty("kind").GetString().Should().Be("UnknownDataset");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_OtherOwnersJob_UsesServiceNotFoundRefusal()
    {
        // The service collapses a foreign owner into not_found so probing cannot
        // confirm the identifier. The tool must not remap that to permission_denied.
        _jobService
            .GetJobResultsAsync("other-job", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnalysisResultPackage>>(_ => throw new GeoprocessingNotFoundException("Job 'other-job' not found."));

        var structured = await ErrorContent(ToolCall("results-other", """{"jobId":"other-job"}"""));

        structured.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.NotFound);
        structured.GetProperty("code").GetString().Should().NotBe(McpErrorMapper.Codes.PermissionDenied);
        structured.GetProperty("message").GetString().Should().Be("Job 'other-job' not found.");
        ServiceCalls().Should().Equal(nameof(IGeoprocessingJobService.GetJobResultsAsync));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_JobSubstrateUnavailable_UsesSubmissionRefusalEnvelope()
    {
        _jobService
            .GetJobResultsAsync("gp-done", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnalysisResultPackage>>(_ => throw new GeoprocessingStoreUnavailableException());

        var structured = await ErrorContent(ToolCall("results-unavailable", """{"jobId":"gp-done"}"""));
        var expected = McpToolHelpers.ErrorResult(new GeoprocessingStoreUnavailableException()).StructuredContent!.Value;

        structured.GetRawText().Should().Be(expected.GetRawText());
        structured.GetProperty("code").GetString().Should().Be(McpErrorMapper.Codes.Unavailable);
        structured.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.SystemDatabaseDependency);
        structured.GetProperty("retryable").GetBoolean().Should().BeFalse();
        structured.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_InlineGeoJsonJoin_ReturnsCountsAndRowsInsteadOfTheDataUri()
    {
        var uri = JoinResultUri(joinCounts: [1, 0, 2, 0], padding: 1200);
        _jobService.GetJobResultsAsync("gp-join", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(uri));

        var result = (await Dispatch(ToolCall("results-join", """{"jobId":"gp-join","rowLimit":2}""")))!.Result!.Value;

        var artifact = result.GetProperty("structuredContent").GetProperty("artifacts")[0];
        artifact.TryGetProperty("uri", out _).Should().BeFalse("an MCP-only client cannot read a base64 data URI");
        artifact.GetProperty("uriOmitted").GetBoolean().Should().BeTrue();
        artifact.GetProperty("uriLength").GetInt32().Should().Be(uri.Length);
        artifact.GetProperty("referenceSha256").GetString().Should().HaveLength(64, "ingest by reference still works");
        var summary = artifact.GetProperty("featureSummary");
        summary.GetProperty("featureCount").GetInt32().Should().Be(4);
        summary.GetProperty("matchedFeatureCount").GetInt32().Should().Be(2);
        summary.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should().Equal("id", "note", "JOIN_COUNT");
        summary.GetProperty("geometryTypes").EnumerateArray().Select(t => t.GetString()).Should().Equal("Point");
        summary.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).Should().Equal(1, 2);
        summary.GetProperty("nextRowOffset").GetInt32().Should().Be(2);
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("4 features, 2 matched");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_EmptyInlineGeoJsonJoin_ReturnsZeroMatchedFeatures()
    {
        var uri = JoinResultUri(joinCounts: [], padding: 0);
        _jobService.GetJobResultsAsync("gp-empty", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(uri, description: new string('x', 1024)));

        var result = (await Dispatch(ToolCall("results-empty", """{"jobId":"gp-empty"}""")))!.Result!.Value;
        var summary = result.GetProperty("structuredContent").GetProperty("artifacts")[0].GetProperty("featureSummary");

        summary.GetProperty("featureCount").GetInt32().Should().Be(0);
        summary.GetProperty("matchedFeatureCount").GetInt32().Should().Be(0);
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("0 features, 0 matched");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_IncludeUriAndLastPage_KeepsTheUriAndEndsPaging()
    {
        var uri = JoinResultUri(joinCounts: [1, 0, 2], padding: 1200);
        _jobService.GetJobResultsAsync("gp-join", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(uri));

        var structured = (await Dispatch(ToolCall(
                "results-page", """{"jobId":"gp-join","includeUri":true,"rowOffset":2,"rowLimit":5}""")))!
            .Result!.Value.GetProperty("structuredContent");

        var artifact = structured.GetProperty("artifacts")[0];
        artifact.GetProperty("uri").GetString().Should().Be(uri);
        artifact.TryGetProperty("uriOmitted", out _).Should().BeFalse();
        var summary = artifact.GetProperty("featureSummary");
        summary.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).Should().Equal(3);
        summary.TryGetProperty("nextRowOffset", out _).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("""{"jobId":"gp-join","rowLimit":51}""")]
    [InlineData("""{"jobId":"gp-join","rowOffset":-1}""")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RowWindowOutOfRange_IsRefusedBeforeReading(string arguments)
    {
        var response = await Dispatch(ToolCall("results-window", arguments));

        response!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        ServiceCalls().Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_NonObjectGeoJson_DoesNotBreakArtifactRetrieval(string json)
    {
        var uri = "data:application/geo+json;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        _jobService.GetJobResultsAsync("gp-join", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(uri));

        var result = (await Dispatch(ToolCall("results-invalid-shape", """{"jobId":"gp-join"}""")))!.Result!.Value;

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("structuredContent").GetProperty("artifacts")[0]
            .TryGetProperty("featureSummary", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CountOnly_DoesNotOfferANonAdvancingPage()
    {
        _jobService.GetJobResultsAsync("gp-join", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(JoinResultUri([1, 0], 0)));

        var summary = (await Dispatch(ToolCall("results-count", """{"jobId":"gp-join","rowLimit":0}""")))!
            .Result!.Value.GetProperty("structuredContent").GetProperty("artifacts")[0].GetProperty("featureSummary");

        summary.GetProperty("featureCount").GetInt32().Should().Be(2);
        summary.GetProperty("rows").GetArrayLength().Should().Be(0);
        summary.TryGetProperty("nextRowOffset", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_OversizedRow_ReportsItsOffsetAndAdvancesThePage()
    {
        _jobService.GetJobResultsAsync("gp-join", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(JoinResultUri([1, 0], 5000)));

        var summary = (await Dispatch(ToolCall("results-large-row", """{"jobId":"gp-join","rowLimit":1}""")))!
            .Result!.Value.GetProperty("structuredContent").GetProperty("artifacts")[0].GetProperty("featureSummary");

        summary.GetProperty("featureCount").GetInt32().Should().Be(2);
        summary.GetProperty("rows").GetArrayLength().Should().Be(0);
        summary.GetProperty("omittedRowOffsets")[0].GetInt32().Should().Be(0);
        summary.GetProperty("nextRowOffset").GetInt32().Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_BufferWithJoinCountAttribute_DoesNotReportMatchedFeatures()
    {
        var uri = JoinResultUri([1], 0, "analytics.buffer");
        _jobService.GetJobResultsAsync("gp-buffer", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(uri));

        var summary = (await Dispatch(ToolCall("results-buffer", """{"jobId":"gp-buffer"}""")))!
            .Result!.Value.GetProperty("structuredContent").GetProperty("artifacts")[0].GetProperty("featureSummary");

        summary.GetProperty("rows")[0].GetProperty("JOIN_COUNT").GetInt32().Should().Be(1);
        summary.TryGetProperty("matchedFeatureCount", out _).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(100)]
    [InlineData(101)]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_FieldPreview_ReportsWhetherNamesWereOmitted(int fieldCount)
    {
        var properties = string.Join(",", Enumerable.Range(0, fieldCount).Select(index => $"""
            "f{index}":1
            """));
        var json = $$$"""{"type":"FeatureCollection","features":[{"type":"Feature","properties":{ {{{properties}}} }},{"type":"Feature","properties":{"f0":2}}]}""";
        var uri = "data:application/geo+json;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        _jobService.GetJobResultsAsync("gp-wide", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(uri));

        var summary = (await Dispatch(ToolCall("results-wide", """{"jobId":"gp-wide","rowLimit":0}""")))!
            .Result!.Value.GetProperty("structuredContent").GetProperty("artifacts")[0].GetProperty("featureSummary");

        summary.GetProperty("fields").GetArrayLength().Should().Be(100);
        summary.GetProperty("fieldsTruncated").GetBoolean().Should().Be(fieldCount > 100);
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(5000)]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_job_results")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RowPreview_IgnoresInsignificantWhitespace(int paddingLength)
    {
        var padding = new string(' ', paddingLength);
        var json = $$$"""{"features":[{"properties":{ {{{padding}}} "id":1}}]}""";
        var uri = "data:application/geo+json;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        _jobService.GetJobResultsAsync("gp-format", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedPackage(uri));

        var summary = (await Dispatch(ToolCall("results-format", """{"jobId":"gp-format"}""")))!
            .Result!.Value.GetProperty("structuredContent").GetProperty("artifacts")[0].GetProperty("featureSummary");

        summary.GetProperty("rows")[0].GetProperty("id").GetInt32().Should().Be(1);
        summary.TryGetProperty("omittedRowOffsets", out _).Should().BeFalse();
    }

    private static string JoinResultUri(int[] joinCounts, int padding, string processId = "analytics.spatial-join")
    {
        var note = new string('n', padding);
        var features = joinCounts.Select((count, index) =>
            $$$"""{"type":"Feature","geometry":{"type":"Point","coordinates":[{{{index}}},{{{index}}}]},"properties":{"id":{{{index + 1}}},"note":"{{{note}}}","JOIN_COUNT":{{{count}}}}}""");
        var json = $$"""{"type":"FeatureCollection","processId":"{{processId}}","features":[{{string.Join(",", features)}}]}""";
        return "data:application/geo+json;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static AnalysisResultPackage CompletedPackage(
        string uri = "honua://artifacts/artifact-1",
        string description = "5-meter parcel buffers")
        => AnalysisResultPackage.CreateCompleted(
            "pkg-123",
            new ResultSummary
            {
                Title = "Buffered parcels",
                Description = description
            },
            [
                new ArtifactRef
                {
                    ArtifactId = "artifact-1",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "Buffered output",
                    Uri = uri,
                    ContentType = "application/geo+json"
                }
            ],
            [],
            new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = ["geometry.buffer"]
            });

    private IEnumerable<string> ServiceCalls()
        => _jobService.ReceivedCalls().Select(call => call.GetMethodInfo().Name);

    private async Task<JsonElement> ErrorContent(McpJsonRpcRequest request)
    {
        var response = await Dispatch(request);
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        return result.GetProperty("structuredContent");
    }

    private async Task<McpJsonRpcResponse?> Dispatch(McpJsonRpcRequest request)
    {
        var surface = new McpDataAccessSurface(
            [new GetJobResultsTool(_jobService, NullLogger<GetJobResultsTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var services = new ServiceCollection().BuildServiceProvider();
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;

        return await surface.DispatchAsync(context, request, CancellationToken.None);
    }

    private static McpJsonRpcRequest ToolCall(string id, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""
            {"name":"{{GetJobResultsTool.ToolName}}","arguments":{{argumentsJson}}}
            """)
    };

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
