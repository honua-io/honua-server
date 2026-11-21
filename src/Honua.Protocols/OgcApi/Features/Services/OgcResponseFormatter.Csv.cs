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
        var fieldNames = options.FieldNames;
        var cancellationToken = options.CancellationToken;
        await using var enumerator = features.GetAsyncEnumerator(cancellationToken);
        var hasFeature = await enumerator.MoveNextAsync().ConfigureAwait(false);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 4096, leaveOpen: true);
        using var rows = new OgcCsvRowBuilder(fieldNames, options.IdentifierFieldName);
        await writer.WriteLineAsync(rows.BuildHeader().AsMemory(), cancellationToken);
        var rowsSinceFlush = 0;
        while (hasFeature)
        {
            await writer.WriteLineAsync(rows.BuildRow(enumerator.Current), cancellationToken);
            if (++rowsSinceFlush >= StreamingFlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                rowsSinceFlush = 0;
            }

            hasFeature = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken);
    }

    public static string BuildCsvResponse(
        IEnumerable<GeoJsonFeature> features, string[] fieldNames, string? identifierFieldName = null)
    {
        var builder = new StringBuilder();
        using var rows = new OgcCsvRowBuilder(fieldNames, identifierFieldName);
        builder.AppendLine(rows.BuildHeader());
        foreach (var feature in features)
        {
            builder.Append(rows.BuildRow(feature)).AppendLine();
        }

        return builder.ToString();
    }
}

internal readonly record struct CsvResponseOptions(
    string[] FieldNames,
    string? IdentifierFieldName = null,
    CancellationToken CancellationToken = default);
