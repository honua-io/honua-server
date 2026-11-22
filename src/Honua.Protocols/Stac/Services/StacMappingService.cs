// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Geometries;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Stac;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Stac.Models;
using NetTopologySuite.IO;

using Honua.Core.Abstractions.Features.Rasters.Abstractions;

namespace Honua.Protocols.Stac.Services;

internal sealed class StacMappingService
{
    [ThreadStatic]
    private static GeoJsonWriter? _geoJsonWriter;

    // RFC 7946 requires the geometry member to be present, so a missing geometry is stored as a cloned JSON null rather than omitted.
    private static readonly JsonElement JsonNullElement = JsonElement.Parse("null");
    private static readonly ImmutableArray<string> DataAssetRoles = ImmutableArray.Create("data");
    private static readonly ImmutableArray<string> CogAssetRoles = ImmutableArray.Create("data", "cloud-optimized");

    internal readonly record struct StacItemMappingOptions
    {
        internal required MetadataV2Object Resource { get; init; }
        internal required MetadataV2Layer Publication { get; init; }
        internal required int LayerIndex { get; init; }
        internal required string BaseUrl { get; init; }
        internal IReadOnlySet<string>? SelectedProperties { get; init; }
        internal int? GeometrySrid { get; init; }
        internal CogArtifactReference? CogArtifact { get; init; }
    }

    internal sealed class StacItemMappingContext
    {
        internal StacItemMappingContext(StacItemMappingOptions options)
        {
            Resource = options.Resource;
            CollectionId = options.LayerIndex.ToString(CultureInfo.InvariantCulture);
            IdFieldName = options.Resource.FindPrimaryIdField()?.Name ?? "objectid";
            StartTimeField = StacFilterHelpers.ResolveTemporalField(options.Resource, out var endField);
            EndTimeField = endField;
            DeclaredExtensions = options.Resource.ResolveDeclaredExtensions();
            GeometrySrid = options.GeometrySrid ?? options.Resource.ReadSrid() ?? 4326;
            SelectedPropertiesLookup = options.SelectedProperties is null
                ? null
                : options.SelectedProperties as HashSet<string> is { Comparer: { } comparer } existing && comparer.Equals(StringComparer.OrdinalIgnoreCase)
                    ? existing
                    : new HashSet<string>(options.SelectedProperties, StringComparer.OrdinalIgnoreCase);

            var stacBase = $"{options.BaseUrl}/stac";
            var collectionHref = $"{stacBase}/collections/{CollectionId}";
            ItemHrefPrefix = $"{collectionHref}/items/";
            OgcItemHrefPrefix = $"{options.BaseUrl}/ogc/features/collections/{CollectionId}/items/";
            var title = ResolveDisplayName(options.Publication, options.Resource, options.LayerIndex);
            CollectionLink = Link.Create(href: collectionHref, rel: RelationTypes.Collection, type: MediaTypes.Json, title: title);
            ParentLink = Link.Create(href: collectionHref, rel: StacConstants.StacRelations.Parent, type: MediaTypes.Json, title: title);
            RootLink = Link.Create(href: stacBase, rel: StacConstants.StacRelations.Root, type: MediaTypes.Json, title: "STAC Catalog");
            CogAsset = options.CogArtifact is null
                ? null
                : new StacAsset
                {
                    Href = options.CogArtifact.Href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? options.CogArtifact.Href
                        : $"{options.BaseUrl}{options.CogArtifact.Href}",
                    Title = "Cloud Optimized GeoTIFF",
                    Type = options.CogArtifact.ContentType,
                    Roles = CogAssetRoles,
                };
        }

        internal MetadataV2Object Resource { get; }
        internal string CollectionId { get; }
        internal string IdFieldName { get; }
        internal string? StartTimeField { get; }
        internal string? EndTimeField { get; }
        internal ImmutableArray<string>? DeclaredExtensions { get; }
        internal int GeometrySrid { get; }
        internal HashSet<string>? SelectedPropertiesLookup { get; }
        internal string ItemHrefPrefix { get; }
        internal string OgcItemHrefPrefix { get; }
        internal Link CollectionLink { get; }
        internal Link ParentLink { get; }
        internal Link RootLink { get; }
        internal StacAsset? CogAsset { get; }
    }

