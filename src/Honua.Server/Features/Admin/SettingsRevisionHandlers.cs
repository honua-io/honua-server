// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace Honua.Server.Features.Admin;

// Revision history and one-step undo for settings kept in configuration documents. Each owning endpoint
// group maps these, so its authorization, platform authority and entitlement gates apply unchanged.
internal static partial class SettingsRevisionHandlers
{
    internal sealed class SettingsRevisionLog;

    private const string HistoryUnavailable =
        "Settings history is kept only when settings are stored in PostgreSQL; this server keeps them in process memory.";

    public static async Task<Results<Ok<ApiResponse<IReadOnlyList<SettingsRevisionResponse>>>, NotFound<ApiResponse<object>>, Conflict<ApiResponse<object>>>>
        ListAsync(ConfigurationDocumentKey document, HttpContext context)
    {
        if (Documents(context) is not { } documents)
        {
            return TypedResults.Conflict(ApiResponse<object>.Failure(HistoryUnavailable));
        }
        var revisions = await documents.ListRevisionsAsync(document, context.RequestAborted).ConfigureAwait(false);
        if (revisions.Count == 0)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("No settings history exists for this item."));
        }
        IReadOnlyList<SettingsRevisionResponse> response = revisions.Select(ToResponse).ToArray();
        return TypedResults.Ok(ApiResponse<IReadOnlyList<SettingsRevisionResponse>>.CreateSuccess(response));
    }

    public static async Task<Results<Ok<ApiResponse<SettingsRevisionResponse>>, BadRequest<ApiResponse<object>>, Conflict<ApiResponse<object>>>>
        UndoAsync(ConfigurationUndoScope scope, UndoSettingsChangeRequest? request, HttpContext context)
    {
        if (request?.ExpectedRevisionId is not { } expected || expected <= 0)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(
                "expectedRevisionId is required: name the newest revision you saw in the settings history."));
        }
        if (Documents(context) is not { } documents)
        {
            return TypedResults.Conflict(ApiResponse<object>.Failure(HistoryUnavailable));
        }
        var result = await documents.UndoAsync(new ConfigurationUndoRequest(scope, expected), context.RequestAborted).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ConfigurationUndoOutcome.Undone:
                var restored = result.Revision!;
                var logger = context.RequestServices.GetRequiredService<ILogger<SettingsRevisionLog>>();
                SettingsChangeUndone(logger, scope.Document.Kind, scope.Document.Key, expected, restored.RevisionId);
                return TypedResults.Ok(ApiResponse<SettingsRevisionResponse>.CreateSuccess(ToResponse(restored)));
            case ConfigurationUndoOutcome.Superseded:
                return TypedResults.Conflict(ApiResponse<object>.Failure(
                    "A later change exists. Read the settings history and undo its newest revision instead."));
            case ConfigurationUndoOutcome.RecreateRefused:
                return TypedResults.Conflict(ApiResponse<object>.Failure(
                    "Undo cannot re-create this deleted item; create it again so its limits and entitlements are checked."));
            default:
                return TypedResults.Conflict(ApiResponse<object>.Failure(
                    "Nothing to undo: no change is recorded since settings history began for this item."));
        }
    }

    private static PostgresConfigurationDocuments? Documents(HttpContext context)
        => context.RequestServices.GetService<NpgsqlDataSource>() is null
            ? null
            : context.RequestServices.GetService<PostgresConfigurationDocuments>();

    private static SettingsRevisionResponse ToResponse(ConfigurationRevision revision) => new()
    {
        RevisionId = revision.RevisionId,
        Change = revision.Change,
        DocumentRevision = revision.DocumentRevision,
        Generation = revision.Generation,
        RestoredFrom = revision.RestoredFrom,
        ChangedAt = revision.ChangedAt,
    };

    [LoggerMessage(EventId = 4580, Level = LogLevel.Information,
        Message = "Undid settings revision {UndoneRevisionId} of {Kind}/{Key} as revision {RevisionId}")]
    private static partial void SettingsChangeUndone(ILogger logger, string kind, string key, long undoneRevisionId, long revisionId);
}
