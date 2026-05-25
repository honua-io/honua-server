// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Connectors;

/// <summary>
/// Phase 1 Honua-layer / PostGIS sink. Batches the feature stream and writes through
/// <see cref="IFeatureSinkWriter"/>, which mirrors the <c>StreamingFileImportService</c>
/// insert path (<c>honua.create_import_table</c> + <c>honua.insert_import_feature</c>).
/// Every row is tagged with the run batch id for soft-delete rollback (ADR-0038).
/// </summary>
/// <remarks>
/// Required <see cref="ConnectorConfig.Options"/>: <c>schema</c>, <c>table</c>,
/// <c>targetSrid</c>. Optional <c>sourceSrid</c> (defaults to the target SRID) and
/// <c>batchSize</c> (defaults to 1000).
/// </remarks>
public sealed class HonuaLayerSinkConnector(IFeatureSinkWriter writer) : IPipelineSinkConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "honua-layer";

    private const int DefaultBatchSize = 1000;

    /// <inheritdoc />
    public string Type => ConnectorType;

    /// <inheritdoc />
    public ConnectorRuntimeProfile RuntimeProfile => ConnectorRuntimeProfile.Managed;

    /// <inheritdoc />
    public async Task<SinkWriteResult> WriteAsync(
        ConnectorConfig config,
        IAsyncEnumerable<IFeature> features,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(features);

        var schema = RequireOption(config, "schema");
        var table = RequireOption(config, "table");
        var targetSrid = RequireIntOption(config, "targetSrid");
        var sourceSrid = config.Options.TryGetValue("sourceSrid", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : targetSrid;
        var batchSize = config.Options.TryGetValue("batchSize", out var rawBatch)
            && int.TryParse(rawBatch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBatch)
            && parsedBatch > 0
            ? parsedBatch
            : DefaultBatchSize;

        await writer.EnsureTargetAsync(schema, table, targetSrid, cancellationToken).ConfigureAwait(false);

        long written = 0;
        long rejected = 0;
        var buffer = new List<IFeature>(batchSize);

        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (feature.Geometry is null)
            {
                rejected++;
                continue;
            }

            buffer.Add(feature);
            if (buffer.Count >= batchSize)
            {
                written += await writer
                    .InsertBatchAsync(schema, table, buffer, sourceSrid, targetSrid, batchId, cancellationToken)
                    .ConfigureAwait(false);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            written += await writer
                .InsertBatchAsync(schema, table, buffer, sourceSrid, targetSrid, batchId, cancellationToken)
                .ConfigureAwait(false);
        }

        return new SinkWriteResult { FeaturesWritten = written, FeaturesRejected = rejected };
    }

    private static string RequireOption(ConnectorConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Honua-layer sink requires a '{key}' option.");
        }

        return value;
    }

    private static int RequireIntOption(ConnectorConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            throw new InvalidOperationException($"Honua-layer sink requires a positive integer '{key}' option.");
        }

        return value;
    }
}
