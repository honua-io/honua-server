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
/// Phase 1 quarantine / dead-letter sink. Writes every feature it receives to a companion
/// GeoJSON artifact, tagging each with the run batch id and an optional rejection reason,
/// and never throws on a malformed row — a single bad feature is captured rather than
/// killing the whole job. This is the sink half of the ADR-0038 row-level-error contract:
/// rows that fail validation or a transform route here instead of aborting the run, and
/// the execution summary reports the quarantined count. The serialization of any single
/// feature that itself fails to serialize is caught and recorded as a minimal placeholder
/// so the artifact is always complete.
/// </summary>
/// <remarks>
/// Required <see cref="ConnectorConfig.Options"/>: <c>path</c> — absolute output file path
/// for the dead-letter GeoJSON FeatureCollection (overwritten if it exists). Optional
/// <c>reasonField</c> — the attribute name carrying a per-row reason string (defaults to
/// <c>_quarantine_reason</c>); when present it is preserved, otherwise a
/// <c>_batch_id</c> tag is still added so quarantined rows trace back to their run.
/// </remarks>
public sealed class QuarantineSinkConnector : IPipelineSinkConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "quarantine";

    private const string DefaultReasonField = "_quarantine_reason";

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
            throw new InvalidOperationException("Quarantine sink requires a 'path' option.");
        }

        var reasonField = config.Options.TryGetValue("reasonField", out var rawReason)
            && !string.IsNullOrWhiteSpace(rawReason)
            ? rawReason
            : DefaultReasonField;

        var geoJsonWriter = new GeoJsonWriter();
        long quarantined = 0;

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
            var tagged = Tag(feature, batchId, reasonField);
            try
            {
                geoJsonWriter.Write(tagged, jsonWriter);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                // Even the dead-letter write must not abort the job — record a minimal placeholder.
                geoJsonWriter.Write(Placeholder(batchId, reasonField, ex.Message), jsonWriter);
            }

            quarantined++;
        }

        await jsonWriter.WriteEndArrayAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.WriteEndObjectAsync(cancellationToken).ConfigureAwait(false);
        await jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Quarantined rows are not "written" to a durable target — they are rejects.
        return new SinkWriteResult { FeaturesWritten = 0, FeaturesRejected = quarantined };
    }

    private static Feature Tag(IFeature feature, string batchId, string reasonField)
    {
        var attributes = new AttributesTable();
        if (feature.Attributes is not null)
        {
            foreach (var name in feature.Attributes.GetNames())
            {
                attributes.Add(name, feature.Attributes.GetOptionalValue(name));
            }
        }

        if (!attributes.Exists("_batch_id"))
        {
            attributes.Add("_batch_id", batchId);
        }

        if (!attributes.Exists(reasonField))
        {
            attributes.Add(reasonField, "unspecified");
        }

        return new Feature(feature.Geometry, attributes);
    }

    private static Feature Placeholder(string batchId, string reasonField, string detail)
    {
        var attributes = new AttributesTable
        {
            { "_batch_id", batchId },
            { reasonField, $"serialization-failed: {detail}" }
        };
        return new Feature(null, attributes);
    }
}
