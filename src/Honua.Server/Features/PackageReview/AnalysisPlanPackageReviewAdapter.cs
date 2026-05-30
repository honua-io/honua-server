// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Geoprocessing;

namespace Honua.Server.Features.PackageReview;

internal sealed class AnalysisPlanPackageReviewAdapter : IPackageFamilyReviewAdapter
{
    private readonly IGeoprocessingJobService _jobService;

    public AnalysisPlanPackageReviewAdapter(IGeoprocessingJobService jobService)
    {
        _jobService = jobService;
    }

    public bool CanReview(PackageReviewRequest request)
        => string.Equals(request.PackageFamily, PackageReviewFamilies.AnalysisPlan, StringComparison.Ordinal) ||
           string.Equals(request.PackageFamily, PackageReviewFamilies.Geoprocessing, StringComparison.Ordinal);

    public ValueTask<PackageFamilyReviewResult> ReviewAsync(
        PackageReviewRequest request,
        PackageReviewContext context,
        CancellationToken cancellationToken)
    {
        var findings = new List<PackageFinding>();
        if (request.PackagePayload is null ||
            request.PackagePayload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            findings.Add(new PackageFinding
            {
                Code = "missing_analysis_plan_payload",
                Severity = PackageFindingSeverity.Blocker,
                Category = PackageFindingCategory.Schema,
                Message = "Analysis plan package payload is required.",
                RequiredAction = new PackageRequiredAction
                {
                    ActionKind = "provide_payload",
                    TargetPath = "packagePayload",
                    Title = "Provide the analysis plan payload."
                },
                AffectedArtifact = new PackageAffectedArtifact { Kind = "analysis_plan", Path = "packagePayload" }
            });

            return ValueTask.FromResult(new PackageFamilyReviewResult { Findings = findings });
        }

        AnalysisPlan plan;
        try
        {
            var payload = request.PackagePayload.Value.Deserialize(
                PackageReviewJsonContext.Default.AnalysisPlanPackagePayload);
            plan = ToDomainPlan(payload);
        }
        catch (JsonException ex)
        {
            findings.Add(InvalidPayloadFinding(ex.Message));
            return ValueTask.FromResult(new PackageFamilyReviewResult { Findings = findings });
        }
        catch (GeoprocessingValidationException ex)
        {
            findings.Add(InvalidPayloadFinding(ex.Message));
            return ValueTask.FromResult(new PackageFamilyReviewResult { Findings = findings });
        }

        var principal = BuildPrincipal(context);
        PlanValidationResult validation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            validation = _jobService.ValidatePlan(plan, principal);
        }
        catch (GeoprocessingValidationException ex)
        {
            findings.Add(InvalidPayloadFinding(ex.Message));
            return ValueTask.FromResult(new PackageFamilyReviewResult { Findings = findings });
        }

        findings.AddRange(validation.Violations.Select(ToFinding));
        findings.AddRange(validation.Warnings.Select(static warning => new PackageFinding
        {
            Code = "analysis_plan_warning",
            Severity = PackageFindingSeverity.Warning,
            Category = PackageFindingCategory.Preview,
            Disposition = PackageFindingDisposition.Unresolved,
            AppliesTo = PackageReviewActionScope.Execute,
            Message = warning,
            RequiredAction = new PackageRequiredAction
            {
                ActionKind = "review_warning",
                TargetPath = "packagePayload.warnings",
                Title = "Review the analysis plan warning."
            }
        }));

        if (validation.RequiresApproval)
        {
            findings.Add(new PackageFinding
            {
                Code = "approval_required",
                Severity = PackageFindingSeverity.Blocker,
                Category = PackageFindingCategory.Approval,
                Disposition = PackageFindingDisposition.RequiresApproval,
                AppliesTo = PackageReviewActionScope.Execute,
                Message = "Analysis plan requires approval before execution.",
                RequiredAction = new PackageRequiredAction
                {
                    ActionKind = "obtain_approval",
                    TargetPath = "packagePayload",
                    Title = "Obtain the required execution approval."
                },
                AffectedArtifact = new PackageAffectedArtifact
                {
                    Kind = "analysis_plan",
                    Id = plan.PlanId,
                    Path = "packagePayload"
                }
            });
        }

