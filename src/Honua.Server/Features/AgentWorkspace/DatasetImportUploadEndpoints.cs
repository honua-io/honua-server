// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Import.Abstractions;
using Honua.Import.FileImport.Resumable;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.AgentWorkspace;

internal static class DatasetImportUploadEndpoints
{
    internal static void MapDatasetImportUploads(this RouteGroupBuilder agent)
    {
        var services = ((IEndpointRouteBuilder)agent).ServiceProvider.GetService<IServiceProviderIsService>();
        if (services is null || !services.IsService(typeof(ImportUploadCoordinator)) ||
            !services.IsService(typeof(IImportStorageBudget)) || !services.IsService(typeof(ImportScratchStorage)) ||
            !services.IsService(typeof(DatasetImportUploadTransportOptions))) return;

        var upload = agent.MapGroup("/workspaces/{workspaceId}/dataset-import-tasks/{taskId}/versions/{revision:long}/upload")
            .RequireAuthorization();
        upload.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (!Configured(context)) return Problem(context, 503, "Task-bound upload transport is not configured.");
            try { return await next(invocation).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) { return Problem(context, 404, "The original import task is unavailable."); }
            catch (InvalidDataException) { return Problem(context, 400, "The upload bytes do not match the original length or SHA-256."); }
            catch (ArgumentException) { return Problem(context, 400, "A valid original task, version and content binding are required."); }
            catch (InvalidOperationException) { return Problem(context, 409, "The upload state or available storage changed. Read the original receipt before continuing."); }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            { return Problem(context, 413, "The upload chunk exceeds the canonical bounded body limit."); }
            catch (IOException) { return Problem(context, 503, "The transfer outcome is unknown. Read the original upload receipt before retrying."); }
        });
        upload.MapGet("", Read);
        upload.MapGet("/capabilities", Capabilities);
        upload.MapPost("", Create).Accepts<CreateDatasetImportUpload>("application/json");
        upload.MapPut("/chunks", Append).Accepts<Stream>("application/octet-stream")
            .WithMetadata(new DatasetImportChunkBodyLimit());
        upload.MapPost("/seal", Seal).Accepts<MutateDatasetImportUpload>("application/json");
        upload.MapPost("/source", RetainSource).Accepts<MutateDatasetImportUpload>("application/json");
        upload.MapDelete("", Cancel);
    }

    private static bool Configured(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<DatasetImportUploadTransportOptions>();
        var scratch = context.RequestServices.GetRequiredService<ImportScratchStorage>();
        var limits = context.RequestServices.GetService<IOptions<LimitsOptions>>()?.Value;
        return options.IsConfigured && scratch.IsBoundToDirectory(options.ScratchDirectory) &&
            limits is not null && limits.Edits.MaxPayloadSize >= DatasetImportUploadTransportOptions.MaximumChunkBytes;
    }

    private static ImportUploadTaskReference Reference(string workspaceId, string taskId, long revision, string? contentHash)
    {
        if (!DatasetImportTaskState.ValidAuthorityScope(workspaceId, taskId, revision, contentHash!))
            throw new ArgumentException("An immutable original task binding is required.");
        return new(workspaceId, taskId, revision, contentHash!);
    }

    private static ImportUploadCoordinator Coordinator(HttpContext context)
        => context.RequestServices.GetRequiredService<ImportUploadCoordinator>();

    private static ImportUploadTaskAuthority Authority(HttpContext context)
        => new(context.RequestServices.GetRequiredService<IDatasetImportTaskProvider>(),
            context.RequestServices.GetService<IDatasetImportContributorAuthorizer>());

    private static async Task<IResult> Read(string workspaceId, string taskId, long revision, string contentHash, HttpContext context)
    {
        var reference = Reference(workspaceId, taskId, revision, contentHash);
        var session = await Coordinator(context).GetAsync(context.User, reference, context.RequestAborted).ConfigureAwait(false);
        return session is null ? Results.NotFound() : Receipt(session);
    }

    private static async Task<IResult> Capabilities(string workspaceId, string taskId, long revision, string contentHash, HttpContext context)
    {
        var reference = Reference(workspaceId, taskId, revision, contentHash);
        _ = await Authority(context).AuthorizeAsync(context.User, reference, false, context.RequestAborted).ConfigureAwait(false);
        var options = context.RequestServices.GetRequiredService<DatasetImportUploadTransportOptions>();
        return Results.Json(new DatasetImportUploadCapabilities(DatasetImportUploadTransportOptions.MaximumChunkBytes,
            options.EffectiveMaximumFileBytes), AgentWorkspaceJsonContext.Default.DatasetImportUploadCapabilities);
    }

    private static async Task<IResult> Create(string workspaceId, string taskId, long revision, HttpContext context)
    {
        var parsed = await DatasetImportTaskEndpoints.ReadBoundedAsync(context, AgentWorkspaceJsonContext.Default.CreateDatasetImportUpload).ConfigureAwait(false);
        if (parsed.Error is not null) return parsed.Error;
        var reference = Reference(workspaceId, taskId, revision, parsed.Request!.TaskContentHash);
        var coordinator = Coordinator(context);
        // Read-only recovery precedes new-admission policy and never creates a second session.
        var original = await coordinator.GetAsync(context.User, reference, context.RequestAborted).ConfigureAwait(false);
        if (original is not null) return Receipt(original);
        var authorized = await Authority(context).AuthorizeAsync(context.User, reference, false, context.RequestAborted).ConfigureAwait(false);
        var options = context.RequestServices.GetRequiredService<DatasetImportUploadTransportOptions>();
        if (authorized.Task.Source.TotalBytes > options.EffectiveMaximumFileBytes)
            return Problem(context, 413, "The original file exceeds this host's configured transfer or scratch allowance.");
        return Receipt(await coordinator.CreateAsync(context.User, reference, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> Append(string workspaceId, string taskId, long revision, string contentHash,
        long expectedVersion, long offset, string sha256, HttpContext context)
    {
        var reference = Reference(workspaceId, taskId, revision, contentHash);
        if (expectedVersion < 1 || offset < 0) throw new ArgumentException("A positive original version and offset are required.");
        RedisImportUploadStore.ValidateDigest(sha256);
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return Problem(context, 415, "A bounded application/octet-stream chunk is required.");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false } && (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > DatasetImportUploadTransportOptions.MaximumChunkBytes))
            feature.MaxRequestBodySize = DatasetImportUploadTransportOptions.MaximumChunkBytes;
        // Canonical staging verifies exact bounded actual bytes + hash, including unknown-length bodies,
        // after current task/contributor authorization and before any durable chunk acceptance.
        return Receipt(await Coordinator(context).AppendAsync(context.User, reference, expectedVersion, offset,
            context.Request.Body, sha256, context.RequestAborted).ConfigureAwait(false));
    }

    private static Task<IResult> Seal(string workspaceId, string taskId, long revision, HttpContext context)
        => Mutate(workspaceId, taskId, revision, context, retainSource: false);

    private static Task<IResult> RetainSource(string workspaceId, string taskId, long revision, HttpContext context)
        => Mutate(workspaceId, taskId, revision, context, retainSource: true);

    private static async Task<IResult> Mutate(string workspaceId, string taskId, long revision, HttpContext context, bool retainSource)
    {
        var parsed = await DatasetImportTaskEndpoints.ReadBoundedAsync(context, AgentWorkspaceJsonContext.Default.MutateDatasetImportUpload).ConfigureAwait(false);
        if (parsed.Error is not null) return parsed.Error;
        var request = parsed.Request!;
        var reference = Reference(workspaceId, taskId, revision, request.TaskContentHash);
        if (request.ExpectedVersion < 1) throw new ArgumentException("A positive original version is required.");
        var coordinator = Coordinator(context);
        var options = context.RequestServices.GetRequiredService<DatasetImportUploadTransportOptions>();
        var session = retainSource
            ? await coordinator.RetainSourceAsync(context.User, reference, request.ExpectedVersion,
                options.ScratchDirectory, options.MaximumScratchBytes, context.RequestAborted).ConfigureAwait(false)
            : await coordinator.VerifyAndSealAsync(context.User, reference, request.ExpectedVersion, context.RequestAborted).ConfigureAwait(false);
        return Receipt(session);
    }

    private static async Task<IResult> Cancel(string workspaceId, string taskId, long revision, string contentHash,
        long expectedVersion, HttpContext context)
    {
        var reference = Reference(workspaceId, taskId, revision, contentHash);
        if (expectedVersion < 1) throw new ArgumentException("A positive original version is required.");
        return Receipt(await Coordinator(context).CancelAsync(context.User, reference, expectedVersion, context.RequestAborted).ConfigureAwait(false));
    }

    private static IResult Receipt(ImportUploadSession session)
        => Results.Json(new DatasetImportUploadReceipt(session.Scope.TaskId, session.Scope.TaskRevision, session.IntentSha256,
            session.Id, session.Version, session.State switch { ImportUploadState.Receiving => "receiving", ImportUploadState.Sealed => "sealed", ImportUploadState.Cancelled => "cancelled", _ => throw new InvalidOperationException() },
            session.AcceptedBytes, session.TotalBytes, session.ChunkBytes, session.ChunkCount, session.ExpiresAt, session.ContentSha256,
            session.StagedSource is { } retained ? new(retained.Bytes, retained.Sha256, retained.ExpiresAt) : null),
            AgentWorkspaceJsonContext.Default.DatasetImportUploadReceipt);

    private static IResult Problem(HttpContext context, int status, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);
}
