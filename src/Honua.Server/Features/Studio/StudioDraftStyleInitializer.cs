// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Studio;

/// <summary>
/// Resolves published service/layer identities into a credential-free style. A
/// browser-supplied URL or metadata.honua.source never establishes source authority.
/// </summary>
internal static class StudioDraftStyleInitializer
{
    internal static async Task<StudioCompositionBody> InitializeAsync(
        HttpContext context, StudioCompositionBody body, CancellationToken cancellationToken)
    {
        if (body.Layers.Count > 256)
            throw new StudioCompositionBodyException("A visual style supports at most 256 composed layers.");

        var snapshot = await context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var sources = new JsonObject();
        var bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var previousBindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var binding in body.SourceBindings ?? [])
        {
            if (binding.ValueKind != JsonValueKind.Object
                || !binding.TryGetProperty("sourceId", out var id) || id.ValueKind != JsonValueKind.String
                || !previousBindings.TryAdd(id.GetString()!, binding))
                throw new StudioCompositionBodyException("Portable source bindings must have distinct source identifiers.");
        }
        var layers = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "honua-basemap:background",
                ["type"] = "background",
                ["paint"] = new JsonObject { ["background-color"] = "#edf2f5" }
            }
        };
        var renderIds = new HashSet<string>(StringComparer.Ordinal) { "honua-basemap:background" };
        // Persist deployment-relative paths. Console routes them through its
        // authenticated data adapter; internal upstream origins never enter the draft.
        var baseUrl = context.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
        foreach (var layer in body.Layers)
        {
            var sourceId = layer.SourceId
                ?? throw new StudioCompositionBodyException("Select a published source for every composed layer before styling.");
            var (serviceId, layerIndex) = ParseSourceIdentity(sourceId);
            var service = snapshot.Graph.Services.FirstOrDefault(service => service.Metadata.Id == serviceId);
            var publication = service is null ? null : snapshot.FindPublicationByLayerIndex(serviceId, layerIndex);
            var resource = publication is null ? null : snapshot.ResolveResource(publication);
            if (service is null || publication is null || resource is null || !snapshot.IsRoutable(publication)
                || !TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service))
                throw new UnauthorizedAccessException();

            var access = await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service,
                AuthorizationOperation.Query, cancellationToken).ConfigureAwait(false);
            if (!access.IsAllowed) throw new UnauthorizedAccessException();

            var type = resource.ReadGeometryType() switch
            {
                MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => "circle",
                MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => "line",
                MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => "fill",
                _ => throw new StudioCompositionBodyException("A map source has no supported vector geometry.")
            };
            var servicePath = string.Join('/', service.Metadata.Name.Split('/').Select(Uri.EscapeDataString));
            var layerUrl = $"{baseUrl}/rest/services/{servicePath}/FeatureServer/{layerIndex.ToString(CultureInfo.InvariantCulture)}";
            var binding = new JsonObject
            {
                ["sourceId"] = sourceId,
                ["protocol"] = "geoservices_feature_service",
                ["locator"] = new JsonObject { ["url"] = layerUrl, ["layerId"] = layerIndex }
            };
            var filter = "1=1";
            if (previousBindings.TryGetValue(sourceId, out var previousBinding))
            {
                foreach (var property in previousBinding.EnumerateObject())
                {
                    if (property.Name is "sourceId" or "protocol" or "locator") continue;
                    if (property.Name is not ("filter" or "attribution" or "metadata"))
                        throw new StudioCompositionBodyException("A portable binding contains unsupported options; resolve them before editing this map.");
                    binding[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
                if (previousBinding.TryGetProperty("filter", out var storedFilter) && storedFilter.ValueKind != JsonValueKind.Null)
                {
                    if (storedFilter.ValueKind != JsonValueKind.String)
                        throw new StudioCompositionBodyException("A portable source filter must be a string.");
                    if (!string.IsNullOrWhiteSpace(storedFilter.GetString())) filter = storedFilter.GetString()!;
                }
            }
            using var bindingDocument = JsonDocument.Parse(binding.ToJsonString());
            bindings[sourceId] = bindingDocument.RootElement.Clone();
            sources[sourceId] = new JsonObject
            {
                ["type"] = "geojson",
                ["data"] = $"{layerUrl}/query?where={Uri.EscapeDataString(filter)}&outFields=*&f=geojson&resultRecordCount=2000"
            };
            AddRenderLayer(layers, renderIds, layer, layer.Id, type);
            if (type == "fill") AddRenderLayer(layers, renderIds, layer, $"{layer.Id}__outline", "line");
        }

        if (body.MapSpec is { ValueKind: JsonValueKind.Object } existing)
        {
            // Older whole-draft writers accepted arbitrary mapSpec objects. Its
            // presence alone cannot establish the source anchor for visual edits.
            if (!existing.TryGetProperty("sources", out var storedSources)
                || !JsonNode.DeepEquals(JsonNode.Parse(storedSources.GetRawText()), sources))
                throw new StudioCompositionBodyException(
                    "The map's stored sources differ from its authorized published bindings. Rebind the map sources before editing this style.");
            _ = StudioCompositionBodyEditor.SetMapStyle(body, existing);
            return body with
            {
                SourceBindings = bindings.Values.ToArray(),
                SourceBindingIds = bindings.Keys.ToHashSet(StringComparer.Ordinal)
            };
        }
        if (body.MapSpec is not null)
            throw new StudioCompositionBodyException("The draft's stored map document must be repaired before style initialization.");

        var style = new JsonObject { ["version"] = 8, ["sources"] = sources, ["layers"] = layers };
        using var document = JsonDocument.Parse(style.ToJsonString());
        return body with
        {
            MapSpec = document.RootElement.Clone(),
            SourceBindings = bindings.Values.ToArray(),
            SourceBindingIds = bindings.Keys.ToHashSet(StringComparer.Ordinal)
        };
    }

    internal static (string ServiceId, int LayerIndex) ParseSourceIdentity(string sourceId)
    {
        var separator = sourceId.LastIndexOf('/');
        if (separator <= 0 || !int.TryParse(sourceId.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var layerIndex))
            throw new StudioCompositionBodyException("Select a published service and layer before initializing its style.");
        var serviceId = Uri.UnescapeDataString(sourceId[..separator]);
        // Match encodeURIComponent output, allowing its unescaped punctuation.
        var canonicalServiceId = Uri.EscapeDataString(serviceId)
            .Replace("%21", "!", StringComparison.Ordinal).Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal).Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);
        if (!string.Equals(sourceId, $"{canonicalServiceId}/{layerIndex.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal))
            throw new StudioCompositionBodyException("The map source must use its canonical published identity.");
        return (serviceId, layerIndex);
    }

    private static void AddRenderLayer(JsonArray layers, HashSet<string> ids,
        StudioCompositionLayer layer, string id, string type)
    {
        if (!ids.Add(id))
            throw new StudioCompositionBodyException("Composition layer identifiers conflict with generated style layers.");
        var paint = type switch
        {
            "circle" => new JsonObject { ["circle-color"] = "#2d69a5", ["circle-radius"] = 4 },
            "line" => new JsonObject { ["line-color"] = "#2d69a5", ["line-width"] = id == layer.Id ? 2 : 1 },
            _ => new JsonObject { ["fill-color"] = "#2d69a5", ["fill-opacity"] = 0.4 }
        };
        layers.Add(new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["source"] = layer.SourceId,
            ["paint"] = paint,
            ["layout"] = new JsonObject { ["visibility"] = layer.Visible ? "visible" : "none" },
            ["metadata"] = new JsonObject
            {
                ["honua:compositionLayerId"] = layer.Id,
                ["honua:compositionSourceId"] = layer.SourceId
            }
        });
    }
}
