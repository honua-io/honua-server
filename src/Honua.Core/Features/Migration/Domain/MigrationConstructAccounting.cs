// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Versioned source-to-target construct matrix for ArcGIS GeoServices service migration (issue #4600,
/// acceptance criterion 1). Every construct a service migration must carry is a row; each row names the
/// pre-apply evidence it is accounted from and the post-apply check that verifies it on the target.
/// </summary>
/// <remarks>
/// The matrix is the denominator of a full-fidelity service migration. A discovered construct that has
/// no row is still accounted (as <see cref="MigrationConstructKeys.Unmapped"/>) and routed to review, so
/// a new source construct can never be silently omitted. Bump <see cref="Version"/> whenever a row is
/// added, removed or changes its verification.
/// </remarks>
public static class MigrationConstructMatrix
{
    /// <summary>Matrix version recorded on every accounting report.</summary>
    public const string Version = "2026.1";

    /// <summary>The matrix rows, in accounting order.</summary>
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

    /// <summary>
    /// Maps a source fidelity classification category onto its matrix construct, or <c>null</c> when the
    /// category has no row.
    /// </summary>
    /// <param name="category">Classification category, such as <c>fields</c>.</param>
    /// <param name="serviceScoped">True when the classification describes the service rather than a resource.</param>
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

/// <summary>Stable construct keys used by <see cref="MigrationConstructMatrix"/>.</summary>
public static class MigrationConstructKeys
{
    /// <summary>ArcGIS service type.</summary>
    public const string ServiceType = "service.type";

    /// <summary>Service identity.</summary>
    public const string ServiceIdentity = "service.identity";

    /// <summary>Service capabilities.</summary>
    public const string ServiceCapabilities = "service.capabilities";

    /// <summary>A discovered layer or table and its records.</summary>
    public const string ServiceResource = "service.resource";

    /// <summary>Resource identity: object and global IDs.</summary>
    public const string ResourceIdentity = "resource.identity";

    /// <summary>Resource query capability.</summary>
    public const string ResourceCapabilities = "resource.capabilities";

    /// <summary>Geometry type, CRS and Z/M.</summary>
    public const string ResourceGeometry = "resource.geometry";

    /// <summary>Fields, attribute values and nulls.</summary>
    public const string ResourceFields = "resource.fields";

    /// <summary>Field domains.</summary>
    public const string ResourceDomains = "resource.domains";

    /// <summary>Subtypes.</summary>
    public const string ResourceSubtypes = "resource.subtypes";

    /// <summary>Relationship classes.</summary>
    public const string ResourceRelationships = "resource.relationships";

    /// <summary>Attachments.</summary>
    public const string ResourceAttachments = "resource.attachments";

    /// <summary>Renderers and label classes.</summary>
    public const string ResourceStyles = "resource.styles";

    /// <summary>Time metadata.</summary>
    public const string ResourceTimeMetadata = "resource.time-metadata";

    /// <summary>Published edit behavior.</summary>
    public const string ResourceEditBehavior = "resource.edit-behavior";

    /// <summary>A discovered construct whose category has no matrix row.</summary>
    public const string Unmapped = "unmapped";
}

/// <summary>How a matrix construct is verified on the target after apply.</summary>
public static class MigrationConstructVerifications
{
    /// <summary>Decided entirely by the pre-apply accounting.</summary>
    public const string SelectionAccounting = "selection-accounting";

    /// <summary>The per-layer data-movement reconciliation probe (counts, object IDs, values).</summary>
    public const string DataReconciliation = "data-reconciliation";

    /// <summary>The per-layer catalog reconciliation (schema, domains, identifiers, subtypes).</summary>
    public const string CatalogReconciliation = "catalog-reconciliation";

    /// <summary>Attachment copy reconciled on its own evidence.</summary>
    public const string AttachmentReconciliation = "attachment-reconciliation";

    /// <summary>Relationship apply outcomes after every layer has published.</summary>
    public const string RelationshipApply = "relationship-apply";

    /// <summary>Not verified automatically; an operator confirms the construct before cutover.</summary>
    public const string OperatorReview = "operator-review";
}

/// <summary>How a discovered (or selected) construct is disposed of by a service migration.</summary>
public static class MigrationConstructDispositions
{
    /// <summary>Selected and carried by the automated import, then verified by its row's check.</summary>
    public const string Migrated = "migrated";

    /// <summary>Selected, but captured only for operator review; it cannot prove full fidelity.</summary>
    public const string Review = "review";

    /// <summary>Selected, but not supported by the migration; it blocks full fidelity.</summary>
    public const string Blocker = "blocker";

    /// <summary>Discovered on the source but left out of the selection; it blocks full fidelity.</summary>
    public const string Unselected = "unselected";

