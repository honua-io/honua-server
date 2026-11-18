// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Rendering;

// A served style document must not name a withheld field (publisher-hidden, or masked for this caller) or list its class values.
// In style documents any string that names such a field counts, so an unrecognised expression form drops styling rather than leaking.
internal static partial class StyleDocumentWithheldFields
{
    private static readonly string[] MapLibreStyledProperties = ["paint", "layout"];

    // A field name that equals a layout keyword ("none") must not flip a hidden layer visible.
    private const string MapLibreVisibilityProperty = "visibility";

    private const string EmptyMapLibreStyle = """{"version":8,"sources":{},"layers":[]}""";

    private static readonly HashSet<string> FieldReferenceProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "field",
        "field1",
        "field2",
        "field3",
        "fields",
        "normalizationField",
        "normalizeField",
        "tooltipField",
        "fieldName",
        "rotationField",
    };

    // Hidden fields plus every field the caller's masks name, including masked names the schema does not declare.
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

    // A catalog stylesheet named after a layer is that layer's style; a standalone style has no fields to withhold.
    public static async Task<string> WithoutWithheldStylesheetAsync(HttpContext context, string styleId, OgcStylesheet stylesheet)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stylesheet);
        var cancellationToken = context.RequestAborted;
        if (context.RequestServices.GetService<IMetadataV2GraphProvider>() is not { } graphProvider)
        {
            return stylesheet.Content;
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resource = snapshot.Graph.Resources.FirstOrDefault(candidate =>
            string.Equals(candidate.Metadata.Name, styleId, StringComparison.Ordinal));
        if (resource is null)
        {
            return stylesheet.Content;
        }

        var withheld = await ResolveAsync(context, resource, cancellationToken).ConfigureAwait(false);
        return stylesheet.Encoding switch
        {
            OgcStyleEncoding.MapboxStyle => WithoutWithheldMapLibre(stylesheet.Content, withheld) ?? EmptyMapLibreStyle,
            OgcStyleEncoding.GeoServicesDrawingInfo => WithoutWithheldDrawingInfo(stylesheet.Content, withheld),
            _ => WithoutWithheldSld(stylesheet.Content, withheld),
        };
    }

    // A style layer filtered on a withheld field is dropped (its features carry no such attribute for this caller);
    // a paint or layout property reading one is dropped, so the renderer default applies. Null when the style is not JSON.
    public static string? WithoutWithheldMapLibre(string styleJson, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);
        if (withheld.Count == 0)
        {
            return styleJson;
        }

        if (TryParse(styleJson) is not JsonObject style)
        {
            return null;
        }

        if (style["layers"] is JsonArray layers)
        {
            style["layers"] = new JsonArray([.. layers.OfType<JsonObject>()
                .Where(layer => !StyleReferences(layer["filter"], withheld))
                .Select(layer => WithoutReferencingProperties((JsonObject)layer.DeepClone(), withheld))]);
        }

        return style.ToJsonString();
    }

    // A renderer naming a withheld field falls back to its default symbol, or is removed; label classes naming one are dropped.
    public static string WithoutWithheldDrawingInfo(string drawingInfoJson, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);
        if (withheld.Count == 0 || TryParse(drawingInfoJson) is not JsonObject drawingInfo)
        {
            return drawingInfoJson;
        }

        if (drawingInfo["renderer"] is JsonObject renderer && StyleReferences(renderer, withheld))
        {
            if (renderer["defaultSymbol"] is JsonObject symbol && !StyleReferences(symbol, withheld))
            {
                drawingInfo["renderer"] = new JsonObject { ["type"] = "simple", ["symbol"] = symbol.DeepClone() };
            }
            else
            {
                drawingInfo.Remove("renderer");
            }
        }

        if (drawingInfo["labelingInfo"] is JsonArray labels)
        {
            drawingInfo["labelingInfo"] = new JsonArray([.. labels
                .Where(label => !StyleReferences(label, withheld))
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
            .Where(element => SldElementReferences(element, withheld))
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

    // Esri metadata rule: a field-reference property naming the field, or an Arcade, label, template or SQL where reference.
    public static bool NamesWithheldField(string? propertyName, string value, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(withheld);
        if (propertyName is not null
            && FieldReferenceProperties.Contains(propertyName)
            && withheld.Contains(value.Trim()))
        {
            return true;
        }

        var pattern = string.Equals(propertyName, "where", StringComparison.OrdinalIgnoreCase)
            ? SqlIdentifier()
            : ExpressionFieldReference();
        return pattern.Matches(value).Any(match => withheld.Contains(match.Groups["name"].Value.Trim()));
    }

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // A stored document that is not JSON is served by the caller's own fallback, never as written.
            return null;
        }
    }

    private static bool SldElementReferences(XElement element, IReadOnlySet<string> withheld)
        => element.Name.LocalName == "PropertyName"
            ? withheld.Contains(element.Value.Trim())
            : !element.HasElements && NamesWithheldField(null, element.Value, withheld);

    private static XElement RemovalTarget(XElement reference)
    {
        var ancestors = reference.Ancestors().ToArray();
        var rule = ancestors.FirstOrDefault(element => element.Name.LocalName == "Rule");
        var symbolizer = ancestors.FirstOrDefault(element => element.Name.LocalName.EndsWith("Symbolizer", StringComparison.Ordinal));
        return symbolizer is not null && rule is not null && symbolizer.Ancestors().Contains(rule)
            ? symbolizer
            : rule ?? reference;
    }

    private static JsonObject WithoutReferencingProperties(JsonObject layer, IReadOnlySet<string> withheld)
    {
        foreach (var styled in MapLibreStyledProperties.Select(name => layer[name]).OfType<JsonObject>())
        {
            var referencing = styled
                .Where(property => property.Key != MapLibreVisibilityProperty && StyleReferences(property.Value, withheld))
                .Select(property => property.Key)
                .ToArray();
            foreach (var name in referencing)
            {
                styled.Remove(name);
            }
        }

        return layer;
    }

    private static bool StyleReferences(JsonNode? node, IReadOnlySet<string> withheld, string? propertyName = null) => node switch
    {
        JsonObject value => value.Any(property => StyleReferences(property.Value, withheld, property.Key)),
        JsonArray items => items.Any(item => StyleReferences(item, withheld, propertyName)),
        JsonValue value when value.TryGetValue<string>(out var text)
            => withheld.Contains(text.Trim()) || NamesWithheldField(propertyName, text, withheld),
        _ => false,
    };

    // Arcade ($feature.name, $feature["name"], DomainName($feature, "name")), label ([name]) and template ({name}) references.
    [GeneratedRegex(
        """\$feature\.(?<name>[A-Za-z_]\w*)|\$feature\[\s*["'](?<name>[^"']+)["']\s*\]|\$feature\s*,\s*["'](?<name>[^"']+)["']|\[(?<name>[^\[\]]+)\]|\{(?<name>[^{}]+)\}""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex ExpressionFieldReference();

    [GeneratedRegex("""(?<name>[A-Za-z_]\w*)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SqlIdentifier();
}
