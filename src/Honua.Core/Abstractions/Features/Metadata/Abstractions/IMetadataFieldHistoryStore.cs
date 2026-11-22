// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Abstractions;

public interface IMetadataActorProvider
{
    string? GetCurrentActor();
}

public sealed record MetadataFieldHistoryEntry
{
    public long Id { get; init; }

    public long Revision { get; init; }

    public required string ItemKind { get; init; }

    public required string ItemId { get; init; }

    public required IReadOnlyList<string> FieldPath { get; init; }

    public JsonElement? OldValue { get; init; }

    public JsonElement? NewValue { get; init; }

    public required string Actor { get; init; }

    public DateTimeOffset ChangedAt { get; init; }
}

public sealed record MetadataFieldHistoryQuery
{
    public string? ItemKind { get; init; }

    public string? ItemId { get; init; }

    public string? FieldPrefix { get; init; }

    public long? BeforeId { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed record MetadataFieldHistoryPage
{
    public required IReadOnlyList<MetadataFieldHistoryEntry> Items { get; init; }

    public long? NextBeforeId { get; init; }

    public bool HasMore { get; init; }
}

public interface IMetadataFieldHistoryStore
{
    Task<MetadataFieldHistoryPage> ListAsync(
        MetadataFieldHistoryQuery query,
        CancellationToken cancellationToken = default);
}
