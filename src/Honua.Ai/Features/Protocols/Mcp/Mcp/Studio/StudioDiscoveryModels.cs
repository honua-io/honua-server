// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Studio;

internal sealed record McpStudioListItemsArgument
{
    [JsonPropertyName("q")]
    public string? Query { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
}

internal sealed record McpStudioListVersionsArgument
{
    public Guid ItemId { get; init; }
    public int? Limit { get; init; }
    public int? BeforeVersionNumber { get; init; }
}

internal sealed record McpStudioVersionSummary(Guid VersionId, int VersionNumber, string ContentHash, DateTimeOffset CreatedAt, string? ChangeNote);
internal sealed record McpStudioVersionPage(Guid ItemId, IReadOnlyList<McpStudioVersionSummary> Versions, int? NextBeforeVersionNumber);
