// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed class DatasetImportTaskProvider(IAgentWorkspaceStore store, IHttpContextAccessor accessor)
    : IDatasetImportTaskProvider, IDatasetImportTaskContextReader
{
    public Task<DatasetImportTask?> AuthorizeAsync(ClaimsPrincipal principal, string workspaceId,
        string taskId, long revision, string contentHash, bool requireWrite, CancellationToken cancellationToken = default)
        => AuthorizeCoreAsync(principal, workspaceId, taskId, revision, contentHash, requireWrite, false, cancellationToken);

    public async Task<DatasetImportTask?> ReadAsync(ClaimsPrincipal principal, string workspaceId,
        string taskId, long revision, string contentHash, CancellationToken cancellationToken = default)
    {
        var task = await AuthorizeCoreAsync(principal, workspaceId, taskId, revision, contentHash, false, true, cancellationToken).ConfigureAwait(false);
        if (task is not null) return task;
        var contributors = accessor.HttpContext?.RequestServices.GetService<IDatasetImportContributorAuthorizer>();
        return contributors is null ? null : (await contributors.AuthorizeAsync(principal, workspaceId, taskId,
            revision, contentHash, false, cancellationToken).ConfigureAwait(false))?.Task;
    }

    private async Task<DatasetImportTask?> AuthorizeCoreAsync(ClaimsPrincipal principal, string workspaceId,
        string taskId, long revision, string contentHash, bool requireWrite, bool allowLinkedContext, CancellationToken cancellationToken)
    {
        var transport = accessor.HttpContext;
        if (transport is null || principal.Identity?.IsAuthenticated != true || !AgentWorkspaceState.IsIdentifier(workspaceId) ||
            !AgentWorkspaceState.IsIdentifier(taskId) || revision <= 0 || !DatasetImportTaskState.Hash(contentHash)) return null;
        if (!await AgentCredentialAuthority.IsCurrentAsync(principal, transport.RequestServices, cancellationToken).ConfigureAwait(false)) return null;
        var actor = CanonicalSecurityActor.Resolve(principal)?.ActorId;
        var tenant = transport.RequestServices.GetService<ITenantContext>()?.TenantId;
        if (actor is null || actor != CanonicalSecurityActor.Resolve(transport.User)?.ActorId) return null;
        var target = await store.GetTargetIdAsync().ConfigureAwait(false);
        var document = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (document is null || !AgentWorkspaceState.CanRead(document, actor, tenant, target) ||
            !document.DatasetImportTasks.TryGetValue(taskId, out var versions)) return null;
        var task = versions.SingleOrDefault(item => item.Revision == revision);
        if (task is null || task.TaskId != taskId || !CanObserve(principal, document, task, actor, allowLinkedContext) || task.TenantId != tenant ||
            task.TargetId != target || task.WorkspaceId != workspaceId || task.ContentHash != contentHash ||
            DatasetImportTaskState.ContentHash(task) != contentHash) return null;
        if (!await transport.RequestServices.GetRequiredService<IDatasetImportDestinationAuthorizer>()
            .IsAuthorizedAsync(principal, target, task.Destination, task.Options, requireWrite, cancellationToken).ConfigureAwait(false)) return null;
        var sourceContext = new DefaultHttpContext { RequestServices = transport.RequestServices, User = principal, RequestAborted = cancellationToken };
        if (!await SourceAuthorizedAsync(sourceContext, document, task.Source, cancellationToken).ConfigureAwait(false)) return null;
        // Source/destination checks can await external providers. Membership and stored
        // credentials must still be current when returning this retained authorization.
        var latest = await store.GetAsync(workspaceId).ConfigureAwait(false);
        if (latest is null || !AgentWorkspaceState.CanRead(latest, actor, tenant, target) ||
            !latest.DatasetImportTasks.TryGetValue(taskId, out var latestVersions) ||
            latestVersions.SingleOrDefault(item => item.Revision == revision) is not { } latestTask ||
            latestTask != task || !CanObserve(principal, latest, latestTask, actor, allowLinkedContext) ||
            !await AgentCredentialAuthority.IsCurrentAsync(principal, transport.RequestServices, cancellationToken).ConfigureAwait(false)) return null;
        return task;
    }

    private static bool CanObserve(ClaimsPrincipal principal, AgentWorkspaceDocument workspace,
        DatasetImportTask task, string actor, bool allowLinkedContext)
        => task.OwnerActorId == actor || allowLinkedContext && AgentCredentialAuthority.IsAgent(principal) &&
            workspace.AgentActorId == actor && workspace.OwnerId == task.OwnerActorId;

    internal static Task<bool> SourceAuthorizedAsync(HttpContext context, AgentWorkspaceDocument workspace,
        DatasetImportSource source, CancellationToken cancellationToken)
        => SourceAuthorizedAsync(context.RequestServices, context.User,
            context.RequestServices.GetService<ITenantContext>()?.TenantId, workspace, source, cancellationToken);

    internal static async Task<bool> HistoricalSourceAuthorizedAsync(IServiceProvider services, ClaimsPrincipal principal,
        string? tenant, AgentWorkspaceDocument workspace, DatasetImportSource source, CancellationToken cancellationToken)
    {
        if (source.Kind == "browser-file") return source.Artifact is null;
        if (source.Kind != "local-process-artifact" || source.Artifact is not { } artifact
            || LocalProcessWorkspaceAuthorization.TrustedAccess(principal, tenant, workspace) is not { } access) return false;
        var gateway = services.GetService<ILocalProcessWorkspaceGateway>();
        if (gateway?.IsConfigured != true) return false;
        try
        {
            // The accepted protected task pins original run/snapshot/path/bytes/hash.
            // Historical observation needs current project ownership, not retained
            // source bytes or an old daemon run manifest that cleanup may remove.
            var identity = await gateway.GetIdentityAsync(access, cancellationToken).ConfigureAwait(false);
            return identity.ProjectId == artifact.ProjectId && LocalProcessWorkspaceAuthorization.BindingMatches(identity.Binding, access);
        }
        catch (LocalProcessWorkspaceException exception) when (exception.StatusCode is 403 or 404) { return false; }
    }

    internal static async Task<bool> SourceAuthorizedAsync(IServiceProvider services, ClaimsPrincipal principal,
        string? tenant, AgentWorkspaceDocument workspace, DatasetImportSource source, CancellationToken cancellationToken)
    {
        if (source.Kind == "browser-file") return source.Artifact is null;
        if (source.Kind != "local-process-artifact" || source.Artifact is not { } artifact ||
            LocalProcessWorkspaceAuthorization.TrustedAccess(principal, tenant, workspace) is not { } access) return false;
        var gateway = services.GetService<ILocalProcessWorkspaceGateway>();
        if (gateway?.IsConfigured != true) return false;
        try
        {
            // Metadata only: never allocate artifact bodies while authorizing a large upload.
            var run = await gateway.GetRunAsync(access, artifact.RunId, cancellationToken).ConfigureAwait(false);
            return run.RunId == artifact.RunId && run.ProjectId == artifact.ProjectId && run.SnapshotId == artifact.SnapshotId &&
                run.SnapshotDigest == artifact.SnapshotDigest && run.Status == "succeeded" && run.ExitCode == 0 &&
                run.Artifacts.Count(item => item.Path == artifact.Path && item.Bytes == source.TotalBytes && item.Sha256 == source.ExpectedSha256) == 1;
        }
        catch (LocalProcessWorkspaceException exception) when (exception.StatusCode is 403 or 404) { return false; }
    }
}

