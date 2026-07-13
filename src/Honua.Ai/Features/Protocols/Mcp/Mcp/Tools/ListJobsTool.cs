// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that enumerates the caller's geoprocessing jobs (newest first), with
/// an optional status filter and cursor paging. Thin adapter over the canonical
/// <see cref="IGeoprocessingJobService.ListJobsAsync"/> — the same caller-scoped,
/// ownership-filtered listing the GPServer and OGC API Processes job surfaces
/// use — so an agent can find a queued or stuck job to feed
/// <c>honua_cancel_job</c> without inventing its own job store. Read-only; the
/// service enforces the operator job-read grant and per-job ownership.
/// </summary>
internal sealed class ListJobsTool : IMcpTool
{
    public const string ToolName = "honua_list_jobs";

    /// <summary>Default page size when the caller omits <c>limit</c>.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Hard ceiling on the page size (matches the service clamp).</summary>
    public const int MaxLimit = 200;

    private const string InputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "status": {
              "type": "array",
              "items": {
                "type": "string",
                "enum": ["Queued", "Provisioning", "Running", "Succeeded", "Failed", "Cancelled"]
              },
              "description": "Optional status filter. When omitted, jobs of any status (including terminal history) are returned. A queued or running job is a candidate for honua_cancel_job."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 200,
              "default": 50,
              "description": "Maximum number of jobs to return on this page (capped at 200)."
            },
            "cursor": {
              "type": "string",
              "description": "Opaque cursor returned as nextCursor by a previous call. When a response reports a non-null nextCursor, re-issue the same call with cursor set to it to fetch the next page."
            }
          }
        }
        """;

    private const string OutputSchemaJson = """
        {
          "type": "object",
          "required": ["jobCount", "jobs"],
          "properties": {
            "jobCount": { "type": "integer" },
            "nextCursor": { "type": ["string", "null"] },
            "jobs": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["jobId", "status"],
                "properties": {
                  "jobId": { "type": "string" },
                  "status": { "type": "string" },
                  "phase": { "type": ["string", "null"] },
                  "priority": { "type": "string" },
                  "createdAt": { "type": "string" },
                  "updatedAt": { "type": "string" }
                }
              }
            }
          }
        }
        """;

    private static readonly JsonElement InputSchema = Parse(InputSchemaJson);
    private static readonly JsonElement OutputSchema = Parse(OutputSchemaJson);

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ListJobsTool> _logger;

    public ListJobsTool(IGeoprocessingJobService jobService, ILogger<ListJobsTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "List jobs",
        Description = "List your geoprocessing jobs (newest first) with an optional status filter and cursor paging. "
            + "Use it to find a queued or running job to cancel with honua_cancel_job, or to poll a submitted job's status.",
        InputSchema = InputSchema,
        OutputSchema = OutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("List jobs")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListJobs");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var argument = McpToolHelpers.ParseOptionalArguments(arguments, ListJobsJsonContext.Default.McpListJobsArgument);

        var statuses = ParseStatuses(argument.Status);
        var limit = NormalizeLimit(argument.Limit);
        var cursor = string.IsNullOrWhiteSpace(argument.Cursor) ? null : argument.Cursor;

        var filter = new GeoprocessingJobListFilter
        {
            Statuses = statuses,
            Limit = limit,
            Cursor = cursor
        };

        var page = await _jobService.ListJobsAsync(filter, principal, cancellationToken).ConfigureAwait(false);

        var jobs = new List<McpJobSummary>(page.Items.Count);
        foreach (var job in page.Items)
        {
            jobs.Add(new McpJobSummary
            {
                JobId = job.OperationId,
                Status = job.Status.ToString(),
                Phase = job.CurrentPhase,
                Priority = job.Priority.ToString(),
                CreatedAt = job.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAt = job.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        var output = new McpListJobsOutput
        {
            JobCount = jobs.Count,
            NextCursor = page.NextCursor,
            Jobs = jobs
        };

        return McpToolHelpers.SuccessResult(output, ListJobsJsonContext.Default.McpListJobsOutput);
    }

    private static IReadOnlyList<ExecutionJobStatus> ParseStatuses(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<ExecutionJobStatus>();
        }

        var statuses = new List<ExecutionJobStatus>(raw.Count);
        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !Enum.TryParse<ExecutionJobStatus>(value.Trim(), ignoreCase: true, out var status)
                || !Enum.IsDefined(status))
            {
                throw new GeoprocessingValidationException(
                    $"Unknown job status '{value}'. Expected one of: Queued, Provisioning, Running, Succeeded, Failed, Cancelled.");
            }

            statuses.Add(status);
        }

        return statuses;
    }

    private static int NormalizeLimit(int? limit)
    {
        if (limit is not { } value)
        {
            return DefaultLimit;
        }

        if (value < 1)
        {
            throw new GeoprocessingValidationException("'limit' must be a positive integer.");
        }

        return Math.Min(value, MaxLimit);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Arguments for <c>honua_list_jobs</c>.</summary>
internal sealed class McpListJobsArgument
{
    [JsonPropertyName("status")]
    public IReadOnlyList<string>? Status { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>Output for <c>honua_list_jobs</c>: a caller-scoped page of jobs.</summary>
internal sealed class McpListJobsOutput
{
    [JsonPropertyName("jobCount")]
    public int JobCount { get; set; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("jobs")]
    public IReadOnlyList<McpJobSummary> Jobs { get; set; } = [];
}

/// <summary>One job entry in a <c>honua_list_jobs</c> page.</summary>
internal sealed class McpJobSummary
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>
/// AOT-compatible source-generated JSON context for the <c>honua_list_jobs</c>
/// DTOs. Kept local to the tool so its serializer surface is self-contained.
/// </summary>
[JsonSerializable(typeof(McpListJobsArgument))]
[JsonSerializable(typeof(McpListJobsOutput))]
[JsonSerializable(typeof(McpJobSummary))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ListJobsJsonContext : JsonSerializerContext;
