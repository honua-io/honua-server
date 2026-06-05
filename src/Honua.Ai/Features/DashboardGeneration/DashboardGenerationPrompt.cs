// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Publishing.Dashboards;

namespace Honua.Ai.DashboardGeneration;

/// <summary>
/// Builds the grounded system/user prompts for dashboard generation. Mirrors <c>ReportGenerationPrompt</c>:
/// a lean, deterministic prompt that enumerates the supported panel kinds and the dashboard-document
/// contract (Vega-Lite charts, data bindings, interactive filter/metric panels) so a local model proposes
/// only structurally-valid dashboards.
/// </summary>
internal static class DashboardGenerationPrompt
{
    /// <summary>Supported dashboard panel kinds (mirrors <see cref="DashboardPanelKinds.All"/>).</summary>
    internal static readonly string[] PanelKinds = DashboardPanelKinds.All;

    public static string BuildSystem(DashboardGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Honua's dashboard author. You convert a natural-language request into a validated honua.dashboard-document.v1 for the dashboard builder.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- ALWAYS set a concise dashboard title. The title is required.");
        sb.AppendLine("- A dashboard is a layout of panels. Produce at least one panel.");
        sb.AppendLine("- Each panel has a unique id (a short handle YOU invent, e.g. \"p_trend\"), a title, and a kind from the allowed list below. Never invent a panel kind.");
        sb.AppendLine("- Panel kinds: \"chart\" (a Vega-Lite visualization), \"map\" (a map slot), \"table\" (a tabular slot), \"filter\" (an interactive cross-filter control that links the other panels), \"metric\" (a single KPI/metric value).");
        sb.AppendLine("- For every \"chart\" panel you MUST provide a non-empty chartSpec: a valid Vega-Lite v5 spec object that declares \"$schema\": \"https://vega.github.io/schema/vega-lite/v5.json\", a \"mark\", and an \"encoding\". Non-chart panels MUST NOT include a chartSpec.");
        sb.AppendLine("- Declare data bindings the panels read from: each binding has an alias (a short handle, e.g. \"requests\"), a contentRef (a descriptive content id; use a placeholder like \"placeholder\" if the request names no concrete dataset), and an optional versionPin.");
        sb.AppendLine("- Set each chart/map/table/metric panel's bindingAlias to one of the declared binding aliases. A \"filter\" panel instead sets a \"field\" (the field it cross-filters the other panels on) and needs no binding.");
        sb.AppendLine("- The data binding content is resolved by the operator at publish time; if the request does not name a concrete dataset, still propose the panels and use a descriptive placeholder contentRef.");
        sb.AppendLine("- Optionally set a routeSlug (a short, url-safe slug), a narrative intro, and a breakpoint (\"desktop\", \"tablet\", or \"mobile\") for the responsive preview.");
        sb.AppendLine("- If the request is ambiguous (which data, which charts, what to filter on), return status \"needs-clarification\" with clarifications instead of guessing.");
        sb.AppendLine("- If the request is not about building a dashboard-style document, return status \"refused\" with a short rationale.");
        sb.AppendLine("- Otherwise return status \"generated\" with the dashboard and a one-paragraph rationale.");
        sb.AppendLine();

        if (request.RepairFailures is { Count: > 0 })
        {
            sb.AppendLine("Your previous dashboard failed structural validation. Fix these failures and return a corrected dashboard:");
            foreach (var failure in request.RepairFailures)
            {
                sb.Append("- [").Append(failure.Code).Append("] ").Append(failure.Message);
                if (!string.IsNullOrWhiteSpace(failure.Path))
                {
                    sb.Append(" (at ").Append(failure.Path).Append(')');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.Append("Allowed panel kinds: ").AppendLine(string.Join(", ", PanelKinds));
        return sb.ToString();
    }

    public static string BuildUser(DashboardGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(request.Prompt);

        if (request.Answers is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Answers to prior clarifications:");
            foreach (var answer in request.Answers)
            {
                sb.Append("- ").Append(answer.QuestionId).Append(" = ").AppendLine(answer.OptionId);
            }
        }

        if (request.CurrentDashboard?.Panels is { Length: > 0 })
        {
            sb.AppendLine();
            sb.Append("Refine the existing dashboard, which currently has ")
              .Append(request.CurrentDashboard.Panels.Length)
              .AppendLine(" panel(s). Preserve existing panels unless the request asks to change them.");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Provider-facing request for a single dashboard-generation turn: the prompt, prior turns, answered
/// clarifications, the current dashboard (refine), and validator failures fed back during repair.
/// </summary>
internal sealed record DashboardGenerationProviderRequest
{
    public required string Prompt { get; init; }
    public string? ModelOverride { get; init; }
    public IReadOnlyList<DashboardGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<DashboardGenerationAnswer> Answers { get; init; } = [];
    public DashboardDocument? CurrentDashboard { get; init; }
    public IReadOnlyList<DashboardValidationIssue> RepairFailures { get; init; } = [];
}
