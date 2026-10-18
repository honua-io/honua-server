// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.AgentWorkspace;

// This is shared workflow state, not an execution language. Mutations continue through
// the canonical operation dispatcher under the requesting principal's authorization.
internal sealed record AgentResourceReference(string Kind, string Id, string? ServiceId = null);
internal sealed record AgentTaskReference(string Id, long Revision, string Kind = "publish-layer", string? PackageKey = null, string? ContentHash = null);
internal sealed record AgentWorkspaceContext(
    string WorkspaceId, string TargetId, string ViewId, long Revision,
    AgentResourceReference[] SelectedResources, AgentTaskReference? Task, string? TenantId = null);
internal sealed record AgentWorkspaceMessage(string Id, string TurnId, string Role, string Content, string EvidenceKind = "conversation");
internal sealed record AgentWorkspaceTurn(
    string TurnId, string ActorId, string Prompt, AgentWorkspaceContext Context, AgentWorkspaceDraft? Draft);
internal sealed record AgentWorkspaceDraft(
    string Id, long Revision, string Kind, Dictionary<string, string> Fields, string[] Validation);
internal sealed record AgentWorkspaceCommand(
    string CommandId, string ActorId, string DraftId, long DraftRevision, string IdempotencyKey,
    string OperationInstanceId, Dictionary<string, string> Fields, DateTimeOffset RetryBefore);
internal sealed record AgentWorkspaceDocument
{
    public required string WorkspaceId { get; init; }
    public required string TargetId { get; init; }
    public required string OwnerId { get; init; }
    public string? TenantId { get; init; }
    public required string[] Members { get; init; }
    public long Version { get; init; }
    public Dictionary<string, AgentWorkspaceContext[]> Views { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkspaceDraft> Drafts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkspaceTurn> Turns { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkspaceCommand> Commands { get; init; } = new(StringComparer.Ordinal);
    public AgentWorkspaceMessage[] Messages { get; init; } = [];
    public AgentTaskReference? Task { get; init; }
}
internal sealed record CreateAgentWorkspace([property: JsonRequired] string ViewId, string? TargetId = null);
internal sealed record UpdateAgentContext(
    [property: JsonRequired] string ViewId, [property: JsonRequired] long ExpectedRevision, [property: JsonRequired] string TargetId,
    [property: JsonRequired] AgentResourceReference[] SelectedResources, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] AgentTaskReference? Task, string? Source);
internal sealed record CreateAgentTurn([property: JsonRequired] string TurnId, [property: JsonRequired] string ViewId, [property: JsonRequired] long ContextRevision, [property: JsonRequired] string Prompt);
internal sealed record CreateAgentDraft([property: JsonRequired] string Kind, [property: JsonRequired] Dictionary<string, string> Fields, [property: JsonRequired] string ViewId, [property: JsonRequired] long ContextRevision);
internal sealed record PatchAgentDraft([property: JsonRequired] long ExpectedRevision, [property: JsonRequired] string ViewId, [property: JsonRequired] Dictionary<string, string> Changes);
internal sealed record SubmitAgentDraft([property: JsonRequired] string CommandId, [property: JsonRequired] long ExpectedRevision, [property: JsonRequired] string ViewId);
internal sealed record AppendAgentMessage([property: JsonRequired] string MessageId, [property: JsonRequired] string TurnId, [property: JsonRequired] string Content);
internal sealed record AgentTargetResponse(string TargetId);
internal sealed record AgentDevOpsAuthorizationRequest(
    string TargetId, string DeployTargetId, string Environment, string? CanonicalTool,
    Dictionary<string, System.Text.Json.JsonElement>? Arguments = null);
internal sealed record AgentDevOpsAuthorizationResponse(string TargetId, string DeployTargetId, string Environment);
internal sealed record AgentConversationResponse(AgentWorkspaceMessage[] Messages);
internal sealed record AgentTurnResponse(string TurnId, string Status, AgentWorkspaceContext Context, AgentWorkspaceMessage[] Messages, AgentWorkspaceDraft? Draft);
internal sealed record AgentWorkspaceError(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentWorkspaceDocument))]
[JsonSerializable(typeof(AgentWorkspaceContext))]
[JsonSerializable(typeof(AgentWorkspaceDraft))]
[JsonSerializable(typeof(CreateAgentWorkspace))]
[JsonSerializable(typeof(UpdateAgentContext))]
[JsonSerializable(typeof(CreateAgentTurn))]
[JsonSerializable(typeof(CreateAgentDraft))]
[JsonSerializable(typeof(PatchAgentDraft))]
[JsonSerializable(typeof(SubmitAgentDraft))]
[JsonSerializable(typeof(AgentTargetResponse))]
[JsonSerializable(typeof(AppendAgentMessage))]
[JsonSerializable(typeof(AgentWorkspaceMessage))]
[JsonSerializable(typeof(AgentDevOpsAuthorizationRequest))]
[JsonSerializable(typeof(AgentDevOpsAuthorizationResponse))]
[JsonSerializable(typeof(AgentTurnResponse))]
[JsonSerializable(typeof(AgentConversationResponse))]
[JsonSerializable(typeof(AgentWorkspaceError))]
internal sealed partial class AgentWorkspaceJsonContext : JsonSerializerContext;
