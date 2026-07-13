// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the <c>honua_list_jobs</c> MCP tool: it adapts the
/// canonical caller-scoped <see cref="IGeoprocessingJobService.ListJobsAsync"/>
/// listing (status filter + cursor paging) into the JSON-RPC surface so an agent
/// can find a job to feed <c>honua_cancel_job</c>.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpListJobsToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_list_jobs")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ListJobs_ProjectsCallerScopedJobsWithCursor()
    {
        _jobService
            .ListJobsAsync(Arg.Any<GeoprocessingJobListFilter>(), Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new GeoprocessingJobListPage
            {
                Items =
                [
                    Job("gp-running-1", ExecutionJobStatus.Running, "Executing"),
                    Job("gp-queued-1", ExecutionJobStatus.Queued, "Queued")
                ],
                NextCursor = "cursor-2"
            });

        var response = await Dispatch(ToolCall("jobs-1", ListJobsTool.ToolName, "{}"));

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("jobCount").GetInt32().Should().Be(2);
        structured.GetProperty("nextCursor").GetString().Should().Be("cursor-2");
        var jobs = structured.GetProperty("jobs").EnumerateArray().ToArray();
        jobs[0].GetProperty("jobId").GetString().Should().Be("gp-running-1");
        jobs[0].GetProperty("status").GetString().Should().Be(nameof(ExecutionJobStatus.Running));
        jobs[0].GetProperty("phase").GetString().Should().Be("Executing");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_list_jobs")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ListJobs_PassesStatusFilterAndClampedLimitToService()
    {
        GeoprocessingJobListFilter? captured = null;
        _jobService
            .ListJobsAsync(Arg.Do<GeoprocessingJobListFilter>(f => captured = f), Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new GeoprocessingJobListPage { Items = [], NextCursor = null });

        var response = await Dispatch(ToolCall("jobs-2", ListJobsTool.ToolName, """
            {"status":["Queued","Running"],"limit":9999,"cursor":"c1"}
            """));

        response!.Error.Should().BeNull();
        captured.Should().NotBeNull();
        captured!.Statuses.Should().BeEquivalentTo([ExecutionJobStatus.Queued, ExecutionJobStatus.Running]);
        captured.Limit.Should().Be(ListJobsTool.MaxLimit, "the tool clamps limit to its supported ceiling");
        captured.Cursor.Should().Be("c1");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_list_jobs")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ListJobs_UnknownStatus_ReturnsToolError()
    {
        var response = await Dispatch(ToolCall("jobs-3", ListJobsTool.ToolName, """
            {"status":["Bogus"]}
            """));

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("error").GetProperty("kind").GetString()
            .Should().Be("ValidationFailed");
    }

    private static ExecutionJobRecord Job(string id, ExecutionJobStatus status, string phase) => new()
    {
        OperationId = id,
        Status = status,
        CurrentPhase = phase,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            Kind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "geo-workload"
        }
    };

    private async Task<McpJsonRpcResponse?> Dispatch(McpJsonRpcRequest request)
    {
        var surface = new McpOperatorSurface(
            [new ListJobsTool(_jobService, NullLogger<ListJobsTool>.Instance)],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var services = new ServiceCollection().BuildServiceProvider();
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;

        return await surface.DispatchAsync(context, request, CancellationToken.None);
    }

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""
            {"name":"{{toolName}}","arguments":{{argumentsJson}}}
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
