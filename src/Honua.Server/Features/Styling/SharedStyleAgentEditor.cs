// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Styling;

internal sealed class SharedStyleAgentEditor(IOperationInvoker invoker, IHttpContextAccessor accessor,
    StyleEditImpactService impacts) : ISharedStyleAgentEditor
{
    public async Task<SharedStyleAgentRead> ReadAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        var turn = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(context, principal, workspaceId, turnId, cancellationToken).ConfigureAwait(false);
        var current = await impacts.ResolveAsync(turn.Task.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException("Shared style not found.");
        RequireTask(turn, current);
        if (!current.CompleteReadableScope) throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        using var document = JsonDocument.Parse(current.CanonicalStyleJson);
        return new(current.Impact, document.RootElement.Clone());
    }

    public Task<OperationHandle> ProposeAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        SharedStyleEditIntent intent, CancellationToken cancellationToken = default)
        => ProposeCoreAsync(principal, workspaceId, turnId, intent, null, cancellationToken);

    public async Task<OperationHandle> ProposeRestoreAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        string sourceProposalId, CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        var turn = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(context, principal, workspaceId, turnId, cancellationToken).ConfigureAwait(false);
        var intent = await SharedStyleHistory.LoadRestoreAsync(context, turn, sourceProposalId, cancellationToken).ConfigureAwait(false);
        return await ProposeCoreAsync(principal, workspaceId, turnId, intent, sourceProposalId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationHandle> ProposeCoreAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        SharedStyleEditIntent intent, string? restoreSourceProposalId, CancellationToken cancellationToken)
    {
        var context = accessor.HttpContext ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: true);
        var turn = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(context, principal, workspaceId, turnId, cancellationToken).ConfigureAwait(false);
        RequireIntent(turn, intent);
        var decision = await OperationAdminAuthorization.EvaluateAsync(context, context.User,
            OperationSideEffectClass.MutatesMetadata, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAuthorized) throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var actor = CanonicalSecurityActor.Resolve(context.User)!.ActorId;
        var json = JsonSerializer.Serialize(intent, SharedStyleGovernanceJsonContext.Default.SharedStyleEditIntent);
        var fingerprint = StyleEditHashes.Parts([actor, turn.Binding.TenantId, workspaceId, turnId,
            intent.StyleId, intent.StyleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            intent.ContentHash, intent.ImpactHash, StyleEditHashes.Content(intent.Style.GetRawText()), restoreSourceProposalId ?? ""]);
        var operationId = Honua.Core.Features.Operations.Services.OperationEnvelopeFactory.DeriveIdempotentInstanceId(
            SharedStyleEditOperation.OperationId, "shared-style:" + fingerprint, turn.Binding.TenantId);
        var prior = await context.RequestServices.GetRequiredService<IOperationInstanceStore>()
            .GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prior?.ProposalId is not null)
        {
            if (!prior.EvidenceRefs.Contains("idempotency-request-sha256:" + fingerprint, StringComparer.Ordinal))
                throw new GeoprocessingPreconditionFailedException("The original command fingerprint differs.");
            return await SharedStyleOperationStatus.ReadAsync(context, workspaceId, operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        }
        return await invoker.SubmitAsync(new()
        {
            OperationId = SharedStyleEditOperation.OperationId,
            Parameters = new Dictionary<string, string?> { ["workspaceId"] = workspaceId, ["turnId"] = turnId, ["intent"] = json, ["restoreSourceProposalId"] = restoreSourceProposalId },
        }, new()
        {
            PrincipalId = actor,
            TenantId = turn.Binding.TenantId,
            SchemaName = context.RequestServices.GetService<Honua.Core.Features.Infrastructure.Abstractions.ISchemaContext>()?.CurrentSchema,
            AuthorizationOutcome = decision.AuthorizationOutcome,
            RequiredApprovalPolicyRef = "agent-workspace.human-approval",
            IdempotencyKey = "shared-style:" + fingerprint,
            IdempotencyRequestHash = fingerprint,
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static void RequireIntent(SharedStyleTurnBinding turn, SharedStyleEditIntent intent)
    {
        if (intent.Style.ValueKind != JsonValueKind.Object || intent.StyleId != turn.Task.Id || intent.StyleVersion != turn.Task.Revision ||
            intent.ContentHash != turn.Task.ContentHash || intent.ImpactHash != turn.Task.ImpactHash)
            throw new GeoprocessingPreconditionFailedException("The proposed style must match the exact frozen shared-style task.");
    }

    internal static void RequireTask(SharedStyleTurnBinding turn, ResolvedStyleEdit current)
    {
        if (current.Impact.StyleId != turn.Task.Id || current.Impact.StyleVersion != turn.Task.Revision ||
            current.Impact.ContentHash != turn.Task.ContentHash || current.Impact.ImpactHash != turn.Task.ImpactHash)
            throw new GeoprocessingPreconditionFailedException("The shared style changed; review a fresh turn before continuing.");
    }
}

internal sealed record SharedStyleApprovalPayload
{
    public required string ActorId { get; init; }
    public required Guid KeyId { get; init; }
    public Honua.Infrastructure.Authentication.AgentCredentialSnapshot? Credential { get; init; }
    public required string[] Permissions { get; init; }
    public required JobSecurityContext AuthorSecurityContext { get; init; }
    public required string WorkspaceId { get; init; }
    public required string TurnId { get; init; }
    public required string OwnerActorId { get; init; }
    public required string TenantId { get; init; }
    public string? SchemaName { get; init; }
    public required string TargetId { get; init; }
    public required string ReleaseId { get; init; }
    public required StyleEditImpact Impact { get; init; }
    public required StyleEditStoreSnapshot StoreSnapshot { get; init; }
    public string? PrimaryResourceId { get; init; }
    public required SharedStyleCandidate Candidate { get; init; }
    public required string BeforeMapLibreJson { get; init; }
    public string? RestoreSourceProposalId { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SharedStyleEditIntent))]
[JsonSerializable(typeof(SharedStyleApprovalPayload))]
internal sealed partial class SharedStyleGovernanceJsonContext : JsonSerializerContext;
