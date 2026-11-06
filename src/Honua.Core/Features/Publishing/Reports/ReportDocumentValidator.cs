// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Reports;

// This structural check is the only validation gate, because the publication registry hashes the payload and does not parse it.
public static class ReportDocumentValidator
{
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
            else if (!IsChart(panel.Kind) && HasNonEmptySpec(panel.ChartSpec))
            {
                // Exact converse of chartSpecRequired: only chart panels may carry a spec. A spec on a
                // map/table/text panel is contradictory authoring — the chart it describes is never
                // rendered — so it is an error, not a tolerated extra. Deliberately keyed off the same
                // non-empty test, so an absent, null, or empty round-tripped chartSpec never trips it.
                issues.Add(new ReportValidationIssue
                {
                    Code = "chartSpecNotAllowed",
                    Severity = "error",
                    Message = $"Panel '{PanelLabel(panel, i)}' has kind '{panel.Kind}' and must not carry a Vega-Lite spec; only chart panels may.",
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

public sealed class ReportValidationResult
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    [JsonPropertyName("issues")]
    public ReportValidationIssue[] Issues { get; init => field = value ?? []; } = [];
}

public sealed class ReportValidationIssue
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
