// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Streaming WKT (Well-Known Text) format reader. Parses WKT geometry lines into features
/// using line-by-line streaming for memory-efficient processing.
/// </summary>
internal static class WktFormatReader
{
    /// <summary>
    /// Streams features from a WKT file, reading one geometry per line.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wktReader = new WKTReader();
        using var reader = new StreamReader(stream, leaveOpen: true);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            IFeature? feature = null;
            try
            {
                var geometry = wktReader.Read(line.Trim());
                if (geometry != null)
                {
                    feature = new Feature(geometry, new AttributesTable());
                }
            }
            catch
            {
                // Skip invalid WKT lines
            }

            if (feature != null)
                yield return feature;
        }
    }
}
