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
    // NullMarker: U+001E (escape prefix) + U+0000 (NUL). Provably out-of-band in the
    // key encoding: EscapeKeyComponent emits U+001E only before U+001E or U+001F,
    // never before NUL, so no serialized attribute value can produce this sequence.
    private const string NullMarker = "\u001E\u0000";

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

        // Not a .Select(...) candidate: each iteration appends two StringBuilder
        // segments (the escaped value and the field separator) rather than mapping to
        // a single projected value, so a LINQ projection wouldn't simplify this.
        foreach (var raw in (keys).Select(field => attributes is not null && attributes.Exists(field)
                ? Convert.ToString(attributes.GetOptionalValue(field), CultureInfo.InvariantCulture)
                : null))
        {
            // Use the out-of-band NullMarker so a no-break-space attribute value cannot
            // collide with the null representation (BH-026). Escape U+001E and U+001F
            // within non-null values so the inter-field separator is always unambiguous
            // (BH-025 sister fix).
            builder.Append(raw is not null ? EscapeKeyComponent(raw) : NullMarker);
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

    /// <summary>
    /// Escapes the U+001E (escape prefix) and U+001F (unit-separator delimiter) characters
    /// within a single key component value so they cannot be mistaken for structural
    /// delimiters in the composite key string. Encoding:
    /// <list type="bullet">
    ///   <item>U+001E in value -&gt; U+001E U+001E</item>
    ///   <item>U+001F in value -&gt; U+001E U+001F</item>
    /// </list>
    /// A single unescaped U+001F therefore always means "field boundary".
    /// </summary>
    private static string EscapeKeyComponent(string value)
    {
        if (value.IndexOfAny(['\u001E', '\u001F']) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (ch is '\u001E' or '\u001F')
            {
                sb.Append('\u001E'); // Escape prefix before any structural character.
            }

            sb.Append(ch);
        }

        return sb.ToString();
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
