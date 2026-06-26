// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.dedup</c> executor. Emits the first feature for each distinct key
/// and drops later duplicates. The key is built from one or more attribute fields,
/// the geometry (normalized WKT), or both. Pure managed — no native dependency.
/// Ported from the GeoETL baseline DedupTransform onto the #1185 process/executor
/// contract.
///
/// <para>
/// <b>Streaming (stateful).</b> Dedup is the one stateful transform: it must remember
/// which keys it has already emitted. It streams the input and output one feature at a
/// time, but keeps a "seen keys" set whose memory would otherwise grow with cardinality.
/// To stay bounded it uses a <see cref="SpillableKeySet"/>, which reduces each key to a
/// fixed 128-bit digest and spills to a temp file once the in-memory digest count crosses
/// a cap. Output stays first-wins and deterministic; the documented bound is digest-level
/// exactness rather than byte-for-byte (collision probability is negligible at any realistic
/// volume — see <see cref="SpillableKeySet"/>).
/// </para>
/// </summary>
internal sealed class DedupTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.dedup";

    // Unit-separator control char between key parts so distinct field boundaries never
    // collide (for example {"ab","c"} versus {"a","bc"}).
    private const char Separator = '\u001F';
    private const char NullMarker = '\u00A0';

    protected override string ProcessId => HandledProcessId;

    protected override async IAsyncEnumerable<IFeature> ApplyStream(
        IAsyncEnumerable<IFeature> source,
        StepInputReader inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (keys, useGeometry) = ReadKeySpec(inputs);
        using var seen = new SpillableKeySet();

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildKey(feature, keys, useGeometry);
            if (seen.Add(key))
            {
                yield return feature;
            }
        }
    }

    private static string BuildKey(IFeature feature, IReadOnlyList<string> keys, bool useGeometry)
    {
        var builder = new StringBuilder();
        var attributes = feature.Attributes;

        foreach (var field in keys)
        {
            var value = attributes is not null && attributes.Exists(field)
                ? Convert.ToString(attributes.GetOptionalValue(field), CultureInfo.InvariantCulture)
                : null;
            builder.Append(value ?? NullMarker.ToString());
            builder.Append(Separator);
        }

        if (useGeometry)
        {
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                builder.Append(NullMarker);
            }
            else
            {
                builder.Append(geometry.Normalized().AsText());
            }
        }

        return builder.ToString();
    }

    private static (IReadOnlyList<string> Keys, bool UseGeometry) ReadKeySpec(StepInputReader inputs)
    {
        var useGeometry = inputs.TryGet("geometry", out var rawGeometry)
            && bool.TryParse(rawGeometry, out var parsed) && parsed;

        string[] keys = [];
        if (inputs.TryGet("keys", out var rawKeys) && !string.IsNullOrWhiteSpace(rawKeys))
        {
            keys = rawKeys!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (keys.Length == 0 && !useGeometry)
        {
            throw new TransformInputException(
                "requires a 'keys' attribute list, 'geometry=true', or both.");
        }

        return (keys, useGeometry);
    }
}
