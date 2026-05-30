// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Services;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Export.Writers;

/// <summary>
/// Writes features as CSV with WKT geometry column.
/// Streams directly to output — O(1) memory regardless of export size.
/// </summary>
internal static class CsvExportWriter
{
    private const int FlushInterval = 32;

    [ThreadStatic]
    private static WKTWriter? _wktWriter;

    private static WKTWriter WktWriter => _wktWriter ??= new WKTWriter();

    public static async Task<int> WriteAsync(
        Stream output,
        IAsyncEnumerable<Feature> features,
        ExportField[] fields,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, bufferSize: 8192, leaveOpen: true);

        // Header row
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(EscapeCsvField(fields[i].Name));
        }
        sb.Append(",WKT");
        await writer.WriteLineAsync(sb.ToString()).ConfigureAwait(false);

        var rowCount = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            sb.Clear();
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(',');
                feature.Attributes.TryGetValue(fields[i].Name, out var value);
                sb.Append(FormatCsvValue(value));
            }

            sb.Append(',');
            sb.Append(FormatGeometry(feature.Geometry));

            await writer.WriteLineAsync(sb.ToString()).ConfigureAwait(false);

            if (++rowCount % FlushInterval == 0)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return rowCount;
    }

    private static string FormatGeometry(byte[]? wkb)
    {
        if (wkb is null || wkb.Length == 0)
            return string.Empty;

        var geometry = WkbReaderCache.Get().Read(wkb);
        return EscapeCsvField(WktWriter.Write(geometry));
    }

    private static string FormatCsvValue(object? value)
    {
        if (value is null or DBNull)
            return string.Empty;

        return value switch
        {
            string s => EscapeCsvField(s),
            DateTime dt => EscapeCsvField(dt.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset dto => EscapeCsvField(dto.ToString("O", CultureInfo.InvariantCulture)),
            double d => d.ToString("G", CultureInfo.InvariantCulture),
            float f => f.ToString("G", CultureInfo.InvariantCulture),
            decimal dec => dec.ToString("G", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => EscapeCsvField(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    private static string EscapeCsvField(string value)
    {
        if (value.Length == 0)
            return value;

        if (value.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || value.Contains('\r'))
            return string.Concat("\"", value.Replace("\"", "\"\""), "\"");

        return value;
    }
}
