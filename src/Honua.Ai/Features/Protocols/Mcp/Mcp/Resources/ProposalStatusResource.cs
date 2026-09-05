// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://proposals/{proposalId}</c>. Exposes a readable view
/// of an operation proposal's status and plan summary so agents can poll until a
/// human resolves it (#1696).
/// </summary>
internal sealed class ProposalStatusResource : IMcpResource
{
    public const string Template = McpResourceUris.ProposalsPrefix + "{proposalId}";

    private readonly ILogger<ProposalStatusResource> _logger;

    public ProposalStatusResource(ILogger<ProposalStatusResource> logger)
    {
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Proposals;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Operation proposal status",
            Description = "Agent-proposed operation lifecycle record including status, risk, plan summary, diff, and dry-run output.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.ProposalsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.ProposalsPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetProposal");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);

        var proposalId = uri[McpResourceUris.ProposalsPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var store = httpContext.RequestServices.GetService<IOperationProposalStore>()
            ?? throw new InvalidOperationException("The operation proposal store is unavailable (durable storage is not configured).");

        var proposal = await store.GetAsync(proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");

        if (!OperationTenantAuthorization.CanAccess(httpContext, proposal.TenantId))
        {
            throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");
        }

        var actor = McpAuthorizationHelper.ResolveActorId(principal);
        var isProposer = !string.IsNullOrWhiteSpace(proposal.RequestedBy)
            && string.Equals(proposal.RequestedBy, actor, StringComparison.Ordinal);
        if (proposal.OperationId == "studio.content.create-publication-request" && !isProposer)
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "The caller is not authorized to read this Studio publication proposal.",
                resourceType: OperatorResourceType.StudioDraft,
                operation: OperatorOperation.Read);
        }

        if (!isProposer)
        {
            var authorization = httpContext.RequestServices.GetService<IGeoprocessingJobService>()
                ?? throw new InvalidOperationException("The authorization service is unavailable.");
            try
            {
                await authorization.EnsureCallerAuthorizedAsync(
                    principal,
                    proposal.Kind is OperationClass.Deploy or OperationClass.MetadataRelease
                        ? OperatorResourceType.Deployment
                        : OperatorResourceType.Catalog,
                    OperatorOperation.Read,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GeoprocessingAuthorizationException)
            {
                throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");
            }
        }

        var operation = proposal.Audit.OperationInstanceId is { Length: > 0 } operationInstanceId
            ? await (httpContext.RequestServices.GetService<IOperationInstanceStore>()
                ?? throw new InvalidOperationException("The canonical operation instance store is unavailable."))
                .GetAsync(operationInstanceId, cancellationToken).ConfigureAwait(false)
            : null;
        var isStudioPublication = proposal.OperationId == "studio.content.create-publication-request";
        var isActive = isStudioPublication && proposal.Status == OperationProposalStatus.Succeeded;
        var resource = new McpProposalResource
        {
            ProposalId = proposal.ProposalId,
            OperationInstanceId = proposal.Audit.OperationInstanceId,
            AuditId = proposal.Audit.AuditId,
            CorrelationId = proposal.Audit.CorrelationId,
            Kind = proposal.Kind.ToString(),
            Status = isActive ? "Active" : proposal.Status.ToString(),
            Summary = proposal.Plan.Summary,
            RiskLevel = proposal.Plan.RiskLevel.ToString(),
            Diff = proposal.Plan.Diff,
            DryRun = proposal.Plan.DryRun,
            BlockingReasons = proposal.Plan.BlockingReasons,
            Warnings = proposal.Plan.Warnings,
            ResolvedBy = proposal.ResolvedBy,
            ResolutionReason = proposal.ResolutionReason,
            ExecutionOperationId = proposal.ExecutionOperationId,
            PublicationId = isActive && operation?.ResourceIds.TryGetValue("publicationId", out var publicationId) == true
                ? publicationId
                : null,
            ActiveUrl = isActive && operation?.ResourceIds.TryGetValue("activeUrl", out var activeUrl) == true
                ? activeUrl
                : null,
            CreatedAt = proposal.CreatedAt,
            UpdatedAt = proposal.UpdatedAt,
        };

        return McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpProposalResource);
    }
}
