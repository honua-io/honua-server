// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.InteropServices;
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
    internal const int ResponsePreviewBytes = 8192;
    private const int OmittedOffsetBytes = 11;
    internal const int MaxEncodedPreviewLength = 24 * 1024 * 1024;

    internal readonly record struct RowWindow(int Offset, int Limit);

    internal readonly record struct PreviewRequest(RowWindow Window, int Bytes, int EncodedLengthLimit);

    internal static void AttachSummary(
        McpArtifactRef artifact, PreviewRequest request, CancellationToken cancellationToken)
    {
        if (artifact.Uri is { } uri && uri.StartsWith(GeoJsonDataUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            artifact.FeatureSummary = TrySummarize(
                uri.AsSpan(GeoJsonDataUriPrefix.Length), request, cancellationToken);
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

    private static McpArtifactFeatureSummary? TrySummarize(
        ReadOnlySpan<char> base64, PreviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (base64.Length > request.EncodedLengthLimit || request.Bytes / 2 < OmittedOffsetBytes)
        {
            return null;
        }

        var buffer = new byte[(base64.Length / 4 * 3) + 3];
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

            var kind = IsSpatialJoin(document.RootElement) ? ResultKind.SpatialJoin : ResultKind.General;
            var accumulator = new SummaryAccumulator(request.Window, request.Bytes, kind);
            foreach (var feature in features.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                accumulator.Add(feature, cancellationToken);
            }

            return accumulator.Build();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSpatialJoin(JsonElement root) =>
        root.TryGetProperty("processId", out var processId)
        && processId.ValueKind == JsonValueKind.String
        && (processId.ValueEquals("analytics.spatial-join"u8)
            || processId.ValueEquals("analytics.spatial-join-managed"u8));

    private enum ResultKind
    {
        General,
        SpatialJoin
    }

    private sealed class SummaryAccumulator(RowWindow window, int previewBytes, ResultKind kind)
    {
        private const string JoinCountField = "JOIN_COUNT";
        private static readonly JsonElement EmptyRow = CreateEmptyRow();

        private readonly List<string> _fields = [];
        private readonly HashSet<string> _seenFields = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _geometryTypes = new(StringComparer.Ordinal);
        private readonly List<JsonElement> _rows = [];
        private readonly List<int> _omittedRowOffsets = [];
        private int _remainingRowBytes = previewBytes / 2;
        private int _remainingMetadataBytes = previewBytes - (previewBytes / 2);
        private int _pageEnd = window.Offset;
        private bool _pageFull;
        private int _count;
        private int _matched;
        private bool _hasJoinCount;

        public void Add(JsonElement feature, CancellationToken cancellationToken)
        {
            var properties = feature.ValueKind == JsonValueKind.Object
                && feature.TryGetProperty("properties", out var found)
                && found.ValueKind == JsonValueKind.Object
                    ? found
                    : default;
            if (properties.ValueKind == JsonValueKind.Object)
            {
                CountProperties(properties, cancellationToken);
            }

            if (feature.ValueKind == JsonValueKind.Object
                && feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object
                && geometry.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && JsonMarshal.GetRawUtf8Value(type).Length <= _remainingMetadataBytes)
            {
                var name = type.GetString()!;
                if (!_geometryTypes.Contains(name) && TryReserveMetadata(name))
                {
                    _geometryTypes.Add(name);
                }
            }

            if (!_pageFull && _count >= window.Offset && _count - window.Offset < window.Limit)
            {
                AddPageRow(properties, cancellationToken);
            }

            _count++;
        }

        public McpArtifactFeatureSummary Build()
        {
            return new McpArtifactFeatureSummary
            {
                FeatureCount = _count,
                Fields = _fields,
                GeometryTypes = [.. _geometryTypes],
                MatchedFeatureCount = _hasJoinCount ? _matched : null,
                RowOffset = window.Offset,
                Rows = _rows,
                OmittedRowOffsets = _omittedRowOffsets.Count > 0 ? _omittedRowOffsets : null,
                NextRowOffset = window.Limit > 0 && _pageEnd < _count ? _pageEnd : null,
            };
        }

        private void CountProperties(JsonElement properties, CancellationToken cancellationToken)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (_fields.Count >= MaxFields || _remainingMetadataBytes == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (JsonMarshal.GetRawUtf8PropertyName(property).Length > _remainingMetadataBytes)
                {
                    continue;
                }

                var name = property.Name;
                if (!_seenFields.Contains(name) && TryReserveMetadata(name))
                {
                    _seenFields.Add(name);
                    _fields.Add(name);
                }
            }

            if (kind == ResultKind.SpatialJoin && properties.TryGetProperty(JoinCountField, out var joinCount))
            {
                _hasJoinCount = true;
                _matched += joinCount.ValueKind == JsonValueKind.Number && joinCount.GetDouble() > 0 ? 1 : 0;
            }
        }

        private bool TryReserveMetadata(string value)
        {
            // A UTF-16 code unit can expand to six JSON bytes. Reserve space for delimiters too.
            if (_remainingMetadataBytes < 3 || value.Length > (_remainingMetadataBytes - 3) / 6)
            {
                return false;
            }

            _remainingMetadataBytes -= (value.Length * 6) + 3;
            return true;
        }

        private static JsonElement CreateEmptyRow()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        private void AddPageRow(JsonElement properties, CancellationToken cancellationToken)
        {
            var row = properties.ValueKind == JsonValueKind.Object ? properties : EmptyRow;
            cancellationToken.ThrowIfCancellationRequested();
            var rowByteLimit = previewBytes / 2;
            var rowBytes = JsonMarshal.GetRawUtf8Value(row).Length >= rowByteLimit
                ? rowByteLimit + 1
                : JsonSerializer.SerializeToUtf8Bytes(row, McpJsonContext.Default.JsonElement).Length + 1;
            var omitRow = rowBytes > rowByteLimit;
            var requiredBytes = omitRow ? OmittedOffsetBytes : rowBytes;
            if (requiredBytes > _remainingRowBytes)
            {
                _pageFull = true;
                return;
            }
            if (omitRow)
            {
                _omittedRowOffsets.Add(_count);
            }
            else
            {
                _rows.Add(row.Clone());
            }

            _remainingRowBytes -= requiredBytes;
            _pageEnd = _count + 1;
        }
    }
}
