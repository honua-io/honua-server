// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Api.Styles;

namespace Honua.Server.Features.Studio;

/// <summary>Resolves portable style bindings from actual catalog and publication state.</summary>
internal static partial class StudioDraftStyleReferences
{
    private static readonly string[] AppearanceProperties = ["paint", "layout", "filter", "minzoom", "maxzoom", "metadata"];
    private static readonly HashSet<string> CrossTypeProperties = new(["filter", "minzoom", "maxzoom", "visibility"], StringComparer.Ordinal);

    internal static async Task<StudioCompositionBody> ResolveAsync(HttpContext context,
        StudioCompositionBody body, CancellationToken cancellationToken)
    {
        var references = (body.StyleRefs ?? []).ToList();
        foreach (var styleId in body.Layers.Select(layer => layer.StyleRef).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (references.Any(reference => reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("styleId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == styleId)) continue;
            var reference = new JsonObject { ["styleId"] = styleId };
            using var document = JsonDocument.Parse(reference.ToJsonString());
            references.Add(document.RootElement.Clone());
        }
        if (references.Count == 0 && body.MapSpec is null) return body;
        if (references.Count > 256 || body.MapSpec is not { ValueKind: JsonValueKind.Object })
            Fail("Style references require a bounded, initialized map.");
        var canonical = JsonNode.Parse(body.MapSpec!.Value.GetRawText())!.AsObject();
        var canonicalRenders = canonical["layers"]?.AsArray()
            ?? throw new StudioCompositionBodyException("Map layers are missing.");
        var effective = canonical.DeepClone().AsObject();
        var renders = effective["layers"]?.AsArray() ?? throw new StudioCompositionBodyException("Map layers are missing.");
        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var normalized = new List<JsonElement>();
        var styleIds = new HashSet<string>(StringComparer.Ordinal);
        var claimedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (reference.ValueKind != JsonValueKind.Object) Fail("A style reference must be an object.");
            var node = JsonNode.Parse(reference.GetRawText())!.AsObject();
            var styleId = RequiredText(node, "styleId");
            if (!styleIds.Add(styleId)) Fail("Duplicate style references must be combined before saving.");
            if (node["body"] is JsonObject inline)
            {
                if (Encoding.UTF8.GetByteCount(inline.ToJsonString()) > 65536 || inline.Count == 0)
                    Fail("An inline style requires a bounded, nonempty override body.");
                if (node["layerBindings"] is not null)
                    Fail("Save map-local snapshots without live catalog bindings.");
                foreach (var entry in inline)
                {
                    var target = FindRender(renders, entry.Key);
                    _ = FindOwner(body, target, styleId);
                    if (entry.Value is not JsonObject) Fail("A style override must be an object.");
                    var patch = entry.Value!.AsObject();
                    if (patch.Any(property => !AppearanceProperties.Contains(property.Key, StringComparer.Ordinal)))
                        Fail("Inline styles may change appearance only.");
                    if (patch["metadata"] is JsonObject metadata)
                    {
                        metadata.Remove("honua:compositionLayerId");
                        metadata.Remove("honua:compositionSourceId");
                    }
                    Merge(target, patch, AppearanceProperties, preserveHidden: false);
                }
            }
            else
            {
                if (node.ContainsKey("body")) Fail("A live style must omit its inline body.");
                if (await OgcStylesEndpoints.AuthorizeStyleAsync(context, styleId, graphProvider, cancellationToken).ConfigureAwait(false) is not null)
                    throw new UnauthorizedAccessException();
                var stylesheet = await context.RequestServices.GetRequiredService<IOgcStyleProjection>()
                    .GetPublishedStylesheetAsync(styleId, OgcStyleEncoding.MapboxStyle, cancellationToken).ConfigureAwait(false);
                if (stylesheet is null || Encoding.UTF8.GetByteCount(stylesheet.Content) > 1048576)
                    Fail("The selected style is unavailable or exceeds the map style limit.");
                var catalog = JsonNode.Parse(stylesheet!.Content)?.AsObject();
                var catalogLayers = catalog?["layers"]?.AsArray()
                    ?? throw new StudioCompositionBodyException("The catalog style has no render layers.");
                var bindings = node["layerBindings"] as JsonArray;
                if (node.ContainsKey("layerBindings") && bindings is null) Fail("Style bindings must be an array.");
                bindings = CompleteBindings(body, canonicalRenders, renders, catalogLayers, styleId, bindings);
                if (bindings.Count is < 1 or > 256) Fail("A shared style requires between 1 and 256 render bindings.");
                foreach (var value in bindings)
                {
                    var binding = value as JsonObject ?? throw new StudioCompositionBodyException("Invalid style binding.");
                    if (binding.Any(property => property.Key is not ("styleLayerId" or "targetLayerId" or "layerType" or "properties")))
                        Fail("A style binding contains unsupported members.");
                    var targetId = RequiredText(binding, "targetLayerId");
                    if (!claimedTargets.Add(targetId)) Fail("Only one shared override may claim a render layer.");
                    var target = FindRender(renders, targetId);
                    _ = FindOwner(body, target, styleId);
                    var selected = FindRender(catalogLayers, RequiredText(binding, "styleLayerId"));
                    var expectedType = RequiredText(binding, "layerType");
                    if (RequiredText(selected, "type") != expectedType) Fail("The shared style layer changed type; rebind it.");
                    var properties = ReadProperties(binding);
                    if (RequiredText(target, "type") != expectedType && properties.Any(property => !CrossTypeProperties.Contains(property)))
                        Fail("Shared paint and layout require compatible render types.");
                    var appearance = new JsonObject();
                    foreach (var property in AppearanceProperties)
                        if (selected[property] is { } member) appearance[property] = member.DeepClone();
                    Merge(target, appearance, properties, preserveHidden: true);
                }
                node["layerBindings"] = bindings.DeepClone();
            }
            using var document = JsonDocument.Parse(node.ToJsonString());
            normalized.Add(document.RootElement.Clone());
        }
        if (body.Layers.Any(layer => layer.StyleRef is { } id && !styleIds.Contains(id)))
            Fail("A selected style has no matching portable reference.");
        // Validate the appearance that will actually render, including map-local
        // snapshots and labels. Unselected catalog properties do not constrain
        // the target's field schema, but every applied expression must resolve
        // against that render layer's current published source.
        foreach (var render in renders.OfType<JsonObject>())
        {
            if (render["source"] is null) continue;
            var owner = FindOwner(body, render, styleId: null);
            await CheckFieldsAsync(context, snapshot, owner, render, cancellationToken).ConfigureAwait(false);
        }
        var effectiveJson = effective.ToJsonString();
        var canonicalJson = canonical.ToJsonString();
        if (Encoding.UTF8.GetByteCount(effectiveJson) > 1048576 || Encoding.UTF8.GetByteCount(canonicalJson) > 1048576)
            Fail("The resulting map style exceeds the map style limit.");
        using var effectiveDocument = JsonDocument.Parse(effectiveJson);
        if (!context.RequestServices.GetRequiredService<IMapStyleValidator>().TryValidate(effectiveDocument.RootElement, out var error))
            Fail(error ?? "The effective map style is invalid.");
        // Persist structural targets only. Catalog paint/layout remains live and
        // is resolved afresh by readers; a save must not turn it into a snapshot.
        using var canonicalDocument = JsonDocument.Parse(canonicalJson);
        return body with { StyleRefs = normalized, MapSpec = canonicalDocument.RootElement.Clone() };
    }

    private static JsonArray CompleteBindings(StudioCompositionBody body, JsonArray canonicalRenders,
        JsonArray renders, JsonArray catalog, string styleId, JsonArray? existing)
    {
        var bindings = existing?.DeepClone().AsArray() ?? new JsonArray();
        var coveredOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in bindings)
        {
            var binding = value as JsonObject ?? throw new StudioCompositionBodyException("Invalid style binding.");
            coveredOwners.Add(FindOwner(body, FindRender(renders, RequiredText(binding, "targetLayerId")), styleId).Id);
        }
        foreach (var owner in body.Layers.Where(layer => layer.StyleRef == styleId))
        {
            // Preserve intentional explicit bindings. Derive defaults only for
            // an owner not yet covered, including a second use of this style.
            if (coveredOwners.Contains(owner.Id)) continue;
            var target = FindRender(renders, owner.Id);
            var type = RequiredText(target, "type");
            var compatible = catalog.OfType<JsonObject>().Where(layer => layer["type"]?.GetValue<string>() == type).ToArray();
            if (compatible.Length != 1) Fail("Select an explicit compatible catalog render layer before saving.");
            var catalogId = RequiredText(compatible[0], "id");
            bindings.Add(new JsonObject { ["styleLayerId"] = catalogId, ["targetLayerId"] = owner.Id, ["layerType"] = type });
            if (type == "fill" && renders.OfType<JsonObject>().Any(layer => layer["id"]?.GetValue<string>() == $"{owner.Id}__outline"))
                bindings.Add(new JsonObject
                {
                    ["styleLayerId"] = catalogId, ["targetLayerId"] = $"{owner.Id}__outline", ["layerType"] = type,
                    ["properties"] = new JsonArray("filter", "minzoom", "maxzoom", "visibility")
                });
            var primary = compatible[0];
            foreach (var symbol in catalog.OfType<JsonObject>().Where(layer =>
                layer["type"]?.GetValue<string>() == "symbol"
                && primary["source"] is not null
                && JsonNode.DeepEquals(layer["source"], primary["source"])
                && JsonNode.DeepEquals(layer["source-layer"], primary["source-layer"])))
            {
                if (type == "symbol" && ReferenceEquals(symbol, primary)) continue;
                if (bindings.Count >= 256) Fail("A shared style supports at most 256 render bindings.");
                var symbolId = RequiredText(symbol, "id");
                // Ensure a duplicate catalog id cannot silently select another layer.
                _ = FindRender(catalog, symbolId);
                var targetId = StudioCompositionBodyEditor.SharedSymbolRenderId(owner.Id, styleId, symbolId);
                if (renders.OfType<JsonObject>().Any(layer => layer["id"]?.GetValue<string>() == targetId))
                    Fail("A generated shared label conflicts with an existing render layer.");
                var label = new JsonObject
                {
                    ["id"] = targetId, ["type"] = "symbol", ["source"] = target["source"]!.DeepClone(),
                    ["metadata"] = new JsonObject
                    {
                        ["honua:compositionLayerId"] = owner.Id,
                        ["honua:compositionSourceId"] = owner.SourceId
                    }
                };
                if (target["source-layer"] is { } sourceLayer) label["source-layer"] = sourceLayer.DeepClone();
                if (!owner.Visible || target["layout"]?["visibility"]?.GetValue<string>() == "none")
                    label["layout"] = new JsonObject { ["visibility"] = "none" };
                InsertOwnedLabel(canonicalRenders, owner.Id, label.DeepClone());
                InsertOwnedLabel(renders, owner.Id, label);
                bindings.Add(new JsonObject
                {
                    ["styleLayerId"] = symbolId, ["targetLayerId"] = targetId, ["layerType"] = "symbol"
                });
            }
        }
        return bindings;
    }

    private static void InsertOwnedLabel(JsonArray renders, string ownerId, JsonNode label)
    {
        var insertAt = Enumerable.Range(0, renders.Count).Where(index => renders[index] is JsonObject render
            && (render["id"]?.GetValue<string>() == ownerId || render["id"]?.GetValue<string>() == $"{ownerId}__outline"
                || render["metadata"]?["honua:compositionLayerId"]?.GetValue<string>() == ownerId)).Last() + 1;
        renders.Insert(insertAt, label);
    }

    private static JsonObject FindRender(JsonArray layers, string id)
    {
        var matches = layers.OfType<JsonObject>().Where(layer => layer["id"]?.GetValue<string>() == id).ToArray();
        if (matches.Length != 1) Fail("A selected render layer is missing or ambiguous.");
        return matches[0];
    }

    private static StudioCompositionLayer FindOwner(StudioCompositionBody body, JsonObject render, string? styleId)
    {
        var id = RequiredText(render, "id");
        var ownerId = (render["metadata"] as JsonObject)?["honua:compositionLayerId"]?.GetValue<string>();
        var owners = body.Layers.Where(layer => ownerId is not null ? layer.Id == ownerId : layer.Id == id || $"{layer.Id}__outline" == id).ToArray();
        if (owners.Length != 1 || styleId is not null && owners[0].StyleRef != styleId
            || owners[0].SourceId != render["source"]?.GetValue<string>())
            Fail("A style binding must belong to its selected composition layer and source.");
        return owners[0];
    }

    private static string[] ReadProperties(JsonObject binding)
    {
        if (!binding.ContainsKey("properties")) return AppearanceProperties;
        if (binding["properties"] is not JsonArray array || array.Count is < 1 or > 7)
            throw new StudioCompositionBodyException("Invalid style binding properties.");
        var properties = array.Select(value => value is JsonValue item && item.TryGetValue<string>(out var text)
            ? text : throw new StudioCompositionBodyException("Invalid style property.")).ToArray();
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
            || properties.Any(property => property != "visibility" && !AppearanceProperties.Contains(property, StringComparer.Ordinal)))
            Fail("A style binding contains unsupported properties.");
        return properties;
    }

