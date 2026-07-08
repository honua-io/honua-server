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
using Honua.Core.Features.Guardrails.Domain;
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
    IServiceProvider services) : IMcpPlatformOpsReader
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const string AgentActorPrefix = "agent:";

    private readonly IOptionsMonitor<ControlPlaneOptions> _controlPlaneOptions = controlPlaneOptions;
    private readonly DeployWorkflowService _deployWorkflowService = deployWorkflowService;
    private readonly IAuthorizationService _authorization = authorization;
    private readonly IServiceProvider _services = services;

    public async Task<JsonElement> GetPlatformReleaseStatusAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        await EnsureOpsReadAsync(principal, cancellationToken).ConfigureAwait(false);

        var skew = PlatformReleaseSkewProjector.Build(_controlPlaneOptions.CurrentValue);
        var response = new DeployPreflightPlatformRelease
        {
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
                HasMore = false
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
            HasMore = result.HasMore
        };

        return Serialize(list, DeployControlJsonContext.Default.DeployOperationListResponse);
    }

    public async Task<McpProposeOperationOutput> ProposeRollbackAsync(
        ClaimsPrincipal principal,
        McpProposeRollbackArgument argument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var gateway = _services.GetService<IOperationGateway>();
        var supportedKinds = ResolveSupportedKinds();
        if (gateway is null)
        {
            return new McpProposeOperationOutput
            {
                Outcome = "unavailable",
                RequiresApproval = false,
                SupportedKinds = supportedKinds,
                Message = "The operation gateway is unavailable (durable storage is not configured)."
            };
        }

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

        var actor = principal.Identity?.Name;
        var result = await gateway.RouteAsync(
                new OperationGatewayRequest
                {
                    Kind = OperationClass.Deploy,
                    RequestedByAgent = string.IsNullOrWhiteSpace(actor) ? $"{AgentActorPrefix}mcp" : $"{AgentActorPrefix}{actor}",
                    RequestedBy = actor,
                    Reason = string.IsNullOrWhiteSpace(argument.Reason)
                        ? $"Propose rollback of deploy target '{targetId}' to prior revision '{selection.DesiredRevision}'."
                        : argument.Reason,
                    IdempotencyKey = Clean(argument.IdempotencyKey)
                        ?? $"rollback:{targetId}:{selection.DesiredRevision}",
                    ExecutionPayload = payload,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new McpProposeOperationOutput
        {
            Outcome = result.Outcome.ToString(),
            RequiresApproval = result.Outcome == OperationGatewayOutcome.ProposalCreated,
            ProposalId = result.ProposalId,
            ResourceUri = result.ProposalId == null ? null : McpResourceUris.ProposalUri(result.ProposalId),
            ExecutionOperationId = result.ExecutionOperationId,
            SupportedKinds = supportedKinds,
            Message = result.Message,
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
