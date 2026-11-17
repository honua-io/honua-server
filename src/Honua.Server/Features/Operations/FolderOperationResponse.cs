// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using OperatorAccessDecision = Honua.Core.Features.Security.Abstractions.AccessDecision;

namespace Honua.Server.Features.Operations;

internal sealed record FolderOperationResponse(int Status, string Payload, string? Etag = null)
{
    internal string? Location { get; init; }

    internal string? ApprovalRefusal { get; init; }

    internal Dictionary<string, string> Details()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["response"] = Payload };
        if (Status >= 400) details["statusCode"] = Status.ToString(CultureInfo.InvariantCulture);
        if (Etag is not null) details["etag"] = Etag;
        if (Location is not null) details["location"] = Location;
        return details;
    }

    internal static FolderOperationResponse From(FolderExecutionResult execution, string? pathBase)
    {
        if (execution.Failure is { } failure) return Failure(failure);
        var saved = execution.Saved ?? throw new InvalidOperationException("Folder mutation did not return a saved snapshot.");
        var mutation = execution.Mutation ?? throw new InvalidOperationException("Folder mutation did not return its result.");
        if (mutation.Status == 204) return new(204, string.Empty, saved.Etag);
        var folder = saved.Graph.Folders.Single(item => item.Metadata.Id == mutation.Folder!.Metadata.Id);
        var payload = JsonSerializer.Serialize(ApiResponse<MetadataV2Folder>.CreateSuccess(folder),
            FolderJsonContext.Default.ApiResponseMetadataV2Folder);
        return new(mutation.Status, payload, saved.Etag)
        {
            Location = mutation.Status == 201 ? $"{pathBase}/api/v1/admin/folders/{folder.Metadata.Id}" : null
        };
    }

    private static FolderOperationResponse Failure(FolderExecutionFailure failure) => failure switch
    {
        FolderExecutionFailure.Replay => HttpProblem(403, FolderApprovalAuthorization.FreshProposalRequired) with
        {
            ApprovalRefusal = FolderApprovalAuthorization.FreshProposalRequired
        },
        FolderExecutionFailure.Mutation mutation => mutation.Refusal.Status == 404
            ? new(404, string.Empty) : HttpProblem(mutation.Refusal.Status, mutation.Refusal.Detail) with
            {
                ApprovalRefusal = IsApprovalRefusal(mutation.Refusal) ? mutation.Refusal.Detail : null
            },
        FolderExecutionFailure.Authorization authorization => Authorization(authorization.Decision),
        FolderExecutionFailure.Approval approval => new(403, JsonSerializer.Serialize(new ApprovalDeniedProblemDetails
        {
            Type = "urn:honua:approval-required",
            Title = "Approval required",
            Status = 403,
            Detail = $"This operation requires approval (policy: {approval.Requirement.PolicyRef}).",
            PolicyRef = approval.Requirement.PolicyRef,
            ReasonCodes = approval.Requirement.ReasonCodes,
            ResourceType = "Catalog",
            Operation = "Publish"
        }, ApprovalGateJsonContext.Default.ApprovalDeniedProblemDetails)),
        FolderExecutionFailure.AffectedServices affected => AffectedServices(affected.Result),
        _ => throw new InvalidOperationException("Unknown folder execution failure.")
    };

    private static bool IsApprovalRefusal(FolderMutationFailure refusal)
        => refusal == FolderMutationFailure.ApprovalSnapshotChanged || refusal == FolderMutationFailure.RequesterAuthorizationChanged;

    private static FolderOperationResponse AffectedServices(FolderAuthorizationResult result)
    {
        foreach (var evaluation in result.RequesterEvaluations)
            if (!evaluation.Decision.IsAllowed) return Requester(evaluation);
        return result.Failure switch
        {
            FolderAuthorizationFailure.Tenant => new(404, string.Empty),
            FolderAuthorizationFailure.Operator denied => Authorization(denied.Decision),
            FolderAuthorizationFailure.Requester denied => Requester(denied.Evaluation),
            _ => throw new InvalidOperationException("Missing folder authorization failure.")
        };
    }

    private static FolderOperationResponse Requester(ServiceAccessEvaluation evaluation)
        => Problem(evaluation.Decision.RequiresAuthentication ? 401 : 403,
            evaluation.Decision.RequiresAuthentication ? AccessPolicyHelpers.AuthRequiredMessage : AccessPolicyHelpers.AccessForbiddenMessage);

    private static FolderOperationResponse Authorization(OperatorAccessDecision decision)
        => Problem(decision.RequiresAuthentication ? 401 : 403, decision.RequiresAuthentication
            ? "Authentication is required for this operation." : "You do not have permission to perform this operation.");

    internal static FolderOperationResponse Problem(int status, string detail)
        => Problem(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = status,
            Detail = detail,
            Type = "https://honua.io/problems/admin",
            Title = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status)
        });

    private static FolderOperationResponse HttpProblem(int status, string detail)
    {
        var problem = TypedResults.Problem(statusCode: status, detail: detail).ProblemDetails;
        problem.Title ??= Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
        return Problem(problem);
    }

    private static FolderOperationResponse Problem(Microsoft.AspNetCore.Mvc.ProblemDetails problem)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (problem.Type is not null) writer.WriteString("type", problem.Type);
            writer.WriteString("title", problem.Title);
            writer.WriteNumber("status", problem.Status!.Value);
            writer.WriteString("detail", problem.Detail);
            writer.WriteEndObject();
        }
        return new(problem.Status!.Value, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
