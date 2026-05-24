// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.PackageReview.Domain;

/// <summary>
/// Request for validating a package and optionally generating a read-only preview plan.
/// </summary>
public sealed class PackageReviewRequest
{
    /// <summary>
    /// Package-review contract version requested by the caller.
    /// </summary>
    public string? ContractVersion { get; init; }

    /// <summary>
    /// Package family, such as <c>analysis_plan</c>, <c>map_package</c>, or <c>etl</c>.
    /// </summary>
    public string PackageFamily { get; init; } = string.Empty;

    /// <summary>
    /// Stable caller-visible package identifier when one exists.
    /// </summary>
    public string? PackageId { get; init; }

    /// <summary>
    /// Caller-requested action being reviewed.
    /// </summary>
    public string? RequestedAction { get; init; }

    /// <summary>
    /// Package format identifier, such as <c>honua-spec</c>, <c>geojson</c>, or <c>mcp-plan</c>.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Opaque package payload. Family adapters may inspect this value, but it is never executed.
    /// </summary>
    public JsonElement? PackagePayload { get; init; }

    /// <summary>
    /// Shared validation requirements used by all package families.
    /// </summary>
    public PackageReviewRequirements Requirements { get; init; } = new();

    /// <summary>
    /// Caller-supplied estimate from an upstream planner, when available.
    /// </summary>
    public PackageEstimate? Estimate { get; init; }

    /// <summary>
    /// Whether the response should include a read-only preview plan.
    /// </summary>
    public bool IncludePreviewPlan { get; init; }

    /// <summary>
    /// Whether non-blocking pass findings should be emitted.
    /// </summary>
    public bool IncludePassFindings { get; init; }

    /// <summary>
    /// Safe caller-visible resource references associated with the review.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResourceRefs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Creates a copy of this request with the supplied preview-plan flag.
    /// </summary>
    /// <param name="includePreviewPlan">Whether the copy should request a preview plan.</param>
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

/// <summary>
/// Shared validation requirements independent of protocol or client.
/// </summary>
public sealed class PackageReviewRequirements
{
    /// <summary>Data bindings required by the package.</summary>
    public IReadOnlyList<PackageDataBindingRequirement> DataBindings { get; init; } = [];

    /// <summary>Permissions required by the package.</summary>
    public IReadOnlyList<PackagePermissionRequirement> Permissions { get; init; } = [];

    /// <summary>Schema requirements for package inputs or outputs.</summary>
    public IReadOnlyList<PackageSchemaRequirement> Schemas { get; init; } = [];

    /// <summary>Field requirements for package inputs or outputs.</summary>
    public IReadOnlyList<PackageFieldRequirement> Fields { get; init; } = [];

    /// <summary>Domain requirements for coded values or constraints.</summary>
    public IReadOnlyList<PackageDomainRequirement> Domains { get; init; } = [];

    /// <summary>Coordinate reference system requirements.</summary>
    public IReadOnlyList<PackageCrsRequirement> Crs { get; init; } = [];

    /// <summary>Geometry type requirements.</summary>
    public IReadOnlyList<PackageGeometryRequirement> Geometry { get; init; } = [];

    /// <summary>Temporal requirements.</summary>
    public IReadOnlyList<PackageTemporalRequirement> Temporal { get; init; } = [];

    /// <summary>Service or server capability requirements.</summary>
    public IReadOnlyList<PackageCapabilityRequirement> Capabilities { get; init; } = [];

    /// <summary>Package dependency requirements.</summary>
    public IReadOnlyList<PackageDependencyRequirement> Dependencies { get; init; } = [];

    /// <summary>Package format requirements.</summary>
    public IReadOnlyList<PackageFormatRequirement> Formats { get; init; } = [];

    /// <summary>Approval or admission-control requirements.</summary>
    public IReadOnlyList<PackageApprovalRequirement> Approvals { get; init; } = [];
}

/// <summary>
/// Data binding required by a package.
/// </summary>
public sealed class PackageDataBindingRequirement
{
    /// <summary>Requirement identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Source identifier or URI.</summary>
    public string? SourceId { get; init; }

