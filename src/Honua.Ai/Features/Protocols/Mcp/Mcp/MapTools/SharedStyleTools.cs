// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.MapTools;

internal sealed class SharedStyleGetTool : IMcpTool
{
    public const string ToolName = "honua_shared_style_get";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;
    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "Read frozen shared style",
        Description = "Read the actual canonical style bytes and authorized impact of this frozen shared-style workspace task. "
            + "Requires workspaceId/turnId and current key/tenant/resource authority; a prior map task cannot stand in for a style. "
            + "Shared catalog edits use honua_shared_style_propose and distinct human approval; map-local draft appearance remains realtime.",
        InputSchema = SharedStyleToolSchemas.ReadInput,
        OutputSchema = SharedStyleToolSchemas.ReadOutput,
        Annotations = McpToolAnnotationSets.ReadOnly("Read frozen shared style"),
    };
    public async Task<McpToolsCallResult> InvokeAsync(HttpContext context, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(context);
        var input = McpToolHelpers.ParseArguments(arguments, SharedStyleToolJsonContext.Default.SharedStyleReadInput);
        if (string.IsNullOrWhiteSpace(input.WorkspaceId) || string.IsNullOrWhiteSpace(input.TurnId))
            throw new GeoprocessingValidationException("workspaceId and turnId are required.");
        var editor = context.RequestServices.GetService<ISharedStyleAgentEditor>()
            ?? throw new GeoprocessingStoreUnavailableException("Shared style authoring is unavailable.");
        return McpToolHelpers.SuccessResult(await editor.ReadAsync(principal, input.WorkspaceId, input.TurnId, cancellationToken).ConfigureAwait(false),
            SharedStyleToolJsonContext.Default.SharedStyleAgentRead);
    }
}

internal sealed class SharedStyleProposeTool : IMcpTool
{
    public const string ToolName = "honua_shared_style_propose";
    public string Name => ToolName;
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;
    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Title = "Propose shared style replacement",
        Description = "Propose the complete canonical MapLibre style for the exact styleId/version/contentHash/impactHash captured by this shared-style turn. "
            + "Requires the ai.approval-workflows entitlement (Pro or Enterprise). Map-local draft appearance remains realtime in every edition. "
            + "The existing canonical operation returns a durable human approval proposal and operation receipt; the agent never approves or directly writes. "
            + "Approval rechecks the original key, roles, membership, tenant, frozen task, style revisions and entire authorized layer impact under the canonical mutation lock. "
            + "After a lost response, use honua_workspace_get_command with the same workspaceId and returned operationInstanceId, including from a later chat; receipt reconciliation never applies the style again or requests another approval. Repeating this exact original turn/payload also reconciles the same operation. "
            + "Read current bytes with honua_shared_style_get; validation alone does not verify rendering. Restore uses restoreFromProposalId from a successful retained canonical proposal, with a fresh frozen task and human approval; a version number alone cannot restore appearance.",
        InputSchema = SharedStyleToolSchemas.ProposeInput,
        OutputSchema = SharedStyleToolSchemas.ProposeOutput,
        Annotations = McpToolAnnotationSets.Write("Propose shared style replacement", destructive: true, idempotent: true),
    };
    public async Task<McpToolsCallResult> InvokeAsync(HttpContext context, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(context);
        var input = McpToolHelpers.ParseArguments(arguments, SharedStyleToolJsonContext.Default.SharedStyleProposeInput);
        if (string.IsNullOrWhiteSpace(input.WorkspaceId) || string.IsNullOrWhiteSpace(input.TurnId) || (input.Intent is null) == string.IsNullOrWhiteSpace(input.RestoreFromProposalId))
            throw new GeoprocessingValidationException("workspaceId, turnId and exactly one of intent or restoreFromProposalId are required.");
        var editor = context.RequestServices.GetService<ISharedStyleAgentEditor>()
            ?? throw new GeoprocessingStoreUnavailableException("Shared style authoring is unavailable.");
        var handle = input.Intent is { } intent
            ? await editor.ProposeAsync(principal, input.WorkspaceId, input.TurnId, intent, cancellationToken).ConfigureAwait(false)
            : await editor.ProposeRestoreAsync(principal, input.WorkspaceId, input.TurnId, input.RestoreFromProposalId!, cancellationToken).ConfigureAwait(false);
        return McpToolHelpers.SuccessResult(handle,
            SharedStyleToolJsonContext.Default.OperationHandle);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SharedStyleReadInput(string? WorkspaceId, string? TurnId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SharedStyleProposeInput(string? WorkspaceId, string? TurnId, SharedStyleEditIntent? Intent, string? RestoreFromProposalId);

internal static class SharedStyleToolSchemas
{
    internal static readonly JsonElement ReadInput = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["workspaceId","turnId"],"properties":{
          "workspaceId":{"type":"string","minLength":1,"maxLength":128},"turnId":{"type":"string","minLength":1,"maxLength":128}}}
        """).RootElement.Clone();
    internal static readonly JsonElement ProposeInput = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["workspaceId","turnId"],"oneOf":[{"required":["intent"],"not":{"required":["restoreFromProposalId"]}},{"required":["restoreFromProposalId"],"not":{"required":["intent"]}}],"properties":{
          "workspaceId":{"type":"string","minLength":1,"maxLength":128},"turnId":{"type":"string","minLength":1,"maxLength":128},
          "restoreFromProposalId":{"type":"string","minLength":1,"maxLength":128},"intent":{"type":"object","additionalProperties":false,"required":["styleId","styleVersion","contentHash","impactHash","style"],"properties":{
            "styleId":{"type":"string","minLength":1,"maxLength":256},"styleVersion":{"type":"integer","minimum":0},
            "contentHash":{"type":"string","pattern":"^[0-9a-f]{64}$"},"impactHash":{"type":"string","pattern":"^[0-9a-f]{64}$"},"style":{"type":"object"}}}}}
        """).RootElement.Clone();
    internal static readonly JsonElement ReadOutput = JsonDocument.Parse("""
        {"type":"object","required":["impact","style","qualification"],"properties":{
          "impact":{"type":"object"},"style":{"type":"object"},"qualification":{"type":"string"}}}
        """).RootElement.Clone();
    internal static readonly JsonElement ProposeOutput = JsonDocument.Parse("""
        {"type":"object","required":["operationId","operationInstanceId","status"],"properties":{
          "operationId":{"type":"string"},"operationInstanceId":{"type":"string"},"status":{"type":"string"},
          "proposalId":{"type":["string","null"]},"correlationId":{"type":["string","null"]},
          "auditId":{"type":["string","null"]},"reason":{"type":["string","null"]},"result":{"type":["object","null"]},
          "createdAt":{"type":"string","format":"date-time"},"updatedAt":{"type":"string","format":"date-time"}}}
        """).RootElement.Clone();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SharedStyleReadInput))]
[JsonSerializable(typeof(SharedStyleProposeInput))]
[JsonSerializable(typeof(SharedStyleAgentRead))]
[JsonSerializable(typeof(OperationHandle))]
internal sealed partial class SharedStyleToolJsonContext : JsonSerializerContext;
