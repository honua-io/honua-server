// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Postgres.Features.Import;

internal sealed partial class GeoservicesImportService
{
    private static MigrationFidelityClassificationRecord[] BuildServiceFidelityClassifications(
        string containerId,
        string serviceKey,
        string serviceDisplayName,
        string serviceType,
        string[] serviceCapabilities)
    {
        var records = new List<MigrationFidelityClassificationRecord>
        {
            CreateFidelityRecord(
                $"{containerId}:identity",
                containerId,
                "service",
                "identity",
                serviceDisplayName,
                MigrationFidelityAutomationStatuses.Automated,
                ImportCompatibilityCodes.Compatible,
                "Service identity and source URL were captured for deterministic migration planning.",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["serviceKey"] = serviceKey,
                    ["serviceType"] = serviceType
                }),
            CreateFidelityRecord(
                $"{containerId}:capabilities",
                containerId,
                "service",
                "capabilities",
                serviceDisplayName,
                serviceCapabilities.Length > 0
                    ? MigrationFidelityAutomationStatuses.Automated
                    : MigrationFidelityAutomationStatuses.ManualReview,
                serviceCapabilities.Length > 0 ? ImportCompatibilityCodes.Compatible : ImportCompatibilityCodes.ManualReview,
                serviceCapabilities.Length > 0
                    ? "Service capabilities were captured from the ArcGIS service root."
                    : "Service capabilities were not advertised and require operator confirmation.",
                manualSteps: serviceCapabilities.Length > 0 ? [] : ["Confirm source service capabilities before migration."],
                metadata: serviceCapabilities.Length > 0
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["capabilities"] = string.Join(",", serviceCapabilities)
                    }
                    : []),
        };

        return records.ToArray();
    }

    private static MigrationFidelityClassificationRecord[] BuildResourceFidelityClassifications(
        MigrationInventoryResource resource,
        JsonElement resourceElement,
        MigrationInventoryStyle? style,
        IReadOnlyCollection<MigrationExternalDependency> dependencies)
    {
        var records = new List<MigrationFidelityClassificationRecord>
        {
            CreateFidelityRecord(
                $"{resource.Id}:identity",
                resource.Id,
                resource.Kind,
                "identity",
                resource.Name,
                MigrationFidelityAutomationStatuses.Automated,
                ImportCompatibilityCodes.Compatible,
                "Layer or table identity was captured with a stable source identifier."),
            CreateFidelityRecord(
                $"{resource.Id}:capabilities",
                resource.Id,
                resource.Kind,
                "capabilities",
                resource.Name,
                resource.Capabilities.Contains("Query", StringComparer.OrdinalIgnoreCase)
                    ? MigrationFidelityAutomationStatuses.Automated
                    : MigrationFidelityAutomationStatuses.Unsupported,
                resource.Capabilities.Contains("Query", StringComparer.OrdinalIgnoreCase)
                    ? ImportCompatibilityCodes.Compatible
                    : ImportCompatibilityCodes.ArcGisQueryCapabilityMissing,
                resource.Capabilities.Contains("Query", StringComparer.OrdinalIgnoreCase)
                    ? "Queryable resource capabilities were captured for import planning."
                    : "The resource does not advertise query capability.",
                manualSteps: resource.Capabilities.Contains("Query", StringComparer.OrdinalIgnoreCase)
                    ? []
                    : ["Enable query access or export the source data through another path before migration."],
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["capabilities"] = string.Join(",", resource.Capabilities)
                }),
            CreateFidelityRecord(
                $"{resource.Id}:fields",
                resource.Id,
                "field",
                "fields",
                resource.Name,
                resource.Fields.Length > 0
                    ? MigrationFidelityAutomationStatuses.Automated
                    : MigrationFidelityAutomationStatuses.ManualReview,
                resource.Fields.Length > 0 ? ImportCompatibilityCodes.Compatible : ImportCompatibilityCodes.ManualReview,
                resource.Fields.Length > 0
                    ? "Field names, aliases, source types, nullability, and compact domain metadata were captured."
                    : "No field metadata was advertised for this resource.",
                manualSteps: resource.Fields.Length > 0 ? [] : ["Confirm source fields before import."],
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fieldCount"] = resource.Fields.Length.ToString(CultureInfo.InvariantCulture)
                })
        };

        var domainFieldCount = resource.Fields.Count(field => !string.IsNullOrWhiteSpace(field.DomainType));
        if (domainFieldCount > 0)
        {
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:domains",
                resource.Id,
                "field-domain",
                "domains",
                resource.Name,
                MigrationFidelityAutomationStatuses.Assisted,
                ImportCompatibilityCodes.ManualReview,
                "Domain metadata was captured for review; target domain enforcement is not automated by this import slice.",
                ["Review coded-value and range domains before publishing target field configuration."],
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["domainFieldCount"] = domainFieldCount.ToString(CultureInfo.InvariantCulture)
                }));
        }

        if (HasSubtypeMetadata(resourceElement))
        {
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:subtypes",
                resource.Id,
                "subtype",
                "subtypes",
                resource.Name,
                MigrationFidelityAutomationStatuses.ManualReview,
                ImportCompatibilityCodes.ArcGisSubtypesManualReview,
                "Subtype metadata was detected and captured for operator review; automated subtype migration is not implemented.",
                ["Recreate subtype behavior or document an accepted gap before cutover."]));
        }

        if (HasRelationshipMetadata(resourceElement))
        {
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:relationships",
                resource.Id,
                "relationship",
                "relationships",
                resource.Name,
                MigrationFidelityAutomationStatuses.ManualReview,
                ImportCompatibilityCodes.ArcGisRelationshipsManualReview,
                "Relationship metadata was detected and captured for operator review; automated relationship migration is not implemented.",
                ["Map related layers or tables to target relationship configuration before cutover."]));
        }

        if (resource.HasAttachments == true)
        {
            var attachmentDependencyIds = dependencies
                .Where(static dependency => string.Equals(dependency.Kind, "attachments", StringComparison.Ordinal))
                .Select(static dependency => dependency.Id)
                .ToArray();
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:attachments",
                resource.Id,
                "attachment",
                "attachments",
                resource.Name,
                MigrationFidelityAutomationStatuses.ManualReview,
                ImportCompatibilityCodes.ArcGisAttachments,
                "Attachment capability was detected, but attachment content migration is not implemented by this slice.",
                ["Plan a separate attachment migration alongside the core data import."],
                relatedIds: attachmentDependencyIds));
        }

        if (style != null)
        {
            records.Add(CreateFidelityRecord(
                $"{style.Id}:renderer",
                style.Id,
                "renderer",
                "renderers",
                style.Name,
                ToFidelityAutomationStatus(style.Compatibility.Level),
                style.Compatibility.Code ?? ImportCompatibilityCodes.ManualReview,
                style.Compatibility.Reason,
                style.Compatibility.ManualSteps,
                [resource.Id, .. style.ExternalDependencyIds],
                style.Metadata));
        }

        if (resourceElement.TryGetProperty("timeInfo", out var timeInfo) && timeInfo.ValueKind == JsonValueKind.Object)
        {
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:time-metadata",
                resource.Id,
                "time",
                "time-metadata",
                resource.Name,
                MigrationFidelityAutomationStatuses.ManualReview,
                ImportCompatibilityCodes.ArcGisTimeMetadataManualReview,
                "Time metadata was detected and captured for operator review; automated temporal service configuration is not implemented.",
                ["Configure or waive target temporal metadata before cutover."],
                metadata: BuildTimeMetadata(timeInfo)));
        }

        return records
            .OrderBy(static record => record.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static MigrationFidelityClassificationRecord CreateFidelityRecord(
        string id,
        string sourceId,
        string kind,
        string category,
        string? name,
        string automationStatus,
        string code,
        string reason,
        IEnumerable<string>? manualSteps = null,
        IEnumerable<string>? relatedIds = null,
        IReadOnlyDictionary<string, string>? metadata = null)
        => new()
        {
            Id = $"classification:{id}",
            SourceId = sourceId,
            Kind = kind,
            Category = category,
            Name = name,
            AutomationStatus = automationStatus,
            Code = code,
            Reason = reason,
            ManualSteps = MigrationInventoryHelpers.NormalizeStrings(manualSteps),
            RelatedIds = MigrationInventoryHelpers.NormalizeStrings(relatedIds),
            Metadata = metadata == null
                ? []
                : metadata
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
        };

    private static string ToFidelityAutomationStatus(string compatibilityLevel)
    {
        if (string.Equals(compatibilityLevel, "incompatible", StringComparison.OrdinalIgnoreCase))
        {
            return MigrationFidelityAutomationStatuses.Unsupported;
        }

        return string.Equals(compatibilityLevel, "compatible", StringComparison.OrdinalIgnoreCase)
            ? MigrationFidelityAutomationStatuses.Assisted
            : MigrationFidelityAutomationStatuses.ManualReview;
    }

    private static bool HasSubtypeMetadata(JsonElement resourceElement)
        => HasNonEmptyArray(resourceElement, "types") ||
            HasNonEmptyArray(resourceElement, "subtypes") ||
            !string.IsNullOrWhiteSpace(GetOptionalStringProperty(resourceElement, "subtypeField"));

    private static bool HasRelationshipMetadata(JsonElement resourceElement)
        => HasNonEmptyArray(resourceElement, "relationships") ||
            HasNonEmptyArray(resourceElement, "relationshipInfos");

    private static bool HasNonEmptyArray(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Array &&
            property.GetArrayLength() > 0;

    private static Dictionary<string, string> BuildTimeMetadata(JsonElement timeInfo)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var propertyName in new[] { "startTimeField", "endTimeField", "trackIdField", "timeIntervalUnits" })
        {
            var value = GetOptionalStringProperty(timeInfo, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[propertyName] = value;
            }
        }

        return metadata.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
    }
}
