// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Models;

public sealed class UpdateOpenDataPageRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; init; }

    [JsonPropertyName("contactName")]
    public string? ContactName { get; init; }

    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("landingPage")]
    public string? LandingPage { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("distributions")]
    public IReadOnlyList<ConsoleOpenDataDistribution>? Distributions { get; init; }

    [JsonPropertyName("spatialExtent")]
    public ConsoleSpatialExtent? SpatialExtent { get; init; }

    [JsonPropertyName("temporalExtent")]
    public ConsoleTemporalExtent? TemporalExtent { get; init; }

    [JsonPropertyName("provenanceRefs")]
    public IReadOnlyList<string>? ProvenanceRefs { get; init; }
}

public sealed class ConsoleOpenDataEligibilityResponse
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    [JsonPropertyName("eligible")]
    public required bool Eligible { get; init; }

    [JsonPropertyName("reasonCode")]
    public required string ReasonCode { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    [JsonPropertyName("hasPage")]
    public required bool HasPage { get; init; }
}

public sealed class ConsoleOpenDataPageResponse
{
    [JsonPropertyName("page")]
    public required ConsoleOpenDataPage Page { get; init; }

    [JsonPropertyName("eligibility")]
    public required ConsoleOpenDataEligibilityResponse Eligibility { get; init; }

    [JsonPropertyName("stacPublication")]
    public required ConsoleStacPublicationState StacPublication { get; init; }

    [JsonPropertyName("dcatValidation")]
    public required ConsoleOpenDataValidationResult DcatValidation { get; init; }
}

public sealed class ConsoleDcatExportResponse
{
    [JsonPropertyName("catalog")]
    public required DcatCatalog Catalog { get; init; }

    [JsonPropertyName("validation")]
    public required ConsoleOpenDataValidationResult Validation { get; init; }
}
