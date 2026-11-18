// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Rendering;

// A served style document must not name a withheld field (publisher-hidden, or masked for this caller) or list its class values.
// Any string that names such a field counts as a reference, so an unrecognised expression form drops styling rather than leaking.
internal static partial class StyleDocumentWithheldFields
{
    private static readonly string[] MapLibreStyledProperties = ["paint", "layout"];

    public static async Task<IReadOnlySet<string>> ResolveAsync(
        HttpContext context, MetadataV2Object resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        var withheld = new HashSet<string>(resource.HiddenSchemaFieldNames(), StringComparer.OrdinalIgnoreCase);
        if (context.RequestServices.GetService<IFieldMaskSource>() is { } masks)
        {
            withheld.UnionWith(await masks.ResolveAsync(resource, cancellationToken).ConfigureAwait(false));
        }

        return withheld;
    }

    // A style layer filtered on a withheld field is dropped (its features carry no such attribute for this caller);
    // a paint or layout property reading one is dropped, so the renderer default applies.
    public static string WithoutWithheldMapLibre(string styleJson, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);
        if (withheld.Count == 0 || JsonNode.Parse(styleJson) is not JsonObject style || style["layers"] is not JsonArray layers)
        {
            return styleJson;
        }

        var kept = new JsonArray();
        foreach (var layer in layers.OfType<JsonObject>().Select(layer => (JsonObject)layer.DeepClone()))
        {
            if (References(layer["filter"], withheld))
            {
                continue;
            }

            foreach (var styled in MapLibreStyledProperties.Select(name => layer[name]).OfType<JsonObject>())
            {
                RemoveReferencingProperties(styled, withheld);
            }

            kept.Add(layer);
        }

        style["layers"] = kept;
        return style.ToJsonString();
    }

    // A renderer naming a withheld field falls back to its default symbol; label classes naming one are dropped.
    public static string WithoutWithheldDrawingInfo(string drawingInfoJson, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);
        if (withheld.Count == 0 || JsonNode.Parse(drawingInfoJson) is not JsonObject drawingInfo)
        {
            return drawingInfoJson;
        }

        if (drawingInfo["renderer"] is JsonObject renderer && References(renderer, withheld))
        {
            drawingInfo["renderer"] = renderer["defaultSymbol"] is JsonObject symbol && !References(symbol, withheld)
                ? new JsonObject { ["type"] = "simple", ["symbol"] = symbol.DeepClone() }
                : null;
        }

        if (drawingInfo["labelingInfo"] is JsonArray labels)
        {
            drawingInfo["labelingInfo"] = new JsonArray([.. labels
                .Where(label => !References(label, withheld))
                .Select(label => label?.DeepClone())]);
        }

        return drawingInfo.ToJsonString();
    }

    // A rule filtered on a withheld field is dropped; a symbolizer reading one (a label, a rotation) is dropped from its rule.
    public static string WithoutWithheldSld(string sld, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);
        if (withheld.Count == 0)
        {
            return sld;
        }

        var document = XDocument.Parse(sld, LoadOptions.PreserveWhitespace);
        var referencing = document.Descendants()
            .Where(element => element.Name.LocalName == "PropertyName" && withheld.Contains(element.Value.Trim()))
            .Select(RemovalTarget)
            .Distinct()
            .ToArray();
        foreach (var element in referencing.Where(element => !element.Ancestors().Any(referencing.Contains)))
        {
            element.Remove();
        }

        var body = document.ToString(SaveOptions.DisableFormatting);
        return document.Declaration is { } declaration ? declaration + body : body;
    }

    private static XElement RemovalTarget(XElement propertyName)
    {
        var ancestors = propertyName.Ancestors().ToArray();
        var rule = ancestors.FirstOrDefault(element => element.Name.LocalName == "Rule");
        var symbolizer = ancestors.FirstOrDefault(element => element.Name.LocalName.EndsWith("Symbolizer", StringComparison.Ordinal));
        return symbolizer is not null && rule is not null && symbolizer.Ancestors().Contains(rule)
            ? symbolizer
            : rule ?? propertyName;
    }

    private static void RemoveReferencingProperties(JsonObject styled, IReadOnlySet<string> withheld)
    {
        foreach (var name in styled.Where(property => References(property.Value, withheld)).Select(property => property.Key).ToArray())
        {
            styled.Remove(name);
        }
    }

    private static bool References(JsonNode? node, IReadOnlySet<string> withheld) => node switch
    {
        JsonObject value => value.Any(property => References(property.Value, withheld)),
        JsonArray items => items.Any(item => References(item, withheld)),
        JsonValue value when value.TryGetValue<string>(out var text) => NamesWithheldField(text, withheld),
        _ => false,
    };

    private static bool NamesWithheldField(string text, IReadOnlySet<string> withheld)
        => withheld.Contains(text.Trim())
           || FieldReference().Matches(text).Any(match => withheld.Contains(match.Groups["name"].Value.Trim()));

    // Arcade ($feature.name, $feature["name"], DomainName($feature, "name")), label ([name]) and template ({name}) references.
    [GeneratedRegex(
        """\$feature\.(?<name>[A-Za-z_]\w*)|\$feature\[\s*["'](?<name>[^"']+)["']\s*\]|\$feature\s*,\s*["'](?<name>[^"']+)["']|\[(?<name>[^\[\]]+)\]|\{(?<name>[^{}]+)\}""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex FieldReference();
}
