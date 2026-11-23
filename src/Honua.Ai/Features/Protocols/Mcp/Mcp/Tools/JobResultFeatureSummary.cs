// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

// An MCP-only client cannot decode a base64 data URI, and stock clients cap tool-result size,
// so honua_get_job_results reports inline GeoJSON artifacts as counts, field names and one
// bounded page of attribute rows. The full package stays on honua://jobs/{jobId}/results.
internal static class JobResultFeatureSummary
{
    internal const string GeoJsonDataUriPrefix = "data:application/geo+json;base64,";
    internal const int InlineUriLimit = 2048;
    internal const int DefaultRowLimit = 10;
    internal const int MaxRowLimit = 50;
    internal const int MaxFields = 100;

    internal readonly record struct RowWindow(int Offset, int Limit);

    internal static void AttachSummary(McpArtifactRef artifact, RowWindow window)
    {
        if (artifact.Uri is { } uri && uri.StartsWith(GeoJsonDataUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            artifact.FeatureSummary = TrySummarize(uri.AsSpan(GeoJsonDataUriPrefix.Length), window);
        }
    }

    internal static void OmitLargeInlineUri(McpArtifactRef artifact)
    {
        if (artifact.Uri is { Length: > InlineUriLimit } uri && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            artifact.Uri = null;
            artifact.UriOmitted = true;
            artifact.UriLength = uri.Length;
        }
    }

    private static McpArtifactFeatureSummary? TrySummarize(ReadOnlySpan<char> base64, RowWindow window)
    {
        var buffer = new byte[(base64.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64Chars(base64, buffer, out var written))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, written));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var accumulator = new SummaryAccumulator(window);
            foreach (var feature in features.EnumerateArray())
            {
                accumulator.Add(feature);
            }

            return accumulator.Build();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class SummaryAccumulator(RowWindow window)
    {
        private const string JoinCountField = "JOIN_COUNT";

        private readonly List<string> _fields = [];
        private readonly HashSet<string> _seenFields = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _geometryTypes = new(StringComparer.Ordinal);
        private readonly List<JsonElement> _rows = [];
        private int _count;
        private int _matched;
        private bool _hasJoinCount;

        public void Add(JsonElement feature)
        {
            var properties = feature.ValueKind == JsonValueKind.Object
                && feature.TryGetProperty("properties", out var found)
                && found.ValueKind == JsonValueKind.Object
                    ? found
                    : default;
            if (properties.ValueKind == JsonValueKind.Object)
            {
                CountProperties(properties);
            }

            if (feature.ValueKind == JsonValueKind.Object
                && feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object
                && geometry.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                _geometryTypes.Add(type.GetString()!);
            }

            if (_count >= window.Offset && _rows.Count < window.Limit)
            {
                _rows.Add(properties.ValueKind == JsonValueKind.Object ? properties.Clone() : EmptyObject());
            }

            _count++;
        }

        public McpArtifactFeatureSummary Build()
        {
            var next = window.Offset + _rows.Count;
            return new McpArtifactFeatureSummary
            {
                FeatureCount = _count,
                Fields = _fields,
                GeometryTypes = [.. _geometryTypes],
                MatchedFeatureCount = _hasJoinCount ? _matched : null,
                RowOffset = window.Offset,
                Rows = _rows,
                NextRowOffset = window.Limit > 0 && next < _count ? next : null,
            };
        }

        private void CountProperties(JsonElement properties)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (_fields.Count < MaxFields && _seenFields.Add(property.Name))
                {
                    _fields.Add(property.Name);
                }
            }

            if (properties.TryGetProperty(JoinCountField, out var joinCount))
            {
                _hasJoinCount = true;
                _matched += joinCount.ValueKind == JsonValueKind.Number && joinCount.GetDouble() > 0 ? 1 : 0;
            }
        }

        private static JsonElement EmptyObject()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }
}
