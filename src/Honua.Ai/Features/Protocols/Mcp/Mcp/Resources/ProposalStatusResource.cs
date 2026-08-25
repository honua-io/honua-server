// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
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
        var authority = proposal.Authority;
        var callerAuthority = OperationAuthorityContext.Capture(
            principal,
            httpContext.RequestServices.GetRequiredService<ITenantContext>(),
            httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("MultiTenancy:Enabled", true));
        if (authority?.ResourceType is not { } resourceType ||
            authority.Operation is not { } operation ||
            string.IsNullOrWhiteSpace(authority.ResourceId) ||
            !string.Equals(authority.EffectiveTenant, callerAuthority.EffectiveTenant, StringComparison.Ordinal))
        {
            throw ProposalNotFound(proposalId);
        }

        var isRetainedProposer =
            string.Equals(authority.Actor, callerAuthority.Actor, StringComparison.Ordinal) &&
            string.Equals(authority.Issuer, callerAuthority.Issuer, StringComparison.Ordinal) &&
            string.Equals(authority.Scheme, callerAuthority.Scheme, StringComparison.Ordinal);
        var readOperation = isRetainedProposer ? operation : OperatorOperation.Read;

        var jobService = httpContext.RequestServices.GetService<IGeoprocessingJobService>()
            ?? throw new InvalidOperationException("The authorization service is unavailable.");
        try
        {
            await jobService.EnsureCallerAuthorizedAsync(
                principal,
                resourceType,
                readOperation,
                cancellationToken).ConfigureAwait(false);

            var evaluator = httpContext.RequestServices.GetService<IOperatorAuthorizationEvaluator>();
            if (evaluator is not null)
            {
                var exact = await evaluator.EvaluateAsync(
                    principal,
                    new OperatorAuthorizationRequest
                    {
                        ResourceType = resourceType,
                        ResourceId = authority.ResourceId,
                        Operation = readOperation,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!exact.IsAllowed)
                {
                    throw ProposalNotFound(proposalId);
                }
            }
        }
        catch (GeoprocessingAuthorizationException)
        {
            throw ProposalNotFound(proposalId);
        }

        var resource = new McpProposalResource
        {
            ProposalId = proposal.ProposalId,
            Kind = proposal.Kind.ToString(),
            Status = proposal.Status.ToString(),
            Summary = proposal.Plan.Summary,
            RiskLevel = proposal.Plan.RiskLevel.ToString(),
            Diff = proposal.Plan.Diff,
            DryRun = proposal.Plan.DryRun,
            BlockingReasons = proposal.Plan.BlockingReasons,
            Warnings = proposal.Plan.Warnings,
            ResolvedBy = proposal.ResolvedBy,
            ResolutionReason = proposal.ResolutionReason,
            ExecutionOperationId = proposal.ExecutionOperationId,
            CreatedAt = proposal.CreatedAt,
            UpdatedAt = proposal.UpdatedAt,
        };

        return McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpProposalResource);
    }

    private static KeyNotFoundException ProposalNotFound(string proposalId)
        => new($"Proposal '{proposalId}' was not found.");
}
