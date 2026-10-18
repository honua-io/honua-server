// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Infrastructure.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.WorkflowPackages;

internal sealed class WorkflowPackageGovernance(IOperationInvoker invoker, IHttpContextAccessor accessor, WorkflowPackageAuthorization authorization)
    : IWorkflowPackageGovernance
{
    public async Task<OperationHandle> ProposeAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        WorkflowPackageGovernanceIntent intent, CancellationToken cancellationToken)
    {
        var context = accessor.HttpContext;
        var actor = CanonicalSecurityActor.Resolve(principal)?.ActorId;
        if (context is null || actor is null || actor != CanonicalSecurityActor.Resolve(context.User)?.ActorId ||
            !context.User.HasClaim("permission", "agent:workspace"))
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var access = await authorization.ResolveAsync(context.User, cancellationToken, workspaceId, turnId, true).ConfigureAwait(false);
        var decision = await OperationAdminAuthorization.EvaluateAsync(context, context.User,
            OperationSideEffectClass.MutatesMetadata, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAuthorized || string.IsNullOrWhiteSpace(access.DelegatedOwnerActorId))
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        var intentJson = JsonSerializer.Serialize(intent, WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageGovernanceIntent);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { actor, access.TenantId!, workspaceId, turnId, intentJson }, WorkflowPackageGovernanceJsonContext.Default.StringArray))));
        return await invoker.SubmitAsync(new OperationRequest
        {
            OperationId = WorkflowPackageGovernanceOperation.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["workspaceId"] = workspaceId,
                ["turnId"] = turnId,
                ["intent"] = intentJson,
            },
        }, new OperationPolicyContext
        {
            PrincipalId = actor,
            TenantId = access.TenantId,
            SchemaName = context.RequestServices.GetService<Honua.Core.Features.Infrastructure.Abstractions.ISchemaContext>()?.CurrentSchema,
            AuthorizationOutcome = decision.AuthorizationOutcome,
            RequiredApprovalPolicyRef = "agent-workspace.human-approval",
            IdempotencyKey = $"workflow-governance:{requestHash}",
            IdempotencyRequestHash = requestHash,
        }, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record WorkflowPackageApprovalPayload
{
    public required WorkflowPackageGovernanceIntent Intent { get; init; }
    public required Honua.Core.Features.Authorization.Domain.JobSecurityContext AuthorSecurityContext { get; init; }
    public required Guid KeyId { get; init; }
    public AgentCredentialSnapshot? Credential { get; init; }
    public required string ActorId { get; init; }
    public required string[] Permissions { get; init; }
    public required string TenantId { get; init; }
    public string? SchemaName { get; init; }
    public required string WorkspaceId { get; init; }
    public required string TurnId { get; init; }
    public required string OwnerActorId { get; init; }
    public required string TargetId { get; init; }
    public required string ReleaseId { get; init; }
    public string? PublicationFingerprint { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowPackageGovernanceIntent))]
[JsonSerializable(typeof(WorkflowPackageApprovalPayload))]
[JsonSerializable(typeof(WorkflowPublication))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WorkflowPackageGovernanceJsonContext : JsonSerializerContext;
