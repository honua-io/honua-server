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
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

internal static class FolderEndpoints
{
    public static void MapFolderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .AddEndpointFilter<FolderReplayAuthorizationFilter>()
            .WithApiVersionSet().HasApiVersion(1, 0).WithTags("Admin", "Services").RequireAdminAuthorization();
        var list = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "folder.list");
        group.MapMethods(list.Path, [list.Method.Method], (Delegate)List).WithAdminOperation(list);
        var create = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "folder.create");
        group.MapMethods(create.Path, [create.Method.Method], Create).WithAdminOperation(create);
        var update = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "folder.update");
        group.MapMethods(update.Path, [update.Method.Method], Update).WithAdminOperation(update);
        var delete = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "folder.delete");
        group.MapMethods(delete.Path, [delete.Method.Method], Delete).WithAdminOperation(delete);
        var move = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "service.move");
        group.MapMethods(move.Path, [move.Method.Method], Move).WithAdminOperation(move);
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
        => Mutate(context, new FolderMutationCommand.Create(request.Name,
            request.TitleSpecified ? new FolderTitleChange(request.Title) : default));

    private static Task<IResult> Update(string id, FolderRequest request, HttpContext context)
        => Mutate(context, new FolderMutationCommand.Update(id, request.Name,
            request.TitleSpecified ? new FolderTitleChange(request.Title) : default));

    private static Task<IResult> Delete(string id, HttpContext context)
        => Mutate(context, new FolderMutationCommand.Delete(id));

    private static Task<IResult> Move(string serviceName, MoveServiceRequest request, HttpContext context)
        => Mutate(context, new FolderMutationCommand.Move(serviceName, request.FolderId));

    private static async Task<IResult> Mutate(HttpContext context, FolderMutationCommand command)
    {
        var scopeError = ValidateScope(context);
        if (scopeError is not null) return scopeError;
        var operation = context.GetEndpoint()?.Metadata.GetMetadata<AdminApiOperationCatalog.Definition>()
            ?? throw new InvalidOperationException("Folder mutation is missing its operation definition.");
        if (operation.OperationId != command.OperationId)
            throw new InvalidOperationException("Folder mutation does not match its operation definition.");
        var replay = context.Features.Get<FolderReplayServicePrincipal>();
        var execution = await FolderMutationService.ExecuteAsync(new FolderMutationExecution
        {
            Services = context.RequestServices,
            Principal = replay?.Admission?.Principal ?? context.User,
            Command = command,
            ExpectedEtag = context.Request.Headers.IfMatch.Count == 1 ? context.Request.Headers.IfMatch.ToString() : null,
            Namespace = context.Request.Query.ContainsKey("namespace") ? context.Request.Query["namespace"].ToString() : null,
            Replay = replay?.Admission,
            RequestCache = replay is null ? context.Items : null
        }, context.RequestAborted).ConfigureAwait(false);
        if (execution.Failure is not null) return MapFailure(context, execution.Failure);
        var saved = execution.Saved ?? throw new InvalidOperationException("Folder mutation did not return a saved snapshot.");
        var changed = execution.Mutation ?? throw new InvalidOperationException("Folder mutation did not return its result.");
        if (execution.Authorization is not null) MapServiceAuthorization(context, execution.Authorization);
        context.Response.Headers.ETag = saved.Etag;
        if (changed.Status == 204) return Results.NoContent();
        var folder = saved.Graph.Folders.Single(f => f.Metadata.Id == changed.Folder!.Metadata.Id);
        if (changed.Status == 201) context.Response.Headers.Location = $"{context.Request.PathBase}/api/v1/admin/folders/{folder.Metadata.Id}";
        return Results.Json(ApiResponse<MetadataV2Folder>.CreateSuccess(folder),
            FolderJsonContext.Default.ApiResponseMetadataV2Folder, statusCode: changed.Status);
    }

    private static IResult MapFailure(HttpContext context, FolderExecutionFailure failure) => failure switch
    {
        FolderExecutionFailure.Replay => Results.Problem(statusCode: 403, detail: FolderApprovalAuthorization.FreshProposalRequired),
        FolderExecutionFailure.Mutation mutation => mutation.Refusal.ToResult(),
        FolderExecutionFailure.Authorization authorization => OperatorApprovalGate.CreateAuthorizationDeniedResult(context, authorization.Decision)!,
        FolderExecutionFailure.Approval approval => context.RequestServices.GetRequiredService<OperatorApprovalGate>()
            .CreateApprovalDeniedResult(context, new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Catalog,
                Operation = OperatorOperation.Publish
            }, approval.Requirement)!,
        FolderExecutionFailure.AffectedServices affected => MapServiceAuthorization(context, affected.Result)!,
        _ => throw new InvalidOperationException("Unknown folder execution failure.")
    };

    internal static async Task<IResult?> AuthorizeAffectedServices(HttpContext context, MetadataV2Graph original, FolderMutation mutation)
    {
        var authorization = await FolderServiceAuthorization.EvaluateAsync(new FolderAuthorizationContext
        {
            Original = original,
            Mutation = mutation,
            Services = context.RequestServices,
            OperatorPrincipal = context.User,
            RequesterAccess = ServiceAccess(context),
            FolderTenant = Tenant(context)
        }, context.RequestAborted).ConfigureAwait(false);
        return MapServiceAuthorization(context, authorization);
    }

    private static IResult? MapServiceAuthorization(HttpContext context, FolderAuthorizationResult authorization)
    {
        foreach (var evaluation in authorization.RequesterEvaluations)
        {
            var denied = AccessPolicyHelpers.CreateServiceAccessDeniedResult(context, evaluation);
            if (denied is not null) return denied;
        }
        return authorization.Failure switch
        {
            null => null,
            FolderAuthorizationFailure.Tenant => Results.NotFound(),
            FolderAuthorizationFailure.Operator denied => OperatorApprovalGate.CreateAuthorizationDeniedResult(context, denied.Decision),
            FolderAuthorizationFailure.Requester denied => AccessPolicyHelpers.CreateServiceAccessDeniedResult(context, denied.Evaluation),
            _ => throw new InvalidOperationException("Unknown folder authorization failure.")
        };
    }

    internal static async Task<IResult?> RequireServiceAccessAsync(HttpContext context, MetadataV2Service service)
    {
        var decision = await AccessPolicyHelpers.EvaluateServiceAccessCoreAsync(ServiceAccess(context), service,
            context.RequestAborted).ConfigureAwait(false);
        return AccessPolicyHelpers.CreateServiceAccessDeniedResult(context, decision);
    }

    private static ServiceAccessContext ServiceAccess(HttpContext context)
    {
        var replay = context.Features.Get<FolderReplayServicePrincipal>();
        return new ServiceAccessContext
        {
            Services = context.RequestServices,
            Principal = replay?.Principal ?? context.User,
            TenantId = TenantScopeHelpers.ResolveRequestTenantId(context),
            Operation = AuthorizationOperation.Admin,
            RequestCache = replay is null ? context.Items : null
        };
    }

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
}

internal sealed class FolderRequest
{
    private string? _title;
    public string? Name { get; init; }
    public string? Title
    {
        get => _title;
        set { _title = value; TitleSpecified = true; }
    }
    [JsonIgnore]
    public bool TitleSpecified { get; private set; }
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
        Message = "Folder catalog mutation {OperationId} committed metadata revision {Revision}")]
    public static partial void Mutated(ILogger logger, string operationId, long revision);
}
