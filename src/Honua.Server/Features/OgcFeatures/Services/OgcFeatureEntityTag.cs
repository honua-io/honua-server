// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Caching;

namespace Honua.Server.Features.OgcFeatures.Services;

/// <summary>
/// Computes canonical entity tags for OGC API Features resources.
/// </summary>
internal static class OgcFeatureEntityTag
{
    private const string RepresentationTagPrefix = "of-";

    public static string Compute(Feature feature, IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(etagService);

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = feature.Id,
            ["geometry"] = feature.Geometry is { Length: > 0 }
                ? Convert.ToBase64String(feature.Geometry)
                : null,
            ["attributes"] = NormalizeForEtag(feature.Attributes)
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return etagService.ComputeETag(json);
    }

    public static string ComputeRepresentation(byte[] payload, string entityETag, IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(etagService);

        var entityToken = NormalizeTagToken(entityETag);
        var payloadToken = NormalizeTagToken(etagService.ComputeETag(payload));
        return $"\"{RepresentationTagPrefix}{entityToken}-{payloadToken}\"";
    }

    public static bool MatchesEntityOrRepresentation(
        string precondition,
        string entityETag,
        IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(etagService);

        if (etagService.MatchesPrecondition(precondition, entityETag))
        {
            return true;
        }

        var entityToken = NormalizeTagToken(entityETag);
        var representationPrefix = string.Concat(RepresentationTagPrefix, entityToken, "-");
        foreach (var rawTag in precondition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = NormalizeTagToken(rawTag);
            if (tag.StartsWith(representationPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeTagToken(string etag)
    {
        var token = etag.Trim();
        if (token.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            token = token[2..].Trim();
        }

        return token.Trim('"');
    }

    private static object? NormalizeForEtag(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, dictionaryValue) in readOnlyDictionary)
            {
                sorted[key] = NormalizeForEtag(dictionaryValue);
            }

            return sorted;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var entry in dictionary)
            {
                sorted[entry.Key] = NormalizeForEtag(entry.Value);
            }

            return sorted;
        }

        if (value is JsonElement element)
        {
            return NormalizeJsonElement(element);
        }

        if (value is IEnumerable enumerable && value is not string && value is not byte[])
        {
            var items = new List<object?>();
            foreach (var item in enumerable)
            {
                items.Add(NormalizeForEtag(item));
            }

            return items;
        }

        return value switch
        {
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value
        };
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToDictionary(
                    static property => property.Name,
                    static property => NormalizeJsonElement(property.Value),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(NormalizeJsonElement)
                .ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }
}
