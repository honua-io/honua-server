// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.PackageReview.Domain;

public sealed class PackageReviewRequest
{
    public string? ContractVersion { get; init; }

    public string PackageFamily { get; init; } = string.Empty;

    public string? PackageId { get; init; }

    public string? RequestedAction { get; init; }

    public string? Format { get; init; }

    // Family adapters may inspect the package payload, and it is never executed.
    public JsonElement? PackagePayload { get; init; }

    public PackageReviewRequirements Requirements { get; init => field = value ?? new(); } = new();

    public PackageEstimate? Estimate { get; init; }

    public bool IncludePreviewPlan { get; init; }

    public bool IncludePassFindings { get; init; }

    public IReadOnlyDictionary<string, string> ResourceRefs { get; init => field = value ?? new Dictionary<string, string>(StringComparer.Ordinal); } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public PackageReviewRequest WithPreviewPlan(bool includePreviewPlan)
        => new()
        {
            ContractVersion = ContractVersion,
            PackageFamily = PackageFamily,
            PackageId = PackageId,
            RequestedAction = RequestedAction,
            Format = Format,
            PackagePayload = PackagePayload,
            Requirements = Requirements,
            Estimate = Estimate,
            IncludePreviewPlan = includePreviewPlan,
            IncludePassFindings = IncludePassFindings,
            ResourceRefs = ResourceRefs
        };
}

