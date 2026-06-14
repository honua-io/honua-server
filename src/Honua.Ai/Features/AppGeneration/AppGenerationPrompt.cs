// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;

namespace Honua.Ai.AppGeneration;

/// <summary>
/// Builds the grounded system/user prompts for app generation. Mirrors <c>MapGenerationPrompt</c>: a
/// lean, deterministic prompt that enumerates the supported studio-app/v1 vocabulary (component kinds,
/// permission tiers, visibility tiers) and the app-package body contract so a local model proposes only
/// valid apps the console can round-trip directly.
/// </summary>
internal static class AppGenerationPrompt
{
    /// <summary>Component kinds a page may render (mirrors the structural validator's allowed set).</summary>
    internal static readonly string[] ComponentKinds = AppGenerationStructuralValidator.ComponentKinds;

    /// <summary>Action permission tiers.</summary>
    internal static readonly string[] Permissions = AppGenerationStructuralValidator.Permissions;

    /// <summary>Share-policy visibility tiers.</summary>
    internal static readonly string[] Visibilities = AppGenerationStructuralValidator.Visibilities;

    public static string BuildSystem(AppGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Honua's app author. You convert a natural-language request into a validated studio-app/v1 app body for the Honua Studio app builder.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- ALWAYS set schemaVersion to \"studio-app/v1\" (the server normalizes it; never invent a different version).");
        sb.AppendLine("- ALWAYS set a non-empty title and a one-sentence summary describing the app.");
        sb.AppendLine("- An app MUST declare at least one page. Each page has a unique route (a URL path beginning with \"/\", e.g. \"/\" for the home page, \"/parcels\"), a title, and a component { kind, binding? }.");
        sb.AppendLine($"- component.kind MUST be one of: {string.Join(", ", ComponentKinds)}. Choose the kind that matches the page's purpose (e.g. map for a spatial view, dashboard for charts/metrics, form for data entry, report for a narrative, table for tabular data).");
        sb.AppendLine("- component.binding references the content the page shows, written as \"content:<ref>@v<N>\" (e.g. \"content:flood-map@v1\"). Invent a descriptive ref when the request implies content; the operator binds the real content version at publish time.");
        sb.AppendLine("- Provide navigation [{ route, label }] for the app's nav menu; each route should reference a declared page.");
        sb.AppendLine($"- Provide actions [{{ name, pageRoute, requiredPermission }}] for interactive actions; pageRoute must reference a declared page route and requiredPermission MUST be one of: {string.Join(", ", Permissions)}.");
        sb.AppendLine($"- Provide sharePolicy {{ visibility, embed, reviewed }}. visibility MUST be one of: {string.Join(", ", Visibilities)}. Default visibility to \"private\", embed to false, and reviewed to false unless the request says otherwise.");
        sb.AppendLine("- The real content/version behind each binding is bound by the operator at publish time; if the request does not name concrete content, still propose the pages and use descriptive binding refs.");
        sb.AppendLine("- If the request is ambiguous (which pages, which components, which sharing), return status \"needs-clarification\" with clarifications instead of guessing.");
        sb.AppendLine("- If the request is not about building an app, return status \"refused\" with a short rationale.");
        sb.AppendLine("- Otherwise return status \"generated\" with the app and a one-paragraph rationale.");
        sb.AppendLine();

        if (request.RepairFailures is { Count: > 0 })
        {
            sb.AppendLine("Your previous app failed server validation. Fix these failures and return a corrected app:");
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

        sb.Append("Allowed component kinds: ").AppendLine(string.Join(", ", ComponentKinds));
        sb.Append("Allowed permissions: ").AppendLine(string.Join(", ", Permissions));
        sb.Append("Allowed visibility tiers: ").AppendLine(string.Join(", ", Visibilities));
        return sb.ToString();
    }

    public static string BuildUser(AppGenerationProviderRequest request)
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

        if (request.CurrentApp is { } current
            && current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty("pages", out var pages)
            && pages.ValueKind == JsonValueKind.Array
            && pages.GetArrayLength() > 0)
        {
            sb.AppendLine();
            sb.Append("Refine the existing app, which currently has ")
              .Append(pages.GetArrayLength())
              .AppendLine(" page(s). Preserve existing pages unless the request asks to change them.");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Provider-facing request for a single app-generation turn: the prompt, prior turns, answered
/// clarifications, the current app (refine), and validator failures fed back during repair.
/// </summary>
internal sealed record AppGenerationProviderRequest
{
    public required string Prompt { get; init; }
    public string? ModelOverride { get; init; }
    public IReadOnlyList<AppGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<AppGenerationAnswer> Answers { get; init; } = [];
    public JsonElement? CurrentApp { get; init; }
    public IReadOnlyList<AppValidationIssue> RepairFailures { get; init; } = [];
}