        PackagePreviewPlan? previewPlan = null;
        PackageEstimate? estimate = null;
        if (request.IncludePreviewPlan && validation.IsExecutable)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dryRun = _jobService.DryRunPlan(plan, principal);
                previewPlan = ToPreviewPlan(plan);
                estimate = new PackageEstimate
                {
                    DurationSeconds = dryRun.EstimatedDurationSeconds,
                    CostWeight = Math.Max(plan.Steps.Count, 1),
                    Confidence = "medium"
                };
            }
            catch (GeoprocessingValidationException ex)
            {
                findings.Add(InvalidPayloadFinding(ex.Message));
            }
        }

        return ValueTask.FromResult(new PackageFamilyReviewResult
        {
            Findings = findings,
            PreviewPlan = previewPlan,
            Estimate = estimate
        });
    }

    private static AnalysisPlan ToDomainPlan(AnalysisPlanPackagePayload? input)
    {
        if (input is null)
        {
            throw new GeoprocessingValidationException("Analysis plan payload is required.");
        }

        var steps = new List<AnalysisPlanStep>(input.Steps?.Count ?? 0);
        if (input.Steps != null)
        {
            for (var i = 0; i < input.Steps.Count; i++)
            {
                var step = input.Steps[i];
                if (step is null)
                {
                    throw new GeoprocessingValidationException(
                        $"Plan step at index {i} is null; each entry must be a step object.");
                }

                steps.Add(ToDomainStep(step, i));
            }
        }

        var outputs = new List<ArtifactKind>(input.Outputs?.Count ?? 0);
        if (input.Outputs != null)
        {
            for (var i = 0; i < input.Outputs.Count; i++)
            {
                var output = input.Outputs[i];
                if (output is null)
                {
                    throw new GeoprocessingValidationException(
                        $"Plan output at index {i} is null; each entry must be an artifact kind string.");
                }

                if (!Enum.TryParse<ArtifactKind>(output, ignoreCase: true, out var parsed))
                {
                    throw new GeoprocessingValidationException($"Unsupported artifact kind '{output}'.");
                }

                outputs.Add(parsed);
            }
        }

        return new AnalysisPlan
        {
            PlanId = input.PlanId ?? string.Empty,
            IntentId = input.IntentId ?? string.Empty,
            Steps = steps,
            Outputs = outputs,
            Warnings = input.Warnings ?? []
        };
    }

    private static AnalysisPlanStep ToDomainStep(AnalysisPlanStepPackagePayload step, int index)
    {
        if (!Enum.TryParse<AnalysisPlanStepKind>(step.Kind, ignoreCase: true, out var kind))
        {
            throw new GeoprocessingValidationException(
                $"Step '{step.StepId ?? $"at index {index}"}' has unsupported step kind '{step.Kind}'.");
        }

        return new AnalysisPlanStep
        {
            StepId = step.StepId ?? string.Empty,
            Kind = kind,
            ProcessId = step.ProcessId,
            Inputs = step.Inputs ?? new Dictionary<string, string>(StringComparer.Ordinal),
            DependsOn = step.DependsOn ?? []
        };
    }

    private static ClaimsPrincipal BuildPrincipal(PackageReviewContext context)
    {
        var claims = new List<Claim>();
        if (!string.IsNullOrWhiteSpace(context.ActorId))
        {
            claims.Add(new Claim(ClaimTypes.Name, context.ActorId));
        }

        if (!string.IsNullOrWhiteSpace(context.SubjectId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, context.SubjectId));
        }

        if (!string.IsNullOrWhiteSpace(context.TenantId))
        {
            claims.Add(new Claim("tenant_id", context.TenantId));
        }

        foreach (var scope in context.Scopes)
        {
            claims.Add(new Claim("scope", scope));
        }

        foreach (var role in context.Roles.Distinct(StringComparer.Ordinal))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("roles", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "PackageReview"));
    }

    private static PackageFinding InvalidPayloadFinding(string message)
        => new()
        {
            Code = "invalid_analysis_plan_payload",
            Severity = PackageFindingSeverity.Blocker,
            Category = PackageFindingCategory.Schema,
            Message = "Analysis plan payload is invalid.",
            RequiredAction = new PackageRequiredAction
            {
                ActionKind = "fix_payload",
                TargetPath = "packagePayload",
                Title = "Fix the analysis plan payload."
            },
            AffectedArtifact = new PackageAffectedArtifact { Kind = "analysis_plan", Path = "packagePayload" },
            Evidence =
            [
                new PackageFindingEvidence
                {
                    Kind = "validation_error",
                    Actual = message,
                    Path = "packagePayload"
                }
            ]
        };

    private static PackageFinding ToFinding(GeoprocessingValidationFailure violation)
    {
        var category = violation.Code switch
        {
            "UNKNOWN_PROCESS" or "MISSING_PROCESS_ID" => PackageFindingCategory.Capability,
            "MISSING_REQUIRED_PARAMETER" or "UNKNOWN_PARAMETER" or "INVALID_PARAMETER_VALUE" => PackageFindingCategory.Field,
            _ => PackageFindingCategory.Schema
        };

        var action = category switch
        {
            PackageFindingCategory.Capability => "choose_supported_capability",
            PackageFindingCategory.Field => "fix_field_or_parameter",
            _ => "fix_schema"
        };

        return new PackageFinding
        {
            Code = ToSnakeCase(violation.Code),
            Severity = PackageFindingSeverity.Blocker,
            Category = category,
            Disposition = category == PackageFindingCategory.Capability &&
                          string.Equals(violation.Code, "UNKNOWN_PROCESS", StringComparison.Ordinal)
                ? PackageFindingDisposition.Unsupported
                : PackageFindingDisposition.Unresolved,
            AppliesTo = PackageReviewActionScope.Execute,
            Message = violation.Message,
            RequiredAction = new PackageRequiredAction
            {
                ActionKind = action,
                TargetPath = violation.FieldPath,
                Title = "Resolve this analysis plan blocker."
            },
            AffectedArtifact = new PackageAffectedArtifact
            {
                Kind = "analysis_plan",
                Path = violation.FieldPath,
                StepId = ExtractStepId(violation.FieldPath)
            },
            Evidence =
            [
                new PackageFindingEvidence
                {
                    Kind = "geoprocessing_validation",
                    Actual = violation.Code,
                    Path = violation.FieldPath
                }
            ]
        };
    }

    private static PackagePreviewPlan ToPreviewPlan(AnalysisPlan plan)
        => new()
        {
            PlanId = string.IsNullOrWhiteSpace(plan.PlanId) ? "analysis-preview" : plan.PlanId,
            MayMutatePublishedState = false,
            Operations = plan.Steps.Select(static step => new PackagePreviewOperation
            {
                OperationId = step.StepId,
                OperationKind = step.Kind.ToString(),
                InputRefs = step.Inputs.Values
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                OutputArtifactKinds = [],
                DependsOn = step.DependsOn,
                MayMutatePublishedState = false,
                RequiredCapabilities = string.IsNullOrWhiteSpace(step.ProcessId) ? [] : [step.ProcessId]
            }).ToArray()
        };

    private static string? ExtractStepId(string? fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath) || !fieldPath.StartsWith("steps[", StringComparison.Ordinal))
        {
            return null;
        }

        var end = fieldPath.IndexOf(']', "steps[".Length);
        return end > "steps[".Length ? fieldPath["steps[".Length..end] : null;
    }

    private static string ToSnakeCase(string code)
        => code.ToLowerInvariant();
}

internal sealed class AnalysisPlanPackagePayload
{
    public string? PlanId { get; init; }

    public string? IntentId { get; init; }

    public IReadOnlyList<AnalysisPlanStepPackagePayload>? Steps { get; init; }

    public IReadOnlyList<string>? Outputs { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }
}

internal sealed class AnalysisPlanStepPackagePayload
{
    public string? StepId { get; init; }

    public string? Kind { get; init; }

    public string? ProcessId { get; init; }

    public IReadOnlyDictionary<string, string>? Inputs { get; init; }

    public IReadOnlyList<string>? DependsOn { get; init; }
}
