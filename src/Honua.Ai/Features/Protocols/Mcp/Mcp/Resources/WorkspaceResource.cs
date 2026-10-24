// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
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
        var workspaceId = uri[McpResourceUris.WorkspacesPrefix.Length..];
        McpLog.ResourceRead(_logger, Family, uri);

        // No lifecycle store: there is no owner or scope to authorize against, so the
        // type-level gate still applies and the stable degraded envelope is unchanged.
        using var lifecycle = OpenLifecycle();
        if (lifecycle is null)
        {
            await _jobService
                .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Workspace, OperatorOperation.Read, cancellationToken)
                .ConfigureAwait(false);
            return McpResourceHelpers.SingleJsonContent(
                uri, CreateDegradedResource(workspaceId), McpJsonContext.Default.McpWorkspaceResource);
        }

        var workspace = await lifecycle.Service.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.State == WorkspaceLifecycleState.Deleted)
        {
            throw new GeoprocessingNotFoundException($"Workspace '{workspaceId}' not found.");
        }

        // Load first, then authorize with the workspace's owner or scope. A denial is
        // not_found so a caller cannot probe which workspace ids exist (honua-server#5629).
        try
        {
            await _jobService
                .EnsureCallerAuthorizedAsync(principal, AuthorizationFor(httpContext, workspace), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GeoprocessingAuthorizationException ex) when (!ex.RequiresAuthentication)
        {
            throw new GeoprocessingNotFoundException($"Workspace '{workspaceId}' not found.");
        }

        return McpResourceHelpers.SingleJsonContent(
            uri, ToResource(workspace), McpJsonContext.Default.McpWorkspaceResource);
    }

    private LifecycleRead? OpenLifecycle()
    {
        if (_scopeFactory is null)
        {
            return null;
        }

        var scope = _scopeFactory.CreateScope();
        var lifecycle = scope.ServiceProvider.GetService<IWorkspaceLifecycleService>();
        if (lifecycle is null)
        {
            scope.Dispose();
            return null;
        }

        return new LifecycleRead(scope, lifecycle);
    }

    private static OperatorAuthorizationRequest AuthorizationFor(HttpContext httpContext, Workspace workspace)
    {
        // GeoprocessingDispatchJobExecutor stores the submitter's tenant id in ScopeId.
        // The shipped authorizer checks a shared scope only against the configured
        // workspace-scope claim (groups by default), not the tenant, so a scope that
        // is the effective tenant stays personal and the owner can still read it.
        var scopeId = string.IsNullOrWhiteSpace(workspace.ScopeId) ? null : workspace.ScopeId.Trim();
        var tenantId = httpContext.RequestServices?.GetService<ITenantContext>()?.TenantId;
        tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
        var shared = scopeId is not null
            && tenantId is not null
            && !string.Equals(scopeId, tenantId, StringComparison.Ordinal);
        return new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Read,
            ResourceId = workspace.WorkspaceId,
            WorkspaceVisibility = shared ? WorkspaceVisibility.Shared : WorkspaceVisibility.Personal,
            WorkspaceOwnerId = shared ? null : workspace.OwnerId,
            WorkspaceScopeId = shared ? scopeId : null
        };
    }

    private sealed class LifecycleRead(IServiceScope scope, IWorkspaceLifecycleService service) : IDisposable
    {
        public IWorkspaceLifecycleService Service { get; } = service;

        public void Dispose() => scope.Dispose();
    }

    private static McpWorkspaceResource CreateDegradedResource(string workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        NotImplementedReason = LifecycleUnavailableReason
    };

    private static McpWorkspaceResource ToResource(Workspace workspace) => new()
    {
        WorkspaceId = workspace.WorkspaceId,
        Kind = ToWireKind(workspace.Kind),
        Label = workspace.Label,
        Uri = workspace.Uri,
        ExpiresAt = workspace.ExpiresAt,
        LifecycleState = ToWireLifecycleState(workspace.State),
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

    private static string ToWireLifecycleState(WorkspaceLifecycleState state) => state switch
    {
        WorkspaceLifecycleState.Active => "active",
        WorkspaceLifecycleState.Expired => "expired",
        WorkspaceLifecycleState.Archived => "archived",
        WorkspaceLifecycleState.Deleted => "deleted",
        _ => state.ToString()
    };

    private static string? ResolveResultsUri(Workspace workspace)
    {
        var resultPackageId = workspace.Artifacts
            .Select(artifact =>
                TryGetMetadataValue(artifact.Metadata, "resultPackageId", out var value) ||
                TryGetMetadataValue(artifact.Metadata, "result.package.id", out value) ||
                TryGetMetadataValue(artifact.Metadata, "honua.resultPackageId", out value) ||
                TryGetMetadataValue(artifact.Metadata, "sourceResultPackageId", out value)
                    ? value
                    : null)
            .FirstOrDefault(value => value is not null);

        return resultPackageId is null
            ? null
            : McpResourceUris.ResultPackageUri(resultPackageId);
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

        var match = metadata.FirstOrDefault(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(pair.Value));

        if (match.Key is not null)
        {
            value = match.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
