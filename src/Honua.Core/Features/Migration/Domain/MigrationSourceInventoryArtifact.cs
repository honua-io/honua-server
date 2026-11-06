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

public sealed record MigrationSourceInventoryArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.source-inventory";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required MigrationInventoryAuthPosture AuthPosture { get; init; }

    public required MigrationInventoryCompleteness ScanCompleteness { get; init; }

    public required MigrationInventorySummary Summary { get; init; }

    public required MigrationCompatibilityAssessment OverallCompatibility { get; init; }

    public MigrationInventoryContainer[] Containers { get; init; } = [];

    public MigrationInventoryResource[] Resources { get; init; } = [];

    public MigrationInventoryStyle[] Styles { get; init; } = [];

    public MigrationExternalDependency[] ExternalDependencies { get; init; } = [];

    public MigrationFidelityClassificationRecord[] FidelityClassifications { get; init; } = [];

    public MigrationFidelityMatrix? FidelityMatrix { get; init; }
}

public static class MigrationFidelityAutomationStatuses
{
    public const string Automated = "automated";

    public const string Assisted = "assisted";

    public const string ManualReview = "manual-review";

    public const string Unsupported = "unsupported";
}

public sealed record MigrationFidelityClassificationRecord
{
    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public required string Kind { get; init; }

    public required string Category { get; init; }

    public string? Name { get; init; }

    public required string AutomationStatus { get; init; }

    public required string Code { get; init; }

    public required string Reason { get; init; }

    public string? TargetKind { get; init; }

    public string? TargetId { get; init; }

    public string? TargetName { get; init; }

    public string[] ManualSteps { get; init; } = [];

    public string[] RelatedIds { get; init; } = [];

    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record MigrationFidelityMatrix
{
    public MigrationFidelityMatrixSummary Summary { get; init; } = new();

    public MigrationFidelityMatrixCell[] Cells { get; init; } = [];
}

public sealed record MigrationFidelityMatrixSummary
{
    public int TotalCount { get; init; }

    public int AutomatedCount { get; init; }

    public int AssistedCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int UnsupportedCount { get; init; }
}

public sealed record MigrationFidelityMatrixCell
{
    public required string Category { get; init; }

    public required string AutomationStatus { get; init; }

    public int Count { get; init; }

    public string[] Kinds { get; init; } = [];

    public string[] SourceIds { get; init; } = [];

    public string[] Codes { get; init; } = [];

    public string[] TargetIds { get; init; } = [];

    public string[] RelatedIds { get; init; } = [];

    public string[] ManualSteps { get; init; } = [];
}

public sealed record MigrationSourceIdentity
{
    public required string DisplayName { get; init; }

    public required string BaseUrl { get; init; }

    public string? Product { get; init; }

    public string? Version { get; init; }

    public string? Build { get; init; }

    public string? ServiceType { get; init; }
}

public sealed record MigrationInventoryAuthPosture
{
    public required string Mode { get; init; }

    public bool CredentialsSupplied { get; init; }

    public bool AccessConfirmed { get; init; }

    public string[] Notes { get; init; } = [];
}

public sealed record MigrationInventoryCompleteness
{
    public required string Status { get; init; }

    public string[] Warnings { get; init; } = [];

    public string[] MissingArtifacts { get; init; } = [];
}

public sealed record MigrationInventorySummary
{
    public int ContainerCount { get; init; }

    public int ResourceCount { get; init; }

    public int StyleCount { get; init; }

    public int ExternalDependencyCount { get; init; }

    public int CompatibleCount { get; init; }

    public int PartiallyCompatibleCount { get; init; }

    public int IncompatibleCount { get; init; }
}

public sealed record MigrationCompatibilityAssessment
{
    public required string Level { get; init; }

    public string? Code { get; init; }

    public required string Reason { get; init; }

    public string[] Warnings { get; init; } = [];

    public string[] ManualSteps { get; init; } = [];
}

public sealed record MigrationInventoryContainer
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public bool? IsDefault { get; init; }

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public sealed record MigrationInventoryResource
{
    public required string Id { get; init; }

    public required string ContainerId { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? GeometryType { get; init; }

    public int? FeatureCount { get; init; }

    public bool? HasAttachments { get; init; }

    public string[] Capabilities { get; init; } = [];

    public MigrationSpatialReferenceInfo[] SpatialReferences { get; init; } = [];

    public MigrationInventoryField[] Fields { get; init; } = [];

    public string[] StyleIds { get; init; } = [];

    public string[] ExternalDependencyIds { get; init; } = [];

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public sealed record MigrationInventoryField
{
    public required string Name { get; init; }

    public string? Alias { get; init; }

    public required string FieldType { get; init; }

    public bool? Nullable { get; init; }

    public string? DomainType { get; init; }

    public string? DomainName { get; init; }

    public MigrationInventoryCodedValue[]? DomainValues { get; init; }
}

public sealed record MigrationInventoryCodedValue
{
    public required string Code { get; init; }

    public required string Name { get; init; }
}

public sealed record MigrationInventoryStyle
{
    public required string Id { get; init; }

    public required string ContainerId { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? Format { get; init; }

    public string[] ResourceIds { get; init; } = [];

    public string[] ExternalDependencyIds { get; init; } = [];

    public Dictionary<string, string> Metadata { get; init; } = [];

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public sealed record MigrationExternalDependency
{
    public required string Id { get; init; }

    public required string ContainerId { get; init; }

    public string? ResourceId { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public string? DependencyType { get; init; }

    public string? Address { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = [];

    public MigrationSpatialReferenceInfo[] SpatialReferences { get; init; } = [];

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public sealed record MigrationSpatialReferenceInfo
{
    public required string Role { get; init; }

    public string? SourceValue { get; init; }

    public int? Srid { get; init; }

    public string? CrsUri { get; init; }

    public string? Datum { get; init; }

    public string? Unit { get; init; }

    public string? AxisOrder { get; init; }

    public bool? IsGeographic { get; init; }
}
