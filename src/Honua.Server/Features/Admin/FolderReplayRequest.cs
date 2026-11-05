// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

internal static class FolderReplayRequest
{
    internal static bool IsFolderMutation(string operationId)
        => operationId is "folder.create" or "folder.update" or "folder.delete" or "service.move";

    internal static string ProposalGrant(string proposalId) => $"admin:operation:proposal:{proposalId}";

    internal static bool Matches(EndpointFilterInvocationContext invocation, OperationProposal proposal)
    {
        if (proposal.Plan.ExecutionPayload is null) return false;
        try
        {
            var payload = JsonSerializer.Deserialize(proposal.Plan.ExecutionPayload,
                AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload);
            if (payload is null || payload.OperationId != proposal.OperationId || payload.DryRun ||
                !MetadataV2ServicePaths.SameTenant(payload.TenantId, proposal.TenantId)) return false;
            var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == payload.OperationId);
            var request = invocation.HttpContext.Request;
            return request.Method == definition.Method.Method &&
                request.Path.Value == "/api/v1/admin" + AdminApiOperationExecutor.BindPath(definition, payload.ToOperationRequest()) &&
                request.Headers.IfMatch.Count == 1 && request.Headers.IfMatch.ToString() == payload.Parameters.GetValueOrDefault("If-Match") &&
                MatchesNamespace(request, payload.Parameters.GetValueOrDefault("namespace")) &&
                MatchesBody(invocation, payload);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool MatchesNamespace(HttpRequest request, string? approvedNamespace)
        => approvedNamespace is null ? request.Query.Count == 0 :
            request.Query.Count == 1 && request.Query["namespace"].Count == 1 && request.Query["namespace"] == approvedNamespace;

    private static bool MatchesBody(EndpointFilterInvocationContext invocation, AdminApiOperationApprovalPayload payload)
    {
        if (payload.OperationId == "folder.delete") return true;
        if (payload.OperationId == "service.move")
        {
            var body = invocation.Arguments.OfType<MoveServiceRequest>().SingleOrDefault();
            return body is not null && body.FolderId.IsSpecified && payload.Parameters.ContainsKey("folderId") &&
                body.FolderId.Value == payload.Parameters["folderId"];
        }
        var folder = invocation.Arguments.OfType<FolderRequest>().SingleOrDefault();
        return folder is not null && folder.Name == payload.Parameters.GetValueOrDefault("name") &&
            folder.TitleSpecified == payload.Parameters.ContainsKey("title") && folder.Title == payload.Parameters.GetValueOrDefault("title");
    }
}
