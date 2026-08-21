// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal abstract class EsriGpProfileToolBase : IMcpTool, IMcpProfileTool
{
    protected EsriGpProfileToolBase(
        IGeoprocessingJobService jobService,
        IProcessCatalog processCatalog,
        ILogger logger)
    {
        JobService = jobService;
        ProcessCatalog = processCatalog;
        Logger = logger;
    }

    protected IGeoprocessingJobService JobService { get; }

    protected IProcessCatalog ProcessCatalog { get; }

    protected ILogger Logger { get; }

    public abstract string Name { get; }

    public string ProfileName => "esri-gp";

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    protected abstract string Title { get; }

    protected abstract string Description { get; }

    protected abstract JsonElement InputSchema { get; }

    protected abstract JsonElement OutputSchema { get; }

    protected virtual McpToolAnnotations Annotations => McpToolAnnotationSets.ReadOnly(Title);

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = Title,
        Description = Description,
        InputSchema = InputSchema,
        OutputSchema = OutputSchema,
        Annotations = Annotations
    };

    public abstract Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken);

    protected async Task<ClaimsPrincipal> AuthorizeAsync(
        HttpContext httpContext,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity(Name);
        McpLog.ToolInvoked(Logger, Name, WorkflowFamily);
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await JobService.EnsureCallerAuthorizedAsync(
            principal, resourceType, operation, cancellationToken).ConfigureAwait(false);
        return principal;
    }

    protected static JsonElement RequireObject(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
        {
            throw new GeoprocessingValidationException("Tool arguments must be a JSON object.");
        }
        return value;
    }

    protected static string RequireString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new GeoprocessingValidationException($"'{propertyName}' must be a non-empty string.");
        }
        return property.GetString()!;
    }

    protected static McpToolsCallResult Success(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return McpToolHelpers.SuccessJsonElement(document.RootElement);
    }
}

internal sealed class ListEsriGpTasksTool(
    IGeoprocessingJobService jobs,
    IProcessCatalog catalog,
    ILogger<ListEsriGpTasksTool> logger)
    : EsriGpProfileToolBase(jobs, catalog, logger)
{
    public const string ToolName = "honua_esri_gp_list_tasks";
    public override string Name => ToolName;
    protected override string Title => "List Esri GP tasks";
    protected override string Description =>
        "List Honua GPServer task names, including Esri-conventional aliases and their canonical process ids. "
        + "This discovers Honua's local GPServer projection; it does not federate an external ArcGIS Server.";
    protected override JsonElement InputSchema => EsriGpToolSchemas.ListInput;
    protected override JsonElement OutputSchema => EsriGpToolSchemas.ListOutput;

    public override async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        _ = arguments;
        await AuthorizeAsync(
            httpContext, OperatorResourceType.Catalog, OperatorOperation.Discover, cancellationToken)
            .ConfigureAwait(false);

        var tasks = new JsonArray();
        foreach (var task in EsriGpTaskProjection.ListTasks(ProcessCatalog))
        {
            tasks.Add(new JsonObject
            {
                ["taskName"] = task.TaskName,
                ["processId"] = task.ProcessId,
                ["displayName"] = task.DisplayName,
                ["category"] = task.Category,
                ["isAlias"] = task.IsAlias,
                ["supportsSynchronousExecution"] = task.SupportsSynchronousExecution
            });
        }

        return Success(new JsonObject { ["tasks"] = tasks });
    }
}

internal sealed class DescribeEsriGpTaskTool(
    IGeoprocessingJobService jobs,
    IProcessCatalog catalog,
    ILogger<DescribeEsriGpTaskTool> logger)
    : EsriGpProfileToolBase(jobs, catalog, logger)
{
    public const string ToolName = "honua_esri_gp_describe_task";
    public override string Name => ToolName;
    protected override string Title => "Describe Esri GP task";
    protected override string Description =>
        "Describe a GPServer task or Esri alias using the same catalog-derived parameter schema GPServer publishes.";
    protected override JsonElement InputSchema => EsriGpToolSchemas.DescribeInput;
    protected override JsonElement OutputSchema => EsriGpToolSchemas.DescribeOutput;

    public override async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(
            httpContext, OperatorResourceType.Catalog, OperatorOperation.Discover, cancellationToken)
            .ConfigureAwait(false);
        var argument = RequireObject(arguments);
        var taskName = RequireString(argument, "taskName");
        var task = EsriGpTaskProjection.DescribeTask(ProcessCatalog, taskName)
            ?? throw new GeoprocessingNotFoundException($"GPServer task '{taskName}' was not found.");

        var parameters = new JsonArray();
        foreach (var parameter in task.Parameters)
        {
            parameters.Add(new JsonObject
            {
                ["name"] = parameter.Name,
                ["displayName"] = parameter.DisplayName,
                ["description"] = parameter.Description,
                ["dataType"] = parameter.DataType,
                ["direction"] = parameter.Direction,
                ["parameterType"] = parameter.ParameterType,
                ["defaultValue"] = parameter.DefaultValue,
                ["choiceList"] = parameter.AllowedValues == null
                    ? null
                    : new JsonArray(parameter.AllowedValues.Select(value => (JsonNode?)value).ToArray())
            });
        }

        return Success(new JsonObject
        {
            ["taskName"] = task.TaskName,
            ["processId"] = task.ProcessId,
            ["displayName"] = task.DisplayName,
            ["description"] = task.Description,
            ["category"] = task.Category,
            ["executionType"] = task.ExecutionType,
            ["supportsSynchronousExecution"] = task.SupportsSynchronousExecution,
            ["parameters"] = parameters
        });
    }
}

