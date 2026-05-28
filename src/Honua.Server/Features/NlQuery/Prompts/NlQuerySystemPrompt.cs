// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.NlQuery.Prompts;

/// <summary>
/// Builds the system prompt for the NL query plan provider.
/// The prompt embeds the resource schema and the filter plan JSON schema
/// so the model can produce valid, grounded structured output.
/// </summary>
internal static class NlQuerySystemPrompt
{
    /// <summary>
    /// Builds a system prompt for the given Metadata v2 resource.
    /// </summary>
    public static string Build(MetadataV2Resource resource)
    {
        var sb = new StringBuilder(2048);

        sb.AppendLine("You are a spatial query planner. Your job is to convert a natural-language query into a structured filter plan.");
        sb.AppendLine("You must ONLY produce a valid JSON object matching the FilterPlan schema. Do NOT produce SQL, CQL, or any other query language.");
        sb.AppendLine();

        sb.AppendLine("## Resource Schema");
        sb.AppendLine();
        sb.Append("Resource name: ").AppendLine(resource.Metadata.Name);
        sb.Append("SRID: ").Append(resource.ReadSrid()).AppendLine();
        sb.Append("Geometry type: ").AppendLine(resource.ReadGeometryType().ToString());
        sb.AppendLine();

        sb.AppendLine("### Queryable Fields");
        sb.AppendLine();
        foreach (var field in resource.SchemaFields)
        {
            if (field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
            {
                continue;
            }

            sb.Append("- ").Append(field.Name).Append(" (").Append(field.Type.ToString().ToLowerInvariant()).Append(')');
            if (!string.IsNullOrWhiteSpace(field.Description))
            {
                sb.Append(" — ").Append(field.Description);
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("## Filter Plan Schema");
        sb.AppendLine();
        sb.AppendLine("The response must be a JSON object with:");
        sb.AppendLine("- `combinator`: \"and\" or \"or\"");
        sb.AppendLine("- `clauses`: array of clause objects");
        sb.AppendLine();
        sb.AppendLine("Each clause has a `type` field and a corresponding data object:");
        sb.AppendLine();
        sb.AppendLine("### Comparison clause (`type: \"comparison\"`)");
        sb.AppendLine("- `comparison.property`: one of the field names above");
        sb.AppendLine("- `comparison.operator`: eq, neq, lt, lte, gt, gte, like, in");
        sb.AppendLine("- `comparison.value`: string, number, boolean, or array of strings (for \"in\")");
        sb.AppendLine();
        sb.AppendLine("### Spatial clause (`type: \"spatial\"`)");
        sb.AppendLine("- `spatial.operator`: intersects, within, contains, dwithin");
        sb.AppendLine("- `spatial.geometry`: GeoJSON geometry object (Point, Polygon, etc.)");
        sb.AppendLine("- `spatial.distance`: number (required when operator is \"dwithin\")");
        sb.AppendLine("- `spatial.distanceUnit`: meters, kilometers, miles, feet (default: meters)");
        sb.AppendLine();
        sb.AppendLine("### Temporal clause (`type: \"temporal\"`)");
        sb.AppendLine("- `temporal.property`: one of the date/datetime field names above");
        sb.AppendLine("- `temporal.operator`: before, after, during");
        sb.AppendLine("- `temporal.start`: ISO 8601 date-time (for after, during)");
        sb.AppendLine("- `temporal.end`: ISO 8601 date-time (for before, during)");
        sb.AppendLine();
        sb.AppendLine("### Nested clause (`type: \"nested\"`)");
        sb.AppendLine("- `nested.combinator`: \"and\" or \"or\"");
        sb.AppendLine("- `nested.clauses`: array of clause objects (one level of nesting only)");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine("1. Only reference fields listed in the Queryable Fields section.");
        sb.AppendLine("2. Use the exact field names as shown (case-sensitive).");
        sb.AppendLine("3. For spatial queries, use GeoJSON with WGS 84 (EPSG:4326) coordinates.");
        sb.AppendLine("4. Never produce SQL, CQL, WKT, or any query language — only the JSON filter plan.");
        sb.AppendLine("5. If the query cannot be expressed as a filter plan, respond with an empty clauses array.");

        return sb.ToString();
    }
}
