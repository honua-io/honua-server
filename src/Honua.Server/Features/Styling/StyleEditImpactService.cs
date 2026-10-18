// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Styling;

internal sealed record ResolvedStyleEdit(StyleEditImpact Impact, StyleEditStoreSnapshot StoreSnapshot,
    MetadataV2Resource? PrimaryResource, string CanonicalStyleJson, bool CompleteReadableScope = true);

/// <summary>One canonical store snapshot and one metadata snapshot, filtered by the actual request principal.</summary>
internal sealed class StyleEditImpactService(IStyleCatalog catalog, IMetadataV2GraphProvider graphs,
    IHttpContextAccessor accessor) : IStyleEditImpactService
{
    public async Task<StyleEditImpact?> GetAsync(string styleId, CancellationToken cancellationToken = default)
    {
        try { return (await ResolveAsync(styleId, cancellationToken).ConfigureAwait(false))?.Impact; }
        catch (GeoprocessingAuthorizationException) { return null; }
    }

    internal async Task<ResolvedStyleEdit?> ResolveAsync(string styleId, CancellationToken cancellationToken)
    {
        var context = accessor.HttpContext;
        if (context is null || !await OperationAdminAuthorization.IsAuthorizedAsync(context, context.User,
                OperationSideEffectClass.ReadOnly, cancellationToken).ConfigureAwait(false))
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        await SharedStyleAuthority.RequireCurrentKeyAsync(context, cancellationToken).ConfigureAwait(false);
        var graph = await graphs.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await catalog.GetEditSnapshotAsync(styleId, null, cancellationToken).ConfigureAwait(false);
        int? layerId = null;
        MetadataV2Resource? primary = null;
        var parsedLayer = 0;
        var synthetic = styleId.StartsWith("style-layer-", StringComparison.Ordinal) &&
            int.TryParse(styleId.AsSpan("style-layer-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out parsedLayer) &&
            styleId == "style-layer-" + parsedLayer.ToString(CultureInfo.InvariantCulture);
        if (snapshot.CatalogStyle is null || synthetic)
        {
            var named = graph.Graph.Resources.FirstOrDefault(resource => resource.Metadata.Name == styleId);
            primary = synthetic && (named is null || !graph.ResolveStorageLayerId(named).HasValue)
                ? graph.Graph.Resources.FirstOrDefault(resource => graph.ResolveStorageLayerId(resource) == parsedLayer)
                : named;
            layerId = primary is null ? null : graph.ResolveStorageLayerId(primary);
            if (layerId.HasValue)
            {
                var mirrorId = "style-layer-" + layerId.Value.ToString(CultureInfo.InvariantCulture);
                var aliases = graph.Graph.Resources.GroupBy(resource => resource.Metadata.Name, StringComparer.Ordinal)
                    .Where(group => graph.ResolveStorageLayerId(group.First()) == layerId)
                    .Select(group => group.Key).Append(styleId).ToList();
                var mirrorNamed = graph.Graph.Resources.FirstOrDefault(resource => resource.Metadata.Name == mirrorId);
                if (mirrorNamed is null || !graph.ResolveStorageLayerId(mirrorNamed).HasValue || graph.ResolveStorageLayerId(mirrorNamed) == layerId)
                    aliases.Add(mirrorId);
                snapshot = await catalog.GetEditSnapshotForReferencesAsync(mirrorId, layerId, aliases, cancellationToken).ConfigureAwait(false);
            }
        }
        // Style metadata itself and a layer-owned primary remain protected even
        // when no associations have been materialized yet.
        foreach (var styleResource in graph.Graph.Resources.Where(resource =>
            resource.Type == MetadataV2ResourceType.Style &&
            (resource.Metadata.Name == styleId || resource.Metadata.Name == snapshot.StyleId)))
        {
            if (!TenantScopeHelpers.IsTenantVisible(context, styleResource, null) ||
                !(await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, styleResource, null,
                    AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false)).IsAllowed) return null;
        }
        if (primary is not null && (!TenantScopeHelpers.IsTenantVisible(context, primary, null) ||
            !(await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, primary, null,
                AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false)).IsAllowed)) return null;
        if (layerId.HasValue && snapshot.LayerStyle is null || !layerId.HasValue && snapshot.CatalogStyle is null) return null;
        var raw = layerId.HasValue ? snapshot.LayerStyle!.MapLibreStyleJson : snapshot.CatalogStyle!.MapLibreStyleJson;
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (primary is null || !OgcStyleProjection.PublishesLayerDefault(primary)) return null;
            raw = StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(StyleLayerDescriptor.FromResource(primary, layerId!.Value)));
        }
        var version = layerId.HasValue ? snapshot.LayerStyle!.StyleVersion : snapshot.CatalogStyle!.StyleVersion;
        var contentHash = StyleEditHashes.Content(raw);
        var affected = snapshot.Associations.Select(item => item.LayerId).Concat(layerId.HasValue ? [layerId.Value] : [])
            .Distinct().Order().ToArray();
        // Style configuration is an admin metadata mutation, not a feature-data edit:
        // read-only datasets still support styling when the complete scope is visible.
        var canEdit = await OperationAdminAuthorization.IsAuthorizedAsync(context, context.User,
            OperationSideEffectClass.MutatesMetadata, cancellationToken).ConfigureAwait(false);
        var completeReadableScope = snapshot.ConsumerIndexComplete;
        canEdit &= snapshot.ConsumerIndexComplete;
        List<StyleEditAssociation> visible = [];
        List<string> geometryPins = [];
        foreach (var affectedLayer in affected)
        {
            var resources = graph.Graph.Resources.Where(resource => graph.ResolveStorageLayerId(resource) == affectedLayer).ToArray();
            if (resources.Length == 0) { canEdit = false; completeReadableScope = false; }
            foreach (var resource in resources)
            {
                var publications = graph.Graph.Publications.Where(publication =>
                    graph.ResolveResource(publication)?.Metadata.Id == resource.Metadata.Id && graph.IsRoutable(publication)).ToArray();
                IEnumerable<(MetadataV2Publication? Publication, MetadataV2Service? Service)> bindings = publications.Length == 0
                    ? [(null, null)]
                    : publications.Select(publication => ((MetadataV2Publication?)publication,
                        graph.Index.ServicesById.GetValueOrDefault(publication.ServiceId)));
                foreach (var binding in bindings)
                {
                    var service = binding.Service;
                    if (binding.Publication is { } publication && !TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service) ||
                        !TenantScopeHelpers.IsTenantVisible(context, resource, service) ||
                        !(await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, service,
                            AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false)).IsAllowed)
                    {
                        canEdit = false;
                        completeReadableScope = false;
                        continue;
                    }
                    visible.Add(new(resource.Metadata.Id, resource.Metadata.Name, resource.Metadata.Title, service?.Metadata.Id));
                    geometryPins.Add(StyleEditHashes.Parts([resource.Metadata.Id, affectedLayer.ToString(CultureInfo.InvariantCulture),
                        resource.ReadGeometryType().ToString(), binding.Publication?.Metadata.Id ?? "", service?.Metadata.Id ?? ""]));
                }
            }
        }
        List<StyleEditConsumer> consumers = [];
        var studio = context.RequestServices.GetService<IStudioAuthorizationService>();
        foreach (var consumer in snapshot.Consumers)
        {
            var decision = studio is null ? null : await studio.AuthorizeAsync(context.User,
                studio.ResolveCallerId(context.User), consumer.DraftId.HasValue ? StudioAuthorizationOperation.ReadDraft : StudioAuthorizationOperation.ReadContentItem,
                consumer.OwnerActorId, consumer.TenantId, false,
                (consumer.DraftId ?? consumer.ItemId).ToString("D"), cancellationToken).ConfigureAwait(false);
            if (decision is not { IsAllowed: true })
            {
                canEdit = false;
                completeReadableScope = false;
                continue;
            }
            consumers.Add(new(consumer.Kind, consumer.ItemId, consumer.PackageKey, consumer.VersionId, consumer.DraftId,
                consumer.Generation, consumer.ContentHash, consumer.DraftId.HasValue ? "draft" : consumer.IsPublished ? "published" : "saved", consumer.IsCurrent));
        }
        var associations = visible.Distinct().OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.ServiceId, StringComparer.Ordinal).ToArray();
        // Public hashes contain only visible references; hidden scope is never exposed.
        // Mutation also requires CanEdit, and compares the complete private store snapshot.
        var publicPins = new List<string> { styleId, version.ToString(CultureInfo.InvariantCulture), contentHash, primary?.Metadata.Id ?? "", primary?.Metadata.Name ?? "" };
        if (snapshot.CatalogStyle is { } mirror)
        {
            publicPins.Add(mirror.StyleVersion.ToString(CultureInfo.InvariantCulture));
            publicPins.Add(StyleEditHashes.Content(mirror.MapLibreStyleJson));
            publicPins.Add(mirror.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        }
        publicPins.AddRange(geometryPins.Distinct().Order(StringComparer.Ordinal));
        if (completeReadableScope) publicPins.AddRange(snapshot.ReferenceIds);
        foreach (var consumer in consumers)
            publicPins.Add(StyleEditHashes.Parts([consumer.Kind, consumer.ItemId.ToString("D"), consumer.PackageKey,
                consumer.VersionId?.ToString("D") ?? "", consumer.DraftId?.ToString("D") ?? "",
                consumer.Generation?.ToString(CultureInfo.InvariantCulture) ?? "", consumer.ContentHash,
                consumer.PublicationState, consumer.IsCurrent.ToString()]));
        foreach (var association in associations)
        {
            publicPins.Add(association.ResourceId);
            publicPins.Add(association.Name);
            publicPins.Add(association.Title ?? "");
            publicPins.Add(association.ServiceId ?? "");
        }
        var impact = new StyleEditImpact(styleId, version, StyleVersionTag.Format(version), contentHash,
            canEdit, StyleEditHashes.Parts([.. publicPins]), associations)
        { CanonicalLayerId = layerId, AssociatedLayerIds = canEdit ? affected : [], Consumers = consumers, ImpactComplete = completeReadableScope };
        return new(impact, snapshot, primary, raw, completeReadableScope);
    }
}

internal static class StyleEditHashes
{
    public static string Content(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SavedMapOperationIdempotency.CanonicalPayload(document.RootElement))));
    }
    public static string Parts(string[] parts) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(parts, StyleEditImpactJsonContext.Default.StringArray)));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(StyleEditImpact))]
internal sealed partial class StyleEditImpactJsonContext : JsonSerializerContext;
