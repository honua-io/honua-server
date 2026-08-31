// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Tools.EsriGp;

internal static class EsriGpToolNames
{
    public const string ListTasks = "honua_esri_gp_list_tasks";
    public const string DescribeTask = "honua_esri_gp_describe_task";
    public const string ExecuteTask = "honua_esri_gp_execute_task";
}

internal sealed class EsriGpListTasksTool(
    IGeoprocessingJobService jobService,
    IProcessCatalog processCatalog) : IMcpTool
{
    public string Name => EsriGpToolNames.ListTasks;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "List Esri GP tasks",
        Description = "List the callable GeoServices GPServer task projection, including Esri aliases.",
        InputSchema = EsriGpToolSchemas.ListInput,
        OutputSchema = EsriGpToolSchemas.ListOutput,
        Annotations = McpToolAnnotationSets.ReadOnly("List Esri GP tasks")
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext context, JsonElement? arguments, CancellationToken cancellationToken)
    {
        McpToolHelpers.EnsureNoArguments(arguments);
        var principal = McpAuthorizationHelper.EnsurePrincipal(context);
        await jobService.EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Catalog, OperatorOperation.Discover, cancellationToken).ConfigureAwait(false);
        var tasks = EsriGpProjection.List(processCatalog)
            .Select(task => EsriGpProjection.ToSummary(task.TaskName, task.Definition))
            .ToArray();
        return McpToolHelpers.SuccessResult(new EsriGpListTasksOutput { Tasks = tasks }, McpJsonContext.Default.EsriGpListTasksOutput);
    }
}

internal sealed class EsriGpDescribeTaskTool(
    IGeoprocessingJobService jobService,
    IProcessCatalog processCatalog) : IMcpTool
{
    public string Name => EsriGpToolNames.DescribeTask;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "Describe Esri GP task",
        Description = "Describe a callable GPServer task by canonical name or Esri alias.",
        InputSchema = EsriGpToolSchemas.DescribeInput,
        OutputSchema = EsriGpToolSchemas.DescribeOutput,
        Annotations = McpToolAnnotationSets.ReadOnly("Describe Esri GP task")
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext context, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(context);
        await jobService.EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Catalog, OperatorOperation.Discover, cancellationToken).ConfigureAwait(false);
        var input = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.EsriGpDescribeTaskInput);
        var definition = EsriGpProjection.Resolve(processCatalog, input.TaskName)
            ?? throw new GeoprocessingNotFoundException($"Esri GP task '{input.TaskName}' was not found.");
        return McpToolHelpers.SuccessResult(
            EsriGpProjection.ToDescription(input.TaskName, definition),
            McpJsonContext.Default.EsriGpTaskDescription);
    }
}

internal sealed class EsriGpExecuteTaskTool(
    IGeoprocessingJobService jobService,
    IProcessCatalog processCatalog) : IMcpTool
{
    public string Name => EsriGpToolNames.ExecuteTask;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "Execute Esri GP task",
        Description = "Submit a GPServer-compatible task through the canonical governed job runtime.",
        InputSchema = EsriGpToolSchemas.ExecuteInput,
        OutputSchema = EsriGpToolSchemas.ExecuteOutput,
        Annotations = McpToolAnnotationSets.Write("Execute Esri GP task", destructive: false, idempotent: true)
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext context, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(context);
        await jobService.EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken).ConfigureAwait(false);
        var input = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.EsriGpExecuteTaskInput);
        var definition = EsriGpProjection.Resolve(processCatalog, input.TaskName)
            ?? throw new GeoprocessingNotFoundException($"Esri GP task '{input.TaskName}' was not found.");
        var plan = EsriGpProjection.BuildPlan(input.ServiceId, input.TaskName, definition, input.Parameters);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["submittedVia"] = "MCP.EsriGP",
            [GeoprocessingProtocolMetadataKeys.GPServerServiceId] = input.ServiceId,
            [GeoprocessingProtocolMetadataKeys.GPServerTaskName] = input.TaskName
        };
        var job = await jobService.SubmitJobAsync(plan, input.IdempotencyKey, principal, metadata, cancellationToken).ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(new EsriGpExecuteTaskOutput
        {
            JobId = job.OperationId,
            Status = job.Status.ToString(),
            ResourceUri = McpResourceUris.JobUri(job.OperationId),
            ServiceId = input.ServiceId,
            TaskName = input.TaskName,
            ProcessId = definition.ProcessId
        }, McpJsonContext.Default.EsriGpExecuteTaskOutput);
    }
}

internal static class EsriGpProjection
{
    public static IEnumerable<(string TaskName, ProcessDefinition Definition)> List(IProcessCatalog catalog)
    {
        var processes = catalog.ListProcesses();
        var ids = processes.Select(p => p.ProcessId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes.Where(ProcessExecutionEligibility.IsJobCallable).OrderBy(p => p.ProcessId, StringComparer.Ordinal))
        {
            yield return (process.ProcessId, process);
            var alias = EsriGpTaskAliases.GetAlias(process.ProcessId);
            if (alias is not null && !ids.Contains(alias)) yield return (alias, process);
        }
    }

