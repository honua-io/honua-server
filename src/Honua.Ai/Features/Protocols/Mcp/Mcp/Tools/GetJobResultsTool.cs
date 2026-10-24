// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that reads one geoprocessing job's result package. Thin adapter over
/// <see cref="IGeoprocessingJobService.GetJobResultsAsync"/> — the same call OGC
/// API Processes and <see cref="JobResultsResource"/> use — so a tool-only client
/// can retrieve results without a resources/read implementation. The service keeps
/// the operator job-read grant and per-job ownership. Status polling stays on
/// <c>honua_list_jobs</c> and <c>honua://jobs/{jobId}</c>.
/// </summary>
internal sealed class GetJobResultsTool : IMcpTool
{
    public const string ToolName = "honua_get_job_results";

    private const string InputSchemaJson = """
        {
          "type": "object",
          "required": ["jobId"],
          "properties": {
            "jobId": {
              "type": "string",
              "minLength": 1,
              "description": "Job id from honua_execute_plan or honua_list_jobs. list_jobs reports status; this returns the terminal package."
            }
          }
        }
        """;

    private const string OutputSchemaJson = """
        {
          "type": "object",
          "required": ["jobId", "resultPackageId", "status", "summary", "artifacts", "totalArtifactCount", "artifactsTruncated", "workspaceRefs", "assumptions", "provenance", "errors"],
          "properties": {
            "jobId": { "type": "string" },
            "resultPackageId": { "type": "string" },
            "status": { "type": "string" },
            "summary": { "type": "object", "required": ["title"], "properties": { "title": { "type": "string" }, "description": { "type": ["string", "null"] } } },
            "artifacts": { "type": "array", "items": { "type": "object", "required": ["artifactId", "kind", "label"], "properties": { "artifactId": { "type": "string" }, "kind": { "type": "string" }, "label": { "type": "string" }, "uri": { "type": ["string", "null"] }, "contentType": { "type": ["string", "null"] } } } },
            "totalArtifactCount": { "type": "integer" },
            "artifactsTruncated": { "type": "boolean" },
            "workspaceRefs": { "type": "array", "items": { "type": "object", "required": ["workspaceId", "kind", "label"], "properties": { "workspaceId": { "type": "string" }, "kind": { "type": "string" }, "label": { "type": "string" }, "resourceUri": { "type": ["string", "null"] } } } },
            "mapPackageId": { "type": ["string", "null"] },
            "appPackageId": { "type": ["string", "null"] },
            "assumptions": { "type": "array", "items": { "type": "string" } },
            "provenance": { "type": "object" },
            "errors": { "type": "array" }
          }
        }
        """;

    private static readonly JsonElement InputSchema = Parse(InputSchemaJson);
    private static readonly JsonElement OutputSchema = Parse(OutputSchemaJson);

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<GetJobResultsTool> _logger;

    public GetJobResultsTool(IGeoprocessingJobService jobService, ILogger<GetJobResultsTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Get job results",
        Description = "Read one terminal job's result package. "
            + "jobId comes from honua_execute_plan or honua_list_jobs, which still reports status. "
            + "Missing and other owners' jobs are not_found; a non-terminal job is failed_precondition. "
            + "honua://jobs/{jobId}/results stays available.",
        InputSchema = InputSchema,
        OutputSchema = OutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Get job results")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetJobResults");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseArguments(arguments, GetJobResultsJsonContext.Default.McpGetJobResultsArgument);
        if (string.IsNullOrWhiteSpace(argument.JobId))
        {
            throw new GeoprocessingValidationException("jobId is required.");
        }

        var package = await _jobService
            .GetJobResultsAsync(argument.JobId, principal, cancellationToken)
            .ConfigureAwait(false);

        var output = JobResultsResource.ToResource(argument.JobId, package);
        return McpToolHelpers.SuccessResult(
            output,
            McpJsonContext.Default.McpJobResultsResource,
            Summarize);
    }

    private static string Summarize(McpJobResultsResource resource)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Job {resource.JobId} results: status {resource.Status}, {resource.TotalArtifactCount} artifact(s). Full package in structuredContent.");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Arguments for <c>honua_get_job_results</c>.</summary>
internal sealed class McpGetJobResultsArgument
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }
}

/// <summary>
/// AOT-compatible source-generated JSON context for <c>honua_get_job_results</c>
/// arguments. The result package reuses <see cref="McpJobResultsResource"/>.
/// </summary>
[JsonSerializable(typeof(McpGetJobResultsArgument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class GetJobResultsJsonContext : JsonSerializerContext;
