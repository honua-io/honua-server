// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Mrtr;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.Studio;

internal abstract partial class StudioDraftToolBase
{
    protected sealed record DraftUpdateResult(StudioPackageDraft? Draft, McpToolsCallResult? Continuation)
    {
        public McpToolsCallResult ToMcpResult() => Continuation
            ?? McpToolHelpers.SuccessResult(Draft!, StudioJsonContext.Default.StudioPackageDraft);
    }

    protected static Task<McpToolsCallResult?> TryResumeUpdateAsync(
        HttpContext httpContext, ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        ApprovalContinuation.TryResumeAsync(
            httpContext,
            new ApprovalRequester(StudioDraftMutationContext.UpdateOperationId, PolicyPrincipalId(principal, null))
            {
                OnFailed = async (proposal, operation, token) =>
                {
                    if (operation.Reason == StudioDraftGenerationConflictException.ConflictMessage
                        && proposal.Plan.ExecutionPayload is { } payload
                        && ReadApprovalDraftId(payload) is { } draftId)
                    {
                        throw await ReadGenerationConflictAsync(httpContext, principal, draftId, token).ConfigureAwait(false);
                    }
                },
                OnCompleted = async (operation, token) =>
                {
                    if (operation.Result?.Details?.TryGetValue("payload", out var payload) != true || payload is null)
                    {
                        throw new GeoprocessingPreconditionFailedException("The completed Studio draft result is unavailable.");
                    }

                    StudioPackageDraft draft;
                    try
                    {
                        draft = JsonSerializer.Deserialize(payload, StudioJsonContext.Default.StudioPackageDraft)
                            ?? throw new GeoprocessingPreconditionFailedException("The completed Studio draft result is unavailable.");
                    }
                    catch (JsonException)
                    {
                        throw new GeoprocessingPreconditionFailedException("The completed Studio draft result is unavailable.");
                    }

                    await RequireAuthorizedUpdateAsync(httpContext, principal, draft.DraftId, token).ConfigureAwait(false);
                    return McpToolHelpers.SuccessResult(draft, StudioJsonContext.Default.StudioPackageDraft);
                }
            }, cancellationToken);

    private static Guid? ReadApprovalDraftId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("draftId", out var draftId)
                && draftId.ValueKind == JsonValueKind.String && draftId.TryGetGuid(out var id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<StudioDraftGenerationConflictException> ReadGenerationConflictAsync(
        HttpContext httpContext, ClaimsPrincipal principal, Guid draftId, CancellationToken cancellationToken)
    {
        // Re-authorize the latest snapshot before returning its generation.
        var draft = await RequireAuthorizedUpdateAsync(httpContext, principal, draftId, cancellationToken).ConfigureAwait(false);
        return new StudioDraftGenerationConflictException(draft.Generation);
    }

    private static Task<StudioPackageDraft> RequireAuthorizedUpdateAsync(
        HttpContext httpContext, ClaimsPrincipal principal, Guid draftId, CancellationToken cancellationToken) =>
        RequireAuthorizedDraftAsync(httpContext, principal, RequireLifecycleService(httpContext), draftId,
            StudioAuthorizationOperation.UpdateDraft, OperatorOperation.Create, cancellationToken);
}
