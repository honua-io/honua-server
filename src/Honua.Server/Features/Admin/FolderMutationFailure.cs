// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Server.Features.Admin;

internal sealed record FolderMutationFailure(int Status, string Detail)
{
    internal static readonly FolderMutationFailure ReadOnlyCatalog = new(503, "Folder mutations are unavailable on this read-only metadata catalog.");
    internal static readonly FolderMutationFailure SnapshotChanged = new(412, "The metadata snapshot has changed. Refresh and retry.");
    internal static readonly FolderMutationFailure MissingPrecondition = new(428, "The current snapshot ETag is required in If-Match.");

    internal static bool Handles(Exception exception)
        => exception is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException;

    internal static FolderMutationFailure From(Exception exception) => exception switch
    {
        MetadataV2GraphConcurrencyException => SnapshotChanged,
        KeyNotFoundException => new(404, "The requested folder or service was not found."),
        JsonException => new(400, "The folder operation payload is invalid."),
        ArgumentException => new(400, "Invalid folder input. Use an unreserved ASCII folder name beginning with a letter, and supply folderId (null for root) when moving a service."),
        InvalidOperationException => new(409, "Folder or public path conflicts with the current catalog or a permanent reservation."),
        _ => throw new ArgumentException("The failure is not a folder mutation refusal.", nameof(exception))
    };

    internal IResult ToResult() => Status == 404 ? Results.NotFound() : Results.Problem(statusCode: Status, detail: Detail);
}
