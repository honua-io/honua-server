// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

internal static class WktFormatReader
{
    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, WktReadOptions.Default, cancellationToken);

    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        WktGeometryDiagnostics? diagnostics,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, new WktReadOptions { Diagnostics = diagnostics }, cancellationToken);

    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        WktReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var effectiveLimit = options.MaxRecordBytes > 0
            ? options.MaxRecordBytes
            : ImportLimits.Default.MaxSingleFeatureBytes;
        var wktReader = new WKTReader();
        var wkbReader = GeometryValueParser.CreateWkbReader();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var lineReader = new BoundedTextLineReader(reader, effectiveLimit);

        var buffer = new StringBuilder();
        long accumulatedBytes = 0;
        var depth = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingBytes = effectiveLimit - accumulatedBytes - (buffer.Length > 0 ? 1L : 0L);
            var line = await ReadNextLineOrThrowAsync(lineReader, remainingBytes, cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            if (buffer.Length == 0 && string.IsNullOrWhiteSpace(line))
                continue;

            accumulatedBytes += AppendRecordLine(buffer, line);
            depth += ParenDelta(line);

            if (depth <= 0)
            {
                var feature = BuildFeature(buffer, wktReader, wkbReader, options.Diagnostics);
                buffer.Clear();
                accumulatedBytes = 0;
                depth = 0;
                if (feature != null)
                    yield return feature;
            }
        }

        if (buffer.Length > 0 && BuildFeature(buffer, wktReader, wkbReader, options.Diagnostics) is { } trailing)
        {
            yield return trailing;
        }
    }

    private static async Task<string?> ReadNextLineOrThrowAsync(
        BoundedTextLineReader lineReader,
        long remainingBytes,
        CancellationToken cancellationToken)
    {
        var (line, exceededLimit) = await lineReader.ReadLineAsync(remainingBytes, cancellationToken)
            .ConfigureAwait(false);
        if (exceededLimit)
        {
            throw new InvalidDataException(
                $"WKT record exceeds maximum size limit of {lineReader.MaxRecordBytes:N0} bytes.");
        }

        return line;
    }

    private static long AppendRecordLine(StringBuilder buffer, string line)
    {
        long added = Encoding.UTF8.GetByteCount(line);
        if (buffer.Length > 0)
        {
            buffer.Append('\n');
            added++;
        }

        buffer.Append(line);
        return added;
    }

    private static Feature? BuildFeature(
        StringBuilder buffer,
        WKTReader wktReader,
        WKBReader wkbReader,
        WktGeometryDiagnostics? diagnostics)
    {
        var text = buffer.ToString().Trim();
        if (text.Length == 0)
            return null;

        var geometry = GeometryValueParser.TryParse(text, wktReader, wkbReader);
        if (geometry != null)
            return new Feature(geometry, new AttributesTable());

        // A non-empty record that parses to nothing is unparseable content (bad WKT, a stray
        // header line, a truncated multi-line tail). Record it so the import surfaces a warning
        // instead of silently dropping the record.
        diagnostics?.RecordUnparseableRecord();
        return null;
    }

    private static int ParenDelta(string line)
    {
        var delta = 0;
        foreach (var ch in line)
        {
            if (ch == '(')
                delta++;
            else if (ch == ')')
                delta--;
        }

        return delta;
    }
}

internal sealed class WktReadOptions
{
    public static WktReadOptions Default { get; } = new();

    public WktGeometryDiagnostics? Diagnostics { get; init; }

    public long MaxRecordBytes { get; init; } = ImportLimits.Default.MaxSingleFeatureBytes;
}

internal sealed class WktGeometryDiagnostics
{
    public int UnparseableRecords { get; private set; }

    public void RecordUnparseableRecord() => UnparseableRecords++;
}
