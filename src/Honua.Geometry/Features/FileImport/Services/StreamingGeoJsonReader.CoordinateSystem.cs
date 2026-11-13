// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.FileImport.Services;

internal sealed partial class StreamingGeoJsonReader
{
    public static async Task<ImportCoordinateSystem> InspectCoordinateSystemAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16384];
        var buffered = 0;
        var state = new JsonReaderState(new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var scan = new CrsMetadataScan();
        var firstChunk = true;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(buffered), cancellationToken);
                buffered += read;
                if (firstChunk)
                {
                    if (buffered < 3 && read > 0) continue;
                    firstChunk = false;
                    if (buffered >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    {
                        buffer.AsSpan(3, buffered - 3).CopyTo(buffer);
                        buffered -= 3;
                    }
                }
                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), isFinalBlock: read == 0, state);
                var consumed = ReadCrsTokens(ref reader, scan);
                state = reader.CurrentState;
                if (scan.Complete || read == 0)
                {
                    if (scan.Declared)
                    {
                        return ImportCoordinateSystem.FromDetection(scan.Srid, ImportCrsProvenance.FileMetadata);
                    }
                    return scan.Complete
                        ? ImportCoordinateSystem.FromDetection(scan.Srid ?? 4326, ImportCrsProvenance.FormatDefined)
                        : ImportCoordinateSystem.FromDetection(scan.Srid, ImportCrsProvenance.Unknown);
                }
                buffer.AsSpan(consumed, buffered - consumed).CopyTo(buffer);
                buffered -= consumed;
                if (buffered == buffer.Length)
                {
                    if (buffer.Length >= 16 * 1024 * 1024) return ImportCoordinateSystem.FromDetection(
                        null, scan.Declared ? ImportCrsProvenance.FileMetadata : ImportCrsProvenance.Unknown);
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
            }
        }
        catch (JsonException)
        {
            // The shared validator and feature reader report malformed JSON before any rows are stored.
            return ImportCoordinateSystem.FromDetection(4326, ImportCrsProvenance.FormatDefined);
        }
        finally
        {
            if (stream.CanSeek) stream.Position = 0;
        }
    }

    private sealed class CrsMetadataScan
    {
        public bool Declared { get; set; }
        public bool AwaitingValue { get; set; }
        public int ValueDepth { get; set; } = -1;
        public int? Srid { get; set; }
        public bool Complete { get; set; }
    }

    private static int ReadCrsTokens(ref Utf8JsonReader reader, CrsMetadataScan scan)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("crs"))
            {
                scan.Declared = true;
                scan.AwaitingValue = true;
                continue;
            }
            if (scan.AwaitingValue)
            {
                scan.AwaitingValue = false;
                scan.ValueDepth = reader.CurrentDepth;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    // "crs": null declares nothing; the file keeps the RFC 7946 coordinate system.
                    scan.Declared = false;
                    scan.Complete = true;
                    break;
                }
                if (reader.TokenType is not JsonTokenType.StartObject and not JsonTokenType.StartArray and not JsonTokenType.String)
                {
                    scan.Complete = true;
                    break;
                }
            }
            if (scan.Declared && reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString()!;
                var match = System.Text.RegularExpressions.Regex.Match(value, @"EPSG[:\s]*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var srid)) scan.Srid = srid;
                else if (value.Contains("CRS84", StringComparison.OrdinalIgnoreCase)) scan.Srid = 4326;
                if (reader.CurrentDepth == scan.ValueDepth) scan.Complete = true;
            }
            if ((reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) &&
                ((scan.Declared && reader.CurrentDepth == scan.ValueDepth) || reader.CurrentDepth == 0))
            {
                scan.Complete = true;
            }
            if (scan.Complete) break;
        }
        return checked((int)reader.BytesConsumed);
    }

}
