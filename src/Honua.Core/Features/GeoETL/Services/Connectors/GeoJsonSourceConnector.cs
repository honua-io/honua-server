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
/// Phase 1 GeoJSON source connector. Wraps the existing managed
/// <see cref="StreamingGeoJsonReader"/> — it does not reimplement GeoJSON parsing.
/// The reader streams features one at a time so the connector keeps constant memory.
/// </summary>
/// <remarks>
/// Supported <see cref="ConnectorConfig.Options"/>:
/// <list type="bullet">
/// <item><c>path</c> — absolute path to a GeoJSON FeatureCollection file.</item>
/// <item><c>inline</c> — a GeoJSON FeatureCollection document supplied directly (used by
/// the inline-enrichment proof workload and by tests).</item>
/// </list>
/// Exactly one of <c>path</c> or <c>inline</c> must be present.
/// </remarks>
public sealed class GeoJsonSourceConnector : IPipelineSourceConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "geojson";

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

        var reader = new StreamingGeoJsonReader();
        await using var stream = OpenStream(config);

        await foreach (var feature in reader.ReadFeaturesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            yield return feature;
        }
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
            "GeoJSON source connector requires either an 'inline' document or a 'path' option.");
    }
}