    /// <summary>Selected, but never discovered on the source, so none of its constructs were accounted.</summary>
    public const string Undiscovered = "undiscovered";
}

/// <summary>One row of <see cref="MigrationConstructMatrix"/>.</summary>
public sealed record MigrationConstructMatrixRow
{
    /// <summary>Stable construct key from <see cref="MigrationConstructKeys"/>.</summary>
    public required string Construct { get; init; }

    /// <summary><c>service</c> or <c>resource</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>What the construct covers.</summary>
    public required string Covers { get; init; }

    /// <summary>The pre-apply evidence the construct is accounted from.</summary>
    public required string Evidence { get; init; }

    /// <summary>The post-apply check from <see cref="MigrationConstructVerifications"/>.</summary>
    public required string Verification { get; init; }
}

/// <summary>
/// Pre-apply accounting of every construct discovered on the source against a service migration's
/// selection (issue #4600, acceptance criterion 1).
/// </summary>
public sealed record MigrationConstructAccountingReport
{
    /// <summary>The <see cref="MigrationConstructMatrix.Version"/> the accounting ran against.</summary>
    public string MatrixVersion { get; init; } = MigrationConstructMatrix.Version;

    /// <summary>
    /// True when a source manifest was available and every discovered construct was accounted. When
    /// false, <see cref="NotExecutedReason"/> says why and full fidelity cannot be proven.
    /// </summary>
    public bool Executed { get; init; }

    /// <summary>Why the accounting did not run, when <see cref="Executed"/> is false.</summary>
    public string? NotExecutedReason { get; init; }

    /// <summary>Layers and tables discovered on the source.</summary>
    public int DiscoveredResourceCount { get; init; }

    /// <summary>Layers and tables in the migration selection.</summary>
    public int SelectedResourceCount { get; init; }

    /// <summary>One entry per accounted construct, ordered by source id then construct.</summary>
    public MigrationConstructAccountingEntry[] Entries { get; init; } = [];

    /// <summary>
    /// Differences the accounting contributes to the fidelity verdict, ordered by code then subject.
    /// </summary>
    public MigrationFidelityDifference[] Differences { get; init; } = [];

    /// <summary>True when at least one difference is blocking.</summary>
    public bool IsBlocking => Differences.Any(static difference => string.Equals(
        difference.Severity,
        MigrationFidelityDifferenceSeverities.Blocking,
        StringComparison.Ordinal));
}

/// <summary>One accounted construct.</summary>
public sealed record MigrationConstructAccountingEntry
{
    /// <summary>Construct key from <see cref="MigrationConstructKeys"/>.</summary>
    public required string Construct { get; init; }

    /// <summary>Source identifier the construct was discovered on (resource, style or service id).</summary>
    public required string SourceId { get; init; }

    /// <summary>Source resource ids that own the construct.</summary>
    public string[] ResourceIds { get; init; } = [];

    /// <summary>Classification automation status from <see cref="MigrationFidelityAutomationStatuses"/>.</summary>
    public required string AutomationStatus { get; init; }

    /// <summary>Stable compatibility or fidelity codes behind the status.</summary>
    public string[] Codes { get; init; } = [];

    /// <summary>Disposition from <see cref="MigrationConstructDispositions"/>.</summary>
    public required string Disposition { get; init; }

    /// <summary>The post-apply check from <see cref="MigrationConstructVerifications"/>.</summary>
    public required string Verification { get; init; }

    /// <summary>Planned target resource id from the manifest, when the construct belongs to one resource.</summary>
    public string? TargetResourceId { get; init; }

    /// <summary>Target table the selection imports the resource into, when selected.</summary>
    public string? TargetTable { get; init; }
}

/// <summary>One layer or table in a service migration selection.</summary>
public sealed record MigrationConstructSelection
{
    /// <summary>Stable source resource id, such as <c>resource:Inspections:layer:0</c>.</summary>
    public required string SourceResourceId { get; init; }

    /// <summary>Target table, qualified with its schema when one was requested.</summary>
    public string? TargetTable { get; init; }
}

/// <summary>
/// Raised when a service migration asked to refuse a selection that cannot migrate at full fidelity and
/// the pre-apply accounting found a blocking construct, or could not run.
/// </summary>
public sealed class MigrationConstructAccountingRefusedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="MigrationConstructAccountingRefusedException"/> class.</summary>
    /// <param name="report">The accounting that refused the selection.</param>
    public MigrationConstructAccountingRefusedException(MigrationConstructAccountingReport report)
        : base(BuildMessage(report))
    {
        Report = report;
    }

    /// <summary>The accounting that refused the selection.</summary>
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
