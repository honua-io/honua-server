// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Tools.EsriGp;

internal sealed class EsriGpDescribeTaskInput { [JsonPropertyName("taskName")] public string TaskName { get; set; } = string.Empty; }
internal sealed class EsriGpExecuteTaskInput
{
    [JsonPropertyName("serviceId")] public string ServiceId { get; set; } = string.Empty;
    [JsonPropertyName("taskName")] public string TaskName { get; set; } = string.Empty;
    [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
    [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; set; }
}
internal sealed class EsriGpListTasksOutput { [JsonPropertyName("tasks")] public IReadOnlyList<EsriGpTaskSummary> Tasks { get; set; } = []; }
internal sealed class EsriGpTaskSummary
{
    [JsonPropertyName("taskName")] public string TaskName { get; set; } = string.Empty;
    [JsonPropertyName("processId")] public string ProcessId { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("isAlias")] public bool IsAlias { get; set; }
    [JsonPropertyName("supportsSynchronousExecution")] public bool SupportsSynchronousExecution { get; set; }
}
internal sealed class EsriGpTaskDescription
{
    [JsonPropertyName("taskName")] public string TaskName { get; set; } = string.Empty;
    [JsonPropertyName("processId")] public string ProcessId { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("executionType")] public string ExecutionType { get; set; } = string.Empty;
    [JsonPropertyName("supportsSynchronousExecution")] public bool SupportsSynchronousExecution { get; set; }
    [JsonPropertyName("parameters")] public IReadOnlyList<EsriGpParameterDescription> Parameters { get; set; } = [];
}
internal sealed class EsriGpParameterDescription
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("dataType")] public string DataType { get; set; } = string.Empty;
    [JsonPropertyName("direction")] public string Direction { get; set; } = string.Empty;
    [JsonPropertyName("parameterType")] public string ParameterType { get; set; } = string.Empty;
    [JsonPropertyName("defaultValue")] public string? DefaultValue { get; set; }
    [JsonPropertyName("choiceList")] public IReadOnlyList<string>? ChoiceList { get; set; }
}
internal sealed class EsriGpExecuteTaskOutput
{
    [JsonPropertyName("jobId")] public string JobId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("resourceUri")] public string ResourceUri { get; set; } = string.Empty;
    [JsonPropertyName("serviceId")] public string ServiceId { get; set; } = string.Empty;
    [JsonPropertyName("taskName")] public string TaskName { get; set; } = string.Empty;
    [JsonPropertyName("processId")] public string ProcessId { get; set; } = string.Empty;
}

internal static class EsriGpToolSchemas
{
    public static readonly JsonElement ListInput = Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""");
    public static readonly JsonElement DescribeInput = Parse("""{"type":"object","properties":{"taskName":{"type":"string"}},"required":["taskName"],"additionalProperties":false}""");
    public static readonly JsonElement ExecuteInput = Parse("""{"type":"object","properties":{"serviceId":{"type":"string"},"taskName":{"type":"string"},"parameters":{"type":"object","additionalProperties":true},"idempotencyKey":{"type":"string"}},"required":["serviceId","taskName","parameters"],"additionalProperties":false}""");
    public static readonly JsonElement ListOutput = Parse("""{"type":"object","required":["tasks"],"properties":{"tasks":{"type":"array","items":{"type":"object","required":["taskName","processId","displayName","category","isAlias","supportsSynchronousExecution"]}}}""");
    public static readonly JsonElement DescribeOutput = Parse("""{"type":"object","required":["taskName","processId","displayName","description","category","executionType","supportsSynchronousExecution","parameters"],"properties":{"taskName":{"type":"string"},"processId":{"type":"string"},"displayName":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"executionType":{"type":"string"},"supportsSynchronousExecution":{"type":"boolean"},"parameters":{"type":"array"}}}""");
    public static readonly JsonElement ExecuteOutput = Parse("""{"type":"object","required":["jobId","status","resourceUri","serviceId","taskName","processId"],"properties":{"jobId":{"type":"string"},"status":{"type":"string"},"resourceUri":{"type":"string"},"serviceId":{"type":"string"},"taskName":{"type":"string"},"processId":{"type":"string"}}}""");
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
}
