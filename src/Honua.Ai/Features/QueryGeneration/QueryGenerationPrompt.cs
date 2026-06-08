// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Ai.QueryGeneration;

/// <summary>
/// Builds the grounded system/user prompts for saved-query generation. Mirrors
/// <c>AnalysisGenerationPrompt</c>/<c>FormGenerationPrompt</c>: a lean, deterministic prompt that
/// enumerates the supported spatial/attribute filter vocabulary (the filter-plan combinators and the
/// comparison/spatial/temporal operators the canonical <c>FilterPlanCompiler</c> accepts) and the
/// saved-query content contract so a local model proposes only filter plans whose clauses use real
/// operators that the server can later compile against the bound layer's schema.
/// </summary>
internal static class QueryGenerationPrompt
{
    public static string BuildSystem(QueryGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Honua's GIS query author. You convert a natural-language request into a validated saved query that the feature-query pipeline can run against a layer.");
        sb.AppendLine();
        sb.AppendLine("A saved query has:");
        sb.AppendLine("- naturalLanguageQuery: the original request text (echo it back).");
        sb.AppendLine("- layerId: the numeric id of the target layer. If the user named a layer but you do not know its numeric id, set 0 and note it in the rationale — the operator binds the real layer at run/preview time.");
        sb.AppendLine("- serviceName: optional service name the user referenced.");
        sb.AppendLine("- filterPlan: the where/filter clause tree (omit entirely for a \"select all features\" query).");
        sb.AppendLine("- outFields: the attribute fields to return. Empty array means all fields.");
        sb.AppendLine("- outputSrid: optional desired output spatial reference id (e.g. 4326).");
        sb.AppendLine("- previewLimit: optional max feature count for previews.");
        sb.AppendLine();
        sb.AppendLine("A filterPlan is { combinator, clauses[] } where combinator is \"and\" or \"or\". Each clause has a \"type\" and the matching payload object:");
        sb.AppendLine("- comparison: { property, operator, value }. Attribute predicate. operator is one of:");
        sb.AppendLine("    eq (=), neq (!=), gt (>), gte (>=), lt (<), lte (<=), like (text pattern, use % wildcards), in (value is an array), isNull (value omitted).");
        sb.AppendLine("- spatial: { operator, geometry, distance?, distanceUnit? }. Spatial predicate against the layer geometry. operator is one of:");
        sb.AppendLine("    intersects, within, contains, dwithin. geometry is a GeoJSON geometry object (WGS84). For dwithin you MUST set distance (a positive number) and distanceUnit (meters/kilometers/miles/feet).");
        sb.AppendLine("- temporal: { property, operator, start?, end? }. Date/time predicate. operator is one of:");
        sb.AppendLine("    before (needs end), after (needs start), during (needs start and end). Bounds are ISO 8601 strings.");
        sb.AppendLine("- nested: { combinator, clauses[] }. One sub-group to express grouping like (A or B) and C.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use ONLY the operators listed above. Never invent an operator, clause type, or combinator.");
        sb.AppendLine("- Each comparison clause must name a property (field) and supply a value (except isNull). The 'in' operator's value MUST be an array. Translate ranges like \"between X and Y\" into two clauses (gte X and lte Y) combined with \"and\".");
        sb.AppendLine("- Each spatial clause must include a GeoJSON geometry. Use intersects for \"in/overlapping\", within for \"inside\", contains for \"contains\", and dwithin for \"within N units of\" (with distance + distanceUnit).");
        sb.AppendLine("- Do not guess field names you were not given; if the request depends on an unknown field or layer, return status \"needs-clarification\" with clarifications instead of guessing.");
        sb.AppendLine("- Set outFields only when the user asked for specific attributes; otherwise return an empty array (all fields).");
        sb.AppendLine("- If the request is not about querying/filtering features, return status \"refused\" with a short rationale.");
        sb.AppendLine("- Otherwise return status \"generated\" with the query and a one-paragraph rationale.");
        sb.AppendLine();

        if (request.RepairFailures is { Count: > 0 })
        {
            sb.AppendLine("Your previous query failed server validation. Fix these failures and return a corrected query:");
            foreach (var failure in request.RepairFailures)
            {
                sb.Append("- [").Append(failure.Code).Append("] ").Append(failure.Message);
                if (!string.IsNullOrWhiteSpace(failure.FieldPath))
                {
                    sb.Append(" (at ").Append(failure.FieldPath).Append(')');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        if (request.AvailableSources is { Count: > 0 })
        {
            sb.AppendLine("REAL published layers available in this workspace. When the request matches one, you MUST set");
            sb.AppendLine("layerId to its EXACT numeric id below (not 0), and choose property/outFields names ONLY from its");
            sb.AppendLine("field list:");
            foreach (var source in request.AvailableSources)
            {
                sb.Append("- layerId=").Append(source.LayerId).Append(" service=\"").Append(source.ServiceId).Append('"');
                if (!string.IsNullOrWhiteSpace(source.Name))
                {
                    sb.Append(" name=\"").Append(source.Name).Append('"');
                }

                if (source.Fields is { Length: > 0 } fields)
                {
                    sb.Append(" fields=[").Append(string.Join(", ", fields)).Append(']');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildUser(QueryGenerationProviderRequest request)
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

        if (request.CurrentQuery is { } current)
        {
            sb.AppendLine();
            var clauseCount = current.FilterPlan?.Clauses?.Length ?? 0;
            sb.Append("Refine the existing saved query, which targets layer ")
              .Append(current.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(" and currently has ")
              .Append(clauseCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .AppendLine(" top-level filter clause(s). Preserve existing clauses unless the request asks to change them.");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Provider-facing request for a single query-generation turn: the prompt, prior turns, answered
/// clarifications, the current saved query (refine), and validator failures fed back during repair.
/// </summary>
internal sealed record QueryGenerationProviderRequest
{
    public required string Prompt { get; init; }
    public string? ModelOverride { get; init; }
    public IReadOnlyList<QueryGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<QueryGenerationAnswer> Answers { get; init; } = [];
    public SavedQueryContent? CurrentQuery { get; init; }
    public IReadOnlyList<QueryStructuralFailure> RepairFailures { get; init; } = [];
    public IReadOnlyList<QueryGenerationSource> AvailableSources { get; init; } = [];
}
