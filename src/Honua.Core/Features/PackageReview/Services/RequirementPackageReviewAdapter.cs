// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;

namespace Honua.Core.Features.PackageReview.Services;

internal sealed class RequirementPackageReviewAdapter : IPackageFamilyReviewAdapter
{
    public bool CanReview(PackageReviewRequest request) => true;

    public ValueTask<PackageFamilyReviewResult> ReviewAsync(
        PackageReviewRequest request,
        PackageReviewContext context,
        CancellationToken cancellationToken)
    {
        var findings = new List<PackageFinding>();
        AddDataBindingFindings(request, findings);
        AddPermissionFindings(request, findings);
        AddSchemaFindings(request, findings);
        AddFieldFindings(request, findings);
        AddDomainFindings(request, findings);
        AddCrsFindings(request, findings);
        AddGeometryFindings(request, findings);
        AddTemporalFindings(request, findings);
        AddCapabilityFindings(request, findings);
        AddDependencyFindings(request, findings);
        AddFormatFindings(request, findings);
        AddApprovalFindings(request, findings);

        var result = new PackageFamilyReviewResult
        {
            Findings = findings,
            PreviewPlan = request.IncludePreviewPlan && CanBuildGenericPreview(request) ? BuildPreviewPlan(request) : null,
            Estimate = request.Estimate,
            ResourceRefs = request.ResourceRefs
        };

        return ValueTask.FromResult(result);
    }

    private static bool CanBuildGenericPreview(PackageReviewRequest request)
        => !string.Equals(request.PackageFamily, PackageReviewFamilies.AnalysisPlan, StringComparison.Ordinal) &&
           !string.Equals(request.PackageFamily, PackageReviewFamilies.Geoprocessing, StringComparison.Ordinal);

