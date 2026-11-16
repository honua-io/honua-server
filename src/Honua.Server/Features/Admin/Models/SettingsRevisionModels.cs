// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

// One recorded settings change. The stored value is never returned: provider secrets live in it.
public sealed class SettingsRevisionResponse
{
    public long RevisionId { get; init; }

    // baseline, created, updated or deleted.
    public required string Change { get; init; }

    public long? DocumentRevision { get; init; }

    public long Generation { get; init; }

    // The revision this change undid, when it was an undo.
    public long? RestoredFrom { get; init; }

    public DateTimeOffset ChangedAt { get; init; }
}

public sealed class UndoSettingsChangeRequest
{
    // The newest revision the caller saw; the undo is refused when a later change exists.
    public long? ExpectedRevisionId { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<SettingsRevisionResponse>>))]
[JsonSerializable(typeof(ApiResponse<SettingsRevisionResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(UndoSettingsChangeRequest))]
internal sealed partial class SettingsRevisionJsonContext : JsonSerializerContext
{
}