    public static ProcessDefinition? Resolve(IProcessCatalog catalog, string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName)) return null;
        var direct = catalog.GetProcess(taskName);
        if (direct is not null) return ProcessExecutionEligibility.IsJobCallable(direct) ? direct : null;
        if (catalog.ListProcesses().Any(p => string.Equals(p.ProcessId, taskName, StringComparison.OrdinalIgnoreCase))) return null;
        return EsriGpTaskAliases.TryResolveProcessId(taskName, out var id)
            && catalog.GetProcess(id) is { } aliased
            && ProcessExecutionEligibility.IsJobCallable(aliased) ? aliased : null;
    }

    public static EsriGpTaskSummary ToSummary(string taskName, ProcessDefinition definition) => new()
    {
        TaskName = taskName,
        ProcessId = definition.ProcessId,
        DisplayName = definition.Title,
        Category = definition.Category,
        IsAlias = !string.Equals(taskName, definition.ProcessId, StringComparison.Ordinal),
        SupportsSynchronousExecution = (definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0
    };

    public static EsriGpTaskDescription ToDescription(string taskName, ProcessDefinition definition) => new()
    {
        TaskName = taskName,
        ProcessId = definition.ProcessId,
        DisplayName = definition.Title,
        Description = definition.Description,
        Category = definition.Category,
        ExecutionType = (definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0
            ? "esriExecutionTypeSynchronous" : "esriExecutionTypeAsynchronous",
        SupportsSynchronousExecution = (definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0,
        Parameters = definition.Parameters.Select(parameter => new EsriGpParameterDescription
        {
            Name = parameter.Name,
            DisplayName = parameter.DisplayName,
            Description = parameter.Description,
            DataType = ToEsriDataType(parameter.ValueType),
            Direction = "esriGPParameterDirectionInput",
            ParameterType = parameter.Required ? "esriGPParameterTypeRequired" : "esriGPParameterTypeOptional",
            DefaultValue = parameter.DefaultValue,
            ChoiceList = parameter.AllowedValues
        }).ToArray()
    };

    public static AnalysisPlan BuildPlan(string serviceId, string taskName, ProcessDefinition definition, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new GeoprocessingValidationException("parameters must be an object.");
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject()) inputs[property.Name] = ToCanonicalValue(property.Name, property.Value, inputs);
        var slug = definition.ProcessId.Replace('.', '-');
        return new AnalysisPlan
        {
            PlanId = $"mcp-esri-gp-{serviceId}-{slug}-{Guid.NewGuid():N}",
            IntentId = $"mcp:esri-gp:{serviceId}:{taskName}",
            Steps = [new AnalysisPlanStep { StepId = $"gp-task-{slug}", Kind = AnalysisPlanStepKind.Geoprocess, ProcessId = definition.ProcessId, Inputs = inputs }],
            Outputs = definition.OutputArtifactKinds
        };
    }

    private static string ToCanonicalValue(string name, JsonElement value, Dictionary<string, string> inputs)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            if (features.GetArrayLength() != 1) throw new GeoprocessingValidationException($"Parameter '{name}' FeatureSet must contain exactly one feature.");
            var feature = features[0];
            if (!feature.TryGetProperty("geometry", out var geometry) || !geometry.TryGetProperty("rings", out var rings))
                throw new GeoprocessingValidationException($"Parameter '{name}' FeatureSet must contain a polygon geometry.");
            if (value.TryGetProperty("spatialReference", out var sr) && sr.TryGetProperty("wkid", out var wkid)) inputs["srid"] = wkid.GetInt32().ToString(CultureInfo.InvariantCulture);
            return Convert.ToBase64String(WritePolygonWkb(rings));
        }
        return value.GetRawText();
    }

    private static byte[] WritePolygonWkb(JsonElement rings)
    {
        var parsed = rings.EnumerateArray().Select(r => r.EnumerateArray().Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToArray()).ToArray();
        var length = 1 + 4 + 4 + parsed.Sum(r => 4 + r.Length * 16);
        var bytes = new byte[length]; var offset = 0; bytes[offset++] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), 3); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)parsed.Length); offset += 4;
        foreach (var ring in parsed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)ring.Length); offset += 4;
            foreach (var point in ring)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(point.X)); offset += 8;
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(point.Y)); offset += 8;
            }
        }
        return bytes;
    }

    private static string ToEsriDataType(ProcessParameterValueType type) => type switch
    {
        ProcessParameterValueType.WholeNumber or ProcessParameterValueType.Srid or ProcessParameterValueType.LayerId => "GPLong",
        ProcessParameterValueType.FloatingPoint => "GPDouble",
        ProcessParameterValueType.Flag => "GPBoolean",
        ProcessParameterValueType.Wkb or ProcessParameterValueType.WkbArray => "GPFeatureRecordSetLayer",
        _ => "GPString"
    };
}