internal sealed class ExecuteEsriGpTaskTool(
    IGeoprocessingJobService jobs,
    IProcessCatalog catalog,
    ILogger<ExecuteEsriGpTaskTool> logger)
    : EsriGpProfileToolBase(jobs, catalog, logger)
{
    public const string ToolName = "honua_esri_gp_execute_task";
    public override string Name => ToolName;
    protected override string Title => "Execute Esri GP task";
    protected override string Description =>
        "Submit a GPServer task or Esri alias through Honua's canonical geoprocessing job service and return a pollable job handle. "
        + "Parameters use the schema returned by honua_esri_gp_describe_task. Tasks that mutate data retain the same approval gate as GPServer.";
    protected override JsonElement InputSchema => EsriGpToolSchemas.ExecuteInput;
    protected override JsonElement OutputSchema => EsriGpToolSchemas.ExecuteOutput;
    protected override McpToolAnnotations Annotations =>
        McpToolAnnotationSets.Write(Title, destructive: true, idempotent: false);

    public override async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var principal = await AuthorizeAsync(
            httpContext, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken)
            .ConfigureAwait(false);
        var argument = RequireObject(arguments);
        var serviceId = RequireString(argument, "serviceId");
        var taskName = RequireString(argument, "taskName");
        var definition = EsriGpTaskProjection.ResolveTask(ProcessCatalog, taskName)
            ?? throw new GeoprocessingNotFoundException($"GPServer task '{taskName}' was not found.");
        var parameters = ReadParameters(argument);
        var translated = EsriGpInputTranslation.Translate(parameters);
        if (translated.CapabilityMessage != null)
        {
            throw new GeoprocessingValidationException(translated.CapabilityMessage);
        }
        var plan = EsriGpTaskProjection.BuildSubmissionPlan(definition, serviceId, translated.Inputs);
        var metadata = EsriGpTaskProjection.BuildProtocolMetadata(serviceId, taskName, definition);
        metadata["submittedThrough"] = "MCP-esri-gp-profile";
        var idempotencyKey = argument.TryGetProperty("idempotencyKey", out var key)
            && key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;
        var job = await JobService.SubmitJobAsync(
            plan, idempotencyKey, principal, metadata, cancellationToken).ConfigureAwait(false);

        return Success(new JsonObject
        {
            ["jobId"] = job.OperationId,
            ["status"] = job.Status.ToString().ToLowerInvariant(),
            ["resourceUri"] = $"honua://jobs/{job.OperationId}",
            ["serviceId"] = serviceId,
            ["taskName"] = taskName,
            ["processId"] = definition.ProcessId
        });
    }

    private static Dictionary<string, string> ReadParameters(JsonElement argument)
    {
        if (!argument.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            throw new GeoprocessingValidationException("'parameters' must be a JSON object.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters.EnumerateObject())
        {
            result[parameter.Name] = parameter.Value.ValueKind switch
            {
                JsonValueKind.String => parameter.Value.GetString() ?? string.Empty,
                JsonValueKind.Number when parameter.Value.TryGetInt64(out var whole) =>
                    whole.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.Number => parameter.Value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => parameter.Value.GetRawText()
            };
        }
        return result;
    }
}

internal static class EsriGpToolSchemas
{
    public static readonly JsonElement ListInput = Parse("""
        {"type":"object","additionalProperties":false}
        """);

    public static readonly JsonElement ListOutput = Parse("""
        {"type":"object","additionalProperties":false,"required":["tasks"],"properties":{"tasks":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["taskName","processId","displayName","category","isAlias","supportsSynchronousExecution"],"properties":{"taskName":{"type":"string"},"processId":{"type":"string"},"displayName":{"type":"string"},"category":{"type":"string"},"isAlias":{"type":"boolean"},"supportsSynchronousExecution":{"type":"boolean"}}}}}}
        """);

    public static readonly JsonElement DescribeInput = Parse("""
        {"type":"object","additionalProperties":false,"required":["taskName"],"properties":{"taskName":{"type":"string","minLength":1}}}
        """);

    public static readonly JsonElement DescribeOutput = Parse("""
        {"type":"object","additionalProperties":false,"required":["taskName","processId","displayName","description","category","executionType","supportsSynchronousExecution","parameters"],"properties":{"taskName":{"type":"string"},"processId":{"type":"string"},"displayName":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"executionType":{"type":"string"},"supportsSynchronousExecution":{"type":"boolean"},"parameters":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["name","displayName","description","dataType","direction","parameterType","defaultValue","choiceList"],"properties":{"name":{"type":"string"},"displayName":{"type":"string"},"description":{"type":"string"},"dataType":{"type":"string"},"direction":{"type":"string"},"parameterType":{"type":"string"},"defaultValue":{"type":["string","null"]},"choiceList":{"type":["array","null"],"items":{"type":"string"}}}}}}}
        """);

    public static readonly JsonElement ExecuteInput = Parse("""
        {"type":"object","additionalProperties":false,"required":["serviceId","taskName","parameters"],"properties":{"serviceId":{"type":"string","minLength":1},"taskName":{"type":"string","minLength":1},"parameters":{"type":"object","additionalProperties":true},"idempotencyKey":{"type":"string","minLength":1}}}
        """);

    public static readonly JsonElement ExecuteOutput = Parse("""
        {"type":"object","additionalProperties":false,"required":["jobId","status","resourceUri","serviceId","taskName","processId"],"properties":{"jobId":{"type":"string"},"status":{"type":"string"},"resourceUri":{"type":"string"},"serviceId":{"type":"string"},"taskName":{"type":"string"},"processId":{"type":"string"}}}
        """);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
