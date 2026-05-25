// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.Import.Services;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Connectors;

/// <summary>
/// Phase 1 CSV source connector. Wraps the existing managed <see cref="CsvFormatReader"/>
/// — it does not reimplement CSV parsing. The reader streams features one record at a
/// time, deriving geometry from either a WKT column or longitude/latitude coordinate
/// columns, so the connector keeps constant memory and requires no native dependency.
/// </summary>
/// <remarks>
/// Supported <see cref="ConnectorConfig.Options"/>:
/// <list type="bullet">
/// <item><c>path</c> — absolute path to a CSV file.</item>
/// <item><c>inline</c> — a CSV document supplied directly (used by tests and inline
/// enrichment workloads).</item>
/// <item><c>delimiter</c> — optional single-character delimiter override; when omitted the
/// reader auto-detects from <c>, \t ; |</c>.</item>
/// </list>
/// Exactly one of <c>path</c> or <c>inline</c> must be present. Geometry columns are
/// auto-detected: a WKT column (<c>wkt</c>, <c>geom</c>, <c>geometry</c>, <c>shape</c>, …)
/// or a longitude/latitude pair (<c>lon</c>/<c>lng</c>/<c>x</c> and <c>lat</c>/<c>y</c>).
/// </remarks>
public sealed class CsvSourceConnector : IPipelineSourceConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "csv";

    /// <inheritdoc />
    public string Type => ConnectorType;

    /// <inheritdoc />
    public ConnectorRuntimeProfile RuntimeProfile => ConnectorRuntimeProfile.Managed;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> ReadAsync(
        ConnectorConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var delimiterOverride = ReadDelimiterOverride(config);
        await using var stream = OpenStream(config);

        await foreach (var feature in CsvFormatReader
                           .ReadStreamingAsync(stream, delimiterOverride, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return feature;
        }
    }

    private static char? ReadDelimiterOverride(ConnectorConfig config)
    {
        if (config.Options.TryGetValue("delimiter", out var delimiter) && delimiter.Length == 1)
        {
            return delimiter[0];
        }

        return null;
    }

    private static Stream OpenStream(ConnectorConfig config)
    {
        if (config.Options.TryGetValue("inline", out var inline) && !string.IsNullOrEmpty(inline))
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(inline), writable: false);
        }

        if (config.Options.TryGetValue("path", out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536,
                useAsync: true);
        }

        throw new InvalidOperationException(
            "CSV source connector requires either an 'inline' document or a 'path' option.");
    }
}
