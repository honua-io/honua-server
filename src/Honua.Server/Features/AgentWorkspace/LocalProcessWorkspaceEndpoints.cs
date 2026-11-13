// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Features.AgentWorkspace;

internal static class LocalProcessWorkspaceEndpoints
{
    internal static void MapLocalProcesses(this RouteGroupBuilder agent)
    {
        var group = agent.MapGroup("/workspaces/{workspaceId}/processes/local").RequireAuthorization();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                invocation.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(invocation).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Results.Json(new LocalProcessError("Invalid local process request."), LocalProcessJsonContext.Default.LocalProcessError,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            catch (LocalProcessWorkspaceException exception)
            {
                return Results.Json(new LocalProcessError(exception.Message), LocalProcessJsonContext.Default.LocalProcessError,
                    statusCode: exception.StatusCode);
            }
        });
        MapProject(group);
        MapSnapshots(group);
        MapRuns(group);
    }

    private static void MapProject(RouteGroupBuilder group)
    {
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
    }

    private static void MapSnapshots(RouteGroupBuilder group)
    {
        group.MapPost("/snapshots", (HttpContext context, ILocalProcessWorkspaceGateway gateway, LocalProcessSnapshotRequest request) =>
            Execute(context, (access, token) => gateway.SnapshotAsync(access, request, token), LocalProcessJsonContext.Default.LocalProcessSnapshot));
        group.MapGet("/snapshots/{snapshotId}", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.GetSnapshotAsync(access, Route(context, "snapshotId"), token), LocalProcessJsonContext.Default.LocalProcessSnapshot));
        group.MapGet("/snapshots/{snapshotId}/sources/{**path}", (HttpContext context, ILocalProcessWorkspaceGateway gateway) =>
            Execute(context, (access, token) => gateway.ReadSnapshotSourceAsync(access, Route(context, "snapshotId"), Route(context, "path"), token), LocalProcessJsonContext.Default.LocalProcessSourceFile));
    }

    private static void MapRuns(RouteGroupBuilder group)
    {
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
        var file = await gateway.ReadSourceAsync(access, Route(context, "path"), context.RequestAborted).ConfigureAwait(false);
        return Results.Json(file, LocalProcessJsonContext.Default.LocalProcessSourceFile);
    }

    private static async Task<IResult> ReadArtifact(HttpContext context, ILocalProcessWorkspaceGateway gateway)
    {
        var access = await Authorize(context).ConfigureAwait(false);
        var artifact = await gateway.ReadArtifactAsync(access, Route(context, "runId"), Route(context, "path"), context.RequestAborted).ConfigureAwait(false);
        return Results.Bytes(artifact.Content, "application/octet-stream");
    }

    private static Task<LocalProcessWorkspaceAccess> Authorize(HttpContext context) =>
        context.RequestServices.GetRequiredService<AgentWorkspaceAuthorization>()
            .AuthorizeAsync(context, Route(context, "workspaceId"));

    private static string Route(HttpContext context, string key) => context.Request.RouteValues[key]?.ToString() ?? string.Empty;
}
