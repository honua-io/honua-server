// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Builds deterministic migration parity and cutover-readiness evidence packs.
/// </summary>
public static class MigrationParityEvidenceGenerator
{
    private static readonly MigrationCutoverReadinessItemDefinition[] ReadinessChecklist =
    [
        new("inventory-confirmed", "Inventory artifact reviewed"),
        new("manifest-reviewed", "Migration manifest reviewed"),
        new("parity-report-reviewed", "Parity evidence reviewed"),
        new("known-gaps-accepted", "Known gaps accepted"),
        new("rollback-plan-documented", "Rollback plan documented"),
        new("traffic-switch-planned", "DNS or load-balancer change planned")
    ];

    /// <summary>
    /// Generate a parity evidence pack from deterministic migration artifacts.
    /// Missing readiness attestations remain <c>unknown</c>.
    /// </summary>
    /// <param name="inventory">Source inventory artifact.</param>
    /// <param name="manifest">Optional migration manifest artifact.</param>
    /// <param name="attestation">Optional operator-supplied readiness evidence.</param>
    /// <param name="performanceCost">Optional performance and migration-cost measurements.</param>
    /// <returns>Deterministic parity evidence artifact.</returns>
    public static MigrationParityEvidenceArtifact Generate(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestArtifact? manifest = null,
        MigrationReadinessAttestation? attestation = null,
        MigrationPerformanceCostEvidence? performanceCost = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var sections = new[]
        {
            BuildCapabilitySection(inventory),
            BuildStyleSection(inventory),
            BuildDataSection(inventory, manifest),
            BuildFidelityMatrixSection(inventory, manifest),
            BuildFidelitySection(inventory)
        };
        var readiness = BuildReadiness(inventory, manifest, sections, attestation);
        var overallState = AggregateState(sections.Select(section => section.State).Append(readiness.State));

        return new MigrationParityEvidenceArtifact
        {
            SourceKind = inventory.SourceKind,
            Source = inventory.Source,
            OverallState = overallState,
            Summary = BuildSummary(overallState, sections, readiness),
            ManifestAvailable = manifest != null,
            Sections = sections,
            CutoverReadiness = readiness,
            PerformanceCost = NormalizePerformanceCost(performanceCost)
        };
    }

    /// <summary>
    /// Returns a deterministic, secret-safe performance/cost evidence block.
    /// </summary>
    /// <param name="performanceCost">Performance and cost evidence supplied by a scanner, importer, or test harness.</param>
    /// <returns>Normalized evidence, or <c>null</c> when no evidence was supplied.</returns>
    public static MigrationPerformanceCostEvidence? NormalizePerformanceCost(
        MigrationPerformanceCostEvidence? performanceCost)
    {
        if (performanceCost == null)
        {
            return null;
        }

        return performanceCost with
        {
            State = NormalizeState(performanceCost.State),
            EvidenceReferences = SanitizeEvidenceReferences(performanceCost.EvidenceReferences),
            Operations = performanceCost.Operations
                .OrderBy(static operation => operation.Id, StringComparer.Ordinal)
                .ThenBy(static operation => operation.Stage, StringComparer.Ordinal)
                .Select(static operation => operation with
                {
                    State = NormalizeState(operation.State),
                    EvidenceReferences = SanitizeEvidenceReferences(operation.EvidenceReferences)
                })
                .ToArray()
        };
    }

    private static MigrationParityEvidenceSection BuildCapabilitySection(MigrationSourceInventoryArtifact inventory)
    {
        var items = inventory.Resources
            .OrderBy(static resource => resource.Id, StringComparer.Ordinal)
            .Select(resource =>
            {
                if (IsIncompatible(resource.Compatibility.Level))
                {
                    return CreateItem(
                        $"capability:{resource.Id}",
                        MigrationEvidenceStates.Fail,
                        $"{resource.Id} has unsupported source capability.",
                        [resource.Compatibility.Reason],
                        resource.Compatibility.ManualSteps,
                        [resource.Id]);
                }

                if (resource.Capabilities.Length == 0)
                {
                    return CreateItem(
                        $"capability:{resource.Id}",
                        MigrationEvidenceStates.Unknown,
                        $"{resource.Id} has no advertised capability evidence.",
                        ["The source inventory did not expose protocol capability flags for this resource."],
                        ["Verify endpoint availability during the pilot parity run."],
                        [resource.Id]);
                }

                return CreateItem(
                    $"capability:{resource.Id}",
                    MigrationEvidenceStates.Pass,
                    $"{resource.Id} advertised capabilities were captured.",
                    [$"capabilities: {string.Join(", ", resource.Capabilities.OrderBy(static value => value, StringComparer.Ordinal))}"],
                    [],
                    [resource.Id]);
            })
            .ToArray();

        if (items.Length == 0)
        {
            items =
            [
                CreateItem(
                    "capability:none",
                    MigrationEvidenceStates.Unknown,
                    "No source resources were available for capability comparison.",
                    ["The source inventory contained no resources."],
                    ["Rerun discovery or confirm the source is intentionally empty."],
                    [])
            ];
        }

        return CreateSection("capability", "Capability parity", items);
    }

