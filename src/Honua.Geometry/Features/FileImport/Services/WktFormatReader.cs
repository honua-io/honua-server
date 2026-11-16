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
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var feature in ReadStreamingAsync(stream, diagnostics: null, cancellationToken))
        {
            yield return feature;
        }
    }

    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        WktGeometryDiagnostics? diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wktReader = new WKTReader();
        var wkbReader = GeometryValueParser.CreateWkbReader();
        using var reader = new StreamReader(stream, leaveOpen: true);

        var buffer = new StringBuilder();
        var depth = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip leading blank lines between records; do not break an in-progress record.
            if (buffer.Length == 0 && string.IsNullOrWhiteSpace(line))
                continue;

            if (buffer.Length > 0)
                buffer.Append('\n');
            buffer.Append(line);
            depth += ParenDelta(line);

            // The record's parentheses are balanced (or it has none, e.g. WKB hex or
            // "POINT EMPTY"): it is a complete geometry, so parse and reset.
            if (depth <= 0)
            {
                var feature = BuildFeature(buffer, wktReader, wkbReader, diagnostics);
                buffer.Clear();
                depth = 0;
                if (feature != null)
                    yield return feature;
            }
        }

        // Flush any trailing content (last record without a trailing newline, or an
        // unbalanced tail we still attempt to parse rather than silently drop).
        if (buffer.Length > 0)
        {
            var feature = BuildFeature(buffer, wktReader, wkbReader, diagnostics);
            if (feature != null)
                yield return feature;
        }
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

internal sealed class WktGeometryDiagnostics
{
    public int UnparseableRecords { get; private set; }

    public void RecordUnparseableRecord() => UnparseableRecords++;
}
