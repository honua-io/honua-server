// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Postgres.Features.Geoprocessing;

/// <summary>
/// Shared GeoJSON page-fetch + feature-projection helper for the HTTP DAG source
/// connectors (<c>source.ogc-features</c>, <c>source.wfs</c>). Reuses the migration
/// <see cref="MigrationHttpContentReader"/> body-size ceiling so the bounded-buffer
/// SSRF-mitigation posture matches the import scanners. Parses a GeoJSON
/// FeatureCollection page and projects each member onto <see cref="DagSourceFeature"/>
/// (geometry re-serialised as a GeoJSON geometry string, properties flattened to a
/// scalar attribute map).
/// </summary>
internal static class GeoJsonPageReader
{
    /// <summary>
    /// Fetches a GeoJSON document from <paramref name="url"/> and returns the parsed
    /// page: the projected features, an optional <c>numberMatched</c> total, and the
    /// raw first-feature text used to detect a non-advancing server.
    /// </summary>
    public static async Task<GeoJsonPage> FetchPageAsync(
        HttpClient httpClient,
        Uri url,
        string? basicUsername,
        string? basicPassword,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyBasicAuth(request, basicUsername, basicPassword);

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await MigrationHttpContentReader
            .ReadStringWithLimitAsync(response, MigrationHttpContentReader.DefaultMaxResponseBytes, cancellationToken)
            .ConfigureAwait(false);

        return ParsePage(body);
    }

    internal static GeoJsonPage ParsePage(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var features = new List<DagSourceFeature>();
        string? firstFeatureRaw = null;
        string? nextLink = null;
        long? numberMatched = null;

        if (root.TryGetProperty("features", out var featureArray)
            && featureArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var feature in featureArray.EnumerateArray())
            {
                firstFeatureRaw ??= feature.GetRawText();
                features.Add(ProjectFeature(feature));
            }
        }

        if (root.TryGetProperty("numberMatched", out var matched)
            && matched.ValueKind == JsonValueKind.Number
            && matched.TryGetInt64(out var matchedValue))
        {
            numberMatched = matchedValue;
        }

        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            nextLink = links.EnumerateArray()
                .Select(link =>
                    link.TryGetProperty("rel", out var rel) &&
                    rel.ValueKind == JsonValueKind.String &&
                    string.Equals(rel.GetString(), "next", StringComparison.OrdinalIgnoreCase) &&
                    link.TryGetProperty("href", out var href) &&
                    href.ValueKind == JsonValueKind.String
                        ? href.GetString()
                        : null)
                .FirstOrDefault(href => href is not null);
        }

        return new GeoJsonPage(features, firstFeatureRaw, nextLink, numberMatched);
    }

    private static DagSourceFeature ProjectFeature(JsonElement feature)
    {
        string? geometryGeoJson = null;
        if (feature.TryGetProperty("geometry", out var geometry)
            && geometry.ValueKind == JsonValueKind.Object)
        {
            geometryGeoJson = geometry.GetRawText();
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (feature.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                attributes[property.Name] = ConvertScalar(property.Value);
            }
        }

        return new DagSourceFeature
        {
            GeometryGeoJson = geometryGeoJson,
            Attributes = attributes
        };
    }

    private static object? ConvertScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText()
    };

    private static void ApplyBasicAuth(HttpRequestMessage request, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }
}

/// <summary>A single parsed GeoJSON page from a paginated HTTP source.</summary>
internal sealed record GeoJsonPage(
    IReadOnlyList<DagSourceFeature> Features,
    string? FirstFeatureRaw,
    string? NextLink,
    long? NumberMatched);
