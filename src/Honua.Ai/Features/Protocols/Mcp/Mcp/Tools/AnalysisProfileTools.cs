// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal abstract class AnalysisProfileToolBase : IMcpTool, IMcpProfileTool
{
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger _logger;

    protected AnalysisProfileToolBase(IGeoprocessingJobService jobService, ILogger logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public abstract string Name { get; }

    public string ProfileName => "analysis";

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    protected abstract string Title { get; }

    protected abstract string Description { get; }

    protected abstract JsonElement InputSchema { get; }

    protected virtual bool IsExport => false;

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = Title,
        Description = Description,
        InputSchema = InputSchema,
        OutputSchema = McpToolOutputSchemas.AnalysisJobOutputSchema,
        Annotations = IsExport
            ? McpToolAnnotationSets.Write(Title, destructive: false, idempotent: true)
            : McpToolAnnotationSets.ReadOnly(Title)
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity(Name);
        McpLog.ToolInvoked(_logger, Name, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService.EnsureCallerAuthorizedAsync(
            principal,
            OperatorResourceType.Process,
            OperatorOperation.Execute,
            cancellationToken).ConfigureAwait(false);

        var argument = RequireObject(arguments);
        var principalKey = McpAuthorizationHelper.ResolvePrincipalKey(principal);
        var requestIdentity = BuildRequestIdentity(principalKey, argument);
        var plan = BuildPlan(argument) with { PlanId = $"mcp-{Name}-{requestIdentity}" };
        var idempotencyKey = $"mcp-analysis:{requestIdentity}";
        var job = await _jobService.SubmitJobAsync(
            plan,
            idempotencyKey,
            principal,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["submittedVia"] = "MCP-analysis-profile",
                ["analysisVerb"] = Name
            },
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ArtifactRef> artifacts = [];
        if (job.Status == ExecutionJobStatus.Succeeded)
        {
            artifacts = (await _jobService.GetJobResultsAsync(
                job.OperationId,
                principal,
                cancellationToken).ConfigureAwait(false)).Artifacts;
        }

        var artifactArray = new JsonArray();
        foreach (var artifact in artifacts)
        {
            artifactArray.Add(new JsonObject
            {
                ["artifactId"] = artifact.ArtifactId,
                ["kind"] = artifact.Kind.ToString(),
                ["label"] = artifact.Label,
                ["uri"] = artifact.Uri,
                ["contentType"] = artifact.ContentType
            });
        }

        var output = new JsonObject
        {
            ["jobId"] = job.OperationId,
            ["status"] = job.Status.ToString().ToLowerInvariant(),
            ["resourceUri"] = $"honua://jobs/{job.OperationId}",
            ["artifacts"] = artifactArray
        };
        using var document = JsonDocument.Parse(output.ToJsonString());
        return McpToolHelpers.SuccessJsonElement(document.RootElement);
    }

    protected abstract AnalysisPlan BuildPlan(JsonElement argument);

    protected static AnalysisPlan SingleStepPlan(
        string verb,
        AnalysisPlanStepKind kind,
        string? processId,
        IReadOnlyDictionary<string, string> inputs,
        params ArtifactKind[] outputs)
        => new()
        {
            PlanId = $"mcp-{verb}-{Guid.NewGuid():N}",
            IntentId = $"mcp-analysis:{verb}",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = verb,
                    Kind = kind,
                    ProcessId = processId,
                    Inputs = inputs
                }
            ],
            Outputs = outputs
        };

    protected static JsonElement RequireObject(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } objectValue)
        {
            throw new GeoprocessingValidationException("Tool arguments must be a JSON object.");
        }

        return objectValue;
    }

    protected static JsonElement RequireProperty(JsonElement argument, string propertyName)
    {
        if (!argument.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new GeoprocessingValidationException($"'{propertyName}' is required.");
        }

        return value;
    }

    protected static string RequireString(JsonElement argument, string propertyName)
    {
        var value = RequireProperty(argument, propertyName);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new GeoprocessingValidationException($"'{propertyName}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    protected static DatasetReference RequireDataset(JsonElement argument, string propertyName)
    {
        var value = RequireProperty(argument, propertyName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new GeoprocessingValidationException($"'{propertyName}' must be a layer or artifact reference.");
        }

        if (value.TryGetProperty("artifactId", out var artifactId)
            && artifactId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(artifactId.GetString()))
        {
            return new DatasetReference(false, null, null, artifactId.GetString()!);
        }

        if (value.TryGetProperty("serviceId", out var serviceId)
            && serviceId.ValueKind == JsonValueKind.String
            && value.TryGetProperty("layerId", out var layerId)
            && layerId.TryGetInt32(out var parsedLayerId)
            && parsedLayerId >= 0)
        {
            return new DatasetReference(true, serviceId.GetString(), parsedLayerId, null);
        }

        throw new GeoprocessingValidationException(
            $"'{propertyName}' must contain either serviceId/layerId or artifactId.");
    }

    protected static void CopyScalar(
        JsonElement argument,
        string sourceName,
        IDictionary<string, string> target,
        string? targetName = null)
    {
        if (!argument.TryGetProperty(sourceName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        target[targetName ?? sourceName] = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
    }

    protected static string JoinStringArray(JsonElement argument, string propertyName)
    {
        if (!argument.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(",", value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()));
    }

    private string BuildRequestIdentity(string principalKey, JsonElement argument)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            principalKey + ":" + Name + ":" + argument.GetRawText()));
        return Convert.ToHexString(bytes);
    }

    protected sealed record DatasetReference(
        bool IsLayer,
        string? ServiceId,
        int? LayerId,
        string? ArtifactId)
    {
        public string CanonicalInput => IsLayer
            ? string.Create(CultureInfo.InvariantCulture, $"honua://services/{ServiceId}/layers/{LayerId}")
            : ArtifactId!;
    }
}