    private static void AddDataBindingFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var binding in request.Requirements.DataBindings)
        {
            if (binding.Required && !binding.IsResolved)
            {
                findings.Add(Blocker(
                    "missing_data_binding",
                    PackageFindingCategory.DataBinding,
                    binding.Message ?? $"Data binding '{binding.Id ?? binding.SourceId ?? binding.Path ?? "<unknown>"}' is unresolved.",
                    "resolve_data_binding",
                    binding.Path,
                    new PackageAffectedArtifact
                    {
                        Kind = "data_binding",
                        Id = binding.Id,
                        Path = binding.Path,
                        SourceId = binding.SourceId
                    },
                    new PackageFindingEvidence
                    {
                        Kind = "data_binding",
                        Expected = "resolved",
                        Actual = "unresolved",
                        Path = binding.Path,
                        ResourceRef = binding.SourceId
                    }));
            }
            else if (request.IncludePassFindings)
            {
                findings.Add(Pass("data_binding_resolved", PackageFindingCategory.DataBinding,
                    $"Data binding '{binding.Id ?? binding.SourceId ?? binding.Path ?? "<unknown>"}' is resolved.", binding.Path));
            }
        }
    }

    private static void AddPermissionFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var permission in request.Requirements.Permissions)
        {
            if (!permission.Granted)
            {
                findings.Add(new PackageFinding
                {
                    Code = "permission_denied",
                    Severity = PackageFindingSeverity.Blocker,
                    Category = PackageFindingCategory.Permission,
                    Disposition = PackageFindingDisposition.AuthDenied,
                    AppliesTo = NormalizeScope(permission.AppliesTo),
                    Message = $"Permission '{permission.Permission}' is required but was not granted.",
                    RequiredAction = new PackageRequiredAction
                    {
                        ActionKind = "grant_permission",
                        TargetPath = permission.Path,
                        Title = "Grant the required permission or choose a different package target."
                    },
                    AffectedArtifact = new PackageAffectedArtifact
                    {
                        Kind = "permission",
                        Id = permission.Permission,
                        Path = permission.Path,
                        SourceId = permission.Target
                    },
                    Evidence =
                    [
                        new PackageFindingEvidence
                        {
                            Kind = "policy",
                            Expected = "granted",
                            Actual = "denied",
                            Path = permission.Path,
                            PolicyRef = permission.PolicyRef,
                            ResourceRef = permission.Target
                        }
                    ]
                });
            }
        }
    }

    private static void AddSchemaFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var schema in request.Requirements.Schemas)
        {
            if (!schema.Exists)
            {
                findings.Add(Blocker(
                    "missing_schema",
                    PackageFindingCategory.Schema,
                    $"Schema '{schema.SchemaId ?? schema.Path ?? "<unknown>"}' is required but missing.",
                    "provide_schema",
                    schema.Path,
                    new PackageAffectedArtifact { Kind = "schema", Id = schema.SchemaId, Path = schema.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "schema",
                        Expected = schema.ExpectedVersion ?? "present",
                        Actual = "missing",
                        Path = schema.Path
                    }));
            }
            else if (!string.IsNullOrWhiteSpace(schema.ExpectedVersion) &&
                     !string.Equals(schema.ExpectedVersion, schema.ActualVersion, StringComparison.Ordinal))
            {
                findings.Add(Blocker(
                    "schema_version_mismatch",
                    PackageFindingCategory.Schema,
                    $"Schema '{schema.SchemaId ?? schema.Path ?? "<unknown>"}' has an incompatible version.",
                    "update_schema_version",
                    schema.Path,
                    new PackageAffectedArtifact { Kind = "schema", Id = schema.SchemaId, Path = schema.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "schema_version",
                        Expected = schema.ExpectedVersion,
                        Actual = schema.ActualVersion,
                        Path = schema.Path
                    }));
            }
        }
    }

    private static void AddFieldFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var field in request.Requirements.Fields)
        {
            if (!field.Exists)
            {
                findings.Add(Blocker(
                    "missing_field",
                    PackageFindingCategory.Field,
                    $"Field '{field.Name}' is required but missing.",
                    "add_field_or_update_binding",
                    field.Path,
                    new PackageAffectedArtifact { Kind = "field", Id = field.Name, Path = field.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "field",
                        Expected = field.ExpectedType ?? "present",
                        Actual = "missing",
                        Path = field.Path,
                        FieldName = field.Name
                    }));
            }
            else if (!string.IsNullOrWhiteSpace(field.ExpectedType) &&
                     !string.Equals(field.ExpectedType, field.ActualType, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Blocker(
                    "field_type_mismatch",
                    PackageFindingCategory.Field,
                    $"Field '{field.Name}' has an incompatible type.",
                    "update_field_type_or_mapping",
                    field.Path,
                    new PackageAffectedArtifact { Kind = "field", Id = field.Name, Path = field.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "field_type",
                        Expected = field.ExpectedType,
                        Actual = field.ActualType,
                        Path = field.Path,
                        FieldName = field.Name
                    }));
            }
        }
    }

    private static void AddDomainFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var domain in request.Requirements.Domains)
        {
            if (!domain.Exists || !domain.IsValid)
            {
                findings.Add(Blocker(
                    domain.Exists ? "domain_mismatch" : "missing_domain",
                    PackageFindingCategory.Domain,
                    $"Domain '{domain.Name}' is not compatible with the package requirement.",
                    "update_domain_or_field_mapping",
                    domain.Path,
                    new PackageAffectedArtifact { Kind = "domain", Id = domain.Name, Path = domain.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "domain",
                        Expected = domain.Expected ?? "compatible",
                        Actual = domain.Exists ? domain.Actual ?? "incompatible" : "missing",
                        Path = domain.Path,
                        FieldName = domain.Field
                    }));
            }
        }
    }

    private static void AddCrsFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var crs in request.Requirements.Crs)
        {
            if (crs.ExpectedSrid.HasValue && crs.ActualSrid.HasValue && crs.ExpectedSrid != crs.ActualSrid)
            {
                findings.Add(Blocker(
                    "crs_srid_mismatch",
                    PackageFindingCategory.Crs,
                    $"Expected SRID {crs.ExpectedSrid.Value} but found {crs.ActualSrid.Value}.",
                    "reproject_or_update_crs",
                    crs.Path,
                    new PackageAffectedArtifact { Kind = "crs", Path = crs.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "srid",
                        Expected = crs.ExpectedSrid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Actual = crs.ActualSrid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Path = crs.Path,
                        Srid = crs.ActualSrid
                    }));
            }

            if (!string.IsNullOrWhiteSpace(crs.ExpectedUnits) &&
                !string.Equals(crs.ExpectedUnits, crs.ActualUnits, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Blocker(
                    "crs_units_mismatch",
                    PackageFindingCategory.Crs,
                    $"Expected CRS units '{crs.ExpectedUnits}' but found '{crs.ActualUnits ?? "<unknown>"}'.",
                    "update_units_or_crs",
                    crs.Path,
                    new PackageAffectedArtifact { Kind = "crs", Path = crs.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "crs_units",
                        Expected = crs.ExpectedUnits,
                        Actual = crs.ActualUnits,
                        Path = crs.Path
                    }));
            }

            if (!string.IsNullOrWhiteSpace(crs.Assumption))
            {
                findings.Add(new PackageFinding
                {
                    Code = "crs_assumption_requires_review",
                    Severity = PackageFindingSeverity.Warning,
                    Category = PackageFindingCategory.Crs,
                    Disposition = PackageFindingDisposition.Unresolved,
                    AppliesTo = PackageReviewActionScope.Review,
                    Message = "CRS or datum assumptions require review before execution or publish.",
                    RequiredAction = new PackageRequiredAction
                    {
                        ActionKind = "review_crs_assumption",
                        TargetPath = crs.Path,
                        Title = "Review CRS or datum assumption."
                    },
                    AffectedArtifact = new PackageAffectedArtifact { Kind = "crs", Path = crs.Path },
                    Evidence =
                    [
                        new PackageFindingEvidence
                        {
                            Kind = "crs_assumption",
                            Actual = crs.Assumption,
                            Path = crs.Path,
                            Srid = crs.ActualSrid
                        }
                    ]
                });
            }
        }
    }

    private static void AddGeometryFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var geometry in request.Requirements.Geometry)
        {
            if (!string.IsNullOrWhiteSpace(geometry.ExpectedGeometryType) &&
                !string.Equals(geometry.ExpectedGeometryType, geometry.ActualGeometryType, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Blocker(
                    "geometry_type_mismatch",
                    PackageFindingCategory.Geometry,
                    $"Expected geometry type '{geometry.ExpectedGeometryType}' but found '{geometry.ActualGeometryType ?? "<unknown>"}'.",
                    "update_geometry_type_or_source",
                    geometry.Path,
                    new PackageAffectedArtifact { Kind = "geometry", Path = geometry.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "geometry_type",
                        Expected = geometry.ExpectedGeometryType,
                        Actual = geometry.ActualGeometryType,
                        Path = geometry.Path
                    }));
            }

            if (!geometry.AllowsNullGeometry && geometry.NullGeometryCount.GetValueOrDefault() > 0)
            {
                findings.Add(Blocker(
                    "null_geometry_not_allowed",
                    PackageFindingCategory.Geometry,
                    "Package requires non-null geometry but the source contains null geometries.",
                    "filter_or_repair_null_geometry",
                    geometry.Path,
                    new PackageAffectedArtifact { Kind = "geometry", Path = geometry.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "null_geometry_count",
                        Expected = "0",
                        Actual = geometry.NullGeometryCount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Path = geometry.Path
                    }));
            }
        }
    }

    private static void AddTemporalFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var temporal in request.Requirements.Temporal)
        {
            if (temporal.Required && !temporal.Exists)
            {
                findings.Add(Blocker(
                    "missing_temporal_field",
                    PackageFindingCategory.Temporal,
                    $"Temporal field '{temporal.Field ?? temporal.Path ?? "<unknown>"}' is required but missing.",
                    "add_temporal_field_or_disable_time_requirement",
                    temporal.Path,
                    new PackageAffectedArtifact { Kind = "temporal", Id = temporal.Field, Path = temporal.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "temporal_field",
                        Expected = temporal.Field ?? "present",
                        Actual = "missing",
                        Path = temporal.Path,
                        FieldName = temporal.Field
                    }));
            }
        }
    }

    private static void AddCapabilityFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var capability in request.Requirements.Capabilities)
        {
            if (capability.Required && !capability.Supported)
            {
                findings.Add(new PackageFinding
                {
                    Code = "unsupported_capability",
                    Severity = PackageFindingSeverity.Blocker,
                    Category = PackageFindingCategory.Capability,
                    Disposition = PackageFindingDisposition.Unsupported,
                    AppliesTo = NormalizeScope(capability.AppliesTo),
                    Message = $"Capability '{capability.Capability}' is required but unsupported.",
                    RequiredAction = new PackageRequiredAction
                    {
                        ActionKind = "choose_supported_capability",
                        TargetPath = capability.Path,
                        Title = "Remove or replace the unsupported capability."
                    },
                    AffectedArtifact = new PackageAffectedArtifact
                    {
                        Kind = "capability",
                        Id = capability.Capability,
                        Path = capability.Path,
                        SourceId = capability.Scope
                    },
                    Evidence =
                    [
                        new PackageFindingEvidence
                        {
                            Kind = "capability",
                            Expected = "supported",
                            Actual = "unsupported",
                            Path = capability.Path,
                            Capability = capability.Capability,
                            ResourceRef = capability.Scope
                        }
                    ]
                });
            }
        }
    }

    private static void AddDependencyFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var dependency in request.Requirements.Dependencies)
        {
            if (dependency.Required && !dependency.Resolved)
            {
                findings.Add(Blocker(
                    "missing_dependency",
                    PackageFindingCategory.Dependency,
                    $"Dependency package '{dependency.PackageId}' is required but unresolved.",
                    "resolve_dependency",
                    dependency.Path,
                    new PackageAffectedArtifact { Kind = "dependency", Id = dependency.PackageId, Path = dependency.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "dependency",
                        Expected = dependency.Version ?? "resolved",
                        Actual = "unresolved",
                        Path = dependency.Path,
                        ResourceRef = dependency.PackageId
                    }));
            }
        }
    }

    private static void AddFormatFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var format in request.Requirements.Formats)
        {
            if (!format.Supported)
            {
                findings.Add(Blocker(
                    "unsupported_package_format",
                    PackageFindingCategory.Format,
                    $"Package format '{format.Format}' is unsupported.",
                    "choose_supported_format",
                    format.Path,
                    new PackageAffectedArtifact { Kind = "format", Id = format.Format, Path = format.Path },
                    new PackageFindingEvidence
                    {
                        Kind = "format",
                        Expected = "supported",
                        Actual = "unsupported",
                        Path = format.Path
                    }));
            }
        }
    }

    private static void AddApprovalFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        foreach (var approval in request.Requirements.Approvals)
        {
            if (approval.Required && !approval.Approved)
            {
                findings.Add(new PackageFinding
                {
                    Code = "approval_required",
                    Severity = PackageFindingSeverity.Blocker,
                    Category = PackageFindingCategory.Approval,
                    Disposition = PackageFindingDisposition.RequiresApproval,
                    AppliesTo = NormalizeScope(approval.AppliesTo),
                    Message = "Package requires approval before the requested action can continue.",
                    RequiredAction = new PackageRequiredAction
                    {
                        ActionKind = "obtain_approval",
                        TargetPath = approval.Path,
                        Title = "Obtain the required approval."
                    },
                    AffectedArtifact = new PackageAffectedArtifact { Kind = "approval", Path = approval.Path },
                    Evidence =
                    [
                        new PackageFindingEvidence
                        {
                            Kind = "approval",
                            Expected = "approved",
                            Actual = "not_approved",
                            Path = approval.Path,
                            PolicyRef = approval.PolicyRef
                        }
                    ]
                });
            }
        }
    }

    private static PackagePreviewPlan BuildPreviewPlan(PackageReviewRequest request)
    {
        var inputRefs = request.Requirements.DataBindings
            .Select(static binding => binding.SourceId ?? binding.Id ?? binding.Path)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var capabilities = request.Requirements.Capabilities
            .Select(static capability => capability.Capability)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new PackagePreviewPlan
        {
            PlanId = string.IsNullOrWhiteSpace(request.PackageId)
                ? $"preview:{request.PackageFamily}"
                : $"preview:{request.PackageFamily}:{request.PackageId}",
            MayMutatePublishedState = false,
            Operations =
            [
                new PackagePreviewOperation
                {
                    OperationId = "review",
                    OperationKind = request.PackageFamily,
                    InputRefs = inputRefs,
                    OutputArtifactKinds = [request.PackageFamily],
                    MayMutatePublishedState = false,
                    RequiredCapabilities = capabilities
                }
            ]
        };
    }

    private static PackageFinding Blocker(
        string code,
        string category,
        string message,
        string actionKind,
        string? targetPath,
        PackageAffectedArtifact affected,
        PackageFindingEvidence evidence)
        => new()
        {
            Code = code,
            Severity = PackageFindingSeverity.Blocker,
            Category = category,
            Disposition = PackageFindingDisposition.Unresolved,
            Message = message,
            RequiredAction = new PackageRequiredAction
            {
                ActionKind = actionKind,
                TargetPath = targetPath,
                Title = "Resolve this blocker before continuing."
            },
            AffectedArtifact = affected,
            Evidence = [evidence]
        };

    private static PackageFinding Pass(string code, string category, string message, string? path)
        => new()
        {
            Code = code,
            Severity = PackageFindingSeverity.Pass,
            Category = category,
            Disposition = PackageFindingDisposition.Resolved,
            AppliesTo = PackageReviewActionScope.Review,
            Message = message,
            AffectedArtifact = new PackageAffectedArtifact { Path = path }
        };

    private static string NormalizeScope(string? value)
        => value switch
        {
            PackageReviewActionScope.Execute => PackageReviewActionScope.Execute,
            PackageReviewActionScope.Publish => PackageReviewActionScope.Publish,
            PackageReviewActionScope.Review => PackageReviewActionScope.Review,
            _ => PackageReviewActionScope.Both
        };
}
