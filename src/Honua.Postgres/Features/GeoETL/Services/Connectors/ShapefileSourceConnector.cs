// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;

namespace Honua.Postgres.Features.GeoETL.Services.Connectors;

/// <summary>
/// Phase 1 Shapefile source connector. Reads an Esri shapefile through the managed
/// <c>NetTopologySuite.IO.Esri</c> reader — the same managed library the
/// <c>StreamingFileImportService</c> shapefile import path uses — so it carries no
/// GDAL/OGR dependency and runs inside the lean serving image. The connector lives in
/// <c>Honua.Postgres</c> because that is where the managed Esri reader is referenced.
/// </summary>
/// <remarks>
/// Required <see cref="ConnectorConfig.Options"/>: <c>path</c> — absolute path to the
/// <c>.shp</c> file (its sidecar <c>.dbf</c> / <c>.shx</c> / <c>.prj</c> must sit beside
/// it). Reads stream one record at a time, skipping deleted records, so memory stays
/// constant for large shapefiles.
/// </remarks>
public sealed class ShapefileSourceConnector : IPipelineSourceConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "shapefile";

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

        if (!config.Options.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "Shapefile source connector requires a 'path' option pointing at the .shp file.");
        }

        var options = new ShapefileReaderOptions
        {
            GeometryBuilderMode = GeometryBuilderMode.QuickFixInvalidShapes
        };

        using var reader = Shapefile.OpenRead(path, options);
        var recordIndex = 0;

        while (reader.Read(out var deleted, out var feature))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deleted || feature is null)
            {
                continue;
            }

            yield return feature;

            if (++recordIndex % 256 == 0)
            {
                await Task.Yield();
            }
        }
    }
}
