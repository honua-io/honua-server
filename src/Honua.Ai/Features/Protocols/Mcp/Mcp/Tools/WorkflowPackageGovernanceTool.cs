// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>Thin workflow publication proposal adapter; never supplies an approval identity or credential.</summary>
internal sealed class WorkflowPackageGovernanceTool : IMcpTool
{
    public const string ToolName = "honua_workflow_package_propose";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["workspaceId","turnId","intent"],"properties":{
          "workspaceId":{"type":"string","minLength":1,"maxLength":128},"turnId":{"type":"string","minLength":1,"maxLength":128},
          "intent":{"type":"object","additionalProperties":false,"required":["action","packageId","version","packageHash","publicationId"],"properties":{
            "action":{"type":"string","enum":["publish","delete","set-status"]},"packageId":{"type":"string"},
            "version":{"type":"integer","minimum":1},"packageHash":{"type":"string"},"publicationId":{"type":"string"},
            "target":{"type":"string","enum":["Job","Schedule","ProcessEndpoint"]},"processId":{"type":["string","null"]},
            "schedule":{"type":["object","null"]},"enabled":{"type":"boolean"},
            "status":{"type":"string","enum":["Active","Disabled"]}}}}}
        """).RootElement.Clone();
    public McpToolDescriptor Describe() => new()
    {
        Name = Name, Title = "Propose workflow publication transition",
        Description = "Propose publish, delete or set-status for an exact immutable workflow package/version/hash and publication. "
            + "Requires an authorized workspace/frozen turn. The canonical operation returns a durable proposal for a distinct human to approve; "
            + "the agent cannot approve it. Poll the returned canonical proposal and operation identities. Draft graph edits use honua_workflow_package.",
        InputSchema = Schema, OutputSchema = WorkflowPackageToolSchemas.GovernanceOutput,
        Annotations = McpToolAnnotationSets.Write("Propose workflow publication transition", destructive: true, idempotent: true),
    };
    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        var input = McpToolHelpers.ParseArguments(arguments, WorkflowPackageGovernanceToolJsonContext.Default.WorkflowPackageGovernanceToolInput);
        if (string.IsNullOrWhiteSpace(input.WorkspaceId) || string.IsNullOrWhiteSpace(input.TurnId) || input.Intent is null)
            throw new GeoprocessingValidationException("workspaceId, turnId and intent are required.");
        var governance = httpContext.RequestServices.GetService<IWorkflowPackageGovernance>()
            ?? throw new GeoprocessingStoreUnavailableException("Workflow publication governance is unavailable.");
        var handle = await governance.ProposeAsync(principal, input.WorkspaceId, input.TurnId, input.Intent, cancellationToken).ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(handle, WorkflowPackageGovernanceToolJsonContext.Default.OperationHandle);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowPackageGovernanceToolInput
{
    public string? WorkspaceId { get; init; }
    public string? TurnId { get; init; }
    public WorkflowPackageGovernanceIntent? Intent { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowPackageGovernanceToolInput))]
[JsonSerializable(typeof(OperationHandle))]
internal sealed partial class WorkflowPackageGovernanceToolJsonContext : JsonSerializerContext;
