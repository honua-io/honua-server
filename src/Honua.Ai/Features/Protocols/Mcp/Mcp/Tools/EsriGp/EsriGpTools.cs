// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;

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
    IProcessCatalog processCatalog,
    IEsriGeoprocessingInputTranslator inputTranslator,
    ILogger<EsriGpExecuteTaskTool> logger) : IMcpTool
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
        McpTelemetry.EnrichActivity("EsriGpExecuteTask");
        McpLog.ToolInvoked(logger, Name, WorkflowFamily);
        var principal = McpAuthorizationHelper.EnsurePrincipal(context);
        await jobService.EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken).ConfigureAwait(false);
        ExecutePlanTool.EnforceExecutionPolicy(context, Name, "honua.mcp.esri-gp");
        var input = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.EsriGpExecuteTaskInput);
        System.Diagnostics.Activity.Current?.SetTag("honua.service.id", input.ServiceId);
        System.Diagnostics.Activity.Current?.SetTag("honua.gp.task.name", input.TaskName);
        McpLog.EsriGpTaskInvoked(logger, input.ServiceId, input.TaskName);
        await ValidateServiceAccessAsync(context, input.ServiceId, cancellationToken).ConfigureAwait(false);
        var definition = EsriGpProjection.Resolve(processCatalog, input.TaskName)
            ?? throw new GeoprocessingNotFoundException($"Esri GP task '{input.TaskName}' was not found.");
        var rawInputs = input.Parameters.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
        var translated = inputTranslator.Translate(rawInputs);
        if (translated.CapabilityMessage is not null)
        {
            throw new GeoprocessingValidationException(translated.CapabilityMessage);
        }

        var plan = EsriGpProjection.BuildPlan(input.ServiceId, input.TaskName, definition, translated.Inputs, input.IdempotencyKey);
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

    private static async Task ValidateServiceAccessAsync(HttpContext context, string serviceId, CancellationToken cancellationToken)
    {
        var validator = context.RequestServices.GetService<IResourceValidator>()
            ?? throw new GeoprocessingStoreUnavailableException("The service catalog is not available on this server.");
        var result = await validator.ValidateServiceV2Async(serviceId, "GPServer", cancellationToken).ConfigureAwait(false);
        if (!result.IsValid || result.Resource is null)
        {
            throw new GeoprocessingNotFoundException(result.ErrorMessage ?? $"GPServer service '{serviceId}' was not found.");
        }

        if (await AccessPolicyHelpers.RequireServiceAccessAsync(
                context, result.Resource, AuthorizationOperation.Query, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new GeoprocessingAuthorizationException(false);
        }
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
        }).Concat(definition.OutputArtifactKinds.Select((kind, index) => new EsriGpParameterDescription
        {
            Name = BuildOutputParameterName(kind, index, definition.OutputArtifactKinds),
            DisplayName = BuildOutputParameterName(kind, index, definition.OutputArtifactKinds),
            Description = $"Output artifact of type {kind}.",
            DataType = ToEsriDataType(kind),
            Direction = "esriGPParameterDirectionOutput",
            ParameterType = definition.OutputsAreAlternatives ? "esriGPParameterTypeOptional" : "esriGPParameterTypeRequired"
        })).ToArray()
    };

    public static AnalysisPlan BuildPlan(string serviceId, string taskName, ProcessDefinition definition, Dictionary<string, string> inputs, string? idempotencyKey)
    {
        var slug = definition.ProcessId.Replace('.', '-');
        return new AnalysisPlan
        {
            PlanId = $"mcp-esri-gp-{serviceId}-{slug}-{StableRequestId(idempotencyKey, inputs)}",
            IntentId = $"mcp:esri-gp:{serviceId}:{taskName}",
            Steps = [new AnalysisPlanStep { StepId = $"gp-task-{slug}", Kind = AnalysisPlanStepKind.Geoprocess, ProcessId = definition.ProcessId, Inputs = inputs }],
            Outputs = definition.OutputArtifactKinds
        };
    }

    private static string StableRequestId(string? idempotencyKey, IReadOnlyDictionary<string, string> inputs)
    {
        var identity = string.IsNullOrWhiteSpace(idempotencyKey)
            ? string.Join('\n', inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))
            : idempotencyKey;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
    }

    private static string ToEsriDataType(ProcessParameterValueType type) => type switch
    {
        ProcessParameterValueType.WholeNumber or ProcessParameterValueType.Srid or ProcessParameterValueType.LayerId => "GPLong",
        ProcessParameterValueType.FloatingPoint => "GPDouble",
        ProcessParameterValueType.Flag => "GPBoolean",
        ProcessParameterValueType.Wkb or ProcessParameterValueType.WkbArray => "GPFeatureRecordSetLayer",
        _ => "GPString"
    };

    private static string ToEsriDataType(ArtifactKind kind) => kind switch
    {
        ArtifactKind.FeatureLayer => "GPFeatureRecordSetLayer",
        ArtifactKind.Table => "GPRecordSet",
        ArtifactKind.Raster => "GPRasterDataLayer",
        ArtifactKind.File or ArtifactKind.Report or ArtifactKind.Map or ArtifactKind.AppBundle => "GPDataFile",
        _ => "GPString"
    };

    private static string BuildOutputParameterName(ArtifactKind kind, int index, IReadOnlyList<ArtifactKind> allKinds)
    {
        var baseName = kind switch
        {
            ArtifactKind.FeatureLayer => "outputFeatureLayer",
            ArtifactKind.Table => "outputTable",
            ArtifactKind.Raster => "outputRaster",
            ArtifactKind.File => "outputFile",
            ArtifactKind.Report => "outputReport",
            ArtifactKind.Map => "outputMap",
            ArtifactKind.Scalar => "outputScalar",
            ArtifactKind.AppBundle => "outputBundle",
            _ => "output"
        };
        return allKinds.Count(candidate => candidate == kind) <= 1
            ? baseName
            : $"{baseName}{allKinds.Take(index + 1).Count(candidate => candidate == kind)}";
    }
}
