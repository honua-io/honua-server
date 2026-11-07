// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record MigrationManifestArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.manifest";

    public string ArtifactVersion { get; init; } = "1.0";

    public string SourceArtifactKind { get; init; } = "honua.migration.source-inventory";

    public string SourceArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required MigrationManifestSummary Summary { get; init; }

    public MigrationManifestTargetResource[] TargetResources { get; init; } = [];

    public MigrationManifestStyleAction[] StyleActions { get; init; } = [];

    public MigrationManifestServicePlan[] ServicePlans { get; init; } = [];

    public MigrationManifestIdentityRemap[] IdentityRemaps { get; init; } = [];

    public Dictionary<string, string> IdentityRemapping { get; init; } = new(StringComparer.Ordinal);

    public MigrationFidelityMatrix? FidelityMatrix { get; init; }

    public MigrationManifestReviewItem[] ManualReviewItems { get; init; } = [];

    public MigrationManifestReviewItem[] UnsupportedItems { get; init; } = [];
}

public sealed record MigrationManifestSummary
{
    public int SourceResourceCount { get; init; }

    public int TargetResourceCount { get; init; }

    public int StyleActionCount { get; init; }

    public int ServicePlanCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int UnsupportedCount { get; init; }
}

public sealed record MigrationManifestTargetResource
{
    public required string SourceResourceId { get; init; }

    public required string SourceKind { get; init; }

    public required string Action { get; init; }

    public required string TargetResourceId { get; init; }

    public required string TargetServiceName { get; init; }

    public required string TargetResourceName { get; init; }

    public string? GeometryType { get; init; }

    public string? MigrationMode { get; init; }

    public string? SourceProtocol { get; init; }

    public MigrationInventoryField[] Fields { get; init; } = [];

    public string[] Capabilities { get; init; } = [];

    public MigrationSpatialReferenceInfo[] SpatialReferences { get; init; } = [];

    public string[] StyleIds { get; init; } = [];

    public string[] ExternalDependencyIds { get; init; } = [];

    public MigrationManifestResourceIdentity? Identity { get; init; }

    public MigrationManifestAttachmentRecord[] Attachments { get; init; } = [];

    public MigrationManifestRelationshipRecord[] Relationships { get; init; } = [];

    public MigrationManifestRendererDiagnostic[] RendererDiagnostics { get; init; } = [];

    public MigrationManifestLabelClassDiagnostic[] LabelClassDiagnostics { get; init; } = [];

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public static class MigrationManifestRendererClassifications
{
    public const string Automated = "automated";

    public const string Assisted = "assisted";

    public const string ManualReview = "manual-review";

    public const string Unsupported = "unsupported";
}

public sealed record MigrationManifestRendererDiagnostic
{
    public required string SourceStyleId { get; init; }

    public required string SourceResourceId { get; init; }

    public required string RendererKind { get; init; }

    public int? CategoryCount { get; init; }

    public required string Classification { get; init; }

    public string? SuggestedTargetStyleId { get; init; }

    public string? Reason { get; init; }
}

public static class MigrationManifestLabelClassClassifications
{
    public const string Automated = "automated";

    public const string Assisted = "assisted";

    public const string ManualReview = "manual-review";

    public const string Unsupported = "unsupported";
}

public sealed record MigrationManifestLabelClassDiagnostic
{
    public required string SourceStyleId { get; init; }

    public required string SourceResourceId { get; init; }

    public int LabelClassIndex { get; init; }

    public string? Expression { get; init; }

    public string? ExpressionEngine { get; init; }

    public required string Classification { get; init; }

    public string? Reason { get; init; }
}

public static class MigrationManifestAttachmentClassifications
{
    public const string Automated = "automated";

    public const string Assisted = "assisted";

    public const string ManualReview = "manual-review";

    public const string Unsupported = "unsupported";
}

public sealed record MigrationManifestAttachmentRecord
{
    public required string SourceAttachmentId { get; init; }

    public required string SourceResourceId { get; init; }

    public string? Name { get; init; }

    public string? ContentType { get; init; }

    public long? Size { get; init; }

    public required string Classification { get; init; }

    public string? TargetAttachmentRef { get; init; }

    public string? Reason { get; init; }
}

public static class MigrationManifestRelationshipClassifications
{
    public const string Automated = "automated";

