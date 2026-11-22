// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Protocols.Ogc.Common;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Ogc.Api.Features;

internal sealed partial class OgcFeaturesQueryHandler
{
    private sealed class CollectionJsonResponse
    {
        public required ReadOnlyMemory<byte> Payload { get; init; }
        public required string OutputFormat { get; init; }
        public required string ContentETag { get; init; }
        public string? CacheKey { get; init; }
        public TimeSpan CacheTtl { get; init; }
    }

    internal readonly record struct OgcCollectionPayload(ReadOnlyMemory<byte> Bytes, int StableHashLength);

    internal readonly record struct RawPayloadContext(
        MetadataV2Object Resource,
        ImmutableArray<Link> Links,
        long? NumberMatched);

    private async Task<IResult> CreateCollectionJsonResultAsync(
        HttpContext context,
        CollectionJsonResponse response,
        CancellationToken cancellationToken)
    {
        var contentType = string.Equals(response.OutputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.GeoJson
            : MediaTypes.Json;
        if (response.CacheKey is not null && ResponseCacheUtilities.ShouldCache(context, _cacheOptions))
        {
            var cachedResponse = new CachedResponse(response.Payload.ToArray(), contentType, response.ContentETag);
            await _responseCache.SetAsync(response.CacheKey, cachedResponse, response.CacheTtl, cancellationToken);
            return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
        }

        return Results.Bytes(response.Payload, contentType, entityTag: EntityTagHeaderValue.Parse(response.ContentETag));
    }

    internal static string ComputeWeakETag(ReadOnlySpan<byte> stableBytes, IETagService etagService)
        => EnsureWeakETag(etagService.ComputeETag(stableBytes));

    internal static string ComputeStableFeatureCollectionETag(FeatureCollection collection, IETagService etagService)
    {
        var stable = collection.TimeStamp.HasValue ? collection with { TimeStamp = null } : collection;
        return EnsureWeakETag(etagService.ComputeETag(stable, OgcJsonContext.Default.FeatureCollection));
    }

    private static string EnsureWeakETag(string tag)
        => tag.StartsWith("W/", StringComparison.Ordinal) ? tag : "W/" + tag;

    internal static OgcCollectionPayload CreateRawFeatureCollectionPayload(
        ImmutableArray<RawGeoJsonFeature> features,
        RawPayloadContext context)
    {
        var propertyFields = ResolveRawPropertyFields(context.Resource);
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
            WriteSchemaProperties(writer, propertiesElement, propertyFields);
            writer.WriteEndObject();
        }

        return CompleteCollectionPayload(writer, buffer, new CollectionFooter(features.Length, context.Links, context.NumberMatched));
    }

    internal static OgcCollectionPayload CreateRawPointFeatureCollectionPayload(
        ImmutableArray<RawGeoServicesFeature> features,
        RawPayloadContext context)
    {
        var propertyFields = ResolveRawPropertyFields(context.Resource);
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
            WriteSchemaProperties(writer, propertiesElement, propertyFields);
            writer.WriteEndObject();
        }

        return CompleteCollectionPayload(writer, buffer, new CollectionFooter(features.Length, context.Links, context.NumberMatched));
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
