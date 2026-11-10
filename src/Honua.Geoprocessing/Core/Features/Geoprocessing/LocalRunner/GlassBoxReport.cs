// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing.LocalRunner;

public sealed record ArtifactPreview
{
    public required string MediaType { get; init; }

    public required long SizeBytes { get; init; }

    public required string Summary { get; init; }

    public static ArtifactPreview FromArtifact(string artifact, int maxFeatures = 3, int maxTextChars = 240)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var (mediaType, bytes) = Decode(artifact);
        var summary = BuildSummary(mediaType, bytes, maxFeatures, maxTextChars);

        return new ArtifactPreview
        {
            MediaType = mediaType,
            SizeBytes = bytes.LongLength,
            Summary = summary,
        };
    }

    private static (string MediaType, byte[] Bytes) Decode(string artifact)
    {
        if (artifact.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = artifact.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                var header = artifact.AsSpan(5, comma - 5); // between "data:" and ","
                var semicolon = header.IndexOf(';');
                var mediaType = (semicolon >= 0 ? header[..semicolon] : header).ToString();
                var payload = artifact[(comma + 1)..];
                var isBase64 = header.Contains("base64", StringComparison.Ordinal);
                var bytes = isBase64
                    ? Convert.FromBase64String(payload)
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                return (string.IsNullOrEmpty(mediaType) ? "application/octet-stream" : mediaType, bytes);
            }
        }

        // Non-data-URI reference (e.g. an object/file path): treat the text itself as the payload.
        return ("text/plain", Encoding.UTF8.GetBytes(artifact));
    }

    private static string BuildSummary(string mediaType, byte[] bytes, int maxFeatures, int maxTextChars)
    {
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var geoJson = TryBuildGeoJsonSummary(bytes, maxFeatures);
            if (geoJson is not null)
            {
                return geoJson;
            }
        }

        return BuildTextHead(bytes, maxTextChars);
    }

    private static string? TryBuildGeoJsonSummary(byte[] bytes, int maxFeatures)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
            {
                return null;
            }

            var type = typeProp.GetString();
            var features = new List<JsonElement>();
            if (string.Equals(type, "FeatureCollection", StringComparison.Ordinal)
                && root.TryGetProperty("features", out var featureArray)
                && featureArray.ValueKind == JsonValueKind.Array)
            {
                features.AddRange(featureArray.EnumerateArray());
            }
            else if (string.Equals(type, "Feature", StringComparison.Ordinal))
            {
                features.Add(root);
            }
            else
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.Append(CultureInfo.InvariantCulture, $"GeoJSON {type}: {features.Count} feature(s)");

            var shown = Math.Min(maxFeatures, features.Count);
            for (var i = 0; i < shown; i++)
            {
                var feature = features[i];
                var geomType = feature.TryGetProperty("geometry", out var geom)
                    && geom.ValueKind == JsonValueKind.Object
                    && geom.TryGetProperty("type", out var gt)
                        ? gt.GetString()
                        : "null";

                builder.Append(CultureInfo.InvariantCulture, $"\n  [{i}] {geomType}");
                if (feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    var pairs = props.EnumerateObject().Take(3)
                        .Select(p => $"{p.Name}={Stringify(p.Value)}");
                    var joined = string.Join(", ", pairs);
                    if (joined.Length > 0)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $" {{{joined}}}");
                    }
                }
            }

            if (features.Count > shown)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\n  … {features.Count - shown} more");
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText(),
    };

    private static string BuildTextHead(byte[] bytes, int maxTextChars)
    {
        // Detect a mostly-printable payload; otherwise fall back to a hex head.
        var sampleLength = Math.Min(bytes.Length, 512);
        var printable = 0;
        for (var i = 0; i < sampleLength; i++)
        {
            var b = bytes[i];
            if (b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b < 0x7F))
            {
                printable++;
            }
        }

        var isText = sampleLength == 0 || (double)printable / sampleLength >= 0.85;
        if (isText)
        {
            var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, maxTextChars * 2));
            if (text.Length > maxTextChars)
            {
                text = text[..maxTextChars] + "…";
            }

            return text.Length == 0 ? "(empty)" : text;
        }

        var hexCount = Math.Min(bytes.Length, 32);
        var hex = Convert.ToHexString(bytes, 0, hexCount);
        return $"(binary) {hex}{(bytes.Length > hexCount ? "…" : string.Empty)}";
    }
}

public sealed record GlassBoxReport
{
    public IReadOnlyList<GlassBoxCommand> Commands { get; init; } = [];

    public IReadOnlyList<LocalRunPhase> Timeline { get; init; } = [];

    public IReadOnlyList<ArtifactPreview> ArtifactPreviews { get; init; } = [];

    public IReadOnlyList<string> ScratchDirectories { get; init; } = [];
}