    private static MigrationParityEvidenceSection BuildStyleSection(MigrationSourceInventoryArtifact inventory)
    {
        if (inventory.Styles.Length == 0)
        {
            return CreateSection(
                "style",
                "Style parity",
                [
                    CreateItem(
                        "style:none",
                        MigrationEvidenceStates.NotApplicable,
                        "No source styles were discovered.",
                        ["The source inventory contained no styles or renderers."],
                        [],
                        [])
                ]);
        }

        var items = inventory.Styles
            .OrderBy(static style => style.Id, StringComparer.Ordinal)
            .Select(style =>
            {
                var state = ToEvidenceState(style.Compatibility.Level);
                var remediation = state == MigrationEvidenceStates.Pass
                    ? []
                    : style.Compatibility.ManualSteps.Length > 0
                        ? style.Compatibility.ManualSteps
                        : ["Recreate or import the style and rerun parity review."];

                return CreateItem(
                    $"style:{style.Id}",
                    state,
                    $"{style.Id} style compatibility is {style.Compatibility.Level}.",
                    [style.Compatibility.Reason],
                    remediation,
                    [style.Id, .. style.ResourceIds.OrderBy(static value => value, StringComparer.Ordinal)]);
            })
            .ToArray();

        return CreateSection("style", "Style parity", items);
    }

    private static MigrationParityEvidenceSection BuildDataSection(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestArtifact? manifest)
    {
        var manifestResourceIds = manifest?.TargetResources
            .Select(resource => resource.SourceResourceId)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        var items = inventory.Resources
            .OrderBy(static resource => resource.Id, StringComparer.Ordinal)
            .Select(resource =>
            {
                if (IsIncompatible(resource.Compatibility.Level))
                {
                    return CreateItem(
                        $"data:{resource.Id}",
                        MigrationEvidenceStates.Fail,
                        $"{resource.Id} is not represented as a migratable target resource.",
                        [resource.Compatibility.Reason],
                        resource.Compatibility.ManualSteps,
                        [resource.Id]);
                }

                if (manifest == null || !manifestResourceIds.Contains(resource.Id))
                {
                    return CreateItem(
                        $"data:{resource.Id}",
                        MigrationEvidenceStates.Unknown,
                        $"{resource.Id} has no manifest target evidence.",
                        ["A migration manifest was not available or did not include this resource."],
                        ["Generate and review the migration manifest before pilot cutover."],
                        [resource.Id]);
                }

                var state = IsPartial(resource.Compatibility.Level)
                    ? MigrationEvidenceStates.Unknown
                    : MigrationEvidenceStates.Pass;
                var remediation = state == MigrationEvidenceStates.Pass
                    ? []
                    : resource.Compatibility.ManualSteps.Length > 0
                        ? resource.Compatibility.ManualSteps
                        : ["Complete manual review and rerun the evidence pack."];

                return CreateItem(
                    $"data:{resource.Id}",
                    state,
                    $"{resource.Id} has manifest target evidence.",
                    [$"target: {manifest.TargetResources.First(target => target.SourceResourceId == resource.Id).TargetResourceName}"],
                    remediation,
                    [resource.Id]);
            })
            .ToArray();

        if (items.Length == 0)
        {
            items =
            [
                CreateItem(
                    "data:none",
                    MigrationEvidenceStates.Unknown,
                    "No source resources were available for data parity.",
                    ["The source inventory contained no resources."],
                    ["Rerun discovery or confirm the source is intentionally empty."],
                    [])
            ];
        }

        return CreateSection("data", "Data parity", items);
    }

    private static MigrationParityEvidenceSection BuildFidelitySection(MigrationSourceInventoryArtifact inventory)
    {
        if (inventory.FidelityClassifications.Length == 0)
        {
            return CreateSection(
                "fidelity",
                "Migration fidelity classification",
                [
                    CreateItem(
                        "fidelity:none",
                        MigrationEvidenceStates.NotApplicable,
                        "No source fidelity classifications were emitted.",
                        ["The inventory artifact does not include fidelity classification records."],
                        [],
                        [])
                ]);
        }

        var items = inventory.FidelityClassifications
            .OrderBy(static record => record.Id, StringComparer.Ordinal)
            .Select(record =>
            {
                var state = ToFidelityEvidenceState(record.AutomationStatus);
                var remediation = state == MigrationEvidenceStates.Pass
                    ? []
                    : record.ManualSteps.Length > 0
                        ? record.ManualSteps
                        : ["Review the source construct and document the target migration disposition."];

                return CreateItem(
                    $"fidelity:{record.Id}",
                    state,
                    $"{record.SourceId} {record.Category} is {record.AutomationStatus}.",
                    [$"{record.Code}: {record.Reason}"],
                    remediation,
                    [record.SourceId, .. record.RelatedIds]);
            })
            .ToArray();

        return CreateSection("fidelity", "Migration fidelity classification", items);
    }

