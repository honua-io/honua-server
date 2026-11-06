// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

// A discovered construct with no matrix row is still accounted and sent to review, so it cannot be omitted silently.
public static class MigrationConstructMatrix
{
    public const string Version = "2026.1";

    public static IReadOnlyList<MigrationConstructMatrixRow> Rows { get; } =
    [
        Row(MigrationConstructKeys.ServiceType, "service",
            "FeatureServer and MapServer services. Any other ArcGIS service type is unsupported.",
            "source service type", MigrationConstructVerifications.SelectionAccounting),
        Row(MigrationConstructKeys.ServiceIdentity, "service",
            "Service identity and source URL.",
            "fidelity classification 'identity'", MigrationConstructVerifications.SelectionAccounting),
        Row(MigrationConstructKeys.ServiceCapabilities, "service",
            "Service capabilities advertised by the service root.",
            "fidelity classification 'capabilities'", MigrationConstructVerifications.SelectionAccounting),
        Row(MigrationConstructKeys.ServiceResource, "resource",
            "Every queryable layer and nonspatial table in the service is selected and its records are transferred.",
            "manifest target resources and unsupported items", MigrationConstructVerifications.DataReconciliation),
        Row(MigrationConstructKeys.ResourceIdentity, "resource",
            "Object IDs, global IDs and the stable source-to-target resource mapping.",
            "fidelity classification 'identity'", MigrationConstructVerifications.DataReconciliation),
        Row(MigrationConstructKeys.ResourceCapabilities, "resource",
            "Published query behavior.",
            "fidelity classification 'capabilities'", MigrationConstructVerifications.DataReconciliation),
        Row(MigrationConstructKeys.ResourceGeometry, "resource",
            "Geometry type, CRS, Z/M and ordinates.",
            "manifest resource compatibility", MigrationConstructVerifications.DataReconciliation),
        Row(MigrationConstructKeys.ResourceFields, "resource",
            "Fields, types, nullability, defaults, attribute values and nulls.",
            "fidelity classification 'fields'", MigrationConstructVerifications.CatalogReconciliation),
        Row(MigrationConstructKeys.ResourceDomains, "resource",
            "Coded-value and range domains, persisted and enforced on edit.",
            "fidelity classification 'domains'", MigrationConstructVerifications.CatalogReconciliation),
        Row(MigrationConstructKeys.ResourceSubtypes, "resource",
            "Subtypes and subtype-driven editing behavior.",
            "fidelity classification 'subtypes'", MigrationConstructVerifications.OperatorReview),
        Row(MigrationConstructKeys.ResourceRelationships, "resource",
            "Relationship classes and their keys.",
            "fidelity classification 'relationships'", MigrationConstructVerifications.RelationshipApply),
        Row(MigrationConstructKeys.ResourceAttachments, "resource",
            "Attachment contents and counts.",
            "fidelity classification 'attachments'", MigrationConstructVerifications.AttachmentReconciliation),
        Row(MigrationConstructKeys.ResourceStyles, "resource",
            "Renderers and label classes.",
            "fidelity classification 'renderers' and manifest label class diagnostics", MigrationConstructVerifications.OperatorReview),
        Row(MigrationConstructKeys.ResourceTimeMetadata, "resource",
            "Time metadata.",
            "fidelity classification 'time-metadata'", MigrationConstructVerifications.OperatorReview),
        Row(MigrationConstructKeys.ResourceEditBehavior, "resource",
            "Published edit behavior (create, update, delete).",
            "manifest resource capabilities", MigrationConstructVerifications.OperatorReview)
    ];

    public static string? ConstructForCategory(string? category, bool serviceScoped) => category switch
    {
        "identity" => serviceScoped ? MigrationConstructKeys.ServiceIdentity : MigrationConstructKeys.ResourceIdentity,
        "capabilities" => serviceScoped ? MigrationConstructKeys.ServiceCapabilities : MigrationConstructKeys.ResourceCapabilities,
        "fields" => MigrationConstructKeys.ResourceFields,
        "domains" => MigrationConstructKeys.ResourceDomains,
        "subtypes" => MigrationConstructKeys.ResourceSubtypes,
        "relationships" => MigrationConstructKeys.ResourceRelationships,
        "attachments" => MigrationConstructKeys.ResourceAttachments,
        "renderers" => MigrationConstructKeys.ResourceStyles,
        "time-metadata" => MigrationConstructKeys.ResourceTimeMetadata,
        _ => null
    };

