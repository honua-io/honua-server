// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Connectors;

/// <summary>
/// Phase 1 dry-run null preview sink. Drains the feature stream and counts rows without
/// writing to any durable target, so a dry run exercises every preceding stage exactly
/// like production while leaving sink targets untouched. See ADR-0038 § Observability of
/// dry runs.
/// </summary>
public sealed class NullPreviewSinkConnector : IPipelineSinkConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "null-preview";

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
        ArgumentNullException.ThrowIfNull(features);

        long counted = 0;
        long rejected = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (feature.Geometry is null)
            {
                rejected++;
                continue;
            }

            counted++;
        }

        return new SinkWriteResult { FeaturesWritten = counted, FeaturesRejected = rejected };
    }
}