// Initial new-table-only boundary. Occupied destinations are unsupported until the bulk
// pipeline enforces every resource/publication grant, RLS, field restrictions and masks.
// The execution coordinator must ALSO fence absence under its transaction/ownership lock.
internal sealed class DatasetImportDestinationAuthorizer(IHttpContextAccessor accessor, IConfiguration configuration,
    IDatabaseSessionFactory sessions) : IDatasetImportDestinationAuthorizer
{
    public async Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, string targetId,
        DatasetImportDestination destination, DatasetImportTaskOptions options, bool requireWrite,
        CancellationToken cancellationToken = default)
    {
        var transport = accessor.HttpContext;
        return transport is not null && await IsAuthorizedCoreAsync(transport.RequestServices, principal, targetId,
            destination, options, requireWrite, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> IsAuthorizedCoreAsync(IServiceProvider services, ClaimsPrincipal principal, string targetId,
        DatasetImportDestination destination, DatasetImportTaskOptions options, bool requireWrite,
        CancellationToken cancellationToken, Honua.Core.Features.Authorization.Domain.OperatorAuthorizationRequest? sealedOperation = null)
    {
        if (DataProviderNames.Normalize(configuration["DataSource:Provider"]) is not (DataProviderNames.Postgis or DataProviderNames.PostgreSql) ||
            !ImportTargetSchemaPolicy.IsAllowed(destination.Schema, configuration) ||
            options.LoadMode is not (ImportLoadMode.Append or ImportLoadMode.Replace) ||
            targetId != await services.GetRequiredService<IAgentWorkspaceStore>().GetTargetIdAsync().ConfigureAwait(false)) return false;
        if (!await ImportAdminAuthorization.IsAuthorizedAsync(services, principal, targetId, requireWrite,
            cancellationToken, sealedOperation).ConfigureAwait(false)) return false;
        if (!requireWrite) return true;
        await using var session = await sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var occupied = await session.QuerySingleOrDefaultAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND (c.relname = @table OR c.relname = @legacy))
            """, new Dictionary<string, object?> { ["schema"] = destination.Schema, ["table"] = ImportPhysicalTableName.Canonical(destination.Table),
                ["legacy"] = ImportPhysicalTableName.Legacy(destination.Table) is { Length: <= 63 } legacy ? legacy : null }, cancellationToken).ConfigureAwait(false);
        return !occupied;
    }
}
