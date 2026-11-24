// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Protocols.Ogc.Common;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Ogc.Api.Features;

internal sealed partial class OgcFeaturesQueryHandler
{
    private readonly record struct CollectionCacheTarget(
        string OutputFormat,
        string? CacheKey,
        TimeSpan CacheTtl);

    internal readonly record struct OgcCollectionPayload(ReadOnlyMemory<byte> Bytes, int StableHashLength);

    internal readonly record struct RawPayloadContext(
        MetadataV2Object Resource,
        ImmutableArray<Link> Links,
        long? NumberMatched);

    private readonly record struct TaggedPayload(ReadOnlyMemory<byte> Bytes, string ETag);

    private Task<IResult> CreateRawCollectionResultAsync(
        HttpContext context,
        OgcCollectionPayload payload,
        CollectionCacheTarget target)
    {
        var contentETag = ComputeWeakETag(payload.Bytes.Span[..payload.StableHashLength], _etagService);
        return CreateCollectionBytesResultAsync(context, new TaggedPayload(payload.Bytes, contentETag), target);
    }

    private Task<IResult> CreateMaterializedCollectionResultAsync(
        HttpContext context,
        FeatureCollection response,
        CollectionCacheTarget target)
    {
        var payload = CreateMaterializedFeatureCollectionPayload(response);
        var contentETag = ComputeWeakETag(payload.Bytes.Span[..payload.StableHashLength], _etagService);
        return CreateCollectionBytesResultAsync(context, new TaggedPayload(payload.Bytes, contentETag), target);
    }

