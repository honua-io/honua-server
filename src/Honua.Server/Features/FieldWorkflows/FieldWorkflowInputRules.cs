// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Server.Features.FieldWorkflows;

// Reviewer text and list/export filters reach PostgreSQL text columns and parameters, so they are
// bounded and checked for storable text before the store is called. An unknown syncStatus used to
// map silently to "pending", and an inverted date range silently matched nothing.
internal static class FieldWorkflowInputRules
{
    internal const int MaxNameLength = 256;
    internal const int MaxTextLength = 4000;

    internal static string? FindAssigneeError(string? assignedTo)
        => PostgresTextValidation.FindSingleLineError("assignedTo", assignedTo, MaxNameLength);

    internal static string? FindNoteError(string? note)
        => PostgresTextValidation.FindMultiLineError("note", note, MaxTextLength);

    internal static string? FindCommentError(string? body)
        => PostgresTextValidation.FindMultiLineError("body", body, MaxTextLength);

    internal static string? FindFilterError(FieldReviewQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FindFilterError(new FilterInput
        {
            FormId = query.FormId,
            ServiceId = query.ServiceId,
            AssignedTo = query.AssignedTo,
            SyncStatus = query.SyncStatus,
            SubmittedFrom = query.SubmittedFrom,
            SubmittedTo = query.SubmittedTo,
        });
    }

    internal static string? FindFilterError(FieldExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FindFilterError(new FilterInput
        {
            FormId = request.FormId,
            ServiceId = request.ServiceId,
            AssignedTo = request.AssignedTo,
            SyncStatus = request.SyncStatus,
            SubmittedFrom = request.SubmittedFrom,
            SubmittedTo = request.SubmittedTo,
        });
    }

    private static string? FindFilterError(in FilterInput filter)
    {
        var textError = PostgresTextValidation.FindSingleLineError("formId", filter.FormId, MaxNameLength)
            ?? PostgresTextValidation.FindSingleLineError("serviceId", filter.ServiceId, MaxNameLength)
            ?? PostgresTextValidation.FindSingleLineError("assignedTo", filter.AssignedTo, MaxNameLength);
        if (textError is not null)
        {
            return textError;
        }

        if (filter.SyncStatus is not null && !FieldSyncStatus.IsValid(filter.SyncStatus))
        {
            return $"syncStatus must be one of: {string.Join(", ", FieldSyncStatus.All)}.";
        }

        return filter.SubmittedFrom > filter.SubmittedTo
            ? "submittedFrom must not be later than submittedTo."
            : null;
    }

    private readonly record struct FilterInput
    {
        public string? FormId { get; init; }

        public string? ServiceId { get; init; }

        public string? AssignedTo { get; init; }

        public string? SyncStatus { get; init; }

        public DateTimeOffset? SubmittedFrom { get; init; }

        public DateTimeOffset? SubmittedTo { get; init; }
    }
}
