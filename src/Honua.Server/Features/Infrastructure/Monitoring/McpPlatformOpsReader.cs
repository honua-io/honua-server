// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Server adapter that backs the platform-release and deploy-operation MCP
/// tools with the same admin DTOs and control-plane gateway used by REST.
/// </summary>
internal sealed class McpPlatformOpsReader(
    IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
    DeployWorkflowService deployWorkflowService,
    IAuthorizationService authorization,
    IOperatorScopeAuthorizer scopeAuthorizer,
    IServiceProvider services) : IMcpPlatformOpsReader
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const string AgentActorPrefix = "agent:";

    private readonly IOptionsMonitor<ControlPlaneOptions> _controlPlaneOptions = controlPlaneOptions;
    private readonly DeployWorkflowService _deployWorkflowService = deployWorkflowService;
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IOperatorScopeAuthorizer _scopeAuthorizer = scopeAuthorizer;
    private readonly IServiceProvider _services = services;

    public async Task<JsonElement> GetPlatformReleaseStatusAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var skew = PlatformReleaseSkewProjector.Build(_controlPlaneOptions.CurrentValue);
        var observedAt = DateTimeOffset.UtcNow;
        var response = new DeployPreflightPlatformRelease
        {
            EvidencePosture = EvidencePostureFactory.Build(observedAt,
                EvidencePostureFactory.Complete(EvidencePostureVocabulary.SourceIds.PlatformReleaseStatus,
                    EvidencePostureVocabulary.BackendKinds.ConfigProjection, "control-plane-options", observedAt, TimeSpan.FromMinutes(5))),
            ReleaseVersion = skew.ReleaseVersion,
            ReleaseDeclared = skew.ReleaseDeclared,
            IsCoVersioned = skew.IsCoVersioned,
            Serving = skew.Serving.Select(MapPlaneProjection).ToArray(),
            Execution = skew.Execution.Select(MapPlaneProjection).ToArray(),
            SkewedIds = skew.SkewedIds
        };

        return Serialize(response, DeployControlJsonContext.Default.DeployPreflightPlatformRelease);
    }

    public async Task<JsonElement> GetDeployOperationsAsync(
        ClaimsPrincipal principal,
        McpDeployOperationsArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument);
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var operationId = Clean(argument.OperationId);
        if (operationId is not null)
        {
            var operation = await _deployWorkflowService.GetAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new GeoprocessingNotFoundException($"Deploy operation '{operationId}' was not found.");

            var response = new DeployOperationListResponse
            {
                Items = [DeployControlEndpoints.MapOperationResponse(operation)],
                Page = 1,
                PageSize = 1,
                TotalCount = 1,
                HasMore = false,
                EvidencePosture = DeployControlEndpoints.BuildDeployOperationsPosture([operation.UpdatedAt], 1, 1, false),
            };

            return Serialize(response, DeployControlJsonContext.Default.DeployOperationListResponse);
        }

        var status = ParseOptionalEnum<WorkflowOperationStatus>(argument.Status, "status");
        var kind = ParseOptionalEnum<WorkflowOperationKind>(argument.Kind, "kind");
        var page = Math.Max(1, argument.Page ?? 1);
        var pageSize = ClampPageSize(argument.PageSize);

        var result = await _deployWorkflowService
            .ListDeployOperationsAsync(status, kind, page, pageSize, cancellationToken)
            .ConfigureAwait(false);

        var list = new DeployOperationListResponse
        {
            Items = result.Items.Select(DeployControlEndpoints.MapOperationResponse).ToArray(),
            Page = result.Page,
            PageSize = result.PageSize,
            TotalCount = result.TotalCount,
            HasMore = result.HasMore,
            EvidencePosture = DeployControlEndpoints.BuildDeployOperationsPosture(result.Items.Select(item => item.UpdatedAt), result.Page, result.PageSize, result.HasMore),
        };

        return Serialize(list, DeployControlJsonContext.Default.DeployOperationListResponse);
    }

    public async Task<McpSupportedOperationKindsOutput> GetSupportedOperationKindsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var catalog = _services.GetService<IOperationExecutorCatalog>();
        return new McpSupportedOperationKindsOutput
        {
            SupportedKinds = catalog?.SupportedKinds
                .Select(kind => kind.ToString())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray() ?? []
        };
    }

    public async Task<McpProposeOperationOutput> ProposeRollbackAsync(
        ClaimsPrincipal principal,
        McpProposeRollbackArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument);

        await EnsureMutationAuthorizedAsync(principal, OperatorResourceType.Deployment, OperatorOperation.Rollback, cancellationToken).ConfigureAwait(false);

        var targetId = Clean(argument.TargetId);
        if (targetId is null)
        {
            throw new GeoprocessingValidationException("'targetId' is required.");
        }

        var selection = await ResolveRollbackRevisionAsync(
                targetId,
                Clean(argument.ToRevision),
                cancellationToken)
            .ConfigureAwait(false);

        var payload = new DeployExecutionPayload
        {
            TargetId = targetId,
            DesiredRevision = selection.DesiredRevision,
            CurrentRevision = selection.CurrentRevision,
        }.Serialize();

        return await SealProposalAsync(principal, OperationClass.Deploy, payload,
            string.IsNullOrWhiteSpace(argument.Reason) ? $"Propose rollback of deploy target '{targetId}' to prior revision '{selection.DesiredRevision}'." : argument.Reason,
            Clean(argument.IdempotencyKey) ?? $"rollback:{targetId}:{selection.DesiredRevision}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpProposeOperationOutput> ProposeFindingAsync(ClaimsPrincipal principal, McpProposeFindingArgument argument, CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(principal, OperatorResourceType.Deployment, OperatorOperation.Publish, cancellationToken).ConfigureAwait(false);
        var findingId = Clean(argument.FindingId) ?? throw new GeoprocessingValidationException("'findingId' is required.");
        var evaluation = await _services.GetRequiredService<IOpsFindingsEvidenceSource>()
            .EvaluateWithEvidenceAsync(cancellationToken).ConfigureAwait(false);
        var finding = evaluation.Findings
            .FirstOrDefault(candidate => string.Equals(candidate.Id, findingId, StringComparison.Ordinal));
        var action = finding?.RecommendedAction ?? throw new GeoprocessingNotFoundException($"Finding '{findingId}' was not found or has no recommended action.");
        if (!OpsFindingEvidenceMap.TryGetActionableRequiredSources(evaluation, finding, out _))
            return new McpProposeOperationOutput { Outcome = "Blocked", SupportedKinds = ResolveSupportedKinds(), Message = "evidencePostureNotActionable" };
        return await SealProposalAsync(principal, action.Kind, action.ExecutionPayload, action.Reason, findingId, cancellationToken, action.ActionDiscriminator).ConfigureAwait(false);
    }

    public async Task<McpProposeOperationOutput> ProposeDeployPlanAsync(ClaimsPrincipal principal, McpDeployMutationArgument argument, CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(principal, OperatorResourceType.Deployment, OperatorOperation.Publish, cancellationToken).ConfigureAwait(false);
        var targetId = Clean(argument.TargetId) ?? throw new GeoprocessingValidationException("'targetId' is required.");
        var desiredRevision = Clean(argument.DesiredRevision) ?? throw new GeoprocessingValidationException("'desiredRevision' is required.");
        var plan = await _deployWorkflowService.PlanAsync(targetId, desiredRevision, Clean(argument.CurrentRevision), null, principal, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException($"Deploy target '{targetId}' was not found.");
        return new McpProposeOperationOutput
        {
            Outcome = "planned",
            RequiresApproval = false,
            Result = Serialize(DeployControlEndpoints.MapPlanResponse(plan), DeployControlJsonContext.Default.DeployPlanResponse),
        };
    }

    public async Task<McpProposeOperationOutput> ProposeDeployOperationAsync(ClaimsPrincipal principal, McpDeployMutationArgument argument, CancellationToken cancellationToken)
        => await ProposeDeployAsync(principal, argument, "deploy-operation", cancellationToken).ConfigureAwait(false);

    public async Task<McpProposeOperationOutput> ProposePlatformReleaseConvergenceAsync(ClaimsPrincipal principal, McpPlatformReleaseConvergenceArgument argument, CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(principal, OperatorResourceType.Deployment, OperatorOperation.Publish, cancellationToken).ConfigureAwait(false);
        var options = _controlPlaneOptions.CurrentValue;
        var release = options.PlatformRelease.ToDefinition() ?? throw new GeoprocessingPreconditionFailedException("A platform release is not declared.");
        var desiredRevision = Clean(release.ServingArtifactReference) ?? throw new GeoprocessingPreconditionFailedException("The platform release has no serving artifact.");
        var targets = options.DeployTargets.Where(candidate => !string.IsNullOrWhiteSpace(candidate.TargetId)).ToArray();
        if (targets.Length == 0) throw new GeoprocessingPreconditionFailedException("No serving deploy target is configured.");
        var outcomes = new List<McpConvergenceTargetOutput>();
        foreach (var target in targets)
        {
            if (!string.IsNullOrWhiteSpace(target.ArtifactReference) && !string.Equals(target.ArtifactReference, desiredRevision, StringComparison.Ordinal))
            {
                outcomes.Add(new() { TargetId = target.TargetId, Outcome = "skipped-pinned", Message = "Target pins an explicit artifact that diverges from the release." });
                continue;
            }
            var lastApplied = (await _deployWorkflowService.GetMostRecentSucceededDeployAsync(target.TargetId, cancellationToken).ConfigureAwait(false))?.Deploy?.DesiredRevision;
            if (string.Equals(lastApplied, desiredRevision, StringComparison.Ordinal))
            {
                outcomes.Add(new() { TargetId = target.TargetId, Outcome = "already-converged" });
                continue;
            }
            var payload = new DeployExecutionPayload { TargetId = target.TargetId, DesiredRevision = desiredRevision }.Serialize();
            var requestedKey = Clean(argument.IdempotencyKey);
            var proposal = await SealProposalAsync(principal, OperationClass.Deploy, payload,
                Clean(argument.Reason) ?? $"Converge serving targets to platform release {release.Version}.",
                requestedKey is null ? $"converge:{release.Version}:{target.TargetId}" : $"{requestedKey}:{target.TargetId}", cancellationToken).ConfigureAwait(false);
            outcomes.Add(new() { TargetId = target.TargetId, Outcome = proposal.Outcome, ProposalId = proposal.ProposalId, Message = proposal.Message });
        }
        return new McpProposeOperationOutput { Outcome = "completed", RequiresApproval = outcomes.Any(item => item.ProposalId is not null), Targets = outcomes.ToArray(), SupportedKinds = ResolveSupportedKinds() };
    }

    private async Task<McpProposeOperationOutput> ProposeDeployAsync(ClaimsPrincipal principal, McpDeployMutationArgument argument, string idempotencyPrefix, CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(principal, OperatorResourceType.Deployment, OperatorOperation.Publish, cancellationToken).ConfigureAwait(false);
        var targetId = Clean(argument.TargetId) ?? throw new GeoprocessingValidationException("'targetId' is required.");
        var desiredRevision = Clean(argument.DesiredRevision) ?? throw new GeoprocessingValidationException("'desiredRevision' is required.");
        var payload = new DeployExecutionPayload { TargetId = targetId, DesiredRevision = desiredRevision, CurrentRevision = Clean(argument.CurrentRevision) }.Serialize();
        return await SealProposalAsync(principal, OperationClass.Deploy, payload,
            Clean(argument.Reason) ?? $"Propose deploy of target '{targetId}' to '{desiredRevision}'.",
            Clean(argument.IdempotencyKey) ?? $"{idempotencyPrefix}:{targetId}:{desiredRevision}", cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpProposeOperationOutput> SealProposalAsync(ClaimsPrincipal principal, OperationClass kind, string? payload, string? reason, string idempotencyKey, CancellationToken cancellationToken, string? actionDiscriminator = null)
    {
        var gateway = _services.GetService<IOperationGateway>();
        var envelopeFactory = _services.GetService<IOperationEnvelopeFactory>();
        if (gateway is null || envelopeFactory is null)
            return new McpProposeOperationOutput { Outcome = "unavailable", SupportedKinds = ResolveSupportedKinds(), Message = "The governed operation proposal runtime is unavailable." };

        var actor = McpAuthorizationHelper.ResolveActorId(principal);
        var scopeGoverned = OperatorScopeCatalog.IsScopeGoverned(principal);
        var scopes = OperatorScopeCatalog.CollectRecognizedScopes(principal).OrderBy(scope => scope, StringComparer.Ordinal).ToArray();
        var authorizationOutcome = scopeGoverned ? "admin-policy-and-oauth-scope-authorized" : "admin-policy-authorized";
        var accepted = await envelopeFactory.CreateAcceptedAsync($"control-plane.{kind.ToString().ToLowerInvariant()}", new OperationPolicyContext
        {
            PrincipalId = actor,
            IdempotencyKey = idempotencyKey,
            AuthorizationOutcome = authorizationOutcome,
            ScopeGoverned = scopeGoverned,
            RecognizedScopes = scopes,
        }, cancellationToken).ConfigureAwait(false);
        if (accepted.Status == OperationHandleStatus.Failed || string.IsNullOrWhiteSpace(accepted.AuditId))
            return new McpProposeOperationOutput { Outcome = "Failed", SupportedKinds = ResolveSupportedKinds(), Message = accepted.Reason ?? "The proposal could not be durably accepted and audited." };

        var result = await gateway.CreateApprovalProposalAsync(accepted.OperationInstanceId, new OperationGatewayRequest
        {
            OperationInstanceId = accepted.OperationInstanceId,
            CorrelationId = accepted.CorrelationId,
            Kind = kind,
            ActionDiscriminator = actionDiscriminator,
            RequestedBy = actor,
            RequestedByAgent = string.IsNullOrWhiteSpace(actor) ? $"{AgentActorPrefix}mcp" : $"{AgentActorPrefix}{actor}",
            Reason = reason,
            IdempotencyKey = idempotencyKey,
            ExecutionPayload = payload,
            ScopeGoverned = scopeGoverned,
            RecognizedScopes = scopes,
        }, cancellationToken).ConfigureAwait(false);
        return new McpProposeOperationOutput
        {
            Outcome = result.Outcome.ToString(),
            RequiresApproval = result.Outcome == OperationGatewayOutcome.ProposalCreated,
            ProposalId = result.ProposalId,
            ResourceUri = result.ProposalId is null ? null : McpResourceUris.ProposalUri(result.ProposalId),
            ExecutionOperationId = result.ExecutionOperationId,
            SupportedKinds = ResolveSupportedKinds(),
            Message = result.Message
        };
    }

    private async Task<RollbackRevisionSelection> ResolveRollbackRevisionAsync(
        string targetId,
        string? explicitRevision,
        CancellationToken cancellationToken)
    {
        var targetDeploys = await ListRecentSucceededDeploysForTargetAsync(
                targetId,
                explicitRevision is null ? 2 : 1,
                cancellationToken)
            .ConfigureAwait(false);

        if (explicitRevision is not null)
        {
            return new RollbackRevisionSelection(
                explicitRevision,
                targetDeploys.Count == 0 ? null : targetDeploys[0].Deploy?.DesiredRevision);
        }

        if (targetDeploys.Count < 2)
        {
            throw new GeoprocessingPreconditionFailedException(
                $"Deploy target '{targetId}' does not have a prior succeeded deploy revision to roll back to.");
        }

        return new RollbackRevisionSelection(
            targetDeploys[1].Deploy!.DesiredRevision,
            targetDeploys[0].Deploy?.DesiredRevision);
    }

    private async Task<IReadOnlyList<WorkflowOperationRecord>> ListRecentSucceededDeploysForTargetAsync(
        string targetId,
        int requiredCount,
        CancellationToken cancellationToken)
    {
        var matches = new List<WorkflowOperationRecord>(Math.Max(1, requiredCount));

        for (var page = 1; matches.Count < requiredCount; page++)
        {
            var result = await _deployWorkflowService
                .ListDeployOperationsAsync(
                    WorkflowOperationStatus.Succeeded,
                    WorkflowOperationKind.Deploy,
                    page,
                    MaxPageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            matches.AddRange(result.Items.Where(operation =>
                operation.Deploy is not null &&
                string.Equals(operation.Deploy.TargetId, targetId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(operation.Deploy.DesiredRevision)));

            if (!result.HasMore || result.Items.Count == 0)
            {
                break;
            }
        }

        return matches;
    }

    private async Task EnsureOpsReadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var resource = new DefaultHttpContext
        {
            User = principal,
            RequestAborted = cancellationToken
        };
        resource.Request.Method = HttpMethods.Get;

        var result = await _authorization
            .AuthorizeAsync(principal, resource, AuthenticationExtensions.OpsReadPolicy)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "Caller is not authorized to read platform operations.");
        }
    }

    private async Task EnsureMutationAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken)
    {
        var resource = new DefaultHttpContext { User = principal, RequestAborted = cancellationToken };
        resource.Request.Method = HttpMethods.Post;
        var grant = await _authorization.AuthorizeAsync(principal, resource, AuthenticationExtensions.AdminPolicy).ConfigureAwait(false);
        if (!grant.Succeeded)
            throw new GeoprocessingAuthorizationException(false, "Caller is not authorized to propose platform mutations.");

        var scope = _scopeAuthorizer.Evaluate(principal, resourceType, operation);
        if (!scope.IsAllowed)
            throw new GeoprocessingAuthorizationException(false, scope.Reason ?? "The access token scope does not authorize this mutation.");
    }

    private string[]? ResolveSupportedKinds()
    {
        var catalog = _services.GetService<IOperationExecutorCatalog>();
        return catalog?.SupportedKinds
            .Select(kind => kind.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static DeployPreflightPlaneProjection MapPlaneProjection(PlatformReleasePlaneProjection projection)
        => new()
        {
            Id = projection.Id,
            RuntimeProfile = projection.RuntimeProfile,
            EffectiveArtifactReference = projection.EffectiveArtifactReference,
            ProjectedFromRelease = projection.ProjectedFromRelease,
            Skewed = projection.Skewed
        };

    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new GeoprocessingValidationException(
            $"'{fieldName}' contains unsupported value '{value}'.");
    }

    private static int ClampPageSize(int? pageSize) =>
        Math.Min(MaxPageSize, Math.Max(1, pageSize ?? DefaultPageSize));

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record RollbackRevisionSelection(string DesiredRevision, string? CurrentRevision);
}
