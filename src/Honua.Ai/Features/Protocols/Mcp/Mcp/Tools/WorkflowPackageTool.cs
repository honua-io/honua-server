// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>Built-in workflow graph authoring; publication and execution are separate governed operations.</summary>
internal sealed class WorkflowPackageTool : IMcpTool
{
    public const string ToolName = "honua_workflow_package";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["action","workspaceId","turnId"],"properties":{
          "action":{"type":"string","enum":["list","read","create"]},
          "workspaceId":{"type":"string","minLength":1,"maxLength":128},
          "turnId":{"type":"string","minLength":1,"maxLength":128},
          "packageId":{"type":"string"},"version":{"type":"integer","minimum":1},
          "name":{"type":"string","minLength":1,"maxLength":200},
          "graph":{"type":"object","required":["nodes"],"properties":{
            "schemaVersion":{"type":"string"},"nodes":{"type":"array","items":{"type":"object"}},
            "edges":{"type":"array","items":{"type":"object"}},"schedule":{"type":["object","null"]},
            "workerProfile":{"type":["string","null"]},"editorMetadata":{"type":"object"}}}
        }}
        """).RootElement.Clone();

    public McpToolDescriptor Describe() => new()
    {
        Name = Name, Title = "Author built-in workflow package",
        Description = "List visible workflow packages, read an immutable version, or create a new package plus validated immutable version. "
            + "Supply the authorized workspace and frozen turn; ownership comes from the server's verified workspace owner. "
            + "Each create is a new package: use the returned packageId/version/content hash in Studio process nodes. "
            + "Validation is not execution verification. This tool never publishes, schedules, enables, or runs a workflow.",
        InputSchema = Schema,
        Annotations = McpToolAnnotationSets.Write("Author built-in workflow package", destructive: false, idempotent: false),
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var input = McpToolHelpers.ParseArguments(arguments, WorkflowPackageToolJsonContext.Default.WorkflowPackageToolInput);
        if (string.IsNullOrWhiteSpace(input.WorkspaceId) || string.IsNullOrWhiteSpace(input.TurnId))
            throw new GeoprocessingValidationException("workspaceId and turnId are required.");
        var authoring = httpContext.RequestServices.GetService<IWorkflowPackageAuthoring>()
            ?? throw new GeoprocessingStoreUnavailableException("Built-in workflow package authoring is unavailable.");
        switch (input.Action)
        {
            case "list" when input.PackageId is null && input.Version is null && input.Name is null && input.Graph is null:
                return McpToolHelpers.SuccessResult(new WorkflowPackageToolOutput
                {
                    Packages = (await authoring.ListAsync(principal, input.WorkspaceId, input.TurnId, cancellationToken).ConfigureAwait(false)).ToArray(),
                }, WorkflowPackageToolJsonContext.Default.WorkflowPackageToolOutput);
            case "read" when !string.IsNullOrWhiteSpace(input.PackageId) && input.Version is > 0 && input.Name is null && input.Graph is null:
                return McpToolHelpers.SuccessResult(new WorkflowPackageToolOutput
                {
                    Version = await authoring.ReadAsync(principal, input.WorkspaceId, input.TurnId, input.PackageId,
                        input.Version.Value, cancellationToken).ConfigureAwait(false)
                        ?? throw new GeoprocessingNotFoundException("Workflow package version was not found."),
                }, WorkflowPackageToolJsonContext.Default.WorkflowPackageToolOutput);
            case "create" when input.Name is { Length: > 0 and <= 200 } && input.Graph is not null && input.PackageId is null && input.Version is null:
                return McpToolHelpers.SuccessResult(new WorkflowPackageToolOutput
                {
                    Version = await authoring.CreateAsync(principal, input.WorkspaceId, input.TurnId, input.Name,
                        input.Graph, cancellationToken).ConfigureAwait(false),
                }, WorkflowPackageToolJsonContext.Default.WorkflowPackageToolOutput);
            default:
                throw new GeoprocessingValidationException("Use list, read with packageId/version, or create with name/graph.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowPackageToolInput
{
    public string? Action { get; init; }
    public string? WorkspaceId { get; init; }
    public string? TurnId { get; init; }
    public string? PackageId { get; init; }
    public int? Version { get; init; }
    public string? Name { get; init; }
    public WorkflowGraph? Graph { get; init; }
}

internal sealed record WorkflowPackageToolOutput
{
    public WorkflowPackage[]? Packages { get; init; }
    public WorkflowPackageVersion? Version { get; init; }
    public string Qualification { get; init; } = "validated-not-execution-verified";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowPackageToolInput))]
[JsonSerializable(typeof(WorkflowPackageToolOutput))]
internal sealed partial class WorkflowPackageToolJsonContext : JsonSerializerContext;
