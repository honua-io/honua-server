// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Accounts for every construct discovered on an ArcGIS GeoServices source against a service migration
/// selection before anything is applied (issue #4600, acceptance criterion 1). Pure and deterministic.
/// </summary>
/// <remarks>
/// A service migration is full fidelity only when the service type is supported, every discovered layer
/// and table is selected, and every selected construct is carried by the automated import. The
/// accounting reads the source manifest that <see cref="MigrationManifestTranslator"/> produces from a
/// scan: its target resources, its unsupported items, its fidelity matrix cells, its style actions and its
/// label class diagnostics. Nothing is fetched from the source, so a persisted manifest and selection
/// replay to the same accounting.
/// </remarks>
public static class MigrationServiceConstructAccountant
{
    /// <summary>The source kind the construct matrix is defined for.</summary>
    public const string GeoservicesSourceKind = "arcgis-geoservices-rest";

    private static readonly string[] SupportedServiceTypes = ["FeatureServer", "MapServer"];

    private static readonly string[] EditCapabilities = ["Create", "Delete", "Editing", "Update", "Uploads"];

    private static readonly Dictionary<string, MigrationConstructMatrixRow> RowsByConstruct =
        MigrationConstructMatrix.Rows.ToDictionary(static row => row.Construct, StringComparer.Ordinal);