    /// <summary>Layer identifier when the source is a Honua layer.</summary>
    public int? LayerId { get; init; }

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the binding is required.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Whether the binding has been resolved.</summary>
    public bool IsResolved { get; init; } = true;

    /// <summary>Optional caller-supplied diagnostic message.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Permission required by a package.
/// </summary>
public sealed class PackagePermissionRequirement
{
    /// <summary>Permission identifier.</summary>
    public string Permission { get; init; } = string.Empty;

    /// <summary>Target resource for the permission.</summary>
    public string? Target { get; init; }

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the permission has been granted.</summary>
    public bool Granted { get; init; } = true;

    /// <summary>Policy reference explaining the permission.</summary>
    public string? PolicyRef { get; init; }

    /// <summary>Action scope affected by the permission.</summary>
    public string AppliesTo { get; init; } = PackageReviewActionScope.Both;
}

/// <summary>
/// Schema required by a package.
/// </summary>
public sealed class PackageSchemaRequirement
{
    /// <summary>Schema identifier.</summary>
    public string? SchemaId { get; init; }

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the schema exists.</summary>
    public bool Exists { get; init; } = true;

    /// <summary>Expected schema version.</summary>
    public string? ExpectedVersion { get; init; }

    /// <summary>Actual schema version.</summary>
    public string? ActualVersion { get; init; }
}

/// <summary>
/// Field required by a package.
/// </summary>
public sealed class PackageFieldRequirement
{
    /// <summary>Field name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the field exists.</summary>
    public bool Exists { get; init; } = true;

    /// <summary>Expected field type.</summary>
    public string? ExpectedType { get; init; }

    /// <summary>Actual field type.</summary>
    public string? ActualType { get; init; }
}

/// <summary>
/// Domain required by a package.
/// </summary>
public sealed class PackageDomainRequirement
{
    /// <summary>Domain name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Field that uses the domain.</summary>
    public string? Field { get; init; }

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the domain exists.</summary>
    public bool Exists { get; init; } = true;

    /// <summary>Whether the domain is compatible.</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>Expected domain value or version.</summary>
    public string? Expected { get; init; }

    /// <summary>Actual domain value or version.</summary>
    public string? Actual { get; init; }
}

/// <summary>
/// Coordinate reference system required by a package.
/// </summary>
public sealed class PackageCrsRequirement
{
    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Expected SRID.</summary>
    public int? ExpectedSrid { get; init; }

    /// <summary>Actual SRID.</summary>
    public int? ActualSrid { get; init; }

    /// <summary>Expected coordinate units.</summary>
    public string? ExpectedUnits { get; init; }

    /// <summary>Actual coordinate units.</summary>
    public string? ActualUnits { get; init; }

    /// <summary>Datum or CRS assumption that must be made explicit.</summary>
    public string? Assumption { get; init; }
}

/// <summary>
/// Geometry type required by a package.
/// </summary>
public sealed class PackageGeometryRequirement
{
    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Expected geometry type.</summary>
    public string? ExpectedGeometryType { get; init; }

    /// <summary>Actual geometry type.</summary>
    public string? ActualGeometryType { get; init; }

    /// <summary>Whether null geometry is allowed.</summary>
    public bool AllowsNullGeometry { get; init; } = true;

    /// <summary>Count of null geometries when known.</summary>
    public long? NullGeometryCount { get; init; }
}

/// <summary>
/// Temporal metadata required by a package.
/// </summary>
public sealed class PackageTemporalRequirement
{
    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether temporal metadata is required.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Temporal field name.</summary>
    public string? Field { get; init; }

    /// <summary>Whether the temporal field or metadata exists.</summary>
    public bool Exists { get; init; } = true;
}

/// <summary>
/// Capability required by a package.
/// </summary>
public sealed class PackageCapabilityRequirement
{
    /// <summary>Capability identifier.</summary>
    public string Capability { get; init; } = string.Empty;

    /// <summary>Capability scope or service identifier.</summary>
    public string? Scope { get; init; }

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the capability is required.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Whether the capability is supported.</summary>
    public bool Supported { get; init; } = true;

