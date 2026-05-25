// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;

namespace Honua.Core.Features.PackageReview.Services;

internal sealed class PackageReviewService : IPackageReviewService
{
    private readonly IReadOnlyList<IPackageFamilyReviewAdapter> _adapters;
    private readonly TimeProvider _timeProvider;

    public PackageReviewService(
        IEnumerable<IPackageFamilyReviewAdapter> adapters,
        TimeProvider timeProvider)
    {
        _adapters = adapters.ToArray();
        _timeProvider = timeProvider;
    }

    public async Task<PackageReviewResponse> ReviewAsync(
        PackageReviewRequest request,
        PackageReviewContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        context ??= new PackageReviewContext();
        using var activity = PackageReviewTelemetry.ActivitySource.StartActivity("PackageReview.Review");
        activity?.SetTag("honua.package_review.family", request.PackageFamily);
        activity?.SetTag("honua.package_review.preview", request.IncludePreviewPlan);

        var findings = new List<PackageFinding>();
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var resourceRefs = new Dictionary<string, string>(request.ResourceRefs, StringComparer.Ordinal);
        PackagePreviewPlan? previewPlan = null;
        PackageEstimate? estimate = request.Estimate;

        AddRequestShapeFindings(request, findings);

        if (PackageReviewFamilies.IsKnown(request.PackageFamily))
        {
            foreach (var adapter in _adapters)
            {
                if (!adapter.CanReview(request))
                {
                    continue;
                }

                var result = await adapter.ReviewAsync(request, context, cancellationToken).ConfigureAwait(false);
                findings.AddRange(result.Findings);
                previewPlan = result.PreviewPlan ?? previewPlan;
                estimate = result.Estimate ?? estimate;
                Merge(links, result.Links);
                Merge(resourceRefs, result.ResourceRefs);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.PackageFamily))
        {
            findings.Add(new PackageFinding
            {
                Code = "unsupported_package_family",
                Severity = PackageFindingSeverity.Blocker,
                Category = PackageFindingCategory.Format,
                Disposition = PackageFindingDisposition.Unsupported,
                Message = $"Package family '{request.PackageFamily}' is not supported by the package-review contract.",
                RequiredAction = new PackageRequiredAction
                {
                    ActionKind = "choose_supported_package_family",
                    TargetPath = "packageFamily",
                    Title = "Use a supported package family."
                },
                Evidence =
                [
                    new PackageFindingEvidence
                    {
                        Kind = "supported_families",
                        Expected = string.Join(",", SupportedFamilies())
                    }
                ]
            });
        }

        if (request.Estimate != null)
        {
            AddEstimateWarnings(request.Estimate, findings);
        }

        if (!request.IncludePassFindings)
        {
            findings.RemoveAll(static f => string.Equals(f.Severity, PackageFindingSeverity.Pass, StringComparison.Ordinal));
        }

        var orderedFindings = findings
            .OrderBy(SeverityRank)
            .ThenBy(static f => f.Category, StringComparer.Ordinal)
            .ThenBy(static f => f.Code, StringComparer.Ordinal)
            .ToArray();

        var status = ComputeStatus(orderedFindings);
        var canExecute = !orderedFindings.Any(IsExecuteBlocker);
        var canPublish = !orderedFindings.Any(IsPublishBlocker);
        var requiresApproval = orderedFindings.Any(static f =>
            string.Equals(f.Category, PackageFindingCategory.Approval, StringComparison.Ordinal) &&
            !string.Equals(f.Disposition, PackageFindingDisposition.Resolved, StringComparison.Ordinal));

        activity?.SetTag("honua.package_review.status", status);
        activity?.SetTag("honua.package_review.finding_count", orderedFindings.Length);
        activity?.SetTag("honua.package_review.blocker_count", orderedFindings.Count(IsUnresolvedBlocker));

        return new PackageReviewResponse
        {
            ContractVersion = PackageReviewContract.Version,
            ReviewId = CreateReviewId(request, context),
            PackageFamily = request.PackageFamily,
            PackageId = request.PackageId,
            Status = status,
            CanExecute = canExecute,
            CanPublish = canPublish,
            RequiresApproval = requiresApproval,
            CheckedAt = _timeProvider.GetUtcNow(),
            Findings = orderedFindings,
            PreviewPlan = previewPlan,
            Estimate = estimate,
            Links = links,
            ResourceRefs = resourceRefs
        };
    }

