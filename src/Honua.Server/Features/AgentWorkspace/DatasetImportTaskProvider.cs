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
    : IDatasetImportTaskProvider
{
    public async Task<DatasetImportTask?> AuthorizeAsync(ClaimsPrincipal principal, string workspaceId,
        string taskId, long revision, string contentHash, bool requireWrite, CancellationToken cancellationToken = default)
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
        if (task is null || task.TaskId != taskId || task.OwnerActorId != actor || task.TenantId != tenant ||
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
            latestTask != task ||
            !await AgentCredentialAuthority.IsCurrentAsync(principal, transport.RequestServices, cancellationToken).ConfigureAwait(false)) return null;
        return task;
    }

    internal static async Task<bool> SourceAuthorizedAsync(HttpContext context, AgentWorkspaceDocument workspace,
        DatasetImportSource source, CancellationToken cancellationToken)
    {
        if (source.Kind == "browser-file") return source.Artifact is null;
        if (source.Kind != "local-process-artifact" || source.Artifact is not { } artifact ||
            LocalProcessWorkspaceAuthorization.TrustedAccess(context, workspace) is not { } access) return false;
        var gateway = context.RequestServices.GetService<ILocalProcessWorkspaceGateway>();
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
        if (transport is null || DataProviderNames.Normalize(configuration["DataSource:Provider"]) is not (DataProviderNames.Postgis or DataProviderNames.PostgreSql) ||
            !ImportTargetSchemaPolicy.IsAllowed(destination.Schema, configuration) ||
            options.LoadMode is not (ImportLoadMode.Append or ImportLoadMode.Replace) ||
            targetId != await transport.RequestServices.GetRequiredService<IAgentWorkspaceStore>().GetTargetIdAsync().ConfigureAwait(false)) return false;
        var context = new DefaultHttpContext { RequestServices = transport.RequestServices, User = principal, RequestAborted = cancellationToken };
        context.Request.Path = "/api/v1/admin/import/upload";
        if (!requireWrite)
            return await OperationAdminAuthorization.IsAuthorizedAsync(context, principal, OperationSideEffectClass.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (!await ImportAdminAuthorization.IsAuthorizedAsync(context, principal, cancellationToken).ConfigureAwait(false)) return false;
        await using var session = await sessions.OpenAsync(cancellationToken).ConfigureAwait(false);
        var occupied = await session.QuerySingleOrDefaultAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table)
            """, new Dictionary<string, object?> { ["schema"] = destination.Schema, ["table"] = destination.Table }, cancellationToken).ConfigureAwait(false);
        return !occupied;
    }
}
