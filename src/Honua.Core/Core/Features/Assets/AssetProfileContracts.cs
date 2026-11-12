// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Assets;

public static class AssetProfileState
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Archived = "archived";

    public static IReadOnlyList<string> All { get; } = [Draft, Active, Archived];

    public static bool IsValid(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);
}

public static class AssetGeometryKind
{
    public const string Point = "point";
    public const string Line = "line";
    public const string Polygon = "polygon";
}

public sealed record AssetProfileFieldMap
{
    [JsonPropertyName("assetId")]
    public string? AssetId { get; init; }

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("lastInspected")]
    public string? LastInspected { get; init; }

    [JsonPropertyName("lastServiced")]
    public string? LastServiced { get; init; }
}

public sealed record AssetProfileWriteRequest
{
    [JsonPropertyName("commandId")]
    public Guid? CommandId { get; init; }

    [JsonPropertyName("expectedVersion")]
    public long? ExpectedVersion { get; init; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("layerId")]
    public int? LayerId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("fields")]
    public AssetProfileFieldMap? Fields { get; init; }

    [JsonPropertyName("conditionLabels")]
    public IReadOnlyList<string>? ConditionLabels { get; init; }

    [JsonPropertyName("inspectionFormId")]
    public string? InspectionFormId { get; init; }

    [JsonPropertyName("serviceFormId")]
    public string? ServiceFormId { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }
}

public sealed record AssetProfile
{
    [JsonPropertyName("profileId")]
    public required Guid ProfileId { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("geometryType")]
    public required string GeometryType { get; init; }

    [JsonPropertyName("fields")]
    public required AssetProfileFieldMap Fields { get; init; }

    [JsonPropertyName("conditionLabels")]
    public required IReadOnlyList<string> ConditionLabels { get; init; }

    [JsonPropertyName("inspectionFormId")]
    public string? InspectionFormId { get; init; }

    [JsonPropertyName("serviceFormId")]
    public string? ServiceFormId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("version")]
    public required long Version { get; init; }

    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedBy")]
    public required string UpdatedBy { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record AssetProfileCommandResponse
{
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("commandId")]
    public required Guid CommandId { get; init; }

    [JsonPropertyName("profile")]
    public required AssetProfile Profile { get; init; }
}

public sealed record AssetProfileListResult
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<AssetProfile> Items { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed record AssetProfileQuery(string ServiceId, Guid? After, int Limit);