    private static GeoJsonWriter GetGeoJsonWriter() => _geoJsonWriter ??= new GeoJsonWriter();
    internal sealed record CollectionMappingContext(
        IFeatureReader FeatureReader, string BaseUrl, ICoordinateTransformService? CoordinateTransformService);

    public static async Task<StacCollection> MapResourceToCollectionAsync(
        StacV2Lookups.ResolvedStacPublication resolved,
        CollectionMappingContext context,
        CancellationToken cancellationToken)
    {
        var resource = resolved.Resource;
        var publication = resolved.Publication;
        var layerIndex = resolved.LayerIndex;
        var baseUrl = context.BaseUrl;
        var descriptive = DescriptiveMetadataResolver.ForDataset(
            resolved.Snapshot.Graph, new(resource, resolved.Service, publication));
        var collectionId = layerIndex.ToString(CultureInfo.InvariantCulture);
        var stacBase = $"{baseUrl}/stac";
        var displayName = ResolveDisplayName(publication, resource, layerIndex);
        var governance = resource.WithStacServiceGovernanceFallbacks(
            DescriptiveMetadataResolver.GovernanceFor(resolved.Snapshot.Graph, resource, resolved.Service));

        var links = ImmutableArray.CreateBuilder<Link>();

        links.Add(Link.Create(
            href: $"{stacBase}/collections/{collectionId}",
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: displayName));

        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Root,
            type: MediaTypes.Json,
            title: "STAC Catalog"));

        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Parent,
            type: MediaTypes.Json,
            title: "STAC Catalog"));

        links.Add(Link.Create(
            href: $"{stacBase}/collections/{collectionId}/items",
            rel: StacConstants.StacRelations.Items,
            type: MediaTypes.GeoJson,
            title: "Items"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Alternate,
            type: MediaTypes.Json,
            title: "OGC API Features collection"));

        // Queryables — the Filter Extension requires each collection to advertise its
        // per-collection queryables document via the OGC queryables rel. The href must
        // match the queryables document's $id exactly.
        links.Add(Link.Create(
            href: $"{stacBase}/collections/{collectionId}/queryables",
            rel: RelationTypes.Queryables,
            type: MediaTypes.SchemaJson,
            title: "Queryables"));

        foreach (var governanceLink in governance.Links.Where(link =>
                     string.Equals(link.Rel, RelationTypes.License, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(link.Rel, RelationTypes.DescribedBy, StringComparison.OrdinalIgnoreCase)))
        {
            links.Add(new Link
            {
                Href = governanceLink.Href,
                Rel = governanceLink.Rel,
                Type = governanceLink.Type,
                Title = governanceLink.Title,
                HrefLang = governanceLink.Hreflang
            });
        }

        var extent = await BuildStacExtentV2Async(
            resource,
            resolved.StorageLayerId,
            context.FeatureReader,
            context.CoordinateTransformService,
            cancellationToken).ConfigureAwait(false);

        var collection = new StacCollection
        {
            Id = collectionId,
            Title = displayName,
            Description = descriptive.Description ?? $"STAC collection for {displayName}",
            License = resource.ResolveLicense(governance),
            Providers = BuildProviders(descriptive),
            Extent = extent,
            Keywords = descriptive.Keywords.Count == 0 ? null : [.. descriptive.Keywords],
            Language = DescriptiveMembers.Language(descriptive.Language),
            Themes = DescriptiveMembers.Themes(descriptive.Themes),
            Links = links.ToImmutable(),
        };
        return collection with { StacExtensions = DeclareEmittedExtensions(resource.ResolveDeclaredExtensions(), collection) };
    }

    private static ImmutableArray<string>? DeclareEmittedExtensions(ImmutableArray<string>? declared, StacCollection collection)
    {
        var extensions = declared ?? [];
        if (collection.Language is not null && !extensions.Contains(StacConstants.Extensions.Language))
        {
            extensions = extensions.Add(StacConstants.Extensions.Language);
        }
        if (collection.Themes is not null && !extensions.Contains(StacConstants.Extensions.Themes))
        {
            extensions = extensions.Add(StacConstants.Extensions.Themes);
        }
        return extensions.IsEmpty ? null : extensions;
    }

    private static ImmutableArray<StacProvider>? BuildProviders(DescriptiveMetadata metadata)
    {
        var providers = new List<StacProvider>();
        if (metadata.Publisher is { } publisher)
        {
            providers.Add(new StacProvider
            {
                Name = publisher,
                Roles = ImmutableArray.Create(StacConstants.ProviderRoles.Producer),
                Description = metadata.Attribution,
            });
        }
        foreach (var contact in metadata.Contacts)
        {
            var name = !string.IsNullOrWhiteSpace(contact.Name) ? contact.Name.Trim()
                : !string.IsNullOrWhiteSpace(contact.Organization) ? contact.Organization.Trim()
                : ResolveContactProviderName(contact.Url, contact.Email);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var role = contact.Role switch
            {
                MetadataV2DescriptiveCodes.PublisherRole => StacConstants.ProviderRoles.Producer,
                "processor" => StacConstants.ProviderRoles.Processor,
                _ => null,
            };
            var existing = providers.FindIndex(provider => string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var provider = providers[existing];
                providers[existing] = provider with
                {
                    Roles = role is null ? provider.Roles
                        : (provider.Roles ?? []).Append(role).Distinct(StringComparer.Ordinal).ToImmutableArray(),
                    Url = provider.Url ?? contact.Url,
                };
            }
            else
            {
                providers.Add(new StacProvider
                {
                    Name = name,
                    Roles = role is null ? null : ImmutableArray.Create(role),
                    Url = contact.Url,
                });
            }
        }
        if (metadata.Attribution is { } attribution && !providers.Any(provider =>
                string.Equals(provider.Name, attribution, StringComparison.OrdinalIgnoreCase)))
        {
            providers.Add(new StacProvider { Name = attribution });
        }
        return providers.Count == 0 ? null : providers.ToImmutableArray();
    }

    private static string? ResolveContactProviderName(string? contactUrl, string? contactEmail)
    {
        if (contactUrl is not null &&
            Uri.TryCreate(contactUrl, UriKind.Absolute, out var parsedContactUrl) &&
            (string.Equals(parsedContactUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(parsedContactUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrEmpty(parsedContactUrl.UserInfo))
        {
            return parsedContactUrl.Host;
        }

        if (contactEmail is null)
        {
            return null;
        }

        var separator = contactEmail.LastIndexOf('@');
        return separator >= 0 && separator < contactEmail.Length - 1
            ? contactEmail[(separator + 1)..]
            : contactEmail;
    }

    internal static string ResolveDisplayName(
        MetadataV2Layer publication,
        MetadataV2Object resource,
        int layerIndex)
    {
        return publication.TitleOverride
            ?? publication.Metadata.Title
            ?? (string.IsNullOrEmpty(publication.Metadata.Name) ? null : publication.Metadata.Name)
            ?? resource.Metadata.Title
            ?? (string.IsNullOrEmpty(resource.Metadata.Name) ? null : resource.Metadata.Name)
            ?? layerIndex.ToString(CultureInfo.InvariantCulture);
    }

    public static StacItem MapFeatureToItem(
        Feature feature,
        MetadataV2Object resource,
        MetadataV2Layer publication,
        int layerIndex,
        string baseUrl,
        IReadOnlySet<string>? selectedProperties = null,
        int? geometrySrid = null,
        CogArtifactReference? cogArtifact = null)
        => MapFeatureToItem(
            feature,
            new StacItemMappingContext(new StacItemMappingOptions
            {
                Resource = resource,
                Publication = publication,
                LayerIndex = layerIndex,
                BaseUrl = baseUrl,
                SelectedProperties = selectedProperties,
                GeometrySrid = geometrySrid,
                CogArtifact = cogArtifact
            }));

    internal static StacItem MapFeatureToItem(Feature feature, StacItemMappingContext context)
    {
        var itemId = ResolveItemId(feature, context.Resource);
        var escapedItemId = Uri.EscapeDataString(itemId);
        var ogcItemId = ResolveOgcPublicId(feature, context.IdFieldName);
        var escapedOgcItemId = string.Equals(ogcItemId, itemId, StringComparison.Ordinal)
            ? escapedItemId
            : Uri.EscapeDataString(ogcItemId);

        IReadOnlyDictionary<string, object?> attributes = feature.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        var properties = new Dictionary<string, object?>(attributes.Count + 3);
        PopulateTemporalPropertiesV2(attributes, context, properties);
        CopyIncludedAttributes(attributes, context.SelectedPropertiesLookup, properties);

        var (geometry, bbox) = MapGeometryAndBbox(feature.Geometry, context.GeometrySrid);
        var links = ImmutableArray.Create(
            Link.Create(
                href: string.Concat(context.ItemHrefPrefix, escapedItemId),
                rel: RelationTypes.Self,
                type: MediaTypes.GeoJson,
                title: string.Concat("Item ", itemId)),
            context.CollectionLink,
            context.ParentLink,
            context.RootLink);

        return new StacItem
        {
            Id = itemId,
            Geometry = geometry,
            Bbox = bbox,
            Properties = properties,
            Links = links,
            Assets = BuildItemAssets(context, escapedOgcItemId),
            Collection = context.CollectionId,
            StacExtensions = context.DeclaredExtensions,
        };
    }

    private static Dictionary<string, StacAsset> BuildItemAssets(StacItemMappingContext context, string escapedOgcItemId)
    {
        var assets = new Dictionary<string, StacAsset>(context.CogAsset is null ? 1 : 2)
        {
            ["geojson"] = new StacAsset
            {
                Href = string.Concat(context.OgcItemHrefPrefix, escapedOgcItemId),
                Title = "GeoJSON",
                Type = MediaTypes.GeoJson,
                Roles = DataAssetRoles,
            }
        };

        if (context.CogAsset is { } cogAsset)
        {
            assets["cog"] = cogAsset;
        }

        return assets;
    }

    private static void CopyIncludedAttributes(
        IReadOnlyDictionary<string, object?> attributes,
        HashSet<string>? selectedPropertiesLookup,
        Dictionary<string, object?> properties)
    {
        foreach (var kvp in attributes)
        {
            var key = kvp.Key;
            if ((IsItemIdAttribute(key) && selectedPropertiesLookup?.Contains(key) != true) ||
                string.Equals(key, "objectid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "datetime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "start_datetime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "end_datetime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "honua:datetime_source", StringComparison.OrdinalIgnoreCase) ||
                FeatureAttributeVisibility.IsInternalAttribute(key) ||
                (selectedPropertiesLookup is not null && !selectedPropertiesLookup.Contains(key)) ||
                (kvp.Value is null && selectedPropertiesLookup is null))
            {
                continue;
            }

            properties[key] = kvp.Value;
        }
    }

    private static (JsonElement? Geometry, ImmutableArray<double>? Bbox) MapGeometryAndBbox(byte[]? wkb, int srid)
    {
        if (wkb is not { Length: > 0 })
        {
            return (JsonNullElement, null);
        }

        try
        {
            var parsed = WkbReaderCache.Get().Read(wkb);
            var geometry = ConvertGeometryToGeoJsonElement(parsed) ?? JsonNullElement;
            var bbox = TryBuildBboxFromGeometry(parsed, srid);
            return (geometry, bbox);
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            return (JsonNullElement, null);
        }
    }

    private static string ResolveOgcPublicId(Feature feature, string idFieldName)
    {
        if (feature.Attributes is not null &&
            feature.Attributes.TryGetValue(idFieldName, out var configured) &&
            configured is not null)
        {
            var asString = Convert.ToString(configured, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(asString))
            {
                return asString;
            }
        }
        return feature.ObjectId?.ToString(CultureInfo.InvariantCulture)
            ?? feature.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static void PopulateTemporalPropertiesV2(
        IReadOnlyDictionary<string, object?> attributes,
        StacItemMappingContext context,
        Dictionary<string, object?> properties)
    {
        var startField = context.StartTimeField;
        var endField = context.EndTimeField;
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (!string.IsNullOrWhiteSpace(startField))
        {
            start = TryReadTemporalValue(attributes, startField);
        }
        if (!string.IsNullOrWhiteSpace(endField))
        {
            end = TryReadTemporalValue(attributes, endField);
        }

        if (start is not null && (end is null || end == start))
        {
            properties["datetime"] = FormatTemporalValue(start.Value);
            return;
        }

        if (start is not null || end is not null)
        {
            // Per STAC 1.0.0 common-metadata: when datetime is null, BOTH start_datetime and
            // end_datetime are required non-null strings.  Use the known bound for both when only
            // one is available (open-ended intervals are not valid in item-level metadata).
            var effectiveStart = start ?? end!.Value;
            var effectiveEnd = end ?? start!.Value;
            properties["datetime"] = null;
            properties["start_datetime"] = FormatTemporalValue(effectiveStart);
            properties["end_datetime"] = FormatTemporalValue(effectiveEnd);
            return;
        }

        // Preserve the legacy readable-item contract and STAC 1.0's required datetime.
        // Explicitly identify this display fallback: it is not an acquisition timestamp,
        // and cannot make an undated collection match a datetime search.
        properties["datetime"] = FormatTemporalValue(ResolveFallbackInstant(resource));
        properties["honua:datetime_source"] = resource.Temporal?.Extent is { Start: not null } or { End: not null }
            ? "collection_extent"
            : "unknown";
    }

    private static DateTimeOffset ResolveFallbackInstant(MetadataV2Object resource)
    {
        var extent = resource.Temporal?.Extent;
        return extent?.Start
            ?? extent?.End
            ?? DateTimeOffset.UnixEpoch;
    }

    internal static string ResolveItemId(Feature feature, MetadataV2Object? resource = null)
    {
        var source = StacItemIdentity.Resolve(resource);
        if (source.IsDeclaredIdentity && source.Field is { } declared &&
            TryReadItemIdValue(feature, declared.Name, out var declaredId))
        {
            return declaredId;
        }

        if (!source.IsDeclaredIdentity)
        {
            foreach (var key in StacItemIdentity.CanonicalNames)
            {
                // A schema column that failed the uniqueness check is not an item id,
                // even when every row stores the same attribute value.
                if (resource is not null && StacItemIdentity.FindSchemaField(resource, key) is not null)
                {
                    continue;
                }

                if (TryReadItemIdValue(feature, key, out var attributeId))
                {
                    return attributeId;
                }
            }
        }

        return feature.ObjectId?.ToString(CultureInfo.InvariantCulture) ?? "0";
    }

    private static bool TryReadItemIdValue(Feature feature, string name, out string itemId)
    {
        itemId = string.Empty;
        if (!TryGetAttribute(feature.Attributes, name, out var value) || value is null)
        {
            return false;
        }

        var resolved = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return false;
        }

        itemId = resolved;
        return true;
    }

    private static bool TryGetAttribute(
        ImmutableDictionary<string, object?>? attributes,
        string name,
        out object? value)
    {
        if (attributes is not null)
        {
            if (attributes.TryGetValue(name, out value))
            {
                return true;
            }

            foreach (var attribute in (attributes).Where(attribute => string.Equals(attribute.Key, name, StringComparison.OrdinalIgnoreCase)))
            {
                value = attribute.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsItemIdAttribute(string attributeName)
        => attributeName.Equals("id", StringComparison.OrdinalIgnoreCase) ||
           attributeName.Equals("stac_id", StringComparison.OrdinalIgnoreCase) ||
           attributeName.Equals("item_id", StringComparison.OrdinalIgnoreCase);

    private static async Task<StacExtent> BuildStacExtentV2Async(
        MetadataV2Object resource,
        int? storageLayerId,
        IFeatureReader featureReader,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        var bbox = ImmutableArray.Create(ImmutableArray.Create(-180.0, -90.0, 180.0, 90.0));

        var resourceBbox = resource.ReadBbox();
        var srid = resource.ReadSrid() ?? 4326;
        if (resourceBbox is { } extent &&
            await OgcExtentTransformer.TryTransformExtentToCrs84Async(
                extent.West,
                extent.South,
                extent.East,
                extent.North,
                srid,
                coordinateTransformService,
                cancellationToken).ConfigureAwait(false) is { } transformedExtent)
        {
            bbox = ImmutableArray.Create(ImmutableArray.Create(
                transformedExtent.MinLon,
                transformedExtent.MinLat,
                transformedExtent.MaxLon,
                transformedExtent.MaxLat));
        }

        var temporalInterval = ImmutableArray.Create(ImmutableArray.Create<string?>(null, null));
        var temporalStartField = StacFilterHelpers.ResolveTemporalField(resource, out var temporalEndField);
        var temporalResource = temporalStartField is null ? resource : resource with
        {
            Temporal = (resource.Temporal ?? new MetadataV2ObjectTemporal()) with
            {
                StartTimeField = temporalStartField,
                EndTimeField = temporalEndField
            }
        };
        var temporalExtent = storageLayerId is int storageLayer
            ? await OgcQueryablesUtilities.BuildTemporalExtentAsync(
                temporalResource, storageLayer, featureReader, cancellationToken).ConfigureAwait(false)
            : null;
        if (temporalExtent is not null)
        {
            temporalInterval = temporalExtent.Interval;
        }

        return new StacExtent
        {
            Spatial = new StacSpatialExtent { Bbox = bbox },
            Temporal = new StacTemporalExtent { Interval = temporalInterval }
        };
    }

    private static JsonElement? ConvertGeometryToGeoJsonElement(NetTopologySuite.Geometries.Geometry geom)
    {
        try
        {
            var json = RingWindingNormalizer.WriteGeoJson(GetGeoJsonWriter(), geom);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Best-effort GeoJSON conversion — STAC allows a null geometry, so any
            // serialization failure here degrades to that explicit-null representation
            // rather than failing the whole item mapping.
            return null;
        }
    }

    private static DateTimeOffset? TryReadTemporalValue(
        IReadOnlyDictionary<string, object?> attributes,
        string fieldName)
    {
        if (!attributes.TryGetValue(fieldName, out var value))
        {
            value = attributes.FirstOrDefault(pair => string.Equals(pair.Key, fieldName, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => dateTime.Kind switch
            {
                DateTimeKind.Utc => new DateTimeOffset(dateTime, TimeSpan.Zero),
                DateTimeKind.Local => new DateTimeOffset(dateTime.ToUniversalTime(), TimeSpan.Zero),
                _ => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero)
            },
            DateOnly dateOnly => new DateTimeOffset(
                dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                TimeSpan.Zero),
            string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedDateTimeOffset) => parsedDateTimeOffset,
            string text when DateOnly.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDateOnly) => new DateTimeOffset(
                    parsedDateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    TimeSpan.Zero),
            JsonElement element when element.ValueKind == JsonValueKind.String => TryReadTemporalValue(
                new Dictionary<string, object?> { [fieldName] = element.GetString() },
                fieldName),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var milliseconds) =>
                DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            long milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            double milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(
                Convert.ToInt64(milliseconds, CultureInfo.InvariantCulture)),
            _ => null
        };
    }

    private static string FormatTemporalValue(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static ImmutableArray<double>? TryBuildBboxFromGeometry(NetTopologySuite.Geometries.Geometry geom, int srid)
    {
        try
        {
            var envelope = geom.EnvelopeInternal;

            if (srid == 4326)
            {
                return ImmutableArray.Create(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
            }

            if (OgcExtentTransformer.TryTransformToCrs84(envelope.MinX, envelope.MinY, srid, out var min) &&
                OgcExtentTransformer.TryTransformToCrs84(envelope.MaxX, envelope.MaxY, srid, out var max))
            {
                return ImmutableArray.Create(min.Lon, min.Lat, max.Lon, max.Lat);
            }

            return null;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Best-effort bbox computation — an unexpected geometry/CRS failure degrades to
            // an absent bbox (allowed by STAC) rather than failing the whole item mapping.
            return null;
        }
    }
}