    /// <summary>
    /// Accounts a selection against a persisted manifest body.
    /// </summary>
    /// <param name="sourceKind">Source kind of the migration, such as <c>arcgis-geoservices-rest</c>.</param>
    /// <param name="manifestBody">Manifest JSON, or <c>null</c> when none was supplied.</param>
    /// <param name="selection">Layers and tables selected for migration.</param>
    /// <returns>The accounting; not executed when the manifest is missing, unreadable or for another source kind.</returns>
    public static MigrationConstructAccountingReport Account(
        string sourceKind,
        string? manifestBody,
        IReadOnlyList<MigrationConstructSelection> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (!string.Equals(sourceKind, GeoservicesSourceKind, StringComparison.Ordinal))
        {
            return NotExecuted(selection, $"no construct matrix is defined for source kind '{sourceKind}'.");
        }

        if (string.IsNullOrWhiteSpace(manifestBody))
        {
            return NotExecuted(selection, "no source manifest was supplied, so the constructs discovered on the source were never accounted for.");
        }

        MigrationManifestArtifact? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBody, MigrationEvidencePackJsonContext.Default.MigrationManifestArtifact);
        }
        catch (JsonException)
        {
            return NotExecuted(selection, "the source manifest could not be parsed.");
        }

        return manifest is null
            ? NotExecuted(selection, "the source manifest was empty.")
            : Account(manifest, selection);
    }

    /// <summary>
    /// Accounts a selection against a source manifest.
    /// </summary>
    /// <param name="manifest">Manifest translated from a source scan.</param>
    /// <param name="selection">Layers and tables selected for migration.</param>
    /// <returns>The accounting.</returns>
    public static MigrationConstructAccountingReport Account(
        MigrationManifestArtifact manifest,
        IReadOnlyList<MigrationConstructSelection> selection)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(selection);

        if (!string.Equals(manifest.SourceKind, GeoservicesSourceKind, StringComparison.Ordinal))
        {
            return NotExecuted(
                selection,
                $"the source manifest describes source kind '{manifest.SourceKind}', not an ArcGIS GeoServices source.");
        }

        var selected = IndexSelection(selection);
        var resources = DiscoverResources(manifest);
        var accounting = new Accounting(selected);

        AccountServiceType(manifest, accounting);

        foreach (var resource in resources.Values.OrderBy(static resource => resource.Id, StringComparer.Ordinal))
        {
            AccountResource(resource, accounting);
        }

        foreach (var sourceResourceId in selected.Keys
            .Where(id => !resources.ContainsKey(id))
            .Order(StringComparer.Ordinal))
        {
            accounting.AddUndiscovered(sourceResourceId);
        }

        AccountClassifications(manifest, resources, accounting);

        foreach (var resource in resources.Values
            .Where(static resource => resource.Target is not null)
            .OrderBy(static resource => resource.Id, StringComparer.Ordinal))
        {
            AccountLabelClasses(resource, accounting);
            AccountEditBehavior(resource, accounting);
        }

        return new MigrationConstructAccountingReport
        {
            Executed = true,
            DiscoveredResourceCount = resources.Count,
            SelectedResourceCount = selected.Count,
            Entries = accounting.Entries
                .OrderBy(static entry => entry.SourceId, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Construct, StringComparer.Ordinal)
                .ThenBy(static entry => entry.AutomationStatus, StringComparer.Ordinal)
                .ToArray(),
            Differences = OrderDifferences(accounting.Differences)
        };
    }

    /// <summary>
    /// An accounting that did not run, with the reason and the unverified difference it contributes.
    /// </summary>
    /// <param name="reason">Why the accounting did not run, as a sentence fragment ending in a period.</param>
    /// <returns>The not-executed accounting.</returns>
    public static MigrationConstructAccountingReport NotExecuted(string reason) => NotExecuted([], reason);

    private static MigrationConstructAccountingReport NotExecuted(
        IReadOnlyList<MigrationConstructSelection> selection,
        string reason) => new()
        {
            Executed = false,
            NotExecutedReason = reason,
            SelectedResourceCount = IndexSelection(selection).Count,
            Differences =
            [
                new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.ConstructAccountingNotExecuted,
                    Severity = MigrationFidelityDifferenceSeverities.Unverified,
                    Subject = "constructs",
                    Expected = "every construct discovered on the source accounted for before apply",
                    Actual = "construct accounting did not run",
                    Summary = "Construct accounting did not run: " + reason
                        + " Full fidelity cannot be proven for this selection."
                }
            ]
        };

    private static Dictionary<string, MigrationConstructSelection> IndexSelection(
        IReadOnlyList<MigrationConstructSelection> selection)
        => selection
            .Where(static item => !string.IsNullOrWhiteSpace(item.SourceResourceId))
            .GroupBy(static item => item.SourceResourceId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

    private static Dictionary<string, DiscoveredResource> DiscoverResources(MigrationManifestArtifact manifest)
    {
        var resources = new Dictionary<string, DiscoveredResource>(StringComparer.Ordinal);
        foreach (var target in manifest.TargetResources.Where(static target => IsLayerOrTable(target.SourceKind)))
        {
            resources.TryAdd(
                target.SourceResourceId,
                new DiscoveredResource(
                    target.SourceResourceId,
                    target.SourceKind,
                    target,
                    target.Compatibility.Level,
                    target.Compatibility.Code));
        }

        // The translator emits no target resource for an incompatible layer or table: it survives only as
        // an unsupported item, and must still be accounted rather than disappear from the denominator.
        foreach (var item in manifest.UnsupportedItems.Where(static item => IsLayerOrTable(item.Kind)))
        {
            resources.TryAdd(item.SourceId, new DiscoveredResource(item.SourceId, item.Kind, null, "incompatible", item.Code));
        }

        return resources;
    }

    private static void AccountServiceType(MigrationManifestArtifact manifest, Accounting accounting)
    {
        var serviceType = string.IsNullOrWhiteSpace(manifest.Source.ServiceType)
            ? LastUrlSegment(manifest.Source.BaseUrl)
            : manifest.Source.ServiceType.Trim();
        var supported = serviceType is not null
            && SupportedServiceTypes.Contains(serviceType, StringComparer.OrdinalIgnoreCase);
        var serviceId = ServiceSourceId(manifest);

        accounting.Add(
            MigrationConstructKeys.ServiceType,
            serviceId,
            serviceScoped: true,
            resourceIds: [],
            supported ? MigrationFidelityAutomationStatuses.Automated : MigrationFidelityAutomationStatuses.Unsupported,
            [serviceType ?? "unknown"],
            reportConstructDifference: false);

        if (!supported && accounting.AnySelected)
        {
            accounting.Differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.ServiceTypeUnsupported,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = serviceId,
                Expected = string.Join(" or ", SupportedServiceTypes),
                Actual = serviceType ?? "service type not advertised",
                Summary = $"Service type '{serviceType ?? "unknown"}' is not supported by service migration, "
                    + "so the service cannot migrate at full fidelity."
            });
        }
    }

    private static void AccountResource(DiscoveredResource resource, Accounting accounting)
    {
        var incompatible = string.Equals(resource.CompatibilityLevel, "incompatible", StringComparison.OrdinalIgnoreCase);
        var codes = resource.CompatibilityCode is null ? Array.Empty<string>() : [resource.CompatibilityCode];

        accounting.Add(
            MigrationConstructKeys.ServiceResource,
            resource.Id,
            serviceScoped: false,
            [resource.Id],
            incompatible ? MigrationFidelityAutomationStatuses.Unsupported : MigrationFidelityAutomationStatuses.Automated,
            codes,
            targetResourceId: resource.Target?.TargetResourceId);

        if (!string.Equals(resource.Kind, "table", StringComparison.OrdinalIgnoreCase))
        {
            // The scan assesses geometry and CRS on the resource: an unsupported geometry type makes the
            // resource incompatible, a missing spatial reference makes it partial. An incompatible resource
            // for any other reason (no query capability) transfers no geometry either.
            var geometryStatus = resource.CompatibilityCode switch
            {
                ImportCompatibilityCodes.ArcGisUnsupportedGeometry => MigrationFidelityAutomationStatuses.Unsupported,
                ImportCompatibilityCodes.ArcGisMissingSpatialRef => MigrationFidelityAutomationStatuses.ManualReview,
                _ when incompatible => MigrationFidelityAutomationStatuses.Unsupported,
                _ => MigrationFidelityAutomationStatuses.Automated
            };
            accounting.Add(
                MigrationConstructKeys.ResourceGeometry,
                resource.Id,
                serviceScoped: false,
                [resource.Id],
                geometryStatus,
                geometryStatus == MigrationFidelityAutomationStatuses.Automated
                    ? (resource.Target?.GeometryType is { } geometryType ? [geometryType] : [])
                    : codes,
                targetResourceId: resource.Target?.TargetResourceId,
                // An incompatible resource already blocks once through its service.resource entry.
                reportConstructDifference: !incompatible);
        }

        if (!accounting.IsSelected(resource.Id))
        {
            accounting.Differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.ServiceResourceUnselected,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = resource.Id,
                Expected = "selected for migration",
                Actual = "not selected",
                Summary = $"The {resource.Kind} '{resource.Id}' was discovered on the source but is not in the migration "
                    + "selection, so the service does not migrate at full fidelity."
            });
        }
    }

    private static void AccountClassifications(
        MigrationManifestArtifact manifest,
        IReadOnlyDictionary<string, DiscoveredResource> resources,
        Accounting accounting)
    {
        if (manifest.FidelityMatrix is null)
        {
            return;
        }

        var styleOwners = BuildStyleOwners(manifest);
        foreach (var cell in manifest.FidelityMatrix.Cells)
        {
            foreach (var sourceId in cell.SourceIds)
            {
                string[] owners = resources.ContainsKey(sourceId)
                    ? [sourceId]
                    : styleOwners.TryGetValue(sourceId, out var styled) ? styled : [];

                // A classification that belongs to no discovered resource (the service root) is accounted at
                // service scope: it travels with any selection.
                var serviceScoped = owners.Length == 0;
                var construct = MigrationConstructMatrix.ConstructForCategory(cell.Category, serviceScoped);
                var targetResourceId = owners.Length == 1 && resources.TryGetValue(owners[0], out var owner)
                    ? owner.Target?.TargetResourceId
                    : null;

                if (construct is null)
                {
                    // A category without a matrix row is never silently dropped: it is accounted and reviewed.
                    accounting.Add(
                        MigrationConstructKeys.Unmapped,
                        sourceId,
                        serviceScoped,
                        owners,
                        MigrationFidelityAutomationStatuses.ManualReview,
                        ["category:" + cell.Category, .. cell.Codes],
                        targetResourceId);
                    continue;
                }

                accounting.Add(construct, sourceId, serviceScoped, owners, cell.AutomationStatus, cell.Codes, targetResourceId);
            }
        }
    }

    private static void AccountLabelClasses(DiscoveredResource resource, Accounting accounting)
    {
        foreach (var labelClass in resource.Target!.LabelClassDiagnostics
            .OrderBy(static item => item.SourceStyleId, StringComparer.Ordinal)
            .ThenBy(static item => item.LabelClassIndex))
        {
            accounting.Add(
                MigrationConstructKeys.ResourceStyles,
                $"{labelClass.SourceStyleId}:label-class:{labelClass.LabelClassIndex}",
                serviceScoped: false,
                [resource.Id],
                labelClass.Classification,
                string.IsNullOrWhiteSpace(labelClass.ExpressionEngine) ? [] : ["expression-engine:" + labelClass.ExpressionEngine],
                resource.Target.TargetResourceId);
        }
    }

    private static void AccountEditBehavior(DiscoveredResource resource, Accounting accounting)
    {
        var editCapabilities = resource.Target!.Capabilities
            .Where(static capability => EditCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (editCapabilities.Length == 0)
        {
            return;
        }

        // Catalog reconciliation verifies schema, domains, identifiers, subtypes and relationships; nothing
        // after apply compares the published edit behavior against the source, so it is reviewed.
        accounting.Add(
            MigrationConstructKeys.ResourceEditBehavior,
            resource.Id,
            serviceScoped: false,
            [resource.Id],
            MigrationFidelityAutomationStatuses.ManualReview,
            editCapabilities,
            resource.Target.TargetResourceId);
    }

    private static Dictionary<string, string[]> BuildStyleOwners(MigrationManifestArtifact manifest)
    {
        var owners = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var action in manifest.StyleActions)
        {
            foreach (var resourceId in action.ResourceIds)
            {
                Owners(action.SourceStyleId).Add(resourceId);
            }
        }

        foreach (var target in manifest.TargetResources)
        {
            foreach (var styleId in target.StyleIds)
            {
                Owners(styleId).Add(target.SourceResourceId);
            }
        }

        return owners.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.Ordinal);

        SortedSet<string> Owners(string styleId)
        {
            if (!owners.TryGetValue(styleId, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                owners[styleId] = set;
            }

            return set;
        }
    }

    private static string ServiceSourceId(MigrationManifestArtifact manifest)
        => manifest.FidelityMatrix?.Cells
            .SelectMany(static cell => cell.SourceIds)
            .Where(static id => id.StartsWith("service:", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
            ?? "service";

    private static string? LastUrlSegment(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : segments[^1];
    }

    private static bool IsLayerOrTable(string? kind)
        => string.Equals(kind, "layer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeStatus(string? status) => status switch
    {
        MigrationFidelityAutomationStatuses.Automated => MigrationFidelityAutomationStatuses.Automated,
        MigrationFidelityAutomationStatuses.Assisted => MigrationFidelityAutomationStatuses.Assisted,
        MigrationFidelityAutomationStatuses.Unsupported => MigrationFidelityAutomationStatuses.Unsupported,
        _ => MigrationFidelityAutomationStatuses.ManualReview
    };

    private static MigrationFidelityDifference[] OrderDifferences(IEnumerable<MigrationFidelityDifference> differences)
        => differences
            .OrderBy(static difference => difference.Code, StringComparer.Ordinal)
            .ThenBy(static difference => difference.Subject ?? string.Empty, StringComparer.Ordinal)
            .ToArray();

    private sealed record DiscoveredResource(
        string Id,
        string Kind,
        MigrationManifestTargetResource? Target,
        string? CompatibilityLevel,
        string? CompatibilityCode);

    private sealed class Accounting(IReadOnlyDictionary<string, MigrationConstructSelection> selected)
    {
        public List<MigrationConstructAccountingEntry> Entries { get; } = [];

        public List<MigrationFidelityDifference> Differences { get; } = [];

        public bool AnySelected => selected.Count > 0;

        public bool IsSelected(string sourceResourceId) => selected.ContainsKey(sourceResourceId);

        public void Add(
            string construct,
            string sourceId,
            bool serviceScoped,
            string[] resourceIds,
            string? automationStatus,
            IEnumerable<string> codes,
            string? targetResourceId = null,
            bool reportConstructDifference = true)
        {
            var status = NormalizeStatus(automationStatus);
            var orderedCodes = codes
                .Where(static code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var isSelected = serviceScoped ? AnySelected : resourceIds.Any(IsSelected);
            var disposition = !isSelected
                ? MigrationConstructDispositions.Unselected
                : status switch
                {
                    MigrationFidelityAutomationStatuses.Automated => MigrationConstructDispositions.Migrated,
                    MigrationFidelityAutomationStatuses.Unsupported => MigrationConstructDispositions.Blocker,
                    _ => MigrationConstructDispositions.Review
                };
            string? targetTable = resourceIds.Length == 1 && selected.TryGetValue(resourceIds[0], out var selection)
                ? selection.TargetTable
                : null;

            Entries.Add(new MigrationConstructAccountingEntry
            {
                Construct = construct,
                SourceId = sourceId,
                ResourceIds = resourceIds,
                AutomationStatus = status,
                Codes = orderedCodes,
                Disposition = disposition,
                Verification = RowsByConstruct.TryGetValue(construct, out var row)
                    ? row.Verification
                    : MigrationConstructVerifications.OperatorReview,
                TargetResourceId = targetResourceId,
                TargetTable = targetTable
            });

            if (!reportConstructDifference || disposition is MigrationConstructDispositions.Unselected or MigrationConstructDispositions.Migrated)
            {
                return;
            }

            var actual = orderedCodes.Length == 0 ? status : $"{status} ({string.Join(", ", orderedCodes)})";
            Differences.Add(disposition == MigrationConstructDispositions.Blocker
                ? new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.ConstructUnsupported,
                    Severity = MigrationFidelityDifferenceSeverities.Blocking,
                    Subject = sourceId,
                    Expected = $"{construct} migrated automatically",
                    Actual = actual,
                    Summary = $"The {construct} construct of '{sourceId}' is not supported by the migration ({actual}), "
                        + "so the selection cannot migrate at full fidelity."
                }
                : new MigrationFidelityDifference
                {
                    Code = MigrationFidelityDifferenceCodes.ConstructReviewRequired,
                    Severity = MigrationFidelityDifferenceSeverities.Unverified,
                    Subject = sourceId,
                    Expected = $"{construct} migrated automatically",
                    Actual = actual,
                    Summary = $"The {construct} construct of '{sourceId}' is carried for operator review ({actual}), not "
                        + "verified automatically, so full fidelity is unproven until an operator confirms it."
                });
        }

        public void AddUndiscovered(string sourceResourceId)
        {
            Entries.Add(new MigrationConstructAccountingEntry
            {
                Construct = MigrationConstructKeys.ServiceResource,
                SourceId = sourceResourceId,
                ResourceIds = [sourceResourceId],
                AutomationStatus = MigrationFidelityAutomationStatuses.Unsupported,
                Disposition = MigrationConstructDispositions.Undiscovered,
                Verification = MigrationConstructVerifications.SelectionAccounting,
                TargetTable = selected[sourceResourceId].TargetTable
            });
            Differences.Add(new MigrationFidelityDifference
            {
                Code = MigrationFidelityDifferenceCodes.ServiceResourceUndiscovered,
                Severity = MigrationFidelityDifferenceSeverities.Blocking,
                Subject = sourceResourceId,
                Expected = "discovered on the source manifest",
                Actual = "not in the source manifest",
                Summary = $"The selected resource '{sourceResourceId}' is not in the source manifest, so none of its "
                    + "constructs were accounted for before apply."
            });
        }
    }
}
