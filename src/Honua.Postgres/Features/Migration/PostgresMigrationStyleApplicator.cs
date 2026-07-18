// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Applies converted migration styles through the canonical style catalog abstractions.
/// </summary>
internal sealed class PostgresMigrationStyleApplicator(
    IStyleCatalog styleCatalog,
    ILayerStyleCatalog layerStyleCatalog,
    IMetadataV2StyleGraphSync? styleGraphSync = null) : IMigrationStyleApplicator
{
    /// <inheritdoc />
    public async Task<MigrationStyleApplyOutcome> ApplyAsync(
        MigrationLiveStyleApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ReviewDisposition, "applied", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.MapLibreLayersJson))
        {
            return MigrationStyleApplyOutcome.SkippedManualReview;
        }

        var targets = request.LayerTargets
            .DistinctBy(static target => target.LayerId)
            .OrderBy(static target => target.Ordinal)
            .ThenBy(static target => target.LayerId)
            .ToArray();
        if (targets.Length == 0)
        {
            return MigrationStyleApplyOutcome.SkippedNoPublishedLayers;
        }

        var canonicalJson = BuildMapLibreStyleDocument(
            request.Title,
            request.MapLibreLayersJson,
            targets.Select(static target => target.LayerId).ToArray());
        var currentStyle = await styleCatalog.GetStyleAsync(request.TargetStyleId, cancellationToken).ConfigureAwait(false);
        if (currentStyle != null &&
            !JsonEquals(currentStyle.MapLibreStyleJson, canonicalJson) &&
            !IsMigrationOwned(currentStyle.RevisedBy))
        {
            return MigrationStyleApplyOutcome.SkippedConflict;
        }

        var currentAssociations = await styleCatalog.ListAssociationsAsync(cancellationToken).ConfigureAwait(false);
        var requestedAssociations = targets
            .Select(target => new StyleLayerAssociation(target.LayerId, request.TargetStyleId, target.Ordinal))
            .ToArray();
        if (currentAssociations.Any(existing =>
                string.Equals(existing.StyleId, request.TargetStyleId, StringComparison.Ordinal) &&
                !requestedAssociations.Contains(existing)))
        {
            // IStyleCatalog currently has no remove-association operation. Refuse to
            // claim a reordered/removed binding was reconciled when stale refs remain.
            return MigrationStyleApplyOutcome.SkippedConflict;
        }

        var defaultTargets = targets.Where(static target => target.Ordinal == 0).ToArray();
        var currentLayerStyles = new Dictionary<int, Honua.Core.Features.Styling.Domain.LayerStyleDefinition?>();
        foreach (var target in defaultTargets)
        {
            var layerJson = BuildMapLibreStyleDocument(request.Title, request.MapLibreLayersJson, [target.LayerId]);
            var current = await layerStyleCatalog.GetLayerStyleAsync(target.LayerId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(current?.MapLibreStyleJson) &&
                !JsonEquals(current.MapLibreStyleJson, layerJson) &&
                !IsMigrationOwned(current.StyleRevisedBy))
            {
                return MigrationStyleApplyOutcome.SkippedConflict;
            }

            currentLayerStyles[target.LayerId] = current;
        }

        var changed = await EnsureCanonicalStyleAsync(request, canonicalJson, currentStyle, cancellationToken).ConfigureAwait(false);

        // Apply associations before the render-facing layer value. A failed
        // association leaves a resumable canonical record, but never exposes a
        // partially replaced default layer style.
        foreach (var target in targets)
        {
            var association = new StyleLayerAssociation(target.LayerId, request.TargetStyleId, target.Ordinal);
            var associationAlreadyPresent = currentAssociations.Contains(association);
            if (!await styleCatalog.AssociateLayerAsync(
                    target.LayerId,
                    request.TargetStyleId,
                    target.Ordinal,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Could not associate migrated style {request.TargetStyleId} with published target layer {target.LayerId}.");
            }

            changed |= !associationAlreadyPresent;
        }

        // Only GeoServer's default style owns the layer's render-facing value.
        // Alternative styles remain independent-catalog associations by ordinal.
        foreach (var target in defaultTargets)
        {
            var layerJson = BuildMapLibreStyleDocument(
                request.Title,
                request.MapLibreLayersJson,
                [target.LayerId]);
            var current = currentLayerStyles[target.LayerId];
            if (!JsonEquals(current?.MapLibreStyleJson, layerJson))
            {
                var updated = await layerStyleCatalog.SetMapLibreStyleAsync(
                        target.LayerId,
                        layerJson,
                        revisedBy: "geoserver-migration",
                        changeSummary: $"Applied migrated style {request.TargetStyleId}.",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (updated == null)
                {
                    throw new InvalidOperationException($"Published target layer {target.LayerId} was not found while applying its migrated style.");
                }

                changed = true;
            }
        }

        if (styleGraphSync != null)
        {
            foreach (var layerId in targets.Select(static target => target.LayerId).Distinct())
            {
                await styleGraphSync.SyncLayerStylesAsync(layerId, cancellationToken).ConfigureAwait(false);
            }
        }

        return changed ? MigrationStyleApplyOutcome.Applied : MigrationStyleApplyOutcome.AlreadyApplied;
    }

    private async Task<bool> EnsureCanonicalStyleAsync(
        MigrationLiveStyleApplyRequest request,
        string canonicalJson,
        Honua.Core.Features.Styling.Domain.StyleCatalogRecord? current,
        CancellationToken cancellationToken)
    {
        if (current == null)
        {
            var created = await styleCatalog.CreateStyleAsync(
                    request.TargetStyleId,
                    canonicalJson,
                    request.Title,
                    description: "Imported from GeoServer SLD/SE.",
                    revisedBy: "geoserver-migration",
                    changeSummary: "Created by GeoServer migration.",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (created != null)
            {
                return true;
            }

            current = await styleCatalog.GetStyleAsync(request.TargetStyleId, cancellationToken).ConfigureAwait(false);
        }

        if (JsonEquals(current?.MapLibreStyleJson, canonicalJson))
        {
            return false;
        }

        var updated = await styleCatalog.UpsertStyleAsync(
                request.TargetStyleId,
                canonicalJson,
                request.Title,
                description: "Imported from GeoServer SLD/SE.",
                revisedBy: "geoserver-migration",
                changeSummary: "Updated by GeoServer migration.",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (updated == null)
        {
            throw new InvalidOperationException($"Canonical style upsert returned no record for {request.TargetStyleId}.");
        }

        return true;
    }

    private static bool IsMigrationOwned(string? revisedBy) =>
        string.Equals(revisedBy, "geoserver-migration", StringComparison.Ordinal);

    private static string BuildMapLibreStyleDocument(
        string title,
        string mapLibreLayersJson,
        int[] layerIds)
    {
        using var layersDocument = JsonDocument.Parse(mapLibreLayersJson);
        if (layersDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Converted SLD style must contain a JSON array of MapLibre layers.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 8);
            writer.WriteString("name", title);
            writer.WritePropertyName("sources");
            writer.WriteStartObject();
            foreach (var layerId in layerIds)
            {
                writer.WritePropertyName($"layer-{layerId}");
                writer.WriteStartObject();
                writer.WriteString("type", "vector");
                writer.WritePropertyName("tiles");
                writer.WriteStartArray();
                writer.WriteStringValue($"/tiles/{layerId}/{{z}}/{{x}}/{{y}}.mvt");
                writer.WriteEndArray();
                writer.WriteNumber("minzoom", 0);
                writer.WriteNumber("maxzoom", 22);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WritePropertyName("layers");
            writer.WriteStartArray();
            foreach (var layerId in layerIds)
            {
                foreach (var convertedLayer in layersDocument.RootElement.EnumerateArray())
                {
                    WriteLayer(writer, convertedLayer, layerId, layerIds.Length > 1);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteLayer(Utf8JsonWriter writer, JsonElement convertedLayer, int layerId, bool qualifyId)
    {
        if (convertedLayer.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Each converted SLD style layer must be a JSON object.");
        }

        writer.WriteStartObject();
        var hasId = false;
        foreach (var property in convertedLayer.EnumerateObject())
        {
            if (property.NameEquals("source") || property.NameEquals("source-layer"))
            {
                continue;
            }

            if (property.NameEquals("id"))
            {
                hasId = true;
                writer.WriteString("id", qualifyId ? $"{property.Value.GetString()}-layer-{layerId}" : property.Value.GetString());
                continue;
            }

            property.WriteTo(writer);
        }

        if (!hasId)
        {
            writer.WriteString("id", $"migrated-style-layer-{layerId}");
        }

        writer.WriteString("source", $"layer-{layerId}");
        writer.WriteString("source-layer", "layer");
        writer.WriteEndObject();
    }

    private static bool JsonEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
