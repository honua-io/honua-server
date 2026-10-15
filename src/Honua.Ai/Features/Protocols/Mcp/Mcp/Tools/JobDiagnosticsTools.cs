// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Geoprocessing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// <c>honua_diagnose_job</c>: read-only diagnosis of one geoprocessing job over the
/// admin job diagnostics projection (honua-server#6219). The structured result keeps
/// <c>observed</c> facts apart from <c>inferred</c> explanations and lists the
/// evidence the server does not hold. A successful call means the evidence was
/// read; it is never proof that the job's analysis succeeded.
/// </summary>
internal sealed class DiagnoseJobTool(ILogger<DiagnoseJobTool> logger) : IMcpTool
{
    public const string ToolName = "honua_diagnose_job";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Diagnose job",
        Description =
            "Read-only diagnosis of one geoprocessing job you can read: which phase and attempt it is in, "
            + "whether the worker is alive, what code and workflow ran, which step failed with what structured "
            + "error, whether the outputs are complete, and whether a retry would repeat the same spec. "
            + "structuredContent.observed holds recorded facts; structuredContent.inferred holds the server's "
            + "explanations (canonical phase, liveness verdict, failure classification, recovery advice); "
            + "structuredContent.evidence lists what is missing or truncated. Treat inferred fields as "
            + "hypotheses to verify against observed ones. A returned diagnosis never means the job succeeded; "
            + "read observed.status. Use honua_job_evidence for more events, and honua_cancel_job or the "
            + "admin retry route for actions.",
        InputSchema = McpJobDiagnosticsSchemas.DiagnoseJobInputSchema,
        OutputSchema = McpJobDiagnosticsSchemas.DiagnoseJobOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Diagnose job")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("DiagnoseJob");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpDiagnoseJobArgument);
        if (string.IsNullOrWhiteSpace(argument.JobId))
        {
            throw new GeoprocessingValidationException("jobId is required.");
        }

        if (argument.Events is < 1 or > McpJobDiagnosticsSchemas.MaxEvents)
        {
            throw new GeoprocessingValidationException($"events must be between 1 and {McpJobDiagnosticsSchemas.MaxEvents}.");
        }

        var payload = await ResolveReader(httpContext)
            .DiagnoseJobAsync(principal, argument, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(payload, SummarizeDiagnosis);
    }

    private static string SummarizeDiagnosis(JsonElement element)
    {
        var jobId = element.TryGetProperty("jobId", out var id) ? id.GetString() : null;
        var status = element.TryGetProperty("observed", out var observed) && observed.TryGetProperty("status", out var s) ? s.GetString() : null;
        var phase = element.TryGetProperty("inferred", out var inferred) && inferred.TryGetProperty("phase", out var p) ? p.GetString() : null;
        return $"Job {jobId}: observed status {status}, inferred phase {phase}. Full diagnosis in structuredContent (observed / inferred / evidence).";
    }

    private static IMcpJobDiagnosticsReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpJobDiagnosticsReader>();
}

/// <summary>
/// <c>honua_job_evidence</c>: bounded evidence retrieval for one job: sanitized
/// steps, the most recent events and artifact availability, without the inferred
/// layer. The event tail is capped server-side; <c>truncated</c> says when it was.
/// </summary>
internal sealed class JobEvidenceTool(ILogger<JobEvidenceTool> logger) : IMcpTool
{
    public const string ToolName = "honua_job_evidence";

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Job evidence",
        Description =
            "Read-only retrieval of the recorded evidence behind a job diagnosis: sanitized per-step "
            + "commands and timings (timingSource distinguishes measured harness durations from inferred log timings), the most recent events "
            + "(bounded by events, maximum 500; truncated reports when older events exist), and artifact "
            + "availability. Paths and credentials are redacted before they leave the server. No "
            + "explanations are added; use honua_diagnose_job for the inferred layer.",
        InputSchema = McpJobDiagnosticsSchemas.JobEvidenceInputSchema,
        OutputSchema = McpJobDiagnosticsSchemas.JobEvidenceOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Job evidence")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetJobEvidence");
        McpLog.ToolInvoked(logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.McpJobEvidenceArgument);
        if (string.IsNullOrWhiteSpace(argument.JobId))
        {
            throw new GeoprocessingValidationException("jobId is required.");
        }

        if (argument.Events is < 1 or > McpJobDiagnosticsSchemas.MaxEvents)
        {
            throw new GeoprocessingValidationException($"events must be between 1 and {McpJobDiagnosticsSchemas.MaxEvents}.");
        }

        var payload = await ResolveReader(httpContext)
            .GetJobEvidenceAsync(principal, argument, cancellationToken)
            .ConfigureAwait(false);

        return McpToolHelpers.SuccessJsonElement(
            payload,
            static element =>
            {
                var jobId = element.TryGetProperty("jobId", out var id) ? id.GetString() : null;
                var total = element.TryGetProperty("events", out var events) && events.TryGetProperty("totalCount", out var t) ? t.GetInt32() : 0;
                return $"Evidence for job {jobId}: {total} recorded event(s); steps, events and artifacts in structuredContent.";
            });
    }

    private static IMcpJobDiagnosticsReader ResolveReader(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IMcpJobDiagnosticsReader>();
}
