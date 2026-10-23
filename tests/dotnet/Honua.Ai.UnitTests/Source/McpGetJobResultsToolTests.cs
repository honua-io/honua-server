// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Focused coverage for <c>honua_get_job_results</c>: it adapts
/// <see cref="IGeoprocessingJobService.GetJobResultsAsync"/> so a tool-only client
/// can read a terminal result package. Ownership and the job-read grant stay in
/// the service. An unavailable job substrate uses the submission refusal envelope.
/// </summary>
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
        structured.GetProperty("artifactsTruncated").GetBoolean().Should().BeFalse();
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
        structured.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.RedisDependency);
        structured.GetProperty("retryable").GetBoolean().Should().BeFalse();
        structured.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
    }

    private static AnalysisResultPackage CompletedPackage()
        => AnalysisResultPackage.CreateCompleted(
            "pkg-123",
            new ResultSummary
            {
                Title = "Buffered parcels",
                Description = "5-meter parcel buffers"
            },
            [
                new ArtifactRef
                {
                    ArtifactId = "artifact-1",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "Buffered output",
                    Uri = "honua://artifacts/artifact-1",
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
