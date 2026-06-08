// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.MapGeneration;

/// <summary>
/// Builds the grounded system/user prompts for map generation. Mirrors <c>FormGenerationPrompt</c>:
/// a lean, deterministic prompt that enumerates the supported source-protocol and basemap vocabulary
/// and the map-package body contract so a local model proposes only valid maps.
/// </summary>
internal static class MapGenerationPrompt
{
    /// <summary>Supported source protocols (mirrors <see cref="SourceProtocol"/> JSON names).</summary>
    internal static readonly string[] SourceProtocols = MapGenerationStructuralValidator.SourceProtocols;

    /// <summary>Suggested basemap style ids the author can bind (advisory; bound at publish).</summary>
    internal static readonly string[] Basemaps =
    [
        "streets", "outdoors", "light", "dark", "satellite", "satellite-streets", "topographic", "none"
    ];

    public static string BuildSystem(MapGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Honua's map author. You convert a natural-language request into a validated honua_map_package.v1 map body for the Honua Studio map builder.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- ALWAYS set format to \"honua_map_package.v1\" and provide a non-empty mapPackageId (a short slug you invent, e.g. \"map_flood_risk\").");
        sb.AppendLine("- Always set status to \"Draft\" and createdAt to the current UTC time in ISO-8601 (the server normalizes both); never invent a different lifecycle value.");
        sb.AppendLine("- A map MUST declare at least one sourceBinding (its layer list). Each sourceBinding has a unique sourceId (a short handle YOU invent, e.g. \"src_parcels\"), a protocol from the allowed list below, and a locator { url, serviceId?, layerId? }. If the request names no concrete service, use a descriptive placeholder url like \"https://placeholder/<service>\"; the operator binds the real layer at publish.");
        sb.AppendLine("- Reference styles via styleRefs [{ styleId, label?, presetId? }]. styleId is a style handle resolved at publish; invent a descriptive one (e.g. \"style_parcels\") when the request implies styling.");
        sb.AppendLine("- Use popupBindings [{ sourceId, fieldName, template? }] to show attributes on click, and labelBindings [{ sourceId, fieldName, placement? }] for labels. Each sourceId must reference a declared sourceBinding.");
        sb.AppendLine("- Use legend [{ label, color (hex like #2D69A5), minValue?, maxValue? }] for the map legend.");
        sb.AppendLine("- ALWAYS set initialView { bbox: [minLon, minLat, maxLon, maxLat], crs } as the starting extent. Default crs to \"EPSG:4326\". If the request names a place, pick a reasonable bounding box; otherwise use a world or regional extent.");
        sb.AppendLine("- Choose a basemap implicitly via a basemap sourceBinding or a styleRef; the supported basemap options are listed below.");
        sb.AppendLine("- The real layer/service/style is bound by the operator at publish time; if the request does not name a concrete layer, still propose the sources and use descriptive placeholders.");
        sb.AppendLine("- If the request is ambiguous (which layers, which extent, which styling), return status \"needs-clarification\" with clarifications instead of guessing.");
        sb.AppendLine("- If the request is not about building a map, return status \"refused\" with a short rationale.");
        sb.AppendLine("- Otherwise return status \"generated\" with the map and a one-paragraph rationale.");
        sb.AppendLine();

        if (request.RepairFailures is { Count: > 0 })
        {
            sb.AppendLine("Your previous map failed server validation. Fix these failures and return a corrected map:");
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

        if (request.AvailableSources is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("REAL published sources available in this workspace. When the request matches one of these,");
            sb.AppendLine("you MUST bind it directly: set the sourceBinding locator.serviceId and locator.layerId to the EXACT");
            sb.AppendLine("values below (use the layerId verbatim), and prefer its protocol. Do NOT use a placeholder url when a");
            sb.AppendLine("real source matches. Set initialView.bbox to the matched source's extent so the data is in view:");
            foreach (var source in request.AvailableSources)
            {
                sb.Append("- serviceId=\"").Append(source.ServiceId).Append("\" layerId=\"").Append(source.LayerId).Append('"');
                if (!string.IsNullOrWhiteSpace(source.Name))
                {
                    sb.Append(" name=\"").Append(source.Name).Append('"');
                }

                if (!string.IsNullOrWhiteSpace(source.GeometryType))
                {
                    sb.Append(" geometry=").Append(source.GeometryType);
                }

                if (!string.IsNullOrWhiteSpace(source.Protocol))
                {
                    sb.Append(" protocol=").Append(source.Protocol);
                }

                if (source.Bbox is { Length: 4 } bbox)
                {
                    sb.Append(" extent=[").Append(bbox[0]).Append(", ").Append(bbox[1]).Append(", ")
                      .Append(bbox[2]).Append(", ").Append(bbox[3]).Append(']');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.Append("Allowed source protocols: ").AppendLine(string.Join(", ", SourceProtocols));
        sb.Append("Suggested basemaps: ").AppendLine(string.Join(", ", Basemaps));
        return sb.ToString();
    }

    public static string BuildUser(MapGenerationProviderRequest request)
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

        if (request.CurrentMap?.SourceBindings is { Count: > 0 })
        {
            sb.AppendLine();
            sb.Append("Refine the existing map, which currently has ")
              .Append(request.CurrentMap.SourceBindings.Count)
              .AppendLine(" source binding(s). Preserve existing sources unless the request asks to change them.");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Provider-facing request for a single map-generation turn: the prompt, prior turns, answered
/// clarifications, the current map (refine), and validator failures fed back during repair.
/// </summary>
internal sealed record MapGenerationProviderRequest
{
    public required string Prompt { get; init; }
    public string? ModelOverride { get; init; }
    public IReadOnlyList<MapGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<MapGenerationAnswer> Answers { get; init; } = [];
    public MapPackage? CurrentMap { get; init; }
    public IReadOnlyList<MapValidationIssue> RepairFailures { get; init; } = [];
    public IReadOnlyList<MapGenerationSource> AvailableSources { get; init; } = [];
}