    /// <summary>Action scope affected by the capability.</summary>
    public string AppliesTo { get; init; } = PackageReviewActionScope.Both;
}

/// <summary>
/// Package dependency requirement.
/// </summary>
public sealed class PackageDependencyRequirement
{
    /// <summary>Dependency package identifier.</summary>
    public string PackageId { get; init; } = string.Empty;

    /// <summary>Expected dependency version.</summary>
    public string? Version { get; init; }

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the dependency is required.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Whether the dependency resolved.</summary>
    public bool Resolved { get; init; } = true;
}

/// <summary>
/// Package format requirement.
/// </summary>
public sealed class PackageFormatRequirement
{
    /// <summary>Format identifier.</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the format is supported.</summary>
    public bool Supported { get; init; } = true;
}

/// <summary>
/// Approval or admission requirement.
/// </summary>
public sealed class PackageApprovalRequirement
{
    /// <summary>Approval policy reference.</summary>
    public string? PolicyRef { get; init; }

    /// <summary>Path in the package payload.</summary>
    public string? Path { get; init; }

    /// <summary>Whether approval is required.</summary>
    public bool Required { get; init; }

    /// <summary>Whether approval has already been granted.</summary>
    public bool Approved { get; init; }

    /// <summary>Action scope affected by the approval.</summary>
    public string AppliesTo { get; init; } = PackageReviewActionScope.Execute;
}

/// <summary>
/// Response returned by all package-review protocol adapters.
/// </summary>
public sealed class PackageReviewResponse
{
    /// <summary>Package-review contract version.</summary>
    public string ContractVersion { get; init; } = PackageReviewContract.Version;

    /// <summary>Deterministic review identifier.</summary>
    public string ReviewId { get; init; } = string.Empty;

    /// <summary>Package family reviewed.</summary>
    public string PackageFamily { get; init; } = string.Empty;

    /// <summary>Package identifier reviewed.</summary>
    public string? PackageId { get; init; }

    /// <summary>Review status.</summary>
    public string Status { get; init; } = PackageReviewStatus.Blocked;

    /// <summary>Whether the package can be executed without unresolved blockers.</summary>
    public bool CanExecute { get; init; }

    /// <summary>Whether the package can be published without unresolved blockers.</summary>
    public bool CanPublish { get; init; }

    /// <summary>Whether explicit approval is required before continuation.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Time when the review completed.</summary>
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>Ordered findings emitted by the review.</summary>
    public IReadOnlyList<PackageFinding> Findings { get; init; } = [];

    /// <summary>Read-only preview plan, when requested and available.</summary>
    public PackagePreviewPlan? PreviewPlan { get; init; }

    /// <summary>Cost or runtime estimate, when available.</summary>
    public PackageEstimate? Estimate { get; init; }

