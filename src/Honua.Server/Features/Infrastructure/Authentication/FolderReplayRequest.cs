// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Admin;

namespace Honua.Server.Features.Infrastructure.Authentication;

internal static class FolderReplayRequest
{
    internal static bool Matches(EndpointFilterInvocationContext invocation, OperationProposal proposal)
    {
        if (proposal.Plan.ExecutionPayload is null) return false;
        try
        {
            var approvedRequest = ReadSealedRequest(proposal.Plan, proposal.OperationId, proposal.TenantId).Request;
            var approvedRoute = new DefaultHttpContext().Request;
            BindRoute(approvedRequest, approvedRoute);
            var request = invocation.HttpContext.Request;
            return request.Method == approvedRoute.Method && request.Path.Value == approvedRoute.Path.Value &&
                request.Headers.IfMatch.Count == 1 && request.Headers.IfMatch.ToString() == approvedRequest.Parameters.GetValueOrDefault("If-Match") &&
                MatchesNamespace(request, approvedRequest.Parameters.GetValueOrDefault("namespace")) &&
                MatchesBody(invocation, approvedRequest);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static OperationApprovalReplayMapping ReadSealedRequest(OperationProposalPlan plan, string? operationId, string? tenantId)
    {
        var payload = plan.ExecutionPayload is null ? null : JsonSerializer.Deserialize(plan.ExecutionPayload,
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload);
        if (payload is null || payload.Parameters is null || payload.OperationId != operationId || payload.DryRun ||
            !MetadataV2ServicePaths.SameTenant(payload.TenantId, tenantId))
            throw new ArgumentException("The sealed folder operation payload is invalid.");
        return new OperationApprovalReplayMapping
        {
            Request = payload.ToOperationRequest(),
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName
        };
    }

    internal static void BindRoute(OperationRequest operation, HttpRequest request)
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == operation.OperationId);
        request.Method = definition.Method.Method;
        request.Path = "/api/v1/admin" + AdminApiOperationExecutor.BindPath(definition, operation);
        request.QueryString = operation.Parameters.TryGetValue("namespace", out var selection) && selection is not null
            ? QueryString.Create("namespace", selection) : QueryString.Empty;
    }

    private static bool MatchesNamespace(HttpRequest request, string? approvedNamespace)
        => approvedNamespace is null ? request.Query.Count == 0 :
            request.Query.Count == 1 && request.Query["namespace"].Count == 1 && request.Query["namespace"] == approvedNamespace;

    private static bool MatchesBody(EndpointFilterInvocationContext invocation, OperationRequest approvedRequest)
    {
        if (approvedRequest.OperationId == "folder.delete") return true;
        if (approvedRequest.OperationId == "service.move")
        {
            var body = invocation.Arguments.OfType<MoveServiceRequest>().SingleOrDefault();
            return body is not null && body.FolderId.IsSpecified && approvedRequest.Parameters.TryGetValue("folderId", out var approvedFolderId) &&
                body.FolderId.Value == approvedFolderId;
        }
        var folder = invocation.Arguments.OfType<FolderRequest>().SingleOrDefault();
        return folder is not null && folder.Name == approvedRequest.Parameters.GetValueOrDefault("name") &&
            folder.TitleSpecified == approvedRequest.Parameters.ContainsKey("title") && folder.Title == approvedRequest.Parameters.GetValueOrDefault("title");
    }
}
