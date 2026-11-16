// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using OperatorAccessDecision = Honua.Core.Features.Security.Abstractions.AccessDecision;

namespace Honua.Server.Features.Admin;

internal static class FolderMutationService
{
    internal static async Task<FolderExecutionResult> ExecuteAsync(
        FolderMutationExecution execution, CancellationToken cancellationToken = default)
    {
        var services = execution.Services;
        var tenant = services.GetService<ITenantContext>()?.TenantId;
        var folderTenant = execution.Namespace is null ? null : MetadataV2ServicePaths.NormalizeTenant(tenant);
        if (!LayerPublicationNamespace.IsValid(execution.Namespace) || (execution.Namespace is not null && folderTenant is null))
            return new(null, null, new FolderExecutionFailure.Mutation(new(400, "Invalid namespace or unresolved tenant.")));
        var principal = execution.Principal;
        FolderReplayAuthority? replay = null;
        if (execution.Replay is not null || execution.OperationReplay is not null ||
            principal.IsInRole(AdminApiKeyPermission.ApprovedOperationRole))
        {
            replay = await AuthorizeReplayAsync(execution, tenant, cancellationToken).ConfigureAwait(false);
            if (replay is null) return new(null, null, new FolderExecutionFailure.Replay());
        }
        var operatorPrincipal = replay?.OperatorPrincipal ?? principal;
        var gate = services.GetRequiredService<OperatorApprovalGate>();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog, Operation = OperatorOperation.Publish
        };
        var access = await gate.CheckAuthorizationAsync(operatorPrincipal, request, cancellationToken).ConfigureAwait(false);
        if (!access.IsAllowed) return new(null, null, new FolderExecutionFailure.Authorization(access));
        var approval = gate.CheckApproval(operatorPrincipal, request);
        if (approval.IsRequired) return new(null, null, new FolderExecutionFailure.Approval(approval));
        if (string.IsNullOrWhiteSpace(execution.ExpectedEtag))
            return new(null, null, new FolderExecutionFailure.Mutation(FolderMutationFailure.MissingPrecondition));
        var store = services.GetService<IMetadataV2GraphStore>();
        if (store is null) return new(null, null, new FolderExecutionFailure.Mutation(FolderMutationFailure.ReadOnlyCatalog));
        try
        {
            if (replay is not null && !await FolderApprovalAuthorization.IsCurrentAsync(replay.Proposal, services, cancellationToken).ConfigureAwait(false))
                return new(null, null, new FolderExecutionFailure.Replay());
            var snapshot = await store.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Etag != execution.ExpectedEtag)
                return new(null, null, new FolderExecutionFailure.Mutation(FolderMutationFailure.SnapshotChanged));
            var mutation = execution.Command.Apply(snapshot.Graph, folderTenant);
            var authorization = await FolderServiceAuthorization.EvaluateAsync(new FolderAuthorizationContext
            {
                Original = snapshot.Graph, Mutation = mutation, Services = services,
                OperatorPrincipal = operatorPrincipal, FolderTenant = folderTenant,
                RequesterAccess = new ServiceAccessContext
                {
                    Services = services, Principal = replay?.RequesterPrincipal ?? principal,
                    TenantId = tenant, Operation = AuthorizationOperation.Admin,
                    RequestCache = replay is null ? execution.RequestCache : null
                }
            }, cancellationToken).ConfigureAwait(false);
            if (authorization.Failure is not null)
                return new(null, mutation, new FolderExecutionFailure.AffectedServices(authorization));
            var saved = await store.SaveAsync(mutation.Graph, snapshot.Etag, cancellationToken).ConfigureAwait(false);
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("FolderMutations")
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            FolderAuditLog.Mutated(logger, execution.Command.OperationId, saved.Graph.Revision);
            return new(saved, mutation, null, authorization);
        }
        catch (Exception exception) when (FolderMutationFailure.Handles(exception))
        {
            return new(null, null, new FolderExecutionFailure.Mutation(FolderMutationFailure.From(exception)));
        }
    }
    private static async Task<FolderReplayAuthority?> AuthorizeReplayAsync(
        FolderMutationExecution execution, string? tenant, CancellationToken cancellationToken)
    {
        FolderReplayAuthority? replay;
        if (execution.OperationReplay is { } operation)
        {
            if (execution.Replay is not null || operation.Request.DryRun ||
                operation.Request.OperationId != execution.Command.OperationId ||
                !MetadataV2ServicePaths.SameTenant(operation.Context.TenantId, tenant) ||
                operation.Context.SchemaName != execution.Services.GetService<ISchemaContext>()?.CurrentSchema)
                return null;
            replay = await FolderReplayAuthority.AuthorizeOperationAsync(operation, execution.Services, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (execution.Replay is not { } admission || !ReferenceEquals(execution.Principal, admission.Principal) ||
                admission.OperationId != execution.Command.OperationId || !MetadataV2ServicePaths.SameTenant(admission.TenantId, tenant))
                return null;
            replay = await FolderReplayAuthority.AuthorizeAsync(admission, execution.Services, cancellationToken).ConfigureAwait(false);
        }
        return replay is not null && replay.MatchesExecution(new FolderReplayExecution
        {
            Command = execution.Command, ExpectedEtag = execution.ExpectedEtag ?? string.Empty,
            Namespace = execution.Namespace, SchemaName = execution.Services.GetService<ISchemaContext>()?.CurrentSchema
        }) ? replay : null;
    }

}

internal sealed record FolderMutationExecution
{
    public required IServiceProvider Services { get; init; }
    public required ClaimsPrincipal Principal { get; init; }
    public required FolderMutationCommand Command { get; init; }
    public string? ExpectedEtag { get; init; }
    public string? Namespace { get; init; }
    public FolderReplayAdmission? Replay { get; init; }
    public FolderOperationReplayAdmission? OperationReplay { get; init; }
    public IDictionary<object, object?>? RequestCache { get; init; }
}

internal sealed record FolderExecutionResult(MetadataV2GraphSnapshot? Saved, FolderMutation? Mutation,
    FolderExecutionFailure? Failure, FolderAuthorizationResult? Authorization = null);

internal abstract record FolderExecutionFailure
{
    internal sealed record Replay : FolderExecutionFailure;
    internal sealed record Authorization(OperatorAccessDecision Decision) : FolderExecutionFailure;
    internal sealed record Approval(ApprovalRequirement Requirement) : FolderExecutionFailure;
    internal sealed record Mutation(FolderMutationFailure Refusal) : FolderExecutionFailure;
    internal sealed record AffectedServices(FolderAuthorizationResult Result) : FolderExecutionFailure;
}
