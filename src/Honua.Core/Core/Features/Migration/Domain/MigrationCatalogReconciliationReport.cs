// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Domain;

public sealed record MigrationCatalogReconciliationReport
{
    public string ArtifactKind { get; init; } = "honua.migration.catalog-reconciliation-report";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string SourceKind { get; init; }

    public required MigrationCatalogReconciliationSummary Summary { get; init; }

    public MigrationCatalogReconciliationResource[] Resources { get; init; } = [];
}

public sealed record MigrationCatalogReconciliationSummary
{
    public int ResourceCount { get; init; }

    public int PassResourceCount { get; init; }

    public int WarnResourceCount { get; init; }

    public int FailResourceCount { get; init; }

    public int NotApplicableResourceCount { get; init; }

    public int FindingCount { get; init; }
}

public sealed record MigrationCatalogReconciliationResource
{
    public required string SourceResourceId { get; init; }

    public string? TargetResourceId { get; init; }

    public required string Classification { get; init; }

    public MigrationCatalogReconciliationFinding[] Findings { get; init; } = [];
}

public sealed record MigrationCatalogReconciliationFinding
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public string? Subject { get; init; }

    public string? Expected { get; init; }

    public string? Actual { get; init; }

    public required string Summary { get; init; }
}

public sealed record MigrationCatalogReconciliationInput
{
    public required MigrationInventoryResource Resource { get; init; }

    public MetadataV2Object? PublishedResource { get; init; }

    public MigrationManifestRelationshipRecord[] ExpectedRelationships { get; init; } = [];

    public double[]? SourceBbox { get; init; }

    public int? PlannedTargetSrid { get; init; }

    public ExtentBox? ObservedTargetExtent { get; init; }

    public string? TargetResourceId { get; init; }

    public Honua.Core.Features.Metadata.Domain.V2.MetadataV2Subtypes? ExpectedSubtypes { get; init; }
}

public static class MigrationCatalogReconciliationCodes
{
    public const string ResourceMissing = "catalog.resource.missing";

    public const string FieldMissing = "catalog.field.missing";

    public const string FieldTypeMismatch = "catalog.field.type-mismatch";

    public const string DomainMissing = "catalog.field.domain-missing";

    public const string DomainNameMismatch = "catalog.field.domain-name-mismatch";

    public const string DomainValuesMismatch = "catalog.field.domain-values-mismatch";

    public const string GeometryTypeMismatch = "catalog.spatial.geometry-type-mismatch";

    public const string SridMismatch = "catalog.spatial.srid-mismatch";

    public const string ExtentMissing = "catalog.spatial.extent-missing";

    public const string ExtentMismatch = "catalog.spatial.extent-mismatch";

    public const string ObjectIdMissing = "catalog.identifier.object-id-missing";

    public const string GlobalIdMissing = "catalog.identifier.global-id-missing";

    public const string RelationshipMissing = "catalog.relationship.missing";

    public const string RelationshipCompositeDowngraded = "catalog.relationship.composite-downgraded";

    public const string AttachmentMissing = "catalog.attachment.missing";

    public const string SubtypeMissing = "catalog.subtype.missing";

    public const string SubtypeFieldMismatch = "catalog.subtype.field-mismatch";

    public const string SubtypeCodeMissing = "catalog.subtype.code-missing";
}

public static class MigrationCatalogReconciliationSeverities
{
    public const string Fail = "fail";

    public const string Warn = "warn";

    public const string Info = "info";
}

public static class MigrationCatalogReconciliationClassifications
{
    public const string Pass = "pass";

    public const string Warn = "warn";

    public const string Fail = "fail";

    public const string NotApplicable = "not-applicable";
}
