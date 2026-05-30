// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// MCP resource for <c>honua://workspaces/{workspaceId}</c>. Projects
/// workspace lifecycle metadata when a workspace store is registered and
/// otherwise returns a degraded, stable envelope.
/// </summary>
internal sealed class WorkspaceResource : IMcpResource
{
    public const string Template = McpResourceUris.WorkspacesPrefix + "{workspaceId}";

    private const string LifecycleUnavailableReason =
        "Workspace lifecycle reads require a registered workspace store and lifecycle service.";

    private readonly IGeoprocessingJobService _jobService;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<WorkspaceResource> _logger;

    public WorkspaceResource(
        IGeoprocessingJobService jobService,
        IServiceScopeFactory scopeFactory,
        ILogger<WorkspaceResource> logger)
    {
        _jobService = jobService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public WorkspaceResource(IGeoprocessingJobService jobService, ILogger<WorkspaceResource> logger)
    {
        _jobService = jobService;
        _scopeFactory = null;
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.Workspaces;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => new[]
    {
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Workspace",
            Description = "Workspace metadata for a referenced workspace id.",
            MimeType = McpResourceHelpers.JsonMimeType
        }
    };

    public bool CanHandle(string uri)
    {
        if (!uri.StartsWith(McpResourceUris.WorkspacesPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = uri.AsSpan(McpResourceUris.WorkspacesPrefix.Length);
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    public async Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GetWorkspace");
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Workspace, OperatorOperation.Read);

        var workspaceId = uri[McpResourceUris.WorkspacesPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        McpWorkspaceResource resource;
        if (_scopeFactory is null)
        {
            resource = CreateDegradedResource(workspaceId);
        }
        else
        {
            using var scope = _scopeFactory.CreateScope();
            var lifecycle = scope.ServiceProvider.GetService<IWorkspaceLifecycleService>();
            if (lifecycle is null)
            {
                resource = CreateDegradedResource(workspaceId);
            }
            else
            {
                var workspace = await lifecycle.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
                if (workspace is null || workspace.State == WorkspaceLifecycleState.Deleted)
                {
                    throw new GeoprocessingNotFoundException($"Workspace '{workspaceId}' not found.");
                }

                resource = ToResource(workspace);
            }
        }

        var result = McpResourceHelpers.SingleJsonContent(uri, resource, McpJsonContext.Default.McpWorkspaceResource);
        return result;
    }

    private static McpWorkspaceResource CreateDegradedResource(string workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        Kind = string.Empty,
        Label = string.Empty,
        Status = "degraded",
        NotImplementedReason = LifecycleUnavailableReason
    };

    private static McpWorkspaceResource ToResource(Workspace workspace) => new()
    {
        WorkspaceId = workspace.WorkspaceId,
        Kind = ToWireKind(workspace.Kind),
        Label = workspace.Label,
        Uri = workspace.Uri,
        ExpiresAt = workspace.ExpiresAt,
        Status = ToWireStatus(workspace.State),
        CleanupScheduledAt = workspace.State == WorkspaceLifecycleState.Expired
            ? workspace.ExpiresAt
            : null,
        ResultsUri = ResolveResultsUri(workspace)
    };

    private static string ToWireKind(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Scratch => "scratch",
        WorkspaceKind.Persistent => "persistent",
        WorkspaceKind.TempLayer => "temp_layer",
        WorkspaceKind.SavedLayer => "saved_layer",
        WorkspaceKind.ResultCollection => "result_collection",
        _ => kind.ToString()
    };

    private static string ToWireStatus(WorkspaceLifecycleState state) => state switch
    {
        WorkspaceLifecycleState.Active => "active",
        WorkspaceLifecycleState.Expired => "cleanup_pending",
        WorkspaceLifecycleState.Archived => "sealed",
        WorkspaceLifecycleState.Deleted => "not_found",
        _ => state.ToString()
    };

    private static string? ResolveResultsUri(Workspace workspace)
    {
        foreach (var artifact in workspace.Artifacts)
        {
            if (TryGetMetadataValue(artifact.Metadata, "jobId", out var jobId)
                || TryGetMetadataValue(artifact.Metadata, "job.id", out jobId)
                || TryGetMetadataValue(artifact.Metadata, "honua.jobId", out jobId)
                || TryGetMetadataValue(artifact.Metadata, "sourceJobId", out jobId))
            {
                return McpResourceUris.JobResultsUri(jobId);
            }
        }

        return null;
    }

    private static bool TryGetMetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        out string value)
    {
        if (metadata.TryGetValue(key, out value!)
            && !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        foreach (var pair in metadata)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
