// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Honua.Core.Features.GeoETL.Services.Connectors;

/// <summary>
/// Phase 1 GeoJSON file sink. Streams the feature set to a GeoJSON <c>FeatureCollection</c>
/// file, writing one feature at a time so the sink keeps constant memory regardless of
/// feature count. Uses the managed <see cref="GeoJsonWriter"/> (NetTopologySuite.IO.GeoJSON)
/// — no native dependency. See the GeoETL roadmap § Sink phasing.
/// </summary>
/// <remarks>
/// Required <see cref="ConnectorConfig.Options"/>: <c>path</c> — absolute output file path
/// (overwritten if it exists). Features with null geometry are still written (GeoJSON
/// permits a null geometry member) but counted as rejected so the run summary reflects
/// them.
/// </remarks>
public sealed class GeoJsonFileSinkConnector : IPipelineSinkConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "geojson-file";

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

        if (!config.Options.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("GeoJSON file sink requires a 'path' option.");
        }

        var geoJsonWriter = new GeoJsonWriter();
        long written = 0;
        long rejected = 0;

        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
        await using var textWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var jsonWriter = new JsonTextWriter(textWriter);

        await jsonWriter.WriteStartObjectAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.WritePropertyNameAsync("type", cancellationToken).ConfigureAwait(false);
        await jsonWriter.WriteValueAsync("FeatureCollection", cancellationToken).ConfigureAwait(false);
        await jsonWriter.WritePropertyNameAsync("features", cancellationToken).ConfigureAwait(false);
        await jsonWriter.WriteStartArrayAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // The GeoJsonWriter overload accepts the concrete Feature type; wrap when needed.
            var concrete = feature as Feature ?? new Feature(feature.Geometry, feature.Attributes);
            geoJsonWriter.Write(concrete, jsonWriter);

            if (feature.Geometry is null)
            {
                rejected++;
            }
            else
            {
                written++;
            }
        }

        await jsonWriter.WriteEndArrayAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.WriteEndObjectAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new SinkWriteResult { FeaturesWritten = written, FeaturesRejected = rejected };
    }
}
