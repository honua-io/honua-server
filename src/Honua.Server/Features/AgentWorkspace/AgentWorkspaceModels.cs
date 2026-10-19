// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.AgentWorkspace.Domain;

namespace Honua.Server.Features.AgentWorkspace;

// This is shared workflow state, not an execution language. Mutations continue through
// the canonical operation dispatcher under the requesting principal's authorization.
internal sealed record AgentResourceReference(string Kind, string Id, string? ServiceId = null);
internal sealed record AgentTaskReference(string Id, long Revision, string Kind = "publish-layer", string? PackageKey = null, string? ContentHash = null, string? ImpactHash = null);
internal sealed record AgentWorkspaceContext(
    string WorkspaceId, string TargetId, string ViewId, long Revision,
    AgentResourceReference[] SelectedResources, AgentTaskReference? Task, string? TenantId = null);
internal sealed record AgentWorkspaceMessage(string Id, string TurnId, string Role, string Content, string EvidenceKind = "conversation");
internal sealed record AgentWorkspaceTurn(
    string TurnId, string ActorId, string Prompt, AgentWorkspaceContext Context, AgentWorkspaceDraft? Draft,
    LocalProcessSnapshot? LocalProcessSnapshot = null);
internal sealed record AgentWorkspaceDraft(
    string Id, long Revision, string Kind, Dictionary<string, string> Fields, string[] Validation);
internal sealed record AgentWorkspaceCommand(
    string CommandId, string ActorId, string DraftId, long DraftRevision, string IdempotencyKey,
    string OperationInstanceId, Dictionary<string, string> Fields, DateTimeOffset RetryBefore, string? OriginatingTurnId = null);
internal sealed record AgentWorkflowAuthoringCommand(
    string CommandId, string ActorId, string WorkspaceId, string TurnId, string RequestFingerprint,
    string PackageId, int ExpectedVersion, string PackageHash, string OwnerActorId, string TenantId,
    string TargetId, string ReleaseId, string? CapturedViewId = null, long? CapturedContextRevision = null,
    string AttachmentState = "Pending", long? AttachedRevision = null);
internal sealed record AgentWorkspaceDocument
{
    public required string WorkspaceId { get; init; }
    public required string TargetId { get; init; }
    public required string OwnerId { get; init; }
    public string? AgentActorId { get; init; }
    public string? TenantId { get; init; }
    public required string[] Members { get; init; }
    public long Version { get; init; }
    public Dictionary<string, AgentWorkspaceContext[]> Views { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkspaceDraft> Drafts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkspaceDraftLineage> DraftLineages { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkspaceTurn> Turns { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkspaceCommand> Commands { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AgentWorkflowAuthoringCommand> WorkflowAuthoringCommands { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, DatasetImportAuthorityRenewal> DatasetImportAuthorityRenewals { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, DatasetImportPreparedAdmission> DatasetImportPreparedAdmissions { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, DatasetImportExecutionBinding> DatasetImportExecutionBindings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, DatasetImportTask[]> DatasetImportTasks { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, DatasetImportTaskIntent> DatasetImportTaskIntents { get; init; } = new(StringComparer.Ordinal);
    public AgentWorkspaceMessage[] Messages { get; init; } = [];
    public AgentTaskReference? Task { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateDatasetImportTask(
    [property: JsonRequired] string IntentId, [property: JsonRequired] string ViewId, [property: JsonRequired] long ContextRevision,
    [property: JsonRequired] DatasetImportDestination Destination, [property: JsonRequired] DatasetImportSource Source,
    [property: JsonRequired] DatasetImportTaskOptions Options);
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
internal sealed record AgentDevOpsAuthorizationResponse(
    string TargetId, string DeployTargetId, string Environment, string RuntimeKind, string Backend,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TenantId)
{
    public required string TargetName { get; init; }
    public bool UsesCustomProviderEndpoint { get; init; }
    public required Dictionary<string, string> ResourceParameters { get; init; }
}
internal sealed record AgentConversationResponse(AgentWorkspaceMessage[] Messages);
internal sealed record AgentTurnResponse(string TurnId, string Status, AgentWorkspaceContext Context, AgentWorkspaceMessage[] Messages,
    AgentWorkspaceDraft? Draft, LocalProcessSnapshot? LocalProcessSnapshot = null, DatasetImportTask? DatasetImportContext = null);
internal sealed record AgentWorkspaceError(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentWorkspaceDocument))]
[JsonSerializable(typeof(AgentWorkspaceTurn))]
[JsonSerializable(typeof(Honua.Core.Features.Authorization.Domain.AgentCredentialSnapshot))]
[JsonSerializable(typeof(DatasetImportTask))]
[JsonSerializable(typeof(CreateDatasetImportUpload))]
[JsonSerializable(typeof(MutateDatasetImportUpload))]
[JsonSerializable(typeof(DatasetImportUploadReceipt))]
[JsonSerializable(typeof(DatasetImportUploadCapabilities))]
[JsonSerializable(typeof(CreateDatasetImportTask))]
[JsonSerializable(typeof(DatasetImportAuthorityDescriptor))]
[JsonSerializable(typeof(RenewDatasetImportAuthority))]
[JsonSerializable(typeof(ReadDatasetImportAuthority))]
[JsonSerializable(typeof(RenewDatasetImportAuthorityCall))]
[JsonSerializable(typeof(DatasetImportAuthorityRenewal))]
[JsonSerializable(typeof(string[]))]
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