    private static void AddRequestShapeFindings(PackageReviewRequest request, List<PackageFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(request.PackageFamily))
        {
            findings.Add(new PackageFinding
            {
                Code = "missing_package_family",
                Severity = PackageFindingSeverity.Blocker,
                Category = PackageFindingCategory.Format,
                Message = "Package family is required.",
                RequiredAction = new PackageRequiredAction
                {
                    ActionKind = "provide_value",
                    TargetPath = "packageFamily",
                    Title = "Provide a package family."
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(request.ContractVersion) &&
            !string.Equals(request.ContractVersion, PackageReviewContract.Version, StringComparison.Ordinal))
        {
            findings.Add(new PackageFinding
            {
                Code = "unsupported_contract_version",
                Severity = PackageFindingSeverity.Blocker,
                Category = PackageFindingCategory.Format,
                Disposition = PackageFindingDisposition.Unsupported,
                Message = $"Contract version '{request.ContractVersion}' is not supported.",
                RequiredAction = new PackageRequiredAction
                {
                    ActionKind = "use_contract_version",
                    TargetPath = "contractVersion",
                    Title = $"Use {PackageReviewContract.Version}."
                },
                Evidence =
                [
                    new PackageFindingEvidence
                    {
                        Kind = "contract_version",
                        Expected = PackageReviewContract.Version,
                        Actual = request.ContractVersion
                    }
                ]
            });
        }
    }

    private static void AddEstimateWarnings(PackageEstimate estimate, List<PackageFinding> findings)
    {
        var expensive = estimate.CostWeight >= 1000 ||
                        estimate.RowCount >= 1_000_000 ||
                        estimate.FeatureCount >= 1_000_000 ||
                        estimate.DurationMs >= 30_000 ||
                        estimate.DurationSeconds >= 30;

        if (!expensive)
        {
            return;
        }

        findings.Add(new PackageFinding
        {
            Code = "expensive_preview_estimate",
            Severity = PackageFindingSeverity.Warning,
            Category = PackageFindingCategory.Preview,
            Disposition = PackageFindingDisposition.Unresolved,
            AppliesTo = PackageReviewActionScope.Execute,
            Message = "Preview estimate indicates this package may be expensive to run.",
            RequiredAction = new PackageRequiredAction
            {
                ActionKind = "review_estimate",
                TargetPath = "estimate",
                Title = "Review runtime or cost estimate before execution."
            },
            Evidence =
            [
                new PackageFindingEvidence
                {
                    Kind = "estimate",
                    Actual = BuildEstimateSummary(estimate)
                }
            ]
        });
    }

    private static string BuildEstimateSummary(PackageEstimate estimate)
    {
        var parts = new List<string>(6);
        if (estimate.RowCount.HasValue) parts.Add($"rows={estimate.RowCount.Value.ToString(CultureInfo.InvariantCulture)}");
        if (estimate.FeatureCount.HasValue) parts.Add($"features={estimate.FeatureCount.Value.ToString(CultureInfo.InvariantCulture)}");
        if (estimate.Bytes.HasValue) parts.Add($"bytes={estimate.Bytes.Value.ToString(CultureInfo.InvariantCulture)}");
        if (estimate.DurationMs.HasValue) parts.Add($"durationMs={estimate.DurationMs.Value.ToString("R", CultureInfo.InvariantCulture)}");
        if (estimate.DurationSeconds.HasValue) parts.Add($"durationSeconds={estimate.DurationSeconds.Value.ToString("R", CultureInfo.InvariantCulture)}");
        if (estimate.CostWeight.HasValue) parts.Add($"costWeight={estimate.CostWeight.Value.ToString("R", CultureInfo.InvariantCulture)}");
        return string.Join(";", parts);
    }

    private static string ComputeStatus(IReadOnlyList<PackageFinding> findings)
    {
        if (findings.Any(IsUnresolvedBlocker))
        {
            return PackageReviewStatus.Blocked;
        }

        if (findings.Any(static f => string.Equals(f.Severity, PackageFindingSeverity.Warning, StringComparison.Ordinal)))
        {
            return PackageReviewStatus.Warning;
        }

        return PackageReviewStatus.Ready;
    }

    private static bool IsExecuteBlocker(PackageFinding finding)
        => IsUnresolvedBlocker(finding) &&
           (string.Equals(finding.AppliesTo, PackageReviewActionScope.Both, StringComparison.Ordinal) ||
            string.Equals(finding.AppliesTo, PackageReviewActionScope.Execute, StringComparison.Ordinal));

    private static bool IsPublishBlocker(PackageFinding finding)
        => IsUnresolvedBlocker(finding) &&
           (string.Equals(finding.AppliesTo, PackageReviewActionScope.Both, StringComparison.Ordinal) ||
            string.Equals(finding.AppliesTo, PackageReviewActionScope.Publish, StringComparison.Ordinal));

    private static bool IsUnresolvedBlocker(PackageFinding finding)
        => string.Equals(finding.Severity, PackageFindingSeverity.Blocker, StringComparison.Ordinal) &&
           !string.Equals(finding.Disposition, PackageFindingDisposition.Resolved, StringComparison.Ordinal);

    private static int SeverityRank(PackageFinding finding) => finding.Severity switch
    {
        PackageFindingSeverity.Blocker => 0,
        PackageFindingSeverity.Warning => 1,
        PackageFindingSeverity.Info => 2,
        PackageFindingSeverity.Pass => 3,
        _ => 4
    };

    private static void Merge(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var pair in source)
        {
            target[pair.Key] = pair.Value;
        }
    }

    private static IEnumerable<string> SupportedFamilies()
    {
        yield return PackageReviewFamilies.Query;
        yield return PackageReviewFamilies.AnalysisPlan;
        yield return PackageReviewFamilies.MapPackage;
        yield return PackageReviewFamilies.DashboardReport;
        yield return PackageReviewFamilies.Form;
        yield return PackageReviewFamilies.AppPackage;
        yield return PackageReviewFamilies.Workflow;
        yield return PackageReviewFamilies.Geoprocessing;
        yield return PackageReviewFamilies.Etl;
    }

    private static string CreateReviewId(PackageReviewRequest request, PackageReviewContext context)
    {
        var builder = new StringBuilder();
        builder.Append(PackageReviewContract.Version).Append('\n');
        builder.Append(request.PackageFamily).Append('\n');
        builder.Append(request.PackageId).Append('\n');
        builder.Append(request.RequestedAction).Append('\n');
        builder.Append(request.Format).Append('\n');
        builder.Append(request.PackagePayload?.GetRawText()).Append('\n');
        builder.Append(JsonSerializer.Serialize(request.Requirements, PackageReviewCoreJsonContext.Default.PackageReviewRequirements)).Append('\n');
        builder.Append(JsonSerializer.Serialize(request.Estimate, PackageReviewCoreJsonContext.Default.PackageEstimate)).Append('\n');
        AppendDictionary(builder, request.ResourceRefs);
        builder.Append(context.TenantId).Append('\n');
        builder.Append(context.ActorId).Append('\n');
        if (!string.IsNullOrWhiteSpace(context.SubjectId))
        {
            builder.Append("subject=").Append(context.SubjectId).Append('\n');
        }

        foreach (var scope in context.Scopes.Order(StringComparer.Ordinal))
        {
            builder.Append(scope).Append('\n');
        }

        foreach (var role in context.Roles.Order(StringComparer.Ordinal))
        {
            builder.Append("role=").Append(role).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "prv_" + Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private static void AppendDictionary(StringBuilder builder, IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        }
    }
}
