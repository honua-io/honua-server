// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing.LocalRunner;

/// <summary>
/// A lightweight, dev-only preview of a single published artifact for the GP Devkit
/// glass box (issue #2128): the artifact's media type, decoded byte size, and a short
/// human-readable head — the first N GeoJSON features for a managed geometry op, or the
/// leading bytes/text for a GDAL op — so the author can eyeball the output without
/// opening it in a desktop GIS.
/// </summary>
public sealed record ArtifactPreview
{
    /// <summary>The artifact's media type (e.g. <c>application/geo+json</c>, <c>text/csv</c>).</summary>
    public required string MediaType { get; init; }

    /// <summary>The decoded artifact size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>A short, human-readable summary of the artifact's head content.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Builds a preview for a canonical <c>data:&lt;media&gt;;base64,&lt;payload&gt;</c>
    /// artifact reference. For a GeoJSON artifact the summary lists the first
    /// <paramref name="maxFeatures"/> features (geometry type + a couple of properties);
    /// otherwise it shows the leading printable text or a byte/hex head.
    /// </summary>
    /// <param name="artifact">The artifact reference the executor published.</param>
    /// <param name="maxFeatures">Maximum number of GeoJSON features to enumerate.</param>
    /// <param name="maxTextChars">Maximum number of characters for the text/byte head.</param>
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

/// <summary>
/// The fully-transparent dev-mode "glass box" view of a single local run (issue #2128).
/// It is only populated when the caller opts a run into glass-box mode; otherwise the
/// run stays on the production-equivalent sanitized path and this is <c>null</c>. It
/// surfaces the UNSANITIZED native command(s) with their real scratch paths and FULL
/// stdout/stderr, the phase timeline, and an artifact preview, plus a "repro locally"
/// hint a developer can paste to re-run the exact command against the intermediate files.
/// </summary>
public sealed record GlassBoxReport
{
    /// <summary>
    /// The fully unsanitized native GDAL invocations captured during the run, in order.
    /// Empty for a purely managed op (which shells out to no native tool).
    /// </summary>
    public IReadOnlyList<GlassBoxCommand> Commands { get; init; } = [];

    /// <summary>The phase/timeline the executor reported, with per-phase elapsed timing.</summary>
    public IReadOnlyList<LocalRunPhase> Timeline { get; init; } = [];

    /// <summary>Previews of the published artifacts, in order.</summary>
    public IReadOnlyList<ArtifactPreview> ArtifactPreviews { get; init; } = [];

    /// <summary>
    /// The real (unredacted) scratch working directories the native tools ran in — where a
    /// developer can find the intermediate files. Empty for a purely managed op.
    /// </summary>
    public IReadOnlyList<string> ScratchDirectories { get; init; } = [];
}
