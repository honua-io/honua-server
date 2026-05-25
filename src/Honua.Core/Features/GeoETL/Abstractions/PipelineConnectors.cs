// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Abstractions;

/// <summary>
/// A source connector extracts a streaming feature set from an external source. Phase 1
/// implementations wrap the existing managed format readers in
/// <c>Honua.Core/Features/Import/Services/</c> and never materialize the full set in
/// memory. See the GeoETL roadmap § Connector phasing.
/// </summary>
public interface IPipelineSourceConnector
{
    /// <summary>
    /// Connector type discriminator matching <see cref="ConnectorConfig.Type"/>.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Worker runtime profile this connector requires. Phase 1 connectors are
    /// <see cref="ConnectorRuntimeProfile.Managed"/>.
    /// </summary>
    ConnectorRuntimeProfile RuntimeProfile { get; }

    /// <summary>
    /// Streams features from the configured source. Features are yielded one at a time
    /// to maintain constant memory usage and honour <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="config">Source connector configuration.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The extracted feature stream.</returns>
    IAsyncEnumerable<IFeature> ReadAsync(ConnectorConfig config, CancellationToken cancellationToken = default);
}

/// <summary>
/// A transform maps an input feature stream to an output feature stream. Transforms are
/// pure streaming operators and never materialize the full set in memory. Row-level
/// errors route to the quarantine channel rather than aborting the run (ADR-0038).
/// </summary>
public interface IPipelineTransform
{
    /// <summary>
    /// Transform type discriminator matching <see cref="TransformConfig.Type"/>.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Applies the transform to <paramref name="source"/>, yielding the transformed stream.
    /// </summary>
    /// <param name="config">Transform configuration.</param>
    /// <param name="source">Input feature stream.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The transformed feature stream.</returns>
    IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Summary returned by a sink after it consumes the feature stream.
/// </summary>
public sealed record SinkWriteResult
{
    /// <summary>
    /// Number of features the sink committed.
    /// </summary>
    public long FeaturesWritten { get; init; }

    /// <summary>
    /// Number of features the sink rejected (for example null geometry).
    /// </summary>
    public long FeaturesRejected { get; init; }
}

/// <summary>
/// A sink connector loads a streaming feature set into a durable target. The Phase 1
/// Honua-layer / PostGIS sink reuses the <c>StreamingFileImportService</c> insert path.
/// The dry-run preview sink counts features without writing.
/// </summary>
public interface IPipelineSinkConnector
{
    /// <summary>
    /// Connector type discriminator matching <see cref="ConnectorConfig.Type"/>.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Worker runtime profile this connector requires. Phase 1 sinks are
    /// <see cref="ConnectorRuntimeProfile.Managed"/>.
    /// </summary>
    ConnectorRuntimeProfile RuntimeProfile { get; }

    /// <summary>
    /// Consumes the feature stream and writes it to the configured target.
    /// </summary>
    /// <param name="config">Sink connector configuration.</param>
    /// <param name="features">The feature stream to load.</param>
    /// <param name="batchId">
    /// Batch identifier tagged on every write so a failed run can soft-delete its rows.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The write summary.</returns>
    Task<SinkWriteResult> WriteAsync(
        ConnectorConfig config,
        IAsyncEnumerable<IFeature> features,
        string batchId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves <see cref="IPipelineSourceConnector"/>, <see cref="IPipelineTransform"/>,
/// and <see cref="IPipelineSinkConnector"/> instances by their type discriminators.
/// </summary>
public interface IPipelineComponentFactory
{
    /// <summary>
    /// Resolves the source connector for the given type, or null when unknown.
    /// </summary>
    /// <param name="type">Source connector type discriminator.</param>
    IPipelineSourceConnector? ResolveSource(string type);

    /// <summary>
    /// Resolves the transform for the given type, or null when unknown.
    /// </summary>
    /// <param name="type">Transform type discriminator.</param>
    IPipelineTransform? ResolveTransform(string type);

    /// <summary>
    /// Resolves the sink connector for the given type, or null when unknown.
    /// </summary>
    /// <param name="type">Sink connector type discriminator.</param>
    IPipelineSinkConnector? ResolveSink(string type);
}
