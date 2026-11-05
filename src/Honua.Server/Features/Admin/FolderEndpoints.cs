// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin;

internal static class FolderEndpoints
{
    public static void MapFolderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .AddEndpointFilter<FolderReplayAuthorizationFilter>()
            .WithApiVersionSet().HasApiVersion(1, 0).WithTags("Admin", "Services").RequireAdminAuthorization();
        group.MapGet("/folders", (Delegate)List);
        group.MapPost("/folders", Create);
        group.MapPatch("/folders/{id}", Update);
        group.MapDelete("/folders/{id}", Delete);
        group.MapPut("/services/{serviceName}/folder", Move);
    }

    private static async Task<IResult> List(HttpContext context)
    {
        var scopeError = ValidateScope(context);
        if (scopeError is not null) return scopeError;
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var denial = await gate.EvaluateAuthorizationAsync(context, OperatorResourceType.Catalog, OperatorOperation.Read).ConfigureAwait(false);
        if (denial is not null) return denial;
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.ETag = snapshot.Etag;
        var folders = snapshot.Graph.Folders.Where(f => !f.Deleted &&
            MetadataV2ServicePaths.SameTenant(f.Metadata.Tenant, Tenant(context))).ToArray();
        return Results.Ok(ApiResponse<MetadataV2Folder[]>.CreateSuccess(folders));
    }

    private static Task<IResult> Create(FolderRequest request, HttpContext context)
        => Mutate(context, graph =>
        {
            var folder = new MetadataV2Folder
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = request.Name ?? string.Empty,
                    Title = request.Title,
                    Tenant = Tenant(context)
                }
            };
            return new(MetadataV2FolderMutations.Create(graph, folder), folder, StatusCodes.Status201Created);
        });

    private static Task<IResult> Update(string id, FolderRequest request, HttpContext context)
        => Mutate(context, graph =>
        {
            var folder = FindFolder(graph, id, Tenant(context));
            var updated = folder with
            {
                Metadata = folder.Metadata with
                {
                    Name = request.Name ?? folder.Metadata.Name,
                    Title = request.TitleSpecified ? request.Title : folder.Metadata.Title
                }
            };
            return new(MetadataV2FolderMutations.Update(graph, updated), updated);
        });

    private static Task<IResult> Delete(string id, HttpContext context)
        => Mutate(context, graph =>
        {
            var folder = FindFolder(graph, id, Tenant(context));
            return new(MetadataV2FolderMutations.Delete(graph, id), folder, StatusCodes.Status204NoContent);
        });

    private static Task<IResult> Move(string serviceName, MoveServiceRequest request, HttpContext context)
        => Mutate(context, graph =>
        {
            var services = graph.Services.Where(s => string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (services.Length == 0 || services.Any(s =>
                !MetadataV2ServicePaths.SameTenant(s.Metadata.Tenant, Tenant(context)))) throw new KeyNotFoundException();
            if (!request.FolderId.IsSpecified) throw new ArgumentException("folderId is required; null selects root.");
            if (request.FolderId.Value is not null) FindFolder(graph, request.FolderId.Value, Tenant(context));
            var changed = MetadataV2FolderMutations.Move(graph, serviceName, request.FolderId.Value);
            return new(changed, null, StatusCodes.Status204NoContent) { ServiceIds = services.Select(s => s.Metadata.Id).ToArray() };
        });

    private static async Task<IResult> Mutate(HttpContext context, Func<MetadataV2Graph, FolderMutation> mutation)
    {
        var scopeError = ValidateScope(context);
        if (scopeError is not null) return scopeError;
        var denied = await AuthorizeMutation(context).ConfigureAwait(false);
        if (denied is not null) return denied;
        if (context.Request.Headers.IfMatch.Count != 1 || string.IsNullOrWhiteSpace(context.Request.Headers.IfMatch))
            return Results.Problem(statusCode: 428, detail: "The current snapshot ETag is required in If-Match.");
        var store = context.RequestServices.GetService<IMetadataV2GraphStore>();
        if (store is null)
            return Results.Problem(statusCode: 503, detail: "Folder mutations are unavailable on this read-only metadata catalog.");
        try
        {
            return await CommitMutationAsync(context, mutation, store).ConfigureAwait(false);
        }
        catch (MetadataV2GraphConcurrencyException) { return Results.Problem(statusCode: 412, detail: "The metadata snapshot has changed. Refresh and retry."); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400,
                detail: "Invalid folder input. Use an unreserved ASCII folder name beginning with a letter, and supply folderId (null for root) when moving a service.");
        }
        catch (InvalidOperationException) { return Results.Problem(statusCode: 409, detail: "Folder or public path conflicts with the current catalog or a permanent reservation."); }
    }

    private static async Task<IResult> CommitMutationAsync(
        HttpContext context, Func<MetadataV2Graph, FolderMutation> mutation, IMetadataV2GraphStore store)
    {
        var snapshot = await store.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        if (snapshot.Etag != context.Request.Headers.IfMatch.ToString())
            return Results.Problem(statusCode: 412, detail: "The metadata snapshot has changed. Refresh and retry.");
        var changed = mutation(snapshot.Graph);
        var denial = await AuthorizeAffectedServices(context, snapshot.Graph, changed).ConfigureAwait(false);
        if (denial is not null) return denial;
        var saved = await store.SaveAsync(changed.Graph, snapshot.Etag, context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.ETag = saved.Etag;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("FolderMutations");
        FolderAuditLog.Mutated(logger, context.Request.Method, saved.Graph.Revision);
        if (changed.Status == 204) return Results.NoContent();
        var folder = saved.Graph.Folders.Single(f => f.Metadata.Id == changed.Folder!.Metadata.Id);
        if (changed.Status == 201) context.Response.Headers.Location = $"{context.Request.PathBase}/api/v1/admin/folders/{folder.Metadata.Id}";
        return Results.Json(ApiResponse<MetadataV2Folder>.CreateSuccess(folder),
            FolderJsonContext.Default.ApiResponseMetadataV2Folder, statusCode: changed.Status);
    }

    private static async Task<IResult?> AuthorizeMutation(HttpContext context)
    {
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var denied = await gate.EvaluateAuthorizationAsync(context, OperatorResourceType.Catalog, OperatorOperation.Publish).ConfigureAwait(false);
        return denied ?? gate.EvaluateApproval(context, OperatorResourceType.Catalog, OperatorOperation.Publish);
    }

    private static async Task<IResult?> AuthorizeAffectedServices(HttpContext context, MetadataV2Graph original, FolderMutation mutation)
    {
        var changedFolders = original.Folders.Where(folder => !mutation.Graph.Folders.Any(candidate => SameFolder(folder, candidate)))
            .Select(folder => folder.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var affected = original.Services.Where(service => mutation.ServiceIds.Contains(service.Metadata.Id) ||
            !mutation.Graph.Services.Contains(service) || (service.FolderId is not null && changedFolders.Contains(service.FolderId)))
            .GroupBy(service => service.Metadata.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var services in affected)
        {
            var denied = await AuthorizeLogicalService(context, services).ConfigureAwait(false);
            if (denied is not null) return denied;
        }
        return null;
    }

    private static bool SameFolder(MetadataV2Folder first, MetadataV2Folder second)
        => first.Metadata.Id == second.Metadata.Id && first.Metadata.Name == second.Metadata.Name &&
           first.Metadata.Title == second.Metadata.Title && MetadataV2ServicePaths.SameTenant(first.Metadata.Tenant, second.Metadata.Tenant) &&
           first.Deleted == second.Deleted && first.PreviousNames.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(second.PreviousNames);

    private static async Task<IResult?> AuthorizeLogicalService(HttpContext context, IGrouping<string, MetadataV2Service> services)
    {
        if (services.Any(service => !MetadataV2ServicePaths.SameTenant(service.Metadata.Tenant, Tenant(context)))) return Results.NotFound();
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var denied = await gate.EvaluateAuthorizationAsync(context, OperatorResourceType.PublishedService,
            OperatorOperation.Update, services.Key).ConfigureAwait(false);
        if (denied is not null) return denied;
        foreach (var service in services)
        {
            denied = await AccessPolicyHelpers.RequireServiceAccessAsync(context, service, AuthorizationOperation.Admin,
                context.RequestAborted).ConfigureAwait(false);
            if (denied is not null) return denied;
        }
        return null;
    }

    private static MetadataV2Folder FindFolder(MetadataV2Graph graph, string id, string? tenant)
        => graph.Folders.FirstOrDefault(f => !f.Deleted && f.Metadata.Id == id &&
            MetadataV2ServicePaths.SameTenant(f.Metadata.Tenant, tenant)) ?? throw new KeyNotFoundException();

    private static IResult? ValidateScope(HttpContext context)
    {
        var selection = context.Request.Query["namespace"];
        if (selection.Count > 1 || !LayerPublicationNamespace.IsValid(selection.Count == 0 ? null : selection.ToString()))
            return Results.Problem(statusCode: 400, detail: "Invalid namespace. Use 1-128 ASCII letters, digits, '.', '_' or '-'.");
        if (selection.Count != 0 && Tenant(context) is null)
            return Results.Problem(statusCode: 400, detail: "Namespaced folder operations require a resolved tenant.");
        return null;
    }

    private static string? Tenant(HttpContext context)
        => context.Request.Query.ContainsKey("namespace")
            ? MetadataV2ServicePaths.NormalizeTenant(context.RequestServices.GetService<ITenantContext>()?.TenantId)
            : null;

    private sealed record FolderMutation(MetadataV2Graph Graph, MetadataV2Folder? Folder, int Status = 200)
    {
        public IReadOnlyList<string> ServiceIds { get; init; } = [];
    }
}

internal sealed class FolderRequest
{
    private string? _title;
    public string? Name { get; init; }
    public string? Title
    {
        get => _title;
        init { _title = value; TitleSpecified = true; }
    }
    [JsonIgnore]
    public bool TitleSpecified { get; private init; }
}
internal sealed record MoveServiceRequest(MetadataV2FolderSelection FolderId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FolderRequest))]
[JsonSerializable(typeof(MoveServiceRequest))]
[JsonSerializable(typeof(ApiResponse<MetadataV2Folder>))]
[JsonSerializable(typeof(ApiResponse<MetadataV2Folder[]>))]
internal sealed partial class FolderJsonContext : JsonSerializerContext;

internal static partial class FolderAuditLog
{
    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 9460, Level = LogLevel.Information,
        Message = "Folder catalog mutation {Method} committed metadata revision {Revision}")]
    public static partial void Mutated(ILogger logger, string method, long revision);
}