public sealed class PackageReviewRequirements
{
    public IReadOnlyList<PackageDataBindingRequirement> DataBindings { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackagePermissionRequirement> Permissions { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageSchemaRequirement> Schemas { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageFieldRequirement> Fields { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageDomainRequirement> Domains { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageCrsRequirement> Crs { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageGeometryRequirement> Geometry { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageTemporalRequirement> Temporal { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageCapabilityRequirement> Capabilities { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageDependencyRequirement> Dependencies { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageFormatRequirement> Formats { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PackageApprovalRequirement> Approvals { get; init => field = value ?? []; } = [];
}

public sealed class PackageDataBindingRequirement
{
    public string? Id { get; init; }

    public string? SourceId { get; init; }

    public int? LayerId { get; init; }

    public string? Path { get; init; }

    public bool Required { get; init; } = true;

    public bool IsResolved { get; init; } = true;

    public string? Message { get; init; }
}

public sealed class PackagePermissionRequirement
{
    public string Permission { get; init; } = string.Empty;

    public string? Target { get; init; }

    public string? Path { get; init; }

    public bool Granted { get; init; } = true;

    public string? PolicyRef { get; init; }

    public string AppliesTo { get; init; } = PackageReviewActionScope.Both;
}

public sealed class PackageSchemaRequirement
{
    public string? SchemaId { get; init; }

    public string? Path { get; init; }

    public bool Exists { get; init; } = true;

    public string? ExpectedVersion { get; init; }

    public string? ActualVersion { get; init; }
}

public sealed class PackageFieldRequirement
{
    public string Name { get; init; } = string.Empty;

    public string? Path { get; init; }

    public bool Exists { get; init; } = true;

    public string? ExpectedType { get; init; }

    public string? ActualType { get; init; }
}

public sealed class PackageDomainRequirement
{
    public string Name { get; init; } = string.Empty;

    public string? Field { get; init; }

    public string? Path { get; init; }

    public bool Exists { get; init; } = true;

    public bool IsValid { get; init; } = true;

    public string? Expected { get; init; }

    public string? Actual { get; init; }
}

public sealed class PackageCrsRequirement
{
    public string? Path { get; init; }

    public int? ExpectedSrid { get; init; }

    public int? ActualSrid { get; init; }

    public string? ExpectedUnits { get; init; }

    public string? ActualUnits { get; init; }

    public string? Assumption { get; init; }
}

public sealed class PackageGeometryRequirement
{
    public string? Path { get; init; }

    public string? ExpectedGeometryType { get; init; }

    public string? ActualGeometryType { get; init; }

    public bool AllowsNullGeometry { get; init; } = true;

    public long? NullGeometryCount { get; init; }
}

public sealed class PackageTemporalRequirement
{
    public string? Path { get; init; }

    public bool Required { get; init; } = true;

    public string? Field { get; init; }

    public bool Exists { get; init; } = true;
}

public sealed class PackageCapabilityRequirement
{
    public string Capability { get; init; } = string.Empty;

    public string? Scope { get; init; }

    public string? Path { get; init; }

    public bool Required { get; init; } = true;

    public bool Supported { get; init; } = true;

    public string AppliesTo { get; init; } = PackageReviewActionScope.Both;
}

public sealed class PackageDependencyRequirement
{
    public string PackageId { get; init; } = string.Empty;

    public string? Version { get; init; }

    public string? Path { get; init; }

    public bool Required { get; init; } = true;

    public bool Resolved { get; init; } = true;
}

public sealed class PackageFormatRequirement
{
    public string Format { get; init; } = string.Empty;

    public string? Path { get; init; }

    public bool Supported { get; init; } = true;
}

public sealed class PackageApprovalRequirement
{
    public string? PolicyRef { get; init; }

    public string? Path { get; init; }

    public bool Required { get; init; }

    public bool Approved { get; init; }

    public string AppliesTo { get; init; } = PackageReviewActionScope.Execute;
}

public sealed class PackageReviewResponse
{
    public string ContractVersion { get; init; } = PackageReviewContract.Version;

    public string ReviewId { get; init; } = string.Empty;

    public string PackageFamily { get; init; } = string.Empty;

    public string? PackageId { get; init; }

    public string Status { get; init; } = PackageReviewStatus.Blocked;

    public bool CanExecute { get; init; }

    public bool CanPublish { get; init; }

    public bool RequiresApproval { get; init; }

    public DateTimeOffset CheckedAt { get; init; }

    public IReadOnlyList<PackageFinding> Findings { get; init; } = [];

    public PackagePreviewPlan? PreviewPlan { get; init; }

    public PackageEstimate? Estimate { get; init; }

    public IReadOnlyDictionary<string, string> Links { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ResourceRefs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class PackageFinding
{
    public string Code { get; init; } = string.Empty;

    public string Severity { get; init; } = PackageFindingSeverity.Info;

    public string Category { get; init; } = string.Empty;

    public string Disposition { get; init; } = PackageFindingDisposition.Unresolved;

    public string AppliesTo { get; init; } = PackageReviewActionScope.Both;

    public string Message { get; init; } = string.Empty;

    public PackageRequiredAction? RequiredAction { get; init; }

    public PackageAffectedArtifact? AffectedArtifact { get; init; }

    public IReadOnlyList<PackageFindingEvidence> Evidence { get; init; } = [];
}

public sealed class PackageRequiredAction
{
    public string ActionKind { get; init; } = string.Empty;

    public string? TargetPath { get; init; }

    public string? Title { get; init; }
}

public sealed class PackageAffectedArtifact
{
    public string? Kind { get; init; }

    public string? Id { get; init; }

    public string? Path { get; init; }

    public string? SourceId { get; init; }

    public string? StepId { get; init; }
}

public sealed class PackageFindingEvidence
{
    public string Kind { get; init; } = string.Empty;

    public string? Expected { get; init; }

    public string? Actual { get; init; }

    public string? Path { get; init; }

    public string? FieldName { get; init; }

    public int? Srid { get; init; }

    public string? Capability { get; init; }

    public string? PolicyRef { get; init; }

    public string? ResourceRef { get; init; }
}

public sealed class PackagePreviewPlan
{
    public string PlanId { get; init; } = string.Empty;

    // Preview must not mutate published state.
    public bool MayMutatePublishedState { get; init; }

    public IReadOnlyList<PackagePreviewOperation> Operations { get; init; } = [];
}

public sealed class PackagePreviewOperation
{
    public string OperationId { get; init; } = string.Empty;

    public string OperationKind { get; init; } = string.Empty;

    public IReadOnlyList<string> InputRefs { get; init; } = [];

    public IReadOnlyList<string> OutputArtifactKinds { get; init; } = [];

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    // A preview operation must not mutate published state.
    public bool MayMutatePublishedState { get; init; }

    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
}

public sealed class PackageEstimate
{
    public long? RowCount { get; init; }

    public long? FeatureCount { get; init; }

    public long? Bytes { get; init; }

    public double? DurationMs { get; init; }

    public double? DurationSeconds { get; init; }

    public double? CostWeight { get; init; }

    public string? Confidence { get; init; }
}

public sealed class PackageFamilyReviewResult
{
    public IReadOnlyList<PackageFinding> Findings { get; init; } = [];

    public PackagePreviewPlan? PreviewPlan { get; init; }

    public PackageEstimate? Estimate { get; init; }

    public IReadOnlyDictionary<string, string> Links { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ResourceRefs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class PackageReviewContext
{
    public string? TenantId { get; init; }

    public string? ActorId { get; init; }

    public string? SubjectId { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public IReadOnlyList<string> Roles { get; init; } = [];
}
