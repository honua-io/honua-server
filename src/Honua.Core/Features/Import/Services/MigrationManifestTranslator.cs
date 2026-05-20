// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Translates source inventory artifacts into deterministic migration manifests.
/// </summary>
public static partial class MigrationManifestTranslator
{
    /// <summary>
    /// Translate source inventory into a target manifest artifact.
    /// </summary>
    /// <param name="inventory">Source inventory artifact.</param>
    /// <param name="options">Optional target naming options.</param>
    /// <returns>Deterministic manifest artifact.</returns>
    public static MigrationManifestArtifact Translate(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestTranslationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var targetServiceName = NormalizeName(
            options?.TargetServiceName ?? inventory.Source.DisplayName,
            fallback: "migration-service");
        var targetResources = new List<MigrationManifestTargetResource>();
        var styleActions = new List<MigrationManifestStyleAction>();
        var servicePlans = new List<MigrationManifestServicePlan>();
        var identityRemaps = new List<MigrationManifestIdentityRemap>();
        var manualReviewItems = new List<MigrationManifestReviewItem>();
        var unsupportedItems = new List<MigrationManifestReviewItem>();
        var sourceProtocol = GetSourceProtocol(inventory);

        var serviceIdentity = BuildServiceIdentity(inventory, targetServiceName);

        foreach (var resource in inventory.Resources.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            AddReviewItems(
                inventory.SourceKind,
                resource.Id,
                resource.Kind,
                resource.Compatibility,
                manualReviewItems,
                unsupportedItems);

            if (IsIncompatible(resource.Compatibility.Level))
            {
                continue;
            }

            var action = IsPartial(resource.Compatibility.Level) ? "manual-review" : "publish";
            var targetResourceName = NormalizeName(resource.Name, fallback: resource.Id);
            var targetResourceId = BuildTargetId("resource", targetServiceName, targetResourceName);
            var identity = BuildResourceIdentity(
                inventory,
                resource,
                serviceIdentity,
                targetServiceName,
                targetResourceId,
                targetResourceName);
            var attachments = BuildResourceAttachments(
                inventory,
                resource,
                targetServiceName,
                targetResourceName);
            var relationships = BuildResourceRelationships(
                inventory,
                resource,
                targetServiceName,
                targetResourceName);
            var rendererDiagnostics = BuildResourceRendererDiagnostics(
                inventory,
                resource,
                targetServiceName,
                targetResourceName);
            var labelClassDiagnostics = BuildResourceLabelClassDiagnostics(
                inventory,
                resource);

            targetResources.Add(new MigrationManifestTargetResource
            {
                SourceResourceId = resource.Id,
                SourceKind = resource.Kind,
                Action = action,
                TargetResourceId = targetResourceId,
                TargetServiceName = targetServiceName,
                TargetResourceName = targetResourceName,
                GeometryType = resource.GeometryType,
                MigrationMode = GetResourceMigrationMode(inventory.SourceKind, resource),
                SourceProtocol = sourceProtocol,
                Fields = resource.Fields.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray(),
                Capabilities = Order(resource.Capabilities),
                SpatialReferences = resource.SpatialReferences
                    .OrderBy(static item => item.Role, StringComparer.Ordinal)
                    .ThenBy(static item => item.SourceValue, StringComparer.Ordinal)
                    .ToArray(),
                StyleIds = Order(resource.StyleIds),
                ExternalDependencyIds = BuildResourceExternalDependencyIds(inventory, resource),
                Identity = identity,
                Attachments = attachments,
                Relationships = relationships,
                RendererDiagnostics = rendererDiagnostics,
                LabelClassDiagnostics = labelClassDiagnostics,
                Compatibility = resource.Compatibility
            });
            identityRemaps.Add(new MigrationManifestIdentityRemap
            {
                SourceId = resource.Id,
                SourceKind = resource.Kind,
                TargetId = targetResourceId,
                TargetKind = "resource",
                TargetName = targetResourceName,
                Action = action,
                IdentityStability = identity.IdentityStability,
                Reason = identity.IdentityRemapReason
            });
        }

        foreach (var style in inventory.Styles.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            AddReviewItems(
                inventory.SourceKind,
                style.Id,
                style.Kind,
                style.Compatibility,
                manualReviewItems,
                unsupportedItems);

            var action = IsCompatible(style.Compatibility.Level) ? "import" : "manual-review";
            var targetStyleName = NormalizeName(style.Name, fallback: style.Id);
            var targetStyleId = BuildTargetId("style", targetServiceName, targetStyleName);

            styleActions.Add(new MigrationManifestStyleAction
            {
                SourceStyleId = style.Id,
                TargetStyleId = targetStyleId,
                Action = action,
                Format = style.Format,
                ResourceIds = Order(style.ResourceIds),
                ExternalDependencyIds = Order(style.ExternalDependencyIds),
                Compatibility = style.Compatibility
            });
            identityRemaps.Add(new MigrationManifestIdentityRemap
            {
                SourceId = style.Id,
                SourceKind = style.Kind,
                TargetId = targetStyleId,
                TargetKind = "style",
                TargetName = targetStyleName,
                Action = action,
                IdentityStability = string.Equals(style.Id, targetStyleId, StringComparison.Ordinal)
                    ? MigrationManifestIdentityStabilities.Preserved
                    : MigrationManifestIdentityStabilities.Remapped,
                Reason = string.Equals(style.Id, targetStyleId, StringComparison.Ordinal)
                    ? null
                    : "Honua reserves a deterministic style identifier scoped to the target service."
            });
        }

        foreach (var dependency in inventory.ExternalDependencies.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            AddReviewItems(
                inventory.SourceKind,
                dependency.Id,
                dependency.Kind,
                dependency.Compatibility,
                manualReviewItems,
                unsupportedItems);
        }

        servicePlans.AddRange(BuildServicePlans(inventory, targetServiceName, serviceIdentity));

        var orderedIdentityRemaps = identityRemaps
            .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetId, StringComparer.Ordinal)
            .ToArray();
        var identityRemapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var remap in orderedIdentityRemaps)
        {
            if (string.Equals(remap.IdentityStability, MigrationManifestIdentityStabilities.Preserved, StringComparison.Ordinal))
            {
                continue;
            }

            if (identityRemapping.TryGetValue(remap.SourceId, out var existingTarget) &&
                !string.Equals(existingTarget, remap.TargetId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Source identifier '{remap.SourceId}' is mapped to conflicting target identifiers " +
                    $"'{existingTarget}' and '{remap.TargetId}'. App migration tooling cannot apply ambiguous remappings; " +
                    "fix the source inventory so each source id resolves to a single target.");
            }

            identityRemapping[remap.SourceId] = remap.TargetId;
        }

        var fidelityMatrix = inventory.FidelityClassifications.Length > 0
            ? MigrationFidelityMatrixBuilder.Build(inventory.FidelityClassifications, orderedIdentityRemaps)
            : inventory.FidelityMatrix;

        return new MigrationManifestArtifact
        {
            SourceArtifactKind = inventory.ArtifactKind,
            SourceArtifactVersion = inventory.ArtifactVersion,
            SourceKind = inventory.SourceKind,
            Source = inventory.Source,
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = inventory.Resources.Length,
                TargetResourceCount = targetResources.Count,
                StyleActionCount = styleActions.Count,
                ServicePlanCount = servicePlans.Count,
                ManualReviewCount = manualReviewItems.Count,
                UnsupportedCount = unsupportedItems.Count
            },
            TargetResources = targetResources.ToArray(),
            StyleActions = styleActions.ToArray(),
            ServicePlans = servicePlans
                .OrderBy(static item => item.SourceContainerId, StringComparer.Ordinal)
                .ToArray(),
            IdentityRemaps = orderedIdentityRemaps,
            IdentityRemapping = identityRemapping,
            FidelityMatrix = fidelityMatrix,
            ManualReviewItems = manualReviewItems
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ToArray(),
            UnsupportedItems = unsupportedItems
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static void AddReviewItems(
        string sourceKind,
        string sourceId,
        string kind,
        MigrationCompatibilityAssessment compatibility,
        List<MigrationManifestReviewItem> manualReviewItems,
        List<MigrationManifestReviewItem> unsupportedItems)
    {
        if (IsCompatible(compatibility.Level))
        {
            return;
        }

        var reviewItem = new MigrationManifestReviewItem
        {
            SourceId = sourceId,
            Kind = kind,
            Code = compatibility.Code ?? BuildFallbackCode(sourceKind, kind, compatibility.Level),
            Severity = IsIncompatible(compatibility.Level) ? "unsupported" : "manual-review",
            Reason = compatibility.Reason,
            ManualSteps = Order(compatibility.ManualSteps),
            Warnings = Order(compatibility.Warnings)
        };

        if (IsIncompatible(compatibility.Level))
        {
            unsupportedItems.Add(reviewItem);
        }
        else
        {
            manualReviewItems.Add(reviewItem);
        }
    }

    private static MigrationManifestServicePlan[] BuildServicePlans(
        MigrationSourceInventoryArtifact inventory,
        string targetServiceName,
        MigrationManifestServiceIdentity serviceIdentity)
    {
        if (!IsOgcRenderOnlySource(inventory.SourceKind))
        {
            return [];
        }

        var serviceType = GetSourceProtocol(inventory);
        return inventory.Containers
            .OrderBy(static container => container.Id, StringComparer.Ordinal)
            .Select(container =>
            {
                var resources = inventory.Resources
                    .Where(resource => string.Equals(resource.ContainerId, container.Id, StringComparison.Ordinal));
                var styles = inventory.Styles
                    .Where(style => string.Equals(style.ContainerId, container.Id, StringComparison.Ordinal));
                var dependencies = inventory.ExternalDependencies
                    .Where(dependency => string.Equals(dependency.ContainerId, container.Id, StringComparison.Ordinal));

                return new MigrationManifestServicePlan
                {
                    SourceContainerId = container.Id,
                    SourceKind = container.Kind,
                    Action = "manual-review",
                    TargetServiceName = targetServiceName,
                    ServiceType = serviceType,
                    ResourceIds = Order(resources.Select(static resource => resource.Id)),
                    StyleIds = Order(styles.Select(static style => style.Id)),
                    ExternalDependencyIds = Order(dependencies.Select(static dependency => dependency.Id)),
                    Identity = serviceIdentity with
                    {
                        SourceServiceId = serviceIdentity.SourceServiceId ?? container.Id
                    },
                    Compatibility = container.Compatibility
                };
            })
            .ToArray();
    }

    private static MigrationManifestServiceIdentity BuildServiceIdentity(
        MigrationSourceInventoryArtifact inventory,
        string targetServiceName)
    {
        var (sourceServiceId, sourceQualifiedName, sourceFolderPath) = ExtractServiceSourceIdentity(inventory);

        var stability = string.IsNullOrEmpty(sourceServiceId)
            ? MigrationManifestIdentityStabilities.Synthesized
            : string.Equals(sourceServiceId, targetServiceName, StringComparison.Ordinal)
                ? MigrationManifestIdentityStabilities.Preserved
                : MigrationManifestIdentityStabilities.Remapped;
        var reason = stability switch
        {
            MigrationManifestIdentityStabilities.Synthesized =>
                "Source did not advertise a stable service identifier; Honua synthesized one from the source display name.",
            MigrationManifestIdentityStabilities.Remapped =>
                "Source service name contained characters that are not allowed in Honua service identifiers and was normalized.",
            _ => null
        };

        return new MigrationManifestServiceIdentity
        {
            SourceServiceId = string.IsNullOrEmpty(sourceServiceId) ? null : sourceServiceId,
            SourceQualifiedName = sourceQualifiedName,
            SourceFolderPath = sourceFolderPath,
            TargetServiceId = targetServiceName,
            TargetName = targetServiceName,
            IdentityStability = stability,
            IdentityRemapReason = reason
        };
    }

    private static MigrationManifestResourceIdentity BuildResourceIdentity(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource,
        MigrationManifestServiceIdentity serviceIdentity,
        string targetServiceName,
        string targetResourceId,
        string targetResourceName)
    {
        var sourceLayerId = ExtractResourceSourceLayerId(inventory, resource);
        var sourceQualifiedName = BuildResourceSourceQualifiedName(
            inventory,
            resource,
            serviceIdentity.SourceQualifiedName,
            sourceLayerId);

        var stability = string.IsNullOrEmpty(sourceLayerId)
            ? MigrationManifestIdentityStabilities.Synthesized
            : string.Equals(sourceLayerId, targetResourceName, StringComparison.Ordinal) &&
              string.Equals(resource.Name, targetResourceName, StringComparison.Ordinal)
                ? MigrationManifestIdentityStabilities.Preserved
                : MigrationManifestIdentityStabilities.Remapped;
        var reason = stability switch
        {
            MigrationManifestIdentityStabilities.Synthesized =>
                "Source did not advertise a stable layer identifier; Honua synthesized a deterministic target id.",
            MigrationManifestIdentityStabilities.Remapped =>
                "Source layer id or name was normalized to fit Honua identifier constraints or to avoid a collision in the target service.",
            _ => null
        };

        return new MigrationManifestResourceIdentity
        {
            SourceServiceId = serviceIdentity.SourceServiceId,
            SourceLayerId = string.IsNullOrEmpty(sourceLayerId) ? null : sourceLayerId,
            SourceQualifiedName = sourceQualifiedName,
            SourceFolderPath = serviceIdentity.SourceFolderPath,
            TargetServiceId = targetServiceName,
            TargetLayerId = targetResourceId,
            TargetName = targetResourceName,
            TargetFolderPath = serviceIdentity.TargetFolderPath,
            IdentityStability = stability,
            IdentityRemapReason = reason
        };
    }

    private static (string? SourceServiceId, string? SourceQualifiedName, string? SourceFolderPath) ExtractServiceSourceIdentity(
        MigrationSourceInventoryArtifact inventory)
    {
        if (IsArcGisSource(inventory.SourceKind))
        {
            // Honua inventory uses container ids like "service:{serviceKey}" for ArcGIS sources.
            var container = inventory.Containers
                .FirstOrDefault(static c => c.Id.StartsWith("service:", StringComparison.Ordinal));
            string? folderPath = null;
            if (!string.IsNullOrEmpty(inventory.Source.BaseUrl))
            {
                folderPath = ExtractArcGisFolderPath(inventory.Source.BaseUrl);
            }

            if (container is not null)
            {
                var serviceKey = container.Id["service:".Length..];
                var qualified = string.IsNullOrEmpty(folderPath)
                    ? serviceKey
                    : $"{folderPath}/{serviceKey}";
                return (serviceKey, qualified, folderPath);
            }
        }

        return (null, null, null);
    }

    private static string? ExtractResourceSourceLayerId(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource)
    {
        if (IsArcGisSource(inventory.SourceKind))
        {
            // Honua inventory uses resource ids like "resource:{serviceName}:{kind}:{id}" for ArcGIS sources.
            const string prefix = "resource:";
            if (resource.Id.StartsWith(prefix, StringComparison.Ordinal))
            {
                var remainder = resource.Id[prefix.Length..];
                var parts = remainder.Split(':');
                if (parts.Length >= 3)
                {
                    return parts[^1];
                }
            }
        }

        return null;
    }

    private static string? BuildResourceSourceQualifiedName(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource,
        string? serviceQualifiedName,
        string? sourceLayerId)
    {
        if (string.IsNullOrEmpty(serviceQualifiedName))
        {
            return null;
        }

        var leaf = !string.IsNullOrEmpty(sourceLayerId)
            ? sourceLayerId!
            : resource.Name;
        return string.IsNullOrEmpty(leaf)
            ? serviceQualifiedName
            : $"{serviceQualifiedName}/{leaf}";
    }

    private static string? ExtractArcGisFolderPath(string baseUrl)
    {
        // ArcGIS REST URLs follow .../rest/services/{folder?}/{service}/{serviceType}.
        // Capture an optional folder segment that sits between "services" and the service name.
        var segments = baseUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var servicesIndex = -1;
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("services", StringComparison.OrdinalIgnoreCase))
            {
                servicesIndex = i;
                break;
            }
        }

        if (servicesIndex < 0)
        {
            return null;
        }

        var serviceTypeIndex = -1;
        for (var i = segments.Length - 1; i > servicesIndex; i--)
        {
            if (segments[i].Equals("FeatureServer", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("MapServer", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("ImageServer", StringComparison.OrdinalIgnoreCase))
            {
                serviceTypeIndex = i;
                break;
            }
        }

        if (serviceTypeIndex < 0)
        {
            return null;
        }

        // Folder segments sit between "services" (exclusive) and the service name (which precedes the service type).
        var folderStart = servicesIndex + 1;
        var folderEnd = serviceTypeIndex - 1; // exclusive of the service name itself
        if (folderEnd <= folderStart)
        {
            return null;
        }

        var folderSegments = new ArraySegment<string>(segments, folderStart, folderEnd - folderStart);
        return folderSegments.Count == 0 ? null : string.Join('/', folderSegments.ToArray());
    }

    private static bool IsArcGisSource(string sourceKind)
        => string.Equals(sourceKind, "arcgis-geoservices-rest", StringComparison.OrdinalIgnoreCase);

    // Attachments at or below this size are small enough for the automated migration lane.
    // Above this size we capture the metadata but require an operator-supervised follow-up so the
    // automated lane is not blocked by large binary transfers.
    private const long AttachmentAutomatedSizeLimitBytes = 10L * 1024L * 1024L; // 10 MiB

    // Attachments above this size are escalated past assisted to manual review because they need
    // out-of-band transfer planning (network, object storage, etc.) rather than inline migration.
    private const long AttachmentManualReviewSizeLimitBytes = 100L * 1024L * 1024L; // 100 MiB

    private static readonly HashSet<string> SupportedAttachmentContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "application/pdf",
        "text/plain",
        "text/csv",
        "application/json"
    };

    private static MigrationManifestAttachmentRecord[] BuildResourceAttachments(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource,
        string targetServiceName,
        string targetResourceName)
    {
        if (!IsArcGisSource(inventory.SourceKind))
        {
            return [];
        }

        var dependencyIdSet = new HashSet<string>(resource.ExternalDependencyIds, StringComparer.Ordinal);
        var records = new List<MigrationManifestAttachmentRecord>();

        foreach (var dependency in inventory.ExternalDependencies
                     .Where(d => string.Equals(d.Kind, "attachments", StringComparison.OrdinalIgnoreCase))
                     .Where(d => string.Equals(d.ResourceId, resource.Id, StringComparison.Ordinal) ||
                                 dependencyIdSet.Contains(d.Id))
                     .OrderBy(static d => d.Id, StringComparer.Ordinal))
        {
            var attachmentId = dependency.Metadata.TryGetValue("attachmentId", out var advertisedId) &&
                               !string.IsNullOrWhiteSpace(advertisedId)
                ? advertisedId
                : dependency.Id;
            var name = dependency.Metadata.TryGetValue("name", out var advertisedName) &&
                       !string.IsNullOrWhiteSpace(advertisedName)
                ? advertisedName
                : dependency.Name;
            var contentType = dependency.Metadata.TryGetValue("contentType", out var ct) && !string.IsNullOrWhiteSpace(ct)
                ? ct
                : null;
            long? size = null;
            if (dependency.Metadata.TryGetValue("size", out var sizeText) &&
                long.TryParse(sizeText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedSize) &&
                parsedSize >= 0)
            {
                size = parsedSize;
            }

            var (classification, reason) = ClassifyAttachment(size, contentType);
            string? targetRef = classification switch
            {
                MigrationManifestAttachmentClassifications.Automated or
                    MigrationManifestAttachmentClassifications.Assisted =>
                    $"target:attachment:{targetServiceName}:{targetResourceName}:{attachmentId}",
                _ => null
            };

            records.Add(new MigrationManifestAttachmentRecord
            {
                SourceAttachmentId = attachmentId,
                SourceResourceId = resource.Id,
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                ContentType = contentType,
                Size = size,
                Classification = classification,
                TargetAttachmentRef = targetRef,
                Reason = reason
            });
        }

        return records
            .OrderBy(static r => r.SourceAttachmentId, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string Classification, string? Reason) ClassifyAttachment(long? size, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) && !SupportedAttachmentContentTypes.Contains(contentType))
        {
            return (
                MigrationManifestAttachmentClassifications.ManualReview,
                $"Attachment content type '{contentType}' is outside the supported automated allowlist; capture for operator review.");
        }

        if (size is null)
        {
            return (
                MigrationManifestAttachmentClassifications.ManualReview,
                "Attachment size was not advertised by the source; defer to operator review before migration.");
        }

        if (size.Value <= AttachmentAutomatedSizeLimitBytes)
        {
            return (MigrationManifestAttachmentClassifications.Automated, null);
        }

        if (size.Value <= AttachmentManualReviewSizeLimitBytes)
        {
            return (
                MigrationManifestAttachmentClassifications.Assisted,
                $"Attachment exceeds the {AttachmentAutomatedSizeLimitBytes / (1024L * 1024L)} MiB automated lane and will run through the assisted attachment migration step.");
        }

        return (
            MigrationManifestAttachmentClassifications.ManualReview,
            $"Attachment exceeds the {AttachmentManualReviewSizeLimitBytes / (1024L * 1024L)} MiB assisted lane and must be planned out-of-band by an operator.");
    }

    private static MigrationManifestRelationshipRecord[] BuildResourceRelationships(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource,
        string targetServiceName,
        string targetResourceName)
    {
        if (!IsArcGisSource(inventory.SourceKind))
        {
            return [];
        }

        var records = new List<MigrationManifestRelationshipRecord>();

        foreach (var classification in inventory.FidelityClassifications
                     .Where(c => string.Equals(c.SourceId, resource.Id, StringComparison.Ordinal))
                     .Where(c => string.Equals(c.Category, "relationships", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static c => c.Id, StringComparer.Ordinal))
        {
            var relationshipId = classification.Metadata.TryGetValue("relationshipId", out var advertisedId) &&
                                 !string.IsNullOrWhiteSpace(advertisedId)
                ? advertisedId
                : classification.Id;
            var name = classification.Metadata.TryGetValue("name", out var advertisedName) &&
                       !string.IsNullOrWhiteSpace(advertisedName)
                ? advertisedName
                : classification.Name;
            var cardinality = classification.Metadata.TryGetValue("cardinality", out var card) &&
                              !string.IsNullOrWhiteSpace(card)
                ? card
                : null;
            var relationshipType = classification.Metadata.TryGetValue("relationshipType", out var rt) &&
                                   !string.IsNullOrWhiteSpace(rt)
                ? rt
                : null;
            var relatedLayerIds = ExtractRelatedLayerIds(classification);

            var (cls, reason) = ClassifyRelationship(cardinality, relationshipType, relatedLayerIds);
            string? targetRef = cls switch
            {
                MigrationManifestRelationshipClassifications.Automated or
                    MigrationManifestRelationshipClassifications.Assisted =>
                    $"target:relationship:{targetServiceName}:{targetResourceName}:{relationshipId}",
                _ => null
            };

            records.Add(new MigrationManifestRelationshipRecord
            {
                SourceRelationshipId = relationshipId,
                SourceResourceId = resource.Id,
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                Cardinality = cardinality,
                RelationshipType = relationshipType,
                RelatedLayerIds = relatedLayerIds,
                Classification = cls,
                TargetRelationshipRef = targetRef,
                Reason = reason
            });
        }

        return records
            .OrderBy(static r => r.SourceRelationshipId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ExtractRelatedLayerIds(MigrationFidelityClassificationRecord classification)
    {
        if (classification.Metadata.TryGetValue("relatedLayerIds", out var csv) && !string.IsNullOrWhiteSpace(csv))
        {
            return Order(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return Order(classification.RelatedIds);
    }

    private static (string Classification, string? Reason) ClassifyRelationship(
        string? cardinality,
        string? relationshipType,
        string[] relatedLayerIds)
    {
        if (!string.IsNullOrWhiteSpace(relationshipType) &&
            (relationshipType.Equals("composite", StringComparison.OrdinalIgnoreCase) ||
             relationshipType.Equals("attributed", StringComparison.OrdinalIgnoreCase)))
        {
            return (
                MigrationManifestRelationshipClassifications.ManualReview,
                $"Relationship type '{relationshipType}' carries side-effects (composite delete or junction attributes) that this slice does not recreate automatically.");
        }

        if (relatedLayerIds.Length == 0)
        {
            return (
                MigrationManifestRelationshipClassifications.ManualReview,
                "Relationship did not advertise related layer ids; operator must identify and re-link related layers before cutover.");
        }

        var normalizedCardinality = NormalizeCardinality(cardinality);
        return normalizedCardinality switch
        {
            "1:1" or "1:N" or "M:N" => (
                MigrationManifestRelationshipClassifications.Assisted,
                $"Relationship cardinality '{normalizedCardinality}' captured; target apply recreates the link table or foreign key during the assisted relationship step."),
            _ => (
                MigrationManifestRelationshipClassifications.ManualReview,
                string.IsNullOrWhiteSpace(cardinality)
                    ? "Relationship cardinality was not advertised; operator must confirm cardinality before recreating the link."
                    : $"Relationship cardinality '{cardinality}' is not recognized by the automated lane; operator review required.")
        };
    }

    private static string? NormalizeCardinality(string? cardinality)
    {
        if (string.IsNullOrWhiteSpace(cardinality))
        {
            return null;
        }

        return cardinality.Trim().ToUpperInvariant() switch
        {
            "1:1" or "ONE_TO_ONE" or "ONETOONE" or "ESRIRELCARDINALITYONETOONE" => "1:1",
            "1:N" or "1:M" or "ONE_TO_MANY" or "ONETOMANY" or "ESRIRELCARDINALITYONETOMANY" => "1:N",
            "M:N" or "MANY_TO_MANY" or "MANYTOMANY" or "ESRIRELCARDINALITYMANYTOMANY" => "M:N",
            _ => null
        };
    }

    // <=10 categories on a uniqueValue or classBreaks renderer keep the renderer eligible for the
    // assisted lane. Above this threshold target apply cannot reasonably re-bind a default style and
    // the renderer is escalated to manual review per the slice 4 contract.
    private const int RendererAssistedCategoryLimit = 10;

    private static readonly HashSet<string> SupportedRendererKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "simple",
        "uniqueValue",
        "classBreaks"
    };

    private static readonly HashSet<string> ManualReviewRendererKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "dictionary",
        "rasterShaded",
        "rasterColormap",
        "vectorField",
        "temporal",
        "heatmap",
        "stretched",
        "classedColor",
        "classedSize"
    };

    private static MigrationManifestRendererDiagnostic[] BuildResourceRendererDiagnostics(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource,
        string targetServiceName,
        string targetResourceName)
    {
        if (!IsArcGisSource(inventory.SourceKind))
        {
            return [];
        }

        var diagnostics = new List<MigrationManifestRendererDiagnostic>();

        foreach (var style in inventory.Styles
                     .Where(style => string.Equals(style.Kind, "renderer", StringComparison.OrdinalIgnoreCase))
                     .Where(style => style.ResourceIds.Contains(resource.Id, StringComparer.Ordinal))
                     .OrderBy(static style => style.Id, StringComparer.Ordinal))
        {
            var rendererKind = style.Metadata.TryGetValue("rendererType", out var advertisedKind) &&
                               !string.IsNullOrWhiteSpace(advertisedKind)
                ? advertisedKind
                : "unknown";
            var categoryCount = ExtractRendererCategoryCount(style.Metadata, rendererKind);
            var hasExternalSymbol = style.ExternalDependencyIds.Length > 0 ||
                                    HasComplexExpression(style.Metadata);

            var (classification, reason, suggestedTargetStyleId) = ClassifyRenderer(
                rendererKind,
                categoryCount,
                hasExternalSymbol,
                targetServiceName,
                targetResourceName);

            diagnostics.Add(new MigrationManifestRendererDiagnostic
            {
                SourceStyleId = style.Id,
                SourceResourceId = resource.Id,
                RendererKind = rendererKind,
                CategoryCount = categoryCount,
                Classification = classification,
                SuggestedTargetStyleId = suggestedTargetStyleId,
                Reason = reason
            });
        }

        return diagnostics
            .OrderBy(static d => d.SourceStyleId, StringComparer.Ordinal)
            .ToArray();
    }

    private static int? ExtractRendererCategoryCount(IReadOnlyDictionary<string, string> metadata, string rendererKind)
    {
        // The scanner surfaces the number of uniqueValue infos / classBreaks infos via metadata so
        // the translator can decide assisted vs manual-review without re-parsing the renderer JSON.
        var key = rendererKind.Equals("classBreaks", StringComparison.OrdinalIgnoreCase)
            ? "classBreakInfoCount"
            : rendererKind.Equals("uniqueValue", StringComparison.OrdinalIgnoreCase)
                ? "uniqueValueInfoCount"
                : null;

        if (key is null)
        {
            return null;
        }

        if (metadata.TryGetValue(key, out var raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0)
        {
            return parsed;
        }

        return null;
    }

    private static bool HasComplexExpression(IReadOnlyDictionary<string, string> metadata)
        => metadata.TryGetValue("hasComplexExpression", out var raw) &&
           bool.TryParse(raw, out var parsed) && parsed;

    private static (string Classification, string? Reason, string? SuggestedTargetStyleId) ClassifyRenderer(
        string rendererKind,
        int? categoryCount,
        bool hasExternalSymbol,
        string targetServiceName,
        string targetResourceName)
    {
        if (string.Equals(rendererKind, "simple", StringComparison.OrdinalIgnoreCase))
        {
            var suggested = $"target:style:{targetServiceName}:{targetResourceName}:simple";
            if (hasExternalSymbol)
            {
                return (
                    MigrationManifestRendererClassifications.Assisted,
                    "Simple renderer references one or more external symbol URLs; target apply must mirror or replace the external symbol assets before binding the suggested target style.",
                    suggested);
            }

            return (
                MigrationManifestRendererClassifications.Automated,
                null,
                suggested);
        }

        if (string.Equals(rendererKind, "uniqueValue", StringComparison.OrdinalIgnoreCase))
        {
            if (categoryCount is { } count && count > RendererAssistedCategoryLimit)
            {
                return (
                    MigrationManifestRendererClassifications.ManualReview,
                    $"Renderer 'uniqueValue' advertises {count} categories, exceeding the {RendererAssistedCategoryLimit}-category assisted lane; operator must review and consolidate categories before migration.",
                    null);
            }

            return (
                MigrationManifestRendererClassifications.Assisted,
                "Renderer 'uniqueValue' captured; target apply recreates the category bindings during the assisted style step.",
                $"target:style:{targetServiceName}:{targetResourceName}:unique-value");
        }

        if (string.Equals(rendererKind, "classBreaks", StringComparison.OrdinalIgnoreCase))
        {
            if (categoryCount is { } breaks && breaks > RendererAssistedCategoryLimit)
            {
                return (
                    MigrationManifestRendererClassifications.ManualReview,
                    $"Renderer 'classBreaks' advertises {breaks} breaks, exceeding the {RendererAssistedCategoryLimit}-break assisted lane; operator must review and simplify class breaks before migration.",
                    null);
            }

            return (
                MigrationManifestRendererClassifications.Assisted,
                "Renderer 'classBreaks' captured; target apply recreates the class-break bindings during the assisted style step.",
                $"target:style:{targetServiceName}:{targetResourceName}:class-breaks");
        }

        if (ManualReviewRendererKinds.Contains(rendererKind))
        {
            return (
                MigrationManifestRendererClassifications.ManualReview,
                $"Renderer '{rendererKind}' uses dictionary lookups, raster shading, or other Esri-specific drawing strategies that this slice does not translate automatically.",
                null);
        }

        return (
            MigrationManifestRendererClassifications.Unsupported,
            $"Renderer kind '{rendererKind}' is not recognized by the automated lane and cannot be migrated by this slice.",
            null);
    }

    private static MigrationManifestLabelClassDiagnostic[] BuildResourceLabelClassDiagnostics(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource)
    {
        if (!IsArcGisSource(inventory.SourceKind))
        {
            return [];
        }

        var diagnostics = new List<MigrationManifestLabelClassDiagnostic>();

        foreach (var style in inventory.Styles
                     .Where(style => string.Equals(style.Kind, "renderer", StringComparison.OrdinalIgnoreCase))
                     .Where(style => style.ResourceIds.Contains(resource.Id, StringComparer.Ordinal))
                     .OrderBy(static style => style.Id, StringComparer.Ordinal))
        {
            // The scanner surfaces per-label-class entries via metadata keys of the form
            // "labelClass.{index}.expression" and "labelClass.{index}.expressionEngine"; the
            // translator iterates by index until the expression key disappears.
            for (var index = 0; ; index++)
            {
                var expressionKey = $"labelClass.{index}.expression";
                var hasExpression = style.Metadata.TryGetValue(expressionKey, out var expression);
                var engineKey = $"labelClass.{index}.expressionEngine";
                var hasEngine = style.Metadata.TryGetValue(engineKey, out var engine);

                if (!hasExpression && !hasEngine)
                {
                    break;
                }

                var trimmedExpression = string.IsNullOrWhiteSpace(expression) ? null : expression;
                var trimmedEngine = string.IsNullOrWhiteSpace(engine) ? null : engine;

                var (classification, reason) = ClassifyLabelClass(trimmedExpression, trimmedEngine);

                diagnostics.Add(new MigrationManifestLabelClassDiagnostic
                {
                    SourceStyleId = style.Id,
                    SourceResourceId = resource.Id,
                    LabelClassIndex = index,
                    Expression = trimmedExpression,
                    ExpressionEngine = trimmedEngine,
                    Classification = classification,
                    Reason = reason
                });
            }
        }

        return diagnostics
            .OrderBy(static d => d.SourceStyleId, StringComparer.Ordinal)
            .ThenBy(static d => d.LabelClassIndex)
            .ToArray();
    }

    private static (string Classification, string? Reason) ClassifyLabelClass(string? expression, string? engine)
    {
        if (!string.IsNullOrWhiteSpace(engine) &&
            engine.Trim().Equals("VBScript", StringComparison.OrdinalIgnoreCase))
        {
            return (
                MigrationManifestLabelClassClassifications.Unsupported,
                "VBScript label expressions are not portable to Honua; operator must rewrite the expression in Arcade or simplify to a field token before migration.");
        }

        if (!string.IsNullOrWhiteSpace(engine) &&
            (engine.Trim().Equals("JScript", StringComparison.OrdinalIgnoreCase) ||
             engine.Trim().Equals("Python", StringComparison.OrdinalIgnoreCase)))
        {
            return (
                MigrationManifestLabelClassClassifications.ManualReview,
                $"Label expression engine '{engine.Trim()}' is captured for operator review; Honua does not execute Esri scripting engines.");
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return (
                MigrationManifestLabelClassClassifications.ManualReview,
                "Label class did not advertise an expression; operator must confirm the label binding before migration.");
        }

        var trimmed = expression.Trim();
        // A pure field token like "[NAME]" or {NAME} maps cleanly to a Honua field-substitution label.
        if (IsSimpleFieldToken(trimmed))
        {
            return (MigrationManifestLabelClassClassifications.Automated, null);
        }

        // Arcade or complex expressions need operator-supervised follow-up.
        if (!string.IsNullOrWhiteSpace(engine) &&
            engine.Trim().Equals("Arcade", StringComparison.OrdinalIgnoreCase))
        {
            return (
                MigrationManifestLabelClassClassifications.Assisted,
                "Arcade label expression captured; target apply recreates the expression during the assisted label step.");
        }

        return (
            MigrationManifestLabelClassClassifications.ManualReview,
            "Label expression mixes field tokens with literal text or function calls and was captured for operator review before migration.");
    }

    private static bool IsSimpleFieldToken(string expression)
    {
        // Recognize "[FIELD]" or "{FIELD}" with no surrounding literal text.
        if (expression.Length < 3)
        {
            return false;
        }

        var first = expression[0];
        var last = expression[^1];
        if (!((first == '[' && last == ']') || (first == '{' && last == '}')))
        {
            return false;
        }

        var inner = expression[1..^1];
        if (string.IsNullOrWhiteSpace(inner))
        {
            return false;
        }

        // Reject expressions that contain whitespace, brackets, or operator characters in the inner.
        foreach (var ch in inner)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string[] BuildResourceExternalDependencyIds(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource)
    {
        var dependencyIds = resource.ExternalDependencyIds.AsEnumerable();
        if (string.Equals(inventory.SourceKind, "ogc-wfs", StringComparison.OrdinalIgnoreCase))
        {
            dependencyIds = dependencyIds.Concat(inventory.ExternalDependencies
                .Where(static dependency => IsOgcWfsEndpointDependency(dependency))
                .Select(static dependency => dependency.Id));
        }

        return Order(dependencyIds);
    }

    private static bool IsOgcWfsEndpointDependency(MigrationExternalDependency dependency)
        => string.Equals(dependency.Kind, "ogc-endpoint", StringComparison.OrdinalIgnoreCase) &&
           dependency.Metadata.TryGetValue("service", out var service) &&
           string.Equals(service, "WFS", StringComparison.OrdinalIgnoreCase);

    private static string? GetResourceMigrationMode(string sourceKind, MigrationInventoryResource resource)
    {
        if (string.Equals(sourceKind, "ogc-wfs", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(resource.Kind, "feature-type", StringComparison.OrdinalIgnoreCase))
        {
            return "feature-import";
        }

        if (string.Equals(sourceKind, "ogc-api-features", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(resource.Kind, "ogc-api-features-collection", StringComparison.OrdinalIgnoreCase))
        {
            return "feature-import";
        }

        if ((string.Equals(sourceKind, "ogc-wcs", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(sourceKind, "ogc-api-coverages", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(resource.Kind, "coverage", StringComparison.OrdinalIgnoreCase))
        {
            return "raster-coverage-import";
        }

        return null;
    }

    private static string? GetSourceProtocol(MigrationSourceInventoryArtifact inventory)
    {
        if (!string.IsNullOrWhiteSpace(inventory.Source.ServiceType))
        {
            return inventory.Source.ServiceType.Trim();
        }

        return inventory.SourceKind.ToLowerInvariant() switch
        {
            "ogc-wfs" => "WFS",
            "ogc-api-features" => "OGC API Features",
            "ogc-wms" => "WMS",
            "ogc-wmts" => "WMTS",
            "ogc-wcs" => "WCS",
            "ogc-api-coverages" => "OGC API Coverages",
            _ => null
        };
    }

    private static bool IsOgcRenderOnlySource(string sourceKind)
        => sourceKind.Equals("ogc-wms", StringComparison.OrdinalIgnoreCase) ||
           sourceKind.Equals("ogc-wmts", StringComparison.OrdinalIgnoreCase);

    private static string BuildFallbackCode(string sourceKind, string kind, string level)
        => $"{ToCodePart(sourceKind)}_{ToCodePart(kind)}_{ToCodePart(level)}";

    private static string BuildTargetId(string targetKind, string targetServiceName, string targetName)
        => $"target:{targetKind}:{targetServiceName}:{targetName}";

    private static string ToCodePart(string value)
        => CodeUnsafeCharacters().Replace(value.Trim(), "_").Trim('_').ToUpperInvariant();

    private static string NormalizeName(string value, string fallback)
    {
        var normalized = NameUnsafeCharacters()
            .Replace(value.Trim(), "-")
            .Trim('-')
            .ToLowerInvariant();

        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized.Length <= 64 ? normalized : normalized[..64].TrimEnd('-');
    }

    private static string[] Order(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCompatible(string? level)
        => string.Equals(level, "compatible", StringComparison.OrdinalIgnoreCase);

    private static bool IsPartial(string? level)
        => string.Equals(level, "partial", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncompatible(string? level)
        => string.Equals(level, "incompatible", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex CodeUnsafeCharacters();

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex NameUnsafeCharacters();
}