internal sealed class BufferFeaturesTool(IGeoprocessingJobService jobs, ILogger<BufferFeaturesTool> logger)
    : AnalysisProfileToolBase(jobs, logger)
{
    public const string ToolName = "honua_buffer_features";
    public override string Name => ToolName;
    protected override string Title => "Buffer features";
    protected override string Description => "Buffer features through the canonical job service.";
    protected override JsonElement InputSchema => McpAnalysisToolSchemas.BufferFeatures;

    protected override AnalysisPlan BuildPlan(JsonElement argument)
    {
        var source = RequireDataset(argument, "source");
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        string processId;
        if (source.IsLayer)
        {
            processId = "analytics.buffer-aggregate";
            inputs["layerId"] = source.LayerId!.Value.ToString(CultureInfo.InvariantCulture);
            CopyScalar(argument, "where", inputs);
        }
        else
        {
            processId = "analytics.buffer-aggregate-managed";
            inputs["input"] = source.CanonicalInput;
        }

        CopyScalar(argument, "distance", inputs);
        CopyScalar(argument, "unit", inputs);
        inputs["dissolve"] = argument.TryGetProperty("dissolve", out var dissolve)
            ? dissolve.GetRawText()
            : "false";
        return SingleStepPlan("buffer-features", AnalysisPlanStepKind.Geoprocess, processId, inputs, ArtifactKind.FeatureLayer);
    }
}

internal sealed class OverlayFeaturesTool(IGeoprocessingJobService jobs, ILogger<OverlayFeaturesTool> logger)
    : AnalysisProfileToolBase(jobs, logger)
{
    public const string ToolName = "honua_overlay_features";
    public override string Name => ToolName;
    protected override string Title => "Overlay features";
    protected override string Description => "Overlay two datasets through the canonical job service.";
    protected override JsonElement InputSchema => McpAnalysisToolSchemas.OverlayFeatures;

    protected override AnalysisPlan BuildPlan(JsonElement argument)
    {
        var source = RequireDataset(argument, "source");
        var overlay = RequireDataset(argument, "overlay");
        var operation = RequireString(argument, "operation");
        var processId = operation switch
        {
            "intersect" => "overlay.intersect",
            "union" => "overlay.union",
            "difference" or "erase" => "overlay.erase",
            "clip" => "overlay.clip",
            _ => throw new GeoprocessingValidationException($"Unsupported overlay operation '{operation}'.")
        };
        var secondInput = operation switch
        {
            "difference" or "erase" => "erase",
            "clip" => "clip",
            _ => "overlay"
        };
        return SingleStepPlan(
            "overlay-features",
            AnalysisPlanStepKind.Geoprocess,
            processId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["input"] = source.CanonicalInput,
                [secondInput] = overlay.CanonicalInput
            },
            ArtifactKind.FeatureLayer);
    }
}

internal sealed class SummarizeStatisticsTool(IGeoprocessingJobService jobs, ILogger<SummarizeStatisticsTool> logger)
    : AnalysisProfileToolBase(jobs, logger)
{
    public const string ToolName = "honua_summarize_statistics";
    public override string Name => ToolName;
    protected override string Title => "Summarize statistics";
    protected override string Description => "Summarize dataset statistics through the canonical job service.";
    protected override JsonElement InputSchema => McpAnalysisToolSchemas.SummarizeStatistics;

    protected override AnalysisPlan BuildPlan(JsonElement argument)
    {
        var source = RequireDataset(argument, "source");
        var statisticValues = RequireProperty(argument, "statistics");
        if (statisticValues.ValueKind != JsonValueKind.Array || statisticValues.GetArrayLength() == 0)
        {
            throw new GeoprocessingValidationException("'statistics' must contain at least one statistic.");
        }

        var statistics = statisticValues.EnumerateArray().Select(statistic =>
            $"{RequireString(statistic, "onField")}:{RequireString(statistic, "statisticType")}");
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["input"] = source.CanonicalInput,
            ["statistics"] = string.Join(";", statistics)
        };
        var groupBy = JoinStringArray(argument, "groupByFields");
        if (groupBy.Length > 0)
        {
            inputs["caseFields"] = groupBy;
        }

        return SingleStepPlan("summarize-statistics", AnalysisPlanStepKind.Geoprocess, "statistics.summarize", inputs, ArtifactKind.Table);
    }
}