    private async Task<IResult> CreateCollectionBytesResultAsync(
        HttpContext context,
        TaggedPayload payload,
        CollectionCacheTarget target)
    {
        var contentType = string.Equals(target.OutputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.GeoJson
            : MediaTypes.Json;
        if (target.CacheKey is not null && ResponseCacheUtilities.ShouldCache(context, _cacheOptions))
        {
            var bytes = MemoryMarshal.TryGetArray(payload.Bytes, out var seg)
                && seg.Array is not null
                && seg.Offset == 0
                && seg.Count == seg.Array.Length
                ? seg.Array
                : payload.Bytes.ToArray();
            var cachedResponse = new CachedResponse(bytes, contentType, payload.ETag);
            await _responseCache.SetAsync(target.CacheKey, cachedResponse, target.CacheTtl, context.RequestAborted);
            return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
        }

        return Results.Bytes(payload.Bytes, contentType, entityTag: EntityTagHeaderValue.Parse(payload.ETag));
    }

    internal static string ComputeWeakETag(ReadOnlySpan<byte> stableBytes, IETagService etagService)
        => EnsureWeakETag(etagService.ComputeETag(stableBytes));

    internal static string ComputeStableFeatureCollectionETag(FeatureCollection collection, IETagService etagService)
    {
        var payload = CreateMaterializedFeatureCollectionPayload(collection);
        return ComputeWeakETag(payload.Bytes.Span[..payload.StableHashLength], etagService);
    }

    internal static OgcCollectionPayload CreateMaterializedFeatureCollectionPayload(FeatureCollection response)
    {
        var estimatedCapacity = Math.Max(4096, (response.Features.Length * 256) + 512);
        var buffer = new ArrayBufferWriter<byte>(estimatedCapacity);
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("type", response.Type);
        writer.WritePropertyName("features");
        JsonSerializer.Serialize(writer, response.Features, OgcJsonContext.Default.GeoJsonFeatureArray);

        if (response.NumberMatched.HasValue)
        {
            writer.WriteNumber("numberMatched", response.NumberMatched.Value);
        }

        writer.WriteNumber("numberReturned", response.NumberReturned);

        if (response.Links is { IsDefault: false } links)
        {
            writer.WritePropertyName("links");
            JsonSerializer.Serialize(writer, links, OgcJsonContext.Default.ImmutableArrayLink);
        }

        writer.Flush();
        var stableHashLength = buffer.WrittenCount;

        if (response.TimeStamp.HasValue)
        {
            writer.WriteString("timeStamp", OgcTimestampFormatter.Format(response.TimeStamp.Value));
        }

        writer.WriteEndObject();
        writer.Flush();

        return new OgcCollectionPayload(buffer.WrittenMemory, stableHashLength);
    }

    private static string EnsureWeakETag(string tag)
        => tag.StartsWith("W/", StringComparison.Ordinal) ? tag : "W/" + tag;

    internal static OgcCollectionPayload CreateRawFeatureCollectionPayload(
        ImmutableArray<RawGeoJsonFeature> features,
        RawPayloadContext context)
    {
        var propertyFields = ResolveRawPropertyFields(context.Resource);
        var encodedPropertyFields = EncodePropertyNames(propertyFields);
        var propertyLookup = propertyFields.Length > 0
            ? new Dictionary<string, JsonElement>(propertyFields.Length, StringComparer.OrdinalIgnoreCase)
            : null;
        var publicIdPropertyName = ResolveRawPublicIdPropertyName(context.Resource);
        var buffer = new ArrayBufferWriter<byte>(
            EstimateRawFeatureCollectionPayloadCapacity(features, propertyFields, context.Links));
        using var writer = new Utf8JsonWriter(buffer);

        WriteCollectionHeader(writer);

        foreach (var feature in features)
        {
            using var propertiesDocument = TryParseJsonObject(feature.PropertiesJson);
            var propertiesElement = propertiesDocument?.RootElement;

            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            writer.WritePropertyName("id");
            WriteRawFeatureId(writer, feature.Id, feature.PublicIdJson, propertiesElement, publicIdPropertyName);
            writer.WritePropertyName("geometry");
            WriteValidatedRawGeometry(writer, feature.GeometryGeoJson);
            writer.WritePropertyName("properties");
            WriteSchemaProperties(writer, propertiesElement, propertyFields, encodedPropertyFields, propertyLookup);
            writer.WriteEndObject();
        }

        return CompleteCollectionPayload(writer, buffer, new CollectionFooter(features.Length, context.Links, context.NumberMatched));
    }

    internal static OgcCollectionPayload CreateRawPointFeatureCollectionPayload(
        ImmutableArray<RawGeoServicesFeature> features,
        RawPayloadContext context)
    {
        var propertyFields = ResolveRawPropertyFields(context.Resource);
        var encodedPropertyFields = EncodePropertyNames(propertyFields);
        var propertyLookup = propertyFields.Length > 0
            ? new Dictionary<string, JsonElement>(propertyFields.Length, StringComparer.OrdinalIgnoreCase)
            : null;
        var publicIdPropertyName = ResolveRawPublicIdPropertyName(context.Resource);
        var buffer = new ArrayBufferWriter<byte>(
            EstimateRawPointFeatureCollectionPayloadCapacity(features, propertyFields, context.Links));
        using var writer = new Utf8JsonWriter(buffer);

        WriteCollectionHeader(writer);

        foreach (var feature in features)
        {
            using var propertiesDocument = TryParseJsonObject(feature.AttributesJson);
            var propertiesElement = propertiesDocument?.RootElement;

            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            writer.WritePropertyName("id");
            WriteRawFeatureId(writer, feature.Id, feature.PublicIdJson, propertiesElement, publicIdPropertyName);
            writer.WritePropertyName("geometry");
            WriteRawPointGeometry(writer, feature);
            writer.WritePropertyName("properties");
            WriteSchemaProperties(writer, propertiesElement, propertyFields, encodedPropertyFields, propertyLookup);
            writer.WriteEndObject();
        }

        return CompleteCollectionPayload(writer, buffer, new CollectionFooter(features.Length, context.Links, context.NumberMatched));
    }

    private static JsonEncodedText[] EncodePropertyNames(string[] propertyFields)
    {
        if (propertyFields.Length == 0)
        {
            return [];
        }

        var encoded = new JsonEncodedText[propertyFields.Length];
        for (var i = 0; i < propertyFields.Length; i++)
        {
            encoded[i] = JsonEncodedText.Encode(propertyFields[i]);
        }

        return encoded;
    }

    private readonly record struct CollectionFooter(
        int NumberReturned,
        ImmutableArray<Link> Links,
        long? NumberMatched);

    private static string[] ResolveRawPropertyFields(MetadataV2Object resource)
    {
        var objectIdName = resource.FindPrimaryIdField()?.Name ?? "objectid";
        return resource.SchemaFields
            .Where(field => !field.Hidden)
            .Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Where(field => !field.Name.Equals(objectIdName, StringComparison.OrdinalIgnoreCase))
            .Select(field => field.Name)
            .ToArray();
    }

    private static void WriteCollectionHeader(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray("features");
    }

    private static OgcCollectionPayload CompleteCollectionPayload(
        Utf8JsonWriter writer,
        ArrayBufferWriter<byte> buffer,
        CollectionFooter footer)
    {
        writer.WriteEndArray();

        if (footer.NumberMatched.HasValue)
        {
            writer.WriteNumber("numberMatched", footer.NumberMatched.Value);
        }

        writer.WriteNumber("numberReturned", footer.NumberReturned);
        writer.WritePropertyName("links");
        JsonSerializer.Serialize(writer, footer.Links, OgcJsonContext.Default.ImmutableArrayLink);
        writer.Flush();
        var stableHashLength = buffer.WrittenCount;
        writer.WriteString("timeStamp", OgcTimestampFormatter.Format(DateTimeOffset.UtcNow));
        writer.WriteEndObject();
        writer.Flush();

        return new OgcCollectionPayload(buffer.WrittenMemory, stableHashLength);
    }
}
