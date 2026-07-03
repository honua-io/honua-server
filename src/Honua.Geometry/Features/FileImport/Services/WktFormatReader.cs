// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Streaming WKT (Well-Known Text) format reader. Parses geometry records into features
/// using streaming for memory-efficient processing. Understands plain WKT, EWKT
/// (<c>SRID=&lt;n&gt;;</c> prefix), and WKB/EWKB hex via <see cref="GeometryValueParser"/>,
/// and accumulates records that span multiple physical lines (a single geometry whose
/// coordinate list is pretty-printed across several lines).
/// </summary>
internal static class WktFormatReader
{
    /// <summary>
    /// Streams features from a WKT file. A record is a single geometry; it may occupy one
    /// line or span several lines. Records are delimited by parenthesis balance so that a
    /// multi-line geometry is accumulated before it is parsed, and hex/EMPTY geometries
    /// (which have no parentheses) are parsed as soon as they are read.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var feature in ReadStreamingAsync(stream, diagnostics: null, cancellationToken))
        {
            yield return feature;
        }
    }

    /// <summary>
    /// Streams features from a WKT file, recording any record that cannot be parsed into the
    /// supplied <paramref name="diagnostics"/> sink so the loss is surfaced as an import warning
    /// rather than silently swallowed (honua-server#2363).
    /// </summary>
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

/// <summary>
/// Collects diagnostics emitted while streaming a WKT file so the import pipeline can surface a
/// warning instead of silently dropping records it cannot parse. A single instance is passed to
/// <see cref="WktFormatReader"/> and read after enumeration completes (honua-server#2363).
/// </summary>
internal sealed class WktGeometryDiagnostics
{
    /// <summary>
    /// Number of non-empty records whose content could not be parsed as WKT, EWKT, or WKB/EWKB
    /// hex. Such records are skipped; counting them keeps the loss visible.
    /// </summary>
    public int UnparseableRecords { get; private set; }

    /// <summary>Records that a WKT record could not be parsed.</summary>
    public void RecordUnparseableRecord() => UnparseableRecords++;
}