    private static async Task CheckFieldsAsync(HttpContext context, MetadataV2GraphSnapshot snapshot,
        StudioCompositionLayer owner, JsonObject appearance, CancellationToken cancellationToken)
    {
        var referencedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in new[] { "paint", "layout", "filter" }) CollectExpressionFields(appearance[property], referencedFields);
        CollectLegacyFilterFields(appearance["filter"], referencedFields);
        // MapLibre expands tokens in literal symbol label/icon strings. Tokens in
        // literal expressions stay literal and must not be treated as fields.
        if (appearance["layout"] is JsonObject layout)
        {
            foreach (var property in new[] { "text-field", "icon-image" })
                if (layout[property] is JsonValue value && value.TryGetValue<string>(out var template))
                    foreach (Match match in SymbolFieldTokens().Matches(template)) referencedFields.Add(match.Groups[1].Value);
        }
        // Static appearance needs the source's query permission, already checked
        // by initialization. Only field-dependent styling needs schema access.
        if (referencedFields.Count == 0) return;
        var (serviceId, index) = StudioDraftStyleInitializer.ParseSourceIdentity(owner.SourceId!);
        var service = snapshot.Graph.Services.FirstOrDefault(value => value.Metadata.Id == serviceId);
        var publication = snapshot.FindPublicationByLayerIndex(serviceId, index);
        var resource = publication is null ? null : snapshot.ResolveResource(publication);
        if (resource is null || service is null || publication is null || !snapshot.IsRoutable(publication)
            || !TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service)) throw new UnauthorizedAccessException();
        var access = await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service,
            AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false);
        if (!access.IsAllowed) throw new UnauthorizedAccessException();
        var fields = resource.VisibleSchemaFields().Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (referencedFields.Any(field => !fields.Contains(field)))
            Fail("A style references a field unavailable on its published source.");
    }

    private static void CollectExpressionFields(JsonNode? node, HashSet<string> fields)
    {
        if (node is JsonObject obj)
        {
            if (obj["property"] is JsonValue propertyNode && propertyNode.TryGetValue<string>(out var name))
                fields.Add(name);
            foreach (var property in obj) CollectExpressionFields(property.Value, fields);
        }
        if (node is not JsonArray array || array.Count == 0) return;
        var op = array[0] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (op == "literal") return;
        if (op is "get" or "has")
        {
            if (array.Count < 2 || array[1] is not JsonValue field || !field.TryGetValue<string>(out var name))
                Fail("A thematic expression must name an actual visible field.");
            else if (array.Count == 2 || array.Count == 3 && array[2] is JsonArray { Count: 1 } source
                && source[0] is JsonValue root && root.TryGetValue<string>(out var sourceOperation) && sourceOperation == "properties") fields.Add(name);
        }
        foreach (var child in array.Skip(1)) CollectExpressionFields(child, fields);
    }

    [GeneratedRegex(@"\{([^{}]+)\}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex SymbolFieldTokens();

    private static void CollectLegacyFilterFields(JsonNode? node, HashSet<string> fields)
    {
        if (node is not JsonArray array || array.Count < 2 || array[0] is not JsonValue operation
            || !operation.TryGetValue<string>(out var op)) return;
        if (op is "all" or "any" or "none")
        {
            foreach (var child in array.Skip(1)) CollectLegacyFilterFields(child, fields);
        }
        else if (op is "==" or "!=" or ">" or ">=" or "<" or "<=" or "in" or "!in" or "has" or "!has")
        {
            // Nested operands are expression filters, not legacy field/literal
            // filters. Their root-property reads were collected above.
            if (array.Skip(1).Any(value => value is JsonArray or JsonObject)
                || (op is "has" or "!has") && array.Count != 2) return;
            if (array[1] is JsonValue field && field.TryGetValue<string>(out var name)
                && name is not ("$type" or "$id"))
                fields.Add(name);
        }
    }

    private static void Merge(JsonObject target, JsonObject appearance, string[] properties, bool preserveHidden)
    {
        var hidden = preserveHidden && (target["layout"] as JsonObject)?["visibility"]?.GetValue<string>() == "none";
        foreach (var property in properties)
        {
            if (property == "visibility")
            {
                if ((appearance["layout"] as JsonObject)?["visibility"] is { } visibility)
                {
                    target["layout"] ??= new JsonObject();
                    target["layout"]!["visibility"] = visibility.DeepClone();
                }
                continue;
            }
            if (appearance[property] is not { } patch) continue;
            if (property is "paint" or "layout" or "metadata")
            {
                if (patch is not JsonObject members) throw new StudioCompositionBodyException("Style appearance members must be objects.");
                target[property] ??= new JsonObject();
                foreach (var member in members)
                {
                    if (property == "metadata" && member.Key is "honua:compositionLayerId" or "honua:compositionSourceId") continue;
                    target[property]![member.Key] = member.Value?.DeepClone();
                }
            }
            else target[property] = patch.DeepClone();
        }
        if (hidden) target["layout"]!["visibility"] = "none";
    }

    private static string RequiredText(JsonObject value, string key)
        => value[key] is JsonValue node && node.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= 256
            ? text : throw new StudioCompositionBodyException($"A style binding requires a bounded {key}.");

    [DoesNotReturn]
    private static void Fail(string message) => throw new StudioCompositionBodyException(message);
}