internal sealed class ReprojectFeaturesTool(IGeoprocessingJobService jobs, ILogger<ReprojectFeaturesTool> logger)
    : AnalysisProfileToolBase(jobs, logger)
{
    public const string ToolName = "honua_reproject_features";
    public override string Name => ToolName;
    protected override string Title => "Reproject features";
    protected override string Description => "Reproject features through the canonical job service.";
    protected override JsonElement InputSchema => McpAnalysisToolSchemas.ReprojectFeatures;

    protected override AnalysisPlan BuildPlan(JsonElement argument)
    {
        var source = RequireDataset(argument, "source");
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        string processId;
        if (source.IsLayer)
        {
            processId = "conversion.feature-project";
            inputs["layerId"] = source.LayerId!.Value.ToString(CultureInfo.InvariantCulture);
            CopyScalar(argument, "targetSrid", inputs);
        }
        else
        {
            processId = "transform.reproject";
            inputs["input"] = source.CanonicalInput;
            inputs["fromSrid"] = argument.TryGetProperty("sourceSrid", out var sourceSrid)
                ? sourceSrid.GetRawText()
                : "4326";
            CopyScalar(argument, "targetSrid", inputs, "toSrid");
        }

        return SingleStepPlan("reproject-features", AnalysisPlanStepKind.Geoprocess, processId, inputs, ArtifactKind.FeatureLayer);
    }
}

internal sealed class JoinFeaturesTool(IGeoprocessingJobService jobs, ILogger<JoinFeaturesTool> logger)
    : AnalysisProfileToolBase(jobs, logger)
{
    public const string ToolName = "honua_join_features";
    public override string Name => ToolName;
    protected override string Title => "Join features";
    protected override string Description => "Join datasets through the canonical job service.";
    protected override JsonElement InputSchema => McpAnalysisToolSchemas.JoinFeatures;

    protected override AnalysisPlan BuildPlan(JsonElement argument)
    {
        var target = RequireDataset(argument, "target");
        var join = RequireDataset(argument, "join");
        var joinType = RequireString(argument, "joinType");
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        string processId;
        if (joinType == "spatial" && target.IsLayer && join.IsLayer)
        {
            processId = "analytics.spatial-join";
            inputs["layerId"] = target.LayerId!.Value.ToString(CultureInfo.InvariantCulture);
            inputs["joinLayerId"] = join.LayerId!.Value.ToString(CultureInfo.InvariantCulture);
            CopyScalar(argument, "spatialRelationship", inputs, "predicate");
        }
        else if (joinType == "spatial")
        {
            processId = "analytics.spatial-join-managed";
            inputs["input"] = target.CanonicalInput;
            inputs["join"] = join.CanonicalInput;
            CopyScalar(argument, "spatialRelationship", inputs, "predicate");
        }
        else if (joinType == "attribute")
        {
            processId = "transform.attribute-join";
            inputs["input"] = target.CanonicalInput;
            inputs["right"] = join.CanonicalInput;
            inputs["leftKeys"] = RequireString(argument, "targetField");
            inputs["rightKeys"] = RequireString(argument, "joinField");
            inputs["type"] = "left";
        }
        else
        {
            throw new GeoprocessingValidationException($"Unsupported join type '{joinType}'.");
        }

        return SingleStepPlan("join-features", AnalysisPlanStepKind.Geoprocess, processId, inputs, ArtifactKind.FeatureLayer);
    }
}

internal sealed class ExportDatasetTool(IGeoprocessingJobService jobs, ILogger<ExportDatasetTool> logger)
    : AnalysisProfileToolBase(jobs, logger)
{
    public const string ToolName = "honua_export_dataset";
    public override string Name => ToolName;
    protected override string Title => "Export dataset";
    protected override string Description => "Export a dataset through the canonical job service.";
    protected override JsonElement InputSchema => McpAnalysisToolSchemas.ExportDataset;
    protected override bool IsExport => true;

    protected override AnalysisPlan BuildPlan(JsonElement argument)
    {
        var source = RequireDataset(argument, "source");
        var format = RequireString(argument, "format");
        if (!string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase))
        {
            throw new GeoprocessingValidationException(
                $"Export format '{format}' is not supported by the current process catalog; use 'geojson'.");
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = source.CanonicalInput,
            ["format"] = format
        };
        CopyScalar(argument, "where", inputs);
        CopyScalar(argument, "returnGeometry", inputs);
        CopyScalar(argument, "outSrid", inputs);
        var outFields = JoinStringArray(argument, "outFields");
        if (outFields.Length > 0)
        {
            inputs["outFields"] = outFields;
        }

        return SingleStepPlan("export-dataset", AnalysisPlanStepKind.Export, null, inputs, ArtifactKind.File);
    }
}
