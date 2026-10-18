// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;

namespace Honua.Server.Features.AgentWorkspace;

internal static class LocalProcessWorkspaceEndpoints
{
    internal static void MapLocalProcesses(this RouteGroupBuilder agent)
    {
        var group = agent.MapGroup("/workspaces/{workspaceId}/processes/local");
        group.MapGet("/project/identity", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.GetIdentityAsync(access, token), LocalProcessJsonContext.Default.LocalProcessProjectIdentity));
        group.MapPost("/project/bind", (HttpContext context, ILocalProcessWorkspaceGateway gateway, LocalProcessBindRequest request) =>
            Execute(context, (access, token) => gateway.BindAsync(access, request.ProjectId, token), LocalProcessJsonContext.Default.LocalProcessProjectIdentity));
        group.MapDelete("/project/bindings/{bindingId}", Revoke);
        group.MapGet("/project", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.GetProjectAsync(access, token), LocalProcessJsonContext.Default.LocalProcessProject));
        group.MapGet("/sources/{**path}", ReadSource);
        group.MapPut("/sources/{**path}", (HttpContext context, ILocalProcessWorkspaceGateway gateway, LocalProcessSourceWrite request) =>
            Execute(context, (access, token) => gateway.WriteSourceAsync(access, Route(context, "path"),
                context.Request.Headers.IfMatch.ToString().Trim('"'), request, token), LocalProcessJsonContext.Default.LocalProcessSourceWriteReceipt));
        group.MapPost("/snapshots", (HttpContext context, ILocalProcessWorkspaceGateway gateway, LocalProcessSnapshotRequest request) =>
            Execute(context, (access, token) => gateway.SnapshotAsync(access, request, token), LocalProcessJsonContext.Default.LocalProcessSnapshot));
        group.MapGet("/snapshots/{snapshotId}", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.GetSnapshotAsync(access, Route(context, "snapshotId"), token), LocalProcessJsonContext.Default.LocalProcessSnapshot));
        group.MapGet("/snapshots/{snapshotId}/sources/{**path}", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.ReadSnapshotSourceAsync(access, Route(context, "snapshotId"), Route(context, "path"), token), LocalProcessJsonContext.Default.LocalProcessSourceFile));
        group.MapPost("/runs", (HttpContext context, ILocalProcessWorkspaceGateway gateway, LocalProcessRunRequest request) =>
            Execute(context, (access, token) => gateway.RunAsync(access, request, token), LocalProcessJsonContext.Default.LocalProcessRun));
        group.MapGet("/runs", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.ListRunsAsync(access, token), LocalProcessJsonContext.Default.LocalProcessRunList));
        group.MapGet("/runs/{runId}", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.GetRunAsync(access, Route(context, "runId"), token), LocalProcessJsonContext.Default.LocalProcessRun));
        group.MapPost("/runs/{runId}/cancel", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.CancelAsync(access, Route(context, "runId"), token), LocalProcessJsonContext.Default.LocalProcessRun));
        group.MapGet("/commands/{commandId}", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.GetCommandAsync(access, Route(context, "commandId"), token), LocalProcessJsonContext.Default.LocalProcessRun));
        group.MapGet("/runs/{runId}/artifacts/{**path}", ReadArtifact);
    }

    private static async Task<IResult> Execute<T>(HttpContext context,
        Func<LocalProcessWorkspaceAccess, CancellationToken, Task<T>> action, JsonTypeInfo<T> type)
    {
        var access = await Authorize(context).ConfigureAwait(false);
        var result = await action(access, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, type);
    }

    private static async Task<IResult> Revoke(HttpContext context, ILocalProcessWorkspaceGateway gateway)
    {
        var access = await Authorize(context).ConfigureAwait(false);
        if (Route(context, "bindingId") != access.WorkspaceId) return Results.Forbid();
        await gateway.RevokeAsync(access, context.RequestAborted).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ReadSource(HttpContext context, ILocalProcessWorkspaceGateway gateway)
    {
        var access = await Authorize(context).ConfigureAwait(false);
        LocalProcessSourceFile file;
        if (access.Delegated && context.Request.Query["current"] != "true")
        {
            var document = await context.RequestServices.GetRequiredService<IAgentWorkspaceStore>()
                .GetAsync(access.WorkspaceId).ConfigureAwait(false);
            var turnId = context.Request.Headers["X-Honua-Turn"].ToString();
            if (document is null || !document.Turns.TryGetValue(turnId, out var turn) || turn.LocalProcessSnapshot is not { } snapshot)
                throw new LocalProcessWorkspaceException(404, "The captured local-process turn is no longer available.");
            file = await gateway.ReadSnapshotSourceAsync(access, snapshot.SnapshotId, Route(context, "path"), context.RequestAborted).ConfigureAwait(false);
        }
        else file = await gateway.ReadSourceAsync(access, Route(context, "path"), context.RequestAborted).ConfigureAwait(false);
        return Results.Json(file, LocalProcessJsonContext.Default.LocalProcessSourceFile);
    }

    private static async Task<IResult> ReadArtifact(HttpContext context, ILocalProcessWorkspaceGateway gateway)
    {
        var access = await Authorize(context).ConfigureAwait(false);
        var artifact = await gateway.ReadArtifactAsync(access, Route(context, "runId"), Route(context, "path"), context.RequestAborted).ConfigureAwait(false);
        return Results.Bytes(artifact.Content, "application/octet-stream");
    }

    private static Task<LocalProcessWorkspaceAccess> Authorize(HttpContext context) => LocalProcessWorkspaceAuthorization.AuthorizeAsync(
        context, Route(context, "workspaceId"), context.Request.Headers["X-Honua-Turn"].FirstOrDefault(), HttpMethods.IsGet(context.Request.Method));

    private static string Route(HttpContext context, string key) => context.Request.RouteValues[key]?.ToString() ?? string.Empty;
}
