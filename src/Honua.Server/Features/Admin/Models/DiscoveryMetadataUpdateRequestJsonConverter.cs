// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Server.Features.Admin.Models;

public sealed class DiscoveryMetadataUpdateRequestJsonConverter : JsonConverter<DiscoveryMetadataUpdateRequest>
{
    public override DiscoveryMetadataUpdateRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A discovery metadata object with an etag is required.");
        }

        var fields = new Dictionary<string, JsonElement>(options.PropertyNameCaseInsensitive
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }
        if (!fields.TryGetValue("etag", out var etag))
        {
            throw new JsonException("A discovery metadata etag is required.");
        }
        var context = new DiscoveryMetadataJsonContext(new JsonSerializerOptions(options));
        var request = new DiscoveryMetadataUpdateRequest
        {
            Etag = etag.Deserialize(context.String)!,
            Title = ReadValue(fields, "title", context.String),
            Description = ReadValue(fields, "description", context.String),
            Keywords = fields.TryGetValue("keywords", out var keywords) ? keywords.Deserialize(context.StringArray)! : []
        };
        return ReadOptional(fields, request, context);
    }

    private static T? ReadValue<T>(IReadOnlyDictionary<string, JsonElement> fields, string name, JsonTypeInfo<T> typeInfo) =>
        fields.TryGetValue(name, out var value) ? value.Deserialize(typeInfo) : default;

    private static DiscoveryMetadataUpdateRequest ReadOptional(IReadOnlyDictionary<string, JsonElement> fields, DiscoveryMetadataUpdateRequest request, DiscoveryMetadataJsonContext context)
    {
        if (fields.TryGetValue("themes", out var themes))
        {
            request = request with { Themes = themes.Deserialize(context.StringArray) };
        }
        if (fields.TryGetValue("language", out var language))
        {
            request = request with { Language = language.Deserialize(context.String) };
        }
        if (fields.TryGetValue("license", out var license))
        {
            request = request with { License = license.Deserialize(context.String) };
        }
        if (fields.TryGetValue("attribution", out var attribution))
        {
            request = request with { Attribution = attribution.Deserialize(context.String) };
        }
        if (fields.TryGetValue("publisher", out var publisher))
        {
            request = request with { Publisher = publisher.Deserialize(context.String) };
        }
        if (fields.TryGetValue("contactPoint", out var contactPoint))
        {
            request = request with { ContactPoint = contactPoint.Deserialize(context.MetadataV2ContactPoint) };
        }
        if (fields.TryGetValue("links", out var links))
        {
            request = request with { Links = links.Deserialize(context.MetadataV2LinkArray) };
        }
        return request;
    }

    public override void Write(Utf8JsonWriter writer, DiscoveryMetadataUpdateRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("etag", value.Etag);
        writer.WriteString("title", value.Title);
        writer.WriteString("description", value.Description);
        writer.WritePropertyName("keywords");
        JsonSerializer.Serialize(writer, value.Keywords, DiscoveryMetadataJsonContext.Default.StringArray);
        WriteOptional(writer, value);
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, DiscoveryMetadataUpdateRequest value)
    {
        var context = DiscoveryMetadataJsonContext.Default;
        if (value.HasThemes)
        {
            writer.WritePropertyName("themes");
            JsonSerializer.Serialize(writer, value.Themes, context.StringArray);
        }
        if (value.HasLanguage)
        {
            writer.WriteString("language", value.Language);
        }
        if (value.HasLicense)
        {
            writer.WriteString("license", value.License);
        }
        if (value.HasAttribution)
        {
            writer.WriteString("attribution", value.Attribution);
        }
        if (value.HasPublisher)
        {
            writer.WriteString("publisher", value.Publisher);
        }
        if (value.HasContactPoint)
        {
            writer.WritePropertyName("contactPoint");
            JsonSerializer.Serialize(writer, value.ContactPoint, context.MetadataV2ContactPoint);
        }
        if (value.HasLinks)
        {
            writer.WritePropertyName("links");
            JsonSerializer.Serialize(writer, value.Links, context.MetadataV2LinkArray);
        }
    }
}
