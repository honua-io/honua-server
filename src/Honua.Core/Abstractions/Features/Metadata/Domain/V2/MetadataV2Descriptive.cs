// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json.Serialization;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2ResponsibleParty : ExtensibleJsonRecord
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = MetadataV2DescriptiveCodes.PointOfContactRole;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("organization")]
    public string? Organization { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed record MetadataV2SpatialResolution : ExtensibleJsonRecord
{
    [JsonPropertyName("distanceMeters")]
    public double? DistanceMeters { get; init; }

    [JsonPropertyName("scaleDenominator")]
    public long? ScaleDenominator { get; init; }
}

// Code values from the ISO 19115-1 code lists, which ISO 19115-3 encodes.
public static class MetadataV2DescriptiveCodes
{
    public const string PointOfContactRole = "pointOfContact";

    public const string PublisherRole = "publisher";

    public static FrozenSet<string> TopicCategories { get; } = FrozenSet.ToFrozenSet(
    [
        "farming", "biota", "boundaries", "climatologyMeteorologyAtmosphere", "economy", "elevation",
        "environment", "geoscientificInformation", "health", "imageryBaseMapsEarthCover",
        "intelligenceMilitary", "inlandWaters", "location", "oceans", "planningCadastre", "society",
        "structure", "transportation", "utilitiesCommunication", "extraTerrestrial", "disaster",
    ], StringComparer.Ordinal);

    public static FrozenSet<string> MaintenanceFrequencies { get; } = FrozenSet.ToFrozenSet(
    [
        "continual", "daily", "weekly", "fortnightly", "monthly", "quarterly", "biannually", "annually",
        "asNeeded", "irregular", "notPlanned", "unknown", "periodic", "semimonthly", "biennially",
    ], StringComparer.Ordinal);

    public static FrozenSet<string> Roles { get; } = FrozenSet.ToFrozenSet(
    [
        "resourceProvider", "custodian", "owner", "user", "distributor", "originator", PointOfContactRole,
        "principalInvestigator", "processor", PublisherRole, "author", "sponsor", "coAuthor", "collaborator",
        "editor", "mediator", "rightsHolder", "contributor", "funder", "stakeholder",
    ], StringComparer.Ordinal);

    // The ISO 19139 (2007) code lists predate ISO 19115-1 and define only these values.
    public static FrozenSet<string> Iso19139Roles { get; } = FrozenSet.ToFrozenSet(
    [
        "resourceProvider", "custodian", "owner", "user", "distributor", "originator", PointOfContactRole,
        "principalInvestigator", "processor", PublisherRole, "author",
    ], StringComparer.Ordinal);

    public static FrozenSet<string> Iso19139TopicCategories { get; } =
        TopicCategories.Where(code => code is not ("extraTerrestrial" or "disaster")).ToFrozenSet(StringComparer.Ordinal);

    public static FrozenSet<string> Iso19139MaintenanceFrequencies { get; } =
        MaintenanceFrequencies.Where(code => code is not ("periodic" or "semimonthly" or "biennially"))
            .ToFrozenSet(StringComparer.Ordinal);
}

internal static class MetadataV2DescriptiveValidation
{
    public const int MaxStatementLength = 4096;

    public static void Validate(List<string> errors, string ownerLabel, MetadataV2ObjectMetadata metadata)
    {
        RequireCodes(errors, $"{ownerLabel} metadata.topicCategories", metadata.TopicCategories, MetadataV2DescriptiveCodes.TopicCategories);
        if (metadata.UpdateFrequency is { } frequency)
        {
            RequireCodes(errors, $"{ownerLabel} metadata.updateFrequency", [frequency], MetadataV2DescriptiveCodes.MaintenanceFrequencies);
        }
        if (metadata.Contacts.Any(contact => contact is null))
        {
            errors.Add($"{ownerLabel} metadata.contacts must not contain null entries.");
        }
        var contacts = metadata.Contacts.OfType<MetadataV2ResponsibleParty>().ToArray();
        RequireCodes(errors, $"{ownerLabel} metadata.contacts[].role", contacts.Select(contact => contact.Role).ToArray(), MetadataV2DescriptiveCodes.Roles);
        foreach (var contact in contacts)
        {
            RequireStatement(errors, $"{ownerLabel} metadata.contacts[].name", contact.Name);
            RequireStatement(errors, $"{ownerLabel} metadata.contacts[].organization", contact.Organization);
            RequireStatement(errors, $"{ownerLabel} metadata.contacts[].email", contact.Email);
            RequireStatement(errors, $"{ownerLabel} metadata.contacts[].url", contact.Url);
            MetadataV2GraphValidator.ValidateContactEmail(errors, $"{ownerLabel} metadata.contacts[].email", contact.Email);
            MetadataV2GraphValidator.ValidateContactUrl(errors, $"{ownerLabel} metadata.contacts[].url", contact.Url);
        }
        RequireStatement(errors, $"{ownerLabel} metadata.accessConstraints", metadata.AccessConstraints);
        RequireStatement(errors, $"{ownerLabel} metadata.fees", metadata.Fees);
        RequireStatement(errors, $"{ownerLabel} metadata.lineage", metadata.Lineage);
        if (metadata.TemporalResolution is { } duration && !IsIsoDuration(duration))
        {
            errors.Add($"{ownerLabel} metadata.temporalResolution must be an ISO 8601 duration such as 'P1D' or 'PT1H'.");
        }
        if (metadata.SpatialResolution is { DistanceMeters: <= 0 } or { ScaleDenominator: <= 0 })
        {
            errors.Add($"{ownerLabel} metadata.spatialResolution values must be positive.");
        }
    }

    private static void RequireCodes(List<string> errors, string field, IReadOnlyList<string> values, FrozenSet<string> allowed)
    {
        var unknown = values.Where(value => !allowed.Contains(value)).ToArray();
        if (unknown.Length > 0)
        {
            errors.Add($"{field} has unknown code value(s) {string.Join(", ", unknown.Select(value => $"'{value}'"))}.");
        }
    }

    private static void RequireStatement(List<string> errors, string field, string? value)
    {
        if (value is { Length: > MaxStatementLength } || (value is not null && value.Any(IsDisallowedControl)))
        {
            errors.Add($"{field} must not exceed {MaxStatementLength} characters or contain control characters.");
        }
    }

    // XML 1.0 carries tab, line feed and carriage return; every other control character makes the documents invalid.
    private static bool IsDisallowedControl(char character)
        => char.IsControl(character) && character is not ('\t' or '\n' or '\r');

    private static bool IsIsoDuration(string value)
    {
        try
        {
            _ = System.Xml.XmlConvert.ToTimeSpan(value);
            return value.StartsWith('P');
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return false;
        }
    }
}