    /// <summary>Safe related links.</summary>
    public IReadOnlyDictionary<string, string> Links { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Safe related resource references.</summary>
    public IReadOnlyDictionary<string, string> ResourceRefs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A package-review finding.
/// </summary>
public sealed class PackageFinding
{
    /// <summary>Stable machine-readable code.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Finding severity.</summary>
    public string Severity { get; init; } = PackageFindingSeverity.Info;

    /// <summary>Finding category.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Finding disposition.</summary>
    public string Disposition { get; init; } = PackageFindingDisposition.Unresolved;

    /// <summary>Action scope affected by the finding.</summary>
    public string AppliesTo { get; init; } = PackageReviewActionScope.Both;

    /// <summary>Human-readable finding message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Machine-readable required action.</summary>
    public PackageRequiredAction? RequiredAction { get; init; }

    /// <summary>Affected package artifact.</summary>
    public PackageAffectedArtifact? AffectedArtifact { get; init; }

    /// <summary>Evidence supporting the finding.</summary>
    public IReadOnlyList<PackageFindingEvidence> Evidence { get; init; } = [];
}

/// <summary>
/// Required action for a package finding.
/// </summary>
public sealed class PackageRequiredAction
{
    /// <summary>Action kind.</summary>
    public string ActionKind { get; init; } = string.Empty;

    /// <summary>Target path or resource.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Action label or title.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Package artifact affected by a finding.
/// </summary>
public sealed class PackageAffectedArtifact
{
    /// <summary>Artifact kind.</summary>
    public string? Kind { get; init; }

    /// <summary>Artifact identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Package payload path.</summary>
    public string? Path { get; init; }

    /// <summary>Source identifier.</summary>
    public string? SourceId { get; init; }

    /// <summary>Plan or workflow step identifier.</summary>
    public string? StepId { get; init; }
}

/// <summary>
/// Evidence supporting a package finding.
/// </summary>
public sealed class PackageFindingEvidence
{
    /// <summary>Evidence kind.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Expected value.</summary>
    public string? Expected { get; init; }

    /// <summary>Actual value.</summary>
    public string? Actual { get; init; }

    /// <summary>Schema or payload path.</summary>
    public string? Path { get; init; }

    /// <summary>Field name.</summary>
    public string? FieldName { get; init; }

    /// <summary>SRID value.</summary>
    public int? Srid { get; init; }

    /// <summary>Capability identifier.</summary>
    public string? Capability { get; init; }

    /// <summary>Policy reference.</summary>
    public string? PolicyRef { get; init; }

    /// <summary>Resource reference.</summary>
    public string? ResourceRef { get; init; }
}

/// <summary>
/// Read-only preview plan for a package.
/// </summary>
public sealed class PackagePreviewPlan
{
    /// <summary>Preview plan identifier.</summary>
    public string PlanId { get; init; } = string.Empty;

    /// <summary>Whether any preview operation may mutate published state. Must remain false.</summary>
    public bool MayMutatePublishedState { get; init; }

    /// <summary>Ordered preview operations.</summary>
    public IReadOnlyList<PackagePreviewOperation> Operations { get; init; } = [];
}

/// <summary>
/// A single read-only preview operation.
/// </summary>
public sealed class PackagePreviewOperation
{
    /// <summary>Operation identifier.</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>Operation kind.</summary>
    public string OperationKind { get; init; } = string.Empty;

    /// <summary>Input resource references.</summary>
    public IReadOnlyList<string> InputRefs { get; init; } = [];

    /// <summary>Output artifact kinds.</summary>
    public IReadOnlyList<string> OutputArtifactKinds { get; init; } = [];

    /// <summary>Operation dependencies.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Whether this operation may mutate published state. Must remain false for preview.</summary>
    public bool MayMutatePublishedState { get; init; }

    /// <summary>Capabilities required by this operation.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
}

/// <summary>
/// Estimate for package review or preview.
/// </summary>
public sealed class PackageEstimate
{
    /// <summary>Estimated row count.</summary>
    public long? RowCount { get; init; }

    /// <summary>Estimated feature count.</summary>
    public long? FeatureCount { get; init; }

    /// <summary>Estimated byte count.</summary>
    public long? Bytes { get; init; }

    /// <summary>Estimated duration in milliseconds.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Estimated duration in seconds.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Relative cost weight.</summary>
    public double? CostWeight { get; init; }

    /// <summary>Estimate confidence.</summary>
    public string? Confidence { get; init; }
}

/// <summary>
/// Supplemental review result returned by a package-family adapter.
/// </summary>
public sealed class PackageFamilyReviewResult
{
    /// <summary>Findings from the adapter.</summary>
    public IReadOnlyList<PackageFinding> Findings { get; init; } = [];

    /// <summary>Preview plan from the adapter.</summary>
    public PackagePreviewPlan? PreviewPlan { get; init; }

    /// <summary>Estimate from the adapter.</summary>
    public PackageEstimate? Estimate { get; init; }

    /// <summary>Safe links from the adapter.</summary>
    public IReadOnlyDictionary<string, string> Links { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Safe resource references from the adapter.</summary>
    public IReadOnlyDictionary<string, string> ResourceRefs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Caller context used when computing deterministic review identifiers and authorization-sensitive findings.
/// </summary>
public sealed class PackageReviewContext
{
    /// <summary>Tenant identifier visible to the caller.</summary>
    public string? TenantId { get; init; }

    /// <summary>Actor identifier visible to the caller.</summary>
    public string? ActorId { get; init; }

    /// <summary>Caller-visible authorization scope identifiers.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
}
