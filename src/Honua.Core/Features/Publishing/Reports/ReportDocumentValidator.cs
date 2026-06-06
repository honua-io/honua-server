// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Reports;

/// <summary>
/// Purely structural validator for a <see cref="ReportDocument"/>. The report has no DB-backed publish
/// validator (the publication registry hashes the payload opaquely), so this is the only validation gate
/// — it requires no live metadata. It checks: a title is present, at least one panel exists, panel ids
/// are non-empty and unique, every panel kind is in the allowed set, and every chart panel carries a
/// non-empty Vega-Lite spec. The result mirrors the form package's
/// <c>FormPackageValidationResult</c> (IsValid + Issues[code, severity, message, path]).
/// </summary>
public static class ReportDocumentValidator
{
    /// <summary>Validates the structural integrity of a report document.</summary>
    public static ReportValidationResult Validate(ReportDocument? document)
    {
        var issues = new List<ReportValidationIssue>();

        if (document is null)
        {
            issues.Add(new ReportValidationIssue
            {
                Code = "documentMissing",
                Severity = "error",
                Message = "A report document is required.",
                Path = "$",
            });
            return new ReportValidationResult { IsValid = false, Issues = issues.ToArray() };
        }

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            issues.Add(new ReportValidationIssue
            {
                Code = "titleRequired",
                Severity = "error",
                Message = "The report must have a non-empty title.",
                Path = "$.title",
            });
        }

        var panels = document.Panels;
        if (panels.Length == 0)
        {
            issues.Add(new ReportValidationIssue
            {
                Code = "panelsRequired",
                Severity = "error",
                Message = "The report must have at least one panel.",
                Path = "$.panels",
            });
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < panels.Length; i++)
        {
            var panel = panels[i];
            var path = $"$.panels[{i}]";

            if (string.IsNullOrWhiteSpace(panel.Id))
            {
                issues.Add(new ReportValidationIssue
                {
                    Code = "panelIdRequired",
                    Severity = "error",
                    Message = $"Panel at index {i} must have a non-empty id.",
                    Path = path + ".id",
                });
            }
            else if (!seenIds.Add(panel.Id))
            {
                issues.Add(new ReportValidationIssue
                {
                    Code = "panelIdDuplicate",
                    Severity = "error",
                    Message = $"Panel id '{panel.Id}' is used by more than one panel.",
                    Path = path + ".id",
                });
            }

            if (!IsAllowedKind(panel.Kind))
            {
                issues.Add(new ReportValidationIssue
                {
                    Code = "panelKindInvalid",
                    Severity = "error",
                    Message = $"Panel '{PanelLabel(panel, i)}' has unsupported kind '{panel.Kind}'. Allowed: {string.Join(", ", ReportPanelKinds.All)}.",
                    Path = path + ".kind",
                });
            }

            if (IsChart(panel.Kind) && !HasNonEmptySpec(panel.ChartSpec))
            {
                issues.Add(new ReportValidationIssue
                {
                    Code = "chartSpecRequired",
                    Severity = "error",
                    Message = $"Chart panel '{PanelLabel(panel, i)}' must carry a non-empty Vega-Lite spec.",
                    Path = path + ".chartSpec",
                });
            }
        }

        var hasError = issues.Exists(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        return new ReportValidationResult { IsValid = !hasError, Issues = issues.ToArray() };
    }

    private static bool IsAllowedKind(string? kind) =>
        Array.Exists(ReportPanelKinds.All, allowed => string.Equals(allowed, kind, StringComparison.OrdinalIgnoreCase));

    private static bool IsChart(string? kind) =>
        string.Equals(kind, ReportPanelKinds.Chart, StringComparison.OrdinalIgnoreCase);

    private static bool HasNonEmptySpec(JsonElement? spec)
    {
        if (spec is not { } element)
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().MoveNext(),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => false,
        };
    }

    private static string PanelLabel(ReportPanel panel, int index) =>
        !string.IsNullOrWhiteSpace(panel.Title) ? panel.Title!
        : !string.IsNullOrWhiteSpace(panel.Id) ? panel.Id
        : $"index {index}";
}

/// <summary>Structural validation result for a report document. Mirrors <c>FormPackageValidationResult</c>.</summary>
public sealed class ReportValidationResult
{
    /// <summary>Whether the document has no error-severity issues.</summary>
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    /// <summary>Validation issues.</summary>
    [JsonPropertyName("issues")]
    public ReportValidationIssue[] Issues { get; init => field = value ?? []; } = [];
}

/// <summary>Single report-document validation issue. Mirrors <c>FormValidationIssue</c>.</summary>
public sealed class ReportValidationIssue
{
    /// <summary>Stable machine-readable issue code.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>Issue severity, usually error or warning.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";

    /// <summary>Optional JSON pointer or property path.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Human-readable issue message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