    public const string Assisted = "assisted";

    public const string ManualReview = "manual-review";

    public const string Unsupported = "unsupported";
}

public sealed record MigrationManifestRelationshipRecord
{
    public required string SourceRelationshipId { get; init; }

    public required string SourceResourceId { get; init; }

    public string? Name { get; init; }

    public string? Cardinality { get; init; }

    public string? RelationshipType { get; init; }

    public string[] RelatedLayerIds { get; init; } = [];

    public string? Role { get; init; }

    // For a simple ArcGIS relationship this matches DestinationKeyField because both sides share one keyField.
    public string? OriginKeyField { get; init; }

    public string? DestinationKeyField { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("esriRelationshipId")]
    public int? GeoServicesRelationshipId { get; init; }

    public required string Classification { get; init; }

    public string? TargetRelationshipRef { get; init; }

    public string? Reason { get; init; }
}

public sealed record MigrationManifestServicePlan
{
    public required string SourceContainerId { get; init; }

    public required string SourceKind { get; init; }

    public required string Action { get; init; }

    public required string TargetServiceName { get; init; }

    public string? ServiceType { get; init; }

    public string[] ResourceIds { get; init; } = [];

    public string[] StyleIds { get; init; } = [];

    public string[] ExternalDependencyIds { get; init; } = [];

    public MigrationManifestPlanEntry[] PlanEntries { get; init; } = [];

    public MigrationManifestPlanDiagnostic[] Diagnostics { get; init; } = [];

    public MigrationManifestServiceIdentity? Identity { get; init; }

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public sealed record MigrationManifestPlanEntry
{
    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public required string SourceKind { get; init; }

    public required string Category { get; init; }

    public required string AutomationStatus { get; init; }

    public required string Code { get; init; }

    public string? Name { get; init; }

    public required string Reason { get; init; }

    public string[] ManualSteps { get; init; } = [];

    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record MigrationManifestPlanDiagnostic
{
    public required string SourceId { get; init; }

    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Message { get; init; }
}

public sealed record MigrationManifestStyleAction
{
    public required string SourceStyleId { get; init; }

    public required string TargetStyleId { get; init; }

    public required string Action { get; init; }

    public string? Format { get; init; }

    public string[] ResourceIds { get; init; } = [];

    public string[] ExternalDependencyIds { get; init; } = [];

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public sealed record MigrationManifestIdentityRemap
{
    public required string SourceId { get; init; }

    public required string SourceKind { get; init; }

    public required string TargetId { get; init; }

    public required string TargetKind { get; init; }

    public required string TargetName { get; init; }

    public required string Action { get; init; }

    public string? IdentityStability { get; init; }

    public string? Reason { get; init; }
}

public static class MigrationManifestIdentityStabilities
{
    public const string Preserved = "preserved";

    public const string Remapped = "remapped";

    public const string Synthesized = "synthesized";
}

public sealed record MigrationManifestResourceIdentity
{
    public string? SourceServiceId { get; init; }

    public string? SourceLayerId { get; init; }

    public string? SourceQualifiedName { get; init; }

    public string? SourceFolderPath { get; init; }

    public string? TargetServiceId { get; init; }

    public string? TargetLayerId { get; init; }

    public string? TargetName { get; init; }

    public string? TargetFolderPath { get; init; }

    public required string IdentityStability { get; init; }

    public string? IdentityRemapReason { get; init; }
}

public sealed record MigrationManifestServiceIdentity
{
    public string? SourceServiceId { get; init; }

    public string? SourceQualifiedName { get; init; }

    public string? SourceFolderPath { get; init; }

    public string? TargetServiceId { get; init; }

    public string? TargetName { get; init; }

    public string? TargetFolderPath { get; init; }

    public required string IdentityStability { get; init; }

    public string? IdentityRemapReason { get; init; }
}

public sealed record MigrationManifestReviewItem
{
    public required string SourceId { get; init; }

    public required string Kind { get; init; }

    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Reason { get; init; }

    public string[] ManualSteps { get; init; } = [];

    public string[] Warnings { get; init; } = [];
}

public sealed record MigrationManifestTranslationOptions
{
    public string? TargetServiceName { get; init; }
}