    private static MigrationParityEvidenceSection BuildFidelityMatrixSection(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestArtifact? manifest)
    {
        var matrix = manifest?.FidelityMatrix ??
            (manifest != null && inventory.FidelityClassifications.Length > 0
                ? MigrationFidelityMatrixBuilder.Build(inventory.FidelityClassifications, manifest.IdentityRemaps)
                : inventory.FidelityMatrix ?? MigrationFidelityMatrixBuilder.Build(inventory.FidelityClassifications));

        if (matrix == null || matrix.Cells.Length == 0)
        {
            return CreateSection(
                "fidelity-matrix",
                "Migration fidelity matrix",
                [
                    CreateItem(
                        "fidelity-matrix:none",
                        MigrationEvidenceStates.NotApplicable,
                        "No migration fidelity matrix was emitted.",
                        ["The inventory artifact does not include matrix cells."],
                        [],
                        [])
                ]);
        }

        var items = matrix.Cells
            .OrderBy(static cell => cell.Category, StringComparer.Ordinal)
            .ThenBy(static cell => cell.AutomationStatus, StringComparer.Ordinal)
            .Select(cell =>
            {
                var state = ToFidelityEvidenceState(cell.AutomationStatus);
                var remediation = state == MigrationEvidenceStates.Pass
                    ? []
                    : cell.ManualSteps.Length > 0
                        ? cell.ManualSteps
                        : ["Review the matrix cell and document the target migration disposition."];
                var evidence = new List<string>
                {
                    $"count: {cell.Count}",
                    $"codes: {string.Join(", ", cell.Codes)}"
                };

                if (cell.TargetIds.Length > 0)
                {
                    evidence.Add($"targets: {string.Join(", ", cell.TargetIds)}");
                }

                return CreateItem(
                    $"fidelity-matrix:{cell.Category}:{cell.AutomationStatus}",
                    state,
                    $"{cell.Category} has {cell.Count} {cell.AutomationStatus} item(s).",
                    evidence,
                    remediation,
                    [.. cell.SourceIds, .. cell.RelatedIds, .. cell.TargetIds]);
            })
            .ToArray();

        return CreateSection("fidelity-matrix", "Migration fidelity matrix", items);
    }

    private static MigrationCutoverReadinessSummary BuildReadiness(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestArtifact? manifest,
        IReadOnlyList<MigrationParityEvidenceSection> sections,
        MigrationReadinessAttestation? attestation)
    {
        var attested = attestation?.Items.ToDictionary(item => item.Id, StringComparer.Ordinal) ?? [];
        var hasGaps = sections.SelectMany(section => section.Items)
            .Any(item => item.State is MigrationEvidenceStates.Fail or MigrationEvidenceStates.Unknown);

        var items = ReadinessChecklist
            .Select(definition =>
            {
                if (attested.TryGetValue(definition.Id, out var item))
                {
                    return new MigrationCutoverReadinessItem
                    {
                        Id = definition.Id,
                        Title = definition.Title,
                        State = NormalizeState(item.State),
                        Evidence = Order(item.Evidence),
                        Remediation = [],
                        Owner = item.Owner
                    };
                }

                return definition.Id switch
                {
                    "known-gaps-accepted" when !hasGaps => new MigrationCutoverReadinessItem
                    {
                        Id = definition.Id,
                        Title = definition.Title,
                        State = MigrationEvidenceStates.NotApplicable,
                        Evidence = ["No fail or unknown parity evidence items were generated."],
                        Remediation = []
                    },
                    "inventory-confirmed" when string.Equals(inventory.ScanCompleteness.Status, "failed", StringComparison.OrdinalIgnoreCase) =>
                        Unknown(definition, "Inventory scan failed and has not been reviewed.", "Rerun discovery or document an approved waiver."),
                    "manifest-reviewed" when manifest == null =>
                        Unknown(definition, "No migration manifest was supplied.", "Generate and review the migration manifest."),
                    _ => Unknown(definition, "No operator attestation was supplied.", "Record pass, fail, or not-applicable evidence before cutover.")
                };
            })
            .ToArray();

        return new MigrationCutoverReadinessSummary
        {
            State = AggregateState(items.Select(item => item.State)),
            Items = items
        };
    }