    private static MigrationConstructMatrixRow Row(
        string construct,
        string scope,
        string covers,
        string evidence,
        string verification) => new()
        {
            Construct = construct,
            Scope = scope,
            Covers = covers,
            Evidence = evidence,
            Verification = verification
        };
}

public static class MigrationConstructKeys
{
    public const string ServiceType = "service.type";

    public const string ServiceIdentity = "service.identity";

    public const string ServiceCapabilities = "service.capabilities";

    public const string ServiceResource = "service.resource";

    public const string ResourceIdentity = "resource.identity";

    public const string ResourceCapabilities = "resource.capabilities";

    public const string ResourceGeometry = "resource.geometry";

    public const string ResourceFields = "resource.fields";

    public const string ResourceDomains = "resource.domains";

    public const string ResourceSubtypes = "resource.subtypes";

    public const string ResourceRelationships = "resource.relationships";

    public const string ResourceAttachments = "resource.attachments";

    public const string ResourceStyles = "resource.styles";

    public const string ResourceTimeMetadata = "resource.time-metadata";

    public const string ResourceEditBehavior = "resource.edit-behavior";

    public const string Unmapped = "unmapped";
}

public static class MigrationConstructVerifications
{
    public const string SelectionAccounting = "selection-accounting";

    public const string DataReconciliation = "data-reconciliation";

    public const string CatalogReconciliation = "catalog-reconciliation";

    public const string AttachmentReconciliation = "attachment-reconciliation";

    public const string RelationshipApply = "relationship-apply";

    public const string OperatorReview = "operator-review";
}

public static class MigrationConstructDispositions
{
    public const string Migrated = "migrated";

    // A selection captured only for review cannot prove full fidelity.
    public const string Review = "review";

    public const string Blocker = "blocker";

    public const string Unselected = "unselected";

    // A selection that was never discovered accounts none of its constructs.
    public const string Undiscovered = "undiscovered";
}

public sealed record MigrationConstructMatrixRow
{
    public required string Construct { get; init; }

    public required string Scope { get; init; }

    public required string Covers { get; init; }

    public required string Evidence { get; init; }

    public required string Verification { get; init; }
}

public sealed record MigrationConstructAccountingReport
{
    public string MatrixVersion { get; init; } = MigrationConstructMatrix.Version;

    // When accounting did not cover every discovered construct, full fidelity cannot be proven.
    public bool Executed { get; init; }

    public string? NotExecutedReason { get; init; }

    public int DiscoveredResourceCount { get; init; }

    public int SelectedResourceCount { get; init; }

    public MigrationConstructAccountingEntry[] Entries { get; init; } = [];

    public MigrationFidelityDifference[] Differences { get; init; } = [];

    public bool IsBlocking => Differences.Any(static difference => string.Equals(
        difference.Severity,
        MigrationFidelityDifferenceSeverities.Blocking,
        StringComparison.Ordinal));
}

public sealed record MigrationConstructAccountingEntry
{
    public required string Construct { get; init; }

    public required string SourceId { get; init; }

    public string[] ResourceIds { get; init; } = [];

    public required string AutomationStatus { get; init; }

    public string[] Codes { get; init; } = [];

    public required string Disposition { get; init; }

    public required string Verification { get; init; }

    public string? TargetResourceId { get; init; }

    public string? TargetTable { get; init; }
}

public sealed record MigrationConstructSelection
{
    public required string SourceResourceId { get; init; }

    public string? TargetTable { get; init; }
}

public sealed class MigrationConstructAccountingRefusedException : InvalidOperationException
{
    public MigrationConstructAccountingRefusedException(MigrationConstructAccountingReport report)
        : base(BuildMessage(report))
    {
        Report = report;
    }

    public MigrationConstructAccountingReport Report { get; }

    private static string BuildMessage(MigrationConstructAccountingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.Executed)
        {
            return "The migration selection was not started: construct accounting did not run because "
                + report.NotExecutedReason;
        }

        var blocking = report.Differences.Count(static difference => string.Equals(
            difference.Severity,
            MigrationFidelityDifferenceSeverities.Blocking,
            StringComparison.Ordinal));
        return $"The migration selection was not started: construct accounting found {blocking} blocking difference(s). "
            + string.Join(" ", report.Differences
                .Where(static difference => string.Equals(
                    difference.Severity,
                    MigrationFidelityDifferenceSeverities.Blocking,
                    StringComparison.Ordinal))
                .Select(static difference => difference.Summary));
    }
}
