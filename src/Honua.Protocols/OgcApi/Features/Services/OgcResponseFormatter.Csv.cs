// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static partial class OgcResponseFormatter
{
    public static async Task StreamCsvResponseAsync(
        Stream destination,
        IAsyncEnumerable<GeoJsonFeature> features,
        CsvResponseOptions options)
    {
        var (fieldNames, cancellationToken) = options;
        await using var enumerator = features.GetAsyncEnumerator(cancellationToken);
        var hasFeature = await enumerator.MoveNextAsync().ConfigureAwait(false);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 4096, leaveOpen: true);
        await writer.WriteLineAsync(BuildCsvHeader(fieldNames).AsMemory(), cancellationToken);
        var rowsSinceFlush = 0;
        while (hasFeature)
        {
            await writer.WriteLineAsync(BuildCsvRow(enumerator.Current, fieldNames).AsMemory(), cancellationToken);
            if (++rowsSinceFlush >= StreamingFlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                rowsSinceFlush = 0;
            }

            hasFeature = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static string BuildCsvHeader(string[] fieldNames) =>
        string.Join(",", fieldNames.Prepend("id").Append("geometry").Select(EscapeCsvField));

    private static string BuildCsvRow(GeoJsonFeature feature, string[] fieldNames)
    {
        var row = new List<string?> { feature.Id?.ToString() };
        row.AddRange(fieldNames.Select(fieldName =>
            feature.Properties?.TryGetValue(fieldName, out var value) == true ? value?.ToString() : null));
        row.Add(BuildCsvGeometryValue(feature.Geometry));
        return string.Join(",", row.Select(EscapeCsvField));
    }
}

internal readonly record struct CsvResponseOptions(string[] FieldNames, CancellationToken CancellationToken = default);
