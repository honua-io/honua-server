// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal sealed class GetJobResultsTool : IMcpTool
{
    public const string ToolName = "honua_get_job_results";
    internal const int MaxExplicitInlineUriLength = 262_144;

    private const string InputSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["jobId"],
          "properties": {
            "jobId": {
              "type": "string",
              "minLength": 1,
              "description": "Job id from honua_execute_plan or honua_list_jobs. list_jobs reports status; this returns the terminal package."
            },
            "includeUri": {
              "type": "boolean",
              "default": false,
              "description": "Return inline data: URIs over 2048 chars (omitted by default; ingest uses referenceSha256)."
            },
            "rowOffset": {
              "type": "integer",
              "minimum": 0,
              "default": 0,
              "description": "First featureSummary row; page with nextRowOffset."
            },
            "rowLimit": {
              "type": "integer",
              "minimum": 0,
              "maximum": 50,
              "default": 10,
              "description": "Attribute rows per GeoJSON artifact; counts cover all features."
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
            "artifacts": { "type": "array", "items": { "type": "object", "required": ["artifactId", "kind", "label"], "properties": { "artifactId": { "type": "string" }, "kind": { "type": "string" }, "label": { "type": "string" }, "uri": { "type": ["string", "null"] }, "referenceSha256": { "type": ["string", "null"], "pattern": "^[0-9a-f]{64}$", "description": "Lowercase hex SHA-256 of the exact UTF-8 uri. Pass it to staged import; it does not prove importability." }, "contentType": { "type": ["string", "null"] }, "uriOmitted": { "type": "boolean" }, "uriLength": { "type": "integer" }, "featureSummary": { "type": "object", "required": ["featureCount", "fields", "geometryTypes", "rowOffset", "rows"], "properties": { "featureCount": { "type": "integer" }, "fields": { "type": "array", "items": { "type": "string" } }, "geometryTypes": { "type": "array", "items": { "type": "string" } }, "fieldsTruncated": { "type": "boolean" }, "geometryTypesTruncated": { "type": "boolean" }, "matchedFeatureCount": { "type": "integer" }, "rowOffset": { "type": "integer" }, "rows": { "type": "array", "items": { "type": "object" } }, "omittedRowOffsets": { "type": "array", "items": { "type": "integer" } }, "nextRowOffset": { "type": "integer" } } } } } },
            "totalArtifactCount": { "type": "integer" },
            "artifactsTruncated": { "type": "boolean" },
            "workspaceRefs": { "type": "array", "items": { "type": "object", "required": ["workspaceId", "kind", "label"], "properties": { "workspaceId": { "type": "string" }, "kind": { "type": "string" }, "label": { "type": "string" }, "resourceUri": { "type": ["string", "null"] } } } },
            "mapId": { "type": ["string", "null"] },
            "appId": { "type": ["string", "null"] },
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
        Description = "Read terminal results. GeoJSON featureSummary includes counts and paged rows. Full artifacts are at honua://jobs/{jobId}/results.",
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

        var window = ReadRowWindow(argument);
        var package = await _jobService
            .GetJobResultsAsync(argument.JobId, principal, cancellationToken)
            .ConfigureAwait(false);

        var output = JobResultsResource.ToResource(argument.JobId, package);
        var artifactCount = Math.Max(1, output.Artifacts.Count);
        var previewBytes = JobResultFeatureSummary.ResponsePreviewBytes / artifactCount;
        var encodedLengthLimit = JobResultFeatureSummary.MaxEncodedPreviewLength / artifactCount;
        var preview = new JobResultFeatureSummary.PreviewRequest(window, previewBytes, encodedLengthLimit);
        foreach (var artifact in output.Artifacts)
        {
            JobResultFeatureSummary.AttachSummary(artifact, preview, cancellationToken);
            if (argument.IncludeUri != true)
            {
                JobResultFeatureSummary.OmitLargeInlineUri(artifact);
            }
            else if (artifact.Uri is { Length: > MaxExplicitInlineUriLength } inlineUri
                && inlineUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                artifact.Uri = null;
                artifact.UriOmitted = true;
                artifact.UriLength = inlineUri.Length;
            }
        }

        return McpToolHelpers.SuccessResult(
            output,
            McpJsonContext.Default.McpJobResultsResource,
            Summarize);
    }

    private static JobResultFeatureSummary.RowWindow ReadRowWindow(McpGetJobResultsArgument argument)
    {
        var rowOffset = argument.RowOffset ?? 0;
        var rowLimit = argument.RowLimit ?? JobResultFeatureSummary.DefaultRowLimit;
        if (rowOffset < 0)
        {
            throw new GeoprocessingValidationException("rowOffset must be 0 or greater.");
        }

        if (rowLimit is < 0 or > JobResultFeatureSummary.MaxRowLimit)
        {
            throw new GeoprocessingValidationException(
                $"rowLimit must be between 0 and {JobResultFeatureSummary.MaxRowLimit}.");
        }

        return new JobResultFeatureSummary.RowWindow(rowOffset, rowLimit);
    }

    private static string Summarize(McpJobResultsResource resource)
    {
        var counts = resource.Artifacts
            .Where(artifact => artifact.FeatureSummary is not null)
            .Select(artifact => DescribeCounts(artifact.Label, artifact.FeatureSummary!));
        var detail = string.Join("; ", counts);
        var suffix = detail.Length > 0 ? " (" + detail + ")" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Job {resource.JobId} results: status {resource.Status}, {resource.TotalArtifactCount} artifact(s){suffix}. Full package in structuredContent.");
    }

    private static string DescribeCounts(string label, McpArtifactFeatureSummary summary)
        => summary.MatchedFeatureCount is { } matched
            ? string.Create(CultureInfo.InvariantCulture, $"{label}: {summary.FeatureCount} features, {matched} matched")
            : string.Create(CultureInfo.InvariantCulture, $"{label}: {summary.FeatureCount} features");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

internal sealed class McpGetJobResultsArgument
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("includeUri")]
    public bool? IncludeUri { get; set; }

    [JsonPropertyName("rowOffset")]
    public int? RowOffset { get; set; }

    [JsonPropertyName("rowLimit")]
    public int? RowLimit { get; set; }
}

[JsonSerializable(typeof(McpGetJobResultsArgument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class GetJobResultsJsonContext : JsonSerializerContext;
