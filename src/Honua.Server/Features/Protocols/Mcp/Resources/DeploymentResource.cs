// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://deployments/{deploymentId}</c>. Reads a
/// <see cref="Deployment"/> through the canonical deployment store and surfaces
/// provenance edges back to the originating published service, map package, or
/// app package that backs the deployment, plus the append-only transition audit
/// trail so agents can observe lifecycle state changes without subscribing.
/// </summary>
internal sealed class DeploymentResource : IMcpResource
{
    public const string Template = McpResourceUris.DeploymentsPrefix + "{deploymentId}";

    private readonly IDeploymentStore _deployments;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<DeploymentResource> _logger;

    public DeploymentResource(
        IDeploymentStore deployments,
        IGeoprocessingJobService jobService,
        ILogger<DeploymentResource> logger)
    {
        _deployments = deployments;
        _jobService = jobService;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Deployments;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Deployment",
            Description = "Hosted deployment record including status, publication state, rollout progression, transition audit trail, and provenance edges.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.DeploymentsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.DeploymentsPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetDeployment");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(
            principal, OperatorResourceType.Deployment, OperatorOperation.Read);

        var deploymentId = uri[McpResourceUris.DeploymentsPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        var deployment = await _deployments.GetAsync(deploymentId, cancellationToken).ConfigureAwait(false);
        if (deployment is null)
        {
            throw new GeoprocessingNotFoundException($"Deployment '{deploymentId}' not found.");
        }

        McpLog.DeploymentRead(_logger, deploymentId, deployment.Status.ToString());

        var view = ToView(deployment);
        return McpResourceHelpers.SingleJsonContent(uri, view, McpJsonContext.Default.McpDeploymentView);
    }

    internal static McpDeploymentView ToView(Deployment deployment) => new()
    {
        DeploymentId = deployment.DeploymentId,
        ResourceUri = McpResourceUris.DeploymentUri(deployment.DeploymentId),
        Status = deployment.Status.ToString(),
        PublicationState = deployment.PublicationState.ToString(),
        RolloutState = deployment.RolloutState.ToString(),
        SourceKind = deployment.Source.Kind.ToString(),
        SourceId = deployment.Source.SourceId,
        SourceResourceUri = ResolveSourceResourceUri(deployment.Source),
        TargetId = deployment.Target.TargetId,
        TargetKind = deployment.Target.Kind.ToString(),
        HostingMode = deployment.Target.HostingMode.ToString(),
        Environment = deployment.Target.Environment,
        RoutePrefix = deployment.Target.RoutePrefix,
        PublicUrl = deployment.Runtime.PublicUrl,
        RuntimeHealth = deployment.Runtime.Health.ToString(),
        CreatedAt = deployment.CreatedAt,
        UpdatedAt = deployment.UpdatedAt,
        ActivatedAt = deployment.ActivatedAt,
        RetiredAt = deployment.RetiredAt,
        FailureReason = deployment.FailureReason,
        Etag = PromotionSurfaceEtag.ForDeployment(deployment),
        Transitions = deployment.Transitions
            .Select(transition => new McpDeploymentTransitionView
            {
                From = transition.From.ToString(),
                To = transition.To.ToString(),
                At = transition.At,
                RolloutState = transition.RolloutState?.ToString(),
                Reason = transition.Reason
            })
            .ToList(),
        Provenance = new McpHostedProvenance
        {
            PublishedServiceResourceUri = deployment.Source.PublishedServiceId is { } publishedServiceId
                ? McpResourceUris.PublishedServiceUri(publishedServiceId)
                : null,
            SupersededByDeploymentResourceUri = deployment.SupersededByDeploymentId is { } successor
                ? McpResourceUris.DeploymentUri(successor)
                : null
        }
    };

    private static string? ResolveSourceResourceUri(DeploymentSource source) => source.Kind switch
    {
        DeploymentSourceKind.PublishedService => McpResourceUris.PublishedServiceUri(source.SourceId),
        DeploymentSourceKind.MapPackage => McpResourceUris.MapPackageUri(source.SourceId),
        DeploymentSourceKind.AppPackage => McpResourceUris.AppPackageUri(source.SourceId),
        _ => null
    };
}
