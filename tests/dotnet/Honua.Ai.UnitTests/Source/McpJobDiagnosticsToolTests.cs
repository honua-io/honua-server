// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit coverage for the job diagnosis MCP tools (honua-server#6219): they forward the
/// caller's principal and arguments to the server-side reader, bound their inputs, and
/// surface the reader's observed / inferred / evidence payload unchanged.
/// </summary>
public sealed class McpJobDiagnosticsToolTests
{
    [UnitTest]
    public async Task DiagnoseJobTool_Invoke_ForwardsPrincipalAndArgumentsAndReturnsReaderPayload()
    {
        var reader = new FakeJobDiagnosticsReader();
        var context = BuildContext(reader);
        var tool = new DiagnoseJobTool(NullLogger<DiagnoseJobTool>.Instance);

        var result = await tool.InvokeAsync(context, Json("""{ "jobId": "gp-1", "events": 7, "eventCursor": "start" }"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.DiagnoseCalls.Should().Be(1);
        reader.LastPrincipal.Should().BeSameAs(context.User);
        reader.DiagnoseArgument!.JobId.Should().Be("gp-1");
        reader.DiagnoseArgument.Events.Should().Be(7);
        reader.DiagnoseArgument.EventCursor.Should().Be("start");
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("observed").GetProperty("status").GetString().Should().Be("Failed");
        structured.GetProperty("inferred").GetProperty("phase").GetString().Should().Be("failed");
        structured.GetProperty("caveat").GetString().Should().Contain("not proof");
    }

    [UnitTest]
    public async Task DiagnoseJobTool_Invoke_WithoutJobId_RejectsBeforeReadingAnything()
    {
        var reader = new FakeJobDiagnosticsReader();
        var context = BuildContext(reader);
        var tool = new DiagnoseJobTool(NullLogger<DiagnoseJobTool>.Instance);

        var act = () => tool.InvokeAsync(context, Json("""{ "jobId": "  " }"""), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        reader.DiagnoseCalls.Should().Be(0);
    }

    [UnitTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(501)]
    public async Task DiagnoseJobTool_Invoke_WithInvalidEventLimit_RejectsBeforeReadingAnything(int events)
    {
        var reader = new FakeJobDiagnosticsReader();
        var context = BuildContext(reader);
        var tool = new DiagnoseJobTool(NullLogger<DiagnoseJobTool>.Instance);

        var act = () => tool.InvokeAsync(context, Json($$"""{ "jobId": "gp-1", "events": {{events}} }"""), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
        reader.DiagnoseCalls.Should().Be(0);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(500)]
    public async Task DiagnoseJobTool_Invoke_WithValidEventLimit_ReadsDiagnosis(int? events)
    {
        var reader = new FakeJobDiagnosticsReader();
        var context = BuildContext(reader);
        var tool = new DiagnoseJobTool(NullLogger<DiagnoseJobTool>.Instance);
        var arguments = events.HasValue
            ? Json($$"""{ "jobId": "gp-1", "events": {{events.Value}} }""")
            : Json("""{ "jobId": "gp-1" }""");

        var result = await tool.InvokeAsync(context, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.DiagnoseCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task JobEvidenceTool_Invoke_BoundsEventsAndReturnsReaderPayload()
    {
        var reader = new FakeJobDiagnosticsReader();
        var context = BuildContext(reader);
        var tool = new JobEvidenceTool(NullLogger<JobEvidenceTool>.Instance);

        var tooMany = () => tool.InvokeAsync(context, Json("""{ "jobId": "gp-1", "events": 501 }"""), CancellationToken.None);
        await tooMany.Should().ThrowAsync<GeoprocessingValidationException>();
        reader.EvidenceCalls.Should().Be(0);

        var result = await tool.InvokeAsync(context, Json("""{ "jobId": "gp-1", "events": 500 }"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        reader.EvidenceCalls.Should().Be(1);
        reader.EvidenceArgument!.JobId.Should().Be("gp-1");
        reader.EvidenceArgument.Events.Should().Be(500);
        result.StructuredContent!.Value.GetProperty("events").GetProperty("totalCount").GetInt32().Should().Be(12);
    }

    [UnitTest]
    public void Describe_AdvertisesReadOnlyToolsWithSchemas()
    {
        var diagnose = new DiagnoseJobTool(NullLogger<DiagnoseJobTool>.Instance).Describe();
        diagnose.Name.Should().Be("honua_diagnose_job");
        diagnose.Annotations!.ReadOnlyHint.Should().BeTrue();
        diagnose.InputSchema.GetProperty("required")[0].GetString().Should().Be("jobId");
        var outputSchema = $"{diagnose.OutputSchema}";
        foreach (var required in new[] { "observed", "inferred", "evidence", "caveat" })
        {
            outputSchema.Should().Contain($"\"{required}\"");
        }

        var evidence = new JobEvidenceTool(NullLogger<JobEvidenceTool>.Instance).Describe();
        evidence.Name.Should().Be("honua_job_evidence");
        evidence.Annotations!.ReadOnlyHint.Should().BeTrue();
    }

    private static DefaultHttpContext BuildContext(IMcpJobDiagnosticsReader reader)
    {
        var services = new ServiceCollection();
        services.AddSingleton(reader);
        var provider = services.BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "mcp-test")],
                "Test"))
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeJobDiagnosticsReader : IMcpJobDiagnosticsReader
    {
        public int DiagnoseCalls { get; private set; }

        public int EvidenceCalls { get; private set; }

        public ClaimsPrincipal? LastPrincipal { get; private set; }

        public McpDiagnoseJobArgument? DiagnoseArgument { get; private set; }

        public McpJobEvidenceArgument? EvidenceArgument { get; private set; }

        public Task<JsonElement> DiagnoseJobAsync(
            ClaimsPrincipal principal,
            McpDiagnoseJobArgument argument,
            CancellationToken cancellationToken)
        {
            DiagnoseCalls++;
            LastPrincipal = principal;
            DiagnoseArgument = argument;
            return Task.FromResult(Json(
                """
                {
                  "schema": "honua.mcp.job-diagnosis/v1",
                  "jobId": "gp-1",
                  "observed": { "status": "Failed" },
                  "inferred": { "phase": "failed" },
                  "evidence": { "missing": [], "truncated": [], "notes": [] },
                  "caveat": "A returned diagnosis is not proof that the job succeeded."
                }
                """));
        }

        public Task<JsonElement> GetJobEvidenceAsync(
            ClaimsPrincipal principal,
            McpJobEvidenceArgument argument,
            CancellationToken cancellationToken)
        {
            EvidenceCalls++;
            LastPrincipal = principal;
            EvidenceArgument = argument;
            return Task.FromResult(Json(
                """
                {
                  "schema": "honua.mcp.job-evidence/v1",
                  "jobId": "gp-1",
                  "steps": [],
                  "events": { "state": "available", "items": [], "totalCount": 12, "limit": 500, "truncated": false },
                  "artifacts": { "state": "none" },
                  "evidence": { "missing": [], "truncated": [], "notes": [] }
                }
                """));
        }
    }
}
