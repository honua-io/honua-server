// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<ConsoleStacPublicationStatus>))]
public enum ConsoleStacPublicationStatus
{
    [JsonStringEnumMemberName("unpublished")]
    Unpublished,

    [JsonStringEnumMemberName("published")]
    Published,
}

public sealed record ConsoleStacPublicationState
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("status")]
    public ConsoleStacPublicationStatus Status { get; init; } = ConsoleStacPublicationStatus.Unpublished;

    [JsonPropertyName("collectionId")]
    public string? CollectionId { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("firstPublishedAt")]
    public DateTimeOffset? FirstPublishedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ConsoleOpenDataValidationSeverity>))]
public enum ConsoleOpenDataValidationSeverity
{
    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("warning")]
    Warning,
}

public sealed record ConsoleOpenDataValidationIssue
{
    [JsonPropertyName("field")]
    public required string Field { get; init; }

    [JsonPropertyName("severity")]
    public required ConsoleOpenDataValidationSeverity Severity { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record ConsoleOpenDataValidationResult
{
    [JsonPropertyName("isValid")]
    public required bool IsValid { get; init; }

    [JsonPropertyName("issues")]
    public IReadOnlyList<ConsoleOpenDataValidationIssue> Issues { get; init; } = Array.Empty<ConsoleOpenDataValidationIssue>();
}
