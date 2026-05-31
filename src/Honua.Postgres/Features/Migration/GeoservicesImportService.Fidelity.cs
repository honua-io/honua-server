// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private MigrationFidelityClassificationRecord[] BuildServiceFidelityClassifications(
        string containerId,
        string serviceKey,
        string serviceDisplayName,
        string serviceType,
        string[] serviceCapabilities)
    {
        var identity = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ServiceIdentity);
        var capabilities = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ServiceCapabilities);
        var hasCapabilities = serviceCapabilities.Length > 0;

        var records = new List<MigrationFidelityClassificationRecord>
        {
            CreateFidelityRecord(
                $"{containerId}:identity",
                containerId,
                "service",
                "identity",
                serviceDisplayName,
                identity.AutomationStatus,
                identity.Code,
                identity.Reason,
                manualSteps: identity.ManualSteps,
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
                hasCapabilities ? capabilities.AutomationStatus : capabilities.UnsupportedAutomationStatus!,
                hasCapabilities ? capabilities.Code : capabilities.UnsupportedCode!,
                hasCapabilities ? capabilities.Reason : capabilities.UnsupportedReason!,
                manualSteps: hasCapabilities ? capabilities.ManualSteps : capabilities.UnsupportedManualSteps,
                metadata: hasCapabilities
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["capabilities"] = string.Join(",", serviceCapabilities)
                    }
                    : []),
        };

        return records.ToArray();
    }

    private MigrationFidelityClassificationRecord[] BuildResourceFidelityClassifications(
        MigrationInventoryResource resource,
        JsonElement resourceElement,
        MigrationInventoryStyle? style,
        IReadOnlyCollection<MigrationExternalDependency> dependencies)
    {
        var identity = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceIdentity);
        var capabilities = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceCapabilities);
        var fields = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceFields);
        var hasQuery = resource.Capabilities.Contains("Query", StringComparer.OrdinalIgnoreCase);
        var hasFields = resource.Fields.Length > 0;

        var records = new List<MigrationFidelityClassificationRecord>
        {
            CreateFidelityRecord(
                $"{resource.Id}:identity",
                resource.Id,
                resource.Kind,
                "identity",
                resource.Name,
                identity.AutomationStatus,
                identity.Code,
                identity.Reason,
                manualSteps: identity.ManualSteps),
            CreateFidelityRecord(
                $"{resource.Id}:capabilities",
                resource.Id,
                resource.Kind,
                "capabilities",
                resource.Name,
                hasQuery ? capabilities.AutomationStatus : capabilities.UnsupportedAutomationStatus!,
                hasQuery ? capabilities.Code : capabilities.UnsupportedCode!,
                hasQuery ? capabilities.Reason : capabilities.UnsupportedReason!,
                manualSteps: hasQuery ? capabilities.ManualSteps : capabilities.UnsupportedManualSteps,
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
                hasFields ? fields.AutomationStatus : fields.UnsupportedAutomationStatus!,
                hasFields ? fields.Code : fields.UnsupportedCode!,
                hasFields ? fields.Reason : fields.UnsupportedReason!,
                manualSteps: hasFields ? fields.ManualSteps : fields.UnsupportedManualSteps,
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fieldCount"] = resource.Fields.Length.ToString(CultureInfo.InvariantCulture)
                })
        };

        var domainFieldCount = resource.Fields.Count(field => !string.IsNullOrWhiteSpace(field.DomainType));
        if (domainFieldCount > 0)
        {
            var domains = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceDomains);
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:domains",
                resource.Id,
                "field-domain",
                "domains",
                resource.Name,
                domains.AutomationStatus,
                domains.Code,
                domains.Reason,
                domains.ManualSteps,
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["domainFieldCount"] = domainFieldCount.ToString(CultureInfo.InvariantCulture)
                }));
        }

        if (HasSubtypeMetadata(resourceElement))
        {
            var subtypes = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceSubtypes);
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:subtypes",
                resource.Id,
                "subtype",
                "subtypes",
                resource.Name,
                subtypes.AutomationStatus,
                subtypes.Code,
                subtypes.Reason,
                subtypes.ManualSteps));
        }

        if (HasRelationshipMetadata(resourceElement))
        {
            var relationships = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceRelationships);
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:relationships",
                resource.Id,
                "relationship",
                "relationships",
                resource.Name,
                relationships.AutomationStatus,
                relationships.Code,
                relationships.Reason,
                relationships.ManualSteps));
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
                MigrationFidelityAutomationStatuses.Automated,
                ImportCompatibilityCodes.Compatible,
                "Attachments are automatically copied to the Honua attachment store during import when ImportAttachments is enabled and auto-publish succeeds.",
                relatedIds: attachmentDependencyIds));
        }

        if (style != null)
        {
            var renderers = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceRenderers);
            records.Add(CreateFidelityRecord(
                $"{style.Id}:renderer",
                style.Id,
                "renderer",
                "renderers",
                style.Name,
                ToFidelityAutomationStatus(style.Compatibility.Level),
                style.Compatibility.Code ?? renderers.Code,
                style.Compatibility.Reason,
                style.Compatibility.ManualSteps,
                [resource.Id, .. style.ExternalDependencyIds],
                style.Metadata));
        }

        if (resourceElement.TryGetProperty("timeInfo", out var timeInfo) && timeInfo.ValueKind == JsonValueKind.Object)
        {
            var time = _constructCapabilityRegistry.ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceTimeMetadata);
            records.Add(CreateFidelityRecord(
                $"{resource.Id}:time-metadata",
                resource.Id,
                "time",
                "time-metadata",
                resource.Name,
                time.AutomationStatus,
                time.Code,
                time.Reason,
                time.ManualSteps,
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