    private static MigrationCutoverReadinessItem Unknown(
        MigrationCutoverReadinessItemDefinition definition,
        string evidence,
        string remediation)
        => new()
        {
            Id = definition.Id,
            Title = definition.Title,
            State = MigrationEvidenceStates.Unknown,
            Evidence = [evidence],
            Remediation = [remediation]
        };

    private static MigrationParityEvidenceSection CreateSection(
        string id,
        string title,
        MigrationParityEvidenceItem[] items)
        => new()
        {
            Id = id,
            Title = title,
            State = AggregateState(items.Select(item => item.State)),
            Items = items
        };

    private static MigrationParityEvidenceItem CreateItem(
        string id,
        string state,
        string summary,
        IEnumerable<string> evidence,
        IEnumerable<string> remediation,
        IEnumerable<string> relatedIds)
        => new()
        {
            Id = id,
            State = NormalizeState(state),
            Summary = summary,
            Evidence = Order(evidence),
            Remediation = Order(remediation),
            RelatedIds = Order(relatedIds)
        };

    private static string BuildSummary(
        string overallState,
        IReadOnlyList<MigrationParityEvidenceSection> sections,
        MigrationCutoverReadinessSummary readiness)
    {
        var failCount = sections.SelectMany(section => section.Items).Count(item => item.State == MigrationEvidenceStates.Fail) +
            readiness.Items.Count(item => item.State == MigrationEvidenceStates.Fail);
        var unknownCount = sections.SelectMany(section => section.Items).Count(item => item.State == MigrationEvidenceStates.Unknown) +
            readiness.Items.Count(item => item.State == MigrationEvidenceStates.Unknown);

        return overallState switch
        {
            MigrationEvidenceStates.Pass => "All generated parity and readiness checks passed.",
            MigrationEvidenceStates.Fail => $"Evidence pack has {failCount} failed item(s) and {unknownCount} unknown item(s).",
            _ => $"Evidence pack has {unknownCount} unknown item(s) requiring operator review."
        };
    }

    private static string ToEvidenceState(string level)
    {
        if (IsIncompatible(level))
        {
            return MigrationEvidenceStates.Fail;
        }

        return IsPartial(level) ? MigrationEvidenceStates.Unknown : MigrationEvidenceStates.Pass;
    }

    private static string ToFidelityEvidenceState(string automationStatus)
        => automationStatus switch
        {
            MigrationFidelityAutomationStatuses.Unsupported => MigrationEvidenceStates.Fail,
            MigrationFidelityAutomationStatuses.ManualReview => MigrationEvidenceStates.Unknown,
            MigrationFidelityAutomationStatuses.Assisted => MigrationEvidenceStates.Unknown,
            MigrationFidelityAutomationStatuses.Automated => MigrationEvidenceStates.Pass,
            _ => MigrationEvidenceStates.Unknown
        };

    private static string AggregateState(IEnumerable<string> states)
    {
        var materialized = states.Select(NormalizeState).ToArray();
        if (materialized.Any(static state => state == MigrationEvidenceStates.Fail))
        {
            return MigrationEvidenceStates.Fail;
        }

        if (materialized.Any(static state => state == MigrationEvidenceStates.Unknown))
        {
            return MigrationEvidenceStates.Unknown;
        }

        return MigrationEvidenceStates.Pass;
    }

    private static string NormalizeState(string state)
        => state switch
        {
            MigrationEvidenceStates.Pass => MigrationEvidenceStates.Pass,
            MigrationEvidenceStates.Fail => MigrationEvidenceStates.Fail,
            MigrationEvidenceStates.Unknown => MigrationEvidenceStates.Unknown,
            MigrationEvidenceStates.NotApplicable => MigrationEvidenceStates.NotApplicable,
            _ => MigrationEvidenceStates.Unknown
        };

    private static string[] Order(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string[] SanitizeEvidenceReferences(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => SanitizeEvidenceReference(value.Trim()))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string SanitizeEvidenceReference(string value)
    {
        var sensitiveTailIndex = value.IndexOfAny(['?', '#']);
        var withoutQueryOrFragment = sensitiveTailIndex >= 0 ? value[..sensitiveTailIndex] : value;

        if (!Uri.TryCreate(withoutQueryOrFragment, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.UserInfo))
        {
            return withoutQueryOrFragment;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private static bool IsPartial(string? level)
        => string.Equals(level, "partial", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncompatible(string? level)
        => string.Equals(level, "incompatible", StringComparison.OrdinalIgnoreCase);

    private sealed record MigrationCutoverReadinessItemDefinition(string Id, string Title);
}
